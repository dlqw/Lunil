using Lunil.Core;

namespace Lunil.Syntax.Lexing;

public sealed record LuaLexerOptions
{
    public static LuaLexerOptions Default { get; } = new();

    public static LuaLexerOptions File { get; } = new()
    {
        AcceptUtf8ByteOrderMark = true,
        AcceptShebang = true,
    };

    public LuaLanguageVersion LanguageVersion { get; init; } = LuaLanguageVersions.Default;

    public bool AcceptUtf8ByteOrderMark { get; init; }

    public bool AcceptShebang { get; init; }

    public int MaximumTokenCount { get; init; } = 1_000_000;

    public int MaximumDiagnosticCount { get; init; } = 1_000;
}
