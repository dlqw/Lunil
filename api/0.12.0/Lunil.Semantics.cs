// Target Frameworks: net10.0
#nullable enable

namespace Lunil.Semantics.Binding
{
    public static class LuaBinder
    {
        public static Lunil.Semantics.Binding.LuaSemanticModel Bind(Lunil.Syntax.Parsing.LuaParseResult syntax, Lunil.Semantics.Binding.LuaBinderOptions? options = null) => throw null;
    }

    public sealed class LuaBinderOptions : System.IEquatable<Lunil.Semantics.Binding.LuaBinderOptions>
    {
        public static Lunil.Semantics.Binding.LuaBinderOptions Default { get => throw null; }
        public Lunil.Core.LuaLanguageVersion LanguageVersion { get => throw null; init { } }
        public int MaximumActiveLocalsPerFunction { get => throw null; init { } }
        public int MaximumUpvaluesPerFunction { get => throw null; init { } }
        public int MaximumDiagnosticCount { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Semantics.Binding.LuaBinderOptions? left, Lunil.Semantics.Binding.LuaBinderOptions? right) => throw null;
        public static bool operator ==(Lunil.Semantics.Binding.LuaBinderOptions? left, Lunil.Semantics.Binding.LuaBinderOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Semantics.Binding.LuaBinderOptions? other) => throw null;
    }

    public sealed class LuaFunctionInfo : System.IEquatable<Lunil.Semantics.Binding.LuaFunctionInfo>
    {
        public int Id { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        public bool IsVarArg { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Semantics.Binding.LuaSymbol> Symbols { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Semantics.Binding.LuaSymbol> Captures { get => throw null; init { } }
        public LuaFunctionInfo(int Id, Lunil.Core.Text.TextSpan Span, bool IsVarArg, System.Collections.Immutable.ImmutableArray<Lunil.Semantics.Binding.LuaSymbol> Symbols, System.Collections.Immutable.ImmutableArray<Lunil.Semantics.Binding.LuaSymbol> Captures) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Semantics.Binding.LuaFunctionInfo? left, Lunil.Semantics.Binding.LuaFunctionInfo? right) => throw null;
        public static bool operator ==(Lunil.Semantics.Binding.LuaFunctionInfo? left, Lunil.Semantics.Binding.LuaFunctionInfo? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Semantics.Binding.LuaFunctionInfo? other) => throw null;
        public void Deconstruct(out int Id, out Lunil.Core.Text.TextSpan Span, out bool IsVarArg, out System.Collections.Immutable.ImmutableArray<Lunil.Semantics.Binding.LuaSymbol> Symbols, out System.Collections.Immutable.ImmutableArray<Lunil.Semantics.Binding.LuaSymbol> Captures) => throw null;
    }

    public enum LuaLocalAttributeKind
    {
        None = 0,
        Constant = 1,
        ToBeClosed = 2,
        VarArg = 3
    }

    public sealed class LuaNameReference : System.IEquatable<Lunil.Semantics.Binding.LuaNameReference>
    {
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        public string Name { get => throw null; init { } }
        public Lunil.Semantics.Binding.LuaNameResolutionKind ResolutionKind { get => throw null; init { } }
        public Lunil.Semantics.Binding.LuaSymbol Symbol { get => throw null; init { } }
        public bool IsWrite { get => throw null; init { } }
        public LuaNameReference(Lunil.Core.Text.TextSpan Span, string Name, Lunil.Semantics.Binding.LuaNameResolutionKind ResolutionKind, Lunil.Semantics.Binding.LuaSymbol Symbol, bool IsWrite) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Semantics.Binding.LuaNameReference? left, Lunil.Semantics.Binding.LuaNameReference? right) => throw null;
        public static bool operator ==(Lunil.Semantics.Binding.LuaNameReference? left, Lunil.Semantics.Binding.LuaNameReference? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Semantics.Binding.LuaNameReference? other) => throw null;
        public void Deconstruct(out Lunil.Core.Text.TextSpan Span, out string Name, out Lunil.Semantics.Binding.LuaNameResolutionKind ResolutionKind, out Lunil.Semantics.Binding.LuaSymbol Symbol, out bool IsWrite) => throw null;
    }

    public enum LuaNameResolutionKind
    {
        Local = 0,
        Upvalue = 1,
        Global = 2
    }

    public sealed class LuaSemanticModel : System.IEquatable<Lunil.Semantics.Binding.LuaSemanticModel>
    {
        public Lunil.Syntax.Parsing.LuaParseResult Syntax { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Core.Diagnostics.Diagnostic> Diagnostics { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Semantics.Binding.LuaSymbol> Symbols { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Semantics.Binding.LuaNameReference> References { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Semantics.Binding.LuaFunctionInfo> Functions { get => throw null; init { } }
        public Lunil.Core.LuaLanguageVersion LanguageVersion { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<Lunil.Semantics.Binding.LuaNameReference> FindReferences(Lunil.Semantics.Binding.LuaSymbol symbol) => throw null;
        public System.Collections.Immutable.ImmutableArray<Lunil.Semantics.Binding.LuaNameReference> FindGlobalReferences(string name) => throw null;
        public LuaSemanticModel(Lunil.Syntax.Parsing.LuaParseResult Syntax, System.Collections.Immutable.ImmutableArray<Lunil.Core.Diagnostics.Diagnostic> Diagnostics, System.Collections.Immutable.ImmutableArray<Lunil.Semantics.Binding.LuaSymbol> Symbols, System.Collections.Immutable.ImmutableArray<Lunil.Semantics.Binding.LuaNameReference> References, System.Collections.Immutable.ImmutableArray<Lunil.Semantics.Binding.LuaFunctionInfo> Functions) { }
        public Lunil.Semantics.Binding.LuaSymbolKey GetSymbolKey(Lunil.Semantics.Binding.LuaSymbol symbol, string moduleIdentity) => throw null;
        public Lunil.Semantics.Binding.LuaSymbolKey GetFunctionKey(Lunil.Semantics.Binding.LuaFunctionInfo function, string moduleIdentity) => throw null;
        public Lunil.Semantics.Binding.LuaSymbol? ResolveSymbolKey(Lunil.Semantics.Binding.LuaSymbolKey key, string moduleIdentity) => throw null;
        public Lunil.Semantics.Binding.LuaFunctionInfo? ResolveFunctionKey(Lunil.Semantics.Binding.LuaSymbolKey key, string moduleIdentity) => throw null;
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Semantics.Binding.LuaSemanticModel? left, Lunil.Semantics.Binding.LuaSemanticModel? right) => throw null;
        public static bool operator ==(Lunil.Semantics.Binding.LuaSemanticModel? left, Lunil.Semantics.Binding.LuaSemanticModel? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Semantics.Binding.LuaSemanticModel? other) => throw null;
        public void Deconstruct(out Lunil.Syntax.Parsing.LuaParseResult Syntax, out System.Collections.Immutable.ImmutableArray<Lunil.Core.Diagnostics.Diagnostic> Diagnostics, out System.Collections.Immutable.ImmutableArray<Lunil.Semantics.Binding.LuaSymbol> Symbols, out System.Collections.Immutable.ImmutableArray<Lunil.Semantics.Binding.LuaNameReference> References, out System.Collections.Immutable.ImmutableArray<Lunil.Semantics.Binding.LuaFunctionInfo> Functions) => throw null;
    }

    public sealed class LuaSymbol
    {
        public int Id { get => throw null; }
        public string Name { get => throw null; }
        public Lunil.Semantics.Binding.LuaSymbolKind Kind { get => throw null; }
        public Lunil.Semantics.Binding.LuaLocalAttributeKind Attribute { get => throw null; }
        public Lunil.Core.Text.TextSpan DeclaringSpan { get => throw null; }
        public int FunctionId { get => throw null; }
        public int ScopeDepth { get => throw null; }
        public bool IsReadOnly { get => throw null; }
        public bool IsCaptured { get => throw null; }
    }

    public readonly struct LuaSymbolKey : System.IEquatable<Lunil.Semantics.Binding.LuaSymbolKey>
    {
        public string Value { get => throw null; }
        public LuaSymbolKey(string value) { }
        public static bool TryParse(string? value, out Lunil.Semantics.Binding.LuaSymbolKey key) => throw null;
        public static Lunil.Semantics.Binding.LuaSymbolKey CreateSymbol(string moduleIdentity, Lunil.Semantics.Binding.LuaSymbolKind declarationKind, string lexicalOwner, string normalizedName, int ambiguityOrdinal = 0) => throw null;
        public static Lunil.Semantics.Binding.LuaSymbolKey CreateFunction(string moduleIdentity, string declarationKind, string lexicalOwner, string normalizedName, int ambiguityOrdinal = 0) => throw null;
        public static Lunil.Semantics.Binding.LuaSymbolKey CreateAnnotation(string moduleIdentity, string annotationKind, string normalizedName, int ambiguityOrdinal = 0) => throw null;
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Semantics.Binding.LuaSymbolKey left, Lunil.Semantics.Binding.LuaSymbolKey right) => throw null;
        public static bool operator ==(Lunil.Semantics.Binding.LuaSymbolKey left, Lunil.Semantics.Binding.LuaSymbolKey right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object obj) => throw null;
        public bool Equals(Lunil.Semantics.Binding.LuaSymbolKey other) => throw null;
    }

    public enum LuaSymbolKind
    {
        Environment = 0,
        Parameter = 1,
        Local = 2,
        NumericForVariable = 3,
        GenericForVariable = 4,
        Global = 5,
        GlobalWildcard = 6
    }
}
namespace Lunil.Semantics.Lowering
{
    public static class LuaLowerer
    {
        public static Lunil.Semantics.Lowering.LuaLoweringResult Lower(Lunil.Semantics.Binding.LuaSemanticModel semanticModel) => throw null;
    }

    public sealed class LuaLoweringResult : System.IEquatable<Lunil.Semantics.Lowering.LuaLoweringResult>
    {
        public Lunil.IR.Canonical.LuaIrModule? Module { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Core.Diagnostics.Diagnostic> Diagnostics { get => throw null; init { } }
        public bool Succeeded { get => throw null; }
        public LuaLoweringResult(Lunil.IR.Canonical.LuaIrModule? Module, System.Collections.Immutable.ImmutableArray<Lunil.Core.Diagnostics.Diagnostic> Diagnostics) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Semantics.Lowering.LuaLoweringResult? left, Lunil.Semantics.Lowering.LuaLoweringResult? right) => throw null;
        public static bool operator ==(Lunil.Semantics.Lowering.LuaLoweringResult? left, Lunil.Semantics.Lowering.LuaLoweringResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Semantics.Lowering.LuaLoweringResult? other) => throw null;
        public void Deconstruct(out Lunil.IR.Canonical.LuaIrModule? Module, out System.Collections.Immutable.ImmutableArray<Lunil.Core.Diagnostics.Diagnostic> Diagnostics) => throw null;
    }
}
