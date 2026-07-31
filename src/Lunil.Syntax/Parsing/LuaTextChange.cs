using System.Collections.Immutable;
using System.Text;
using Lunil.Core.Text;

namespace Lunil.Syntax.Parsing;

/// <summary>A replacement expressed in source byte offsets.</summary>
public sealed record LuaTextChange
{
    public LuaTextChange(TextSpan span, ImmutableArray<byte> newText)
    {
        if (newText.IsDefault)
        {
            throw new ArgumentException("Replacement bytes must be initialized.", nameof(newText));
        }

        Span = span;
        NewText = newText;
    }

    public TextSpan Span { get; }

    public ImmutableArray<byte> NewText { get; }

    public int Delta => checked(NewText.Length - Span.Length);

    public static LuaTextChange FromUtf8(TextSpan span, string newText)
    {
        LunilGuard.NotNull(newText);
        return new LuaTextChange(span, [.. Encoding.UTF8.GetBytes(newText)]);
    }

    public static LuaTextChange FromBytes(TextSpan span, ReadOnlySpan<byte> newText) =>
        new(span, [.. newText]);

    public SourceText Apply(SourceText source)
    {
        LunilGuard.NotNull(source);
        if (Span.End > source.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(source),
                "The change span extends beyond the source.");
        }

        var result = new byte[checked(source.Length + Delta)];
        source.AsSpan()[..Span.Start].CopyTo(result);
        NewText.AsSpan().CopyTo(result.AsSpan(Span.Start));
        source.AsSpan()[Span.End..].CopyTo(result.AsSpan(Span.Start + NewText.Length));
        return new SourceText(result);
    }

    internal bool IsUtf8BoundarySafe(SourceText source)
    {
        if (!IsBoundary(source.AsSpan(), Span.Start) || !IsBoundary(source.AsSpan(), Span.End))
        {
            return false;
        }

        try
        {
            _ = new UTF8Encoding(false, true).GetCharCount(NewText.AsSpan());
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsBoundary(ReadOnlySpan<byte> source, int offset) =>
        offset == 0 || offset == source.Length || (source[offset] & 0xc0) != 0x80;
}
