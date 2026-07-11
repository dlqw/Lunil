using System.Collections.Immutable;
using Luac.Core.Diagnostics;
using Luac.Syntax.Parsing;

namespace Luac.Semantics.Binding;

public sealed record LuaSemanticModel(
    LuaParseResult Syntax,
    ImmutableArray<Diagnostic> Diagnostics,
    ImmutableArray<LuaSymbol> Symbols,
    ImmutableArray<LuaNameReference> References,
    ImmutableArray<LuaFunctionInfo> Functions);
