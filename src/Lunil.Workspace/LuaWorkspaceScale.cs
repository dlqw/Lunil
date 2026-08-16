using System.Collections.Immutable;
using Lunil.Analysis;
using Lunil.Core.Text;
using Lunil.Semantics.Binding;

namespace Lunil.Workspace;

public enum LuaWorkspaceProgressPhase : byte
{
    Discovery,
    Resolution,
    Analysis,
    Indexing,
    CacheMaintenance,
    Completed,
}

public sealed record LuaWorkspaceProgress(
    LuaWorkspaceProgressPhase Phase,
    int CompletedWorkItems,
    int TotalWorkItems,
    string? ModuleName = null);

/// <summary>Compact module identity and summary retained without syntax or semantic trees.</summary>
public sealed record LuaWorkspaceCompactModule(
    LuaModuleIdentity Identity,
    string SourceIdentity,
    string ContentHash,
    string ExportHash,
    string ExportSymbolHash,
    string FunctionSummaryHash,
    string DependencySummaryHash,
    ImmutableArray<LuaWorkspaceExportSymbol> ExportedSymbols);

/// <summary>
/// Queryable workspace snapshot that stores compact references and summaries but no full compiler models.
/// </summary>
public sealed class LuaWorkspaceCompactSnapshot
{
    private readonly ImmutableArray<CompactReference> _references;
    private readonly ImmutableArray<CompactMemberReference> _memberReferences;
    private readonly ImmutableArray<CompactAnnotationReference> _annotationReferences;
    private readonly ImmutableArray<ImmutableDictionary<string, ImmutableArray<int>>> _targetIndexes;
    private readonly ImmutableArray<ImmutableDictionary<string, ImmutableArray<int>>> _memberIndexes;
    private readonly ImmutableArray<ImmutableDictionary<string, ImmutableArray<int>>> _annotationIndexes;
    private readonly ImmutableArray<ImmutableDictionary<string, ImmutableArray<int>>> _globalIndexes;
    private readonly ImmutableArray<ImmutableDictionary<string, ImmutableArray<int>>> _callIndexes;
    private readonly ImmutableArray<ImmutableDictionary<string, ImmutableArray<string>>> _callbackIndexes;
    private readonly ImmutableArray<ImmutableDictionary<string, ImmutableArray<string>>> _persistenceIndexes;
    private readonly ImmutableArray<string> _strings;

    private LuaWorkspaceCompactSnapshot(
        ImmutableArray<LuaWorkspaceCompactModule> modules,
        LuaModuleGraph graph,
        ImmutableArray<LuaWorkspaceDiagnostic> diagnostics,
        LuaWorkspaceMetrics metrics,
        LuaWorkspaceExportGraph exports,
        LuaWorkspaceModuleCallBindings calls,
        ImmutableArray<CompactReference> references,
        ImmutableArray<CompactMemberReference> memberReferences,
        ImmutableArray<CompactAnnotationReference> annotationReferences,
        ImmutableArray<ImmutableDictionary<string, ImmutableArray<int>>> targetIndexes,
        ImmutableArray<ImmutableDictionary<string, ImmutableArray<int>>> memberIndexes,
        ImmutableArray<ImmutableDictionary<string, ImmutableArray<int>>> annotationIndexes,
        ImmutableArray<ImmutableDictionary<string, ImmutableArray<int>>> globalIndexes,
        ImmutableArray<ImmutableDictionary<string, ImmutableArray<int>>> callIndexes,
        ImmutableArray<ImmutableDictionary<string, ImmutableArray<string>>> callbackIndexes,
        ImmutableArray<ImmutableDictionary<string, ImmutableArray<string>>> persistenceIndexes,
        ImmutableArray<string> strings,
        long estimatedResidentBytes)
    {
        Modules = modules;
        Graph = graph;
        Diagnostics = diagnostics;
        Metrics = metrics;
        ExportGraph = exports;
        CallBindings = calls;
        _references = references;
        _memberReferences = memberReferences;
        _annotationReferences = annotationReferences;
        _targetIndexes = targetIndexes;
        _memberIndexes = memberIndexes;
        _annotationIndexes = annotationIndexes;
        _globalIndexes = globalIndexes;
        _callIndexes = callIndexes;
        _callbackIndexes = callbackIndexes;
        _persistenceIndexes = persistenceIndexes;
        _strings = strings;
        EstimatedResidentBytes = estimatedResidentBytes;
    }

    public ImmutableArray<LuaWorkspaceCompactModule> Modules { get; }

    public LuaModuleGraph Graph { get; }

    public ImmutableArray<LuaWorkspaceDiagnostic> Diagnostics { get; }

    public LuaWorkspaceMetrics Metrics { get; }

    public LuaWorkspaceExportGraph ExportGraph { get; }

    public LuaWorkspaceModuleCallBindings CallBindings { get; }

    public long EstimatedResidentBytes { get; }

    public LuaWorkspaceCompactModule? GetModule(string name) => Modules.FirstOrDefault(module =>
        string.Equals(module.Identity.Name, name, StringComparison.Ordinal));

    public ImmutableArray<LuaWorkspaceReference> FindReferences(LuaSymbolKey key)
    {
        if (!GetShard(_targetIndexes, key.Value).TryGetValue(key.Value, out var indexes))
        {
            return [];
        }

        var result = ImmutableArray.CreateBuilder<LuaWorkspaceReference>(indexes.Length);
        foreach (var index in indexes)
        {
            result.Add(Materialize(_references[index]));
        }

        return result.MoveToImmutable();
    }

    /// <summary>
    /// Finds every indexed member or index reference with the given name across the workspace.
    /// Member references have no lexical symbol identity, so lookup is name-based.
    /// </summary>
    public ImmutableArray<LuaWorkspaceMemberReference> FindMemberReferences(string name)
    {
        LunilGuard.NotNullOrWhiteSpace(name);
        if (!GetShard(_memberIndexes, name).TryGetValue(name, out var indexes))
        {
            return [];
        }

        var result = ImmutableArray.CreateBuilder<LuaWorkspaceMemberReference>(indexes.Length);
        foreach (var index in indexes)
        {
            var reference = _memberReferences[index];
            result.Add(new LuaWorkspaceMemberReference(
                Modules[reference.ModuleIndex].Identity,
                reference.Span,
                _strings[reference.NameIndex]));
        }

        return result.MoveToImmutable();
    }

    /// <summary>
    /// Finds every annotation element that mentions the type name across the workspace:
    /// named type references inside type expressions plus the declaration lines of
    /// classes, aliases, and enums. Lookup is name-based.
    /// </summary>
    public ImmutableArray<LuaWorkspaceMemberReference> FindAnnotationReferences(string name)
    {
        LunilGuard.NotNullOrWhiteSpace(name);
        if (!GetShard(_annotationIndexes, name).TryGetValue(name, out var indexes))
        {
            return [];
        }

        var result = ImmutableArray.CreateBuilder<LuaWorkspaceMemberReference>(indexes.Length);
        foreach (var index in indexes)
        {
            var reference = _annotationReferences[index];
            result.Add(new LuaWorkspaceMemberReference(
                Modules[reference.ModuleIndex].Identity,
                reference.Span,
                name));
        }

        return result.MoveToImmutable();
    }

    public ImmutableArray<LuaWorkspaceReference> FindGlobalReferences(string name)
    {
        LunilGuard.NotNullOrWhiteSpace(name);
        if (!GetShard(_globalIndexes, name).TryGetValue(name, out var indexes))
        {
            return [];
        }

        var result = ImmutableArray.CreateBuilder<LuaWorkspaceReference>(indexes.Length);
        foreach (var index in indexes)
        {
            result.Add(Materialize(_references[index]));
        }

        return result.MoveToImmutable();
    }

    public ImmutableArray<LuaWorkspaceModuleCallBinding> FindCallsToExport(string targetSymbolKey)
    {
        LunilGuard.NotNullOrWhiteSpace(targetSymbolKey);
        if (!GetShard(_callIndexes, targetSymbolKey).TryGetValue(targetSymbolKey, out var indexes))
        {
            return [];
        }

        return [.. indexes.Select(index => CallBindings.Edges[index])];
    }

    public ImmutableArray<LuaWorkspaceExportSymbol> FindCallbackRegistrations(string hostTargetKey)
    {
        LunilGuard.NotNullOrWhiteSpace(hostTargetKey);
        if (!GetShard(_callbackIndexes, hostTargetKey).TryGetValue(hostTargetKey, out var sourceKeys))
        {
            return [];
        }

        var keys = sourceKeys.ToHashSet(StringComparer.Ordinal);
        return [.. ExportGraph.Symbols.Where(symbol => keys.Contains(symbol.Key))];
    }

    public ImmutableArray<LuaWorkspaceExportSymbol> FindPersistenceSchemas(string schemaId)
    {
        LunilGuard.NotNullOrWhiteSpace(schemaId);
        if (!GetShard(_persistenceIndexes, schemaId).TryGetValue(schemaId, out var symbolKeys))
        {
            return [];
        }

        var keys = symbolKeys.ToHashSet(StringComparer.Ordinal);
        return [.. ExportGraph.Symbols.Where(symbol => keys.Contains(symbol.Key))];
    }

    /// <summary>Re-materializes full compiler models from current source documents on demand.</summary>
    public Task<LuaWorkspaceResult> MaterializeAsync(
        LuaWorkspace workspace,
        IEnumerable<LuaWorkspaceDocument> documents,
        CancellationToken cancellationToken = default)
    {
        LunilGuard.NotNull(workspace);
        LunilGuard.NotNull(documents);
        if (Modules.IsDefault)
        {
            throw new InvalidOperationException("The compact snapshot is not initialized.");
        }

        return workspace.AnalyzeAsync(documents, cancellationToken);
    }

    internal sealed class StreamingBuilder
    {
        private readonly int _shardCount;
        private readonly LuaHostAnalysisContract? _hostContract;
        private readonly StringPool _strings = new();
        private readonly List<LuaWorkspaceCompactModule> _modules = [];
        private readonly List<CompactReference> _references = [];
        private readonly List<CompactMemberReference> _memberReferences = [];
        private readonly List<CompactAnnotationReference> _annotationReferences = [];
        private readonly Dictionary<string, ImmutableArray<int>.Builder> _targetIndexes =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, ImmutableArray<int>.Builder> _memberIndexes =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, ImmutableArray<int>.Builder> _annotationIndexes =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, ImmutableArray<int>.Builder> _globals =
            new(StringComparer.Ordinal);
        private readonly List<LuaWorkspaceExportSymbol> _symbols = [];
        private readonly List<LuaWorkspaceExportEdge> _edges = [];
        private readonly List<LuaWorkspaceModuleCallBinding> _rawCalls = [];
        private readonly Dictionary<string, string> _reExports = new(StringComparer.Ordinal);
        private int _totalCallCount;

        public StreamingBuilder(int shardCount, LuaHostAnalysisContract? hostContract)
        {
            _shardCount = shardCount;
            _hostContract = hostContract;
        }

        public LuaWorkspaceCompactSnapshot? Snapshot { get; private set; }

        public void AddModule(LuaWorkspaceModuleResult module)
        {
            var moduleIndex = _modules.Count;
            _modules.Add(new LuaWorkspaceCompactModule(
                module.Identity,
                module.SourceIdentity,
                module.ContentHash,
                module.ExportHash,
                module.ExportSymbolHash,
                module.FunctionSummaryHash,
                module.DependencySummaryHash,
                module.ExportedSymbols));
            AddReferences(module, moduleIndex);
            AddAnnotationReferences(module, moduleIndex);
            _totalCallCount += module.Compilation.Analysis.CallGraph.Edges.Length;
            var projection = WorkspaceSymbolGraphBuilder.Build([module], _hostContract);
            _symbols.AddRange(projection.Exports.Symbols.Where(static symbol => !symbol.IsExternal));
            _edges.AddRange(projection.Exports.Edges.Where(static edge => edge.Kind != "re-export"));
            _rawCalls.AddRange(projection.Calls.Edges);
            if (WorkspaceSymbolGraphBuilder.GetDirectReExportTarget(module) is { } target)
            {
                _reExports[module.Identity.Name] = target;
            }
        }

        public LuaWorkspaceCompactSnapshot Build(
            LuaModuleGraph graph,
            ImmutableArray<LuaWorkspaceDiagnostic> diagnostics,
            LuaWorkspaceMetrics metrics)
        {
            var hostProjection = WorkspaceSymbolGraphBuilder.Build([], _hostContract);
            _symbols.AddRange(hostProjection.Exports.Symbols);
            var lookup = _symbols
                .GroupBy(static symbol => (symbol.IsExternal, symbol.ModuleName, symbol.Path))
                .ToDictionary(static group => group.Key, static group => group.First());
            for (var index = 0; index < _symbols.Count; index++)
            {
                var symbol = _symbols[index];
                if (symbol.IsExternal || !_reExports.TryGetValue(symbol.ModuleName, out var targetModule) ||
                    !lookup.TryGetValue((false, targetModule, symbol.Path), out var target))
                {
                    continue;
                }

                var updated = symbol with
                {
                    TargetKey = target.Key,
                    IsReExport = true,
                    FunctionKey = target.FunctionKey,
                };
                _symbols[index] = updated;
                _edges.Add(new LuaWorkspaceExportEdge(updated.Key, target.Key, "re-export"));
            }

            var orderedSymbols = _symbols
                .GroupBy(static symbol => symbol.Key, StringComparer.Ordinal)
                .Select(static group => group.First())
                .OrderBy(static symbol => symbol.ModuleName, StringComparer.Ordinal)
                .ThenBy(static symbol => symbol.Path, StringComparer.Ordinal)
                .ThenBy(static symbol => symbol.Key, StringComparer.Ordinal)
                .ToImmutableArray();
            lookup = orderedSymbols
                .GroupBy(static symbol => (symbol.IsExternal, symbol.ModuleName, symbol.Path))
                .ToDictionary(static group => group.Key, static group => group.First());
            var symbolsByModule = orderedSymbols
                .GroupBy(static symbol => (symbol.IsExternal, symbol.ModuleName))
                .ToDictionary(static group => group.Key, static group => group.ToImmutableArray());
            var externalModules = orderedSymbols
                .Where(static symbol => symbol.IsExternal)
                .Select(static symbol => symbol.ModuleName)
                .ToHashSet(StringComparer.Ordinal);
            var dynamicSymbolsByModule = orderedSymbols
                .Where(static symbol => symbol.IsDynamic)
                .GroupBy(static symbol => (symbol.IsExternal, symbol.ModuleName))
                .ToDictionary(
                    static group => group.Key,
                    static group => group.Select(static symbol => symbol.Key).ToImmutableArray());
            var symbolsByName = orderedSymbols
                .GroupBy(static symbol => (symbol.IsExternal, symbol.ModuleName, symbol.Name))
                .ToDictionary(
                    static group => group.Key,
                    static group => group.Select(static symbol => symbol.Key).ToImmutableArray());
            var resolvedCalls = _rawCalls.Select(call => ResolveCall(
                    call,
                    lookup,
                    externalModules,
                    dynamicSymbolsByModule,
                    symbolsByName))
                .OrderBy(static call => call.SourceModuleName, StringComparer.Ordinal)
                .ThenBy(static call => call.Span.Start)
                .ToImmutableArray();
            var exports = new LuaWorkspaceExportGraph(
                orderedSymbols,
                [.. _edges
                    .Distinct()
                    .OrderBy(static edge => edge.SourceKey, StringComparer.Ordinal)
                    .ThenBy(static edge => edge.TargetKey, StringComparer.Ordinal)]);
            var localSymbolsByModule = symbolsByModule
                .Where(static pair => !pair.Key.IsExternal)
                .ToDictionary(static pair => pair.Key.ModuleName, static pair => pair.Value, StringComparer.Ordinal);
            for (var index = 0; index < _modules.Count; index++)
            {
                _modules[index] = _modules[index] with
                {
                    ExportedSymbols = localSymbolsByModule.GetValueOrDefault(
                        _modules[index].Identity.Name,
                        []),
                };
            }

            Snapshot = CreateSnapshot(
                [.. _modules],
                graph,
                diagnostics,
                metrics,
                exports,
                new LuaWorkspaceModuleCallBindings(resolvedCalls),
                [.. _references],
                [.. _memberReferences],
                [.. _annotationReferences],
                _targetIndexes,
                _memberIndexes,
                _annotationIndexes,
                _globals,
                _strings.ToImmutable(),
                _totalCallCount,
                _shardCount);
            return Snapshot;
        }

        private void AddReferences(LuaWorkspaceModuleResult module, int moduleIndex)
        {
            var model = module.Compilation.SemanticModel;
            var functionsById = model.Functions.ToDictionary(static function => function.Id);
            var containingFunctions = model.UnifiedReferences
                .Where(static reference => reference.LexicalReference is not null)
                .GroupBy(static reference => reference.Span)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.First().ContainingFunctionId);
            var functionKeys = functionsById.ToDictionary(
                static pair => pair.Key,
                pair => model.GetFunctionKey(pair.Value, module.Identity).Value);
            foreach (var reference in model.References)
            {
                var containingFunctionId = containingFunctions.GetValueOrDefault(reference.Span);
                var target = reference.Symbol.Kind == LuaSymbolKind.Environment
                    ? null
                    : model.GetSymbolKey(reference.Symbol, module.Identity).Value;
                var index = _references.Count;
                _references.Add(new CompactReference(
                    moduleIndex,
                    reference.Span,
                    containingFunctionId,
                    _strings.GetOrAdd(functionKeys[containingFunctionId]),
                    _strings.GetOrAdd(reference.Name),
                    reference.IsWrite,
                    reference.ResolutionKind,
                    target is null ? -1 : _strings.GetOrAdd(target)));
                if (target is not null)
                {
                    GetIndexBuilder(_targetIndexes, target).Add(index);
                }

                if (reference.ResolutionKind == LuaNameResolutionKind.Global)
                {
                    GetIndexBuilder(_globals, reference.Name).Add(index);
                }
            }

            // Member and index references have no lexical symbol identity; they are indexed
            // by member name so cross-file member navigation can find them workspace-wide.
            foreach (var reference in model.UnifiedReferences)
            {
                if (reference.Kind is not (LuaReferenceKind.Member or LuaReferenceKind.Index) ||
                    string.IsNullOrEmpty(reference.Name))
                {
                    continue;
                }

                var memberIndex = _memberReferences.Count;
                _memberReferences.Add(new CompactMemberReference(
                    moduleIndex,
                    reference.Span,
                    _strings.GetOrAdd(reference.Name)));
                GetIndexBuilder(_memberIndexes, reference.Name).Add(memberIndex);
            }
        }

        /// <summary>Adds one entry per annotation element naming a type (references and declarations).</summary>
        private void AddAnnotationReferences(LuaWorkspaceModuleResult module, int moduleIndex)
        {
            AnnotationReferenceCollector.Collect(module.Compilation.Annotations, name =>
            {
                var annotationIndex = _annotationReferences.Count;
                _annotationReferences.Add(new CompactAnnotationReference(moduleIndex, name.Span));
                GetIndexBuilder(_annotationIndexes, name.Name).Add(annotationIndex);
            });
        }

        private static LuaWorkspaceModuleCallBinding ResolveCall(
                LuaWorkspaceModuleCallBinding call,
                Dictionary<(bool IsExternal, string ModuleName, string Path), LuaWorkspaceExportSymbol> lookup,
                HashSet<string> externalModules,
                Dictionary<(bool IsExternal, string ModuleName), ImmutableArray<string>> dynamicSymbolsByModule,
                Dictionary<(bool IsExternal, string ModuleName, string Name), ImmutableArray<string>> symbolsByName)
        {
            if (call.Status == LuaWorkspaceBindingStatus.Resolved ||
                call.Reason == "module-alias-reassigned")
            {
                return call;
            }

            var external = externalModules.Contains(call.RequestedModuleName);
            lookup.TryGetValue((external, call.RequestedModuleName, call.MemberPath), out var target);
            ImmutableArray<string> candidates;
            if (target is not null)
            {
                candidates = target.IsDynamic ? [target.Key] : [];
            }
            else
            {
                var separator = call.MemberPath.LastIndexOf('.');
                var memberName = separator < 0 ? call.MemberPath : call.MemberPath[(separator + 1)..];
                candidates = [.. dynamicSymbolsByModule
                    .GetValueOrDefault((external, call.RequestedModuleName), [])
                    .Concat(symbolsByName.GetValueOrDefault(
                        (external, call.RequestedModuleName, memberName), []))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(static key => key, StringComparer.Ordinal)];
            }
            var status = target is not null && !target.IsDynamic
                ? LuaWorkspaceBindingStatus.Resolved
                : candidates.Length != 0
                    ? LuaWorkspaceBindingStatus.Dynamic
                    : LuaWorkspaceBindingStatus.Unresolved;
            return call with
            {
                TargetSymbolKey = target?.Key,
                TargetFunctionKey = target?.Kind == LuaWorkspaceExportKind.Function
                    ? target.FunctionKey
                    : null,
                CandidateKeys = candidates,
                Status = status,
                Reason = status switch
                {
                    LuaWorkspaceBindingStatus.Resolved => null,
                    LuaWorkspaceBindingStatus.Dynamic => "dynamic-export-candidate",
                    _ => "exported-member-not-found",
                },
                DefinitionSpan = target is { IsExternal: false } ? target.DefinitionSpan : null,
                ExternalDefinition = target?.ExternalSource,
                ExternalImplementation = target?.ExternalSource is { ImplementationUri: not null } source
                    ? source with { Uri = source.ImplementationUri! }
                    : null,
            };
        }
    }

    internal static LuaWorkspaceCompactSnapshot Create(LuaWorkspaceResult workspace, int shardCount)
    {
        var strings = new StringPool();
        var references = new List<CompactReference>();
        var memberReferences = new List<CompactMemberReference>();
        var annotationReferences = new List<CompactAnnotationReference>();
        var targetIndexes = new Dictionary<string, ImmutableArray<int>.Builder>(StringComparer.Ordinal);
        var memberIndexes = new Dictionary<string, ImmutableArray<int>.Builder>(StringComparer.Ordinal);
        var annotationIndexes = new Dictionary<string, ImmutableArray<int>.Builder>(StringComparer.Ordinal);
        var globals = new Dictionary<string, ImmutableArray<int>.Builder>(StringComparer.Ordinal);
        var modules = workspace.Modules.Select(module => new LuaWorkspaceCompactModule(
            module.Identity,
            module.SourceIdentity,
            module.ContentHash,
            module.ExportHash,
            module.ExportSymbolHash,
            module.FunctionSummaryHash,
            module.DependencySummaryHash,
            module.ExportedSymbols)).ToImmutableArray();
        for (var moduleIndex = 0; moduleIndex < workspace.Modules.Length; moduleIndex++)
        {
            var module = workspace.Modules[moduleIndex];
            var model = module.Compilation.SemanticModel;
            var functionsById = model.Functions.ToDictionary(static function => function.Id);
            var containingFunctions = model.UnifiedReferences
                .Where(static reference => reference.LexicalReference is not null)
                .GroupBy(static reference => reference.Span)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.First().ContainingFunctionId);
            var functionKeys = functionsById.ToDictionary(
                static pair => pair.Key,
                pair => model.GetFunctionKey(pair.Value, module.Identity).Value);
            foreach (var reference in model.References)
            {
                var containingFunctionId = containingFunctions.GetValueOrDefault(reference.Span);
                var target = reference.Symbol.Kind == LuaSymbolKind.Environment
                    ? null
                    : model.GetSymbolKey(reference.Symbol, module.Identity).Value;
                var global = reference.ResolutionKind == LuaNameResolutionKind.Global
                    ? reference.Name
                    : null;
                var index = references.Count;
                references.Add(new CompactReference(
                    moduleIndex,
                    reference.Span,
                    containingFunctionId,
                    strings.GetOrAdd(functionKeys[containingFunctionId]),
                    strings.GetOrAdd(reference.Name),
                    reference.IsWrite,
                    reference.ResolutionKind,
                    target is null ? -1 : strings.GetOrAdd(target)));
                if (target is not null)
                {
                    if (!targetIndexes.TryGetValue(target, out var targetList))
                    {
                        targetList = ImmutableArray.CreateBuilder<int>();
                        targetIndexes.Add(target, targetList);
                    }

                    targetList.Add(index);
                }

                if (global is not null)
                {
                    if (!globals.TryGetValue(global, out var globalList))
                    {
                        globalList = ImmutableArray.CreateBuilder<int>();
                        globals.Add(global, globalList);
                    }

                    globalList.Add(index);
                }
            }

            foreach (var reference in model.UnifiedReferences)
            {
                if (reference.Kind is not (LuaReferenceKind.Member or LuaReferenceKind.Index) ||
                    string.IsNullOrEmpty(reference.Name))
                {
                    continue;
                }

                var memberIndex = memberReferences.Count;
                memberReferences.Add(new CompactMemberReference(
                    moduleIndex,
                    reference.Span,
                    strings.GetOrAdd(reference.Name)));
                if (!memberIndexes.TryGetValue(reference.Name, out var memberList))
                {
                    memberList = ImmutableArray.CreateBuilder<int>();
                    memberIndexes.Add(reference.Name, memberList);
                }

                memberList.Add(memberIndex);
            }

            AnnotationReferenceCollector.Collect(module.Compilation.Annotations, name =>
            {
                var annotationIndex = annotationReferences.Count;
                annotationReferences.Add(new CompactAnnotationReference(moduleIndex, name.Span));
                if (!annotationIndexes.TryGetValue(name.Name, out var annotationList))
                {
                    annotationList = ImmutableArray.CreateBuilder<int>();
                    annotationIndexes.Add(name.Name, annotationList);
                }

                annotationList.Add(annotationIndex);
            });
        }

        var frozenStrings = strings.ToImmutable();
        var callIndexes = new Dictionary<string, ImmutableArray<int>.Builder>(StringComparer.Ordinal);
        for (var index = 0; index < workspace.CallBindings.Edges.Length; index++)
        {
            var call = workspace.CallBindings.Edges[index];
            foreach (var key in call.TargetSymbolKey is { } target
                         ? call.CandidateKeys.Prepend(target)
                         : call.CandidateKeys)
            {
                if (!callIndexes.TryGetValue(key, out var indexes))
                {
                    indexes = ImmutableArray.CreateBuilder<int>();
                    callIndexes.Add(key, indexes);
                }

                indexes.Add(index);
            }
        }

        var callbackIndexes = workspace.ExportGraph.Edges
            .Where(static edge => edge.Kind is "callback-registration" or "callback-unsubscribe")
            .GroupBy(static edge => edge.TargetKey, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static edge => edge.SourceKey).Distinct(StringComparer.Ordinal)
                    .ToImmutableArray(),
                StringComparer.Ordinal);
        var persistenceIndexes = workspace.ExportGraph.Symbols
            .Where(static symbol => symbol.Path.StartsWith("$persistence-schema/", StringComparison.Ordinal))
            .Select(symbol => (Symbol: symbol, Segments: symbol.Path.Split('/')))
            .Where(static item => item.Segments.Length >= 3)
            .GroupBy(static item => item.Segments[1], StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static item => item.Symbol.Key).ToImmutableArray(),
                StringComparer.Ordinal);
        var estimatedBytes = checked(
            (long)references.Count * 40 +
            (long)memberReferences.Count * 16 +
            (long)annotationReferences.Count * 16 +
            frozenStrings.Sum(static value => 24L + value.Length * sizeof(char)) +
            modules.Length * 160L +
            workspace.CallBindings.Edges.Length * 160L);
        var indexedCallCount = workspace.Modules.Sum(static module =>
            module.Compilation.Analysis.CallGraph.Edges.Length);
        var metrics = workspace.Metrics with
        {
            IndexedReferenceCount = references.Count,
            IndexedCallCount = indexedCallCount,
            CompactResidentBytes = estimatedBytes,
        };
        return new LuaWorkspaceCompactSnapshot(
            modules,
            workspace.Graph,
            workspace.Diagnostics,
            workspace.Metrics,
            workspace.ExportGraph,
            workspace.CallBindings,
            [.. references],
            [.. memberReferences],
            [.. annotationReferences],
            CreateShards(targetIndexes.ToImmutableDictionary(
                static pair => pair.Key,
                static pair => pair.Value.ToImmutable(),
                StringComparer.Ordinal), shardCount),
            CreateShards(memberIndexes.ToImmutableDictionary(
                static pair => pair.Key,
                static pair => pair.Value.ToImmutable(),
                StringComparer.Ordinal), shardCount),
            CreateShards(annotationIndexes.ToImmutableDictionary(
                static pair => pair.Key,
                static pair => pair.Value.ToImmutable(),
                StringComparer.Ordinal), shardCount),
            CreateShards(globals.ToImmutableDictionary(
                static pair => pair.Key,
                static pair => pair.Value.ToImmutable(),
                StringComparer.Ordinal), shardCount),
            CreateShards(callIndexes.ToImmutableDictionary(
                static pair => pair.Key,
                static pair => pair.Value.ToImmutable(),
                StringComparer.Ordinal), shardCount),
            CreateShards(callbackIndexes.ToImmutableDictionary(StringComparer.Ordinal), shardCount),
            CreateShards(persistenceIndexes.ToImmutableDictionary(StringComparer.Ordinal), shardCount),
            frozenStrings,
            estimatedBytes);
    }

    private static LuaWorkspaceCompactSnapshot CreateSnapshot(
        ImmutableArray<LuaWorkspaceCompactModule> modules,
        LuaModuleGraph graph,
        ImmutableArray<LuaWorkspaceDiagnostic> diagnostics,
        LuaWorkspaceMetrics sourceMetrics,
        LuaWorkspaceExportGraph exports,
        LuaWorkspaceModuleCallBindings calls,
        ImmutableArray<CompactReference> references,
        ImmutableArray<CompactMemberReference> memberReferences,
        ImmutableArray<CompactAnnotationReference> annotationReferences,
        Dictionary<string, ImmutableArray<int>.Builder> targetIndexes,
        Dictionary<string, ImmutableArray<int>.Builder> memberIndexes,
        Dictionary<string, ImmutableArray<int>.Builder> annotationIndexes,
        Dictionary<string, ImmutableArray<int>.Builder> globals,
        ImmutableArray<string> strings,
        int indexedCallCount,
        int shardCount)
    {
        var callIndexes = new Dictionary<string, ImmutableArray<int>.Builder>(StringComparer.Ordinal);
        for (var index = 0; index < calls.Edges.Length; index++)
        {
            var call = calls.Edges[index];
            foreach (var key in call.TargetSymbolKey is { } target
                         ? call.CandidateKeys.Prepend(target)
                         : call.CandidateKeys)
            {
                GetIndexBuilder(callIndexes, key).Add(index);
            }
        }

        var callbackIndexes = exports.Edges
            .Where(static edge => edge.Kind is "callback-registration" or "callback-unsubscribe")
            .GroupBy(static edge => edge.TargetKey, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static edge => edge.SourceKey)
                    .Distinct(StringComparer.Ordinal).ToImmutableArray(),
                StringComparer.Ordinal);
        var persistenceIndexes = exports.Symbols
            .Where(static symbol => symbol.Path.StartsWith("$persistence-schema/", StringComparison.Ordinal))
            .Select(symbol => (Symbol: symbol, Segments: symbol.Path.Split('/')))
            .Where(static item => item.Segments.Length >= 3)
            .GroupBy(static item => item.Segments[1], StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static item => item.Symbol.Key).ToImmutableArray(),
                StringComparer.Ordinal);
        var estimatedBytes = checked(
            (long)references.Length * 40 +
            (long)memberReferences.Length * 16 +
            (long)annotationReferences.Length * 16 +
            strings.Sum(static value => 24L + value.Length * sizeof(char)) +
            modules.Length * 160L +
            calls.Edges.Length * 160L);
        var metrics = sourceMetrics with
        {
            IndexedReferenceCount = references.Length,
            IndexedCallCount = indexedCallCount,
            CompactResidentBytes = estimatedBytes,
        };
        return new LuaWorkspaceCompactSnapshot(
            modules,
            graph,
            diagnostics,
            metrics,
            exports,
            calls,
            references,
            memberReferences,
            annotationReferences,
            CreateShards(targetIndexes.ToImmutableDictionary(
                static pair => pair.Key,
                static pair => pair.Value.ToImmutable(),
                StringComparer.Ordinal), shardCount),
            CreateShards(memberIndexes.ToImmutableDictionary(
                static pair => pair.Key,
                static pair => pair.Value.ToImmutable(),
                StringComparer.Ordinal), shardCount),
            CreateShards(annotationIndexes.ToImmutableDictionary(
                static pair => pair.Key,
                static pair => pair.Value.ToImmutable(),
                StringComparer.Ordinal), shardCount),
            CreateShards(globals.ToImmutableDictionary(
                static pair => pair.Key,
                static pair => pair.Value.ToImmutable(),
                StringComparer.Ordinal), shardCount),
            CreateShards(callIndexes.ToImmutableDictionary(
                static pair => pair.Key,
                static pair => pair.Value.ToImmutable(),
                StringComparer.Ordinal), shardCount),
            CreateShards(callbackIndexes.ToImmutableDictionary(StringComparer.Ordinal), shardCount),
            CreateShards(persistenceIndexes.ToImmutableDictionary(StringComparer.Ordinal), shardCount),
            strings,
            estimatedBytes);
    }

    private LuaWorkspaceReference Materialize(CompactReference reference)
    {
        var module = Modules[reference.ModuleIndex];
        return new LuaWorkspaceReference(
            module.Identity,
            module.SourceIdentity,
            reference.Span,
            reference.ContainingFunctionId,
            new LuaSymbolKey(_strings[reference.ContainingFunctionKeyIndex]),
            _strings[reference.NameIndex],
            reference.IsWrite,
            reference.ResolutionKind,
            reference.TargetKeyIndex < 0 ? null : new LuaSymbolKey(_strings[reference.TargetKeyIndex]));
    }

    private readonly record struct CompactReference(
        int ModuleIndex,
        TextSpan Span,
        int ContainingFunctionId,
        int ContainingFunctionKeyIndex,
        int NameIndex,
        bool IsWrite,
        LuaNameResolutionKind ResolutionKind,
        int TargetKeyIndex);

    private readonly record struct CompactMemberReference(
        int ModuleIndex,
        TextSpan Span,
        int NameIndex);

    private readonly record struct CompactAnnotationReference(
        int ModuleIndex,
        TextSpan Span);

    private static ImmutableDictionary<string, TValue> GetShard<TValue>(
        ImmutableArray<ImmutableDictionary<string, TValue>> shards,
        string key) => shards[GetShardIndex(key, shards.Length)];

    private static ImmutableArray<ImmutableDictionary<string, TValue>> CreateShards<TValue>(
        ImmutableDictionary<string, TValue> values,
        int shardCount)
    {
        var builders = Enumerable.Range(0, shardCount)
            .Select(_ => ImmutableDictionary.CreateBuilder<string, TValue>(StringComparer.Ordinal))
            .ToArray();
        foreach (var pair in values)
        {
            builders[GetShardIndex(pair.Key, shardCount)].Add(pair.Key, pair.Value);
        }

        return [.. builders.Select(static builder => builder.ToImmutable())];
    }

    private static int GetShardIndex(string key, int shardCount)
    {
        uint hash = 2_166_136_261;
        foreach (var character in key)
        {
            hash = (hash ^ character) * 16_777_619;
        }

        return (int)(hash % (uint)shardCount);
    }

    private static ImmutableArray<int>.Builder GetIndexBuilder(
        Dictionary<string, ImmutableArray<int>.Builder> indexes,
        string key)
    {
        if (!indexes.TryGetValue(key, out var builder))
        {
            builder = ImmutableArray.CreateBuilder<int>();
            indexes.Add(key, builder);
        }

        return builder;
    }

    private sealed class StringPool
    {
        private readonly Dictionary<string, int> _indexes = new(StringComparer.Ordinal);
        private readonly List<string> _values = [];

        public int GetOrAdd(string value)
        {
            if (_indexes.TryGetValue(value, out var index))
            {
                return index;
            }

            index = _values.Count;
            _values.Add(value);
            _indexes.Add(value, index);
            return index;
        }

        public ImmutableArray<string> ToImmutable() => [.. _values];
    }
}
