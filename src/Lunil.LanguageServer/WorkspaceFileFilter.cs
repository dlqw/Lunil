using System.Collections.Immutable;
using System.Text;

namespace Lunil.LanguageServer;

/// <summary>
/// Decides which workspace files stay out of the analysis corpus: user-provided glob
/// patterns (<c>lunil.analysis.exclude</c>) and automatic detection of generated data
/// files (huge table literals with essentially no executable statements). Excluded
/// files are never loaded into the document set, so they cost no residency, no
/// declaration scan, and no compact-rebuild work on multi-million-line workspaces.
/// </summary>
internal sealed class WorkspaceFileFilter
{
    /// <summary>Files below this size are never auto-classified as data.</summary>
    internal const int DataDetectionMinimumBytes = 512 * 1024;

    /// <summary>Only the first bytes of a file are scanned for data classification.</summary>
    internal const int DataDetectionSampleBytes = 4 * 1024 * 1024;

    internal const string PatternExclusionReason = "pattern";
    internal const string DataExclusionReason = "data";

    private readonly ImmutableArray<ImmutableArray<GlobSegment>> _patterns;
    private readonly bool _autoDetect;

    private WorkspaceFileFilter(
        ImmutableArray<ImmutableArray<GlobSegment>> patterns,
        bool autoDetect)
    {
        _patterns = patterns;
        _autoDetect = autoDetect;
    }

    public static WorkspaceFileFilter? Create(IEnumerable<string?>? patterns, bool autoDetect)
    {
        var compiled = (patterns ?? [])
            .Where(static pattern => !string.IsNullOrWhiteSpace(pattern))
            .Select(static pattern => pattern!.Trim())
            .Where(static pattern => pattern.Length > 0)
            .SelectMany(ExpandBraces)
            .Select(static pattern => CompilePattern(pattern))
            .ToImmutableArray();
        if (compiled.IsEmpty && !autoDetect)
        {
            return null;
        }

        return new WorkspaceFileFilter(compiled, autoDetect);
    }

    public bool AutoDetectDataFiles => _autoDetect;

    /// <summary>
    /// Matches a <c>/</c>-separated workspace-relative path (or a bare file name when the
    /// file sits outside every folder root) against the configured patterns. Comparison
    /// ignores case so Windows-only capitalization drift cannot silently re-include files.
    /// Patterns without a separator match the file name in any directory.
    /// </summary>
    public bool IsExcludedByPattern(string relativePath)
    {
        if (_patterns.IsEmpty)
        {
            return false;
        }

        var fileName = GetFileName(relativePath);
        foreach (var pattern in _patterns)
        {
            var subject = pattern.Length > 1 || pattern[0].IsDoubleStar
                ? relativePath
                : fileName;
            if (MatchSegments(pattern, 0, SplitPath(subject), 0))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Classifies a UTF-8 (or UTF-16 with BOM) source as generated data: a massive table
    /// literal of keys, strings, and numbers with no functions, requires, or control flow.
    /// The scanner walks raw bytes — skipping comments and string bodies — so classification
    /// stays linear and allocation-free even for multi-gigabyte inputs.
    /// </summary>
    public static bool LooksLikeDataFile(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < DataDetectionMinimumBytes)
        {
            return false;
        }

        var sample = bytes.Length > DataDetectionSampleBytes
            ? bytes[..DataDetectionSampleBytes]
            : bytes;
        // UTF-16 sources are decoded once for the sample: generated exports occasionally
        // carry a UTF-16 BOM, and their interleaved zero bytes would defeat byte counting.
        if (sample is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..])
        {
            sample = DecodeUtf16Sample(sample);
        }

        return ClassifySample(sample);
    }

    private static byte[] DecodeUtf16Sample(ReadOnlySpan<byte> sample)
    {
        var bigEndian = sample[0] == 0xFE;
        var charCount = Math.Min((sample.Length - 2) / 2, DataDetectionSampleBytes);
        var chars = new char[charCount];
        for (var index = 0; index < charCount; index++)
        {
            var offset = 2 + index * 2;
            chars[index] = bigEndian
                ? (char)((sample[offset] << 8) | sample[offset + 1])
                : (char)((sample[offset + 1] << 8) | sample[offset]);
        }

        // Stop at a truncated surrogate so UTF-8 re-encoding cannot split a pair.
        if (chars.Length > 0 && char.IsHighSurrogate(chars[^1]))
        {
            chars = chars[..^1];
        }

        return Encoding.UTF8.GetBytes(chars);
    }

    /// <summary>
    /// Density rule: at least one data token (comma, string, or number) per 16 bytes and
    /// one comma per 64 bytes, with zero functions/requires and at most incidental
    /// control-flow keywords — real code files, even huge ones, fall well below both.
    /// </summary>
    private static bool ClassifySample(ReadOnlySpan<byte> sample)
    {
        long commas = 0;
        long strings = 0;
        long numbers = 0;
        long functions = 0;
        long requires = 0;
        long control = 0;
        long returns = 0;
        var index = 0;
        while (index < sample.Length)
        {
            var current = sample[index];
            if (current == (byte)'-' && index + 1 < sample.Length && sample[index + 1] == (byte)'-')
            {
                index = SkipComment(sample, index + 2);
            }
            else if (current is (byte)'"' or (byte)'\'')
            {
                index = SkipQuotedString(sample, index + 1, current);
                strings++;
            }
            else if (current == (byte)'[' && TryReadLongBracketOpen(sample, index, out var contentStart, out var level))
            {
                index = SkipLongBracketBody(sample, contentStart, level);
                strings++;
            }
            else if (current == (byte)',')
            {
                commas++;
                index++;
            }
            else if (current is >= (byte)'0' and <= (byte)'9')
            {
                index = SkipWhileIdentifier(sample, index);
                numbers++;
            }
            else if (IsWordStartByte(current))
            {
                var end = SkipWhileIdentifier(sample, index);
                ClassifyWord(sample[index..end], ref functions, ref requires, ref control, ref returns);
                index = end;
            }
            else
            {
                index++;
            }
        }

        if (functions != 0 || requires != 0 || returns > 4)
        {
            return false;
        }

        var incidentalControlAllowance = 1 + sample.Length / (1024 * 1024);
        if (control > incidentalControlAllowance)
        {
            return false;
        }

        return commas + strings + numbers >= sample.Length / 16 &&
               commas >= sample.Length / 64;
    }

    private static void ClassifyWord(
        ReadOnlySpan<byte> word,
        ref long functions,
        ref long requires,
        ref long control,
        ref long returns)
    {
        switch (word.Length)
        {
            case 2:
                if (word.SequenceEqual("if"u8))
                {
                    control++;
                }

                break;
            case 3:
                if (word.SequenceEqual("for"u8))
                {
                    control++;
                }

                break;
            case 4:
                if (word.SequenceEqual("else"u8) || word.SequenceEqual("goto"u8) || word.SequenceEqual("then"u8))
                {
                    control++;
                }

                break;
            case 5:
                if (word.SequenceEqual("while"u8))
                {
                    control++;
                }

                break;
            case 6:
                if (word.SequenceEqual("repeat"u8))
                {
                    control++;
                }
                else if (word.SequenceEqual("return"u8))
                {
                    returns++;
                }

                break;
            case 7:
                if (word.SequenceEqual("require"u8))
                {
                    requires++;
                }
                else if (word.SequenceEqual("elseif"u8))
                {
                    control++;
                }

                break;
            case 8:
                if (word.SequenceEqual("function"u8))
                {
                    functions++;
                }

                break;
        }
    }

    private static bool IsWordStartByte(byte value) =>
        value is >= (byte)'A' and <= (byte)'Z' or
            >= (byte)'a' and <= (byte)'z' or
            (byte)'_' or >= 0x80;

    private static bool IsIdentifierByte(byte value) =>
        value is >= (byte)'0' and <= (byte)'9' or
            >= (byte)'A' and <= (byte)'Z' or
            >= (byte)'a' and <= (byte)'z' or
            (byte)'_' or >= 0x80;

    private static int SkipWhileIdentifier(ReadOnlySpan<byte> sample, int index)
    {
        while (index < sample.Length && IsIdentifierByte(sample[index]))
        {
            index++;
        }

        return index;
    }

    private static int SkipQuotedString(ReadOnlySpan<byte> sample, int index, byte quote)
    {
        while (index < sample.Length)
        {
            var current = sample[index];
            if (current == (byte)'\\')
            {
                index += 2;
                continue;
            }

            if (current == quote || current == (byte)'\n')
            {
                return index + 1;
            }

            index++;
        }

        return index;
    }

    private static int SkipComment(ReadOnlySpan<byte> sample, int index)
    {
        if (TryReadLongBracketOpen(sample, index, out var contentStart, out var level))
        {
            return SkipLongBracketBody(sample, contentStart, level);
        }

        while (index < sample.Length && sample[index] is not ((byte)'\n' or (byte)'\r'))
        {
            index++;
        }

        return index;
    }

    /// <summary>Reads a <c>[</c>, <c>=</c>*, <c>[</c> opener; <paramref name="level"/> is the <c>=</c> count.</summary>
    private static bool TryReadLongBracketOpen(
        ReadOnlySpan<byte> sample,
        int index,
        out int contentStart,
        out int level)
    {
        contentStart = 0;
        level = 0;
        var cursor = index + 1;
        while (cursor < sample.Length && sample[cursor] == (byte)'=')
        {
            cursor++;
        }

        if (cursor >= sample.Length || sample[cursor] != (byte)'[')
        {
            return false;
        }

        contentStart = cursor + 1;
        level = cursor - index - 1;
        return true;
    }

    private static int SkipLongBracketBody(ReadOnlySpan<byte> sample, int index, int level)
    {
        while (index < sample.Length)
        {
            if (sample[index] == (byte)']' && ClosesBracketLevel(sample, index, level))
            {
                return index + level + 2;
            }

            index++;
        }

        return sample.Length;
    }

    private static bool ClosesBracketLevel(ReadOnlySpan<byte> sample, int index, int level)
    {
        for (var offset = 1; offset <= level; offset++)
        {
            if (index + offset >= sample.Length || sample[index + offset] != (byte)'=')
            {
                return false;
            }
        }

        return index + level + 1 < sample.Length && sample[index + level + 1] == (byte)']';
    }

    private static string GetFileName(string path)
    {
        var lastSlash = path.LastIndexOf('/');
        return lastSlash < 0 ? path : path[(lastSlash + 1)..];
    }

    private static IEnumerable<string> ExpandBraces(string pattern)
    {
        var open = pattern.IndexOf('{');
        if (open < 0)
        {
            yield return pattern;
            yield break;
        }

        var close = pattern.IndexOf('}', open + 1);
        if (close < 0)
        {
            yield return pattern;
            yield break;
        }

        var prefix = pattern[..open];
        var suffix = pattern[(close + 1)..];
        foreach (var alternative in pattern[(open + 1)..close].Split(','))
        {
            foreach (var expanded in ExpandBraces(prefix + alternative + suffix))
            {
                yield return expanded;
            }
        }
    }

    private static ImmutableArray<GlobSegment> CompilePattern(string pattern) =>
        SplitPath(pattern.Replace('\\', '/'))
            .Select(static segment => segment == "**"
                ? GlobSegment.DoubleStar
                : new GlobSegment(segment))
            .ToImmutableArray();

    private static List<string> SplitPath(string path)
    {
        var segments = new List<string>();
        var start = 0;
        for (var index = 0; index <= path.Length; index++)
        {
            if (index == path.Length || path[index] == '/')
            {
                if (index > start)
                {
                    segments.Add(path[start..index]);
                }

                start = index + 1;
            }
        }

        return segments;
    }

    private static bool MatchSegments(
        ImmutableArray<GlobSegment> pattern,
        int patternIndex,
        List<string> segments,
        int segmentIndex)
    {
        while (patternIndex < pattern.Length)
        {
            var segment = pattern[patternIndex];
            if (segment.IsDoubleStar)
            {
                if (patternIndex == pattern.Length - 1)
                {
                    return true;
                }

                for (var skip = segmentIndex; skip <= segments.Count; skip++)
                {
                    if (MatchSegments(pattern, patternIndex + 1, segments, skip))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (segmentIndex >= segments.Count ||
                !MatchesGlobText(segment.Text!, segments[segmentIndex]))
            {
                return false;
            }

            patternIndex++;
            segmentIndex++;
        }

        return segmentIndex == segments.Count;
    }

    private static bool MatchesGlobText(ReadOnlySpan<char> pattern, ReadOnlySpan<char> value)
    {
        if (pattern.IsEmpty)
        {
            return value.IsEmpty;
        }

        switch (pattern[0])
        {
            case '*':
                for (var length = 0; length <= value.Length; length++)
                {
                    if (MatchesGlobText(pattern[1..], value[length..]))
                    {
                        return true;
                    }
                }

                return false;
            case '?':
                return value.Length > 0 && MatchesGlobText(pattern[1..], value[1..]);
            default:
                return value.Length > 0 &&
                    char.ToUpperInvariant(pattern[0]) == char.ToUpperInvariant(value[0]) &&
                    MatchesGlobText(pattern[1..], value[1..]);
        }
    }

    private readonly record struct GlobSegment(string? Text)
    {
        public static GlobSegment DoubleStar { get; } = new(null);

        public bool IsDoubleStar => Text is null;
    }
}
