using System.Collections.Immutable;
using Lunil.Core.Diagnostics;
using Lunil.Syntax.Parsing;

namespace Lunil.Semantics.Binding;

public sealed record LuaSemanticModel(
    LuaParseResult Syntax,
    ImmutableArray<Diagnostic> Diagnostics,
    ImmutableArray<LuaSymbol> Symbols,
    ImmutableArray<LuaNameReference> References,
    ImmutableArray<LuaFunctionInfo> Functions);
