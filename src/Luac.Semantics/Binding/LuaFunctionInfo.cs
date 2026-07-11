using System.Collections.Immutable;
using Luac.Core.Text;

namespace Luac.Semantics.Binding;

public sealed record LuaFunctionInfo(
    int Id,
    TextSpan Span,
    bool IsVarArg,
    ImmutableArray<LuaSymbol> Symbols,
    ImmutableArray<LuaSymbol> Captures);
