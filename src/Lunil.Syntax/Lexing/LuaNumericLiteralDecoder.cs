using Lunil.Core.Numerics;
using Lunil.Core.Text;

namespace Lunil.Syntax.Lexing;

/// <summary>Classifies and decodes a valid Lua 5.4 numeral as integer or float.</summary>
public static class LuaNumericLiteralDecoder
{
    public static LuaTokenValue Decode(SourceText source, LuaSyntaxToken token)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(token);

        if (token.Kind != LuaTokenKind.NumericLiteral)
        {
            throw new ArgumentException("Token is not a Lua numeric literal.", nameof(token));
        }

        if (!TryDecode(source.GetSpan(token.Span), out var value))
        {
            throw new FormatException("The token does not contain a valid Lua 5.4 numeral.");
        }

        return value;
    }

    internal static bool TryDecode(ReadOnlySpan<byte> text, out LuaTokenValue value)
    {
        value = null!;
        if (!LuaNumberParser.TryParseLiteral(text, out var number))
        {
            return false;
        }

        value = number.Kind switch
        {
            LuaNumberKind.Integer => new LuaIntegerTokenValue(number.Integer),
            LuaNumberKind.Float => new LuaFloatTokenValue(number.Float),
            _ => throw new InvalidOperationException(),
        };
        return true;
    }
}
