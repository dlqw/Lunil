using System.Text;

namespace Lunil.Core.Text;

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
        LunilGuard.NotNull(text);
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
        LunilGuard.NotNegative(line);
        LunilGuard.LessThan(line, _lineStarts.Length);

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
        LunilGuard.NotNegative(byteOffset);
        LunilGuard.LessThanOrEqual(byteOffset, _bytes.Length);

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
            var consumed = GetValidUtf8SequenceLength(bytes);
            if (consumed > 0)
            {
                count += consumed == 4 ? 2 : 1;
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

    private static int GetValidUtf8SequenceLength(ReadOnlySpan<byte> bytes)
    {
        var first = bytes[0];
        if (first <= 0x7f)
        {
            return 1;
        }

        if (first is >= 0xc2 and <= 0xdf)
        {
            return bytes.Length >= 2 && IsContinuation(bytes[1]) ? 2 : 0;
        }

        if (bytes.Length >= 3 && first is >= 0xe0 and <= 0xef)
        {
            var second = bytes[1];
            var validSecond = first switch
            {
                0xe0 => second is >= 0xa0 and <= 0xbf,
                0xed => second is >= 0x80 and <= 0x9f,
                _ => IsContinuation(second),
            };
            return validSecond && IsContinuation(bytes[2]) ? 3 : 0;
        }

        if (bytes.Length >= 4 && first is >= 0xf0 and <= 0xf4)
        {
            var second = bytes[1];
            var validSecond = first switch
            {
                0xf0 => second is >= 0x90 and <= 0xbf,
                0xf4 => second is >= 0x80 and <= 0x8f,
                _ => IsContinuation(second),
            };
            return validSecond && IsContinuation(bytes[2]) && IsContinuation(bytes[3]) ? 4 : 0;
        }

        return 0;
    }

    private static bool IsContinuation(byte value) => value is >= 0x80 and <= 0xbf;
}
