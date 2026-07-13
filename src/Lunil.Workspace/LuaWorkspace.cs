using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Lunil.Analysis;
using Lunil.Compiler;
using Lunil.Core.Diagnostics;

namespace Lunil.Workspace;

/// <summary>
/// Reusable, serialized workspace that resolves module graphs and preserves content-addressed
/// discovery and analysis caches across immutable snapshots.
/// </summary>
public sealed class LuaWorkspace : IDisposable
{
    private readonly LuaCompiler _compiler;
    private readonly ILuaModuleResolver? _resolver;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _lifetimeLock = new();
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, DiscoveryEntry> _discoveryCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AnalysisCacheEntry> _analysisCache = new(StringComparer.Ordinal);
    private Dictionary<string, string> _previousModuleKeys = new(StringComparer.Ordinal);
    private int _activeOperations;
    private bool _disposed;
    private bool _resourcesDisposed;

    public LuaWorkspace(
        LuaWorkspaceOptions? options = null,
        ILuaModuleResolver? resolver = null)
    {
        Options = options ?? LuaWorkspaceOptions.Default;
        _resolver = resolver;
        ValidateOptions(Options);
        _compiler = new LuaCompiler(Options.Compiler);
    }

    public LuaWorkspaceOptions Options { get; }

    public async Task<LuaWorkspaceResult> AnalyzeAsync(
        IEnumerable<LuaWorkspaceDocument> roots,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        EnterOperation();
        var gateAcquired = false;
        try
        {
            await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateAcquired = true;
            using var operation = new OperationMetrics(Options.MaximumParallelism);
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
            while (pending.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batchNames = pending.ToImmutableArray();
                pending.Clear();
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
                    discoveries[discovery.Document.Module.Name] = discovery;
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
            }

            var orderedDependencies = dependencies
                .OrderBy(static dependency => dependency.Source.Name, StringComparer.Ordinal)
                .ThenBy(static dependency => dependency.Span.Start)
                .ThenBy(static dependency => dependency.RequestedName, StringComparer.Ordinal)
                .ToImmutableArray();
            var components = GraphAlgorithms.BuildComponents(
                documents.Values.Select(static document => document.Module).ToArray(),
                orderedDependencies);
            var nodes = discoveries.Values
                .OrderBy(static discovery => discovery.Document.Module.Name, StringComparer.Ordinal)
                .Select(discovery => new LuaModuleNode(
                    discovery.Document.Module,
                    discovery.Document.SourceIdentity,
                    discovery.ContentHash,
                    [.. orderedDependencies.Where(dependency =>
                        dependency.Source == discovery.Document.Module)]))
                .ToImmutableArray();
            var graph = new LuaModuleGraph(nodes, orderedDependencies, components);

            var dependencyLevels = GraphAlgorithms.BuildDependencyLevels(
                components,
                orderedDependencies);
            var exports = new Dictionary<string, ExportValue>(StringComparer.Ordinal);
            var moduleResults = new Dictionary<string, ModuleAnalysis>(StringComparer.Ordinal);
            foreach (var level in dependencyLevels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var exportSnapshot = exports.ToImmutableDictionary(StringComparer.Ordinal);
                var levelResults = await Task.WhenAll(level.Select(componentId =>
                    AnalyzeComponentAsync(
                        components.Single(component => component.Id == componentId),
                        discoveries,
                        orderedDependencies,
                        exportSnapshot,
                        operation,
                        cancellationToken))).ConfigureAwait(false);
                foreach (var componentResult in levelResults.OrderBy(static result => result.ComponentId))
                {
                    diagnostics.AddRange(componentResult.Diagnostics);
                    operation.FixedPointIterations += componentResult.FixedPointIterations;
                    foreach (var module in componentResult.Modules.OrderBy(static module =>
                                 module.Result.Identity.Name,
                                 StringComparer.Ordinal))
                    {
                        moduleResults[module.Result.Identity.Name] = module;
                        exports[module.Result.Identity.Name] = new ExportValue(
                            module.Result.ExportedType,
                            module.Result.ExportHash);
                    }
                }
            }

            foreach (var module in moduleResults.Values)
            {
                foreach (var diagnostic in module.Result.Compilation.Diagnostics)
                {
                    AddDiagnostic(
                        diagnostics,
                        LuaWorkspaceDiagnosticPhase.Compilation,
                        module.Result.Identity,
                        diagnostic.Code,
                        diagnostic.Severity,
                        diagnostic.Span,
                        diagnostic.Message,
                        diagnostic.Phase);
                }
            }

            var currentKeys = moduleResults.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value.CacheKey,
                StringComparer.Ordinal);
            operation.InvalidatedModules = CountInvalidatedModules(currentKeys);
            lock (_cacheLock)
            {
                _previousModuleKeys = currentKeys;
                PruneCache(_discoveryCache);
                PruneCache(_analysisCache);
            }

            var filteredDiagnostics = FinalizeDiagnostics(diagnostics);
            var results = moduleResults.Values
                .Select(static module => module.Result)
                .OrderBy(static module => module.Identity.Name, StringComparer.Ordinal)
                .ToImmutableArray();
            return new LuaWorkspaceResult(
                graph,
                results,
                filteredDiagnostics,
                new LuaWorkspaceMetrics(
                    discoveries.Count,
                    results.Length,
                    operation.CacheHits,
                    operation.CacheMisses,
                    operation.InvalidatedModules,
                    operation.FixedPointIterations,
                    operation.PeakParallelism));
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
            ObjectDisposedException.ThrowIf(_disposed, this);
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
            if (_discoveryCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var compilation = await Task.Run(
            () => _compiler.Compile(document.Source, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        var entry = new DiscoveryEntry(
            document,
            contentHash,
            compilation,
            DependencyExtractor.Extract(compilation));
        lock (_cacheLock)
        {
            _discoveryCache.TryAdd(key, entry);
            return _discoveryCache[key];
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
                    discoveries[module.Name],
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
                    module.Result.ExportHash),
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
            return module with
            {
                Result = module.Result with
                {
                    ExportedType = type,
                    ExportHash = HashType(type),
                    WasWidened = true,
                },
            };
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
            .Append(discovery.ContentHash).Append('\n');
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
        lock (_cacheLock)
        {
            if (_analysisCache.TryGetValue(cacheKey, out var cached))
            {
                Interlocked.Increment(ref operation.CacheHits);
                return new ModuleAnalysis(
                    cached.Result with
                    {
                        FixedPointIterationCount = iteration,
                        WasCacheHit = true,
                    },
                    cacheKey);
            }
        }

        Interlocked.Increment(ref operation.CacheMisses);
        var environment = new LuaAnalysisEnvironment { ModuleTypes = moduleTypes.ToImmutable() };
        var compilation = await Task.Run(
            () => _compiler.Compile(discovery.Document.Source, environment, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        var exportedType = GetExportedType(compilation);
        var result = new LuaWorkspaceModuleResult(
            discovery.Document.Module,
            discovery.Document.SourceIdentity,
            discovery.ContentHash,
            compilation,
            dependencies,
            exportedType,
            HashType(exportedType),
            iteration,
            WasCacheHit: false,
            WasWidened: false);
        lock (_cacheLock)
        {
            _analysisCache.TryAdd(cacheKey, new AnalysisCacheEntry(result));
        }

        return new ModuleAnalysis(result, cacheKey);
    }

    private bool TryAddDocument(
        LuaWorkspaceDocument document,
        Dictionary<string, LuaWorkspaceDocument> documents,
        Dictionary<string, string> sourceOwners,
        ref long sourceBytes,
        ICollection<LuaWorkspaceDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(document);
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

        var tasks = itemArray.Select(async item =>
        {
            await operation.Scheduler.WaitAsync(cancellationToken).ConfigureAwait(false);
            operation.EnterWorker();
            try
            {
                return await action(item).ConfigureAwait(false);
            }
            finally
            {
                operation.ExitWorker();
                operation.Scheduler.Release();
            }
        });
        return [.. await Task.WhenAll(tasks).ConfigureAwait(false)];
    }

    private static Task<ImmutableArray<TResult>> RunBoundedAsync<TItem, TResult>(
        IEnumerable<TItem> items,
        Func<TItem, Task<TResult>> action,
        OperationMetrics operation,
        CancellationToken cancellationToken) =>
        RunBoundedCoreAsync(items, action, operation, cancellationToken);

    private void PruneCache<T>(Dictionary<string, T> cache)
    {
        if (cache.Count <= Options.MaximumCacheEntryCount)
        {
            return;
        }

        foreach (var key in cache.Keys.OrderByDescending(static key => key, StringComparer.Ordinal)
                     .Take(cache.Count - Options.MaximumCacheEntryCount)
                     .ToArray())
        {
            cache.Remove(key);
        }
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

    private static string HashType(LuaType type) => HashText(type.DisplayName);

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string HashBytes(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static void ValidateOptions(LuaWorkspaceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options.Compiler);
        ArgumentNullException.ThrowIfNull(options.SuppressedDiagnosticCodes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumModuleCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumDependencyCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumSourceBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumParallelism);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumFixedPointIterations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumCacheEntryCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumDiagnosticCount, 2);
        if (!Enum.IsDefined(options.UnresolvedModuleSeverity) ||
            !Enum.IsDefined(options.DynamicRequireSeverity) ||
            !Enum.IsDefined(options.FixedPointSeverity))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "A workspace severity is invalid.");
        }
    }

    private sealed record DiscoveryEntry(
        LuaWorkspaceDocument Document,
        string ContentHash,
        LuaCompilationResult Compilation,
        ImmutableArray<DiscoveredDependency> Dependencies);

    private sealed record AnalysisCacheEntry(LuaWorkspaceModuleResult Result);

    private sealed record ExportValue(LuaType Type, string Hash);

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

        public OperationMetrics(int maximumParallelism)
        {
            Scheduler = new SemaphoreSlim(maximumParallelism, maximumParallelism);
        }

        public SemaphoreSlim Scheduler { get; }

        public int CacheHits;

        public int CacheMisses;

        public int InvalidatedModules;

        public int FixedPointIterations;

        public int PeakParallelism => Volatile.Read(ref _peakParallelism);

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
    }
}
