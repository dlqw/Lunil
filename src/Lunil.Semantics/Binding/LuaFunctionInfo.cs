using System.Collections.Immutable;
using Lunil.Core.Text;

namespace Lunil.Semantics.Binding;

public sealed record LuaFunctionInfo(
    int Id,
    TextSpan Span,
    bool IsVarArg,
    ImmutableArray<LuaSymbol> Symbols,
    ImmutableArray<LuaSymbol> Captures);
