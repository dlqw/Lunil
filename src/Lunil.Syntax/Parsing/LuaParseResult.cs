using System.Collections.Immutable;
using Lunil.Core.Diagnostics;
using Lunil.Core.Text;

namespace Lunil.Syntax.Parsing;

public sealed record LuaParseResult(
    SourceText Source,
    LuaSyntaxNode Root,
    ImmutableArray<Diagnostic> Diagnostics);
