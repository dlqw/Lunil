using System.Diagnostics;
using System.Text;
using Lunil.Core.Text;

namespace Lunil.LanguageServer;

internal readonly record struct LspPosition(int Line, int Character);

internal readonly record struct LspRange(LspPosition Start, LspPosition End);

internal readonly record struct LspTextChange(LspRange? Range, string Text);

/// <summary>
/// A workspace text document stored as UTF-8 bytes. Disk-loaded documents keep only
/// the byte form; the UTF-16 view and the line index materialize lazily on first
/// use, so a large scanned workspace pays one byte copy per file instead of three
/// parallel representations. Lazy materialization races are benign: both racers
/// produce equal values and only the reference assignment races.
/// </summary>
internal sealed class LspTextDocument
{
    private string? _text;
    private byte[]? _utf8;
    private int[]? _lineStarts;
    private long _lastAccess;
    private int _recordedByteLength = -1;
    private bool _trimmed;

    public LspTextDocument(Uri uri, int version, string text, bool isOpen = true)
    {
        Uri = uri;
        Version = version;
        IsOpen = isOpen;
        _text = text;
    }

    /// <summary>Takes ownership of the decoded UTF-8 bytes (BOM, if present, is stripped).</summary>
    public LspTextDocument(Uri uri, int version, byte[] utf8, bool isOpen)
    {
        Uri = uri;
        Version = version;
        IsOpen = isOpen;
        // File.ReadAllBytes keeps a UTF-8 BOM that File.ReadAllText stripped; the
        // pipeline has no BOM handling of its own, so drop it to keep the byte view
        // identical to what the string-based path produced.
        _utf8 = utf8 is [0xEF, 0xBB, 0xBF, .. var rest] ? rest : utf8;
    }

    private LspTextDocument(
        Uri uri,
        int version,
        string? text,
        byte[]? utf8,
        int[]? lineStarts,
        bool isOpen,
        int recordedByteLength,
        bool trimmed)
    {
        Uri = uri;
        Version = version;
        IsOpen = isOpen;
        _text = text;
        _utf8 = utf8;
        _lineStarts = lineStarts;
        _recordedByteLength = recordedByteLength;
        _trimmed = trimmed;
    }

    public Uri Uri { get; }

    public int Version { get; }

    public string Text
    {
        get
        {
            if (_text is null && _utf8 is null && Volatile.Read(ref _trimmed))
            {
                ReloadFromDisk();
                if (_text is null && _utf8 is null)
                {
                    // The disk copy vanished; watchers retire the document shortly.
                    return string.Empty;
                }
            }

            return _text ??= Encoding.UTF8.GetString(Utf8.Span);
        }
    }

    public bool IsOpen { get; }

    public bool IsTrimmed => Volatile.Read(ref _trimmed);

    public int ByteLength
    {
        get
        {
            if (_utf8 is not null)
            {
                return _utf8.Length;
            }

            if (_text is not null)
            {
                _recordedByteLength = Encoding.UTF8.GetByteCount(_text);
                return _recordedByteLength;
            }

            return Math.Max(0, _recordedByteLength);
        }
    }

    /// <summary>The UTF-8 encoding of the source; reused instead of re-encoding.</summary>
    public ReadOnlyMemory<byte> Utf8
    {
        get
        {
            if (_utf8 is null && _text is null && Volatile.Read(ref _trimmed))
            {
                ReloadFromDisk();
                if (_utf8 is null && _text is null)
                {
                    return ReadOnlyMemory<byte>.Empty;
                }
            }

            return _utf8 ??= Encoding.UTF8.GetBytes(Text);
        }
    }

    /// <summary>
    /// The canonical UTF-8 array backing <see cref="Utf8"/>. Analysis passes wrap this
    /// array without copying; callers must treat it as immutable.
    /// </summary>
    internal byte[] Utf8Array
    {
        get
        {
            if (_utf8 is null && _text is null && Volatile.Read(ref _trimmed))
            {
                ReloadFromDisk();
            }

            return _utf8 ??= Encoding.UTF8.GetBytes(Text);
        }
    }

    /// <summary>Marks the document as recently used for residency eviction ordering.</summary>
    internal void Touch() => Interlocked.Exchange(ref _lastAccess, Stopwatch.GetTimestamp());

    internal long LastAccess => Interlocked.Read(ref _lastAccess);

    /// <summary>
    /// Drops the materialized representations of a closed, disk-backed document so a
    /// huge scanned workspace does not keep every file resident; the first later use
    /// reloads from disk transparently. Non-file and open documents are ignored.
    /// </summary>
    public void Trim()
    {
        if (IsOpen || !Uri.IsFile)
        {
            return;
        }

        if (_text is not null)
        {
            _recordedByteLength = Encoding.UTF8.GetByteCount(_text);
        }

        Volatile.Write(ref _trimmed, true);
        _lineStarts = null;
        _text = null;
        if (_utf8 is not null)
        {
            _recordedByteLength = _utf8.Length;
            _utf8 = null;
        }
    }

    /// <summary>
    /// Restores a trimmed document from its file. Concurrent reloads are benign: both
    /// readers load identical bytes and the winning assignments stick.
    /// </summary>
    private void ReloadFromDisk()
    {
        try
        {
            if (!Uri.IsFile)
            {
                return;
            }

            var bytes = File.ReadAllBytes(Uri.LocalPath);
            if (bytes is [0xEF, 0xBB, 0xBF, .. var rest])
            {
                bytes = rest;
            }

            _utf8 = bytes;
            Volatile.Write(ref _trimmed, false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    public LspTextDocument WithOpen(bool isOpen)
    {
        if (IsOpen == isOpen)
        {
            return this;
        }

        return new LspTextDocument(Uri, Version, _text, _utf8, _lineStarts, isOpen, _recordedByteLength, _trimmed);
    }

    public LspTextDocument Apply(int version, IReadOnlyList<LspTextChange> changes)
    {
        var current = this;
        foreach (var change in changes)
        {
            if (change.Range is null)
            {
                current = new LspTextDocument(Uri, version, change.Text, IsOpen);
                continue;
            }

            var start = current.ToCharOffset(change.Range.Value.Start);
            var end = current.ToCharOffset(change.Range.Value.End);
            if (end < start)
            {
                throw new ArgumentException("Text change range end precedes its start.", nameof(changes));
            }

            var updated = string.Concat(current.Text.AsSpan(0, start), change.Text, current.Text.AsSpan(end));
            current = new LspTextDocument(Uri, version, updated, IsOpen);
        }

        return current.WithVersion(version);
    }

    public int ToByteOffset(LspPosition position)
    {
        var characterOffset = ToCharOffset(position);
        return Encoding.UTF8.GetByteCount(Text.AsSpan(0, characterOffset));
    }

    public LspPosition ToPosition(int byteOffset)
    {
        var utf8 = Utf8.Span;
        var bounded = Math.Clamp(byteOffset, 0, utf8.Length);
        while (bounded > 0 && bounded < utf8.Length && (utf8[bounded] & 0xC0) == 0x80)
        {
            bounded--;
        }

        var characterOffset = Encoding.UTF8.GetCharCount(utf8[..bounded]);
        var line = FindLine(characterOffset);
        return new LspPosition(line, characterOffset - LineIndex[line]);
    }

    public LspRange ToRange(TextSpan span) => new(
        ToPosition(span.Start),
        ToPosition(span.End));

    public int ToCharOffset(LspPosition position)
    {
        var lineStarts = LineIndex;
        var line = Math.Clamp(position.Line, 0, lineStarts.Length - 1);
        var lineStart = lineStarts[line];
        var lineEnd = line + 1 < lineStarts.Length ? lineStarts[line + 1] : Text.Length;
        while (lineEnd > lineStart && Text[lineEnd - 1] is '\r' or '\n')
        {
            lineEnd--;
        }

        var result = Math.Clamp(lineStart + Math.Max(0, position.Character), lineStart, lineEnd);
        if (result > lineStart && result < Text.Length &&
            char.IsHighSurrogate(Text[result - 1]) && char.IsLowSurrogate(Text[result]))
        {
            result--;
        }

        return result;
    }

    private int[] LineIndex => _lineStarts ??= BuildLineStarts(Text);

    private LspTextDocument WithVersion(int version) => Version == version
        ? this
        : new LspTextDocument(Uri, version, _text, _utf8, _lineStarts, IsOpen, _recordedByteLength, _trimmed);

    private int FindLine(int characterOffset)
    {
        var index = Array.BinarySearch(LineIndex, characterOffset);
        return index >= 0 ? index : Math.Max(0, ~index - 1);
    }

    private static int[] BuildLineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                starts.Add(index + 1);
            }
            else if (text[index] == '\n')
            {
                starts.Add(index + 1);
            }
        }

        return [.. starts];
    }
}
