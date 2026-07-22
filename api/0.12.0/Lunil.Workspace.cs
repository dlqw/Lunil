// Target Frameworks: net10.0
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
        Dynamic = 1
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
        public System.Threading.Tasks.Task<Lunil.Workspace.LuaWorkspaceResult> AnalyzeAsync(System.Collections.Generic.IEnumerable<Lunil.Workspace.LuaWorkspaceDocument> roots, System.Threading.CancellationToken cancellationToken = null) => throw null;
        public void ClearCache() { }
        public void Dispose() { }
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
        Budget = 5
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

    public sealed class LuaWorkspaceMetrics : System.IEquatable<Lunil.Workspace.LuaWorkspaceMetrics>
    {
        public int DiscoveredModuleCount { get => throw null; init { } }
        public int AnalyzedModuleCount { get => throw null; init { } }
        public int CacheHitCount { get => throw null; init { } }
        public int CacheMissCount { get => throw null; init { } }
        public int InvalidatedModuleCount { get => throw null; init { } }
        public int FixedPointIterationCount { get => throw null; init { } }
        public int PeakParallelism { get => throw null; init { } }
        public LuaWorkspaceMetrics(int DiscoveredModuleCount, int AnalyzedModuleCount, int CacheHitCount, int CacheMissCount, int InvalidatedModuleCount, int FixedPointIterationCount, int PeakParallelism) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Workspace.LuaWorkspaceMetrics? left, Lunil.Workspace.LuaWorkspaceMetrics? right) => throw null;
        public static bool operator ==(Lunil.Workspace.LuaWorkspaceMetrics? left, Lunil.Workspace.LuaWorkspaceMetrics? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Workspace.LuaWorkspaceMetrics? other) => throw null;
        public void Deconstruct(out int DiscoveredModuleCount, out int AnalyzedModuleCount, out int CacheHitCount, out int CacheMissCount, out int InvalidatedModuleCount, out int FixedPointIterationCount, out int PeakParallelism) => throw null;
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
        public int MaximumModuleCount { get => throw null; init { } }
        public int MaximumDependencyCount { get => throw null; init { } }
        public long MaximumSourceBytes { get => throw null; init { } }
        public int MaximumParallelism { get => throw null; init { } }
        public int MaximumFixedPointIterations { get => throw null; init { } }
        public int MaximumCacheEntryCount { get => throw null; init { } }
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

    public sealed class LuaWorkspaceResult : System.IEquatable<Lunil.Workspace.LuaWorkspaceResult>
    {
        public Lunil.Workspace.LuaModuleGraph Graph { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceModuleResult> Modules { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Workspace.LuaWorkspaceDiagnostic> Diagnostics { get => throw null; init { } }
        public Lunil.Workspace.LuaWorkspaceMetrics Metrics { get => throw null; init { } }
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
