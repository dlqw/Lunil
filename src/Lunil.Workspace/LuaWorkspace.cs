using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Lunil.Analysis;
using Lunil.Compiler;
using Lunil.Core;
using Lunil.Core.Diagnostics;
using Lunil.Semantics.Binding;

namespace Lunil.Workspace;

/// <summary>
/// Reusable, serialized workspace that resolves module graphs and preserves content-addressed
/// discovery and analysis caches across immutable snapshots.
/// </summary>
public sealed class LuaWorkspace : IDisposable
{
    private readonly LuaFrontEndSession _frontEnd;
    private readonly ILuaModuleResolver? _resolver;
    private readonly WorkspaceDiskCache? _diskCache;
    private readonly AsyncLocal<bool> _analysisOnly = new();
    private readonly AsyncLocal<LuaWorkspaceCompactSnapshot.StreamingBuilder?> _compactBuilder = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _lifetimeLock = new();
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, CacheEntry<DiscoveryEntry>> _discoveryCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CacheEntry<LuaWorkspaceModuleResult>> _analysisCache = new(StringComparer.Ordinal);
    private Dictionary<string, string> _previousModuleKeys = new(StringComparer.Ordinal);
    private Dictionary<string, ModuleSummaryState> _previousSummaries = new(StringComparer.Ordinal);
    private int _activeOperations;
    private bool _disposed;
    private bool _resourcesDisposed;

    public LuaWorkspace(
        LuaWorkspaceOptions? options = null,
        ILuaModuleResolver? resolver = null)
    {
        var configured = options ?? LuaWorkspaceOptions.Default;
        LunilGuard.NotNull(configured.Compiler);
        Options = configured with
        {
            Compiler = configured.Compiler with
            {
                LanguageVersion = configured.LanguageVersion,
                Binder = configured.Compiler.Binder with
                {
                    CollectCodeReferences = true,
                },
            },
        };
        _resolver = resolver;
        ValidateOptions(Options);
        _frontEnd = new LuaFrontEndSession(Options.Compiler);
        _diskCache = Options.DiskCacheDirectory is { } cacheDirectory
            ? new WorkspaceDiskCache(cacheDirectory, Options.MaximumDiskCacheBytes)
            : null;
    }

    public LuaWorkspaceOptions Options { get; }

    /// <summary>Analyzes sources and returns a queryable snapshot that does not retain compiler models.</summary>
    public async Task<LuaWorkspaceCompactSnapshot> AnalyzeCompactAsync(
        IEnumerable<LuaWorkspaceDocument> roots,
        CancellationToken cancellationToken = default)
    {
        var previous = _analysisOnly.Value;
        var previousBuilder = _compactBuilder.Value;
        var builder = new LuaWorkspaceCompactSnapshot.StreamingBuilder(
            Options.IndexShardCount,
            Options.HostContract);
        _analysisOnly.Value = true;
        _compactBuilder.Value = builder;
        try
        {
            _ = await AnalyzeAsync(roots, cancellationToken).ConfigureAwait(false);
            return builder.Snapshot ??
                throw new InvalidOperationException("Compact workspace construction did not complete.");
        }
        finally
        {
            _analysisOnly.Value = previous;
            _compactBuilder.Value = previousBuilder;
        }
    }

    public async Task<LuaWorkspaceResult> AnalyzeAsync(
        IEnumerable<LuaWorkspaceDocument> roots,
        CancellationToken cancellationToken = default)
    {
        LunilGuard.NotNull(roots);
        EnterOperation();
        var gateAcquired = false;
        try
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateAcquired = true;
            using var operation = new OperationMetrics(
                Options.MaximumParallelism,
                Options.MaximumPendingWorkItems);
            var compactBuilder = _compactBuilder.Value;
            var compactMode = compactBuilder is not null;
            var diagnostics = new List<LuaWorkspaceDiagnostic>();
            var documents = new Dictionary<string, LuaWorkspaceDocument>(StringComparer.Ordinal);
            var sourceOwners = new Dictionary<string, string>(StringComparer.Ordinal);
            long sourceBytes = 0;
            foreach (var document in roots
                         .OrderBy(static document => document.Module.Name, StringComparer.Ordinal)
                         .ThenBy(static document => document.SourceIdentity, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryAddDocument(
                        document,
                        documents,
                        sourceOwners,
                        ref sourceBytes,
                        diagnostics))
                {
                    continue;
                }
            }

            var discoveries = new Dictionary<string, DiscoveryEntry>(StringComparer.Ordinal);
            var dependencies = new List<LuaModuleDependency>();
            var resolvedRequests = new Dictionary<(string Origin, string Request), LuaWorkspaceDocument?>();
            var pending = new SortedSet<string>(documents.Keys, StringComparer.Ordinal);
            var discoveredCount = 0;
            var workChunkSize = GetWorkChunkSize();
            while (pending.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batchNames = pending.Take(workChunkSize).ToImmutableArray();
                foreach (var moduleName in batchNames)
                {
                    pending.Remove(moduleName);
                }
                var batch = await RunBoundedAsync(
                    batchNames,
                    async moduleName =>
                    {
                        var document = documents[moduleName];
                        return await GetDiscoveryAsync(document, cancellationToken)
                            .ConfigureAwait(false);
                    },
                    operation,
                    cancellationToken).ConfigureAwait(false);

                foreach (var discovery in batch.OrderBy(static item =>
                             item.Document.Module.Name,
                             StringComparer.Ordinal))
                {
                    discoveredCount++;
                    discoveries[discovery.Document.Module.Name] = compactMode
                        ? discovery with { FrontEndSnapshot = null }
                        : discovery;
                    foreach (var discovered in discovery.Dependencies)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (dependencies.Count >= Options.MaximumDependencyCount)
                        {
                            AddDiagnostic(
                                diagnostics,
                                LuaWorkspaceDiagnosticPhase.Budget,
                                discovery.Document.Module,
                                "LUA7004",
                                DiagnosticSeverity.Error,
                                discovered.Span,
                                $"Workspace dependency budget {Options.MaximumDependencyCount} was exhausted.");
                            break;
                        }

                        if (discovered.Kind == LuaModuleDependencyKind.Dynamic)
                        {
                            dependencies.Add(new LuaModuleDependency(
                                discovery.Document.Module,
                                discovered.RequestedName,
                                null,
                                LuaModuleDependencyKind.Dynamic,
                                discovered.Span));
                            AddDiagnostic(
                                diagnostics,
                                LuaWorkspaceDiagnosticPhase.Resolution,
                                discovery.Document.Module,
                                "LUA7003",
                                Options.DynamicRequireSeverity,
                                discovered.Span,
                                "Dynamic require cannot be resolved statically; its result is treated as any.");
                            continue;
                        }

                        if (Options.HostContract?.Modules.ContainsKey(discovered.RequestedName) == true)
                        {
                            dependencies.Add(new LuaModuleDependency(
                                discovery.Document.Module,
                                discovered.RequestedName,
                                null,
                                LuaModuleDependencyKind.Host,
                                discovered.Span));
                            continue;
                        }

                        LuaWorkspaceDocument? target;
                        try
                        {
                            target = await ResolveDependencyAsync(
                                discovery.Document.Module,
                                discovered,
                                documents,
                                resolvedRequests,
                                cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException and
                            not OutOfMemoryException and not StackOverflowException and
                            not AccessViolationException)
                        {
                            dependencies.Add(new LuaModuleDependency(
                                discovery.Document.Module,
                                discovered.RequestedName,
                                null,
                                LuaModuleDependencyKind.Static,
                                discovered.Span));
                            AddDiagnostic(
                                diagnostics,
                                LuaWorkspaceDiagnosticPhase.Resolution,
                                discovery.Document.Module,
                                "LUA7006",
                                DiagnosticSeverity.Error,
                                discovered.Span,
                                $"Resolver failed for module '{discovered.RequestedName}': " +
                                $"{exception.GetType().Name}: {exception.Message}");
                            continue;
                        }
                        if (target is null)
                        {
                            dependencies.Add(new LuaModuleDependency(
                                discovery.Document.Module,
                                discovered.RequestedName,
                                null,
                                LuaModuleDependencyKind.Static,
                                discovered.Span));
                            AddDiagnostic(
                                diagnostics,
                                LuaWorkspaceDiagnosticPhase.Resolution,
                                discovery.Document.Module,
                                "LUA7002",
                                Options.UnresolvedModuleSeverity,
                                discovered.Span,
                                $"Module '{discovered.RequestedName}' could not be resolved.");
                            continue;
                        }

                        var targetWasPresent = documents.ContainsKey(target.Module.Name);
                        var targetWasAdded = TryAddDocument(
                                target,
                                documents,
                                sourceOwners,
                                ref sourceBytes,
                                diagnostics);
                        if (!targetWasPresent && !targetWasAdded)
                        {
                            dependencies.Add(new LuaModuleDependency(
                                discovery.Document.Module,
                                discovered.RequestedName,
                                null,
                                LuaModuleDependencyKind.Static,
                                discovered.Span));
                            continue;
                        }

                        dependencies.Add(new LuaModuleDependency(
                            discovery.Document.Module,
                            discovered.RequestedName,
                            target.Module,
                            LuaModuleDependencyKind.Static,
                            discovered.Span));
                        if (targetWasAdded)
                        {
                            pending.Add(target.Module.Name);
                        }
                    }
                }

                ReportProgress(
                    LuaWorkspaceProgressPhase.Discovery,
                    discoveredCount,
                    discoveredCount + pending.Count);
            }

            var orderedDependencies = dependencies
                .OrderBy(static dependency => dependency.Source.Name, StringComparer.Ordinal)
                .ThenBy(static dependency => dependency.Span.Start)
                .ThenBy(static dependency => dependency.RequestedName, StringComparer.Ordinal)
                .ToImmutableArray();
            var dependenciesBySource = orderedDependencies
                .GroupBy(static dependency => dependency.Source.Name, StringComparer.Ordinal)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.ToImmutableArray(),
                    StringComparer.Ordinal);
            var components = GraphAlgorithms.BuildComponents(
                documents.Values.Select(static document => document.Module).ToArray(),
                orderedDependencies);
            var nodes = discoveries.Values
                .OrderBy(static discovery => discovery.Document.Module.Name, StringComparer.Ordinal)
                .Select(discovery => new LuaModuleNode(
                    discovery.Document.Module,
                    discovery.Document.SourceIdentity,
                    discovery.ContentHash,
                    dependenciesBySource.GetValueOrDefault(discovery.Document.Module.Name, [])))
                .ToImmutableArray();
            var graph = new LuaModuleGraph(nodes, orderedDependencies, components);

            var dependencyLevels = GraphAlgorithms.BuildDependencyLevels(
                components,
                orderedDependencies);
            var componentsById = components.ToDictionary(static component => component.Id);
            var componentDependencies = components.ToDictionary(
                static component => component.Id,
                component => component.Modules
                    .SelectMany(module => dependenciesBySource.GetValueOrDefault(module.Name, []))
                    .ToImmutableArray());
            var exports = new Dictionary<string, ExportValue>(StringComparer.Ordinal);
            var moduleResults = new Dictionary<string, ModuleAnalysis>(StringComparer.Ordinal);
            var currentKeys = new Dictionary<string, string>(StringComparer.Ordinal);
            var currentSummaries = new Dictionary<string, ModuleSummaryState>(StringComparer.Ordinal);
            var analyzedComponentCount = 0;
            foreach (var level in dependencyLevels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var exportSnapshot = exports.ToImmutableDictionary(StringComparer.Ordinal);
                var chunkSize = workChunkSize;
                for (var chunkStart = 0; chunkStart < level.Length; chunkStart += chunkSize)
                {
                    var chunkLength = Math.Min(chunkSize, level.Length - chunkStart);
                    var chunk = level.AsSpan(chunkStart, chunkLength).ToArray();
                    var chunkResults = await RunComponentBoundedAsync(
                        chunk,
                        componentId => AnalyzeComponentAsync(
                            componentsById[componentId],
                            discoveries,
                            componentDependencies[componentId],
                            exportSnapshot,
                            operation,
                            cancellationToken),
                        operation,
                        cancellationToken).ConfigureAwait(false);
                    foreach (var componentResult in chunkResults.OrderBy(static result => result.ComponentId))
                    {
                        analyzedComponentCount++;
                        diagnostics.AddRange(componentResult.Diagnostics);
                        operation.FixedPointIterations += componentResult.FixedPointIterations;
                        foreach (var module in componentResult.Modules.OrderBy(static module =>
                                     module.Result.Identity.Name,
                                     StringComparer.Ordinal))
                        {
                            var result = module.Result;
                            exports[result.Identity.Name] = new ExportValue(
                                result.ExportedType,
                                result.DependencySummaryHash);
                            currentKeys[result.Identity.Name] = module.CacheKey;
                            currentSummaries[result.Identity.Name] = ToSummaryState(result);
                            foreach (var diagnostic in result.Compilation.Diagnostics)
                            {
                                AddDiagnostic(
                                    diagnostics,
                                    LuaWorkspaceDiagnosticPhase.Compilation,
                                    result.Identity,
                                    diagnostic.Code,
                                    diagnostic.Severity,
                                    diagnostic.Span,
                                    diagnostic.Message,
                                    diagnostic.Phase);
                            }

                            if (compactMode)
                            {
                                compactBuilder!.AddModule(result);
                            }
                            else
                            {
                                moduleResults[result.Identity.Name] = module;
                            }
                        }
                    }

                    ReportProgress(
                        LuaWorkspaceProgressPhase.Analysis,
                        analyzedComponentCount,
                        components.Length);
                }
            }

            operation.InvalidatedModules = CountInvalidatedModules(currentKeys);
            lock (_cacheLock)
            {
                CountDirtySummaries(
                    currentSummaries,
                    out operation.DirtyExports,
                    out operation.DirtyFunctions,
                    out operation.DirtyHostSummaries);
                _previousModuleKeys = currentKeys;
                operation.ReclaimedAnalyses += RemoveReclaimedEntries(_analysisCache);
                var discoveryEntryBudget = Math.Max(1, Options.MaximumCacheEntryCount / 2);
                var discoveryByteBudget = Math.Max(1, Options.MaximumCacheBytes / 3);
                operation.CacheEvictions += PruneCache(
                    _discoveryCache,
                    discoveryEntryBudget,
                    discoveryByteBudget);
                operation.CacheEvictions += PruneCache(
                    _analysisCache,
                    Math.Max(1, Options.MaximumCacheEntryCount - discoveryEntryBudget),
                    Math.Max(1, Options.MaximumCacheBytes - discoveryByteBudget));
                operation.CacheResidentBytes = _discoveryCache.Values.Sum(static entry => entry.EstimatedBytes) +
                    _analysisCache.Values.Sum(static entry => entry.EstimatedBytes);
            }
            operation.CacheEvictions += _diskCache?.Prune() ?? 0;

            var filteredDiagnostics = FinalizeDiagnostics(diagnostics);
            var metrics = new LuaWorkspaceMetrics(
                discoveries.Count,
                currentKeys.Count,
                operation.CacheHits,
                operation.CacheMisses,
                operation.InvalidatedModules,
                operation.FixedPointIterations,
                operation.PeakParallelism)
            {
                PendingWorkItemHighWatermark = operation.PendingHighWatermark,
                CacheEvictionCount = operation.CacheEvictions,
                ReclaimedAnalysisCount = operation.ReclaimedAnalyses,
                DiskCacheHitCount = operation.DiskCacheHits,
                CacheResidentBytes = operation.CacheResidentBytes,
                DirtyExportCount = operation.DirtyExports,
                DirtyFunctionCount = operation.DirtyFunctions,
                DirtyHostSummaryCount = operation.DirtyHostSummaries,
            };
            if (compactMode)
            {
                ReportProgress(LuaWorkspaceProgressPhase.Indexing, 0, currentKeys.Count);
                compactBuilder!.Build(graph, filteredDiagnostics, metrics);
                ReportProgress(LuaWorkspaceProgressPhase.Completed, currentKeys.Count, currentKeys.Count);
                return new LuaWorkspaceResult(graph, [], filteredDiagnostics, metrics);
            }

            var results = moduleResults.Values
                .Select(static module => module.Result)
                .OrderBy(static module => module.Identity.Name, StringComparer.Ordinal)
                .ToImmutableArray();
            ReportProgress(LuaWorkspaceProgressPhase.Indexing, 0, results.Length);
            var symbolGraphs = WorkspaceSymbolGraphBuilder.Build(results, Options.HostContract);
            results = [.. results.Select(module => module with
            {
                ExportedSymbols = symbolGraphs.ModuleSymbols.GetValueOrDefault(module.Identity.Name, []),
            })];
            var workspaceResult = new LuaWorkspaceResult(
                graph,
                results,
                filteredDiagnostics,
                metrics)
            {
                ExportGraph = symbolGraphs.Exports,
                CallBindings = symbolGraphs.Calls,
            };
            ReportProgress(LuaWorkspaceProgressPhase.Completed, results.Length, results.Length);
            return workspaceResult;
        }
        finally
        {
            if (gateAcquired)
            {
                _operationGate.Release();
            }

            ExitOperation();
        }
    }

    public void ClearCache()
    {
        EnterOperation();
        var gateAcquired = false;
        try
        {
            _operationGate.Wait();
            gateAcquired = true;
            lock (_cacheLock)
            {
                _discoveryCache.Clear();
                _analysisCache.Clear();
                _previousModuleKeys.Clear();
                _previousSummaries.Clear();
            }
        }
        finally
        {
            if (gateAcquired)
            {
                _operationGate.Release();
            }

            ExitOperation();
        }
    }

    public void Dispose()
    {
        var disposeResources = false;
        lock (_lifetimeLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            disposeResources = TryClaimResourceDisposal();
        }

        if (disposeResources)
        {
            DisposeResources();
        }
    }

    private void EnterOperation()
    {
        lock (_lifetimeLock)
        {
            LunilGuard.NotDisposed(_disposed, this);
            _activeOperations++;
        }
    }

    private void ExitOperation()
    {
        var disposeResources = false;
        lock (_lifetimeLock)
        {
            _activeOperations--;
            disposeResources = TryClaimResourceDisposal();
        }

        if (disposeResources)
        {
            DisposeResources();
        }
    }

    private bool TryClaimResourceDisposal()
    {
        if (!_disposed || _activeOperations != 0 || _resourcesDisposed)
        {
            return false;
        }

        _resourcesDisposed = true;
        return true;
    }

    private void DisposeResources()
    {
        lock (_cacheLock)
        {
            _discoveryCache.Clear();
            _analysisCache.Clear();
            _previousModuleKeys.Clear();
            _previousSummaries.Clear();
        }

        _operationGate.Dispose();
    }

    private async Task<DiscoveryEntry> GetDiscoveryAsync(
        LuaWorkspaceDocument document,
        CancellationToken cancellationToken)
    {
        var contentHash = HashBytes(document.Source.Text.AsSpan());
        var key = HashText($"discovery-v1\n{document.Module.Name}\n{document.SourceIdentity}\n{contentHash}");
        lock (_cacheLock)
        {
            if (_discoveryCache.TryGetValue(key, out var cached) && cached.TryGetValue(out var value))
            {
                return value;
            }

            _discoveryCache.Remove(key);
        }

        var snapshot = await Task.Run(
            () => _frontEnd.Process(
                document.Source,
                LuaFrontEndStage.Binding,
                cancellationToken: cancellationToken),
            cancellationToken).ConfigureAwait(false);
        var entry = new DiscoveryEntry(
            document,
            contentHash,
            snapshot,
            DependencyExtractor.Extract(snapshot));
        lock (_cacheLock)
        {
            if (!_discoveryCache.TryGetValue(key, out var cached) || !cached.TryGetValue(out var value))
            {
                cached = new CacheEntry<DiscoveryEntry>(
                    entry,
                    EstimateDiscoveryBytes(entry),
                    Options.RetainFullAnalysisCacheResults);
                _discoveryCache[key] = cached;
                return entry;
            }

            return value;
        }
    }

    private async Task<LuaWorkspaceDocument?> ResolveDependencyAsync(
        LuaModuleIdentity origin,
        DiscoveredDependency dependency,
        Dictionary<string, LuaWorkspaceDocument> documents,
        Dictionary<(string Origin, string Request), LuaWorkspaceDocument?> resolvedRequests,
        CancellationToken cancellationToken)
    {
        if (documents.TryGetValue(dependency.RequestedName, out var existing))
        {
            return existing;
        }

        if (_resolver is null)
        {
            return null;
        }

        var key = (origin.Name, dependency.RequestedName);
        if (resolvedRequests.TryGetValue(key, out var resolved))
        {
            return resolved;
        }

        resolved = await _resolver.ResolveAsync(
            new LuaModuleResolutionRequest(origin, dependency.RequestedName, dependency.Span),
            cancellationToken).ConfigureAwait(false);
        resolvedRequests.Add(key, resolved);
        return resolved;
    }

    private async Task<ComponentAnalysis> AnalyzeComponentAsync(
        LuaModuleStronglyConnectedComponent component,
        IReadOnlyDictionary<string, DiscoveryEntry> discoveries,
        ImmutableArray<LuaModuleDependency> dependencies,
        ImmutableDictionary<string, ExportValue> externalExports,
        OperationMetrics operation,
        CancellationToken cancellationToken)
    {
        var componentDiscoveries = await RunBoundedAsync(
            component.Modules,
            async module => await EnsureFrontEndSnapshotAsync(
                discoveries[module.Name],
                cancellationToken).ConfigureAwait(false),
            operation,
            cancellationToken).ConfigureAwait(false);
        var discoveriesByName = componentDiscoveries.ToDictionary(
            static discovery => discovery.Document.Module.Name,
            StringComparer.Ordinal);
        var componentNames = component.Modules.Select(static module => module.Name)
            .ToImmutableHashSet(StringComparer.Ordinal);
        var currentExports = component.Modules.ToDictionary(
            static module => module.Name,
            static _ => new ExportValue(LuaTypes.Unknown, HashType(LuaTypes.Unknown)),
            StringComparer.Ordinal);
        var histories = component.Modules.ToDictionary(
            static module => module.Name,
            static _ => new List<LuaType>(),
            StringComparer.Ordinal);
        ImmutableArray<ModuleAnalysis> final = [];
        var fixedPointIterations = 0;
        var stable = !component.IsCyclic;
        var iterationLimit = component.IsCyclic ? Options.MaximumFixedPointIterations : 1;
        for (var iteration = 1; iteration <= iterationLimit; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fixedPointIterations++;
            var iterationExports = currentExports.ToImmutableDictionary(StringComparer.Ordinal);
            var analyzed = await RunBoundedAsync(
                component.Modules,
                module => AnalyzeModuleAsync(
                    discoveriesByName[module.Name],
                    dependencies.Where(dependency => dependency.Source == module).ToImmutableArray(),
                    componentNames,
                    iterationExports,
                    externalExports,
                    iteration,
                    operation,
                    cancellationToken),
                operation,
                cancellationToken).ConfigureAwait(false);
            final = analyzed.OrderBy(static module => module.Result.Identity.Name, StringComparer.Ordinal)
                .ToImmutableArray();
            var nextExports = final.ToDictionary(
                static module => module.Result.Identity.Name,
                static module => new ExportValue(
                    module.Result.ExportedType,
                    module.Result.DependencySummaryHash),
                StringComparer.Ordinal);
            foreach (var pair in nextExports)
            {
                histories[pair.Key].Add(pair.Value.Type);
            }

            if (!component.IsCyclic || currentExports.All(pair =>
                    nextExports.TryGetValue(pair.Key, out var next) &&
                    string.Equals(pair.Value.Hash, next.Hash, StringComparison.Ordinal)))
            {
                stable = true;
                break;
            }

            currentExports = nextExports;
        }

        if (stable)
        {
            return new ComponentAnalysis(component.Id, final, [], fixedPointIterations);
        }

        var relations = new LuaTypeRelations(maximumUnionMemberCount:
            Options.Compiler.Analysis.MaximumUnionMemberCount);
        var widened = final.Select(module =>
        {
            var type = relations.Union(histories[module.Result.Identity.Name]);
            var widenedResult = module.Result with
            {
                ExportedType = type,
                ExportHash = HashType(type),
                WasWidened = true,
            };
            return module with { Result = PopulateSummaryHashes(widenedResult) };
        }).ToImmutableArray();
        var diagnostic = new LuaWorkspaceDiagnostic(
            LuaWorkspaceDiagnosticPhase.FixedPoint,
            component.Modules[0],
            "LUA7005",
            Options.FixedPointSeverity,
            default,
            $"Module cycle [{string.Join(", ", component.Modules.Select(static module => module.Name))}] " +
            $"did not stabilize within {Options.MaximumFixedPointIterations} iteration(s); exports were widened.");
        return new ComponentAnalysis(component.Id, widened, [diagnostic], fixedPointIterations);
    }

    private async Task<ModuleAnalysis> AnalyzeModuleAsync(
        DiscoveryEntry discovery,
        ImmutableArray<LuaModuleDependency> dependencies,
        ImmutableHashSet<string> componentNames,
        ImmutableDictionary<string, ExportValue> internalExports,
        ImmutableDictionary<string, ExportValue> externalExports,
        int iteration,
        OperationMetrics operation,
        CancellationToken cancellationToken)
    {
        var moduleTypes = ImmutableDictionary.CreateBuilder<string, LuaType>(StringComparer.Ordinal);
        var keyBuilder = new StringBuilder()
            .Append("analysis-v1\n")
            .Append(discovery.Document.Module.Name).Append('\n')
            .Append(discovery.Document.SourceIdentity).Append('\n')
            .Append(discovery.ContentHash).Append('\n')
            .Append(_analysisOnly.Value ? "analysis-only\n" : "verified\n");
        var hostSummaryHash = string.Empty;
        if (Options.HostContract is { } hostContract)
        {
            hostSummaryHash = ComputeHostSummaryHash(hostContract, discovery, dependencies);
            keyBuilder.Append("host:").Append(hostSummaryHash).Append('\n');
        }
        foreach (var dependency in dependencies.Where(static dependency =>
                     dependency.Kind == LuaModuleDependencyKind.Static &&
                     dependency.Target is not null)
                 .OrderBy(static dependency => dependency.RequestedName, StringComparer.Ordinal)
                 .ThenBy(static dependency => dependency.Target!.Name, StringComparer.Ordinal))
        {
            var values = componentNames.Contains(dependency.Target!.Name)
                ? internalExports
                : externalExports;
            if (!values.TryGetValue(dependency.Target.Name, out var export))
            {
                continue;
            }

            moduleTypes[dependency.RequestedName] = export.Type;
            keyBuilder.Append(dependency.RequestedName).Append("=>")
                .Append(dependency.Target.Name).Append(':').Append(export.Hash).Append('\n');
        }

        if (dependencies.Any(static dependency => dependency.Kind == LuaModuleDependencyKind.Dynamic))
        {
            keyBuilder.Append("dynamic-require\n");
        }

        var cacheKey = HashText(keyBuilder.ToString());
        if (_diskCache?.TryRead(
                cacheKey,
                discovery.Document.Module.Name,
                discovery.ContentHash) == true)
        {
            Interlocked.Increment(ref operation.DiskCacheHits);
        }

        lock (_cacheLock)
        {
            if (_analysisCache.TryGetValue(cacheKey, out var cached) &&
                cached.TryGetValue(out var cachedResult))
            {
                Interlocked.Increment(ref operation.CacheHits);
                return new ModuleAnalysis(
                    cachedResult with
                    {
                        FixedPointIterationCount = iteration,
                        WasCacheHit = true,
                    },
                    cacheKey);
            }

            if (cached is not null)
            {
                _analysisCache.Remove(cacheKey);
                Interlocked.Increment(ref operation.ReclaimedAnalyses);
            }
        }

        Interlocked.Increment(ref operation.CacheMisses);
        var moduleTypeSnapshot = moduleTypes.ToImmutable();
        var analysisEnvironment = moduleTypeSnapshot.Count == 0 && Options.HostContract is null
            ? LuaAnalysisEnvironment.Empty
            : new LuaAnalysisEnvironment
            {
                ModuleTypes = moduleTypeSnapshot,
                HostContract = Options.HostContract,
            };
        var analysisOnly = _analysisOnly.Value;
        var compilation = await Task.Run(
            () => CreateCompilationResult(_frontEnd.Advance(
                discovery.FrontEndSnapshot ??
                    throw new InvalidOperationException("Binding snapshot was not materialized."),
                analysisOnly ? LuaFrontEndStage.Analysis : LuaFrontEndStage.Verification,
                analysisEnvironment,
                cancellationToken),
                analysisOnly),
            cancellationToken).ConfigureAwait(false);
        var exportedType = GetExportedType(compilation);
        var result = PopulateSummaryHashes(new LuaWorkspaceModuleResult(
            discovery.Document.Module,
            discovery.Document.SourceIdentity,
            discovery.ContentHash,
            compilation,
            dependencies,
            exportedType,
            HashType(exportedType),
            iteration,
            WasCacheHit: false,
            WasWidened: false)
        {
            HostSummaryHash = hostSummaryHash,
        });
        lock (_cacheLock)
        {
            _analysisCache[cacheKey] = new CacheEntry<LuaWorkspaceModuleResult>(
                result,
                EstimateAnalysisBytes(result),
                Options.RetainFullAnalysisCacheResults);
        }
        _diskCache?.Write(cacheKey, result);

        return new ModuleAnalysis(result, cacheKey);
    }

    private async Task<DiscoveryEntry> EnsureFrontEndSnapshotAsync(
        DiscoveryEntry discovery,
        CancellationToken cancellationToken)
    {
        if (discovery.FrontEndSnapshot is not null)
        {
            return discovery;
        }

        var snapshot = await Task.Run(
            () => _frontEnd.Process(
                discovery.Document.Source,
                LuaFrontEndStage.Binding,
                cancellationToken: cancellationToken),
            cancellationToken).ConfigureAwait(false);
        return discovery with { FrontEndSnapshot = snapshot };
    }

    private bool TryAddDocument(
        LuaWorkspaceDocument document,
        Dictionary<string, LuaWorkspaceDocument> documents,
        Dictionary<string, string> sourceOwners,
        ref long sourceBytes,
        ICollection<LuaWorkspaceDiagnostic> diagnostics)
    {
        LunilGuard.NotNull(document);
        if (documents.TryGetValue(document.Module.Name, out var existing))
        {
            if (!ReferenceEquals(existing, document) &&
                (!existing.Source.Text.AsSpan().SequenceEqual(document.Source.Text.AsSpan()) ||
                 !string.Equals(existing.SourceIdentity, document.SourceIdentity, StringComparison.Ordinal)))
            {
                AddDiagnostic(
                    diagnostics,
                    LuaWorkspaceDiagnosticPhase.Discovery,
                    document.Module,
                    "LUA7001",
                    DiagnosticSeverity.Error,
                    default,
                    $"Module identity '{document.Module.Name}' resolves to conflicting sources.");
            }

            return false;
        }

        if (documents.Count >= Options.MaximumModuleCount)
        {
            AddDiagnostic(
                diagnostics,
                LuaWorkspaceDiagnosticPhase.Budget,
                document.Module,
                "LUA7004",
                DiagnosticSeverity.Error,
                default,
                $"Workspace module budget {Options.MaximumModuleCount} was exhausted.");
            return false;
        }

        var length = document.Source.Text.Length;
        if (sourceBytes > Options.MaximumSourceBytes - length)
        {
            AddDiagnostic(
                diagnostics,
                LuaWorkspaceDiagnosticPhase.Budget,
                document.Module,
                "LUA7004",
                DiagnosticSeverity.Error,
                default,
                $"Workspace source-byte budget {Options.MaximumSourceBytes} was exhausted.");
            return false;
        }

        if (sourceOwners.TryGetValue(document.SourceIdentity, out var owner) &&
            !string.Equals(owner, document.Module.Name, StringComparison.Ordinal))
        {
            AddDiagnostic(
                diagnostics,
                LuaWorkspaceDiagnosticPhase.Discovery,
                document.Module,
                "LUA7007",
                DiagnosticSeverity.Error,
                default,
                $"Source identity '{document.SourceIdentity}' is already owned by module '{owner}'.");
            return false;
        }

        documents.Add(document.Module.Name, document);
        sourceOwners[document.SourceIdentity] = document.Module.Name;
        sourceBytes += length;
        return true;
    }

    private int CountInvalidatedModules(Dictionary<string, string> currentKeys)
    {
        lock (_cacheLock)
        {
            return _previousModuleKeys.Keys
                .Count(module => !currentKeys.TryGetValue(module, out var current) ||
                    !_previousModuleKeys.TryGetValue(module, out var previous) ||
                    !string.Equals(previous, current, StringComparison.Ordinal));
        }
    }

    private void CountDirtySummaries(
        IReadOnlyDictionary<string, ModuleSummaryState> current,
        out int dirtyExports,
        out int dirtyFunctions,
        out int dirtyHostSummaries)
    {
        dirtyExports = 0;
        dirtyFunctions = 0;
        dirtyHostSummaries = 0;
        foreach (var pair in current)
        {
            var state = pair.Value;
            if (!_previousSummaries.TryGetValue(pair.Key, out var previous))
            {
                dirtyExports += state.Exports.Count;
                dirtyFunctions += state.Functions.Count;
                dirtyHostSummaries += string.IsNullOrEmpty(state.Host) ? 0 : 1;
                continue;
            }

            dirtyExports += CountChanged(previous.Exports, state.Exports);
            dirtyFunctions += CountChanged(previous.Functions, state.Functions);
            if (!string.Equals(previous.Host, state.Host, StringComparison.Ordinal))
            {
                dirtyHostSummaries++;
            }
        }

        foreach (var removed in _previousSummaries.Where(pair => !current.ContainsKey(pair.Key)))
        {
            dirtyExports += removed.Value.Exports.Count;
            dirtyFunctions += removed.Value.Functions.Count;
            dirtyHostSummaries += string.IsNullOrEmpty(removed.Value.Host) ? 0 : 1;
        }

        _previousSummaries = new Dictionary<string, ModuleSummaryState>(current, StringComparer.Ordinal);
    }

    private static ModuleSummaryState ToSummaryState(LuaWorkspaceModuleResult module) => new(
        module.ExportSummaryHashes,
        module.FunctionSummaryHashes,
        module.HostSummaryHash);

    private static int CountChanged(
        ImmutableDictionary<string, string> previous,
        ImmutableDictionary<string, string> current) =>
        previous.Keys.Union(current.Keys, StringComparer.Ordinal).Count(key =>
            !previous.TryGetValue(key, out var oldValue) ||
            !current.TryGetValue(key, out var newValue) ||
            !string.Equals(oldValue, newValue, StringComparison.Ordinal));

    private ImmutableArray<LuaWorkspaceDiagnostic> FinalizeDiagnostics(
        IEnumerable<LuaWorkspaceDiagnostic> diagnostics)
    {
        var filtered = diagnostics
            .Where(diagnostic => !Options.SuppressedDiagnosticCodes.Contains(diagnostic.Code))
            .Distinct()
            .OrderBy(static diagnostic => diagnostic.Module?.Name ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Span.Start)
            .ThenBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .ToList();
        if (filtered.Count <= Options.MaximumDiagnosticCount)
        {
            return filtered.ToImmutableArray();
        }

        var result = filtered.Take(Options.MaximumDiagnosticCount - 1).ToList();
        result.Add(new LuaWorkspaceDiagnostic(
            LuaWorkspaceDiagnosticPhase.Budget,
            null,
            "LUA7008",
            DiagnosticSeverity.Error,
            default,
            $"Workspace diagnostic budget {Options.MaximumDiagnosticCount} was exhausted."));
        return result.ToImmutableArray();
    }

    private static LuaType GetExportedType(LuaCompilationResult compilation)
    {
        var main = compilation.Analysis.Functions.FirstOrDefault(static function =>
            function.FunctionId == 0);
        if (main is null)
        {
            return LuaTypes.Unknown;
        }

        var type = main.InferredReturns.GetElementOrNil(0);
        if (type.Kind is LuaTypeKind.Any or LuaTypeKind.Unknown or LuaTypeKind.Never)
        {
            return type;
        }

        if (type.Kind == LuaTypeKind.Nil)
        {
            return new LuaBooleanLiteralType(true);
        }

        if (type is not LuaUnionType union ||
            !union.Types.Any(static member => member.Kind == LuaTypeKind.Nil))
        {
            return type;
        }

        var relations = new LuaTypeRelations();
        return relations.Union(
            union.Types.Where(static member => member.Kind != LuaTypeKind.Nil)
                .Append<LuaType>(new LuaBooleanLiteralType(true)));
    }

    private LuaCompilationResult CreateCompilationResult(
        LuaFrontEndSnapshot snapshot,
        bool analysisOnly)
    {
        if (!analysisOnly)
        {
            return _frontEnd.ToCompilationResult(snapshot);
        }

        return new LuaCompilationResult(
            snapshot.Source,
            snapshot.Syntax,
            snapshot.Annotations,
            snapshot.SemanticModel ?? throw new InvalidOperationException("Binding did not complete."),
            snapshot.Analysis ?? throw new InvalidOperationException("Analysis did not complete."),
            Module: null,
            snapshot.Diagnostics)
        {
            FrontEndSnapshot = snapshot,
            IsAnalysisOnly = true,
        };
    }

    private static LuaWorkspaceModuleResult PopulateSummaryHashes(LuaWorkspaceModuleResult result)
    {
        var symbols = WorkspaceSymbolGraphBuilder.BuildModuleSymbols(result);
        var exportEntries = symbols.ToDictionary(
            static symbol => symbol.Key,
            static symbol => HashText(string.Join('|',
                symbol.Path,
                symbol.Kind,
                symbol.Type.DisplayName,
                symbol.FunctionKey ?? string.Empty,
                symbol.IsDynamic)),
            StringComparer.Ordinal);
        var exportSummary = string.Join('\n', exportEntries.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => pair.Key + "|" + pair.Value));
        var model = result.Compilation.SemanticModel;
        var analysesById = result.Compilation.Analysis.Functions.ToDictionary(static function =>
            function.FunctionId);
        var functionEntries = model.Functions
            .OrderBy(static function => function.Id)
            .ToDictionary(function => model.GetFunctionKey(function, result.Identity).Value, function =>
            {
                var key = model.GetFunctionKey(function, result.Identity).Value;
                var summary = analysesById.TryGetValue(function.Id, out var analysis)
                    ? string.Join('|', analysis.Type.DisplayName,
                        analysis.InferredReturns.DisplayName, analysis.WasWidened)
                    : key;
                return HashText(summary);
            }, StringComparer.Ordinal);
        var functionSummary = string.Join('\n', functionEntries
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => pair.Key + "|" + pair.Value));
        var exportHash = HashText(exportSummary);
        var functionHash = HashText(functionSummary);
        return result with
        {
            ExportedSymbols = symbols,
            ExportSymbolHash = exportHash,
            FunctionSummaryHash = functionHash,
            AnalysisSummaryHash = HashText(result.ExportHash + "\n" + exportHash + "\n" + functionHash),
            DependencySummaryHash = HashText(result.ExportHash + "\n" + exportHash),
            ExportSummaryHashes = exportEntries.ToImmutableDictionary(StringComparer.Ordinal),
            FunctionSummaryHashes = functionEntries.ToImmutableDictionary(StringComparer.Ordinal),
        };
    }

    private static string ComputeHostSummaryHash(
        LuaHostAnalysisContract contract,
        DiscoveryEntry discovery,
        ImmutableArray<LuaModuleDependency> dependencies)
    {
        var roots = discovery.FrontEndSnapshot?.SemanticModel?.References
            .Where(static reference => reference.ResolutionKind == LuaNameResolutionKind.Global)
            .Select(static reference => reference.Name)
            .ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in dependencies.Where(static dependency =>
                     dependency.Kind == LuaModuleDependencyKind.Host))
        {
            roots.Add(dependency.RequestedName);
        }

        var sourceText = Encoding.UTF8.GetString(discovery.Document.Source.Text.AsSpan());
        var explicitlyReferencedFunctions = contract.Functions.Values
            .Where(function => sourceText.Contains(function.Path, StringComparison.Ordinal))
            .Select(static function => function.Path)
            .ToHashSet(StringComparer.Ordinal);
        var hostModules = dependencies.Where(static dependency =>
                dependency.Kind == LuaModuleDependencyKind.Host)
            .Select(static dependency => dependency.RequestedName)
            .ToHashSet(StringComparer.Ordinal);
        var summary = new StringBuilder();
        foreach (var root in roots.OrderBy(static name => name, StringComparer.Ordinal))
        {
            if (contract.Globals.TryGetValue(root, out var global))
            {
                summary.Append("global|").Append(root).Append('|')
                    .Append(LuaHostAnalysisContract.ToLuaType(global).DisplayName).Append('\n');
            }

            if (contract.Modules.TryGetValue(root, out var module))
            {
                summary.Append("module|").Append(root).Append('|')
                    .Append(LuaHostAnalysisContract.ToLuaType(module).DisplayName).Append('\n');
            }

            var prefix = root + ".";
            foreach (var function in contract.Functions.Values
                         .Where(function => function.Path == root ||
                             function.Path.StartsWith(prefix, StringComparison.Ordinal))
                         .Where(function => hostModules.Contains(root) ||
                             explicitlyReferencedFunctions.Contains(function.Path))
                         .OrderBy(static function => function.Path, StringComparer.Ordinal))
            {
                AppendHostFunctionSummary(summary, function);
            }
        }

        return HashText(summary.ToString());
    }

    private static void AppendHostFunctionSummary(
        StringBuilder summary,
        LuaHostFunctionContract function)
    {
        summary.Append("function|").Append(function.Path).Append('|')
            .Append((int)function.Effects).Append('|')
            .Append(function.HasVariadicParameters).Append('|')
            .Append(function.HasVariadicReturns).Append('|');
        foreach (var parameter in function.Parameters)
        {
            summary.Append(parameter.Name).Append(':')
                .Append(LuaHostAnalysisContract.ToLuaType(parameter.Type).DisplayName)
                .Append(parameter.IsOptional ? '?' : '!').Append(',');
        }

        summary.Append("->");
        foreach (var result in function.Returns)
        {
            summary.Append(LuaHostAnalysisContract.ToLuaType(result).DisplayName).Append(',');
        }

        if (function.Callback is { } callback)
        {
            summary.Append("|callback:").Append(callback.ParameterIndex).Append(':')
                .Append((int)callback.Invocation).Append(':').Append((int)callback.Cardinality)
                .Append(':').Append((int)callback.Retention).Append(':')
                .Append(callback.UnsubscribeFunction);
        }

        if (function.Persistence is { } persistence)
        {
            summary.Append("|persistence:").Append((int)persistence.Operation).Append(':')
                .Append(persistence.SchemaId).Append(':').Append(persistence.SchemaVersion).Append(':')
                .Append(persistence.KeyParameterIndex).Append(':').Append(persistence.ValueParameterIndex)
                .Append(':').Append(LuaHostAnalysisContract.ToLuaType(persistence.ValueType).DisplayName)
                .Append(':').Append(persistence.MigrationFunction);
        }

        if (function.Source is { } source)
        {
            summary.Append("|source:").Append(source.Uri).Append(':').Append(source.Line).Append(':')
                .Append(source.Column).Append(':').Append(source.ImplementationUri);
        }

        foreach (var overload in function.Overloads)
        {
            summary.Append("|overload:");
            foreach (var parameter in overload.Parameters)
            {
                summary.Append(LuaHostAnalysisContract.ToLuaType(parameter.Type).DisplayName).Append(',');
            }

            summary.Append("->");
            foreach (var result in overload.Returns)
            {
                summary.Append(LuaHostAnalysisContract.ToLuaType(result).DisplayName).Append(',');
            }
        }

        summary.Append('\n');
    }

    private static async Task<ImmutableArray<TResult>> RunBoundedCoreAsync<TItem, TResult>(
        IEnumerable<TItem> items,
        Func<TItem, Task<TResult>> action,
        OperationMetrics operation,
        CancellationToken cancellationToken)
    {
        var itemArray = items.ToArray();
        if (itemArray.Length == 0)
        {
            return [];
        }

        operation.ObservePending(itemArray.Length);
        var results = new TResult[itemArray.Length];
        var nextIndex = -1;
        var workerCount = Math.Min(itemArray.Length, operation.MaximumParallelism);
        var workers = new Task[workerCount];
        for (var workerIndex = 0; workerIndex < workers.Length; workerIndex++)
        {
            workers[workerIndex] = Task.Run(async () =>
            {
                while (true)
                {
                    var index = Interlocked.Increment(ref nextIndex);
                    if (index >= itemArray.Length)
                    {
                        return;
                    }

                    await operation.Scheduler.WaitAsync(cancellationToken).ConfigureAwait(false);
                    operation.EnterWorker();
                    try
                    {
                        results[index] = await action(itemArray[index]).ConfigureAwait(false);
                    }
                    finally
                    {
                        operation.ExitWorker();
                        operation.Scheduler.Release();
                    }
                }
            }, cancellationToken);
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
        return [.. results];
    }

    private static Task<ImmutableArray<TResult>> RunBoundedAsync<TItem, TResult>(
        IEnumerable<TItem> items,
        Func<TItem, Task<TResult>> action,
        OperationMetrics operation,
        CancellationToken cancellationToken) =>
        RunBoundedCoreAsync(items, action, operation, cancellationToken);

    private static async Task<ImmutableArray<TResult>> RunComponentBoundedAsync<TItem, TResult>(
        IEnumerable<TItem> items,
        Func<TItem, Task<TResult>> action,
        OperationMetrics operation,
        CancellationToken cancellationToken)
    {
        var itemArray = items.ToArray();
        if (itemArray.Length == 0)
        {
            return [];
        }

        operation.ObservePending(itemArray.Length);
        var results = new TResult[itemArray.Length];
        var nextIndex = -1;
        var workerCount = Math.Min(itemArray.Length, operation.MaximumParallelism);
        var workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(async () =>
        {
            while (true)
            {
                var index = Interlocked.Increment(ref nextIndex);
                if (index >= itemArray.Length)
                {
                    return;
                }

                results[index] = await action(itemArray[index]).ConfigureAwait(false);
            }
        }, cancellationToken)).ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
        return [.. results];
    }

    private static int PruneCache<T>(
        Dictionary<string, CacheEntry<T>> cache,
        int maximumEntries,
        long maximumBytes)
        where T : class
    {
        var residentBytes = cache.Values.Sum(static entry => entry.EstimatedBytes);
        if (cache.Count <= maximumEntries && residentBytes <= maximumBytes)
        {
            return 0;
        }

        var removed = 0;
        foreach (var pair in cache.OrderBy(static pair => pair.Value.LastAccess).ToArray())
        {
            if (cache.Count <= maximumEntries && residentBytes <= maximumBytes)
            {
                break;
            }

            if (cache.Remove(pair.Key))
            {
                residentBytes -= pair.Value.EstimatedBytes;
                removed++;
            }
        }

        return removed;
    }

    private static int RemoveReclaimedEntries<T>(Dictionary<string, CacheEntry<T>> cache)
        where T : class
    {
        var removed = 0;
        foreach (var pair in cache.ToArray())
        {
            if (!pair.Value.TryGetValue(out _) && cache.Remove(pair.Key))
            {
                removed++;
            }
        }

        return removed;
    }

    private static void AddDiagnostic(
        ICollection<LuaWorkspaceDiagnostic> diagnostics,
        LuaWorkspaceDiagnosticPhase phase,
        LuaModuleIdentity? module,
        string code,
        DiagnosticSeverity severity,
        Lunil.Core.Text.TextSpan span,
        string message,
        LuaCompilationPhase? compilationPhase = null) =>
        diagnostics.Add(new LuaWorkspaceDiagnostic(
            phase,
            module,
            code,
            severity,
            span,
            message,
            compilationPhase));

    private void ReportProgress(
        LuaWorkspaceProgressPhase phase,
        int completed,
        int total,
        string? moduleName = null) =>
        Options.Progress?.Report(new LuaWorkspaceProgress(phase, completed, total, moduleName));

    private int GetWorkChunkSize() => Math.Min(
        Options.MaximumPendingWorkItems,
        Math.Max(64, Options.MaximumParallelism * 4));

    private static string HashType(LuaType type) => HashText(type.DisplayName);

    private static string HashText(string value) =>
        LunilCryptography.Sha256Hex(Encoding.UTF8.GetBytes(value)).ToLowerInvariant();

    private static string HashBytes(ReadOnlySpan<byte> value) =>
        LunilCryptography.Sha256Hex(value).ToLowerInvariant();

    private static long EstimateDiscoveryBytes(DiscoveryEntry entry) => checked(
        512L + entry.Document.Source.Text.Length * 8L + entry.Dependencies.Length * 96L);

    private static long EstimateAnalysisBytes(LuaWorkspaceModuleResult result) => checked(
        2_048L +
        result.Compilation.Source.Text.Length * 12L +
        result.Compilation.SemanticModel.Symbols.Length * 128L +
        result.Compilation.SemanticModel.UnifiedReferences.Length * 96L +
        result.Compilation.Analysis.Functions.Length * 512L);

    private static void ValidateOptions(LuaWorkspaceOptions options)
    {
        LunilGuard.NotNull(options.Compiler);
        LunilGuard.NotNull(options.SuppressedDiagnosticCodes);
        if (!LuaLanguageVersions.IsKnown(options.LanguageVersion))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.LanguageVersion,
                "The workspace language version is invalid.");
        }
        LunilGuard.Positive(options.MaximumModuleCount);
        LunilGuard.Positive(options.MaximumDependencyCount);
        LunilGuard.Positive(options.MaximumSourceBytes);
        LunilGuard.Positive(options.MaximumParallelism);
        LunilGuard.Positive(options.MaximumFixedPointIterations);
        LunilGuard.Positive(options.MaximumCacheEntryCount);
        LunilGuard.Positive(options.MaximumCacheBytes);
        LunilGuard.Positive(options.MaximumPendingWorkItems);
        LunilGuard.Positive(options.IndexShardCount);
        LunilGuard.Positive(options.MaximumDiskCacheBytes);
        if (options.IndexShardCount > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Index shard count cannot exceed 4096.");
        }

        if (options.DiskCacheDirectory is { Length: 0 })
        {
            throw new ArgumentException("Disk cache directory cannot be empty.", nameof(options));
        }
        LunilGuard.GreaterThanOrEqual(options.MaximumDiagnosticCount, 2);
        if (!LunilEnum.IsDefined(options.UnresolvedModuleSeverity) ||
            !LunilEnum.IsDefined(options.DynamicRequireSeverity) ||
            !LunilEnum.IsDefined(options.FixedPointSeverity))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "A workspace severity is invalid.");
        }
    }

    private sealed record DiscoveryEntry(
        LuaWorkspaceDocument Document,
        string ContentHash,
        LuaFrontEndSnapshot? FrontEndSnapshot,
        ImmutableArray<DiscoveredDependency> Dependencies);

    private sealed record ExportValue(LuaType Type, string Hash);

    private sealed record ModuleSummaryState(
        ImmutableDictionary<string, string> Exports,
        ImmutableDictionary<string, string> Functions,
        string Host);

    private sealed record ModuleAnalysis(LuaWorkspaceModuleResult Result, string CacheKey);

    private sealed record ComponentAnalysis(
        int ComponentId,
        ImmutableArray<ModuleAnalysis> Modules,
        ImmutableArray<LuaWorkspaceDiagnostic> Diagnostics,
        int FixedPointIterations);

    private sealed class OperationMetrics : IDisposable
    {
        private int _activeWorkers;
        private int _peakParallelism;
        private int _pendingHighWatermark;
        private readonly int _maximumPendingWorkItems;

        public OperationMetrics(int maximumParallelism, int maximumPendingWorkItems)
        {
            MaximumParallelism = maximumParallelism;
            _maximumPendingWorkItems = maximumPendingWorkItems;
            Scheduler = new SemaphoreSlim(maximumParallelism, maximumParallelism);
        }

        public int MaximumParallelism { get; }

        public SemaphoreSlim Scheduler { get; }

        public int CacheHits;

        public int CacheMisses;

        public int InvalidatedModules;

        public int FixedPointIterations;

        public int CacheEvictions;

        public int ReclaimedAnalyses;

        public int DiskCacheHits;

        public int DirtyExports;

        public int DirtyFunctions;

        public int DirtyHostSummaries;

        public long CacheResidentBytes;

        public int PeakParallelism => Volatile.Read(ref _peakParallelism);

        public int PendingHighWatermark => Volatile.Read(ref _pendingHighWatermark);

        public void Dispose() => Scheduler.Dispose();

        public void EnterWorker()
        {
            var active = Interlocked.Increment(ref _activeWorkers);
            int observed;
            do
            {
                observed = Volatile.Read(ref _peakParallelism);
                if (observed >= active)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _peakParallelism, active, observed) != observed);
        }

        public void ExitWorker() => Interlocked.Decrement(ref _activeWorkers);

        public void ObservePending(int count)
        {
            var bounded = Math.Min(count, _maximumPendingWorkItems);
            int observed;
            do
            {
                observed = Volatile.Read(ref _pendingHighWatermark);
                if (observed >= bounded)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _pendingHighWatermark, bounded, observed) != observed);
        }
    }

    private sealed class CacheEntry<T>
        where T : class
    {
        private readonly WeakReference<T> _weak;
        private readonly T? _strong;
        private long _lastAccess;

        public CacheEntry(T value, long estimatedBytes, bool retainStrongly)
        {
            _weak = new WeakReference<T>(value);
            _strong = retainStrongly ? value : null;
            EstimatedBytes = estimatedBytes;
            _lastAccess = Stopwatch.GetTimestamp();
        }

        public long EstimatedBytes { get; }

        public long LastAccess => Interlocked.Read(ref _lastAccess);

        public bool TryGetValue(out T value)
        {
            if (_strong is not null)
            {
                value = _strong;
                Interlocked.Exchange(ref _lastAccess, Stopwatch.GetTimestamp());
                return true;
            }

            if (_weak.TryGetTarget(out value!))
            {
                Interlocked.Exchange(ref _lastAccess, Stopwatch.GetTimestamp());
                return true;
            }

            value = null!;
            return false;
        }
    }
}
