using System.Collections.Immutable;
using Luac.Core.Diagnostics;
using Luac.Core.Text;

namespace Luac.Syntax.Lexing;

public sealed record LuaLexResult(
    SourceText Source,
    ImmutableArray<LuaSyntaxToken> Tokens,
    ImmutableArray<Diagnostic> Diagnostics);
