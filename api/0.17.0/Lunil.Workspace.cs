// Target Frameworks: net10.0, netstandard2.1
#nullable enable

namespace Lunil.Workspace
{
    public interface ILuaModuleResolver
    {
        System.Threading.Tasks.ValueTask<Lunil.Workspace.LuaWorkspaceDocument?> ResolveAsync(Lunil.Workspace.LuaModuleResolutionRequest request, System.Threading.CancellationToken cancellationToken = null);
    }

    public sealed class LuaFileSystemModuleResolver : Lunil.Workspace.ILuaModuleResolver
    {
        public LuaFileSystemModuleResolver(Lunil.Workspace.LuaFileSystemModuleResolverOptions options) { }
        public System.Threading.Tasks.ValueTask<Lunil.Workspace.LuaWorkspaceDocument?> ResolveAsync(Lunil.Workspace.LuaModuleResolutionRequest request, System.Threading.CancellationToken cancellationToken = null) => throw null;
    }

    public sealed class LuaFileSystemModuleResolverOptions : System.IEquatable<Lunil.Workspace.LuaFileSystemModuleResolverOptions>
    {
        public System.Collections.Immutable.ImmutableArray<string> RootDirectories { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<string> PathPatterns { get => throw null; init { } }
        public long MaximumFileBytes { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Workspace.LuaFileSystemModuleResolverOptions? left, Lunil.Workspace.LuaFileSystemModuleResolverOptions? right) => throw null;
        public static bool operator ==(Lunil.Workspace.LuaFileSystemModuleResolverOptions? left, Lunil.Workspace.LuaFileSystemModuleResolverOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Workspace.LuaFileSystemModuleResolverOptions? other) => throw null;
    }

    public sealed class LuaInMemoryModuleResolver : Lunil.Workspace.ILuaModuleResolver
    {
        public LuaInMemoryModuleResolver(System.Collections.Generic.IEnumerable<Lunil.Workspace.LuaWorkspaceDocument> documents) { }
        public System.Threading.Tasks.ValueTask<Lunil.Workspace.LuaWorkspaceDocument?> ResolveAsync(Lunil.Workspace.LuaModuleResolutionRequest request, System.Threading.CancellationToken cancellationToken = null) => throw null;
    }

    public sealed class LuaModuleDependency : System.IEquatable<Lunil.Workspace.LuaModuleDependency>
    {
        public Lunil.Workspace.LuaModuleIdentity Source { get => throw null; init { } }
        public string RequestedName { get => throw null; init { } }
        public Lunil.Workspace.LuaModuleIdentity? Target { get => throw null; init { } }
        public Lunil.Workspace.LuaModuleDependencyKind Kind { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        public LuaModuleDependency(Lunil.Workspace.LuaModuleIdentity Source, string RequestedName, Lunil.Workspace.LuaModuleIdentity? Target, Lunil.Workspace.LuaModuleDependencyKind Kind, Lunil.Core.Text.TextSpan Span) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Workspace.LuaModuleDependency? left, Lunil.Workspace.LuaModuleDependency? right) => throw null;
        public static bool operator ==(Lunil.Workspace.LuaModuleDependency? left, Lunil.Workspace.LuaModuleDependency? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Workspace.LuaModuleDependency? other) => throw null;
        public void Deconstruct(out Lunil.Workspace.LuaModuleIdentity Source, out string RequestedName, out Lunil.Workspace.LuaModuleIdentity? Target, out Lunil.Workspace.LuaModuleDependencyKind Kind, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public enum LuaModuleDependencyKind
    {
        Static = 0,
        Dynamic = 1,
        Host = 2
    }

    public sealed class LuaModuleGraph : System.IEquatable<Lunil.Workspace.LuaModuleGraph>
    {
        public System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaModuleNode> Nodes { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaModuleDependency> Dependencies { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaModuleStronglyConnectedComponent> Components { get => throw null; init { } }
        public static Lunil.Workspace.LuaModuleGraph Empty { get => throw null; }
        public LuaModuleGraph(System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaModuleNode> Nodes, System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaModuleDependency> Dependencies, System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaModuleStronglyConnectedComponent> Components) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Workspace.LuaModuleGraph? left, Lunil.Workspace.LuaModuleGraph? right) => throw null;
        public static bool operator ==(Lunil.Workspace.LuaModuleGraph? left, Lunil.Workspace.LuaModuleGraph? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Workspace.LuaModuleGraph? other) => throw null;
        public void Deconstruct(out System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaModuleNode> Nodes, out System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaModuleDependency> Dependencies, out System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaModuleStronglyConnectedComponent> Components) => throw null;
    }

    public sealed class LuaModuleIdentity : System.IEquatable<Lunil.Workspace.LuaModuleIdentity>
    {
        public string Name { get => throw null; }
        public LuaModuleIdentity(string name) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Workspace.LuaModuleIdentity? left, Lunil.Workspace.LuaModuleIdentity? right) => throw null;
        public static bool operator ==(Lunil.Workspace.LuaModuleIdentity? left, Lunil.Workspace.LuaModuleIdentity? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Workspace.LuaModuleIdentity? other) => throw null;
    }

    public sealed class LuaModuleNode : System.IEquatable<Lunil.Workspace.LuaModuleNode>
    {
        public Lunil.Workspace.LuaModuleIdentity Identity { get => throw null; init { } }
        public string SourceIdentity { get => throw null; init { } }
        public string ContentHash { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaModuleDependency> Dependencies { get => throw null; init { } }
        public LuaModuleNode(Lunil.Workspace.LuaModuleIdentity Identity, string SourceIdentity, string ContentHash, System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaModuleDependency> Dependencies) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Workspace.LuaModuleNode? left, Lunil.Workspace.LuaModuleNode? right) => throw null;
        public static bool operator ==(Lunil.Workspace.LuaModuleNode? left, Lunil.Workspace.LuaModuleNode? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Workspace.LuaModuleNode? other) => throw null;
        public void Deconstruct(out Lunil.Workspace.LuaModuleIdentity Identity, out string SourceIdentity, out string ContentHash, out System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaModuleDependency> Dependencies) => throw null;
    }

    public sealed class LuaModuleResolutionRequest : System.IEquatable<Lunil.Workspace.LuaModuleResolutionRequest>
    {
        public Lunil.Workspace.LuaModuleIdentity Origin { get => throw null; init { } }
        public string RequestedName { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        public LuaModuleResolutionRequest(Lunil.Workspace.LuaModuleIdentity Origin, string RequestedName, Lunil.Core.Text.TextSpan Span) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Workspace.LuaModuleResolutionRequest? left, Lunil.Workspace.LuaModuleResolutionRequest? right) => throw null;
        public static bool operator ==(Lunil.Workspace.LuaModuleResolutionRequest? left, Lunil.Workspace.LuaModuleResolutionRequest? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Workspace.LuaModuleResolutionRequest? other) => throw null;
        public void Deconstruct(out Lunil.Workspace.LuaModuleIdentity Origin, out string RequestedName, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public sealed class LuaModuleStronglyConnectedComponent : System.IEquatable<Lunil.Workspace.LuaModuleStronglyConnectedComponent>
    {
        public int Id { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaModuleIdentity> Modules { get => throw null; init { } }
        public bool IsCyclic { get => throw null; init { } }
        public LuaModuleStronglyConnectedComponent(int Id, System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaModuleIdentity> Modules, bool IsCyclic) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Workspace.LuaModuleStronglyConnectedComponent? left, Lunil.Workspace.LuaModuleStronglyConnectedComponent? right) => throw null;
        public static bool operator ==(Lunil.Workspace.LuaModuleStronglyConnectedComponent? left, Lunil.Workspace.LuaModuleStronglyConnectedComponent? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Workspace.LuaModuleStronglyConnectedComponent? other) => throw null;
        public void Deconstruct(out int Id, out System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaModuleIdentity> Modules, out bool IsCyclic) => throw null;
    }

    public static class LuaSymbolKeyWorkspaceExtensions
    {
        public static Lunil.Semantics.Binding.LuaSymbolKey GetSymbolKey(this Lunil.Semantics.Binding.LuaSemanticModel model, Lunil.Semantics.Binding.LuaSymbol symbol, Lunil.Workspace.LuaModuleIdentity module) => throw null;
        public static Lunil.Semantics.Binding.LuaSymbolKey GetFunctionKey(this Lunil.Semantics.Binding.LuaSemanticModel model, Lunil.Semantics.Binding.LuaFunctionInfo function, Lunil.Workspace.LuaModuleIdentity module) => throw null;
        public static Lunil.Semantics.Binding.LuaSymbol? ResolveSymbolKey(this Lunil.Semantics.Binding.LuaSemanticModel model, Lunil.Semantics.Binding.LuaSymbolKey key, Lunil.Workspace.LuaModuleIdentity module) => throw null;
        public static Lunil.Semantics.Binding.LuaFunctionInfo? ResolveFunctionKey(this Lunil.Semantics.Binding.LuaSemanticModel model, Lunil.Semantics.Binding.LuaSymbolKey key, Lunil.Workspace.LuaModuleIdentity module) => throw null;
        public static Lunil.Semantics.Binding.LuaSymbolKey GetAnnotationKey(this Lunil.Compiler.LuaCompilationResult compilation, Lunil.EmmyLua.LuaAnnotationSyntax annotation, Lunil.Workspace.LuaModuleIdentity module) => throw null;
        public static Lunil.EmmyLua.LuaAnnotationSyntax? ResolveAnnotationKey(this Lunil.Compiler.LuaCompilationResult compilation, Lunil.Semantics.Binding.LuaSymbolKey key, Lunil.Workspace.LuaModuleIdentity module) => throw null;
    }

    public sealed class LuaWorkspace : System.IDisposable
    {
        public Lunil.Workspace.LuaWorkspaceOptions Options { get => throw null; }
        public LuaWorkspace(Lunil.Workspace.LuaWorkspaceOptions? options = null, Lunil.Workspace.ILuaModuleResolver? resolver = null) { }
        public System.Threading.Tasks.Task<Lunil.Workspace.LuaWorkspaceCompactSnapshot> AnalyzeCompactAsync(System.Collections.Generic.IEnumerable<Lunil.Workspace.LuaWorkspaceDocument> roots, System.Threading.CancellationToken cancellationToken = null) => throw null;
        public System.Threading.Tasks.Task<Lunil.Workspace.LuaWorkspaceResult> AnalyzeAsync(System.Collections.Generic.IEnumerable<Lunil.Workspace.LuaWorkspaceDocument> roots, System.Threading.CancellationToken cancellationToken = null) => throw null;
        public void ClearCache() { }
        public void Dispose() { }
    }

    public enum LuaWorkspaceBindingStatus
    {
        Resolved = 0,
        Dynamic = 1,
        Unresolved = 2
    }

    public sealed class LuaWorkspaceCallGraph : System.IEquatable<Lunil.Workspace.LuaWorkspaceCallGraph>
    {
        public System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceFunction> Functions { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceCallSite> Edges { get => throw null; init { } }
        public static Lunil.Workspace.LuaWorkspaceCallGraph Empty { get => throw null; }
        public LuaWorkspaceCallGraph(System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceFunction> Functions, System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceCallSite> Edges) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Workspace.LuaWorkspaceCallGraph? left, Lunil.Workspace.LuaWorkspaceCallGraph? right) => throw null;
        public static bool operator ==(Lunil.Workspace.LuaWorkspaceCallGraph? left, Lunil.Workspace.LuaWorkspaceCallGraph? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Workspace.LuaWorkspaceCallGraph? other) => throw null;
        public void Deconstruct(out System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceFunction> Functions, out System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceCallSite> Edges) => throw null;
    }

    public sealed class LuaWorkspaceCallSite : System.IEquatable<Lunil.Workspace.LuaWorkspaceCallSite>
    {
        public Lunil.Workspace.LuaModuleIdentity Module { get => throw null; init { } }
        public string SourceIdentity { get => throw null; init { } }
        public Lunil.Analysis.LuaCallSite Site { get => throw null; init { } }
        public Lunil.Semantics.Binding.LuaSymbolKey ContainingFunctionKey { get => throw null; init { } }
        public Lunil.Semantics.Binding.LuaSymbolKey? TargetFunctionKey { get => throw null; init { } }
        public Lunil.Workspace.LuaModuleIdentity? TargetModule { get => throw null; init { } }
        public string? TargetExportName { get => throw null; init { } }
        public string? TargetExportSymbolKey { get => throw null; init { } }
        public string? TargetExportFunctionKey { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<string> CandidateTargetKeys { get => throw null; init { } }
        public Lunil.Workspace.LuaWorkspaceBindingStatus WorkspaceResolutionStatus { get => throw null; init { } }
        public string? WorkspaceResolutionReason { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan? ExternalDefinitionSpan { get => throw null; init { } }
        public Lunil.Analysis.LuaHostSourceLocation? ExternalDefinition { get => throw null; init { } }
        public Lunil.Analysis.LuaHostSourceLocation? ExternalImplementation { get => throw null; init { } }
        public LuaWorkspaceCallSite(Lunil.Workspace.LuaModuleIdentity Module, string SourceIdentity, Lunil.Analysis.LuaCallSite Site, Lunil.Semantics.Binding.LuaSymbolKey ContainingFunctionKey, Lunil.Semantics.Binding.LuaSymbolKey? TargetFunctionKey, Lunil.Workspace.LuaModuleIdentity? TargetModule, string? TargetExportName) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Workspace.LuaWorkspaceCallSite? left, Lunil.Workspace.LuaWorkspaceCallSite? right) => throw null;
        public static bool operator ==(Lunil.Workspace.LuaWorkspaceCallSite? left, Lunil.Workspace.LuaWorkspaceCallSite? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Workspace.LuaWorkspaceCallSite? other) => throw null;
        public void Deconstruct(out Lunil.Workspace.LuaModuleIdentity Module, out string SourceIdentity, out Lunil.Analysis.LuaCallSite Site, out Lunil.Semantics.Binding.LuaSymbolKey ContainingFunctionKey, out Lunil.Semantics.Binding.LuaSymbolKey? TargetFunctionKey, out Lunil.Workspace.LuaModuleIdentity? TargetModule, out string? TargetExportName) => throw null;
    }

    public static class LuaWorkspaceCodeIndex
    {
        public static System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceReference> FindReferences(this Lunil.Workspace.LuaWorkspaceResult workspace, Lunil.Semantics.Binding.LuaSymbolKey key) => throw null;
        public static System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceReference> FindGlobalReferences(this Lunil.Workspace.LuaWorkspaceResult workspace, string name) => throw null;
        public static Lunil.Workspace.LuaWorkspaceCallGraph GetCallGraph(this Lunil.Workspace.LuaWorkspaceResult workspace) => throw null;
    }

    public sealed class LuaWorkspaceCompactModule : System.IEquatable<Lunil.Workspace.LuaWorkspaceCompactModule>
    {
        public Lunil.Workspace.LuaModuleIdentity Identity { get => throw null; init { } }
        public string SourceIdentity { get => throw null; init { } }
        public string ContentHash { get => throw null; init { } }
        public string ExportHash { get => throw null; init { } }
        public string ExportSymbolHash { get => throw null; init { } }
        public string FunctionSummaryHash { get => throw null; init { } }
        public string DependencySummaryHash { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceExportSymbol> ExportedSymbols { get => throw null; init { } }
        public LuaWorkspaceCompactModule(Lunil.Workspace.LuaModuleIdentity Identity, string SourceIdentity, string ContentHash, string ExportHash, string ExportSymbolHash, string FunctionSummaryHash, string DependencySummaryHash, System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceExportSymbol> ExportedSymbols) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Workspace.LuaWorkspaceCompactModule? left, Lunil.Workspace.LuaWorkspaceCompactModule? right) => throw null;
        public static bool operator ==(Lunil.Workspace.LuaWorkspaceCompactModule? left, Lunil.Workspace.LuaWorkspaceCompactModule? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Workspace.LuaWorkspaceCompactModule? other) => throw null;
        public void Deconstruct(out Lunil.Workspace.LuaModuleIdentity Identity, out string SourceIdentity, out string ContentHash, out string ExportHash, out string ExportSymbolHash, out string FunctionSummaryHash, out string DependencySummaryHash, out System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceExportSymbol> ExportedSymbols) => throw null;
    }

    public sealed class LuaWorkspaceCompactSnapshot
    {
        public System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceCompactModule> Modules { get => throw null; }
        public Lunil.Workspace.LuaModuleGraph Graph { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceDiagnostic> Diagnostics { get => throw null; }
        public Lunil.Workspace.LuaWorkspaceMetrics Metrics { get => throw null; }
        public Lunil.Workspace.LuaWorkspaceExportGraph ExportGraph { get => throw null; }
        public Lunil.Workspace.LuaWorkspaceModuleCallBindings CallBindings { get => throw null; }
        public long EstimatedResidentBytes { get => throw null; }
        public Lunil.Workspace.LuaWorkspaceCompactModule? GetModule(string name) => throw null;
        public System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceReference> FindReferences(Lunil.Semantics.Binding.LuaSymbolKey key) => throw null;
        public System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceMemberReference> FindMemberReferences(string name) => throw null;
        public System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceMemberReference> FindAnnotationReferences(string name) => throw null;
        public System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceReference> FindGlobalReferences(string name) => throw null;
        public System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceModuleCallBinding> FindCallsToExport(string targetSymbolKey) => throw null;
        public System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceExportSymbol> FindCallbackRegistrations(string hostTargetKey) => throw null;
        public System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceExportSymbol> FindPersistenceSchemas(string schemaId) => throw null;
        public System.Threading.Tasks.Task<Lunil.Workspace.LuaWorkspaceResult> MaterializeAsync(Lunil.Workspace.LuaWorkspace workspace, System.Collections.Generic.IEnumerable<Lunil.Workspace.LuaWorkspaceDocument> documents, System.Threading.CancellationToken cancellationToken = null) => throw null;
    }

    public sealed class LuaWorkspaceDiagnostic : System.IEquatable<Lunil.Workspace.LuaWorkspaceDiagnostic>
    {
        public Lunil.Workspace.LuaWorkspaceDiagnosticPhase Phase { get => throw null; init { } }
        public Lunil.Workspace.LuaModuleIdentity? Module { get => throw null; init { } }
        public string Code { get => throw null; init { } }
        public Lunil.Core.Diagnostics.DiagnosticSeverity Severity { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        public string Message { get => throw null; init { } }
        public Lunil.Compiler.LuaCompilationPhase? CompilationPhase { get => throw null; init { } }
        public LuaWorkspaceDiagnostic(Lunil.Workspace.LuaWorkspaceDiagnosticPhase Phase, Lunil.Workspace.LuaModuleIdentity? Module, string Code, Lunil.Core.Diagnostics.DiagnosticSeverity Severity, Lunil.Core.Text.TextSpan Span, string Message, Lunil.Compiler.LuaCompilationPhase? CompilationPhase = null) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Workspace.LuaWorkspaceDiagnostic? left, Lunil.Workspace.LuaWorkspaceDiagnostic? right) => throw null;
        public static bool operator ==(Lunil.Workspace.LuaWorkspaceDiagnostic? left, Lunil.Workspace.LuaWorkspaceDiagnostic? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Workspace.LuaWorkspaceDiagnostic? other) => throw null;
        public void Deconstruct(out Lunil.Workspace.LuaWorkspaceDiagnosticPhase Phase, out Lunil.Workspace.LuaModuleIdentity? Module, out string Code, out Lunil.Core.Diagnostics.DiagnosticSeverity Severity, out Lunil.Core.Text.TextSpan Span, out string Message, out Lunil.Compiler.LuaCompilationPhase? CompilationPhase) => throw null;
    }

    public enum LuaWorkspaceDiagnosticPhase
    {
        Discovery = 0,
        Resolution = 1,
        Graph = 2,
        Compilation = 3,
        FixedPoint = 4,
        Budget = 5,
        Analysis = 6
    }

    public sealed class LuaWorkspaceDocument : System.IEquatable<Lunil.Workspace.LuaWorkspaceDocument>
    {
        public Lunil.Workspace.LuaModuleIdentity Module { get => throw null; }
        public Lunil.Compiler.LuaSourceDocument Source { get => throw null; }
        public string SourceIdentity { get => throw null; }
        public LuaWorkspaceDocument(Lunil.Workspace.LuaModuleIdentity module, Lunil.Compiler.LuaSourceDocument source) { }
        public static Lunil.Workspace.LuaWorkspaceDocument FromUtf8(string moduleName, string source, string? sourceIdentity = null) => throw null;
        public static Lunil.Workspace.LuaWorkspaceDocument FromBytes(string moduleName, System.ReadOnlySpan<byte> source, string? sourceIdentity = null) => throw null;
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Workspace.LuaWorkspaceDocument? left, Lunil.Workspace.LuaWorkspaceDocument? right) => throw null;
        public static bool operator ==(Lunil.Workspace.LuaWorkspaceDocument? left, Lunil.Workspace.LuaWorkspaceDocument? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Workspace.LuaWorkspaceDocument? other) => throw null;
    }

    public sealed class LuaWorkspaceExportEdge : System.IEquatable<Lunil.Workspace.LuaWorkspaceExportEdge>
    {
        public string SourceKey { get => throw null; init { } }
        public string TargetKey { get => throw null; init { } }
        public string Kind { get => throw null; init { } }
        public LuaWorkspaceExportEdge(string SourceKey, string TargetKey, string Kind) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Workspace.LuaWorkspaceExportEdge? left, Lunil.Workspace.LuaWorkspaceExportEdge? right) => throw null;
        public static bool operator ==(Lunil.Workspace.LuaWorkspaceExportEdge? left, Lunil.Workspace.LuaWorkspaceExportEdge? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Workspace.LuaWorkspaceExportEdge? other) => throw null;
        public void Deconstruct(out string SourceKey, out string TargetKey, out string Kind) => throw null;
    }

    public sealed class LuaWorkspaceExportGraph : System.IEquatable<Lunil.Workspace.LuaWorkspaceExportGraph>
    {
        public System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceExportSymbol> Symbols { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceExportEdge> Edges { get => throw null; init { } }
        public static Lunil.Workspace.LuaWorkspaceExportGraph Empty { get => throw null; }
        public LuaWorkspaceExportGraph(System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceExportSymbol> Symbols, System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceExportEdge> Edges) { }
        public Lunil.Workspace.LuaWorkspaceExportSymbol? Find(string moduleName, string path) => throw null;
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Workspace.LuaWorkspaceExportGraph? left, Lunil.Workspace.LuaWorkspaceExportGraph? right) => throw null;
        public static bool operator ==(Lunil.Workspace.LuaWorkspaceExportGraph? left, Lunil.Workspace.LuaWorkspaceExportGraph? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Workspace.LuaWorkspaceExportGraph? other) => throw null;
        public void Deconstruct(out System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceExportSymbol> Symbols, out System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceExportEdge> Edges) => throw null;
    }

    public enum LuaWorkspaceExportKind
    {
        Module = 0,
        Value = 1,
        Field = 2,
        Function = 3,
        Class = 4,
        Alias = 5,
        Callback = 6,
        Persistence = 7,
        Dynamic = 8
    }

    public sealed class LuaWorkspaceExportSymbol : System.IEquatable<Lunil.Workspace.LuaWorkspaceExportSymbol>
    {
        public string Key { get => throw null; init { } }
        public string ModuleName { get => throw null; init { } }
        public string Path { get => throw null; init { } }
        public string Name { get => throw null; init { } }
        public Lunil.Workspace.LuaWorkspaceExportKind Kind { get => throw null; init { } }
        public Lunil.Analysis.LuaType Type { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan DefinitionSpan { get => throw null; init { } }
        public string? TargetKey { get => throw null; init { } }
        public bool IsReExport { get => throw null; init { } }
        public bool IsExternal { get => throw null; init { } }
        public bool IsDynamic { get => throw null; init { } }
        public Lunil.Analysis.LuaHostSourceLocation? ExternalSource { get => throw null; init { } }
        public string? FunctionKey { get => throw null; init { } }
        public LuaWorkspaceExportSymbol(string Key, string ModuleName, string Path, string Name, Lunil.Workspace.LuaWorkspaceExportKind Kind, Lunil.Analysis.LuaType Type, Lunil.Core.Text.TextSpan DefinitionSpan, string? TargetKey, bool IsReExport, bool IsExternal, bool IsDynamic, Lunil.Analysis.LuaHostSourceLocation? ExternalSource) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Workspace.LuaWorkspaceExportSymbol? left, Lunil.Workspace.LuaWorkspaceExportSymbol? right) => throw null;
        public static bool operator ==(Lunil.Workspace.LuaWorkspaceExportSymbol? left, Lunil.Workspace.LuaWorkspaceExportSymbol? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Workspace.LuaWorkspaceExportSymbol? other) => throw null;
        public void Deconstruct(out string Key, out string ModuleName, out string Path, out string Name, out Lunil.Workspace.LuaWorkspaceExportKind Kind, out Lunil.Analysis.LuaType Type, out Lunil.Core.Text.TextSpan DefinitionSpan, out string? TargetKey, out bool IsReExport, out bool IsExternal, out bool IsDynamic, out Lunil.Analysis.LuaHostSourceLocation? ExternalSource) => throw null;
    }

    public sealed class LuaWorkspaceFunction : System.IEquatable<Lunil.Workspace.LuaWorkspaceFunction>
    {
        public Lunil.Workspace.LuaModuleIdentity Module { get => throw null; init { } }
        public string SourceIdentity { get => throw null; init { } }
        public int FunctionId { get => throw null; init { } }
        public Lunil.Semantics.Binding.LuaSymbolKey FunctionKey { get => throw null; init { } }
        public LuaWorkspaceFunction(Lunil.Workspace.LuaModuleIdentity Module, string SourceIdentity, int FunctionId, Lunil.Semantics.Binding.LuaSymbolKey FunctionKey) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Workspace.LuaWorkspaceFunction? left, Lunil.Workspace.LuaWorkspaceFunction? right) => throw null;
        public static bool operator ==(Lunil.Workspace.LuaWorkspaceFunction? left, Lunil.Workspace.LuaWorkspaceFunction? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Workspace.LuaWorkspaceFunction? other) => throw null;
        public void Deconstruct(out Lunil.Workspace.LuaModuleIdentity Module, out string SourceIdentity, out int FunctionId, out Lunil.Semantics.Binding.LuaSymbolKey FunctionKey) => throw null;
    }

    public sealed class LuaWorkspaceMemberReference : System.IEquatable<Lunil.Workspace.LuaWorkspaceMemberReference>
    {
        public Lunil.Workspace.LuaModuleIdentity Module { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        public string Name { get => throw null; init { } }
        public LuaWorkspaceMemberReference(Lunil.Workspace.LuaModuleIdentity Module, Lunil.Core.Text.TextSpan Span, string Name) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Workspace.LuaWorkspaceMemberReference? left, Lunil.Workspace.LuaWorkspaceMemberReference? right) => throw null;
        public static bool operator ==(Lunil.Workspace.LuaWorkspaceMemberReference? left, Lunil.Workspace.LuaWorkspaceMemberReference? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Workspace.LuaWorkspaceMemberReference? other) => throw null;
        public void Deconstruct(out Lunil.Workspace.LuaModuleIdentity Module, out Lunil.Core.Text.TextSpan Span, out string Name) => throw null;
    }

    public sealed class LuaWorkspaceMetrics : System.IEquatable<Lunil.Workspace.LuaWorkspaceMetrics>
    {
        public int DiscoveredModuleCount { get => throw null; init { } }
        public int AnalyzedModuleCount { get => throw null; init { } }
        public int CacheHitCount { get => throw null; init { } }
        public int CacheMissCount { get => throw null; init { } }
        public int InvalidatedModuleCount { get => throw null; init { } }
        public int FixedPointIterationCount { get => throw null; init { } }
        public int PeakParallelism { get => throw null; init { } }
        public int DirtyFunctionCount { get => throw null; init { } }
        public int DirtyExportCount { get => throw null; init { } }
        public int DirtyHostSummaryCount { get => throw null; init { } }
        public int IndexedReferenceCount { get => throw null; init { } }
        public int IndexedCallCount { get => throw null; init { } }
        public int PendingWorkItemHighWatermark { get => throw null; init { } }
        public int CacheEvictionCount { get => throw null; init { } }
        public int ReclaimedAnalysisCount { get => throw null; init { } }
        public int DiskCacheHitCount { get => throw null; init { } }
        public long CacheResidentBytes { get => throw null; init { } }
        public long CompactResidentBytes { get => throw null; init { } }
        public LuaWorkspaceMetrics(int DiscoveredModuleCount, int AnalyzedModuleCount, int CacheHitCount, int CacheMissCount, int InvalidatedModuleCount, int FixedPointIterationCount, int PeakParallelism) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Workspace.LuaWorkspaceMetrics? left, Lunil.Workspace.LuaWorkspaceMetrics? right) => throw null;
        public static bool operator ==(Lunil.Workspace.LuaWorkspaceMetrics? left, Lunil.Workspace.LuaWorkspaceMetrics? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Workspace.LuaWorkspaceMetrics? other) => throw null;
        public void Deconstruct(out int DiscoveredModuleCount, out int AnalyzedModuleCount, out int CacheHitCount, out int CacheMissCount, out int InvalidatedModuleCount, out int FixedPointIterationCount, out int PeakParallelism) => throw null;
    }

    public sealed class LuaWorkspaceModuleCallBinding : System.IEquatable<Lunil.Workspace.LuaWorkspaceModuleCallBinding>
    {
        public string SourceModuleName { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        public int ContainingFunctionId { get => throw null; init { } }
        public string RequestedModuleName { get => throw null; init { } }
        public string MemberPath { get => throw null; init { } }
        public string? TargetSymbolKey { get => throw null; init { } }
        public string? TargetFunctionKey { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<string> CandidateKeys { get => throw null; init { } }
        public Lunil.Workspace.LuaWorkspaceBindingStatus Status { get => throw null; init { } }
        public string? Reason { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan? DefinitionSpan { get => throw null; init { } }
        public Lunil.Analysis.LuaHostSourceLocation? ExternalDefinition { get => throw null; init { } }
        public Lunil.Analysis.LuaHostSourceLocation? ExternalImplementation { get => throw null; init { } }
        public LuaWorkspaceModuleCallBinding(string SourceModuleName, Lunil.Core.Text.TextSpan Span, int ContainingFunctionId, string RequestedModuleName, string MemberPath, string? TargetSymbolKey, string? TargetFunctionKey, System.Collections.Immutable.ImmutableArray<string> CandidateKeys, Lunil.Workspace.LuaWorkspaceBindingStatus Status, string? Reason, Lunil.Core.Text.TextSpan? DefinitionSpan, Lunil.Analysis.LuaHostSourceLocation? ExternalDefinition, Lunil.Analysis.LuaHostSourceLocation? ExternalImplementation) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Workspace.LuaWorkspaceModuleCallBinding? left, Lunil.Workspace.LuaWorkspaceModuleCallBinding? right) => throw null;
        public static bool operator ==(Lunil.Workspace.LuaWorkspaceModuleCallBinding? left, Lunil.Workspace.LuaWorkspaceModuleCallBinding? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Workspace.LuaWorkspaceModuleCallBinding? other) => throw null;
        public void Deconstruct(out string SourceModuleName, out Lunil.Core.Text.TextSpan Span, out int ContainingFunctionId, out string RequestedModuleName, out string MemberPath, out string? TargetSymbolKey, out string? TargetFunctionKey, out System.Collections.Immutable.ImmutableArray<string> CandidateKeys, out Lunil.Workspace.LuaWorkspaceBindingStatus Status, out string? Reason, out Lunil.Core.Text.TextSpan? DefinitionSpan, out Lunil.Analysis.LuaHostSourceLocation? ExternalDefinition, out Lunil.Analysis.LuaHostSourceLocation? ExternalImplementation) => throw null;
    }

    public sealed class LuaWorkspaceModuleCallBindings : System.IEquatable<Lunil.Workspace.LuaWorkspaceModuleCallBindings>
    {
        public System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceModuleCallBinding> Edges { get => throw null; init { } }
        public static Lunil.Workspace.LuaWorkspaceModuleCallBindings Empty { get => throw null; }
        public LuaWorkspaceModuleCallBindings(System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceModuleCallBinding> Edges) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Workspace.LuaWorkspaceModuleCallBindings? left, Lunil.Workspace.LuaWorkspaceModuleCallBindings? right) => throw null;
        public static bool operator ==(Lunil.Workspace.LuaWorkspaceModuleCallBindings? left, Lunil.Workspace.LuaWorkspaceModuleCallBindings? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Workspace.LuaWorkspaceModuleCallBindings? other) => throw null;
        public void Deconstruct(out System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceModuleCallBinding> Edges) => throw null;
    }

    public sealed class LuaWorkspaceModuleResult : System.IEquatable<Lunil.Workspace.LuaWorkspaceModuleResult>
    {
        public Lunil.Workspace.LuaModuleIdentity Identity { get => throw null; init { } }
        public string SourceIdentity { get => throw null; init { } }
        public string ContentHash { get => throw null; init { } }
        public Lunil.Compiler.LuaCompilationResult Compilation { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaModuleDependency> Dependencies { get => throw null; init { } }
        public Lunil.Analysis.LuaType ExportedType { get => throw null; init { } }
        public string ExportHash { get => throw null; init { } }
        public int FixedPointIterationCount { get => throw null; init { } }
        public bool WasCacheHit { get => throw null; init { } }
        public bool WasWidened { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceExportSymbol> ExportedSymbols { get => throw null; init { } }
        public string ExportSymbolHash { get => throw null; init { } }
        public string FunctionSummaryHash { get => throw null; init { } }
        public string AnalysisSummaryHash { get => throw null; init { } }
        public string DependencySummaryHash { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableDictionary<string, string> ExportSummaryHashes { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableDictionary<string, string> FunctionSummaryHashes { get => throw null; init { } }
        public string HostSummaryHash { get => throw null; init { } }
        public LuaWorkspaceModuleResult(Lunil.Workspace.LuaModuleIdentity Identity, string SourceIdentity, string ContentHash, Lunil.Compiler.LuaCompilationResult Compilation, System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaModuleDependency> Dependencies, Lunil.Analysis.LuaType ExportedType, string ExportHash, int FixedPointIterationCount, bool WasCacheHit, bool WasWidened) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Workspace.LuaWorkspaceModuleResult? left, Lunil.Workspace.LuaWorkspaceModuleResult? right) => throw null;
        public static bool operator ==(Lunil.Workspace.LuaWorkspaceModuleResult? left, Lunil.Workspace.LuaWorkspaceModuleResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Workspace.LuaWorkspaceModuleResult? other) => throw null;
        public void Deconstruct(out Lunil.Workspace.LuaModuleIdentity Identity, out string SourceIdentity, out string ContentHash, out Lunil.Compiler.LuaCompilationResult Compilation, out System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaModuleDependency> Dependencies, out Lunil.Analysis.LuaType ExportedType, out string ExportHash, out int FixedPointIterationCount, out bool WasCacheHit, out bool WasWidened) => throw null;
    }

    public sealed class LuaWorkspaceOptions : System.IEquatable<Lunil.Workspace.LuaWorkspaceOptions>
    {
        public static Lunil.Workspace.LuaWorkspaceOptions Default { get => throw null; }
        public Lunil.Core.LuaLanguageVersion LanguageVersion { get => throw null; init { } }
        public Lunil.Compiler.LuaCompilerOptions Compiler { get => throw null; init { } }
        public Lunil.Analysis.LuaHostAnalysisContract? HostContract { get => throw null; init { } }
        public int MaximumModuleCount { get => throw null; init { } }
        public int MaximumDependencyCount { get => throw null; init { } }
        public long MaximumSourceBytes { get => throw null; init { } }
        public int MaximumParallelism { get => throw null; init { } }
        public int MaximumFixedPointIterations { get => throw null; init { } }
        public int MaximumCacheEntryCount { get => throw null; init { } }
        public long MaximumCacheBytes { get => throw null; init { } }
        public int MaximumPendingWorkItems { get => throw null; init { } }
        public int IndexShardCount { get => throw null; init { } }
        public string? DiskCacheDirectory { get => throw null; init { } }
        public long MaximumDiskCacheBytes { get => throw null; init { } }
        public bool RetainFullAnalysisCacheResults { get => throw null; init { } }
        public System.IProgress<Lunil.Workspace.LuaWorkspaceProgress>? Progress { get => throw null; init { } }
        public int MaximumDiagnosticCount { get => throw null; init { } }
        public Lunil.Core.Diagnostics.DiagnosticSeverity UnresolvedModuleSeverity { get => throw null; init { } }
        public Lunil.Core.Diagnostics.DiagnosticSeverity DynamicRequireSeverity { get => throw null; init { } }
        public Lunil.Core.Diagnostics.DiagnosticSeverity FixedPointSeverity { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableHashSet<string> SuppressedDiagnosticCodes { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Workspace.LuaWorkspaceOptions? left, Lunil.Workspace.LuaWorkspaceOptions? right) => throw null;
        public static bool operator ==(Lunil.Workspace.LuaWorkspaceOptions? left, Lunil.Workspace.LuaWorkspaceOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Workspace.LuaWorkspaceOptions? other) => throw null;
    }

    public sealed class LuaWorkspaceProgress : System.IEquatable<Lunil.Workspace.LuaWorkspaceProgress>
    {
        public Lunil.Workspace.LuaWorkspaceProgressPhase Phase { get => throw null; init { } }
        public int CompletedWorkItems { get => throw null; init { } }
        public int TotalWorkItems { get => throw null; init { } }
        public string? ModuleName { get => throw null; init { } }
        public LuaWorkspaceProgress(Lunil.Workspace.LuaWorkspaceProgressPhase Phase, int CompletedWorkItems, int TotalWorkItems, string? ModuleName = null) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Workspace.LuaWorkspaceProgress? left, Lunil.Workspace.LuaWorkspaceProgress? right) => throw null;
        public static bool operator ==(Lunil.Workspace.LuaWorkspaceProgress? left, Lunil.Workspace.LuaWorkspaceProgress? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Workspace.LuaWorkspaceProgress? other) => throw null;
        public void Deconstruct(out Lunil.Workspace.LuaWorkspaceProgressPhase Phase, out int CompletedWorkItems, out int TotalWorkItems, out string? ModuleName) => throw null;
    }

    public enum LuaWorkspaceProgressPhase
    {
        Discovery = 0,
        Resolution = 1,
        Analysis = 2,
        Indexing = 3,
        CacheMaintenance = 4,
        Completed = 5
    }

    public sealed class LuaWorkspaceReference : System.IEquatable<Lunil.Workspace.LuaWorkspaceReference>
    {
        public Lunil.Workspace.LuaModuleIdentity Module { get => throw null; init { } }
        public string SourceIdentity { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        public int ContainingFunctionId { get => throw null; init { } }
        public Lunil.Semantics.Binding.LuaSymbolKey ContainingFunctionKey { get => throw null; init { } }
        public string Name { get => throw null; init { } }
        public bool IsWrite { get => throw null; init { } }
        public Lunil.Semantics.Binding.LuaNameResolutionKind ResolutionKind { get => throw null; init { } }
        public Lunil.Semantics.Binding.LuaSymbolKey? TargetKey { get => throw null; init { } }
        public LuaWorkspaceReference(Lunil.Workspace.LuaModuleIdentity Module, string SourceIdentity, Lunil.Core.Text.TextSpan Span, int ContainingFunctionId, Lunil.Semantics.Binding.LuaSymbolKey ContainingFunctionKey, string Name, bool IsWrite, Lunil.Semantics.Binding.LuaNameResolutionKind ResolutionKind, Lunil.Semantics.Binding.LuaSymbolKey? TargetKey) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Workspace.LuaWorkspaceReference? left, Lunil.Workspace.LuaWorkspaceReference? right) => throw null;
        public static bool operator ==(Lunil.Workspace.LuaWorkspaceReference? left, Lunil.Workspace.LuaWorkspaceReference? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Workspace.LuaWorkspaceReference? other) => throw null;
        public void Deconstruct(out Lunil.Workspace.LuaModuleIdentity Module, out string SourceIdentity, out Lunil.Core.Text.TextSpan Span, out int ContainingFunctionId, out Lunil.Semantics.Binding.LuaSymbolKey ContainingFunctionKey, out string Name, out bool IsWrite, out Lunil.Semantics.Binding.LuaNameResolutionKind ResolutionKind, out Lunil.Semantics.Binding.LuaSymbolKey? TargetKey) => throw null;
    }

    public sealed class LuaWorkspaceResult : System.IEquatable<Lunil.Workspace.LuaWorkspaceResult>
    {
        public Lunil.Workspace.LuaModuleGraph Graph { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceModuleResult> Modules { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceDiagnostic> Diagnostics { get => throw null; init { } }
        public Lunil.Workspace.LuaWorkspaceMetrics Metrics { get => throw null; init { } }
        public Lunil.Workspace.LuaWorkspaceExportGraph ExportGraph { get => throw null; init { } }
        public Lunil.Workspace.LuaWorkspaceModuleCallBindings CallBindings { get => throw null; init { } }
        public bool Succeeded { get => throw null; }
        public LuaWorkspaceResult(Lunil.Workspace.LuaModuleGraph Graph, System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceModuleResult> Modules, System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceDiagnostic> Diagnostics, Lunil.Workspace.LuaWorkspaceMetrics Metrics) { }
        public Lunil.Workspace.LuaWorkspaceModuleResult? GetModule(string name) => throw null;
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Workspace.LuaWorkspaceResult? left, Lunil.Workspace.LuaWorkspaceResult? right) => throw null;
        public static bool operator ==(Lunil.Workspace.LuaWorkspaceResult? left, Lunil.Workspace.LuaWorkspaceResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Workspace.LuaWorkspaceResult? other) => throw null;
        public void Deconstruct(out Lunil.Workspace.LuaModuleGraph Graph, out System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceModuleResult> Modules, out System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceDiagnostic> Diagnostics, out Lunil.Workspace.LuaWorkspaceMetrics Metrics) => throw null;
    }
}
