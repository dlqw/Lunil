using Lunil.Core.Numerics;

namespace Lunil.Syntax.Lexing;

internal static class LuaNumericLiteralValidator
{
    public static bool IsValid(ReadOnlySpan<byte> text) =>
        LuaNumberParser.TryParseLiteral(text, out _);
}
