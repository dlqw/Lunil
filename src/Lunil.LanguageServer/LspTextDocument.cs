using System.Text;
using Lunil.Core.Text;

namespace Lunil.LanguageServer;

internal readonly record struct LspPosition(int Line, int Character);

internal readonly record struct LspRange(LspPosition Start, LspPosition End);

internal readonly record struct LspTextChange(LspRange? Range, string Text);

internal sealed class LspTextDocument
{
    private readonly int[] _lineStarts;
    private readonly byte[] _utf8;

    public LspTextDocument(Uri uri, int version, string text, bool isOpen = true)
    {
        Uri = uri;
        Version = version;
        Text = text;
        IsOpen = isOpen;
        _lineStarts = BuildLineStarts(text);
        _utf8 = Encoding.UTF8.GetBytes(text);
    }

    public Uri Uri { get; }

    public int Version { get; }

    public string Text { get; }

    public bool IsOpen { get; }

    public int ByteLength => _utf8.Length;

    /// <summary>The cached UTF-8 encoding of <see cref="Text"/>; reused instead of re-encoding.</summary>
    public ReadOnlyMemory<byte> Utf8 => _utf8;

    public LspTextDocument WithOpen(bool isOpen) => new(Uri, Version, Text, isOpen);

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

        return changes.Count == 0 ? new LspTextDocument(Uri, version, Text, IsOpen) : current;
    }

    public int ToByteOffset(LspPosition position)
    {
        var characterOffset = ToCharOffset(position);
        return Encoding.UTF8.GetByteCount(Text.AsSpan(0, characterOffset));
    }

    public LspPosition ToPosition(int byteOffset)
    {
        var bounded = Math.Clamp(byteOffset, 0, _utf8.Length);
        while (bounded > 0 && bounded < _utf8.Length && (_utf8[bounded] & 0xC0) == 0x80)
        {
            bounded--;
        }

        var characterOffset = Encoding.UTF8.GetCharCount(_utf8.AsSpan(0, bounded));
        var line = FindLine(characterOffset);
        return new LspPosition(line, characterOffset - _lineStarts[line]);
    }

    public LspRange ToRange(TextSpan span) => new(
        ToPosition(span.Start),
        ToPosition(span.End));

    public int ToCharOffset(LspPosition position)
    {
        var line = Math.Clamp(position.Line, 0, _lineStarts.Length - 1);
        var lineStart = _lineStarts[line];
        var lineEnd = line + 1 < _lineStarts.Length ? _lineStarts[line + 1] : Text.Length;
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

    private int FindLine(int characterOffset)
    {
        var index = Array.BinarySearch(_lineStarts, characterOffset);
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
