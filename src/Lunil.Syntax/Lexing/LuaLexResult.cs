using System.Collections.Immutable;
using Lunil.Core;
using Lunil.Core.Diagnostics;
using Lunil.Core.Text;

namespace Lunil.Syntax.Lexing;

public sealed record LuaLexResult(
    SourceText Source,
    ImmutableArray<LuaSyntaxToken> Tokens,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public LuaLanguageVersion LanguageVersion { get; init; } = LuaLanguageVersions.Default;
}
