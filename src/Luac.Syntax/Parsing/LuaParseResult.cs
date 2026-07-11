using System.Collections.Immutable;
using Luac.Core.Diagnostics;
using Luac.Core.Text;

namespace Luac.Syntax.Parsing;

public sealed record LuaParseResult(
    SourceText Source,
    LuaSyntaxNode Root,
    ImmutableArray<Diagnostic> Diagnostics);
