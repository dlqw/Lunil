using System.Collections.Immutable;
using Lunil.Core;
using Lunil.Core.Diagnostics;
using Lunil.Syntax.Parsing;

namespace Lunil.Semantics.Binding;

public sealed partial record LuaSemanticModel(
    LuaParseResult Syntax,
    ImmutableArray<Diagnostic> Diagnostics,
    ImmutableArray<LuaSymbol> Symbols,
    ImmutableArray<LuaNameReference> References,
    ImmutableArray<LuaFunctionInfo> Functions)
{
    public LuaLanguageVersion LanguageVersion => Syntax.LanguageVersion;
}
