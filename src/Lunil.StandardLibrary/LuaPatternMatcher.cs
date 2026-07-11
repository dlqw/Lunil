using Lunil.Runtime;
using Lunil.Runtime.Values;

namespace Lunil.StandardLibrary;

/// <summary>Byte-oriented Lua 5.4 pattern virtual machine.</summary>
internal sealed class LuaPatternMatcher
{
    private const int MaximumCaptures = 32;
    private const int MaximumDepth = 200;
    private const int Unfinished = -1;
    private const int PositionCapture = -2;

    private readonly byte[] _source;
    private readonly byte[] _pattern;
    private readonly Capture[] _captures = new Capture[MaximumCaptures];
    private int _captureCount;
    private int _depth;

    public LuaPatternMatcher(byte[] source, byte[] pattern)
    {
        _source = source;
        _pattern = pattern;
    }

    public PatternMatch? Find(int initial, bool anchored)
    {
        var patternStart = anchored ? 1 : 0;
        for (var start = initial; start <= _source.Length; start++)
        {
            _captureCount = 0;
            _depth = MaximumDepth;
            var end = Match(start, patternStart);
            if (end >= 0)
            {
                return new PatternMatch(start, end, SnapshotCaptures(start, end));
            }

            if (anchored)
            {
                break;
            }
        }

        return null;
    }

    private int Match(int source, int pattern)
    {
        if (_depth-- == 0)
        {
            throw new LuaRuntimeException("pattern too complex");
        }

        try
        {
            while (true)
            {
                if (pattern == _pattern.Length)
                {
                    return source;
                }

                switch (_pattern[pattern])
                {
                    case (byte)'(':
                        if (pattern + 1 < _pattern.Length && _pattern[pattern + 1] == (byte)')')
                        {
                            return StartCapture(source, pattern + 2, PositionCapture);
                        }

                        return StartCapture(source, pattern + 1, Unfinished);
                    case (byte)')':
                        return EndCapture(source, pattern + 1);
                    case (byte)'$' when pattern + 1 == _pattern.Length:
                        return source == _source.Length ? source : -1;
                    case (byte)'%':
                        if (pattern + 1 >= _pattern.Length)
                        {
                            throw new LuaRuntimeException("malformed pattern (ends with '%')");
                        }

                        switch (_pattern[pattern + 1])
                        {
                            case (byte)'b':
                                {
                                    var balancedEnd = MatchBalance(source, pattern + 2);
                                    if (balancedEnd < 0)
                                    {
                                        return -1;
                                    }

                                    source = balancedEnd;
                                    pattern += 4;
                                    continue;
                                }
                            case (byte)'f':
                                {
                                    pattern += 2;
                                    if (pattern >= _pattern.Length || _pattern[pattern] != (byte)'[')
                                    {
                                        throw new LuaRuntimeException("missing '[' after '%f' in pattern");
                                    }

                                    var classEnd = ClassEnd(pattern);
                                    var previous = source == 0 ? (byte)0 : _source[source - 1];
                                    var current = source == _source.Length ? (byte)0 : _source[source];
                                    if (MatchBracketClass(previous, pattern, classEnd - 1) ||
                                        !MatchBracketClass(current, pattern, classEnd - 1))
                                    {
                                        return -1;
                                    }

                                    pattern = classEnd;
                                    continue;
                                }
                            case >= (byte)'0' and <= (byte)'9':
                                {
                                    var captureEnd = MatchCapture(source, _pattern[pattern + 1] - (byte)'1');
                                    if (captureEnd < 0)
                                    {
                                        return -1;
                                    }

                                    source = captureEnd;
                                    pattern += 2;
                                    continue;
                                }
                        }

                        goto default;
                    default:
                        {
                            var itemEnd = ClassEnd(pattern);
                            var matched = source < _source.Length && SingleMatch(_source[source], pattern, itemEnd);
                            var suffix = itemEnd < _pattern.Length ? _pattern[itemEnd] : (byte)0;
                            if (!matched)
                            {
                                if (suffix is (byte)'*' or (byte)'?' or (byte)'-')
                                {
                                    pattern = itemEnd + 1;
                                    continue;
                                }

                                return -1;
                            }

                            switch (suffix)
                            {
                                case (byte)'?':
                                    {
                                        var result = Match(source + 1, itemEnd + 1);
                                        if (result >= 0)
                                        {
                                            return result;
                                        }

                                        pattern = itemEnd + 1;
                                        continue;
                                    }
                                case (byte)'+':
                                    source++;
                                    return MaxExpand(source, pattern, itemEnd);
                                case (byte)'*':
                                    return MaxExpand(source, pattern, itemEnd);
                                case (byte)'-':
                                    return MinExpand(source, pattern, itemEnd);
                                default:
                                    source++;
                                    pattern = itemEnd;
                                    continue;
                            }
                        }
                }
            }
        }
        finally
        {
            _depth++;
        }
    }

    private int StartCapture(int source, int pattern, int length)
    {
        if (_captureCount >= MaximumCaptures)
        {
            throw new LuaRuntimeException("too many captures");
        }

        _captures[_captureCount++] = new Capture(source, length);
        var result = Match(source, pattern);
        if (result < 0)
        {
            _captureCount--;
        }

        return result;
    }

    private int EndCapture(int source, int pattern)
    {
        var capture = FindOpenCapture();
        _captures[capture] = new Capture(_captures[capture].Start, source - _captures[capture].Start);
        var result = Match(source, pattern);
        if (result < 0)
        {
            _captures[capture] = new Capture(_captures[capture].Start, Unfinished);
        }

        return result;
    }

    private int MaxExpand(int source, int pattern, int itemEnd)
    {
        var count = 0;
        while (source + count < _source.Length &&
            SingleMatch(_source[source + count], pattern, itemEnd))
        {
            count++;
        }

        while (count >= 0)
        {
            var result = Match(source + count, itemEnd + 1);
            if (result >= 0)
            {
                return result;
            }

            count--;
        }

        return -1;
    }

    private int MinExpand(int source, int pattern, int itemEnd)
    {
        while (true)
        {
            var result = Match(source, itemEnd + 1);
            if (result >= 0)
            {
                return result;
            }

            if (source < _source.Length && SingleMatch(_source[source], pattern, itemEnd))
            {
                source++;
            }
            else
            {
                return -1;
            }
        }
    }

    private int MatchBalance(int source, int pattern)
    {
        if (pattern + 1 >= _pattern.Length)
        {
            throw new LuaRuntimeException("malformed pattern (missing arguments to '%b')");
        }

        if (source >= _source.Length || _source[source] != _pattern[pattern])
        {
            return -1;
        }

        var open = _pattern[pattern];
        var close = _pattern[pattern + 1];
        var count = 1;
        while (++source < _source.Length)
        {
            if (_source[source] == close)
            {
                if (--count == 0)
                {
                    return source + 1;
                }
            }
            else if (_source[source] == open)
            {
                count++;
            }
        }

        return -1;
    }

    private int MatchCapture(int source, int capture)
    {
        if (capture < 0 || capture >= _captureCount || _captures[capture].Length == Unfinished)
        {
            throw new LuaRuntimeException($"invalid capture index %{capture + 1}");
        }

        var length = _captures[capture].Length;
        return length >= 0 && source + length <= _source.Length &&
            _source.AsSpan(source, length).SequenceEqual(
                _source.AsSpan(_captures[capture].Start, length))
            ? source + length
            : -1;
    }

    private int ClassEnd(int pattern)
    {
        switch (_pattern[pattern])
        {
            case (byte)'%':
                if (++pattern == _pattern.Length)
                {
                    throw new LuaRuntimeException("malformed pattern (ends with '%')");
                }

                return pattern + 1;
            case (byte)'[':
                pattern++;
                if (pattern < _pattern.Length && _pattern[pattern] == (byte)'^')
                {
                    pattern++;
                }

                if (pattern < _pattern.Length && _pattern[pattern] == (byte)']')
                {
                    pattern++;
                }

                while (pattern < _pattern.Length)
                {
                    if (_pattern[pattern] == (byte)']')
                    {
                        return pattern + 1;
                    }

                    if (_pattern[pattern++] == (byte)'%' && pattern < _pattern.Length)
                    {
                        pattern++;
                    }
                }

                throw new LuaRuntimeException("malformed pattern (missing ']')");
            default:
                return pattern + 1;
        }
    }

    private bool SingleMatch(byte character, int pattern, int end) => _pattern[pattern] switch
    {
        (byte)'.' => true,
        (byte)'%' => MatchClass(character, _pattern[pattern + 1]),
        (byte)'[' => MatchBracketClass(character, pattern, end - 1),
        _ => _pattern[pattern] == character,
    };

    private bool MatchBracketClass(byte character, int pattern, int end)
    {
        var negate = pattern + 1 < end && _pattern[pattern + 1] == (byte)'^';
        var matched = false;
        for (var index = pattern + (negate ? 2 : 1); index < end; index++)
        {
            if (_pattern[index] == (byte)'%')
            {
                if (++index < end && MatchClass(character, _pattern[index]))
                {
                    matched = true;
                }
            }
            else if (index + 2 < end && _pattern[index + 1] == (byte)'-')
            {
                if (_pattern[index] <= character && character <= _pattern[index + 2])
                {
                    matched = true;
                }

                index += 2;
            }
            else if (_pattern[index] == character)
            {
                matched = true;
            }
        }

        return negate ? !matched : matched;
    }

    private static bool MatchClass(byte character, byte pattern)
    {
        var lower = pattern is >= (byte)'a' and <= (byte)'z';
        var upper = pattern is >= (byte)'A' and <= (byte)'Z';
        var code = lower ? pattern : (byte)(pattern | 0x20);
        var matched = code switch
        {
            (byte)'a' => IsAlpha(character),
            (byte)'c' => character < 32 || character == 127,
            (byte)'d' => character is >= (byte)'0' and <= (byte)'9',
            (byte)'g' => character is >= 33 and <= 126,
            (byte)'l' => character is >= (byte)'a' and <= (byte)'z',
            (byte)'p' => character is >= 33 and <= 126 && !IsAlphaNumeric(character),
            (byte)'s' => character is 9 or 10 or 11 or 12 or 13 or 32,
            (byte)'u' => character is >= (byte)'A' and <= (byte)'Z',
            (byte)'w' => IsAlphaNumeric(character),
            (byte)'x' => character is >= (byte)'0' and <= (byte)'9' or
                >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F',
            (byte)'z' => character == 0,
            _ => character == pattern,
        };
        return lower ? matched : upper ? !matched : matched;
    }

    private int FindOpenCapture()
    {
        for (var index = _captureCount - 1; index >= 0; index--)
        {
            if (_captures[index].Length == Unfinished)
            {
                return index;
            }
        }

        throw new LuaRuntimeException("invalid pattern capture");
    }

    private PatternCapture[] SnapshotCaptures(int matchStart, int matchEnd)
    {
        if (_captureCount == 0)
        {
            return [new PatternCapture(matchStart, matchEnd - matchStart, false)];
        }

        var result = new PatternCapture[_captureCount];
        for (var index = 0; index < result.Length; index++)
        {
            var capture = _captures[index];
            if (capture.Length == Unfinished)
            {
                throw new LuaRuntimeException("unfinished capture");
            }

            result[index] = capture.Length == PositionCapture
                ? new PatternCapture(capture.Start, 0, true)
                : new PatternCapture(capture.Start, capture.Length, false);
        }

        return result;
    }

    private static bool IsAlpha(byte value) => value is >= (byte)'a' and <= (byte)'z' or
        >= (byte)'A' and <= (byte)'Z';

    private static bool IsAlphaNumeric(byte value) => IsAlpha(value) ||
        value is >= (byte)'0' and <= (byte)'9';

    private readonly record struct Capture(int Start, int Length);
}

internal sealed record PatternMatch(int Start, int End, PatternCapture[] Captures);

internal readonly record struct PatternCapture(int Start, int Length, bool IsPosition)
{
    public LuaValue ToLuaValue(LuaState state, byte[] source) => IsPosition
        ? LuaValue.FromInteger(Start + 1L)
        : LuaValue.FromString(state.Strings.GetOrCreate(source.AsSpan(Start, Length)));
}
