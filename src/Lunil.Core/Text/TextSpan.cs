namespace Lunil.Core.Text;

/// <summary>Identifies a half-open byte range in a source file.</summary>
public readonly record struct TextSpan
{
    public TextSpan(int start, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _ = checked(start + length);

        Start = start;
        Length = length;
    }

    public int Start { get; }

    public int Length { get; }

    public int End => Start + Length;

    public static TextSpan FromBounds(int start, int end)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(end, start);
        return new TextSpan(start, end - start);
    }

    public bool Contains(int byteOffset) => byteOffset >= Start && byteOffset < End;
}
