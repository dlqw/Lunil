// Target Frameworks: net10.0, netstandard2.1
#nullable enable

namespace Lunil.Analysis
{
    public sealed class LuaAliasDeclaration : Lunil.Analysis.LuaTypeDeclaration, System.IEquatable<Lunil.Analysis.LuaAliasDeclaration>
    {
        protected System.Type EqualityContract { get => throw null; }
        public string AliasName { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaGenericParameterType> TypeParameters { get => throw null; init { } }
        public Lunil.Analysis.LuaType Target { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan DeclaringSpan { get => throw null; init { } }
        public LuaAliasDeclaration(string AliasName, System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaGenericParameterType> TypeParameters, Lunil.Analysis.LuaType Target, Lunil.Core.Text.TextSpan DeclaringSpan) : base(default(string), default(Lunil.Analysis.LuaTypeDeclarationKind), default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Analysis.LuaAliasDeclaration? left, Lunil.Analysis.LuaAliasDeclaration? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaAliasDeclaration? left, Lunil.Analysis.LuaAliasDeclaration? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Analysis.LuaTypeDeclaration? other) => throw null;
        public bool Equals(Lunil.Analysis.LuaAliasDeclaration? other) => throw null;
        public void Deconstruct(out string AliasName, out System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaGenericParameterType> TypeParameters, out Lunil.Analysis.LuaType Target, out Lunil.Core.Text.TextSpan DeclaringSpan) => throw null;
    }

    public sealed class LuaAliasType : Lunil.Analysis.LuaType, System.IEquatable<Lunil.Analysis.LuaAliasType>
    {
        protected System.Type EqualityContract { get => throw null; }
        public string Name { get => throw null; init { } }
        public Lunil.Analysis.LuaType Target { get => throw null; init { } }
        public string DisplayName { get => throw null; }
        public LuaAliasType(string Name, Lunil.Analysis.LuaType Target) : base(default(Lunil.Analysis.LuaTypeKind)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Analysis.LuaAliasType? left, Lunil.Analysis.LuaAliasType? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaAliasType? left, Lunil.Analysis.LuaAliasType? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Analysis.LuaType? other) => throw null;
        public bool Equals(Lunil.Analysis.LuaAliasType? other) => throw null;
        public void Deconstruct(out string Name, out Lunil.Analysis.LuaType Target) => throw null;
    }

    public sealed class LuaAnalysisBudgetUsage : System.IEquatable<Lunil.Analysis.LuaAnalysisBudgetUsage>
    {
        public int TypeCount { get => throw null; init { } }
        public int ConstraintCount { get => throw null; init { } }
        public int ControlFlowBlockCount { get => throw null; init { } }
        public int GenericInstantiationCount { get => throw null; init { } }
        public int MaximumObservedTypeDepth { get => throw null; init { } }
        public bool WasExceeded { get => throw null; init { } }
        public LuaAnalysisBudgetUsage(int TypeCount, int ConstraintCount, int ControlFlowBlockCount, int GenericInstantiationCount, int MaximumObservedTypeDepth, bool WasExceeded) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaAnalysisBudgetUsage? left, Lunil.Analysis.LuaAnalysisBudgetUsage? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaAnalysisBudgetUsage? left, Lunil.Analysis.LuaAnalysisBudgetUsage? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaAnalysisBudgetUsage? other) => throw null;
        public void Deconstruct(out int TypeCount, out int ConstraintCount, out int ControlFlowBlockCount, out int GenericInstantiationCount, out int MaximumObservedTypeDepth, out bool WasExceeded) => throw null;
    }

    public sealed class LuaAnalysisEnvironment : System.IEquatable<Lunil.Analysis.LuaAnalysisEnvironment>
    {
        public static Lunil.Analysis.LuaAnalysisEnvironment Empty { get => throw null; }
        public System.Collections.Immutable.ImmutableDictionary<string, Lunil.Analysis.LuaType> ModuleTypes { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableDictionary<string, Lunil.Analysis.LuaExternalTypeDeclaration> ExternalTypeDeclarations { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableDictionary<string, System.Collections.Immutable.ImmutableDictionary<string, Lunil.Analysis.LuaType>> ExternalClassMembers { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableDictionary<string, Lunil.Analysis.LuaType> BuiltinGlobals { get => throw null; init { } }
        public Lunil.Analysis.LuaHostAnalysisContract? HostContract { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaAnalysisEnvironment? left, Lunil.Analysis.LuaAnalysisEnvironment? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaAnalysisEnvironment? left, Lunil.Analysis.LuaAnalysisEnvironment? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaAnalysisEnvironment? other) => throw null;
    }

    public sealed class LuaAnalysisOptions : System.IEquatable<Lunil.Analysis.LuaAnalysisOptions>
    {
        public static Lunil.Analysis.LuaAnalysisOptions Default { get => throw null; }
        public bool Enabled { get => throw null; init { } }
        public Lunil.Core.Diagnostics.DiagnosticSeverity DiagnosticSeverity { get => throw null; init { } }
        public bool ReportUnknownGlobals { get => throw null; init { } }
        public bool ReportImplicitAny { get => throw null; init { } }
        public bool ReportUnreachableCode { get => throw null; init { } }
        public bool ReportRedundantConditions { get => throw null; init { } }
        public int MaximumTypeCount { get => throw null; init { } }
        public int MaximumConstraintCount { get => throw null; init { } }
        public int MaximumControlFlowBlockCount { get => throw null; init { } }
        public int MaximumFlowIterations { get => throw null; init { } }
        public int MaximumUnionMemberCount { get => throw null; init { } }
        public int MaximumTypeDepth { get => throw null; init { } }
        public int MaximumGenericInstantiationCount { get => throw null; init { } }
        public int MaximumReturnPackLength { get => throw null; init { } }
        public int MaximumDiagnosticCount { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableHashSet<string> SuppressedDiagnosticCodes { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaAnalysisOptions? left, Lunil.Analysis.LuaAnalysisOptions? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaAnalysisOptions? left, Lunil.Analysis.LuaAnalysisOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaAnalysisOptions? other) => throw null;
    }

    public sealed class LuaAnalysisResult : System.IEquatable<Lunil.Analysis.LuaAnalysisResult>
    {
        public Lunil.Semantics.Binding.LuaSemanticModel SemanticModel { get => throw null; init { } }
        public Lunil.EmmyLua.LuaAnnotationDocument Annotations { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaTypeDeclaration> TypeDeclarations { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaSymbolTypeInfo> Symbols { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaExpressionTypeInfo> Expressions { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaFunctionAnalysis> Functions { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Core.Diagnostics.Diagnostic> Diagnostics { get => throw null; init { } }
        public Lunil.Analysis.LuaAnalysisBudgetUsage BudgetUsage { get => throw null; init { } }
        public Lunil.Analysis.LuaCallGraph CallGraph { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaMetatableFact> MetatableFacts { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaObjectModelFact> ObjectModels { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaHostEffectFact> HostEffects { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaCallbackRegistrationFact> CallbackRegistrations { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaPersistenceAccessFact> PersistenceAccesses { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaUpvalueCellFact> UpvalueCells { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaNilPathFact> NilPaths { get => throw null; init { } }
        public LuaAnalysisResult(Lunil.Semantics.Binding.LuaSemanticModel SemanticModel, Lunil.EmmyLua.LuaAnnotationDocument Annotations, System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaTypeDeclaration> TypeDeclarations, System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaSymbolTypeInfo> Symbols, System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaExpressionTypeInfo> Expressions, System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaFunctionAnalysis> Functions, System.Collections.Immutable.ImmutableArray<Lunil.Core.Diagnostics.Diagnostic> Diagnostics, Lunil.Analysis.LuaAnalysisBudgetUsage BudgetUsage) { }
        public static Lunil.Analysis.LuaAnalysisResult Empty(Lunil.Semantics.Binding.LuaSemanticModel semanticModel, Lunil.EmmyLua.LuaAnnotationDocument annotations) => throw null;
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaAnalysisResult? left, Lunil.Analysis.LuaAnalysisResult? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaAnalysisResult? left, Lunil.Analysis.LuaAnalysisResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaAnalysisResult? other) => throw null;
        public void Deconstruct(out Lunil.Semantics.Binding.LuaSemanticModel SemanticModel, out Lunil.EmmyLua.LuaAnnotationDocument Annotations, out System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaTypeDeclaration> TypeDeclarations, out System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaSymbolTypeInfo> Symbols, out System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaExpressionTypeInfo> Expressions, out System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaFunctionAnalysis> Functions, out System.Collections.Immutable.ImmutableArray<Lunil.Core.Diagnostics.Diagnostic> Diagnostics, out Lunil.Analysis.LuaAnalysisBudgetUsage BudgetUsage) => throw null;
    }

    public sealed class LuaArrayType : Lunil.Analysis.LuaType, System.IEquatable<Lunil.Analysis.LuaArrayType>
    {
        protected System.Type EqualityContract { get => throw null; }
        public Lunil.Analysis.LuaType ElementType { get => throw null; init { } }
        public string DisplayName { get => throw null; }
        public LuaArrayType(Lunil.Analysis.LuaType ElementType) : base(default(Lunil.Analysis.LuaTypeKind)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Analysis.LuaArrayType? left, Lunil.Analysis.LuaArrayType? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaArrayType? left, Lunil.Analysis.LuaArrayType? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Analysis.LuaType? other) => throw null;
        public bool Equals(Lunil.Analysis.LuaArrayType? other) => throw null;
        public void Deconstruct(out Lunil.Analysis.LuaType ElementType) => throw null;
    }

    public sealed class LuaBooleanLiteralType : Lunil.Analysis.LuaLiteralType, System.IEquatable<Lunil.Analysis.LuaBooleanLiteralType>
    {
        protected System.Type EqualityContract { get => throw null; }
        public bool Value { get => throw null; init { } }
        public string DisplayName { get => throw null; }
        public LuaBooleanLiteralType(bool Value) : base(default(Lunil.Analysis.LuaLiteralKind)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Analysis.LuaBooleanLiteralType? left, Lunil.Analysis.LuaBooleanLiteralType? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaBooleanLiteralType? left, Lunil.Analysis.LuaBooleanLiteralType? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Analysis.LuaLiteralType? other) => throw null;
        public bool Equals(Lunil.Analysis.LuaBooleanLiteralType? other) => throw null;
        public void Deconstruct(out bool Value) => throw null;
    }

    public sealed class LuaCallGraph : System.IEquatable<Lunil.Analysis.LuaCallGraph>
    {
        public System.Collections.Immutable.ImmutableArray<int> FunctionIds { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaCallSite> Edges { get => throw null; init { } }
        public static Lunil.Analysis.LuaCallGraph Empty { get => throw null; }
        public LuaCallGraph(System.Collections.Immutable.ImmutableArray<int> FunctionIds, System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaCallSite> Edges) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaCallGraph? left, Lunil.Analysis.LuaCallGraph? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaCallGraph? left, Lunil.Analysis.LuaCallGraph? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaCallGraph? other) => throw null;
        public void Deconstruct(out System.Collections.Immutable.ImmutableArray<int> FunctionIds, out System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaCallSite> Edges) => throw null;
    }

    public enum LuaCallKind
    {
        Direct = 0,
        Member = 1,
        Method = 2,
        Callable = 3,
        Dynamic = 4
    }

    public enum LuaCallResolutionStatus
    {
        Resolved = 0,
        Dynamic = 1,
        Unresolved = 2
    }

    public sealed class LuaCallSite : System.IEquatable<Lunil.Analysis.LuaCallSite>
    {
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        public int ContainingFunctionId { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan CalleeSpan { get => throw null; init { } }
        public Lunil.Analysis.LuaCallKind Kind { get => throw null; init { } }
        public Lunil.Semantics.Binding.LuaSymbol? DirectSymbol { get => throw null; init { } }
        public string? DirectName { get => throw null; init { } }
        public Lunil.Analysis.LuaType CalleeType { get => throw null; init { } }
        public Lunil.Analysis.LuaMemberTarget? MemberTarget { get => throw null; init { } }
        public string? ModuleRequest { get => throw null; init { } }
        public int? TargetFunctionId { get => throw null; init { } }
        public Lunil.Analysis.LuaCallResolutionStatus ResolutionStatus { get => throw null; init { } }
        public string? UnresolvedReason { get => throw null; init { } }
        public LuaCallSite(Lunil.Core.Text.TextSpan Span, int ContainingFunctionId, Lunil.Core.Text.TextSpan CalleeSpan, Lunil.Analysis.LuaCallKind Kind, Lunil.Semantics.Binding.LuaSymbol? DirectSymbol, string? DirectName, Lunil.Analysis.LuaType CalleeType, Lunil.Analysis.LuaMemberTarget? MemberTarget, string? ModuleRequest, int? TargetFunctionId, Lunil.Analysis.LuaCallResolutionStatus ResolutionStatus, string? UnresolvedReason) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaCallSite? left, Lunil.Analysis.LuaCallSite? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaCallSite? left, Lunil.Analysis.LuaCallSite? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaCallSite? other) => throw null;
        public void Deconstruct(out Lunil.Core.Text.TextSpan Span, out int ContainingFunctionId, out Lunil.Core.Text.TextSpan CalleeSpan, out Lunil.Analysis.LuaCallKind Kind, out Lunil.Semantics.Binding.LuaSymbol? DirectSymbol, out string? DirectName, out Lunil.Analysis.LuaType CalleeType, out Lunil.Analysis.LuaMemberTarget? MemberTarget, out string? ModuleRequest, out int? TargetFunctionId, out Lunil.Analysis.LuaCallResolutionStatus ResolutionStatus, out string? UnresolvedReason) => throw null;
    }

    public static class LuaCallUnresolvedReasons
    {
        public const string CalleeSignatureIsDynamic = "callee-signature-is-dynamic";
        public const string ModuleRequestIsDynamic = "module-request-is-dynamic";
        public const string CalleeIsNotCallable = "callee-is-not-callable";
        public const string CallWasNotAnalyzed = "call-was-not-analyzed";
    }

    public sealed class LuaCallableType : Lunil.Analysis.LuaType, System.IEquatable<Lunil.Analysis.LuaCallableType>
    {
        protected System.Type EqualityContract { get => throw null; }
        public Lunil.Analysis.LuaType ReceiverType { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaFunctionType> Signatures { get => throw null; init { } }
        public string DisplayName { get => throw null; }
        public LuaCallableType(Lunil.Analysis.LuaType ReceiverType, System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaFunctionType> Signatures) : base(default(Lunil.Analysis.LuaTypeKind)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Analysis.LuaCallableType? left, Lunil.Analysis.LuaCallableType? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaCallableType? left, Lunil.Analysis.LuaCallableType? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Analysis.LuaType? other) => throw null;
        public bool Equals(Lunil.Analysis.LuaCallableType? other) => throw null;
        public void Deconstruct(out Lunil.Analysis.LuaType ReceiverType, out System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaFunctionType> Signatures) => throw null;
    }

    public sealed class LuaCallbackRegistrationFact : System.IEquatable<Lunil.Analysis.LuaCallbackRegistrationFact>
    {
        public string FunctionPath { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan CallbackSpan { get => throw null; init { } }
        public int? CallbackFunctionId { get => throw null; init { } }
        public Lunil.Analysis.LuaHostCallbackInvocationKind Invocation { get => throw null; init { } }
        public Lunil.Analysis.LuaHostCallbackCardinality Cardinality { get => throw null; init { } }
        public Lunil.Analysis.LuaHostCallbackRetentionKind Retention { get => throw null; init { } }
        public string? UnsubscribeFunction { get => throw null; init { } }
        public bool Escapes { get => throw null; init { } }
        public LuaCallbackRegistrationFact(string FunctionPath, Lunil.Core.Text.TextSpan Span, Lunil.Core.Text.TextSpan CallbackSpan, int? CallbackFunctionId, Lunil.Analysis.LuaHostCallbackInvocationKind Invocation, Lunil.Analysis.LuaHostCallbackCardinality Cardinality, Lunil.Analysis.LuaHostCallbackRetentionKind Retention, string? UnsubscribeFunction, bool Escapes) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaCallbackRegistrationFact? left, Lunil.Analysis.LuaCallbackRegistrationFact? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaCallbackRegistrationFact? left, Lunil.Analysis.LuaCallbackRegistrationFact? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaCallbackRegistrationFact? other) => throw null;
        public void Deconstruct(out string FunctionPath, out Lunil.Core.Text.TextSpan Span, out Lunil.Core.Text.TextSpan CallbackSpan, out int? CallbackFunctionId, out Lunil.Analysis.LuaHostCallbackInvocationKind Invocation, out Lunil.Analysis.LuaHostCallbackCardinality Cardinality, out Lunil.Analysis.LuaHostCallbackRetentionKind Retention, out string? UnsubscribeFunction, out bool Escapes) => throw null;
    }

    public sealed class LuaClassDeclaration : Lunil.Analysis.LuaTypeDeclaration, System.IEquatable<Lunil.Analysis.LuaClassDeclaration>
    {
        protected System.Type EqualityContract { get => throw null; }
        public string ClassName { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaGenericParameterType> TypeParameters { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaType> BaseTypes { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaTableField> Fields { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaFunctionType> CallSignatures { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableDictionary<string, Lunil.Analysis.LuaFunctionType> Operators { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan DeclaringSpan { get => throw null; init { } }
        public LuaClassDeclaration(string ClassName, System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaGenericParameterType> TypeParameters, System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaType> BaseTypes, System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaTableField> Fields, System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaFunctionType> CallSignatures, System.Collections.Immutable.ImmutableDictionary<string, Lunil.Analysis.LuaFunctionType> Operators, Lunil.Core.Text.TextSpan DeclaringSpan) : base(default(string), default(Lunil.Analysis.LuaTypeDeclarationKind), default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Analysis.LuaClassDeclaration? left, Lunil.Analysis.LuaClassDeclaration? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaClassDeclaration? left, Lunil.Analysis.LuaClassDeclaration? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Analysis.LuaTypeDeclaration? other) => throw null;
        public bool Equals(Lunil.Analysis.LuaClassDeclaration? other) => throw null;
        public void Deconstruct(out string ClassName, out System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaGenericParameterType> TypeParameters, out System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaType> BaseTypes, out System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaTableField> Fields, out System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaFunctionType> CallSignatures, out System.Collections.Immutable.ImmutableDictionary<string, Lunil.Analysis.LuaFunctionType> Operators, out Lunil.Core.Text.TextSpan DeclaringSpan) => throw null;
    }

    public sealed class LuaClassType : Lunil.Analysis.LuaType, System.IEquatable<Lunil.Analysis.LuaClassType>
    {
        protected System.Type EqualityContract { get => throw null; }
        public string Name { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaType> TypeArguments { get => throw null; init { } }
        public string DisplayName { get => throw null; }
        public LuaClassType(string Name, System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaType> TypeArguments) : base(default(Lunil.Analysis.LuaTypeKind)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Analysis.LuaClassType? left, Lunil.Analysis.LuaClassType? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaClassType? left, Lunil.Analysis.LuaClassType? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Analysis.LuaType? other) => throw null;
        public bool Equals(Lunil.Analysis.LuaClassType? other) => throw null;
        public void Deconstruct(out string Name, out System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaType> TypeArguments) => throw null;
    }

    public sealed class LuaControlFlowBlock : System.IEquatable<Lunil.Analysis.LuaControlFlowBlock>
    {
        public int Id { get => throw null; init { } }
        public Lunil.Analysis.LuaControlFlowBlockKind Kind { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Core.Text.TextSpan> StatementSpans { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaControlFlowEdge> Successors { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<int> Predecessors { get => throw null; init { } }
        public bool IsReachable { get => throw null; init { } }
        public LuaControlFlowBlock(int Id, Lunil.Analysis.LuaControlFlowBlockKind Kind, Lunil.Core.Text.TextSpan Span, System.Collections.Immutable.ImmutableArray<Lunil.Core.Text.TextSpan> StatementSpans, System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaControlFlowEdge> Successors, System.Collections.Immutable.ImmutableArray<int> Predecessors, bool IsReachable) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaControlFlowBlock? left, Lunil.Analysis.LuaControlFlowBlock? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaControlFlowBlock? left, Lunil.Analysis.LuaControlFlowBlock? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaControlFlowBlock? other) => throw null;
        public void Deconstruct(out int Id, out Lunil.Analysis.LuaControlFlowBlockKind Kind, out Lunil.Core.Text.TextSpan Span, out System.Collections.Immutable.ImmutableArray<Lunil.Core.Text.TextSpan> StatementSpans, out System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaControlFlowEdge> Successors, out System.Collections.Immutable.ImmutableArray<int> Predecessors, out bool IsReachable) => throw null;
    }

    public enum LuaControlFlowBlockKind
    {
        Entry = 0,
        Exit = 1,
        Statement = 2,
        Condition = 3,
        LoopHeader = 4,
        Label = 5
    }

    public sealed class LuaControlFlowEdge : System.IEquatable<Lunil.Analysis.LuaControlFlowEdge>
    {
        public int TargetBlockId { get => throw null; init { } }
        public Lunil.Analysis.LuaControlFlowEdgeKind Kind { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan ConditionSpan { get => throw null; init { } }
        public LuaControlFlowEdge(int TargetBlockId, Lunil.Analysis.LuaControlFlowEdgeKind Kind, Lunil.Core.Text.TextSpan ConditionSpan = null) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaControlFlowEdge? left, Lunil.Analysis.LuaControlFlowEdge? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaControlFlowEdge? left, Lunil.Analysis.LuaControlFlowEdge? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaControlFlowEdge? other) => throw null;
        public void Deconstruct(out int TargetBlockId, out Lunil.Analysis.LuaControlFlowEdgeKind Kind, out Lunil.Core.Text.TextSpan ConditionSpan) => throw null;
    }

    public enum LuaControlFlowEdgeKind
    {
        Next = 0,
        True = 1,
        False = 2,
        Loop = 3,
        Break = 4,
        Goto = 5,
        Return = 6
    }

    public sealed class LuaControlFlowGraph : System.IEquatable<Lunil.Analysis.LuaControlFlowGraph>
    {
        public int FunctionId { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        public int EntryBlockId { get => throw null; init { } }
        public int ExitBlockId { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaControlFlowBlock> Blocks { get => throw null; init { } }
        public LuaControlFlowGraph(int FunctionId, Lunil.Core.Text.TextSpan Span, int EntryBlockId, int ExitBlockId, System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaControlFlowBlock> Blocks) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaControlFlowGraph? left, Lunil.Analysis.LuaControlFlowGraph? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaControlFlowGraph? left, Lunil.Analysis.LuaControlFlowGraph? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaControlFlowGraph? other) => throw null;
        public void Deconstruct(out int FunctionId, out Lunil.Core.Text.TextSpan Span, out int EntryBlockId, out int ExitBlockId, out System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaControlFlowBlock> Blocks) => throw null;
    }

    public sealed class LuaEnumDeclaration : Lunil.Analysis.LuaTypeDeclaration, System.IEquatable<Lunil.Analysis.LuaEnumDeclaration>
    {
        protected System.Type EqualityContract { get => throw null; }
        public string EnumName { get => throw null; init { } }
        public Lunil.Analysis.LuaType UnderlyingType { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaLiteralType> Members { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan DeclaringSpan { get => throw null; init { } }
        public LuaEnumDeclaration(string EnumName, Lunil.Analysis.LuaType UnderlyingType, System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaLiteralType> Members, Lunil.Core.Text.TextSpan DeclaringSpan) : base(default(string), default(Lunil.Analysis.LuaTypeDeclarationKind), default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Analysis.LuaEnumDeclaration? left, Lunil.Analysis.LuaEnumDeclaration? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaEnumDeclaration? left, Lunil.Analysis.LuaEnumDeclaration? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Analysis.LuaTypeDeclaration? other) => throw null;
        public bool Equals(Lunil.Analysis.LuaEnumDeclaration? other) => throw null;
        public void Deconstruct(out string EnumName, out Lunil.Analysis.LuaType UnderlyingType, out System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaLiteralType> Members, out Lunil.Core.Text.TextSpan DeclaringSpan) => throw null;
    }

    public sealed class LuaEnumType : Lunil.Analysis.LuaType, System.IEquatable<Lunil.Analysis.LuaEnumType>
    {
        protected System.Type EqualityContract { get => throw null; }
        public string Name { get => throw null; init { } }
        public Lunil.Analysis.LuaType UnderlyingType { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaLiteralType> Members { get => throw null; init { } }
        public string DisplayName { get => throw null; }
        public LuaEnumType(string Name, Lunil.Analysis.LuaType UnderlyingType, System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaLiteralType> Members) : base(default(Lunil.Analysis.LuaTypeKind)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Analysis.LuaEnumType? left, Lunil.Analysis.LuaEnumType? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaEnumType? left, Lunil.Analysis.LuaEnumType? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Analysis.LuaType? other) => throw null;
        public bool Equals(Lunil.Analysis.LuaEnumType? other) => throw null;
        public void Deconstruct(out string Name, out Lunil.Analysis.LuaType UnderlyingType, out System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaLiteralType> Members) => throw null;
    }

    public sealed class LuaExpressionTypeInfo : System.IEquatable<Lunil.Analysis.LuaExpressionTypeInfo>
    {
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        public Lunil.Analysis.LuaType Type { get => throw null; init { } }
        public LuaExpressionTypeInfo(Lunil.Core.Text.TextSpan Span, Lunil.Analysis.LuaType Type) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaExpressionTypeInfo? left, Lunil.Analysis.LuaExpressionTypeInfo? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaExpressionTypeInfo? left, Lunil.Analysis.LuaExpressionTypeInfo? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaExpressionTypeInfo? other) => throw null;
        public void Deconstruct(out Lunil.Core.Text.TextSpan Span, out Lunil.Analysis.LuaType Type) => throw null;
    }

    public sealed class LuaExternalTypeDeclaration : System.IEquatable<Lunil.Analysis.LuaExternalTypeDeclaration>
    {
        public string Name { get => throw null; init { } }
        public Lunil.EmmyLua.LuaAnnotationSyntax Root { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaAnnotationSyntax> Extras { get => throw null; init { } }
        public LuaExternalTypeDeclaration(string Name, Lunil.EmmyLua.LuaAnnotationSyntax Root, System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaAnnotationSyntax> Extras) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaExternalTypeDeclaration? left, Lunil.Analysis.LuaExternalTypeDeclaration? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaExternalTypeDeclaration? left, Lunil.Analysis.LuaExternalTypeDeclaration? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaExternalTypeDeclaration? other) => throw null;
        public void Deconstruct(out string Name, out Lunil.EmmyLua.LuaAnnotationSyntax Root, out System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaAnnotationSyntax> Extras) => throw null;
    }

    public static class LuaExternalTypeDeclarations
    {
        public static System.Collections.Immutable.ImmutableDictionary<string, Lunil.Analysis.LuaExternalTypeDeclaration> Collect(Lunil.EmmyLua.LuaAnnotationDocument document) => throw null;
    }

    public sealed class LuaFloatLiteralType : Lunil.Analysis.LuaLiteralType, System.IEquatable<Lunil.Analysis.LuaFloatLiteralType>
    {
        protected System.Type EqualityContract { get => throw null; }
        public double Value { get => throw null; init { } }
        public string DisplayName { get => throw null; }
        public LuaFloatLiteralType(double Value) : base(default(Lunil.Analysis.LuaLiteralKind)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Analysis.LuaFloatLiteralType? left, Lunil.Analysis.LuaFloatLiteralType? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaFloatLiteralType? left, Lunil.Analysis.LuaFloatLiteralType? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Analysis.LuaLiteralType? other) => throw null;
        public bool Equals(Lunil.Analysis.LuaFloatLiteralType? other) => throw null;
        public void Deconstruct(out double Value) => throw null;
    }

    public sealed class LuaFunctionAnalysis : System.IEquatable<Lunil.Analysis.LuaFunctionAnalysis>
    {
        public int FunctionId { get => throw null; init { } }
        public Lunil.Analysis.LuaFunctionType Type { get => throw null; init { } }
        public Lunil.Analysis.LuaTypePack InferredReturns { get => throw null; init { } }
        public Lunil.Analysis.LuaControlFlowGraph ControlFlowGraph { get => throw null; init { } }
        public int FlowIterationCount { get => throw null; init { } }
        public bool WasWidened { get => throw null; init { } }
        public LuaFunctionAnalysis(int FunctionId, Lunil.Analysis.LuaFunctionType Type, Lunil.Analysis.LuaTypePack InferredReturns, Lunil.Analysis.LuaControlFlowGraph ControlFlowGraph, int FlowIterationCount, bool WasWidened) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaFunctionAnalysis? left, Lunil.Analysis.LuaFunctionAnalysis? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaFunctionAnalysis? left, Lunil.Analysis.LuaFunctionAnalysis? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaFunctionAnalysis? other) => throw null;
        public void Deconstruct(out int FunctionId, out Lunil.Analysis.LuaFunctionType Type, out Lunil.Analysis.LuaTypePack InferredReturns, out Lunil.Analysis.LuaControlFlowGraph ControlFlowGraph, out int FlowIterationCount, out bool WasWidened) => throw null;
    }

    public sealed class LuaFunctionParameter : System.IEquatable<Lunil.Analysis.LuaFunctionParameter>
    {
        public string? Name { get => throw null; init { } }
        public Lunil.Analysis.LuaType Type { get => throw null; init { } }
        public bool IsOptional { get => throw null; init { } }
        public bool IsVararg { get => throw null; init { } }
        public LuaFunctionParameter(string? Name, Lunil.Analysis.LuaType Type, bool IsOptional = false, bool IsVararg = false) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaFunctionParameter? left, Lunil.Analysis.LuaFunctionParameter? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaFunctionParameter? left, Lunil.Analysis.LuaFunctionParameter? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaFunctionParameter? other) => throw null;
        public void Deconstruct(out string? Name, out Lunil.Analysis.LuaType Type, out bool IsOptional, out bool IsVararg) => throw null;
    }

    public sealed class LuaFunctionType : Lunil.Analysis.LuaType, System.IEquatable<Lunil.Analysis.LuaFunctionType>
    {
        protected System.Type EqualityContract { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaFunctionParameter> Parameters { get => throw null; init { } }
        public Lunil.Analysis.LuaTypePack Returns { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaGenericParameterType> TypeParameters { get => throw null; init { } }
        public bool HasImplicitSelf { get => throw null; init { } }
        public string DisplayName { get => throw null; }
        public LuaFunctionType(System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaFunctionParameter> Parameters, Lunil.Analysis.LuaTypePack Returns, System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaGenericParameterType> TypeParameters, bool HasImplicitSelf = false) : base(default(Lunil.Analysis.LuaTypeKind)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Analysis.LuaFunctionType? left, Lunil.Analysis.LuaFunctionType? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaFunctionType? left, Lunil.Analysis.LuaFunctionType? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Analysis.LuaType? other) => throw null;
        public bool Equals(Lunil.Analysis.LuaFunctionType? other) => throw null;
        public void Deconstruct(out System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaFunctionParameter> Parameters, out Lunil.Analysis.LuaTypePack Returns, out System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaGenericParameterType> TypeParameters, out bool HasImplicitSelf) => throw null;
    }

    public sealed class LuaGenericInstanceType : Lunil.Analysis.LuaType, System.IEquatable<Lunil.Analysis.LuaGenericInstanceType>
    {
        protected System.Type EqualityContract { get => throw null; }
        public Lunil.Analysis.LuaType Definition { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaType> TypeArguments { get => throw null; init { } }
        public string DisplayName { get => throw null; }
        public LuaGenericInstanceType(Lunil.Analysis.LuaType Definition, System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaType> TypeArguments) : base(default(Lunil.Analysis.LuaTypeKind)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Analysis.LuaGenericInstanceType? left, Lunil.Analysis.LuaGenericInstanceType? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaGenericInstanceType? left, Lunil.Analysis.LuaGenericInstanceType? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Analysis.LuaType? other) => throw null;
        public bool Equals(Lunil.Analysis.LuaGenericInstanceType? other) => throw null;
        public void Deconstruct(out Lunil.Analysis.LuaType Definition, out System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaType> TypeArguments) => throw null;
    }

    public sealed class LuaGenericParameterType : Lunil.Analysis.LuaType, System.IEquatable<Lunil.Analysis.LuaGenericParameterType>
    {
        protected System.Type EqualityContract { get => throw null; }
        public string Name { get => throw null; init { } }
        public int Ordinal { get => throw null; init { } }
        public Lunil.Analysis.LuaType? Constraint { get => throw null; init { } }
        public string DisplayName { get => throw null; }
        public LuaGenericParameterType(string Name, int Ordinal, Lunil.Analysis.LuaType? Constraint = null) : base(default(Lunil.Analysis.LuaTypeKind)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Analysis.LuaGenericParameterType? left, Lunil.Analysis.LuaGenericParameterType? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaGenericParameterType? left, Lunil.Analysis.LuaGenericParameterType? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Analysis.LuaType? other) => throw null;
        public bool Equals(Lunil.Analysis.LuaGenericParameterType? other) => throw null;
        public void Deconstruct(out string Name, out int Ordinal, out Lunil.Analysis.LuaType? Constraint) => throw null;
    }

    public sealed class LuaHostAnalysisContract : System.IEquatable<Lunil.Analysis.LuaHostAnalysisContract>
    {
        public const int CurrentSchemaVersion = 1;
        public int SchemaVersion { get => throw null; init { } }
        public string ContractId { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableDictionary<string, Lunil.Analysis.LuaHostTypeDescriptor> Globals { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableDictionary<string, Lunil.Analysis.LuaHostTypeDescriptor> Modules { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableDictionary<string, Lunil.Analysis.LuaHostFunctionContract> Functions { get => throw null; init { } }
        public string ToJson() => throw null;
        public string ToLuaStub() => throw null;
        public static Lunil.Analysis.LuaHostAnalysisContract ParseJson(string json) => throw null;
        public void Validate() { }
        public System.Collections.Immutable.ImmutableDictionary<string, Lunil.Analysis.LuaType> CreateGlobalTypes() => throw null;
        public System.Collections.Immutable.ImmutableDictionary<string, Lunil.Analysis.LuaType> CreateModuleTypes() => throw null;
        public static Lunil.Analysis.LuaType ToLuaType(Lunil.Analysis.LuaHostTypeDescriptor descriptor) => throw null;
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaHostAnalysisContract? left, Lunil.Analysis.LuaHostAnalysisContract? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaHostAnalysisContract? left, Lunil.Analysis.LuaHostAnalysisContract? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaHostAnalysisContract? other) => throw null;
    }

    public enum LuaHostCallbackCardinality
    {
        Once = 0,
        Many = 1
    }

    public sealed class LuaHostCallbackContract : System.IEquatable<Lunil.Analysis.LuaHostCallbackContract>
    {
        public int ParameterIndex { get => throw null; init { } }
        public Lunil.Analysis.LuaHostCallbackInvocationKind Invocation { get => throw null; init { } }
        public Lunil.Analysis.LuaHostCallbackCardinality Cardinality { get => throw null; init { } }
        public Lunil.Analysis.LuaHostCallbackRetentionKind Retention { get => throw null; init { } }
        public string? UnsubscribeFunction { get => throw null; init { } }
        public bool MayYield { get => throw null; init { } }
        public bool MayThrow { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaHostCallbackContract? left, Lunil.Analysis.LuaHostCallbackContract? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaHostCallbackContract? left, Lunil.Analysis.LuaHostCallbackContract? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaHostCallbackContract? other) => throw null;
    }

    public enum LuaHostCallbackInvocationKind
    {
        Synchronous = 0,
        Deferred = 1,
        Asynchronous = 2
    }

    public enum LuaHostCallbackRetentionKind
    {
        Borrowed = 0,
        Stored = 1
    }

    public sealed class LuaHostContractBuilder
    {
        public LuaHostContractBuilder(string contractId) { }
        public Lunil.Analysis.LuaHostContractBuilder AddGlobal(string name, Lunil.Analysis.LuaHostTypeDescriptor type) => throw null;
        public Lunil.Analysis.LuaHostContractBuilder AddModule(string name, Lunil.Analysis.LuaHostTypeDescriptor type) => throw null;
        public Lunil.Analysis.LuaHostContractBuilder AddFunction(Lunil.Analysis.LuaHostFunctionContract function) => throw null;
        public Lunil.Analysis.LuaHostAnalysisContract Build() => throw null;
    }

    public sealed class LuaHostEffectFact : System.IEquatable<Lunil.Analysis.LuaHostEffectFact>
    {
        public string FunctionPath { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        public Lunil.Analysis.LuaHostEffectKind Effects { get => throw null; init { } }
        public Lunil.Analysis.LuaHostSourceLocation? Source { get => throw null; init { } }
        public LuaHostEffectFact(string FunctionPath, Lunil.Core.Text.TextSpan Span, Lunil.Analysis.LuaHostEffectKind Effects, Lunil.Analysis.LuaHostSourceLocation? Source) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaHostEffectFact? left, Lunil.Analysis.LuaHostEffectFact? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaHostEffectFact? left, Lunil.Analysis.LuaHostEffectFact? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaHostEffectFact? other) => throw null;
        public void Deconstruct(out string FunctionPath, out Lunil.Core.Text.TextSpan Span, out Lunil.Analysis.LuaHostEffectKind Effects, out Lunil.Analysis.LuaHostSourceLocation? Source) => throw null;
    }

    [System.Flags]
    public enum LuaHostEffectKind
    {
        None = 0,
        ReadsGlobal = 1,
        WritesGlobal = 2,
        ReadsTable = 4,
        WritesTable = 8,
        MayYield = 16,
        MayThrow = 32,
        RegistersCallback = 64,
        UnregistersCallback = 128,
        ReadsPersistence = 256,
        WritesPersistence = 512,
        DeletesPersistence = 1024,
        ClearsPersistence = 2048
    }

    public sealed class LuaHostFunctionContract : System.IEquatable<Lunil.Analysis.LuaHostFunctionContract>
    {
        public string Path { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaHostParameterContract> Parameters { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaHostTypeDescriptor> Returns { get => throw null; init { } }
        public bool HasVariadicParameters { get => throw null; init { } }
        public bool HasVariadicReturns { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaHostFunctionOverloadContract> Overloads { get => throw null; init { } }
        public Lunil.Analysis.LuaHostEffectKind Effects { get => throw null; init { } }
        public Lunil.Analysis.LuaHostCallbackContract? Callback { get => throw null; init { } }
        public Lunil.Analysis.LuaHostPersistenceContract? Persistence { get => throw null; init { } }
        public Lunil.Analysis.LuaHostSourceLocation? Source { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaHostFunctionContract? left, Lunil.Analysis.LuaHostFunctionContract? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaHostFunctionContract? left, Lunil.Analysis.LuaHostFunctionContract? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaHostFunctionContract? other) => throw null;
    }

    public sealed class LuaHostFunctionOverloadContract : System.IEquatable<Lunil.Analysis.LuaHostFunctionOverloadContract>
    {
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaHostParameterContract> Parameters { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaHostTypeDescriptor> Returns { get => throw null; init { } }
        public bool HasVariadicParameters { get => throw null; init { } }
        public bool HasVariadicReturns { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaHostFunctionOverloadContract? left, Lunil.Analysis.LuaHostFunctionOverloadContract? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaHostFunctionOverloadContract? left, Lunil.Analysis.LuaHostFunctionOverloadContract? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaHostFunctionOverloadContract? other) => throw null;
    }

    public sealed class LuaHostParameterContract : System.IEquatable<Lunil.Analysis.LuaHostParameterContract>
    {
        public string Name { get => throw null; init { } }
        public Lunil.Analysis.LuaHostTypeDescriptor Type { get => throw null; init { } }
        public bool IsOptional { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaHostParameterContract? left, Lunil.Analysis.LuaHostParameterContract? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaHostParameterContract? left, Lunil.Analysis.LuaHostParameterContract? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaHostParameterContract? other) => throw null;
    }

    public sealed class LuaHostPersistenceContract : System.IEquatable<Lunil.Analysis.LuaHostPersistenceContract>
    {
        public Lunil.Analysis.LuaPersistenceOperationKind Operation { get => throw null; init { } }
        public int? KeyParameterIndex { get => throw null; init { } }
        public int? ValueParameterIndex { get => throw null; init { } }
        public string SchemaId { get => throw null; init { } }
        public int SchemaVersion { get => throw null; init { } }
        public Lunil.Analysis.LuaHostTypeDescriptor ValueType { get => throw null; init { } }
        public bool MissingReturnsNil { get => throw null; init { } }
        public string? MigrationFunction { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaHostPersistenceContract? left, Lunil.Analysis.LuaHostPersistenceContract? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaHostPersistenceContract? left, Lunil.Analysis.LuaHostPersistenceContract? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaHostPersistenceContract? other) => throw null;
    }

    public sealed class LuaHostSourceLocation : System.IEquatable<Lunil.Analysis.LuaHostSourceLocation>
    {
        public string Uri { get => throw null; init { } }
        public int Line { get => throw null; init { } }
        public int Column { get => throw null; init { } }
        public string? ImplementationUri { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaHostSourceLocation? left, Lunil.Analysis.LuaHostSourceLocation? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaHostSourceLocation? left, Lunil.Analysis.LuaHostSourceLocation? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaHostSourceLocation? other) => throw null;
    }

    public sealed class LuaHostTypeDescriptor : System.IEquatable<Lunil.Analysis.LuaHostTypeDescriptor>
    {
        public Lunil.Analysis.LuaHostTypeKind Kind { get => throw null; init { } }
        public string? Name { get => throw null; init { } }
        public bool IsNullable { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableDictionary<string, Lunil.Analysis.LuaHostTypeDescriptor> Fields { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaHostParameterContract> Parameters { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaHostTypeDescriptor> Returns { get => throw null; init { } }
        public bool HasVariadicParameters { get => throw null; init { } }
        public bool HasVariadicReturns { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaHostTypeDescriptor? left, Lunil.Analysis.LuaHostTypeDescriptor? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaHostTypeDescriptor? left, Lunil.Analysis.LuaHostTypeDescriptor? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaHostTypeDescriptor? other) => throw null;
    }

    public enum LuaHostTypeKind
    {
        Any = 0,
        Nil = 1,
        Boolean = 2,
        Integer = 3,
        Number = 4,
        String = 5,
        Table = 6,
        Function = 7,
        Thread = 8,
        Userdata = 9
    }

    public sealed class LuaIntegerLiteralType : Lunil.Analysis.LuaLiteralType, System.IEquatable<Lunil.Analysis.LuaIntegerLiteralType>
    {
        protected System.Type EqualityContract { get => throw null; }
        public long Value { get => throw null; init { } }
        public string DisplayName { get => throw null; }
        public LuaIntegerLiteralType(long Value) : base(default(Lunil.Analysis.LuaLiteralKind)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Analysis.LuaIntegerLiteralType? left, Lunil.Analysis.LuaIntegerLiteralType? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaIntegerLiteralType? left, Lunil.Analysis.LuaIntegerLiteralType? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Analysis.LuaLiteralType? other) => throw null;
        public bool Equals(Lunil.Analysis.LuaIntegerLiteralType? other) => throw null;
        public void Deconstruct(out long Value) => throw null;
    }

    public sealed class LuaIntersectionType : Lunil.Analysis.LuaType, System.IEquatable<Lunil.Analysis.LuaIntersectionType>
    {
        protected System.Type EqualityContract { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaType> Types { get => throw null; init { } }
        public string DisplayName { get => throw null; }
        public LuaIntersectionType(System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaType> Types) : base(default(Lunil.Analysis.LuaTypeKind)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Analysis.LuaIntersectionType? left, Lunil.Analysis.LuaIntersectionType? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaIntersectionType? left, Lunil.Analysis.LuaIntersectionType? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Analysis.LuaType? other) => throw null;
        public bool Equals(Lunil.Analysis.LuaIntersectionType? other) => throw null;
        public void Deconstruct(out System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaType> Types) => throw null;
    }

    public enum LuaLiteralKind
    {
        Boolean = 0,
        Integer = 1,
        Float = 2,
        String = 3
    }

    public abstract class LuaLiteralType : Lunil.Analysis.LuaType, System.IEquatable<Lunil.Analysis.LuaLiteralType>
    {
        protected System.Type EqualityContract { get => throw null; }
        public Lunil.Analysis.LuaLiteralKind LiteralKind { get => throw null; init { } }
        protected LuaLiteralType(Lunil.Analysis.LuaLiteralKind LiteralKind) : base(default(Lunil.Analysis.LuaTypeKind)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Analysis.LuaLiteralType? left, Lunil.Analysis.LuaLiteralType? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaLiteralType? left, Lunil.Analysis.LuaLiteralType? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Analysis.LuaType? other) => throw null;
        public virtual bool Equals(Lunil.Analysis.LuaLiteralType? other) => throw null;
        protected LuaLiteralType(Lunil.Analysis.LuaLiteralType original) : base(default(Lunil.Analysis.LuaTypeKind)) { }
        public void Deconstruct(out Lunil.Analysis.LuaLiteralKind LiteralKind) => throw null;
    }

    public sealed class LuaMapType : Lunil.Analysis.LuaType, System.IEquatable<Lunil.Analysis.LuaMapType>
    {
        protected System.Type EqualityContract { get => throw null; }
        public Lunil.Analysis.LuaType KeyType { get => throw null; init { } }
        public Lunil.Analysis.LuaType ValueType { get => throw null; init { } }
        public string DisplayName { get => throw null; }
        public LuaMapType(Lunil.Analysis.LuaType KeyType, Lunil.Analysis.LuaType ValueType) : base(default(Lunil.Analysis.LuaTypeKind)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Analysis.LuaMapType? left, Lunil.Analysis.LuaMapType? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaMapType? left, Lunil.Analysis.LuaMapType? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Analysis.LuaType? other) => throw null;
        public bool Equals(Lunil.Analysis.LuaMapType? other) => throw null;
        public void Deconstruct(out Lunil.Analysis.LuaType KeyType, out Lunil.Analysis.LuaType ValueType) => throw null;
    }

    public sealed class LuaMemberTarget : System.IEquatable<Lunil.Analysis.LuaMemberTarget>
    {
        public Lunil.Core.Text.TextSpan ReceiverSpan { get => throw null; init { } }
        public string Name { get => throw null; init { } }
        public Lunil.Analysis.LuaType ReceiverType { get => throw null; init { } }
        public LuaMemberTarget(Lunil.Core.Text.TextSpan ReceiverSpan, string Name, Lunil.Analysis.LuaType ReceiverType) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaMemberTarget? left, Lunil.Analysis.LuaMemberTarget? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaMemberTarget? left, Lunil.Analysis.LuaMemberTarget? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaMemberTarget? other) => throw null;
        public void Deconstruct(out Lunil.Core.Text.TextSpan ReceiverSpan, out string Name, out Lunil.Analysis.LuaType ReceiverType) => throw null;
    }

    public sealed class LuaMetatableFact : System.IEquatable<Lunil.Analysis.LuaMetatableFact>
    {
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        public Lunil.Analysis.LuaType RawType { get => throw null; init { } }
        public Lunil.Analysis.LuaType MetatableType { get => throw null; init { } }
        public Lunil.Analysis.LuaMetatableType EffectiveType { get => throw null; init { } }
        public bool IsPrecise { get => throw null; init { } }
        public LuaMetatableFact(Lunil.Core.Text.TextSpan Span, Lunil.Analysis.LuaType RawType, Lunil.Analysis.LuaType MetatableType, Lunil.Analysis.LuaMetatableType EffectiveType, bool IsPrecise) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaMetatableFact? left, Lunil.Analysis.LuaMetatableFact? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaMetatableFact? left, Lunil.Analysis.LuaMetatableFact? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaMetatableFact? other) => throw null;
        public void Deconstruct(out Lunil.Core.Text.TextSpan Span, out Lunil.Analysis.LuaType RawType, out Lunil.Analysis.LuaType MetatableType, out Lunil.Analysis.LuaMetatableType EffectiveType, out bool IsPrecise) => throw null;
    }

    public sealed class LuaMetatableType : Lunil.Analysis.LuaType, System.IEquatable<Lunil.Analysis.LuaMetatableType>
    {
        protected System.Type EqualityContract { get => throw null; }
        public Lunil.Analysis.LuaType BaseType { get => throw null; init { } }
        public Lunil.Analysis.LuaType MetatableType { get => throw null; init { } }
        public bool IsPrecise { get => throw null; init { } }
        public string DisplayName { get => throw null; }
        public LuaMetatableType(Lunil.Analysis.LuaType BaseType, Lunil.Analysis.LuaType MetatableType, bool IsPrecise = true) : base(default(Lunil.Analysis.LuaTypeKind)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Analysis.LuaMetatableType? left, Lunil.Analysis.LuaMetatableType? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaMetatableType? left, Lunil.Analysis.LuaMetatableType? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Analysis.LuaType? other) => throw null;
        public bool Equals(Lunil.Analysis.LuaMetatableType? other) => throw null;
        public void Deconstruct(out Lunil.Analysis.LuaType BaseType, out Lunil.Analysis.LuaType MetatableType, out bool IsPrecise) => throw null;
    }

    public sealed class LuaNilPathFact : System.IEquatable<Lunil.Analysis.LuaNilPathFact>
    {
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        public string Path { get => throw null; init { } }
        public int HopCount { get => throw null; init { } }
        public Lunil.Analysis.LuaType InputType { get => throw null; init { } }
        public Lunil.Analysis.LuaType ResultType { get => throw null; init { } }
        public bool WasNarrowed { get => throw null; init { } }
        public LuaNilPathFact(Lunil.Core.Text.TextSpan Span, string Path, int HopCount, Lunil.Analysis.LuaType InputType, Lunil.Analysis.LuaType ResultType, bool WasNarrowed) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaNilPathFact? left, Lunil.Analysis.LuaNilPathFact? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaNilPathFact? left, Lunil.Analysis.LuaNilPathFact? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaNilPathFact? other) => throw null;
        public void Deconstruct(out Lunil.Core.Text.TextSpan Span, out string Path, out int HopCount, out Lunil.Analysis.LuaType InputType, out Lunil.Analysis.LuaType ResultType, out bool WasNarrowed) => throw null;
    }

    public sealed class LuaObjectModelFact : System.IEquatable<Lunil.Analysis.LuaObjectModelFact>
    {
        public string Name { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan DeclaringSpan { get => throw null; init { } }
        public Lunil.Analysis.LuaPrototypeType PrototypeType { get => throw null; init { } }
        public Lunil.Analysis.LuaMetatableType InstanceType { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaType> BaseTypes { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaPrototypeMethodFact> Methods { get => throw null; init { } }
        public bool IsPrecise { get => throw null; init { } }
        public LuaObjectModelFact(string Name, Lunil.Core.Text.TextSpan DeclaringSpan, Lunil.Analysis.LuaPrototypeType PrototypeType, Lunil.Analysis.LuaMetatableType InstanceType, System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaType> BaseTypes, System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaPrototypeMethodFact> Methods, bool IsPrecise) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaObjectModelFact? left, Lunil.Analysis.LuaObjectModelFact? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaObjectModelFact? left, Lunil.Analysis.LuaObjectModelFact? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaObjectModelFact? other) => throw null;
        public void Deconstruct(out string Name, out Lunil.Core.Text.TextSpan DeclaringSpan, out Lunil.Analysis.LuaPrototypeType PrototypeType, out Lunil.Analysis.LuaMetatableType InstanceType, out System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaType> BaseTypes, out System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaPrototypeMethodFact> Methods, out bool IsPrecise) => throw null;
    }

    public sealed class LuaOverloadType : Lunil.Analysis.LuaType, System.IEquatable<Lunil.Analysis.LuaOverloadType>
    {
        protected System.Type EqualityContract { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaFunctionType> Signatures { get => throw null; init { } }
        public string DisplayName { get => throw null; }
        public LuaOverloadType(System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaFunctionType> Signatures) : base(default(Lunil.Analysis.LuaTypeKind)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Analysis.LuaOverloadType? left, Lunil.Analysis.LuaOverloadType? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaOverloadType? left, Lunil.Analysis.LuaOverloadType? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Analysis.LuaType? other) => throw null;
        public bool Equals(Lunil.Analysis.LuaOverloadType? other) => throw null;
        public void Deconstruct(out System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaFunctionType> Signatures) => throw null;
    }

    public sealed class LuaPersistenceAccessFact : System.IEquatable<Lunil.Analysis.LuaPersistenceAccessFact>
    {
        public string FunctionPath { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        public Lunil.Analysis.LuaPersistenceOperationKind Operation { get => throw null; init { } }
        public string? Key { get => throw null; init { } }
        public bool IsDynamicKey { get => throw null; init { } }
        public string SchemaId { get => throw null; init { } }
        public int SchemaVersion { get => throw null; init { } }
        public Lunil.Analysis.LuaType ValueType { get => throw null; init { } }
        public bool MissingReturnsNil { get => throw null; init { } }
        public string? MigrationFunction { get => throw null; init { } }
        public LuaPersistenceAccessFact(string FunctionPath, Lunil.Core.Text.TextSpan Span, Lunil.Analysis.LuaPersistenceOperationKind Operation, string? Key, bool IsDynamicKey, string SchemaId, int SchemaVersion, Lunil.Analysis.LuaType ValueType, bool MissingReturnsNil, string? MigrationFunction) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaPersistenceAccessFact? left, Lunil.Analysis.LuaPersistenceAccessFact? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaPersistenceAccessFact? left, Lunil.Analysis.LuaPersistenceAccessFact? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaPersistenceAccessFact? other) => throw null;
        public void Deconstruct(out string FunctionPath, out Lunil.Core.Text.TextSpan Span, out Lunil.Analysis.LuaPersistenceOperationKind Operation, out string? Key, out bool IsDynamicKey, out string SchemaId, out int SchemaVersion, out Lunil.Analysis.LuaType ValueType, out bool MissingReturnsNil, out string? MigrationFunction) => throw null;
    }

    public enum LuaPersistenceOperationKind
    {
        Read = 0,
        Write = 1,
        Delete = 2,
        Clear = 3
    }

    public sealed class LuaPrimitiveType : Lunil.Analysis.LuaType, System.IEquatable<Lunil.Analysis.LuaPrimitiveType>
    {
        protected System.Type EqualityContract { get => throw null; }
        public Lunil.Analysis.LuaTypeKind PrimitiveKind { get => throw null; init { } }
        public string Name { get => throw null; init { } }
        public string DisplayName { get => throw null; }
        public LuaPrimitiveType(Lunil.Analysis.LuaTypeKind PrimitiveKind, string Name) : base(default(Lunil.Analysis.LuaTypeKind)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Analysis.LuaPrimitiveType? left, Lunil.Analysis.LuaPrimitiveType? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaPrimitiveType? left, Lunil.Analysis.LuaPrimitiveType? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Analysis.LuaType? other) => throw null;
        public bool Equals(Lunil.Analysis.LuaPrimitiveType? other) => throw null;
        public void Deconstruct(out Lunil.Analysis.LuaTypeKind PrimitiveKind, out string Name) => throw null;
    }

    public sealed class LuaPrototypeMethodFact : System.IEquatable<Lunil.Analysis.LuaPrototypeMethodFact>
    {
        public string Name { get => throw null; init { } }
        public Lunil.Analysis.LuaType Type { get => throw null; init { } }
        public bool HasImplicitSelf { get => throw null; init { } }
        public LuaPrototypeMethodFact(string Name, Lunil.Analysis.LuaType Type, bool HasImplicitSelf) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaPrototypeMethodFact? left, Lunil.Analysis.LuaPrototypeMethodFact? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaPrototypeMethodFact? left, Lunil.Analysis.LuaPrototypeMethodFact? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaPrototypeMethodFact? other) => throw null;
        public void Deconstruct(out string Name, out Lunil.Analysis.LuaType Type, out bool HasImplicitSelf) => throw null;
    }

    public sealed class LuaPrototypeType : Lunil.Analysis.LuaType, System.IEquatable<Lunil.Analysis.LuaPrototypeType>
    {
        protected System.Type EqualityContract { get => throw null; }
        public string Name { get => throw null; init { } }
        public Lunil.Analysis.LuaType Shape { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaType> BaseTypes { get => throw null; init { } }
        public bool UsesSelfIndex { get => throw null; init { } }
        public bool IsPrecise { get => throw null; init { } }
        public string DisplayName { get => throw null; }
        public LuaPrototypeType(string Name, Lunil.Analysis.LuaType Shape, System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaType> BaseTypes, bool UsesSelfIndex, bool IsPrecise = true) : base(default(Lunil.Analysis.LuaTypeKind)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Analysis.LuaPrototypeType? left, Lunil.Analysis.LuaPrototypeType? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaPrototypeType? left, Lunil.Analysis.LuaPrototypeType? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Analysis.LuaType? other) => throw null;
        public bool Equals(Lunil.Analysis.LuaPrototypeType? other) => throw null;
        public void Deconstruct(out string Name, out Lunil.Analysis.LuaType Shape, out System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaType> BaseTypes, out bool UsesSelfIndex, out bool IsPrecise) => throw null;
    }

    public sealed class LuaStringLiteralType : Lunil.Analysis.LuaLiteralType, System.IEquatable<Lunil.Analysis.LuaStringLiteralType>
    {
        protected System.Type EqualityContract { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<byte> Value { get => throw null; init { } }
        public string DisplayName { get => throw null; }
        public LuaStringLiteralType(System.Collections.Immutable.ImmutableArray<byte> Value) : base(default(Lunil.Analysis.LuaLiteralKind)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Analysis.LuaStringLiteralType? left, Lunil.Analysis.LuaStringLiteralType? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaStringLiteralType? left, Lunil.Analysis.LuaStringLiteralType? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Analysis.LuaLiteralType? other) => throw null;
        public bool Equals(Lunil.Analysis.LuaStringLiteralType? other) => throw null;
        public void Deconstruct(out System.Collections.Immutable.ImmutableArray<byte> Value) => throw null;
    }

    public sealed class LuaStructuralTableType : Lunil.Analysis.LuaType, System.IEquatable<Lunil.Analysis.LuaStructuralTableType>
    {
        protected System.Type EqualityContract { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaTableField> Fields { get => throw null; init { } }
        public Lunil.Analysis.LuaType? ArrayElementType { get => throw null; init { } }
        public Lunil.Analysis.LuaType? MapKeyType { get => throw null; init { } }
        public Lunil.Analysis.LuaType? MapValueType { get => throw null; init { } }
        public bool IsOpen { get => throw null; init { } }
        public string DisplayName { get => throw null; }
        public LuaStructuralTableType(System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaTableField> Fields, Lunil.Analysis.LuaType? ArrayElementType = null, Lunil.Analysis.LuaType? MapKeyType = null, Lunil.Analysis.LuaType? MapValueType = null, bool IsOpen = false) : base(default(Lunil.Analysis.LuaTypeKind)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Analysis.LuaStructuralTableType? left, Lunil.Analysis.LuaStructuralTableType? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaStructuralTableType? left, Lunil.Analysis.LuaStructuralTableType? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Analysis.LuaType? other) => throw null;
        public bool Equals(Lunil.Analysis.LuaStructuralTableType? other) => throw null;
        public void Deconstruct(out System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaTableField> Fields, out Lunil.Analysis.LuaType? ArrayElementType, out Lunil.Analysis.LuaType? MapKeyType, out Lunil.Analysis.LuaType? MapValueType, out bool IsOpen) => throw null;
    }

    public sealed class LuaSymbolTypeInfo : System.IEquatable<Lunil.Analysis.LuaSymbolTypeInfo>
    {
        public Lunil.Semantics.Binding.LuaSymbol Symbol { get => throw null; init { } }
        public Lunil.Analysis.LuaType DeclaredType { get => throw null; init { } }
        public Lunil.Analysis.LuaType InferredType { get => throw null; init { } }
        public bool IsDefinitelyAssigned { get => throw null; init { } }
        public LuaSymbolTypeInfo(Lunil.Semantics.Binding.LuaSymbol Symbol, Lunil.Analysis.LuaType DeclaredType, Lunil.Analysis.LuaType InferredType, bool IsDefinitelyAssigned) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaSymbolTypeInfo? left, Lunil.Analysis.LuaSymbolTypeInfo? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaSymbolTypeInfo? left, Lunil.Analysis.LuaSymbolTypeInfo? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaSymbolTypeInfo? other) => throw null;
        public void Deconstruct(out Lunil.Semantics.Binding.LuaSymbol Symbol, out Lunil.Analysis.LuaType DeclaredType, out Lunil.Analysis.LuaType InferredType, out bool IsDefinitelyAssigned) => throw null;
    }

    public sealed class LuaTableField : System.IEquatable<Lunil.Analysis.LuaTableField>
    {
        public string? Name { get => throw null; init { } }
        public Lunil.Analysis.LuaType? KeyType { get => throw null; init { } }
        public Lunil.Analysis.LuaType ValueType { get => throw null; init { } }
        public bool IsOptional { get => throw null; init { } }
        public bool IsReadOnly { get => throw null; init { } }
        public LuaTableField(string? Name, Lunil.Analysis.LuaType? KeyType, Lunil.Analysis.LuaType ValueType, bool IsOptional, bool IsReadOnly = false) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaTableField? left, Lunil.Analysis.LuaTableField? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaTableField? left, Lunil.Analysis.LuaTableField? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaTableField? other) => throw null;
        public void Deconstruct(out string? Name, out Lunil.Analysis.LuaType? KeyType, out Lunil.Analysis.LuaType ValueType, out bool IsOptional, out bool IsReadOnly) => throw null;
    }

    public sealed class LuaTupleType : Lunil.Analysis.LuaType, System.IEquatable<Lunil.Analysis.LuaTupleType>
    {
        protected System.Type EqualityContract { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaType> Elements { get => throw null; init { } }
        public string DisplayName { get => throw null; }
        public LuaTupleType(System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaType> Elements) : base(default(Lunil.Analysis.LuaTypeKind)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Analysis.LuaTupleType? left, Lunil.Analysis.LuaTupleType? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaTupleType? left, Lunil.Analysis.LuaTupleType? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Analysis.LuaType? other) => throw null;
        public bool Equals(Lunil.Analysis.LuaTupleType? other) => throw null;
        public void Deconstruct(out System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaType> Elements) => throw null;
    }

    public abstract class LuaType : System.IEquatable<Lunil.Analysis.LuaType>
    {
        protected System.Type EqualityContract { get => throw null; }
        public Lunil.Analysis.LuaTypeKind Kind { get => throw null; init { } }
        public string DisplayName { get => throw null; }
        protected LuaType(Lunil.Analysis.LuaTypeKind Kind) { }
        public override string ToString() => throw null;
        protected virtual bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Analysis.LuaType? left, Lunil.Analysis.LuaType? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaType? left, Lunil.Analysis.LuaType? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public virtual bool Equals(Lunil.Analysis.LuaType? other) => throw null;
        protected LuaType(Lunil.Analysis.LuaType original) { }
        public void Deconstruct(out Lunil.Analysis.LuaTypeKind Kind) => throw null;
    }

    public static class LuaTypeAnalyzer
    {
        public static Lunil.Analysis.LuaAnalysisResult Analyze(Lunil.Semantics.Binding.LuaSemanticModel semanticModel, Lunil.EmmyLua.LuaAnnotationDocument annotations, Lunil.Analysis.LuaAnalysisOptions? options = null, System.Threading.CancellationToken cancellationToken = null) => throw null;
        public static Lunil.Analysis.LuaAnalysisResult Analyze(Lunil.Semantics.Binding.LuaSemanticModel semanticModel, Lunil.EmmyLua.LuaAnnotationDocument annotations, Lunil.Analysis.LuaAnalysisEnvironment environment, Lunil.Analysis.LuaAnalysisOptions? options = null, System.Threading.CancellationToken cancellationToken = null) => throw null;
    }

    public abstract class LuaTypeDeclaration : System.IEquatable<Lunil.Analysis.LuaTypeDeclaration>
    {
        protected System.Type EqualityContract { get => throw null; }
        public string Name { get => throw null; init { } }
        public Lunil.Analysis.LuaTypeDeclarationKind Kind { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        protected LuaTypeDeclaration(string Name, Lunil.Analysis.LuaTypeDeclarationKind Kind, Lunil.Core.Text.TextSpan Span) { }
        public override string ToString() => throw null;
        protected virtual bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Analysis.LuaTypeDeclaration? left, Lunil.Analysis.LuaTypeDeclaration? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaTypeDeclaration? left, Lunil.Analysis.LuaTypeDeclaration? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public virtual bool Equals(Lunil.Analysis.LuaTypeDeclaration? other) => throw null;
        protected LuaTypeDeclaration(Lunil.Analysis.LuaTypeDeclaration original) { }
        public void Deconstruct(out string Name, out Lunil.Analysis.LuaTypeDeclarationKind Kind, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public enum LuaTypeDeclarationKind
    {
        Class = 0,
        Alias = 1,
        Enum = 2
    }

    public enum LuaTypeKind
    {
        Any = 0,
        Unknown = 1,
        Never = 2,
        Nil = 3,
        Boolean = 4,
        Integer = 5,
        Float = 6,
        Number = 7,
        String = 8,
        Table = 9,
        Function = 10,
        Thread = 11,
        Userdata = 12,
        Literal = 13,
        Union = 14,
        Intersection = 15,
        Array = 16,
        Map = 17,
        StructuralTable = 18,
        Tuple = 19,
        TypePack = 20,
        GenericParameter = 21,
        GenericInstance = 22,
        Class = 23,
        Alias = 24,
        Enum = 25,
        Overload = 26,
        Callable = 27,
        Metatable = 28,
        Prototype = 29
    }

    public sealed class LuaTypePack : Lunil.Analysis.LuaType, System.IEquatable<Lunil.Analysis.LuaTypePack>
    {
        protected System.Type EqualityContract { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaType> Head { get => throw null; init { } }
        public Lunil.Analysis.LuaType? VariadicType { get => throw null; init { } }
        public static Lunil.Analysis.LuaTypePack Empty { get => throw null; }
        public string DisplayName { get => throw null; }
        public LuaTypePack(System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaType> Head, Lunil.Analysis.LuaType? VariadicType = null) : base(default(Lunil.Analysis.LuaTypeKind)) { }
        public Lunil.Analysis.LuaType GetElementOrNil(int index) => throw null;
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Analysis.LuaTypePack? left, Lunil.Analysis.LuaTypePack? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaTypePack? left, Lunil.Analysis.LuaTypePack? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Analysis.LuaType? other) => throw null;
        public bool Equals(Lunil.Analysis.LuaTypePack? other) => throw null;
        public void Deconstruct(out System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaType> Head, out Lunil.Analysis.LuaType? VariadicType) => throw null;
    }

    public sealed class LuaTypeRelations
    {
        public LuaTypeRelations(System.Collections.Generic.IEnumerable<Lunil.Analysis.LuaTypeDeclaration>? declarations = null, int maximumUnionMemberCount = 32) { }
        public Lunil.Analysis.LuaType Union(params System.ReadOnlySpan<Lunil.Analysis.LuaType> types) => throw null;
        public Lunil.Analysis.LuaType Union(System.Collections.Generic.IEnumerable<Lunil.Analysis.LuaType> types) => throw null;
        public static Lunil.Analysis.LuaType Intersection(System.Collections.Generic.IEnumerable<Lunil.Analysis.LuaType> types) => throw null;
        public bool IsAssignable(Lunil.Analysis.LuaType source, Lunil.Analysis.LuaType target) => throw null;
        public Lunil.Analysis.LuaType Exclude(Lunil.Analysis.LuaType source, Lunil.Analysis.LuaType excluded) => throw null;
        public Lunil.Analysis.LuaType NarrowTo(Lunil.Analysis.LuaType source, Lunil.Analysis.LuaType target) => throw null;
        public Lunil.Analysis.LuaType RemoveNil(Lunil.Analysis.LuaType source) => throw null;
        public Lunil.Analysis.LuaType TruthyPart(Lunil.Analysis.LuaType source) => throw null;
        public Lunil.Analysis.LuaType FalsyPart(Lunil.Analysis.LuaType source) => throw null;
        public Lunil.Analysis.LuaType Substitute(Lunil.Analysis.LuaType type, System.Collections.Generic.IReadOnlyDictionary<string, Lunil.Analysis.LuaType> substitutions) => throw null;
        public Lunil.Analysis.LuaTableField? FindField(Lunil.Analysis.LuaType type, string name) => throw null;
    }

    public static class LuaTypes
    {
        public static Lunil.Analysis.LuaPrimitiveType Any { get => throw null; }
        public static Lunil.Analysis.LuaPrimitiveType Unknown { get => throw null; }
        public static Lunil.Analysis.LuaPrimitiveType Never { get => throw null; }
        public static Lunil.Analysis.LuaPrimitiveType Nil { get => throw null; }
        public static Lunil.Analysis.LuaPrimitiveType Boolean { get => throw null; }
        public static Lunil.Analysis.LuaPrimitiveType Integer { get => throw null; }
        public static Lunil.Analysis.LuaPrimitiveType Float { get => throw null; }
        public static Lunil.Analysis.LuaPrimitiveType Number { get => throw null; }
        public static Lunil.Analysis.LuaPrimitiveType String { get => throw null; }
        public static Lunil.Analysis.LuaPrimitiveType Table { get => throw null; }
        public static Lunil.Analysis.LuaPrimitiveType Function { get => throw null; }
        public static Lunil.Analysis.LuaPrimitiveType Thread { get => throw null; }
        public static Lunil.Analysis.LuaPrimitiveType Userdata { get => throw null; }
    }

    public sealed class LuaUnionType : Lunil.Analysis.LuaType, System.IEquatable<Lunil.Analysis.LuaUnionType>
    {
        protected System.Type EqualityContract { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaType> Types { get => throw null; init { } }
        public string DisplayName { get => throw null; }
        public LuaUnionType(System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaType> Types) : base(default(Lunil.Analysis.LuaTypeKind)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.Analysis.LuaUnionType? left, Lunil.Analysis.LuaUnionType? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaUnionType? left, Lunil.Analysis.LuaUnionType? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.Analysis.LuaType? other) => throw null;
        public bool Equals(Lunil.Analysis.LuaUnionType? other) => throw null;
        public void Deconstruct(out System.Collections.Immutable.ImmutableArray<Lunil.Analysis.LuaType> Types) => throw null;
    }

    public sealed class LuaUpvalueCellFact : System.IEquatable<Lunil.Analysis.LuaUpvalueCellFact>
    {
        public Lunil.Semantics.Binding.LuaSymbol Symbol { get => throw null; init { } }
        public Lunil.Analysis.LuaType Type { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<int> ReaderFunctionIds { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<int> WriterFunctionIds { get => throw null; init { } }
        public bool Escapes { get => throw null; init { } }
        public bool IsLoopCaptured { get => throw null; init { } }
        public LuaUpvalueCellFact(Lunil.Semantics.Binding.LuaSymbol Symbol, Lunil.Analysis.LuaType Type, System.Collections.Immutable.ImmutableArray<int> ReaderFunctionIds, System.Collections.Immutable.ImmutableArray<int> WriterFunctionIds, bool Escapes, bool IsLoopCaptured) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Analysis.LuaUpvalueCellFact? left, Lunil.Analysis.LuaUpvalueCellFact? right) => throw null;
        public static bool operator ==(Lunil.Analysis.LuaUpvalueCellFact? left, Lunil.Analysis.LuaUpvalueCellFact? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Analysis.LuaUpvalueCellFact? other) => throw null;
        public void Deconstruct(out Lunil.Semantics.Binding.LuaSymbol Symbol, out Lunil.Analysis.LuaType Type, out System.Collections.Immutable.ImmutableArray<int> ReaderFunctionIds, out System.Collections.Immutable.ImmutableArray<int> WriterFunctionIds, out bool Escapes, out bool IsLoopCaptured) => throw null;
    }
}
