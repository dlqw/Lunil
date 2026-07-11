using System.Buffers;
using System.Text;

namespace Luac.Core.Text;

/// <summary>
/// Stores Lua source as immutable bytes. Text decoding is a view used for
/// diagnostics and never changes the underlying Lua byte semantics.
/// </summary>
public sealed class SourceText
{
    private readonly byte[] _bytes;
    private readonly int[] _lineStarts;

    public SourceText(ReadOnlySpan<byte> bytes)
    {
        _bytes = bytes.ToArray();
        _lineStarts = BuildLineStarts(_bytes);
    }

    public int Length => _bytes.Length;

    public int LineCount => _lineStarts.Length;

    public static SourceText FromUtf8(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new SourceText(Encoding.UTF8.GetBytes(text));
    }

    public ReadOnlySpan<byte> AsSpan() => _bytes;

    public ReadOnlySpan<byte> GetSpan(TextSpan span)
    {
        if (span.End > _bytes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(span));
        }

        return _bytes.AsSpan(span.Start, span.Length);
    }

    public byte[] ToArray() => (byte[])_bytes.Clone();

    public TextSpan GetLineSpan(int line)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(line);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(line, _lineStarts.Length);

        var start = _lineStarts[line];
        var end = line + 1 < _lineStarts.Length ? _lineStarts[line + 1] : _bytes.Length;

        if (end > start && _bytes[end - 1] is (byte)'\r' or (byte)'\n')
        {
            var last = _bytes[end - 1];
            end--;
            if (end > start &&
                _bytes[end - 1] is (byte)'\r' or (byte)'\n' &&
                _bytes[end - 1] != last)
            {
                end--;
            }
        }

        return TextSpan.FromBounds(start, end);
    }

    public SourceLocation GetLocation(int byteOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteOffset);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(byteOffset, _bytes.Length);

        var line = Array.BinarySearch(_lineStarts, byteOffset);
        if (line < 0)
        {
            line = ~line - 1;
        }

        var lineStart = _lineStarts[line];
        var byteColumn = byteOffset - lineStart;
        var utf16Column = CountUtf16CodeUnits(_bytes.AsSpan(lineStart, byteColumn));
        return new SourceLocation(byteOffset, line, byteColumn, utf16Column);
    }

    private static int[] BuildLineStarts(ReadOnlySpan<byte> bytes)
    {
        var starts = new List<int> { 0 };

        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] is (byte)'\r' or (byte)'\n')
            {
                var first = bytes[index];
                if (index + 1 < bytes.Length &&
                    bytes[index + 1] is (byte)'\r' or (byte)'\n' &&
                    bytes[index + 1] != first)
                {
                    index++;
                }

                starts.Add(index + 1);
            }
        }

        return starts.ToArray();
    }

    private static int CountUtf16CodeUnits(ReadOnlySpan<byte> bytes)
    {
        var count = 0;
        while (!bytes.IsEmpty)
        {
            var status = Rune.DecodeFromUtf8(bytes, out var rune, out var consumed);
            if (status == OperationStatus.Done)
            {
                count += rune.Utf16SequenceLength;
                bytes = bytes[consumed..];
                continue;
            }

            // Invalid UTF-8 is legal in Lua source comments and strings. Count
            // one replacement code unit per offending byte to remain monotonic,
            // including when a requested byte offset splits a valid sequence.
            count++;
            bytes = bytes[1..];
        }

        return count;
    }
}
