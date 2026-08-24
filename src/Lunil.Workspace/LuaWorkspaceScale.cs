using System.Collections.Immutable;
using Lunil.Analysis;
using Lunil.Core.Diagnostics;
using Lunil.Core.Text;
using Lunil.Semantics.Binding;

namespace Lunil.Workspace;

public enum LuaWorkspaceProgressPhase : byte
{
    /// <summary>Reading workspace files from disk into the document set.</summary>
    Loading,
    /// <summary>Scanning loaded documents for type declarations (lexing only).</summary>
    Declarations,
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

    /// <summary>
    /// Per-module projection contributions keyed by module name: everything the snapshot
    /// derives from one module plus the analysis cache key that produced it. A later
    /// rebuild whose module key still matches reuses the contribution verbatim instead
    /// of re-parsing and re-analyzing the module.
    /// </summary>
    public ImmutableDictionary<string, ModuleContribution> Contributions { get; set; } =
        ImmutableDictionary<string, ModuleContribution>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>
    /// Global assignments collected from every module's analysis (game, settings, logger,
    /// ...): the per-document analysis seeds these so a global defined in one file
    /// resolves with its real type in every other file — no user configuration, no
    /// annotations, just Lua code (the EmmyLua zero-config model).
    /// </summary>
    public ImmutableDictionary<string, LuaType> Globals { get; set; } =
        ImmutableDictionary<string, LuaType>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>
    /// The reusable projection of one module: its compact entry, reference segments with
    /// the names they interned, symbol-graph output, raw call edges, and compilation
    /// diagnostics. Re-merging into a fresh snapshot only remaps string indexes and the
    /// module index.
    /// </summary>
    public sealed record ModuleContribution(
        string ModuleName,
        string CacheKey,
        LuaWorkspaceCompactModule Module,
        Lunil.Analysis.LuaType ExportedType,
        ImmutableDictionary<string, string> ExportSummaryHashes,
        ImmutableDictionary<string, string> FunctionSummaryHashes,
        string HostSummaryHash,
        string AnalysisSummaryHash,
        ImmutableArray<string> Names,
        ImmutableArray<int> NameIndexes,
        ImmutableArray<CompactReference> References,
        ImmutableArray<CompactMemberReference> MemberReferences,
        ImmutableArray<CompactAnnotationReference> AnnotationReferences,
        ImmutableArray<string> AnnotationNames,
        ImmutableArray<LuaWorkspaceExportSymbol> Symbols,
        ImmutableArray<LuaWorkspaceExportEdge> Edges,
        ImmutableArray<LuaWorkspaceModuleCallBinding> RawCalls,
        int CallCount,
        string? ReExportTarget,
        ImmutableArray<Lunil.Compiler.LuaCompilationDiagnostic> CompilationDiagnostics);

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

    /// <summary>
    /// Persists the navigation surface of this snapshot (modules, graph, export symbols
    /// and edges, call bindings, references, string pool, and the query indexes) so a
    /// later server start can serve search, definitions, references, and hierarchy
    /// before the first rebuild lands. Type payloads and per-module contributions are
    /// not persisted: hovers fall back until the replacement round stores.
    /// </summary>
    public void SaveNavigationSnapshot(string path, string fingerprint)
    {
        LunilGuard.NotNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var deflate = new System.IO.Compression.DeflateStream(
                stream, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            using (var writer = new BinaryWriter(deflate, System.Text.Encoding.UTF8))
            {
                WriteNavigationSnapshot(writer, fingerprint);
            }

            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(temporary, path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException)
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Loads a snapshot saved by <see cref="SaveNavigationSnapshot"/>. Returns null on
    /// any mismatch (fingerprint, format version) or corruption — callers fall back to
    /// a cold build.
    /// </summary>
    public static LuaWorkspaceCompactSnapshot? TryLoadNavigationSnapshot(string path, string fingerprint)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            // A large BufferedStream between DeflateStream and BinaryReader: millions of
            // small BinaryReader reads each pay DeflateStream decompression overhead, but
            // the buffer refills in bulk. (Decompressing to memory first would exceed
            // MemoryStream's 2 GB limit on large corpora.)
            using var deflate = new System.IO.Compression.DeflateStream(
                stream, System.IO.Compression.CompressionMode.Decompress);
            using var buffered = new BufferedStream(deflate, 4 * 1024 * 1024);
            using var reader = new BinaryReader(buffered, System.Text.Encoding.UTF8);
            return ReadNavigationSnapshot(reader, fingerprint);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException or EndOfStreamException or FormatException or
            InvalidCastException or ArgumentException)
        {
            return null;
        }
    }

    private const int NavigationSnapshotMagic = 0x4C554E53; // "LUNS"
    private const int NavigationSnapshotVersion = 3;

    private void WriteNavigationSnapshot(BinaryWriter writer, string fingerprint)
    {
        writer.Write(NavigationSnapshotMagic);
        writer.Write(NavigationSnapshotVersion);
        writer.Write(fingerprint);

        // Pass 1: build the deduplicated tables across everything the file references.
        // Symbols and edges appear both in the export graph and inside per-module
        // contributions — the same records — so table dedup halves their footprint;
        // their strings are value-interned on top (module names, keys, and paths repeat
        // per symbol). Types dedup by reference (shared subtrees once).
        var types = new TypeTableBuilder();
        var symbols = new SymbolTableBuilder(types);
        var edges = new EdgeTableBuilder();
        foreach (var symbol in ExportGraph.Symbols)
        {
            symbols.Visit(symbol);
        }

        foreach (var edge in ExportGraph.Edges)
        {
            edges.Visit(edge);
        }

        var orderedContributions = Contributions.OrderBy(static pair => pair.Key, StringComparer.Ordinal).ToArray();
        foreach (var (_, contribution) in orderedContributions)
        {
            types.Visit(contribution.ExportedType);
            foreach (var symbol in contribution.Symbols)
            {
                symbols.Visit(symbol);
            }

            foreach (var edge in contribution.Edges)
            {
                edges.Visit(edge);
            }
        }

        WriteStrings(writer, _strings);
        WriteTypes(writer, types);
        symbols.Write(writer);
        WriteInt32Array(writer, [.. ExportGraph.Symbols.Select(symbols.Visit)]);
        WriteModules(writer, Modules, symbols);
        WriteGraph(writer, Graph);
        edges.Write(writer);
        WriteInt32Array(writer, [.. ExportGraph.Edges.Select(edges.Visit)]);
        WriteCallBindings(writer, CallBindings.Edges);
        WriteReferences(writer, _references);
        WriteMemberReferences(writer, _memberReferences);
        WriteAnnotationReferences(writer, _annotationReferences);
        WriteIntIndexes(writer, _targetIndexes);
        WriteIntIndexes(writer, _memberIndexes);
        WriteIntIndexes(writer, _annotationIndexes);
        WriteIntIndexes(writer, _globalIndexes);
        WriteIntIndexes(writer, _callIndexes);
        WriteStringIndexes(writer, _callbackIndexes);
        WriteStringIndexes(writer, _persistenceIndexes);
        WriteContributions(writer, orderedContributions, types, symbols, edges);
        writer.Write(EstimatedResidentBytes);
    }

    private static LuaWorkspaceCompactSnapshot? ReadNavigationSnapshot(BinaryReader reader, string fingerprint)
    {
        if (reader.ReadInt32() != NavigationSnapshotMagic || reader.ReadInt32() != NavigationSnapshotVersion)
        {
            return null;
        }

        if (!string.Equals(reader.ReadString(), fingerprint, StringComparison.Ordinal))
        {
            return null;
        }

        var strings = ReadStrings(reader);
        const int shardCount = 64;
        var types = ReadTypes(reader);
        var symbols = SymbolTableBuilder.ReadSymbols(reader, types);
        var graphSymbolIndexes = ReadInt32Array(reader);
        var modules = ReadModules(reader, symbols);
        var graph = ReadGraph(reader);
        // The write order is edge intern pool, edge table, then the graph edge index
        // array — the index array argument must NOT be read before the edge table.
        var graphEdges = EdgeTableBuilder.ReadEdges(reader);
        var calls = ReadCallBindings(reader);
        var references = ReadReferences(reader);
        var memberReferences = ReadMemberReferences(reader);
        var annotationReferences = ReadAnnotationReferences(reader);
        var targetIndexes = ReadIntIndexes(reader, shardCount);
        var memberIndexes = ReadIntIndexes(reader, shardCount);
        var annotationIndexes = ReadIntIndexes(reader, shardCount);
        var globalIndexes = ReadIntIndexes(reader, shardCount);
        var callIndexes = ReadIntIndexes(reader, shardCount);
        var callbackIndexes = ReadStringIndexes(reader, shardCount);
        var persistenceIndexes = ReadStringIndexes(reader, shardCount);
        var contributions = ReadContributions(reader, types, modules, references, memberReferences, annotationReferences, symbols, graphEdges);
        var estimatedBytes = reader.ReadInt64();

        var exportSymbols = ImmutableArray.CreateBuilder<LuaWorkspaceExportSymbol>(graphSymbolIndexes.Length);
        foreach (var index in graphSymbolIndexes)
        {
            exportSymbols.Add(index >= 0 && index < symbols.Length ? symbols[index] : NothingSymbol);
        }

        return new LuaWorkspaceCompactSnapshot(
            modules,
            graph,
            [],
            new LuaWorkspaceMetrics(0, modules.Length, 0, 0, 0, 0, 0),
            new LuaWorkspaceExportGraph(exportSymbols.MoveToImmutable(), graphEdges),
            new LuaWorkspaceModuleCallBindings(calls),
            references,
            memberReferences,
            annotationReferences,
            targetIndexes,
            memberIndexes,
            annotationIndexes,
            globalIndexes,
            callIndexes,
            callbackIndexes,
            persistenceIndexes,
            strings,
            estimatedBytes)
        {
            Contributions = contributions,
        };
    }

    private static readonly LuaWorkspaceExportSymbol NothingSymbol = new(
        string.Empty, string.Empty, string.Empty, string.Empty, LuaWorkspaceExportKind.Dynamic,
        LuaTypes.Any, default, null, false, false, true, null);

    /// <summary>
    /// Builds a reference-deduplicated table of export symbols with value-interned
    /// strings: the export graph and the per-module contributions hold the same records,
    /// and module names/keys/paths repeat across symbols — the table stores each unique
    /// record and string once.
    /// </summary>
    private sealed class SymbolTableBuilder
    {
        private readonly Dictionary<LuaWorkspaceExportSymbol, int> _indexes = new(SymbolReferenceComparer.Instance);
        private readonly List<LuaWorkspaceExportSymbol> _nodes = [];
        private readonly Dictionary<string, int> _interned = new(StringComparer.Ordinal);
        private readonly List<string> _interns = [];
        private readonly TypeTableBuilder _types;

        public SymbolTableBuilder(TypeTableBuilder types)
        {
            _types = types;
        }

        public int Count => _nodes.Count;

        public int Visit(LuaWorkspaceExportSymbol symbol)
        {
            if (_indexes.TryGetValue(symbol, out var existing))
            {
                return existing;
            }

            // Register the symbol's type before its own entry so the type table is
            // complete by write time (the type table is written after this table but
            // both are in-memory until then).
            var typeIndex = _types.Visit(symbol.Type);
            var index = _nodes.Count;
            _nodes.Add(symbol);
            _indexes[symbol] = index;
            _ = typeIndex;
            return index;
        }

        private int Intern(string value)
        {
            if (_interned.TryGetValue(value, out var existing))
            {
                return existing;
            }

            var index = _interns.Count;
            _interns.Add(value);
            _interned[value] = index;
            return index;
        }

        public void Write(BinaryWriter writer)
        {
            // Intern pass (strings repeat across symbols).
            foreach (var symbol in _nodes)
            {
                Intern(symbol.Key);
                Intern(symbol.ModuleName);
                Intern(symbol.Path);
                Intern(symbol.Name);
                if (symbol.TargetKey is not null)
                {
                    Intern(symbol.TargetKey);
                }

                if (symbol.FunctionKey is not null)
                {
                    Intern(symbol.FunctionKey);
                }
            }

            writer.Write(_interns.Count);
            foreach (var value in _interns)
            {
                writer.Write(value);
            }

            writer.Write(_nodes.Count);
            foreach (var symbol in _nodes)
            {
                writer.Write(Intern(symbol.Key));
                writer.Write(Intern(symbol.ModuleName));
                writer.Write(Intern(symbol.Path));
                writer.Write(Intern(symbol.Name));
                writer.Write((byte)symbol.Kind);
                writer.Write(_types.Visit(symbol.Type));
                WriteSpan(writer, symbol.DefinitionSpan);
                writer.Write(symbol.TargetKey is null ? -1 : Intern(symbol.TargetKey));
                writer.Write(symbol.FunctionKey is null ? -1 : Intern(symbol.FunctionKey));
                writer.Write(symbol.IsReExport);
                writer.Write(symbol.IsExternal);
                writer.Write(symbol.IsDynamic);
            }
        }

        public static LuaWorkspaceExportSymbol[] ReadSymbols(BinaryReader reader, LuaType[] types)
        {
            var internCount = reader.ReadInt32();
            if (internCount < 0 || internCount > 50_000_000)
            {
                throw new FormatException("Corrupt symbol intern count.");
            }

            var interns = new string[internCount];
            for (var index = 0; index < internCount; index++)
            {
                interns[index] = reader.ReadString();
            }

            var count = reader.ReadInt32();
            if (count < 0 || count > 100_000_000)
            {
                throw new FormatException("Corrupt symbol count.");
            }

            var nodes = new LuaWorkspaceExportSymbol[count];
            string Intern(int index) => index >= 0 && index < interns.Length ? interns[index] : string.Empty;
            for (var index = 0; index < count; index++)
            {
                var key = Intern(reader.ReadInt32());
                var moduleName = Intern(reader.ReadInt32());
                var path = Intern(reader.ReadInt32());
                var name = Intern(reader.ReadInt32());
                var kind = (LuaWorkspaceExportKind)reader.ReadByte();
                var typeIndex = reader.ReadInt32();
                var type = typeIndex >= 0 && typeIndex < types.Length ? types[typeIndex] : LuaTypes.Any;
                var span = ReadSpan(reader);
                var targetKeyIndex = reader.ReadInt32();
                var functionKeyIndex = reader.ReadInt32();
                var isReExport = reader.ReadBoolean();
                var isExternal = reader.ReadBoolean();
                var isDynamic = reader.ReadBoolean();
                nodes[index] = new LuaWorkspaceExportSymbol(
                    key, moduleName, path, name, kind, type, span,
                    targetKeyIndex < 0 ? null : Intern(targetKeyIndex), isReExport,
                    isExternal, isDynamic, null)
                {
                    FunctionKey = functionKeyIndex < 0 ? null : Intern(functionKeyIndex),
                };
            }

            return nodes;
        }
    }

    private sealed class SymbolReferenceComparer : IEqualityComparer<LuaWorkspaceExportSymbol>
    {
        public static readonly SymbolReferenceComparer Instance = new();

        public bool Equals(LuaWorkspaceExportSymbol? x, LuaWorkspaceExportSymbol? y) =>
            ReferenceEquals(x, y);

        public int GetHashCode(LuaWorkspaceExportSymbol obj) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    /// <summary>
    /// Builds a reference-deduplicated table of export edges with value-interned
    /// strings: graph edges and contribution edges are the same records.
    /// </summary>
    private sealed class EdgeTableBuilder
    {
        private readonly Dictionary<LuaWorkspaceExportEdge, int> _indexes = new(EdgeReferenceComparer.Instance);
        private readonly List<LuaWorkspaceExportEdge> _nodes = [];
        private readonly Dictionary<string, int> _interned = new(StringComparer.Ordinal);
        private readonly List<string> _interns = [];

        public int Visit(LuaWorkspaceExportEdge edge)
        {
            if (_indexes.TryGetValue(edge, out var existing))
            {
                return existing;
            }

            var index = _nodes.Count;
            _nodes.Add(edge);
            _indexes[edge] = index;
            return index;
        }

        private int Intern(string value)
        {
            if (_interned.TryGetValue(value, out var existing))
            {
                return existing;
            }

            var index = _interns.Count;
            _interns.Add(value);
            _interned[value] = index;
            return index;
        }

        public void Write(BinaryWriter writer)
        {
            foreach (var edge in _nodes)
            {
                Intern(edge.SourceKey);
                Intern(edge.TargetKey);
                Intern(edge.Kind);
            }

            writer.Write(_interns.Count);
            foreach (var value in _interns)
            {
                writer.Write(value);
            }

            writer.Write(_nodes.Count);
            foreach (var edge in _nodes)
            {
                writer.Write(Intern(edge.SourceKey));
                writer.Write(Intern(edge.TargetKey));
                writer.Write(Intern(edge.Kind));
            }
        }

        public static ImmutableArray<LuaWorkspaceExportEdge> ReadEdges(BinaryReader reader)
        {
            var internCount = reader.ReadInt32();
            if (internCount < 0 || internCount > 50_000_000)
            {
                throw new FormatException("Corrupt edge intern count.");
            }

            var interns = new string[internCount];
            for (var index = 0; index < internCount; index++)
            {
                interns[index] = reader.ReadString();
            }

            var count = reader.ReadInt32();
            if (count < 0 || count > 100_000_000)
            {
                throw new FormatException("Corrupt edge count.");
            }

            var nodes = new LuaWorkspaceExportEdge[count];
            string Intern(int index) => index >= 0 && index < interns.Length ? interns[index] : string.Empty;
            for (var index = 0; index < count; index++)
            {
                nodes[index] = new LuaWorkspaceExportEdge(
                    Intern(reader.ReadInt32()), Intern(reader.ReadInt32()), Intern(reader.ReadInt32()));
            }

            // The graph edge index array follows the edge table in the stream.
            var indexes = ReadInt32Array(reader);
            var builder = ImmutableArray.CreateBuilder<LuaWorkspaceExportEdge>(indexes.Length);
            foreach (var index in indexes)
            {
                if (index >= 0 && index < nodes.Length)
                {
                    builder.Add(nodes[index]);
                }
            }

            return builder.MoveToImmutable();
        }
    }

    private sealed class EdgeReferenceComparer : IEqualityComparer<LuaWorkspaceExportEdge>
    {
        public static readonly EdgeReferenceComparer Instance = new();

        public bool Equals(LuaWorkspaceExportEdge? x, LuaWorkspaceExportEdge? y) =>
            ReferenceEquals(x, y);

        public int GetHashCode(LuaWorkspaceExportEdge obj) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    private static ImmutableArray<int> ReadInt32Array(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > 100_000_000)
        {
            throw new FormatException("Corrupt index array count.");
        }

        var builder = ImmutableArray.CreateBuilder<int>(count);
        for (var index = 0; index < count; index++)
        {
            builder.Add(reader.ReadInt32());
        }

        return builder.MoveToImmutable();
    }

    private static void WriteInt32Array(BinaryWriter writer, ImmutableArray<int> values)
    {
        writer.Write(values.Length);
        foreach (var value in values)
        {
            writer.Write(value);
        }
    }

    /// <summary>
    /// Builds a reference-deduplicated, post-order table of type nodes: children always
    /// receive a lower index than their parents, so the reader resolves every forward
    /// reference eagerly. Shared subtrees (the common case across a corpus) serialize
    /// once.
    /// </summary>
    private sealed class TypeTableBuilder
    {
        private readonly Dictionary<LuaType, int> _indexes = new(ReferenceComparer.Instance);
        private readonly List<LuaType> _nodes = [];
        private int _depth;

        public IReadOnlyList<LuaType> Nodes => _nodes;

        /// <summary>Visits a type, returning its table index (-1 for null).</summary>
        public int Visit(LuaType? type)
        {
            if (type is null)
            {
                return -1;
            }

            if (_indexes.TryGetValue(type, out var existing))
            {
                return existing;
            }

            // Types are immutable records, so cycles cannot be constructed; the depth
            // cap is defense-in-depth against pathological nesting.
            if (_depth >= 256)
            {
                return Visit(LuaTypes.Any);
            }

            _depth++;
            try
            {
                switch (type)
                {
                    case LuaUnionType union:
                        foreach (var member in union.Types)
                        {
                            Visit(member);
                        }

                        break;
                    case LuaIntersectionType intersection:
                        foreach (var member in intersection.Types)
                        {
                            Visit(member);
                        }

                        break;
                    case LuaArrayType array:
                        Visit(array.ElementType);
                        break;
                    case LuaMapType map:
                        Visit(map.KeyType);
                        Visit(map.ValueType);
                        break;
                    case LuaStructuralTableType table:
                        foreach (var field in table.Fields)
                        {
                            Visit(field.KeyType);
                            Visit(field.ValueType);
                        }

                        Visit(table.ArrayElementType);
                        Visit(table.MapKeyType);
                        Visit(table.MapValueType);
                        break;
                    case LuaMetatableType metatable:
                        Visit(metatable.BaseType);
                        Visit(metatable.MetatableType);
                        break;
                    case LuaPrototypeType prototype:
                        Visit(prototype.Shape);
                        foreach (var baseType in prototype.BaseTypes)
                        {
                            Visit(baseType);
                        }

                        break;
                    case LuaTupleType tuple:
                        foreach (var element in tuple.Elements)
                        {
                            Visit(element);
                        }

                        break;
                    case LuaTypePack pack:
                        foreach (var element in pack.Head)
                        {
                            Visit(element);
                        }

                        Visit(pack.VariadicType);
                        break;
                    case LuaGenericParameterType genericParameter:
                        Visit(genericParameter.Constraint);
                        break;
                    case LuaGenericInstanceType genericInstance:
                        Visit(genericInstance.Definition);
                        foreach (var argument in genericInstance.TypeArguments)
                        {
                            Visit(argument);
                        }

                        break;
                    case LuaFunctionType function:
                        foreach (var parameter in function.Parameters)
                        {
                            Visit(parameter.Type);
                        }

                        Visit(function.Returns);
                        foreach (var typeParameter in function.TypeParameters)
                        {
                            Visit(typeParameter);
                        }

                        break;
                    case LuaOverloadType overload:
                        foreach (var signature in overload.Signatures)
                        {
                            Visit(signature);
                        }

                        break;
                    case LuaCallableType callable:
                        Visit(callable.ReceiverType);
                        foreach (var signature in callable.Signatures)
                        {
                            Visit(signature);
                        }

                        break;
                    case LuaClassType classType:
                        foreach (var argument in classType.TypeArguments)
                        {
                            Visit(argument);
                        }

                        break;
                    case LuaAliasType alias:
                        Visit(alias.Target);
                        break;
                    case LuaEnumType enumType:
                        Visit(enumType.UnderlyingType);
                        foreach (var member in enumType.Members)
                        {
                            Visit(member);
                        }

                        break;
                }
            }
            finally
            {
                _depth--;
            }

            var index = _nodes.Count;
            _nodes.Add(type);
            _indexes[type] = index;
            return index;
        }
    }

    private sealed class ReferenceComparer : IEqualityComparer<LuaType>
    {
        public static readonly ReferenceComparer Instance = new();

        public bool Equals(LuaType? x, LuaType? y) => ReferenceEquals(x, y);

        public int GetHashCode(LuaType obj) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    /// <summary>
    /// Distinguishes <see cref="LuaFunctionType"/> from the primitive function singleton,
    /// which share <see cref="LuaTypeKind.Function"/>.
    /// </summary>
    private const byte FunctionTypeTag = 0xFE;

    private static void WriteTypes(BinaryWriter writer, TypeTableBuilder types)
    {
        var nodes = types.Nodes;
        writer.Write(nodes.Count);
        foreach (var node in nodes)
        {
            writer.Write(node is LuaFunctionType ? FunctionTypeTag : (byte)node.Kind);
            switch (node)
            {
                case LuaPrimitiveType:
                    // Primitive kinds restore to the canonical singletons.
                    break;
                case LuaBooleanLiteralType booleanLiteral:
                    writer.Write((byte)0);
                    writer.Write(booleanLiteral.Value);
                    break;
                case LuaIntegerLiteralType integerLiteral:
                    writer.Write((byte)1);
                    writer.Write(integerLiteral.Value);
                    break;
                case LuaFloatLiteralType floatLiteral:
                    writer.Write((byte)2);
                    writer.Write(floatLiteral.Value);
                    break;
                case LuaStringLiteralType stringLiteral:
                    writer.Write((byte)3);
                    writer.Write(stringLiteral.Value.Length);
                    foreach (var value in stringLiteral.Value)
                    {
                        writer.Write(value);
                    }

                    break;
                case LuaUnionType union:
                    WriteTypeIndexes(writer, types, union.Types);
                    break;
                case LuaIntersectionType intersection:
                    WriteTypeIndexes(writer, types, intersection.Types);
                    break;
                case LuaArrayType array:
                    writer.Write(types.Visit(array.ElementType));
                    break;
                case LuaMapType map:
                    writer.Write(types.Visit(map.KeyType));
                    writer.Write(types.Visit(map.ValueType));
                    break;
                case LuaStructuralTableType table:
                    writer.Write(table.Fields.Length);
                    foreach (var field in table.Fields)
                    {
                        WriteNullableString(writer, field.Name);
                        writer.Write(types.Visit(field.KeyType));
                        writer.Write(types.Visit(field.ValueType));
                        writer.Write(field.IsOptional);
                        writer.Write(field.IsReadOnly);
                    }

                    writer.Write(types.Visit(table.ArrayElementType));
                    writer.Write(types.Visit(table.MapKeyType));
                    writer.Write(types.Visit(table.MapValueType));
                    writer.Write(table.IsOpen);
                    break;
                case LuaMetatableType metatable:
                    writer.Write(types.Visit(metatable.BaseType));
                    writer.Write(types.Visit(metatable.MetatableType));
                    writer.Write(metatable.IsPrecise);
                    break;
                case LuaPrototypeType prototype:
                    writer.Write(prototype.Name);
                    writer.Write(types.Visit(prototype.Shape));
                    WriteTypeIndexes(writer, types, prototype.BaseTypes);
                    writer.Write(prototype.UsesSelfIndex);
                    writer.Write(prototype.IsPrecise);
                    break;
                case LuaTupleType tuple:
                    WriteTypeIndexes(writer, types, tuple.Elements);
                    break;
                case LuaTypePack pack:
                    WriteTypeIndexes(writer, types, pack.Head);
                    writer.Write(types.Visit(pack.VariadicType));
                    break;
                case LuaGenericParameterType genericParameter:
                    writer.Write(genericParameter.Name);
                    writer.Write(genericParameter.Ordinal);
                    writer.Write(types.Visit(genericParameter.Constraint));
                    break;
                case LuaGenericInstanceType genericInstance:
                    writer.Write(types.Visit(genericInstance.Definition));
                    WriteTypeIndexes(writer, types, genericInstance.TypeArguments);
                    break;
                case LuaFunctionType function:
                    writer.Write(function.Parameters.Length);
                    foreach (var parameter in function.Parameters)
                    {
                        WriteNullableString(writer, parameter.Name);
                        writer.Write(types.Visit(parameter.Type));
                        writer.Write(parameter.IsOptional);
                        writer.Write(parameter.IsVararg);
                    }

                    writer.Write(types.Visit(function.Returns));
                    writer.Write(function.TypeParameters.Length);
                    foreach (var typeParameter in function.TypeParameters)
                    {
                        writer.Write(types.Visit(typeParameter));
                    }

                    writer.Write(function.HasImplicitSelf);
                    break;
                case LuaOverloadType overload:
                    WriteTypeIndexes(writer, types, [.. overload.Signatures]);
                    break;
                case LuaCallableType callable:
                    writer.Write(types.Visit(callable.ReceiverType));
                    WriteTypeIndexes(writer, types, [.. callable.Signatures]);
                    break;
                case LuaClassType classType:
                    writer.Write(classType.Name);
                    WriteTypeIndexes(writer, types, classType.TypeArguments);
                    break;
                case LuaAliasType alias:
                    writer.Write(alias.Name);
                    writer.Write(types.Visit(alias.Target));
                    break;
                case LuaEnumType enumType:
                    writer.Write(enumType.Name);
                    writer.Write(types.Visit(enumType.UnderlyingType));
                    WriteTypeIndexes(writer, types, [.. enumType.Members]);
                    break;
            }
        }
    }

    private static void WriteTypeIndexes(
        BinaryWriter writer,
        TypeTableBuilder types,
        ImmutableArray<LuaType> values)
    {
        writer.Write(values.Length);
        foreach (var value in values)
        {
            writer.Write(types.Visit(value));
        }
    }

    private static LuaType[] ReadTypes(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > 50_000_000)
        {
            throw new FormatException("Corrupt type table count.");
        }

        var nodes = new LuaType[count];
        LuaType Resolve(int index) =>
            index < 0 || index >= count ? LuaTypes.Any : nodes[index];

        for (var index = 0; index < count; index++)
        {
            var tag = reader.ReadByte();
            nodes[index] = tag switch
            {
                (byte)LuaTypeKind.Any => LuaTypes.Any,
                (byte)LuaTypeKind.Unknown => LuaTypes.Unknown,
                (byte)LuaTypeKind.Never => LuaTypes.Never,
                (byte)LuaTypeKind.Nil => LuaTypes.Nil,
                (byte)LuaTypeKind.Boolean => LuaTypes.Boolean,
                (byte)LuaTypeKind.Integer => LuaTypes.Integer,
                (byte)LuaTypeKind.Float => LuaTypes.Float,
                (byte)LuaTypeKind.Number => LuaTypes.Number,
                (byte)LuaTypeKind.String => LuaTypes.String,
                (byte)LuaTypeKind.Table => LuaTypes.Table,
                (byte)LuaTypeKind.Function => LuaTypes.Function,
                (byte)LuaTypeKind.Thread => LuaTypes.Thread,
                (byte)LuaTypeKind.Userdata => LuaTypes.Userdata,
                (byte)LuaTypeKind.Literal => ReadLiteral(reader),
                (byte)LuaTypeKind.Union => new LuaUnionType(ReadTypeArray(reader, Resolve)),
                (byte)LuaTypeKind.Intersection => new LuaIntersectionType(ReadTypeArray(reader, Resolve)),
                (byte)LuaTypeKind.Array => new LuaArrayType(Resolve(reader.ReadInt32())),
                (byte)LuaTypeKind.Map => new LuaMapType(
                                    Resolve(reader.ReadInt32()), Resolve(reader.ReadInt32())),
                (byte)LuaTypeKind.StructuralTable => ReadStructuralTable(reader, Resolve),
                (byte)LuaTypeKind.Metatable => new LuaMetatableType(
                                    Resolve(reader.ReadInt32()),
                                    Resolve(reader.ReadInt32()),
                                    reader.ReadBoolean()),
                (byte)LuaTypeKind.Prototype => new LuaPrototypeType(
                                    reader.ReadString(),
                                    Resolve(reader.ReadInt32()),
                                    ReadTypeArray(reader, Resolve),
                                    reader.ReadBoolean(),
                                    reader.ReadBoolean()),
                (byte)LuaTypeKind.Tuple => new LuaTupleType(ReadTypeArray(reader, Resolve)),
                (byte)LuaTypeKind.TypePack => ReadTypePack(reader, Resolve),
                (byte)LuaTypeKind.GenericParameter => new LuaGenericParameterType(
                                    reader.ReadString(),
                                    reader.ReadInt32(),
                                    ResolveOrNull(reader, Resolve)),
                (byte)LuaTypeKind.GenericInstance => new LuaGenericInstanceType(
                                    Resolve(reader.ReadInt32()),
                                    ReadTypeArray(reader, Resolve)),
                FunctionTypeTag => ReadFunctionType(reader, Resolve),
                (byte)LuaTypeKind.Overload => new LuaOverloadType(
                                    [.. ReadTypeArray(reader, Resolve).Select(static type => (LuaFunctionType)type)]),
                (byte)LuaTypeKind.Callable => new LuaCallableType(
                                    Resolve(reader.ReadInt32()),
                                    [.. ReadTypeArray(reader, Resolve).Select(static type => (LuaFunctionType)type)]),
                (byte)LuaTypeKind.Class => new LuaClassType(
                                    reader.ReadString(),
                                    ReadTypeArray(reader, Resolve)),
                (byte)LuaTypeKind.Alias => new LuaAliasType(
                                    reader.ReadString(),
                                    Resolve(reader.ReadInt32())),
                (byte)LuaTypeKind.Enum => new LuaEnumType(
                                    reader.ReadString(),
                                    Resolve(reader.ReadInt32()),
                                    [.. ReadTypeArray(reader, Resolve).Select(static type => (LuaLiteralType)type)]),
                _ => throw new FormatException($"Unknown type tag {tag}."),
            };
        }

        return nodes;

        static LuaType ReadLiteral(BinaryReader reader) => reader.ReadByte() switch
        {
            0 => new LuaBooleanLiteralType(reader.ReadBoolean()),
            1 => new LuaIntegerLiteralType(reader.ReadInt64()),
            2 => new LuaFloatLiteralType(reader.ReadDouble()),
            _ => ReadStringLiteral(reader),
        };

        static LuaStringLiteralType ReadStringLiteral(BinaryReader reader)
        {
            var length = reader.ReadInt32();
            if (length < 0 || length > 10_000_000)
            {
                throw new FormatException("Corrupt string literal length.");
            }

            var builder = ImmutableArray.CreateBuilder<byte>(length);
            for (var index = 0; index < length; index++)
            {
                builder.Add(reader.ReadByte());
            }

            return new LuaStringLiteralType(builder.MoveToImmutable());
        }

        static LuaStructuralTableType ReadStructuralTable(BinaryReader reader, Func<int, LuaType> resolve)
        {
            var fieldCount = reader.ReadInt32();
            if (fieldCount < 0 || fieldCount > 1_000_000)
            {
                throw new FormatException("Corrupt field count.");
            }

            var fields = ImmutableArray.CreateBuilder<LuaTableField>(fieldCount);
            for (var index = 0; index < fieldCount; index++)
            {
                var name = ReadNullableString(reader);
                var keyType = resolve(reader.ReadInt32());
                var valueType = resolve(reader.ReadInt32());
                var isOptional = reader.ReadBoolean();
                var isReadOnly = reader.ReadBoolean();
                fields.Add(new LuaTableField(name, keyType, valueType, isOptional, isReadOnly));
            }

            return new LuaStructuralTableType(
                fields.MoveToImmutable(),
                ResolveOrNull(reader, resolve),
                ResolveOrNull(reader, resolve),
                ResolveOrNull(reader, resolve),
                reader.ReadBoolean());
        }

        static LuaTypePack ReadTypePack(BinaryReader reader, Func<int, LuaType> resolve) =>
            new(ReadTypeArray(reader, resolve), ResolveOrNull(reader, resolve));

        static LuaFunctionType ReadFunctionType(BinaryReader reader, Func<int, LuaType> resolve)
        {
            var parameterCount = reader.ReadInt32();
            if (parameterCount < 0 || parameterCount > 1_000_000)
            {
                throw new FormatException("Corrupt parameter count.");
            }

            var parameters = ImmutableArray.CreateBuilder<LuaFunctionParameter>(parameterCount);
            for (var index = 0; index < parameterCount; index++)
            {
                var name = ReadNullableString(reader);
                var type = resolve(reader.ReadInt32());
                parameters.Add(new LuaFunctionParameter(name, type, reader.ReadBoolean(), reader.ReadBoolean()));
            }

            var returns = (LuaTypePack)resolve(reader.ReadInt32());
            var typeParameterCount = reader.ReadInt32();
            if (typeParameterCount < 0 || typeParameterCount > 1_000)
            {
                throw new FormatException("Corrupt type parameter count.");
            }

            var typeParameters = ImmutableArray.CreateBuilder<LuaGenericParameterType>(typeParameterCount);
            for (var index = 0; index < typeParameterCount; index++)
            {
                typeParameters.Add((LuaGenericParameterType)resolve(reader.ReadInt32()));
            }

            return new LuaFunctionType(
                parameters.MoveToImmutable(), returns, typeParameters.MoveToImmutable(), reader.ReadBoolean());
        }
    }

    private static LuaType? ResolveOrNull(BinaryReader reader, Func<int, LuaType> resolve)
    {
        var index = reader.ReadInt32();
        return index < 0 ? null : resolve(index);
    }

    private static ImmutableArray<LuaType> ReadTypeArray(BinaryReader reader, Func<int, LuaType> resolve)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > 10_000_000)
        {
            throw new FormatException("Corrupt type array count.");
        }

        var builder = ImmutableArray.CreateBuilder<LuaType>(count);
        for (var index = 0; index < count; index++)
        {
            builder.Add(resolve(reader.ReadInt32()));
        }

        return builder.MoveToImmutable();
    }

    private static void WriteNullableString(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
        {
            writer.Write(value);
        }
    }

    private static string? ReadNullableString(BinaryReader reader) =>
        reader.ReadBoolean() ? reader.ReadString() : null;

    private static void WriteSpan(BinaryWriter writer, TextSpan span)
    {
        writer.Write(span.Start);
        writer.Write(span.Length);
    }

    private static TextSpan ReadSpan(BinaryReader reader) =>
        new(reader.ReadInt32(), reader.ReadInt32());

    private static void WriteStrings(BinaryWriter writer, ImmutableArray<string> strings)
    {
        writer.Write(strings.Length);
        foreach (var value in strings)
        {
            writer.Write(value);
        }
    }

    private static ImmutableArray<string> ReadStrings(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > 100_000_000)
        {
            throw new FormatException("Corrupt string pool count.");
        }

        var builder = ImmutableArray.CreateBuilder<string>(count);
        for (var index = 0; index < count; index++)
        {
            builder.Add(reader.ReadString());
        }

        return builder.MoveToImmutable();
    }

    private static void WriteModules(
        BinaryWriter writer,
        ImmutableArray<LuaWorkspaceCompactModule> modules,
        SymbolTableBuilder symbols)
    {
        // Modules reference the same symbol records as the export graph; persist
        // table indexes instead of duplicating the payloads.
        writer.Write(modules.Length);
        foreach (var module in modules)
        {
            writer.Write(module.Identity.Name);
            writer.Write(module.SourceIdentity);
            writer.Write(module.ContentHash);
            writer.Write(module.ExportHash);
            writer.Write(module.ExportSymbolHash);
            writer.Write(module.FunctionSummaryHash);
            writer.Write(module.DependencySummaryHash);
            writer.Write(module.ExportedSymbols.Length);
            foreach (var symbol in module.ExportedSymbols)
            {
                writer.Write(symbols.Visit(symbol));
            }
        }
    }

    private static ImmutableArray<LuaWorkspaceCompactModule> ReadModules(
        BinaryReader reader,
        LuaWorkspaceExportSymbol[] symbols)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > 10_000_000)
        {
            throw new FormatException("Corrupt module count.");
        }

        var builder = ImmutableArray.CreateBuilder<LuaWorkspaceCompactModule>(count);
        for (var index = 0; index < count; index++)
        {
            var name = reader.ReadString();
            var sourceIdentity = reader.ReadString();
            var contentHash = reader.ReadString();
            var exportHash = reader.ReadString();
            var exportSymbolHash = reader.ReadString();
            var functionSummaryHash = reader.ReadString();
            var dependencySummaryHash = reader.ReadString();
            var symbolCount = reader.ReadInt32();
            if (symbolCount < 0 || symbolCount > 10_000_000)
            {
                throw new FormatException("Corrupt module symbol count.");
            }

            var symbolBuilder = ImmutableArray.CreateBuilder<LuaWorkspaceExportSymbol>(symbolCount);
            for (var symbolIndex = 0; symbolIndex < symbolCount; symbolIndex++)
            {
                var indexIntoSymbols = reader.ReadInt32();
                if (indexIntoSymbols >= 0 && indexIntoSymbols < symbols.Length)
                {
                    symbolBuilder.Add(symbols[indexIntoSymbols]);
                }
            }

            builder.Add(new LuaWorkspaceCompactModule(
                new LuaModuleIdentity(name),
                sourceIdentity,
                contentHash,
                exportHash,
                exportSymbolHash,
                functionSummaryHash,
                dependencySummaryHash,
                symbolBuilder.MoveToImmutable()));
        }

        return builder.MoveToImmutable();
    }

    private static void WriteDependency(BinaryWriter writer, LuaModuleDependency dependency)
    {
        writer.Write(dependency.Source.Name);
        writer.Write(dependency.RequestedName);
        WriteNullableString(writer, dependency.Target?.Name);
        writer.Write((byte)dependency.Kind);
        WriteSpan(writer, dependency.Span);
    }

    private static LuaModuleDependency ReadDependency(BinaryReader reader)
    {
        var source = reader.ReadString();
        var requestedName = reader.ReadString();
        var target = ReadNullableString(reader);
        var kind = (LuaModuleDependencyKind)reader.ReadByte();
        var span = ReadSpan(reader);
        return new LuaModuleDependency(
            new LuaModuleIdentity(source),
            requestedName,
            target is null ? null : new LuaModuleIdentity(target),
            kind,
            span);
    }

    private static void WriteGraph(BinaryWriter writer, LuaModuleGraph graph)
    {
        writer.Write(graph.Nodes.Length);
        foreach (var node in graph.Nodes)
        {
            writer.Write(node.Identity.Name);
            writer.Write(node.SourceIdentity);
            writer.Write(node.ContentHash);
            writer.Write(node.Dependencies.Length);
            foreach (var dependency in node.Dependencies)
            {
                WriteDependency(writer, dependency);
            }
        }

        writer.Write(graph.Dependencies.Length);
        foreach (var dependency in graph.Dependencies)
        {
            WriteDependency(writer, dependency);
        }

        writer.Write(graph.Components.Length);
        foreach (var component in graph.Components)
        {
            writer.Write(component.Id);
            writer.Write(component.Modules.Length);
            foreach (var module in component.Modules)
            {
                writer.Write(module.Name);
            }

            writer.Write(component.IsCyclic);
        }
    }

    private static LuaModuleGraph ReadGraph(BinaryReader reader)
    {
        var nodeCount = reader.ReadInt32();
        if (nodeCount < 0 || nodeCount > 10_000_000)
        {
            throw new FormatException("Corrupt graph node count.");
        }

        var nodes = ImmutableArray.CreateBuilder<LuaModuleNode>(nodeCount);
        for (var index = 0; index < nodeCount; index++)
        {
            var name = reader.ReadString();
            var sourceIdentity = reader.ReadString();
            var contentHash = reader.ReadString();
            var dependencyCount = reader.ReadInt32();
            if (dependencyCount < 0 || dependencyCount > 100_000_000)
            {
                throw new FormatException("Corrupt node dependency count.");
            }

            var dependencies = ImmutableArray.CreateBuilder<LuaModuleDependency>(dependencyCount);
            for (var dependencyIndex = 0; dependencyIndex < dependencyCount; dependencyIndex++)
            {
                dependencies.Add(ReadDependency(reader));
            }

            nodes.Add(new LuaModuleNode(
                new LuaModuleIdentity(name), sourceIdentity, contentHash, dependencies.MoveToImmutable()));
        }

        var orderedCount = reader.ReadInt32();
        if (orderedCount < 0 || orderedCount > 100_000_000)
        {
            throw new FormatException("Corrupt graph dependency count.");
        }

        var ordered = ImmutableArray.CreateBuilder<LuaModuleDependency>(orderedCount);
        for (var index = 0; index < orderedCount; index++)
        {
            ordered.Add(ReadDependency(reader));
        }

        var componentCount = reader.ReadInt32();
        if (componentCount < 0 || componentCount > 10_000_000)
        {
            throw new FormatException("Corrupt component count.");
        }

        var components = ImmutableArray.CreateBuilder<LuaModuleStronglyConnectedComponent>(componentCount);
        for (var index = 0; index < componentCount; index++)
        {
            var id = reader.ReadInt32();
            var moduleCount = reader.ReadInt32();
            if (moduleCount < 0 || moduleCount > 10_000_000)
            {
                throw new FormatException("Corrupt component module count.");
            }

            var modules = ImmutableArray.CreateBuilder<LuaModuleIdentity>(moduleCount);
            for (var moduleIndex = 0; moduleIndex < moduleCount; moduleIndex++)
            {
                modules.Add(new LuaModuleIdentity(reader.ReadString()));
            }

            components.Add(new LuaModuleStronglyConnectedComponent(
                id, modules.MoveToImmutable(), reader.ReadBoolean()));
        }

        return new LuaModuleGraph(nodes.MoveToImmutable(), ordered.MoveToImmutable(),
            components.MoveToImmutable());
    }

    private static void WriteCallBindings(
        BinaryWriter writer,
        ImmutableArray<LuaWorkspaceModuleCallBinding> calls)
    {
        writer.Write(calls.Length);
        foreach (var call in calls)
        {
            writer.Write(call.SourceModuleName);
            WriteSpan(writer, call.Span);
            writer.Write(call.ContainingFunctionId);
            writer.Write(call.RequestedModuleName);
            writer.Write(call.MemberPath);
            WriteNullableString(writer, call.TargetSymbolKey);
            WriteNullableString(writer, call.TargetFunctionKey);
            writer.Write(call.CandidateKeys.Length);
            foreach (var key in call.CandidateKeys)
            {
                writer.Write(key);
            }

            writer.Write((byte)call.Status);
            WriteNullableString(writer, call.Reason);
            var hasDefinition = call.DefinitionSpan is { };
            writer.Write(hasDefinition);
            if (hasDefinition)
            {
                WriteSpan(writer, call.DefinitionSpan!.Value);
            }
        }
    }

    private static ImmutableArray<LuaWorkspaceModuleCallBinding> ReadCallBindings(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > 100_000_000)
        {
            throw new FormatException("Corrupt call binding count.");
        }

        var builder = ImmutableArray.CreateBuilder<LuaWorkspaceModuleCallBinding>(count);
        for (var index = 0; index < count; index++)
        {
            var sourceModuleName = reader.ReadString();
            var span = ReadSpan(reader);
            var containingFunctionId = reader.ReadInt32();
            var requestedModuleName = reader.ReadString();
            var memberPath = reader.ReadString();
            var targetSymbolKey = ReadNullableString(reader);
            var targetFunctionKey = ReadNullableString(reader);
            var candidateCount = reader.ReadInt32();
            if (candidateCount < 0 || candidateCount > 1_000_000)
            {
                throw new FormatException("Corrupt candidate count.");
            }

            var candidates = ImmutableArray.CreateBuilder<string>(candidateCount);
            for (var candidateIndex = 0; candidateIndex < candidateCount; candidateIndex++)
            {
                candidates.Add(reader.ReadString());
            }

            var status = (LuaWorkspaceBindingStatus)reader.ReadByte();
            var reason = ReadNullableString(reader);
            var hasDefinition = reader.ReadBoolean();
            var definitionSpan = hasDefinition ? ReadSpan(reader) : (TextSpan?)null;
            builder.Add(new LuaWorkspaceModuleCallBinding(
                sourceModuleName, span, containingFunctionId, requestedModuleName, memberPath,
                targetSymbolKey, targetFunctionKey, candidates.MoveToImmutable(), status, reason,
                definitionSpan, null, null));
        }

        return builder.MoveToImmutable();
    }

    private static void WriteReferences(
        BinaryWriter writer,
        ImmutableArray<CompactReference> references)
    {
        writer.Write(references.Length);
        foreach (var reference in references)
        {
            writer.Write(reference.ModuleIndex);
            WriteSpan(writer, reference.Span);
            writer.Write(reference.ContainingFunctionId);
            writer.Write(reference.ContainingFunctionKeyIndex);
            writer.Write(reference.NameIndex);
            writer.Write(reference.IsWrite);
            writer.Write((byte)reference.ResolutionKind);
            writer.Write(reference.TargetKeyIndex);
        }
    }

    private static ImmutableArray<CompactReference> ReadReferences(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > 200_000_000)
        {
            throw new FormatException("Corrupt reference count.");
        }

        var builder = ImmutableArray.CreateBuilder<CompactReference>(count);
        for (var index = 0; index < count; index++)
        {
            builder.Add(new CompactReference(
                reader.ReadInt32(),
                ReadSpan(reader),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                reader.ReadBoolean(),
                (LuaNameResolutionKind)reader.ReadByte(),
                reader.ReadInt32()));
        }

        return builder.MoveToImmutable();
    }

    private static void WriteMemberReferences(
        BinaryWriter writer,
        ImmutableArray<CompactMemberReference> references)
    {
        writer.Write(references.Length);
        foreach (var reference in references)
        {
            writer.Write(reference.ModuleIndex);
            WriteSpan(writer, reference.Span);
            writer.Write(reference.NameIndex);
        }
    }

    private static ImmutableArray<CompactMemberReference> ReadMemberReferences(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > 200_000_000)
        {
            throw new FormatException("Corrupt member reference count.");
        }

        var builder = ImmutableArray.CreateBuilder<CompactMemberReference>(count);
        for (var index = 0; index < count; index++)
        {
            builder.Add(new CompactMemberReference(
                reader.ReadInt32(), ReadSpan(reader), reader.ReadInt32()));
        }

        return builder.MoveToImmutable();
    }

    private static void WriteAnnotationReferences(
        BinaryWriter writer,
        ImmutableArray<CompactAnnotationReference> references)
    {
        writer.Write(references.Length);
        foreach (var reference in references)
        {
            writer.Write(reference.ModuleIndex);
            WriteSpan(writer, reference.Span);
        }
    }

    private static ImmutableArray<CompactAnnotationReference> ReadAnnotationReferences(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > 200_000_000)
        {
            throw new FormatException("Corrupt annotation reference count.");
        }

        var builder = ImmutableArray.CreateBuilder<CompactAnnotationReference>(count);
        for (var index = 0; index < count; index++)
        {
            builder.Add(new CompactAnnotationReference(reader.ReadInt32(), ReadSpan(reader)));
        }

        return builder.MoveToImmutable();
    }

    private static void WriteIntIndexes(
        BinaryWriter writer,
        ImmutableArray<ImmutableDictionary<string, ImmutableArray<int>>> shards)
    {
        var entries = shards.SelectMany(static shard => shard).ToArray();
        writer.Write(entries.Length);
        foreach (var (key, values) in entries)
        {
            writer.Write(key);
            writer.Write(values.Length);
            foreach (var value in values)
            {
                writer.Write(value);
            }
        }
    }

    private static ImmutableArray<ImmutableDictionary<string, ImmutableArray<int>>> ReadIntIndexes(
        BinaryReader reader,
        int shardCount)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > 100_000_000)
        {
            throw new FormatException("Corrupt index entry count.");
        }

        var entries = ImmutableDictionary.CreateBuilder<string, ImmutableArray<int>>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var key = reader.ReadString();
            var valueCount = reader.ReadInt32();
            if (valueCount < 0 || valueCount > 200_000_000)
            {
                throw new FormatException("Corrupt index value count.");
            }

            var values = ImmutableArray.CreateBuilder<int>(valueCount);
            for (var valueIndex = 0; valueIndex < valueCount; valueIndex++)
            {
                values.Add(reader.ReadInt32());
            }

            entries[key] = values.MoveToImmutable();
        }

        return CreateShards(entries.ToImmutable(), shardCount);
    }

    private static void WriteStringIndexes(
        BinaryWriter writer,
        ImmutableArray<ImmutableDictionary<string, ImmutableArray<string>>> shards)
    {
        var entries = shards.SelectMany(static shard => shard).ToArray();
        writer.Write(entries.Length);
        foreach (var (key, values) in entries)
        {
            writer.Write(key);
            writer.Write(values.Length);
            foreach (var value in values)
            {
                writer.Write(value);
            }
        }
    }

    private static ImmutableArray<ImmutableDictionary<string, ImmutableArray<string>>> ReadStringIndexes(
        BinaryReader reader,
        int shardCount)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > 100_000_000)
        {
            throw new FormatException("Corrupt index entry count.");
        }

        var entries = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var key = reader.ReadString();
            var valueCount = reader.ReadInt32();
            if (valueCount < 0 || valueCount > 100_000_000)
            {
                throw new FormatException("Corrupt index value count.");
            }

            var values = ImmutableArray.CreateBuilder<string>(valueCount);
            for (var valueIndex = 0; valueIndex < valueCount; valueIndex++)
            {
                values.Add(reader.ReadString());
            }

            entries[key] = values.MoveToImmutable();
        }

        return CreateShards(entries.ToImmutable(), shardCount);
    }

    private void WriteContributions(
        BinaryWriter writer,
        KeyValuePair<string, ModuleContribution>[] ordered,
        TypeTableBuilder types,
        SymbolTableBuilder symbols,
        EdgeTableBuilder edges)
    {
        // The snapshot's reference arrays are appended per module (AddModule /
        // ReuseModule append one contiguous run each), so each contribution's
        // segments are (start, count) runs keyed by the module's index.
        var referenceRuns = BuildRuns(_references, static item => item.ModuleIndex);
        var memberRuns = BuildRuns(_memberReferences, static item => item.ModuleIndex);
        var annotationRuns = BuildRuns(_annotationReferences, static item => item.ModuleIndex);
        var moduleIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < Modules.Length; index++)
        {
            moduleIndexes.TryAdd(Modules[index].Identity.Name, index);
        }

        writer.Write(ordered.Length);
        foreach (var (_, contribution) in ordered)
        {
            writer.Write(contribution.ModuleName);
            writer.Write(contribution.CacheKey);
            writer.Write(moduleIndexes.GetValueOrDefault(contribution.ModuleName, -1));
            writer.Write(types.Visit(contribution.ExportedType));
            WriteStringDictionary(writer, contribution.ExportSummaryHashes);
            WriteStringDictionary(writer, contribution.FunctionSummaryHashes);
            writer.Write(contribution.HostSummaryHash);
            writer.Write(contribution.AnalysisSummaryHash);

            WriteStringArray(writer, contribution.Names);
            writer.Write(contribution.NameIndexes.Length);
            foreach (var value in contribution.NameIndexes)
            {
                writer.Write(value);
            }

            var moduleIndex = moduleIndexes.GetValueOrDefault(contribution.ModuleName, -1);
            WriteRun(writer, referenceRuns, moduleIndex);
            WriteRun(writer, memberRuns, moduleIndex);
            WriteRun(writer, annotationRuns, moduleIndex);
            WriteStringArray(writer, contribution.AnnotationNames);

            writer.Write(contribution.Symbols.Length);
            foreach (var symbol in contribution.Symbols)
            {
                writer.Write(symbols.Visit(symbol));
            }

            writer.Write(contribution.Edges.Length);
            foreach (var edge in contribution.Edges)
            {
                writer.Write(edges.Visit(edge));
            }

            WriteCallBindings(writer, contribution.RawCalls);
            writer.Write(contribution.CallCount);
            WriteNullableString(writer, contribution.ReExportTarget);
            writer.Write(contribution.CompilationDiagnostics.Length);
            foreach (var diagnostic in contribution.CompilationDiagnostics)
            {
                writer.Write((byte)diagnostic.Phase);
                writer.Write((byte)diagnostic.Severity);
                WriteSpan(writer, diagnostic.Span);
                writer.Write(diagnostic.Code);
                writer.Write(diagnostic.Message);
            }
        }
    }

    private static ImmutableDictionary<string, ModuleContribution> ReadContributions(
        BinaryReader reader,
        LuaType[] types,
        ImmutableArray<LuaWorkspaceCompactModule> modules,
        ImmutableArray<CompactReference> references,
        ImmutableArray<CompactMemberReference> memberReferences,
        ImmutableArray<CompactAnnotationReference> annotationReferences,
        LuaWorkspaceExportSymbol[] symbolTable,
        ImmutableArray<LuaWorkspaceExportEdge> edgeTable)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > 10_000_000)
        {
            throw new FormatException("Corrupt contribution count.");
        }

        var builder = ImmutableDictionary.CreateBuilder<string, ModuleContribution>(StringComparer.Ordinal);
        for (var entryIndex = 0; entryIndex < count; entryIndex++)
        {
            var moduleName = reader.ReadString();
            var cacheKey = reader.ReadString();
            var moduleIndex = reader.ReadInt32();
            var exportedTypeIndex = reader.ReadInt32();
            var exportedType = exportedTypeIndex >= 0 && exportedTypeIndex < types.Length
                ? types[exportedTypeIndex]
                : LuaTypes.Any;
            var exportHashes = ReadStringDictionary(reader);
            var functionHashes = ReadStringDictionary(reader);
            var hostHash = reader.ReadString();
            var analysisHash = reader.ReadString();
            var names = ReadStringArray(reader);
            var nameIndexCount = reader.ReadInt32();
            if (nameIndexCount < 0 || nameIndexCount > 50_000_000)
            {
                throw new FormatException("Corrupt name index count.");
            }

            var nameIndexes = ImmutableArray.CreateBuilder<int>(nameIndexCount);
            for (var index = 0; index < nameIndexCount; index++)
            {
                nameIndexes.Add(reader.ReadInt32());
            }

            var referenceCount = ReadCount(reader);
            var referenceStart = reader.ReadInt32();
            var memberCount = ReadCount(reader);
            var memberStart = reader.ReadInt32();
            var annotationCount = ReadCount(reader);
            var annotationStart = reader.ReadInt32();
            var annotationNames = ReadStringArray(reader);

            var symbolCount = ReadCount(reader);
            var symbols = ImmutableArray.CreateBuilder<LuaWorkspaceExportSymbol>(symbolCount);
            for (var index = 0; index < symbolCount; index++)
            {
                var symbolTableIndex = reader.ReadInt32();
                if (symbolTableIndex >= 0 && symbolTableIndex < symbolTable.Length)
                {
                    symbols.Add(symbolTable[symbolTableIndex]);
                }
            }

            var edgeCount = ReadCount(reader);
            var edges = ImmutableArray.CreateBuilder<LuaWorkspaceExportEdge>(edgeCount);
            for (var index = 0; index < edgeCount; index++)
            {
                var edgeTableIndex = reader.ReadInt32();
                if (edgeTableIndex >= 0 && edgeTableIndex < edgeTable.Length)
                {
                    edges.Add(edgeTable[edgeTableIndex]);
                }
            }

            var rawCalls = ReadCallBindings(reader);
            var callCount = reader.ReadInt32();
            var reExportTarget = ReadNullableString(reader);
            var diagnosticCount = ReadCount(reader);
            var diagnostics = ImmutableArray.CreateBuilder<Lunil.Compiler.LuaCompilationDiagnostic>(diagnosticCount);
            for (var index = 0; index < diagnosticCount; index++)
            {
                var phase = (Lunil.Compiler.LuaCompilationPhase)reader.ReadByte();
                var severity = (DiagnosticSeverity)reader.ReadByte();
                var span = ReadSpan(reader);
                var code = reader.ReadString();
                var message = reader.ReadString();
                diagnostics.Add(new Lunil.Compiler.LuaCompilationDiagnostic(
                    phase,
                    new Diagnostic(code, severity, span, message)));
            }

            var module = moduleIndex >= 0 && moduleIndex < modules.Length
                ? modules[moduleIndex]
                : new LuaWorkspaceCompactModule(
                    new LuaModuleIdentity(moduleName), moduleName, string.Empty, string.Empty,
                    string.Empty, string.Empty, string.Empty, []);
            var referencesSegment = SliceRange(references, referenceStart, referenceCount);
            var memberSegment = SliceRange(memberReferences, memberStart, memberCount);
            var annotationSegment = SliceRange(annotationReferences, annotationStart, annotationCount);

            builder[moduleName] = new ModuleContribution(
                moduleName,
                cacheKey,
                module,
                exportedType,
                exportHashes,
                functionHashes,
                hostHash,
                analysisHash,
                names,
                nameIndexes.MoveToImmutable(),
                referencesSegment,
                memberSegment,
                annotationSegment,
                annotationNames,
                symbols.MoveToImmutable(),
                edges.MoveToImmutable(),
                rawCalls,
                callCount,
                reExportTarget,
                diagnostics.MoveToImmutable());
        }

        return builder.ToImmutable();
    }

    private static int ReadCount(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > 200_000_000)
        {
            throw new FormatException("Corrupt segment count.");
        }

        return count;
    }

    private static ImmutableArray<T> SliceRange<T>(ImmutableArray<T> values, int start, int count)
    {
        if (start < 0 || count <= 0 || start + count > values.Length)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<T>(count);
        for (var index = 0; index < count; index++)
        {
            builder.Add(values[start + index]);
        }

        return builder.MoveToImmutable();
    }

    private static Dictionary<int, (int Start, int Count)> BuildRuns<T>(
        ImmutableArray<T> values,
        Func<T, int> moduleIndexOf)
    {
        var runs = new Dictionary<int, (int Start, int Count)>();
        var index = 0;
        while (index < values.Length)
        {
            var start = index;
            var module = moduleIndexOf(values[index]);
            while (index < values.Length && moduleIndexOf(values[index]) == module)
            {
                index++;
            }

            runs[module] = (start, index - start);
        }

        return runs;
    }

    private static void WriteRun(
        BinaryWriter writer,
        Dictionary<int, (int Start, int Count)> runs,
        int moduleIndex)
    {
        if (runs.TryGetValue(moduleIndex, out var run))
        {
            writer.Write(run.Count);
            writer.Write(run.Start);
        }
        else
        {
            writer.Write(0);
            writer.Write(-1);
        }
    }

    private static void WriteStringDictionary(
        BinaryWriter writer,
        ImmutableDictionary<string, string> values)
    {
        var entries = values.OrderBy(static pair => pair.Key, StringComparer.Ordinal).ToArray();
        writer.Write(entries.Length);
        foreach (var (key, value) in entries)
        {
            writer.Write(key);
            writer.Write(value);
        }
    }

    private static ImmutableDictionary<string, string> ReadStringDictionary(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > 10_000_000)
        {
            throw new FormatException("Corrupt string dictionary count.");
        }

        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            builder[reader.ReadString()] = reader.ReadString();
        }

        return builder.ToImmutable();
    }

    private static void WriteStringArray(BinaryWriter writer, ImmutableArray<string> values)
    {
        writer.Write(values.Length);
        foreach (var value in values)
        {
            writer.Write(value);
        }
    }

    private static ImmutableArray<string> ReadStringArray(BinaryReader reader)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > 50_000_000)
        {
            throw new FormatException("Corrupt string array count.");
        }

        var builder = ImmutableArray.CreateBuilder<string>(count);
        for (var index = 0; index < count; index++)
        {
            builder.Add(reader.ReadString());
        }

        return builder.MoveToImmutable();
    }

    internal sealed class StreamingBuilder
    {
        private readonly int _shardCount;
        private readonly LuaHostAnalysisContract? _hostContract;
        private readonly LuaWorkspaceStringInterner? _interner;
        private readonly StringPool _strings;
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
        /// <summary>Annotation names parallel to <see cref="_annotationReferences"/>, for contribution reuse.</summary>
        private readonly List<string> _annotationNames = [];
        private readonly Dictionary<string, string> _reExports = new(StringComparer.Ordinal);
        private int _totalCallCount;

        public StreamingBuilder(
            int shardCount,
            LuaHostAnalysisContract? hostContract,
            LuaWorkspaceStringInterner? interner = null)
        {
            _shardCount = shardCount;
            _hostContract = hostContract;
            _interner = interner;
            _strings = new StringPool(interner);
        }

        public LuaWorkspaceCompactSnapshot? Snapshot { get; private set; }

        /// <summary>Pending per-module segments, materialized into contributions at Build.</summary>
        private readonly List<PendingContribution> _pending = [];

        /// <summary>Contributions merged from the previous snapshot, carried forward at Build.</summary>
        private readonly List<LuaWorkspaceCompactSnapshot.ModuleContribution> _reused = [];

        public void AddModule(LuaWorkspaceModuleResult module, string cacheKey)
        {
            var moduleIndex = _modules.Count;
            _strings.BeginCapture();
            _modules.Add(new LuaWorkspaceCompactModule(
                module.Identity,
                module.SourceIdentity,
                module.ContentHash,
                module.ExportHash,
                module.ExportSymbolHash,
                module.FunctionSummaryHash,
                module.DependencySummaryHash,
                module.ExportedSymbols));
            var referencesStart = _references.Count;
            var memberReferencesStart = _memberReferences.Count;
            var annotationReferencesStart = _annotationReferences.Count;
            AddReferences(module, moduleIndex);
            AddAnnotationReferences(module, moduleIndex);
            var (nameIndexes, names) = _strings.EndCapture();
            var callCount = module.Compilation.Analysis.CallGraph.Edges.Length;
            _totalCallCount += callCount;
            var symbolsStart = _symbols.Count;
            var edgesStart = _edges.Count;
            var callsStart = _rawCalls.Count;
            var projection = WorkspaceSymbolGraphBuilder.Build([module], _hostContract);
            // Fresh projections emit their own string instances; interning routes them
            // onto the canonical instances older snapshots and indexes already share,
            // so a rebuild cannot strand a second copy of the same keys and names.
            _symbols.AddRange(projection.Exports.Symbols
                .Where(static symbol => !symbol.IsExternal)
                .Select(InternSymbol));
            _edges.AddRange(projection.Exports.Edges
                .Where(static edge => edge.Kind != "re-export")
                .Select(InternEdge));
            _rawCalls.AddRange(projection.Calls.Edges.Select(InternCall));
            string? reExportTarget = null;
            if (WorkspaceSymbolGraphBuilder.GetDirectReExportTarget(module) is { } target)
            {
                reExportTarget = target;
                _reExports[module.Identity.Name] = target;
            }

            _pending.Add(new PendingContribution(
                module.Identity.Name,
                cacheKey,
                moduleIndex,
                module.ExportedType,
                module.ExportSummaryHashes,
                module.FunctionSummaryHashes,
                module.HostSummaryHash,
                module.AnalysisSummaryHash,
                names,
                nameIndexes,
                _references.Count - referencesStart,
                referencesStart,
                _memberReferences.Count - memberReferencesStart,
                memberReferencesStart,
                _annotationReferences.Count - annotationReferencesStart,
                annotationReferencesStart,
                symbolsStart,
                _symbols.Count - symbolsStart,
                edgesStart,
                _edges.Count - edgesStart,
                callsStart,
                _rawCalls.Count - callsStart,
                callCount,
                reExportTarget,
                [.. module.Compilation.Diagnostics]));
        }

        /// <summary>
        /// Merges a previously captured contribution without a compiler model: names are
        /// re-interned into this snapshot's pool, reference structs are re-indexed, and
        /// the module's symbol-graph output is re-appended for the Build-time fixups
        /// (re-export marking and call resolution) to redo against the fresh universe.
        /// </summary>
        public void ReuseModule(LuaWorkspaceCompactSnapshot.ModuleContribution contribution)
        {
            var moduleIndex = _modules.Count;
            _modules.Add(contribution.Module);
            _reused.Add(contribution);
            var remap = new Dictionary<int, int>(contribution.Names.Length);
            var values = new Dictionary<int, string>(contribution.Names.Length);
            for (var index = 0; index < contribution.Names.Length; index++)
            {
                var oldIndex = contribution.NameIndexes[index];
                values[oldIndex] = contribution.Names[index];
                remap[oldIndex] = _strings.GetOrAdd(contribution.Names[index]);
            }

            foreach (var reference in contribution.References)
            {
                var index = _references.Count;
                _references.Add(reference with
                {
                    ModuleIndex = moduleIndex,
                    ContainingFunctionKeyIndex = remap[reference.ContainingFunctionKeyIndex],
                    NameIndex = remap[reference.NameIndex],
                    TargetKeyIndex = reference.TargetKeyIndex < 0 ? -1 : remap[reference.TargetKeyIndex],
                });
                if (reference.TargetKeyIndex >= 0)
                {
                    GetIndexBuilder(_targetIndexes, values[reference.TargetKeyIndex]).Add(index);
                }

                if (reference.ResolutionKind == LuaNameResolutionKind.Global)
                {
                    GetIndexBuilder(_globals, values[reference.NameIndex]).Add(index);
                }
            }

            foreach (var reference in contribution.MemberReferences)
            {
                var index = _memberReferences.Count;
                _memberReferences.Add(reference with
                {
                    ModuleIndex = moduleIndex,
                    NameIndex = remap[reference.NameIndex],
                });
                GetIndexBuilder(_memberIndexes, values[reference.NameIndex]).Add(index);
            }

            for (var index = 0; index < contribution.AnnotationReferences.Length; index++)
            {
                var annotationIndex = _annotationReferences.Count;
                var reference = contribution.AnnotationReferences[index];
                _annotationReferences.Add(reference with { ModuleIndex = moduleIndex });
                // The names list must stay parallel to the references list: a later
                // AddModule captures its segment start on _annotationReferences, and
                // MaterializeContributions reads the same range from _annotationNames.
                // Skipping this append desynchronized the two lists and made the
                // GetRange below throw "offset and length were out of bounds" on any
                // round that mixed reused and freshly analyzed modules.
                _annotationNames.Add(contribution.AnnotationNames[index]);
                GetIndexBuilder(_annotationIndexes, contribution.AnnotationNames[index]).Add(annotationIndex);
            }

            _totalCallCount += contribution.CallCount;
            _symbols.AddRange(contribution.Symbols);
            _edges.AddRange(contribution.Edges);
            _rawCalls.AddRange(contribution.RawCalls);
            if (contribution.ReExportTarget is { } target)
            {
                _reExports[contribution.ModuleName] = target;
            }
        }

        private sealed record PendingContribution(
            string ModuleName,
            string CacheKey,
            int ModuleIndex,
            Lunil.Analysis.LuaType ExportedType,
            ImmutableDictionary<string, string> ExportSummaryHashes,
            ImmutableDictionary<string, string> FunctionSummaryHashes,
            string HostSummaryHash,
            string AnalysisSummaryHash,
            ImmutableArray<string> Names,
            ImmutableArray<int> NameIndexes,
            int ReferencesCount,
            int ReferencesStart,
            int MemberReferencesCount,
            int MemberReferencesStart,
            int AnnotationReferencesCount,
            int AnnotationReferencesStart,
            int SymbolsStart,
            int SymbolsCount,
            int EdgesStart,
            int EdgesCount,
            int CallsStart,
            int CallsCount,
            int CallCount,
            string? ReExportTarget,
            ImmutableArray<Lunil.Compiler.LuaCompilationDiagnostic> CompilationDiagnostics);

        public LuaWorkspaceCompactSnapshot Build(
            LuaModuleGraph graph,
            ImmutableArray<LuaWorkspaceDiagnostic> diagnostics,
            LuaWorkspaceMetrics metrics,
            Action<int, int>? reportProgress = null)
        {
            // Coarse step counter (symbols, call bindings, snapshot, contributions): a
            // full-workspace build is minutes of otherwise-silent work, and step events
            // let clients attribute the time between them to a specific step.
            void ReportStep(int step)
            {
                if (reportProgress is not null)
                {
                    reportProgress(step, 4);
                }
            }

            ReportStep(0);
            var hostProjection = WorkspaceSymbolGraphBuilder.Build([], _hostContract);
            _symbols.AddRange(hostProjection.Exports.Symbols.Select(InternSymbol));
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
            ReportStep(1);
            var resolvedCalls = _rawCalls.Select(call => ResolveCall(
                    call,
                    lookup,
                    externalModules,
                    dynamicSymbolsByModule,
                    symbolsByName))
                .OrderBy(static call => call.SourceModuleName, StringComparer.Ordinal)
                .ThenBy(static call => call.Span.Start)
                .ToImmutableArray();
            ReportStep(2);
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
            ReportStep(3);
            Snapshot!.Contributions = MaterializeContributions();
            Snapshot!.Globals = _workspaceGlobals.ToImmutableDictionary(StringComparer.Ordinal);
            ReportStep(4);
            return Snapshot;
        }

        /// <summary>Global types collected from module analyses during the round.</summary>
        private Dictionary<string, LuaType> _workspaceGlobals = new(StringComparer.Ordinal);

        /// <summary>
        /// Merges a module's global assignments into the workspace-wide map: globals
        /// (game, settings, logger, ...) resolve across files without annotations or
        /// configuration. First definition wins; the caller feeds the accumulated map
        /// to later modules' analyses.
        /// </summary>
        public void CollectGlobals(LuaWorkspaceModuleResult module)
        {
            foreach (var info in module.Compilation.Analysis.Symbols)
            {
                if (info.Symbol.Kind == LuaSymbolKind.Global &&
                    !string.IsNullOrEmpty(info.Symbol.Name) &&
                    !_workspaceGlobals.ContainsKey(info.Symbol.Name))
                {
                    _workspaceGlobals[info.Symbol.Name] = info.InferredType;
                }
            }
        }

        /// <summary>
        /// Copies each freshly added module's segments into reusable contributions. The
        /// module entries come from the fixed-up list (ExportedSymbols rewritten from the
        /// merged symbol universe); symbols stay in their pre-re-export-marking shape so
        /// reuse re-applies the marking against the next rebuild's universe.
        /// </summary>
        private ImmutableDictionary<string, LuaWorkspaceCompactSnapshot.ModuleContribution> MaterializeContributions()
        {
            if (_pending.Count == 0 && _reused.Count == 0)
            {
                return ImmutableDictionary<string, LuaWorkspaceCompactSnapshot.ModuleContribution>.Empty
                    .WithComparers(StringComparer.Ordinal);
            }

            var builder = ImmutableDictionary.CreateBuilder<string, LuaWorkspaceCompactSnapshot.ModuleContribution>(
                StringComparer.Ordinal);
            // Reused contributions carry forward unchanged; a module both reused and
            // re-added (fixed-point re-analysis) is overwritten by its fresh capture.
            foreach (var reused in _reused)
            {
                builder[reused.ModuleName] = reused;
            }

            foreach (var pending in _pending)
            {
                builder[pending.ModuleName] = new LuaWorkspaceCompactSnapshot.ModuleContribution(
                    pending.ModuleName,
                    pending.CacheKey,
                    _modules[pending.ModuleIndex],
                    pending.ExportedType,
                    pending.ExportSummaryHashes,
                    pending.FunctionSummaryHashes,
                    pending.HostSummaryHash,
                    pending.AnalysisSummaryHash,
                    pending.Names,
                    pending.NameIndexes,
                    [.. _references.GetRange(pending.ReferencesStart, pending.ReferencesCount)],
                    [.. _memberReferences.GetRange(pending.MemberReferencesStart, pending.MemberReferencesCount)],
                    [.. _annotationReferences.GetRange(pending.AnnotationReferencesStart, pending.AnnotationReferencesCount)],
                    [.. _annotationNames.GetRange(pending.AnnotationReferencesStart, pending.AnnotationReferencesCount)],
                    [.. _symbols.GetRange(pending.SymbolsStart, pending.SymbolsCount)],
                    [.. _edges.GetRange(pending.EdgesStart, pending.EdgesCount)],
                    [.. _rawCalls.GetRange(pending.CallsStart, pending.CallsCount)],
                    pending.CallCount,
                    pending.ReExportTarget,
                    pending.CompilationDiagnostics);
            }

            return builder.ToImmutable();
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
                var targetKeyIndex = -1;
                string targetKey = "";
                if (target is not null)
                {
                    targetKeyIndex = _strings.GetOrAdd(target, out targetKey);
                }

                _references.Add(new CompactReference(
                    moduleIndex,
                    reference.Span,
                    containingFunctionId,
                    _strings.GetOrAdd(functionKeys[containingFunctionId]),
                    _strings.GetOrAdd(reference.Name, out var name),
                    reference.IsWrite,
                    reference.ResolutionKind,
                    targetKeyIndex));
                if (targetKeyIndex >= 0)
                {
                    GetIndexBuilder(_targetIndexes, targetKey).Add(index);
                }

                if (reference.ResolutionKind == LuaNameResolutionKind.Global)
                {
                    GetIndexBuilder(_globals, name).Add(index);
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
                    _strings.GetOrAdd(reference.Name, out var memberName)));
                GetIndexBuilder(_memberIndexes, memberName).Add(memberIndex);
            }
        }

        private LuaWorkspaceExportSymbol InternSymbol(LuaWorkspaceExportSymbol symbol)
        {
            if (_interner is null)
            {
                return symbol;
            }

            var key = _interner.Intern(symbol.Key);
            var moduleName = _interner.Intern(symbol.ModuleName);
            var path = _interner.Intern(symbol.Path);
            var name = _interner.Intern(symbol.Name);
            var targetKey = symbol.TargetKey is null ? null : _interner.Intern(symbol.TargetKey);
            var functionKey = symbol.FunctionKey is null ? null : _interner.Intern(symbol.FunctionKey);
            return ReferenceEquals(key, symbol.Key) &&
                ReferenceEquals(moduleName, symbol.ModuleName) &&
                ReferenceEquals(path, symbol.Path) &&
                ReferenceEquals(name, symbol.Name) &&
                ReferenceEquals(targetKey, symbol.TargetKey) &&
                ReferenceEquals(functionKey, symbol.FunctionKey)
                    ? symbol
                    : symbol with
                    {
                        Key = key,
                        ModuleName = moduleName,
                        Path = path,
                        Name = name,
                        TargetKey = targetKey,
                        FunctionKey = functionKey,
                    };
        }

        private LuaWorkspaceExportEdge InternEdge(LuaWorkspaceExportEdge edge)
        {
            if (_interner is null)
            {
                return edge;
            }

            var sourceKey = _interner.Intern(edge.SourceKey);
            var targetKey = _interner.Intern(edge.TargetKey);
            return ReferenceEquals(sourceKey, edge.SourceKey) && ReferenceEquals(targetKey, edge.TargetKey)
                ? edge
                : edge with { SourceKey = sourceKey, TargetKey = targetKey };
        }

        private LuaWorkspaceModuleCallBinding InternCall(LuaWorkspaceModuleCallBinding call)
        {
            if (_interner is null)
            {
                return call;
            }

            var sourceModuleName = _interner.Intern(call.SourceModuleName);
            var requestedModuleName = _interner.Intern(call.RequestedModuleName);
            var memberPath = _interner.Intern(call.MemberPath);
            var targetSymbolKey = call.TargetSymbolKey is null ? null : _interner.Intern(call.TargetSymbolKey);
            var targetFunctionKey = call.TargetFunctionKey is null
                ? null
                : _interner.Intern(call.TargetFunctionKey);
            var candidateKeys = call.CandidateKeys;
            var candidatesChanged = false;
            if (!candidateKeys.IsDefault)
            {
                var interned = new string[candidateKeys.Length];
                for (var index = 0; index < candidateKeys.Length; index++)
                {
                    interned[index] = _interner.Intern(candidateKeys[index]);
                    candidatesChanged |= !ReferenceEquals(interned[index], candidateKeys[index]);
                }

                if (candidatesChanged)
                {
                    candidateKeys = interned.ToImmutableArray();
                }
            }

            return ReferenceEquals(sourceModuleName, call.SourceModuleName) &&
                ReferenceEquals(requestedModuleName, call.RequestedModuleName) &&
                ReferenceEquals(memberPath, call.MemberPath) &&
                ReferenceEquals(targetSymbolKey, call.TargetSymbolKey) &&
                ReferenceEquals(targetFunctionKey, call.TargetFunctionKey) &&
                !candidatesChanged
                ? call
                : call with
                {
                    SourceModuleName = sourceModuleName,
                    RequestedModuleName = requestedModuleName,
                    MemberPath = memberPath,
                    TargetSymbolKey = targetSymbolKey,
                    TargetFunctionKey = targetFunctionKey,
                    CandidateKeys = candidateKeys,
                };
        }

        /// <summary>Adds one entry per annotation element naming a type (references and declarations).</summary>
        private void AddAnnotationReferences(LuaWorkspaceModuleResult module, int moduleIndex)
        {
            AnnotationReferenceCollector.Collect(module.Compilation.Annotations, name =>
            {
                var annotationIndex = _annotationReferences.Count;
                _annotationReferences.Add(new CompactAnnotationReference(moduleIndex, name.Span));
                var annotationName = _strings.Canonicalize(name.Name);
                _annotationNames.Add(annotationName);
                GetIndexBuilder(_annotationIndexes, annotationName).Add(annotationIndex);
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

    public readonly record struct CompactReference(
        int ModuleIndex,
        TextSpan Span,
        int ContainingFunctionId,
        int ContainingFunctionKeyIndex,
        int NameIndex,
        bool IsWrite,
        LuaNameResolutionKind ResolutionKind,
        int TargetKeyIndex);

    public readonly record struct CompactMemberReference(
        int ModuleIndex,
        TextSpan Span,
        int NameIndex);

    public readonly record struct CompactAnnotationReference(
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
        private readonly LuaWorkspaceStringInterner? _interner;
        private readonly Dictionary<string, int> _indexes = new(StringComparer.Ordinal);
        private readonly List<string> _values = [];
        private List<(int Index, string Value)>? _capture;

        public StringPool(LuaWorkspaceStringInterner? interner = null) => _interner = interner;

        /// <summary>Returns the canonical instance for the content without pooling an index for it.</summary>
        public string Canonicalize(string value) => _interner is null ? value : _interner.Intern(value);

        public int GetOrAdd(string value) => GetOrAdd(value, out _);

        public int GetOrAdd(string value, out string canonical)
        {
            canonical = _interner is null ? value : _interner.Intern(value);
            int index;
            if (_indexes.TryGetValue(canonical, out var existing))
            {
                index = existing;
            }
            else
            {
                index = _values.Count;
                _values.Add(canonical);
                _indexes.Add(canonical, index);
            }

            // Captures record every index the current module's structs reference,
            // including indexes that already existed for shared names.
            _capture?.Add((index, canonical));
            return index;
        }

        public void BeginCapture() => _capture = [];

        public (ImmutableArray<int> Indexes, ImmutableArray<string> Values) EndCapture()
        {
            var captured = _capture ?? [];
            _capture = null;
            return (
                [.. captured.Select(static pair => pair.Index)],
                [.. captured.Select(static pair => pair.Value)]);
        }

        public ImmutableArray<string> ToImmutable() => [.. _values];
    }
}
