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
    /// <summary>Gets non-lexical dot, colon, and bracket references in source order.</summary>
    public ImmutableArray<LuaMemberReference> MemberReferences { get; init; } = [];

    /// <summary>Gets the ordered union of lexical and non-lexical code references.</summary>
    public ImmutableArray<LuaCodeReference> UnifiedReferences { get; init; } = [];

    public LuaLanguageVersion LanguageVersion => Syntax.LanguageVersion;
}
