using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Lunil.Analysis;
using Lunil.Compiler;
using Lunil.Core.Diagnostics;
using Lunil.EmmyLua;
using Lunil.Semantics.Binding;
using Lunil.Syntax.Lexing;
using Lunil.Workspace;

namespace Lunil.LanguageServer;

internal sealed record LanguageDocumentAnalysis(
    LspTextDocument Document,
    LuaModuleIdentity Module,
    LuaCompilationResult Compilation)
{
    /// <summary>Shared lazily built lookups for request handlers on this analysis.</summary>
    internal LuaLanguageService.ServiceCaches ServiceCaches { get; } = new();

    /// <summary>
    /// The workspace-knowledge generation this analysis was built with. Analyses cached
    /// before the workspace index existed must not survive the index becoming available.
    /// </summary>
    internal int EnvironmentGeneration { get; init; }
}

/// <summary>An annotation-declared class and its declared base class names.</summary>
internal sealed record WorkspaceClassDeclaration(
    string ModuleName,
    string Name,
    ImmutableArray<string> BaseNames);

internal enum FileIndexStatus : byte
{
    Pending,
    InProgress,
    Succeeded,
    Failed,
}

internal sealed class LanguageServerWorkspace : IDisposable
{
    /// <summary>Library globals for one stub document, cached by its exact text.</summary>
    private sealed record LibraryGlobalsCacheEntry(
        string Text,
        ImmutableDictionary<string, LuaType> Globals);

    private static readonly Lazy<bool> CompilerWarmup = new(() =>
    {
        _ = new LuaCompiler().CompileUtf8("return nil", "@lunil/language-server-warmup.lua");
        return true;
    }, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly string[] ExcludedDirectories =
        [".git", ".svn", "bin", "obj", "node_modules", ".vscode", ".idea"];

    /// <summary>Corpus-scan progress is reported every N files so parallel workers cannot flood the channel.</summary>
    private const int CorpusProgressInterval = 64;
    private static BuiltinLibrary Builtin => BuiltinLibrary.Value;
    private readonly object _gate = new();
    private readonly LuaFrontEndSession _frontEnd = new(new LuaCompilerOptions
    {
        // Single-document analysis (open files outside an indexed workspace) uses the
        // same unified reference projection as workspace modules so member navigation,
        // semantic tokens, and completion behave identically in both paths.
        Binder = LuaBinderOptions.Default with { CollectCodeReferences = true },
    });
    private readonly Dictionary<string, LspTextDocument> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LanguageDocumentAnalysis> _analyses = new(StringComparer.Ordinal);
    /** Recency order bounding <see cref="_analyses"/>; least-recently-used non-open entries evict first. */
    private readonly LinkedList<string> _analysisOrder = new();
    private long _cachedAnalysisBytes;

    /// <summary>Byte budget for cached document analyses; open documents are pinned
    /// and never evicted for budget reasons. The default scales with the memory the
    /// runtime grants this process (see <see cref="LanguageServerMemoryBudget"/>).</summary>
    internal long MaximumCachedAnalysisBytes { get; set; } = LanguageServerMemoryBudget.AnalysisCacheBytes;

    /// <summary>
    /// Byte budget for closed documents' resident sources. Above it, least-recently-used
    /// closed documents drop their bytes and reload from disk on next use; open documents
    /// and virtual documents stay resident regardless. The default scales with the memory
    /// the runtime grants this process (see <see cref="LanguageServerMemoryBudget"/>).
    /// </summary>
    internal long MaximumDocumentResidencyBytes { get; set; } = LanguageServerMemoryBudget.DocumentResidencyBytes;

    private static readonly TimeSpan StartupDeclarationsTimeout = TimeSpan.FromSeconds(120);
    private ImmutableDictionary<string, Uri>? _uriByModuleName;
    private int _uriIndexGeneration = -1;
    private ImmutableDictionary<string, Uri>? _uriBySnapshotModule;
    private LuaWorkspaceCompactSnapshot? _uriBySnapshotModuleSource;
    private int _documentSetGeneration;
    private ImmutableArray<Uri> _folders = [];

    /// <summary>
    /// Completes when the first folder scan has merged its documents (immediately for
    /// folder-less sessions). Rounds await it: didChangeConfiguration arrives while the
    /// scan is still reading the corpus, and a round that wins that race analyzes an
    /// empty working set, completes against a corpus-sized ledger, and stores a garbage
    /// snapshot (kilobytes instead of hundreds of megabytes).
    /// </summary>
    private readonly TaskCompletionSource _initialScanCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Declaration files found under the registered folders — any non-<c>.lua</c> file
    /// whose base name is a valid Lua identifier (entity/component definition files,
    /// config manifests, host-API docs). Members that resolve to no Lua definition
    /// anywhere may be declared by such a file, so it is the ground-truth jump target
    /// for engine-injected chains like <c>game.player.Entity.server</c>. The scan runs lazily
    /// on first lookup and is cached until the folder set changes.
    /// </summary>
    private readonly object _declarationFileGate = new();
    private ImmutableArray<Uri>? _declarationFileFolderStamp;
    private ImmutableDictionary<string, Uri> _declarationFiles =
        ImmutableDictionary<string, Uri>.Empty;

    /// <summary>
    /// Finds the declaration file whose base name (without extension) equals
    /// <paramref name="name"/> under the registered folders, if any.
    /// </summary>
    public bool TryGetDeclarationFile(string name, [NotNullWhen(true)] out Uri? uri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        ImmutableArray<Uri> folders;
        lock (_gate)
        {
            folders = _folders;
        }

        lock (_declarationFileGate)
        {
            if (_declarationFileFolderStamp is null ||
                !_declarationFileFolderStamp.Value.SequenceEqual(folders))
            {
                _declarationFiles = ScanDeclarationFiles(folders);
                _declarationFileFolderStamp = folders;
            }

            return _declarationFiles.TryGetValue(name, out uri!);
        }
    }

    private static ImmutableDictionary<string, Uri> ScanDeclarationFiles(ImmutableArray<Uri> folders)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, Uri>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            try
            {
                foreach (var group in Directory.EnumerateFiles(
                             ToLocalPath(folder), "*", SearchOption.AllDirectories)
                             .Where(static path =>
                                 !path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) &&
                                 IsIdentifierStem(Path.GetFileNameWithoutExtension(path)))
                             .OrderBy(static path => path, StringComparer.Ordinal)
                             .GroupBy(static path => Path.GetFileNameWithoutExtension(path), StringComparer.Ordinal))
                {
                    // Deterministic winner on same-stem collisions: the first path in
                    // ordinal order.
                    builder.TryAdd(group.Key, new Uri(Path.GetFullPath(group.First())));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Unreadable roots contribute no declarations; lookup simply misses.
            }
        }

        return builder.ToImmutable();
    }

    private static bool IsIdentifierStem(string stem) =>
        stem.Length > 0 &&
        (char.IsLetter(stem[0]) || stem[0] == '_') &&
        stem.All(static character => char.IsLetterOrDigit(character) || character == '_');

    /// <summary>
    /// Files kept out of the analysis corpus (`lunil.analysis.exclude` patterns plus
    /// auto-detected generated data files), keyed by URI with the exclusion reason.
    /// They stay unloaded and unanalyzed until opened in the editor.
    /// </summary>
    private readonly Dictionary<string, string> _excludedFiles = new(StringComparer.Ordinal);
    private WorkspaceFileFilter? _fileFilter = WorkspaceFileFilter.Create([], autoDetect: true);
    private ImmutableArray<string> _excludePatterns = [];
    private bool _autoDetectDataFiles = true;

    /// <summary>
    /// Read-only declaration-stub folders (`lunil.workspace.library`): LuaLS-style
    /// `---@meta` trees describing host-injected globals and classes.
    /// </summary>
    private ImmutableArray<Uri> _libraryFolders = [];
    private ImmutableArray<string> _libraryPaths = [];
    private ImmutableDictionary<string, LuaType> _libraryGlobals =
        ImmutableDictionary<string, LuaType>.Empty.WithComparers(StringComparer.Ordinal);
    private readonly Dictionary<string, LibraryGlobalsCacheEntry> _libraryGlobalsCache =
        new(StringComparer.Ordinal);

    private LuaWorkspace _workspace;
    private LuaHostAnalysisContract? _hostContract;
    private ImmutableArray<string> _requireSearchPaths = [];
    private ImmutableDictionary<string, bool> _classFactoryCalls =
        ImmutableDictionary<string, bool>.Empty.WithComparers(StringComparer.Ordinal);
    private ImmutableHashSet<string> _suppressedDiagnosticCodes =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
    private LuaWorkspaceCompactSnapshot? _snapshot;

    /// <summary>
    /// True while <see cref="_snapshot"/> came from disk rather than a completed round:
    /// corpus-level invalidations (folder merges, opens of not-yet-loaded documents)
    /// keep serving it — the fingerprint matched this exact corpus — until the first
    /// fresh round replaces it.
    /// </summary>
    private bool _snapshotRestored;

    /// <summary>The fingerprint of the last disk-restore attempt (retry on change).</summary>
    private string? _lastSnapshotLoadFingerprintAttempt;

    /// <summary>
    /// Test isolation: unit tests share fake folder URIs (file:///src/), so their
    /// fingerprints collide and snapshots would leak between test cases. The test
    /// assembly disables persistence entirely through this switch.
    /// </summary>
    internal static bool SnapshotPersistenceDisabled;
    private CancellationTokenSource? _indexCancellation;
    private ImmutableDictionary<string, LuaExternalTypeDeclaration> _externalTypeDeclarations =
        ImmutableDictionary<string, LuaExternalTypeDeclaration>.Empty.WithComparers(StringComparer.Ordinal);
    private readonly Dictionary<string, ImmutableDictionary<string, LuaExternalTypeDeclaration>>
        _perDocumentDeclarations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FileIndexStatus> _indexStatus = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _indexErrors = new(StringComparer.Ordinal);
    private ImmutableArray<WorkspaceClassDeclaration>? _classDeclarations;
    private int _classDeclarationsGeneration = -1;
    private ImmutableDictionary<string, ImmutableDictionary<string, Lunil.Analysis.LuaType>>? _externalClassMembers;
    private LuaWorkspaceCompactSnapshot? _externalClassMembersSnapshot;
    private int _externalClassMembersGeneration = -1;
    private string? _externalClassMemberSignature;
    private int _environmentGeneration;
    private int _generation;
    private int _declarationsGeneration;
    private readonly SemaphoreSlim _analysisConcurrency = new(8, 8);
    private readonly TaskCompletionSource<bool> _declarationsReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _declarationsReadySet;
    private bool _disposed;

    /// <summary>
    /// Resolves a file URI to a usable local path. VSCode encodes the Windows drive colon
    /// (<c>file:///c%3A/...</c>), which makes <see cref="Uri.LocalPath"/> return a POSIX-style
    /// <c>/c:/...</c> that <see cref="Path.GetFullPath"/> mangles into <c>C:\c:\...</c>. Strip the
    /// leading slash for drive-letter paths so directory scans and file reads find the real files.
    /// </summary>
    private static string ToLocalPath(Uri uri)
    {
        var path = uri.LocalPath;
        if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':')
        {
            return path.Substring(1);
        }

        return path;
    }

    /// <summary>
    /// Canonicalizes a client-supplied URI into the key form the folder scan
    /// produces. VS Code percent-encodes the Windows drive colon
    /// (<c>file:///c%3A/...</c>) while scanning local paths yields
    /// <c>file:///c:/...</c>; without this normalization every didOpen lookup
    /// misses the corpus document and schedules a full workspace rebuild.
    /// </summary>
    internal static Uri CanonicalUri(Uri uri)
    {
        if (!uri.IsFile)
        {
            return uri;
        }

        try
        {
            return new Uri(ToLocalPath(uri));
        }
        catch (UriFormatException)
        {
            // Rootless paths (for example test fixtures like file:///src/a.lua)
            // have no absolute local-path form; keep the URI as given.
            return uri;
        }
    }

    public LanguageServerWorkspace()
    {
        _ = CompilerWarmup.Value;
        _workspace = CreateWorkspace(hostContract: null);
    }

    public Func<Uri, int?, JsonArray, Task>? DiagnosticsPublished { get; set; }

    public Func<LuaWorkspaceProgress, Task>? ProgressReported { get; set; }

    /// <summary>Forwards a parallel corpus-scan count as a progress event; display-only, never awaited.</summary>
    private void ReportCorpusProgress(LuaWorkspaceProgressPhase phase, int completed, int total)
    {
        var report = ProgressReported;
        if (report is null)
        {
            return;
        }

        _ = report(new LuaWorkspaceProgress(phase, completed, total));
    }

    /// <summary>Raised when a document leaves the workspace so per-document caches follow.</summary>
    public event Action<Uri>? DocumentRemoved;

    /// <summary>
    /// Receives informational lifecycle messages. Without a sink they go to stderr,
    /// which clients surface as errors in their output channels.
    /// </summary>
    public Action<string>? InfoLogged { get; set; }

    private void LogInfo(string message)
    {
        if (InfoLogged is { } log)
        {
            log(message);
        }
        else
        {
            Console.Error.WriteLine(message);
        }
    }

    public ImmutableArray<Uri> Folders
    {
        get
        {
            lock (_gate)
            {
                return _folders;
            }
        }
    }

    public void Initialize(IEnumerable<Uri> folders)
    {
        var normalized = folders.Where(static uri => uri.IsFile)
            .Select(static uri => new Uri(Path.GetFullPath(ToLocalPath(uri)) + Path.DirectorySeparatorChar))
            .Distinct()
            .ToImmutableArray();
        lock (_gate)
        {
            ThrowIfDisposed();
            _folders = normalized;
            foreach (var key in _documents.Where(pair => !pair.Value.IsOpen &&
                         !normalized.Any(folder => IsUnderRoot(ToLocalPath(pair.Value.Uri), ToLocalPath(folder))))
                     .Select(static pair => pair.Key).ToArray())
            {
                _documents.Remove(key);
                RemoveAnalysis(key);
                DocumentRemoved?.Invoke(new Uri(key, UriKind.Absolute));
            }

            _documentSetGeneration++;
            InvalidateIndexNoLock();
        }

        PrefetchNavigationSnapshot();
        // The first round is scheduled by the folder-scan merge (LoadFolders), never
        // here: scheduling at initialize lets an empty-corpus round run before the scan
        // has loaded any documents, and its stored snapshot would be garbage. Folder-less
        // sessions (single-file) still get a round so configuration changes apply.
        if (normalized.IsEmpty)
        {
            _initialScanCompletion.TrySetResult();
            ScheduleIndex("initialize (no folders)");
        }

        _ = Task.Run(() =>
        {
            try
            {
                LoadFolders(normalized, "initialize");
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and
                not StackOverflowException and not AccessViolationException)
            {
                LogInfo($"Lunil workspace: folder scan failed: {exception.Message}");
            }
            finally
            {
                _initialScanCompletion.TrySetResult();
            }
        });
    }

    public void AddFolder(Uri folder)
    {
        Initialize(Folders.Add(folder));
    }

    public void RemoveFolder(Uri folder)
    {
        var root = Path.GetFullPath(ToLocalPath(folder));
        lock (_gate)
        {
            _folders = [.. _folders.Where(item => !PathsEqual(ToLocalPath(item), root))];
            foreach (var key in _documents.Where(pair => !pair.Value.IsOpen &&
                         IsUnderRoot(ToLocalPath(pair.Value.Uri), root)).Select(static pair => pair.Key).ToArray())
            {
                _documents.Remove(key);
                RemoveAnalysis(key);
                DocumentRemoved?.Invoke(new Uri(key, UriKind.Absolute));
            }

            _documentSetGeneration++;
            InvalidateIndexNoLock();
        }

        ScheduleIndex("remove-folder");
    }

    /// <summary>
    /// Applies <c>lunil.analysis.exclude</c> patterns and the data-file auto-detection
    /// toggle. Closed documents under the workspace folders are dropped so a fresh
    /// folder load re-reads the corpus against the new rules; open documents keep
    /// their analyses regardless of exclusion.
    /// </summary>
    public void ConfigureAnalysisExclusions(IEnumerable<string?>? patterns, bool? autoDetect)
    {
        var normalized = (patterns ?? [])
            .Where(static pattern => !string.IsNullOrWhiteSpace(pattern))
            .Select(static pattern => pattern!.Trim())
            .Where(static pattern => pattern.Length > 0)
            .ToImmutableArray();
        var removedAny = false;
        lock (_gate)
        {
            ThrowIfDisposed();
            // didChangeConfiguration carries the whole lunil section on every settings
            // edit; an unchanged exclusion configuration must not trigger a reload.
            var settingsChanged = !normalized.SequenceEqual(_excludePatterns) ||
                autoDetect is { } value && value != _autoDetectDataFiles;
            if (!settingsChanged)
            {
                return;
            }

            _excludePatterns = normalized;
            if (autoDetect is { } enabled)
            {
                _autoDetectDataFiles = enabled;
            }

            _fileFilter = WorkspaceFileFilter.Create(_excludePatterns, _autoDetectDataFiles);
            _excludedFiles.Clear();
            foreach (var key in _documents.Where(pair => !pair.Value.IsOpen &&
                         _folders.Any(folder => IsUnderRoot(ToLocalPath(pair.Value.Uri), ToLocalPath(folder))))
                     .Select(static pair => pair.Key).ToArray())
            {
                _documents.Remove(key);
                RemoveAnalysis(key);
                _indexStatus.Remove(key);
                _indexErrors.Remove(key);
                DocumentRemoved?.Invoke(new Uri(key, UriKind.Absolute));
                removedAny = true;
            }

            _documentSetGeneration++;
            InvalidateIndexNoLock();
        }

        if (!_folders.IsEmpty)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    LoadFolders(_folders, "configure-analysis-exclusions");
                }
                finally
                {
                    _initialScanCompletion.TrySetResult();
                }
            });
        }
        else if (removedAny)
        {
            ScanAllTypeDeclarations();
            ScheduleIndex("configure-analysis-exclusions");
        }
    }

    /// <summary>
    /// Classifies already-loaded bytes against the current filter; returns the exclusion
    /// reason or null when the file stays in the corpus.
    /// </summary>
    private static string? ClassifyExclusion(
        Uri uri,
        ReadOnlySpan<byte> bytes,
        WorkspaceFileFilter? filter,
        ImmutableArray<Uri> folders)
    {
        if (filter is null)
        {
            return null;
        }

        var localPath = ToLocalPath(uri);
        var relativePath = TryGetWorkspaceRelativePath(localPath, folders) ??
            Path.GetFileName(localPath);
        if (filter.IsExcludedByPattern(relativePath))
        {
            return WorkspaceFileFilter.PatternExclusionReason;
        }

        return filter.AutoDetectDataFiles && WorkspaceFileFilter.LooksLikeDataFile(bytes)
            ? WorkspaceFileFilter.DataExclusionReason
            : null;
    }

    /// <summary>
    /// Classifies a file on disk without loading it fully: pattern rules first, then a
    /// bounded sample read only when the file is large enough to qualify as data.
    /// </summary>
    private static string? ClassifyDiskExclusion(
        Uri uri,
        WorkspaceFileFilter? filter,
        ImmutableArray<Uri> folders)
    {
        if (filter is null)
        {
            return null;
        }

        var localPath = ToLocalPath(uri);
        var relativePath = TryGetWorkspaceRelativePath(localPath, folders) ??
            Path.GetFileName(localPath);
        if (filter.IsExcludedByPattern(relativePath))
        {
            return WorkspaceFileFilter.PatternExclusionReason;
        }

        if (!filter.AutoDetectDataFiles)
        {
            return null;
        }

        try
        {
            var info = new FileInfo(localPath);
            if (info.Length < WorkspaceFileFilter.DataDetectionMinimumBytes)
            {
                return null;
            }

            // One extra byte proves the sample was truncated by the reader, not by EOF.
            var sampleLength = (int)Math.Min(
                info.Length,
                WorkspaceFileFilter.DataDetectionSampleBytes + 1L);
            var sample = new byte[sampleLength];
            using var stream = File.OpenRead(localPath);
            var read = 0;
            while (read < sampleLength)
            {
                var chunk = stream.Read(sample, read, sampleLength - read);
                if (chunk <= 0)
                {
                    break;
                }

                read += chunk;
            }

            return WorkspaceFileFilter.LooksLikeDataFile(sample.AsSpan(0, read))
                ? WorkspaceFileFilter.DataExclusionReason
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? TryGetWorkspaceRelativePath(string path, ImmutableArray<Uri> folders)
    {
        var ownerRoot = "";
        foreach (var folder in folders)
        {
            var root = ToLocalPath(folder);
            if (root.Length > ownerRoot.Length && IsUnderRoot(path, root))
            {
                ownerRoot = root;
            }
        }

        return ownerRoot.Length == 0
            ? null
            : Path.GetRelativePath(ownerRoot, path).Replace('\\', '/');
    }

    public void Open(Uri uri, int version, string text)
    {
        var contentUnchanged = false;
        lock (_gate)
        {
            ThrowIfDisposed();
            // Opening a corpus document whose content matches what the editor sends
            // (the preview-tab case after a navigation) changes nothing the index
            // depends on: keep the snapshot, declarations, and per-document analyses
            // instead of scheduling a full workspace rebuild for every file the
            // editor merely displays.
            if (_documents.TryGetValue(uri.AbsoluteUri, out var existing) &&
                !_excludedFiles.ContainsKey(uri.AbsoluteUri) &&
                string.Equals(existing.Text, text, StringComparison.Ordinal))
            {
                _documents[uri.AbsoluteUri] = new LspTextDocument(uri, version, text);
                contentUnchanged = true;
            }
            else
            {
                // A document already in the analysis corpus keeps serving the previous
                // snapshot while the rebuild runs (only its content changed); a new or
                // previously excluded document changes the corpus itself and drops it.
                var wasCorpusDocument = existing is not null &&
                    !_excludedFiles.ContainsKey(uri.AbsoluteUri);
                _documents[uri.AbsoluteUri] = new LspTextDocument(uri, version, text);
                RemoveAnalysis(uri.AbsoluteUri);
                _indexStatus[uri.AbsoluteUri] = FileIndexStatus.Pending;
                _indexErrors.Remove(uri.AbsoluteUri);
                // Opening a file is an explicit request to analyze it, even when it was
                // excluded from background indexing.
                _excludedFiles.Remove(uri.AbsoluteUri);
                _documentSetGeneration++;
                // A restored snapshot survives opens of documents the folder scan has
                // not loaded yet (startup editor reopen) — the first round replaces it.
                InvalidateIndexNoLock(keepStaleSnapshot: wasCorpusDocument || _snapshotRestored);
            }
        }

        if (contentUnchanged)
        {
            // Diagnostics for the now-open tab come from the retained caches.
            LogInfo($"didOpen (fast path, no rebuild): {uri}");
            _ = AnalyzeAndPublishAsync(uri, version, CancellationToken.None);
            return;
        }

        LogInfo($"didOpen (slow path, scheduling rebuild): {uri}");
        UpdateDocumentTypeDeclarations(uri.AbsoluteUri, text);
        // An opened document is itself a declarations source. Registered folders that are
        // empty or missing on disk would otherwise leave the declaration gate closed
        // forever, and every subsequent analysis request would hang waiting for it.
        SignalDeclarationsReady();
        if (_folders.IsEmpty)
        {
            // No workspace folder was registered (for example a single-file session). Scan the
            // opened document's directory tree, including ancestor directories' direct .lua files,
            // so cross-file @class/@alias/@enum declarations are still available.
            ScanAncestorDeclarations(uri);
        }

        _ = AnalyzeAndPublishAsync(uri, version, CancellationToken.None);
        ScheduleIndex("didOpen slow path");
    }

    /// <summary>Collects type declarations from a document's directory and each ancestor directory.</summary>
    private void ScanAncestorDeclarations(Uri uri)
    {
        // Best-effort scan; failures on inaccessible roots must never block document analysis.
        try
        {
            WorkspaceFileFilter? filter;
            lock (_gate)
            {
                filter = _fileFilter;
            }

            var added = new Dictionary<string, ImmutableDictionary<string, LuaExternalTypeDeclaration>>(StringComparer.Ordinal);
            var directory = Path.GetDirectoryName(ToLocalPath(uri));
            while (directory is not null)
            {
                IEnumerable<string>? files;
                try
                {
                    files = Directory.Exists(directory)
                        ? Directory.EnumerateFiles(directory, "*.lua", SearchOption.TopDirectoryOnly).ToArray()
                        : [];
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    files = [];
                }

                foreach (var path in files)
                {
                    try
                    {
                        var fileUri = new Uri(Path.GetFullPath(path));
                        if (added.ContainsKey(fileUri.AbsoluteUri))
                        {
                            continue;
                        }

                        var bytes = File.ReadAllBytes(path);
                        // Generated data tables in ancestor directories carry no
                        // annotations; skip them so the scan does not lex megabytes
                        // of data while opening a single file.
                        if (filter is { AutoDetectDataFiles: true } &&
                            WorkspaceFileFilter.LooksLikeDataFile(bytes))
                        {
                            continue;
                        }

                        added[fileUri.AbsoluteUri] = ScanTypeDeclarations(bytes, fileUri.AbsoluteUri);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                    }
                }

                var parent = Path.GetDirectoryName(directory);
                if (parent == directory)
                {
                    break;
                }

                directory = parent;
            }

            lock (_gate)
            {
                foreach (var pair in added)
                {
                    _perDocumentDeclarations[pair.Key] = pair.Value;
                }

                RebuildExternalTypeDeclarationsNoLock();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not AccessViolationException)
        {
            // Best-effort directory scan; ignore any failure.
            Console.Error.WriteLine($"Lunil workspace: ancestor declaration scan failed for {uri}: {exception.Message}");
        }
        finally
        {
            // Even when no ancestor .lua files exist (for example a virtual/single-file session),
            // the declaration gate must lift so the document can be analyzed.
            lock (_gate)
            {
                if (!_declarationsReadySet)
                {
                    _declarationsReadySet = true;
                    _declarationsReady.TrySetResult(true);
                }
            }
        }
    }

    public bool Change(Uri uri, int version, IReadOnlyList<LspTextChange> changes)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_documents.TryGetValue(uri.AbsoluteUri, out var document) ||
                !document.IsOpen || version <= document.Version)
            {
                return false;
            }

            // An empty change list is a version bump (some clients send these on
            // focus changes); it changes nothing the index depends on.
            if (changes.Count == 0)
            {
                _documents[uri.AbsoluteUri] = document.WithVersion(version);
                return true;
            }

            _documents[uri.AbsoluteUri] = document.Apply(version, changes);
            RemoveAnalysis(uri.AbsoluteUri);
            _indexStatus[uri.AbsoluteUri] = FileIndexStatus.Pending;
            _indexErrors.Remove(uri.AbsoluteUri);
            // An edit changes one document's content, not the corpus: the previous
            // snapshot keeps serving cross-file navigation until the replacement
            // round stores (slightly stale beats a null window measured in tens of
            // seconds on large workspaces).
            InvalidateIndexNoLock(keepStaleSnapshot: true);
        }

        var changedText = _documents.TryGetValue(uri.AbsoluteUri, out var changedDocument)
            ? changedDocument.Text
            : null;
        if (changedText is not null)
        {
            UpdateDocumentTypeDeclarations(uri.AbsoluteUri, changedText);
        }

        _ = AnalyzeAndPublishAsync(uri, version, CancellationToken.None);
        ScheduleIndex("didChange");
        return true;
    }

    public void Close(Uri uri)
    {
        LspTextDocument? disk = null;
        string? exclusion = null;
        var contentUnchanged = false;
        ImmutableArray<Uri> folders;
        WorkspaceFileFilter? filter;
        lock (_gate)
        {
            folders = _folders;
            filter = _fileFilter;
        }

        if (uri.IsFile && File.Exists(ToLocalPath(uri)))
        {
            exclusion = ClassifyDiskExclusion(uri, filter, folders);
            if (exclusion is null)
            {
                disk = LoadDiskDocument(uri, 0);
            }
        }

        lock (_gate)
        {
            _excludedFiles.Remove(uri.AbsoluteUri);
            if (disk is null)
            {
                _documents.Remove(uri.AbsoluteUri);
                _indexStatus.Remove(uri.AbsoluteUri);
                _indexErrors.Remove(uri.AbsoluteUri);
                if (exclusion is not null)
                {
                    _excludedFiles[uri.AbsoluteUri] = exclusion;
                }

                DocumentRemoved?.Invoke(uri);
            }
            else
            {
                // Closing a tab that matches its disk version is a pure view
                // operation: the corpus content is identical, so the snapshot,
                // declarations, and analyses all stay valid — no rebuild needed.
                contentUnchanged = _documents.TryGetValue(uri.AbsoluteUri, out var existing) &&
                    existing is not null &&
                    string.Equals(existing.Text, disk.Text, StringComparison.Ordinal);
                _documents[uri.AbsoluteUri] = disk;
                _indexStatus[uri.AbsoluteUri] = FileIndexStatus.Pending;
                _indexErrors.Remove(uri.AbsoluteUri);
            }

            RemoveAnalysis(uri.AbsoluteUri);
            _documentSetGeneration++;
            // Closing back to the on-disk version is a content change (stale-keep);
            // a document that vanished from disk leaves the corpus (drop).
            InvalidateIndexNoLock(keepStaleSnapshot: disk is not null || contentUnchanged);
        }

        if (contentUnchanged)
        {
            // Pure tab close with no edits: nothing changed, no rebuild.
            if (DiagnosticsPublished is { } publish)
            {
                _ = publish(uri, null, []);
            }

            return;
        }

        if (disk is not null)
        {
            UpdateDocumentTypeDeclarations(uri.AbsoluteUri, disk.Text);
        }
        else
        {
            lock (_gate)
            {
                _perDocumentDeclarations.Remove(uri.AbsoluteUri);
                RebuildExternalTypeDeclarationsNoLock();
            }
        }

        if (DiagnosticsPublished is { } publish2)
        {
            _ = publish2(uri, null, []);
        }

        ScheduleIndex("didClose content changed");
    }

    public void WatchedFileChanged(Uri uri, int changeType)
    {
        ImmutableArray<Uri> folders;
        WorkspaceFileFilter? filter;
        lock (_gate)
        {
            folders = _folders;
            filter = _fileFilter;
            if (_documents.TryGetValue(uri.AbsoluteUri, out var open) && open.IsOpen)
            {
                return;
            }
        }

        string? exclusion = null;
        var deleted = changeType == 3 || !uri.IsFile || !File.Exists(ToLocalPath(uri));
        if (!deleted)
        {
            exclusion = ClassifyDiskExclusion(uri, filter, folders);
        }

        var contentUnchanged = false;
        lock (_gate)
        {
            var wasExcluded = _excludedFiles.TryGetValue(uri.AbsoluteUri, out var previousReason);
            if (wasExcluded && !deleted && exclusion == previousReason)
            {
                // Still excluded with the same reason: no document-set change, no rebuild.
                return;
            }

            if (deleted)
            {
                _documents.Remove(uri.AbsoluteUri);
                _indexStatus.Remove(uri.AbsoluteUri);
                _indexErrors.Remove(uri.AbsoluteUri);
                _excludedFiles.Remove(uri.AbsoluteUri);
                DocumentRemoved?.Invoke(uri);
            }
            else if (exclusion is not null)
            {
                // Newly (or still, with a different reason) excluded: drop any resident
                // document without loading the file's contents.
                if (_documents.Remove(uri.AbsoluteUri))
                {
                    _indexStatus.Remove(uri.AbsoluteUri);
                    _indexErrors.Remove(uri.AbsoluteUri);
                    DocumentRemoved?.Invoke(uri);
                }

                _excludedFiles[uri.AbsoluteUri] = exclusion;
            }
            else
            {
                _excludedFiles.Remove(uri.AbsoluteUri);
                var disk = LoadDiskDocument(uri, 0);
                if (_documents.TryGetValue(uri.AbsoluteUri, out var current) &&
                    string.Equals(current.Text, disk.Text, StringComparison.Ordinal))
                {
                    // A watcher event with unchanged content (an mtime touch, an SVN
                    // status pass, a formatter no-op) is not a real change: keep the
                    // snapshot, declarations, and analyses instead of scheduling a
                    // full workspace rebuild.
                    contentUnchanged = true;
                    LogInfo($"watched file unchanged, no rebuild ({changeType}): {uri}");
                }
                else
                {
                    _documents[uri.AbsoluteUri] = disk;
                    _indexStatus[uri.AbsoluteUri] = FileIndexStatus.Pending;
                    _indexErrors.Remove(uri.AbsoluteUri);
                }
            }

            if (!contentUnchanged)
            {
                RemoveAnalysis(uri.AbsoluteUri);
                _documentSetGeneration++;
                // A disk change of an existing, non-excluded document only changes its
                // content; deletions and exclusion transitions change the corpus itself.
                InvalidateIndexNoLock(keepStaleSnapshot: !deleted && exclusion is null);
            }
        }

        if (contentUnchanged)
        {
            return;
        }

        if (_documents.TryGetValue(uri.AbsoluteUri, out var watched))
        {
            UpdateDocumentTypeDeclarations(uri.AbsoluteUri, watched.Text);
        }
        else
        {
            lock (_gate)
            {
                _perDocumentDeclarations.Remove(uri.AbsoluteUri);
                RebuildExternalTypeDeclarationsNoLock();
            }
        }

        ScheduleIndex($"watched-file (type {changeType})");
    }

    public void ConfigureHostContract(string? json, string? path)
    {
        LuaHostAnalysisContract? contract = null;
        if (!string.IsNullOrWhiteSpace(json))
        {
            contract = LuaHostAnalysisContract.ParseJson(json);
        }
        else if (!string.IsNullOrWhiteSpace(path))
        {
            contract = LuaHostAnalysisContract.ParseJson(File.ReadAllText(Path.GetFullPath(path)));
        }

        lock (_gate)
        {
            _hostContract = contract;
            var previous = _workspace;
            _workspace = CreateWorkspace(contract);
            previous.Dispose();
            ClearAnalyses();
            InvalidateIndexNoLock();
        }

        ScheduleIndex("configure-host-contract");
    }

    /// <summary>
    /// Configures require search-path roots (<c>lunil.require.searchPaths</c>). Each root is a
    /// directory relative to the first workspace folder; a require string that is not itself a
    /// module identity resolves through these roots in order.
    /// </summary>
    public void ConfigureRequireSearchPaths(IEnumerable<string?>? paths)
    {
        var normalized = (paths ?? []).Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path!.Trim())
            .Where(static path => path.Length > 0)
            .ToImmutableArray();

        lock (_gate)
        {
            _requireSearchPaths = normalized;
            var previous = _workspace;
            _workspace = CreateWorkspace(_hostContract);
            previous.Dispose();
            ClearAnalyses();
            InvalidateIndexNoLock();
        }

        PrefetchNavigationSnapshot();
        ScheduleIndex("configure-require-search-paths");
    }

    /// <summary>
    /// Configures the global class-factory functions recognized by the analyzer
    /// (<c>lunil.analysis.classFactories</c>): <c>local X = class("Name", Base, ...)</c>
    /// style calls infer a self-indexed prototype — hover shows the class, member writes
    /// define methods, and lookup walks the inheritance chain. Each entry maps the
    /// function name to whether its arguments after the class name are base classes.
    /// </summary>
    public void ConfigureClassFactoryCalls(IReadOnlyDictionary<string, bool> factories)
    {
        var normalized = factories
            .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key))
            .ToImmutableDictionary(
                static pair => pair.Key.Trim(),
                static pair => pair.Value,
                StringComparer.Ordinal);
        lock (_gate)
        {
            if (normalized.Count == _classFactoryCalls.Count &&
                normalized.All(pair =>
                    _classFactoryCalls.TryGetValue(pair.Key, out var takesBases) &&
                    takesBases == pair.Value))
            {
                return;
            }

            _classFactoryCalls = normalized;
            var previous = _workspace;
            _workspace = CreateWorkspace(_hostContract);
            previous.Dispose();
            ClearAnalyses();
            InvalidateIndexNoLock();
            // Runtime class edges derive from these names too.
            _runtimeClassBases = null;
            _runtimeEdgeScans = new(StringComparer.Ordinal);
        }

        PrefetchNavigationSnapshot();
        ScheduleIndex("configure-class-factories");
    }

    /// <summary>
    /// Expands a require string into candidate module identities using the configured search
    /// paths, with the raw name first. Member resolution and require-string navigation probe
    /// these candidates in order against the workspace export graph.
    /// </summary>
    public ImmutableArray<string> ExpandRequireName(string requestedName) =>
        RequireNameExpansion.Expand(requestedName, _requireSearchPaths);

    /// <summary>
    /// Resolves a require string to the first candidate identity that names a known module,
    /// or null when none of the search paths (nor the raw name) match.
    /// </summary>
    public string? ResolveRequireName(string requestedName)
    {
        foreach (var candidate in ExpandRequireName(requestedName))
        {
            if (GetUri(candidate) is not null)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a require string names anything analyzable: a workspace document (through
    /// the search paths) or a host-contract module (matched by the raw name, mirroring
    /// dependency resolution). Dangling requires return false.
    /// </summary>
    public bool ModuleExists(string requireName) =>
        ResolveRequireName(requireName) is not null ||
        (_hostContract is not null && _hostContract.Modules.ContainsKey(requireName));

    /// <summary>
    /// Configures read-only declaration-stub folders. Relative paths resolve against
    /// the first workspace folder. Stub globals seed every analysis, and their
    /// <c>---@class</c> declarations join the workspace declaration map.
    /// </summary>
    public void ConfigureLibraryFolders(IEnumerable<string?>? paths)
    {
        var normalizedPaths = (paths ?? []).Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path!.Trim())
            .Where(static path => path.Length > 0)
            .ToImmutableArray();

        // didChangeConfiguration carries the whole lunil section on every settings
        // edit; an unchanged library configuration must not drop the snapshot or
        // trigger a reload (ConfigureAnalysisExclusions applies the same guard).
        if (normalizedPaths.SequenceEqual(_libraryPaths))
        {
            return;
        }

        _libraryPaths = normalizedPaths;
        var roots = new List<Uri>();
        string? workspaceRoot;
        lock (_gate)
        {
            ThrowIfDisposed();
            workspaceRoot = _folders.IsEmpty ? null : ToLocalPath(_folders[0]);
        }

        foreach (var raw in _libraryPaths)
        {
            try
            {
                var expanded = raw;
                if (!Path.IsPathRooted(expanded))
                {
                    if (workspaceRoot is null)
                    {
                        LogInfo($"Lunil workspace: ignoring relative library folder '{raw}' without a workspace folder");
                        continue;
                    }

                    expanded = Path.Combine(workspaceRoot, raw);
                }

                roots.Add(new Uri(Path.GetFullPath(expanded) + Path.DirectorySeparatorChar));
            }
            catch (Exception exception) when (exception is ArgumentException or
                UriFormatException or NotSupportedException or IOException)
            {
                LogInfo($"Lunil workspace: ignoring invalid library folder '{raw}': {exception.Message}");
            }
        }

        var normalized = roots.Distinct().ToImmutableArray();
        lock (_gate)
        {
            // Documents that came only from library roots no longer configured must
            // leave the index; open documents stay until they are closed.
            foreach (var key in _documents.Where(pair => !pair.Value.IsOpen &&
                         IsUnderAnyRoot(ToLocalPath(pair.Value.Uri), _libraryFolders) &&
                         !IsUnderAnyRoot(ToLocalPath(pair.Value.Uri), normalized))
                         .Select(static pair => pair.Key).ToArray())
            {
                _documents.Remove(key);
                _indexStatus.Remove(key);
                _indexErrors.Remove(key);
                RemoveAnalysis(key);
                DocumentRemoved?.Invoke(new Uri(key, UriKind.Absolute));
            }

            if (!_libraryFolders.SequenceEqual(normalized))
            {
                _libraryGlobalsCache.Clear();
            }

            _libraryFolders = normalized;
            _documentSetGeneration++;
            InvalidateIndexNoLock();
        }

        ScanAllTypeDeclarations();
        ScheduleIndex("configure-library-folders");
        _ = Task.Run(() => LoadLibraryFolders(normalized));
    }

    /// <summary>Re-reads the configured library folders from disk (`Lunil: Reindex Workspace`).</summary>
    public void ReloadLibraryFolders() => ConfigureLibraryFolders(_libraryPaths);

    private void LoadLibraryFolders(ImmutableArray<Uri> folders)
    {
        var paths = new List<string>();
        foreach (var folder in folders)
        {
            paths.AddRange(EnumerateLuaFiles(ToLocalPath(folder)));
        }

        var loaded = new System.Collections.Concurrent.ConcurrentDictionary<string, LspTextDocument>(StringComparer.Ordinal);
        Parallel.ForEach(
            paths,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount) },
            path =>
            {
                try
                {
                    var uri = new Uri(Path.GetFullPath(path));
                    var bytes = File.ReadAllBytes(path);
                    loaded[uri.AbsoluteUri] = bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..]
                        ? new LspTextDocument(uri, 0, DecodeText(bytes), isOpen: false)
                        : new LspTextDocument(uri, 0, bytes, isOpen: false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            });

        LogInfo($"Lunil workspace: loading {folders.Length} library folder(s) -> {paths.Count} .lua files");

        lock (_gate)
        {
            if (_disposed || !_libraryFolders.SequenceEqual(folders))
            {
                return;
            }

            foreach (var pair in loaded)
            {
                if (!_documents.TryGetValue(pair.Key, out var existing) || !existing.IsOpen)
                {
                    _documents[pair.Key] = pair.Value;
                    _indexStatus.TryAdd(pair.Key, FileIndexStatus.Pending);
                }
            }

            _documentSetGeneration++;
            InvalidateIndexNoLock();
        }

        ScanAllTypeDeclarations();
        ScheduleIndex("load-library-folders");
    }

    private static bool IsUnderAnyRoot(string path, ImmutableArray<Uri> roots) =>
        roots.Any(root => IsUnderRoot(path, ToLocalPath(root)));

    /// <summary>
    /// The host globals declared by the current library stub documents. Each stub is
    /// analyzed once per distinct text: its global writes are collected, then a probe
    /// chunk returning those globals reveals their flow types.
    /// </summary>
    private async Task<ImmutableDictionary<string, LuaType>> BuildLibraryGlobalsAsync(
        CancellationToken cancellationToken)
    {
        ImmutableArray<LspTextDocument> libraryDocuments;
        lock (_gate)
        {
            if (_libraryFolders.IsEmpty)
            {
                return ImmutableDictionary<string, LuaType>.Empty;
            }

            libraryDocuments =
            [
                .. _documents.Values
                    .Where(document => document.Uri.IsFile &&
                        IsUnderAnyRoot(ToLocalPath(document.Uri), _libraryFolders))
                    .OrderBy(static document => document.Uri.AbsoluteUri, StringComparer.Ordinal),
            ];
        }

        return await Task.Run(() =>
        {
            var merged = ImmutableDictionary.CreateBuilder<string, LuaType>(StringComparer.Ordinal);
            foreach (var document in libraryDocuments)
            {
                LibraryGlobalsCacheEntry entry;
                lock (_gate)
                {
                    if (!_libraryGlobalsCache.TryGetValue(document.Uri.AbsoluteUri, out entry!) ||
                        entry.Text != document.Text)
                    {
                        entry = null!;
                    }
                }

                if (entry is null)
                {
                    var globals = AnalyzeLibraryGlobals(document);
                    entry = new LibraryGlobalsCacheEntry(document.Text, globals);
                    lock (_gate)
                    {
                        _libraryGlobalsCache[document.Uri.AbsoluteUri] = entry;
                    }
                }

                foreach (var pair in entry.Globals)
                {
                    merged[pair.Key] = pair.Value;
                }
            }

            return merged.ToImmutable();
        }, cancellationToken).ConfigureAwait(false);
    }

    private ImmutableDictionary<string, LuaType> AnalyzeLibraryGlobals(LspTextDocument document)
    {
        try
        {
            var source = LuaSourceDocument.FromBytes(document.Utf8.Span, document.Uri.AbsoluteUri);
            var snapshot = _frontEnd.Process(
                source, LuaFrontEndStage.Analysis, LuaAnalysisEnvironment.Empty);
            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var reference in snapshot.SemanticModel!.UnifiedReferences)
            {
                if (reference.Name is { Length: > 0 } name &&
                    reference.LexicalReference is { } lexical &&
                    lexical.ResolutionKind == LuaNameResolutionKind.Global &&
                    reference.Access.HasFlag(LuaReferenceAccess.Write) &&
                    reference.ReceiverSpan is not { Length: > 0 })
                {
                    names.Add(name);
                }
            }

            if (names.Count == 0)
            {
                return ImmutableDictionary<string, LuaType>.Empty;
            }

            // Appending `return { Game = Game, ... }` to a copy of the stub exposes the
            // globals' final flow types as the chunk export shape — the same extraction
            // the builtin library uses. Stub files must not end in a top-level return.
            var probe = new System.Text.StringBuilder(document.Text.Length + names.Count * 16 + 16);
            probe.Append(document.Text);
            if (!document.Text.EndsWith('\n'))
            {
                probe.Append('\n');
            }

            probe.Append("\nreturn {\n");
            foreach (var name in names)
            {
                probe.Append("  ").Append(name).Append(" = ").Append(name).Append(",\n");
            }

            probe.Append("}\n");
            var probeSnapshot = _frontEnd.Process(
                LuaSourceDocument.FromUtf8(probe.ToString(), document.Uri.AbsoluteUri),
                LuaFrontEndStage.Analysis,
                LuaAnalysisEnvironment.Empty);
            var exported = probeSnapshot.Analysis!.Functions
                .FirstOrDefault(static function => function.FunctionId == 0)
                ?.InferredReturns.GetElementOrNil(0);
            if (exported is not LuaStructuralTableType shape)
            {
                return ImmutableDictionary<string, LuaType>.Empty;
            }

            var globals = ImmutableDictionary.CreateBuilder<string, LuaType>(StringComparer.Ordinal);
            foreach (var field in shape.Fields)
            {
                if (field.Name is not null && names.Contains(field.Name))
                {
                    globals[field.Name] = field.ValueType;
                }
            }

            return globals.ToImmutable();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not AccessViolationException)
        {
            LogInfo($"Lunil workspace: library globals for {document.Uri} failed: {exception.Message}");
            return ImmutableDictionary<string, LuaType>.Empty;
        }
    }

    private static string BuildLibraryGlobalsSignature(ImmutableDictionary<string, LuaType> globals)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var pair in globals.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            builder.Append(pair.Key).Append(':').Append(pair.Value.DisplayName).Append('\n');
        }

        return builder.ToString();
    }

    public bool TryGetDocument(Uri uri, out LspTextDocument document)
    {
        lock (_gate)
        {
            return _documents.TryGetValue(uri.AbsoluteUri, out document!);
        }
    }

    public ImmutableArray<LspTextDocument> GetDocuments()
    {
        lock (_gate)
        {
            return [.. _documents.Values.OrderBy(static document => document.Uri.AbsoluteUri, StringComparer.Ordinal)];
        }
    }

    internal void LoadDocumentsForScale(IEnumerable<LspTextDocument> documents)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            foreach (var document in documents)
            {
                _documents[document.Uri.AbsoluteUri] = document;
                _indexStatus.TryAdd(document.Uri.AbsoluteUri, FileIndexStatus.Pending);
                RemoveAnalysis(document.Uri.AbsoluteUri);
            }

            _documentSetGeneration++;
            InvalidateIndexNoLock();
        }

        // Parity with LoadFolders: a loaded document set must lift the declaration gate
        // so analyses of these documents cannot wait on it forever.
        SignalDeclarationsReady();
    }

    public async Task<LanguageDocumentAnalysis?> GetAnalysisAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        LspTextDocument document;
        int environmentGeneration;
        ImmutableDictionary<string, LuaType> libraryGlobals;
        ImmutableDictionary<string, LuaType> moduleTypes;
        LanguageDocumentAnalysis? cachedCandidate = null;
        lock (_gate)
        {
            if (!_documents.TryGetValue(uri.AbsoluteUri, out document!))
            {
                return null;
            }

            environmentGeneration = _environmentGeneration;
            libraryGlobals = _libraryGlobals;
            moduleTypes = GetModuleTypesNoLock();
            // Cheap identity checks stay inside the global lock; the full byte
            // comparison (which may rehydrate a trimmed document from disk) runs
            // outside so hover/definition never blocks every other request.
            if (_analyses.TryGetValue(uri.AbsoluteUri, out var cached) &&
                cached.Document.Version == document.Version &&
                cached.EnvironmentGeneration == environmentGeneration &&
                cached.Document.ByteLength == document.ByteLength)
            {
                cachedCandidate = cached;
            }
        }

        if (cachedCandidate is not null)
        {
            var candidate = cachedCandidate;
            if (candidate.Document.Utf8.Span.SequenceEqual(document.Utf8.Span))
            {
                lock (_gate)
                {
                    if (_documents.TryGetValue(uri.AbsoluteUri, out var current) &&
                        ReferenceEquals(current, document))
                    {
                        TouchAnalysis(uri.AbsoluteUri);
                        return candidate;
                    }
                }
            }
        }

        // Interactive use pins this document's residency for the next eviction pass.
        document.Touch();

        var module = GetModuleIdentity(document.Uri);
        LuaHostAnalysisContract? hostContract;
        lock (_gate)
        {
            hostContract = _hostContract;
        }
        await Task.CompletedTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // Wait for the initial workspace declaration scan so cross-file @class/@alias/@enum types
        // resolve correctly on the very first analyses (avoids stale LUA6001 races at startup).
        // A scan that never signals must not hang every analysis forever: after the
        // timeout the request proceeds with whatever declarations exist, and a later
        // scan still invalidates these analyses through the environment generation.
        bool declarationsReady;
        lock (_gate)
        {
            declarationsReady = _declarationsReadySet;
        }

        if (!declarationsReady)
        {
            var completed = await Task.WhenAny(
                _declarationsReady.Task,
                Task.Delay(StartupDeclarationsTimeout, cancellationToken)).ConfigureAwait(false);
            if (completed != _declarationsReady.Task)
            {
                cancellationToken.ThrowIfCancellationRequested();
                LogInfo(
                    "Lunil workspace: startup declaration scan timed out; analyzing without cross-file declarations");
            }
        }

        var source = LuaSourceDocument.FromBytes(document.Utf8.Span, document.Uri.AbsoluteUri);
        // Merge workspace globals (game, settings, logger, ... — collected from every
        // module's analysis) with library stub globals: a global defined in any Lua
        // file resolves with its real type in every other file, no configuration.
        ImmutableDictionary<string, LuaType> workspaceGlobals;
        lock (_gate)
        {
            workspaceGlobals = _snapshot?.Globals ??
                ImmutableDictionary<string, LuaType>.Empty.WithComparers(StringComparer.Ordinal);
        }

        var effectiveGlobals = libraryGlobals.Count == 0
            ? workspaceGlobals
            : workspaceGlobals.Count == 0
                ? libraryGlobals
                : libraryGlobals.AddRange(
                    workspaceGlobals.Where(pair =>
                        !libraryGlobals.ContainsKey(pair.Key)));
        var environment = new LuaAnalysisEnvironment
        {
            HostContract = hostContract,
            ModuleTypes = moduleTypes,
            ExternalTypeDeclarations = _externalTypeDeclarations,
            ExternalClassMembers = GetExternalClassMembers(),
            BuiltinGlobals = Builtin.Globals,
            ExternalGlobals = effectiveGlobals,
            ClassFactoryCalls = _classFactoryCalls,
        };
        // The front-end analysis is CPU-bound and synchronous. Run it on the thread pool so a
        // large document (for example a multi-megabyte generated config file) cannot stall the
        // JSON-RPC message loop; otherwise shutdown/exit requests queue behind the analysis and
        // the client kills the server as unresponsive after its stop timeout.
        var compilation = await Task.Run(
            () => CreateAnalysisCompilationResult(_frontEnd.Process(
                source,
                LuaFrontEndStage.Analysis,
                environment,
                cancellationToken)),
            cancellationToken).ConfigureAwait(false);
        var result = new LanguageDocumentAnalysis(document, module, compilation)
        {
            EnvironmentGeneration = environmentGeneration,
        };
        lock (_gate)
        {
            if (_documents.TryGetValue(uri.AbsoluteUri, out var current) &&
                current.Version == document.Version && current.Text == document.Text)
            {
                StoreAnalysis(uri.AbsoluteUri, result);
                return result;
            }
        }

        return null;
    }

    /// <summary>Stores an analysis and keeps the cache inside its byte budget.</summary>
    private void StoreAnalysis(string key, LanguageDocumentAnalysis analysis)
    {
        if (_analyses.TryGetValue(key, out var previous))
        {
            _cachedAnalysisBytes -= EstimateAnalysisBytes(previous);
            _analysisOrder.Remove(key);
        }

        _analyses[key] = analysis;
        _analysisOrder.AddLast(key);
        _cachedAnalysisBytes += EstimateAnalysisBytes(analysis);
        EvictAnalysesOverBudget();
    }

    /// <summary>Marks a cached analysis as most recently used; caller holds <see cref="_gate"/>.</summary>
    private void TouchAnalysis(string key)
    {
        _analysisOrder.Remove(key);
        _analysisOrder.AddLast(key);
    }

    /// <summary>
    /// Evicts least-recently-used analyses until the byte budget holds. Documents
    /// currently open in the editor stay pinned — their analyses back interactive
    /// requests — so a burst of opens cannot evict what the user is looking at; if
    /// the pinned set alone exceeds the budget the cache simply overshoots.
    /// </summary>
    private void EvictAnalysesOverBudget()
    {
        var node = _analysisOrder.First;
        while (_cachedAnalysisBytes > MaximumCachedAnalysisBytes && node is not null)
        {
            var next = node.Next;
            if (!_documents.TryGetValue(node.Value, out var document) || !document.IsOpen)
            {
                if (_analyses.TryGetValue(node.Value, out var evicted))
                {
                    _cachedAnalysisBytes -= EstimateAnalysisBytes(evicted);
                }

                _analysisOrder.Remove(node);
                _analyses.Remove(node.Value);
            }

            node = next;
        }
    }

    private void RemoveAnalysis(string key)
    {
        if (_analyses.TryGetValue(key, out var removed))
        {
            _cachedAnalysisBytes -= EstimateAnalysisBytes(removed);
        }

        _analyses.Remove(key);
        _analysisOrder.Remove(key);
    }

    private void ClearAnalyses()
    {
        _analyses.Clear();
        _analysisOrder.Clear();
        _cachedAnalysisBytes = 0;
    }

    /// <summary>
    /// Mirrors the workspace cache estimator: source-derived retention plus per-symbol
    /// and per-reference overhead. An order-of-magnitude figure is sufficient — it
    /// only drives eviction order, never correctness.
    /// </summary>
    private static long EstimateAnalysisBytes(LanguageDocumentAnalysis analysis) => checked(
        2_048L +
        analysis.Document.ByteLength * 12L +
        analysis.Compilation.SemanticModel.Symbols.Length * 128L +
        analysis.Compilation.SemanticModel.UnifiedReferences.Length * 96L +
        analysis.Compilation.Analysis.Functions.Length * 512L);

    public LuaWorkspaceCompactSnapshot? GetSnapshot()
    {
        lock (_gate)
        {
            // Lazy restore: configuration (require search paths, class factories)
            // arrives after initialize and changes the fingerprint, so the restore
            // retries whenever the fingerprint moves and no round has stored yet.
            if (_snapshot is null && !SnapshotPersistenceDisabled)
            {
                var fingerprint = SnapshotFingerprint();
                if (!string.Equals(fingerprint, _lastSnapshotLoadFingerprintAttempt, StringComparison.Ordinal))
                {
                    _lastSnapshotLoadFingerprintAttempt = fingerprint;
                    TryLoadNavigationSnapshotNoLock();
                }
            }

            return _snapshot;
        }
    }

    /// <summary>
    /// The declared type of a global: embedded stdlib definitions first, then globals
    /// declared by library stub folders (host-injected APIs). Host contract globals
    /// surface through analysis instead.
    /// </summary>
    public bool TryGetKnownGlobalType(string name, out LuaType type)
    {
        if (Builtin.Globals.TryGetValue(name, out type!))
        {
            return true;
        }

        lock (_gate)
        {
            return _libraryGlobals.TryGetValue(name, out type!);
        }
    }

    public Uri? GetUri(string moduleName)
    {
        lock (_gate)
        {
            // The module-name reverse index is rebuilt only when the document set or the
            // folder layout changes; reference-heavy requests query it in tight loops.
            // Virtual documents (builtin pages, host contract) never own module names.
            if (_uriByModuleName is null || _uriIndexGeneration != _documentSetGeneration)
            {
                var builder = ImmutableDictionary.CreateBuilder<string, Uri>(StringComparer.Ordinal);
                foreach (var document in _documents.Values)
                {
                    if (!document.Uri.IsFile)
                    {
                        continue;
                    }

                    var name = GetModuleIdentity(document.Uri).Name;
                    builder.TryAdd(name, document.Uri);
                }

                _uriByModuleName = builder.ToImmutable();
                _uriIndexGeneration = _documentSetGeneration;
            }

            if (_uriByModuleName.TryGetValue(moduleName, out var uri))
            {
                return uri;
            }

            // A restored navigation snapshot knows its modules before the folder scan
            // finishes loading documents; its source identities are the file URIs.
            // Built once per snapshot — GetUri sits in tight reference loops.
            if (_snapshot is not null)
            {
                if (_uriBySnapshotModule is null || !ReferenceEquals(_uriBySnapshotModuleSource, _snapshot))
                {
                    var snapshotBuilder = ImmutableDictionary.CreateBuilder<string, Uri>(StringComparer.Ordinal);
                    foreach (var module in _snapshot.Modules)
                    {
                        if (Uri.TryCreate(module.SourceIdentity, UriKind.Absolute, out var restoredUri))
                        {
                            snapshotBuilder.TryAdd(module.Identity.Name, restoredUri);
                        }
                    }

                    _uriBySnapshotModule = snapshotBuilder.ToImmutable();
                    _uriBySnapshotModuleSource = _snapshot;
                }

                return _uriBySnapshotModule.TryGetValue(moduleName, out var restored) ? restored : null;
            }

            return null;
        }
    }

    public LuaModuleIdentity GetModuleIdentity(Uri uri)
    {
        // Virtual documents (builtin pages, the host contract) get their URI as the
        // module name: unique, and never reachable through a `require` string.
        if (!uri.IsFile)
        {
            return new LuaModuleIdentity(uri.AbsoluteUri);
        }

        var path = Path.GetFullPath(ToLocalPath(uri));
        Uri? owner;
        lock (_gate)
        {
            owner = _folders.Where(folder => IsUnderRoot(path, ToLocalPath(folder)))
                .OrderByDescending(static folder => ToLocalPath(folder).Length)
                .FirstOrDefault() ??
                _libraryFolders.Where(folder => IsUnderRoot(path, ToLocalPath(folder)))
                    .OrderByDescending(static folder => ToLocalPath(folder).Length)
                    .FirstOrDefault();
        }

        var relative = owner is null ? Path.GetFileName(path) : Path.GetRelativePath(ToLocalPath(owner), path);
        var name = Path.ChangeExtension(relative, null)!.Replace(Path.DirectorySeparatorChar, '.')
            .Replace(Path.AltDirectorySeparatorChar, '.');
        if (name.EndsWith(".init", StringComparison.Ordinal))
        {
            name = name[..^5];
        }

        return new LuaModuleIdentity(string.IsNullOrWhiteSpace(name) ? "main" : name);
    }

    public async Task ReindexNowAsync(CancellationToken cancellationToken)
    {
        // A round must not run before the first folder scan has merged its documents:
        // a configuration-triggered round that wins the race against the scan analyzes
        // an empty working set and stores a garbage snapshot.
        Task initialScan;
        lock (_gate)
        {
            initialScan = _initialScanCompletion.Task;
        }

        try
        {
            await initialScan.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // superseded by a newer round
        }

        int generation;
        int documentSetGeneration;
        LuaWorkspace workspace;
        LspTextDocument[] documentSnapshot;
        string[] excludedSnapshot;
        lock (_gate)
        {
            generation = _generation;
            documentSetGeneration = _documentSetGeneration;
            workspace = _workspace;
            documentSnapshot = [.. _documents.Values];
            excludedSnapshot = [.. _excludedFiles.Keys];
        }

        // Reload trimmed closed documents in parallel so the source pass below does not
        // serialize on disk reads, and mark the whole corpus as recently used.
        EnsureDocumentsLoaded(documentSnapshot);

        // Excluded files exist at runtime but sit outside the corpus: requires that name
        // them resolve untyped instead of reporting an unresolved module.
        var externallyProvidedModules = excludedSnapshot
            .Select(static key => new Uri(key, UriKind.Absolute))
            .Where(static uri => uri.IsFile)
            .Select(uri => GetModuleIdentity(uri).Name)
            .ToImmutableHashSet(StringComparer.Ordinal);

        // Building workspace documents copies every source body; doing that under
        // the gate held the message loop hostage for the whole corpus on large
        // workspaces. References are read outside; the document-set guard below
        // discards the result if the corpus changed meanwhile. The canonical byte
        // arrays are wrapped without copying — a rebuild must not duplicate the
        // whole corpus on multi-hundred-megabyte workspaces.
        var documents = documentSnapshot
            .Select(document => new LuaWorkspaceDocument(
                GetModuleIdentity(document.Uri),
                LuaSourceDocument.FromOwnedBytes(document.Utf8Array, document.Uri.AbsoluteUri)))
            .ToImmutableArray();

        // Wait for the startup snapshot prefetch (bounded): its restored contributions
        // make an unchanged corpus's first round fully incremental, and waiting here —
        // on the round's task, off the message loop — costs nothing the prefetch was
        // not already doing in the background.
        Task prefetch;
        lock (_gate)
        {
            prefetch = _prefetchTask;
        }

        try
        {
            await prefetch.WaitAsync(TimeSpan.FromSeconds(120), CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The load is too slow to gate the round; proceed with whatever state is
            // adopted — later rounds pick up anything the prefetch adds mid-flight.
        }

        LuaWorkspaceCompactSnapshot snapshot;
        try
        {
            snapshot = await workspace.AnalyzeCompactAsync(documents, externallyProvidedModules, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) when (!_disposed)
        {
            // A configuration change swapped (and disposed) the workspace while this
            // rebuild was in flight; retry once on the live instance. The generation
            // guard below still discards the result if another change followed.
            lock (_gate)
            {
                workspace = _workspace;
            }

            snapshot = await workspace.AnalyzeCompactAsync(documents, externallyProvidedModules, cancellationToken).ConfigureAwait(false);
        }

        // Library stub globals participate in the environment-generation signature, so
        // edits to host-API stubs invalidate analyses exactly like class-member changes.
        var libraryGlobals = await BuildLibraryGlobalsAsync(cancellationToken).ConfigureAwait(false);
        var librarySignature = BuildLibraryGlobalsSignature(libraryGlobals);

        // The runtime class-edge scan is keyed to the document set; running it here —
        // while the round still has every source resident, before the store's trim —
        // keeps the first navigation request after the store from paying the
        // whole-corpus scan (seconds on large corpora) itself.
        GetRuntimeClassBases();

        // Environment signature, computed OUTSIDE _gate: the snapshot and every input
        // are immutable and the symbol-universe scan takes seconds on large corpora —
        // computing it inside the store's critical section froze every navigation
        // request for the scan's duration. Only the swap and the generation decision
        // stay under the lock.
        var signatureWatch = System.Diagnostics.Stopwatch.StartNew();
        var signature = BuildClassMemberSignature(snapshot) +
            "\n#libraryGlobals\n" + librarySignature +
            "\n#moduleTypes\n" + BuildModuleTypesHash(snapshot).ToString(System.Globalization.CultureInfo.InvariantCulture);
        signatureWatch.Stop();
        if (signatureWatch.ElapsedMilliseconds > 500)
        {
            LogInfo($"Lunil workspace: environment signature took {signatureWatch.ElapsedMilliseconds}ms");
        }

        List<LspTextDocument>? openDocuments = null;
        string? snapshotSavePath = null;
        string? snapshotSaveFingerprint = null;
        var storeWatch = System.Diagnostics.Stopwatch.StartNew();
        lock (_gate)
        {
            // Store unless the document corpus itself changed mid-rebuild. The old
            // `generation == _generation` equality also rejected results whenever a
            // declaration scan or another debounced reindex bumped the generation
            // in flight, which could drop the only rebuild that ever observed the
            // library globals and leave them empty forever. Corpus mutations all
            // schedule a follow-up rebuild, so a skipped store is always retried.
            if (documentSetGeneration == _documentSetGeneration &&
                ReferenceEquals(workspace, _workspace))
            {
                var previousSnapshotWasNull = _snapshot is null;
                _snapshot = snapshot;
                _snapshotRestored = false;
                _libraryGlobals = libraryGlobals;
                // Analyses produced before this snapshot existed were denied workspace
                // member knowledge; when the exported class-member surface or a module's
                // exported type changed, their cache entries are stale and open documents
                // are re-published with it. A snapshot replacing a null one always
                // invalidates: analyses from the null window ran without module types
                // and class-member knowledge even when the signature repeats.
                if (!string.Equals(signature, _externalClassMemberSignature, StringComparison.Ordinal) ||
                    previousSnapshotWasNull)
                {
                    _externalClassMemberSignature = signature;
                    _environmentGeneration++;
                    openDocuments = _documents.Values.Where(static document => document.IsOpen)
                        .OrderBy(static document => document.Uri.AbsoluteUri, StringComparer.Ordinal)
                        .ToList();
                }

                // The compact pass analyzed every tracked document; files that were queued
                // or previously failed are now covered unless a per-document publish is
                // still running for them. Documents that entered through paths that do not
                // register status (for example scale loads) are enrolled here.
                foreach (var document in _documents.Values)
                {
                    _indexStatus.TryAdd(document.Uri.AbsoluteUri, FileIndexStatus.Pending);
                }

                foreach (var key in _indexStatus.Keys.ToArray())
                {
                    if (_indexStatus[key] is FileIndexStatus.Pending or FileIndexStatus.Failed)
                    {
                        _indexStatus[key] = FileIndexStatus.Succeeded;
                        _indexErrors.Remove(key);
                    }
                }

                // Persist the navigation surface so the next server start serves search
                // and navigation before its first rebuild.
                snapshotSaveFingerprint = SnapshotFingerprint();
                snapshotSavePath = SnapshotCachePath(snapshotSaveFingerprint);
            }

            // The rebuild just touched every source; now drop cold closed documents'
            // bytes back under the residency budget.
            TrimClosedDocumentsOverBudget();
        }

        storeWatch.Stop();
        if (storeWatch.ElapsedMilliseconds > 500)
        {
            LogInfo($"Lunil workspace: round store took {storeWatch.ElapsedMilliseconds}ms");
        }

        if (snapshotSavePath is not null && snapshotSaveFingerprint is not null &&
            !SnapshotPersistenceDisabled)
        {
            var snapshotToSave = snapshot;
            var savePath = snapshotSavePath;
            var saveFingerprint = snapshotSaveFingerprint;
            // The snapshot is immutable; serializing on a worker keeps the round's
            // completion off the message loop (large corpora write hundreds of MB).
            _ = Task.Run(() =>
            {
                var saveWatch = System.Diagnostics.Stopwatch.StartNew();
                snapshotToSave.SaveNavigationSnapshot(savePath, saveFingerprint);
                ImmutableArray<Uri> saveFolders;
                lock (_gate)
                {
                    saveFolders = _folders;
                }

                var manifest = ComputeCorpusManifestHash(saveFolders);
                if (manifest is not null)
                {
                    try
                    {
                        File.WriteAllText(SnapshotManifestPath(savePath), manifest);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        // A snapshot without a manifest is not trusted on restore, so a
                        // failed manifest write makes the next start rebuild — safe.
                    }
                }

                saveWatch.Stop();
                LogInfo($"Lunil workspace: navigation snapshot saved in {saveWatch.ElapsedMilliseconds}ms");
            }, CancellationToken.None);
        }

        if (openDocuments is not null)
        {
            foreach (var document in openDocuments)
            {
                _ = AnalyzeAndPublishAsync(document.Uri, document.Version, CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// A stable signature of the exported class-member surface: class names, their member
    /// names, and module ownership. Type drift alone does not invalidate analyses; a member
    /// appearing or disappearing does.
    /// </summary>
    private string BuildClassMemberSignature(LuaWorkspaceCompactSnapshot snapshot)
    {
        // Index the plain (non-nested) export names by module once: iterating the
        // whole symbol universe per class declaration was O(declarations × symbols)
        // — 16 seconds on a 24k-file corpus, computed under _gate during the round
        // store and freezing every concurrent navigation request.
        var plainNamesByModule = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var symbol in snapshot.ExportGraph.Symbols)
        {
            if (symbol.IsExternal || symbol.Path.Length == 0 ||
                symbol.Path.Contains('.', StringComparison.Ordinal))
            {
                continue;
            }

            if (!plainNamesByModule.TryGetValue(symbol.ModuleName, out var names))
            {
                plainNamesByModule[symbol.ModuleName] = names = [];
            }

            names.Add(symbol.Path);
        }

        var builder = new System.Text.StringBuilder();
        foreach (var declaration in GetClassDeclarations())
        {
            builder.Append(declaration.ModuleName).Append(':').Append(declaration.Name).Append('=');
            if (plainNamesByModule.TryGetValue(declaration.ModuleName, out var moduleNames))
            {
                foreach (var name in moduleNames)
                {
                    builder.Append(name).Append(',');
                }
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>The URIs of documents whose last analysis failed.</summary>
    public Uri[] GetFailedDocuments()
    {
        lock (_gate)
        {
            return _indexStatus.Where(pair => pair.Value == FileIndexStatus.Failed)
                .Select(pair => new Uri(pair.Key, UriKind.Absolute))
                .ToArray();
        }
    }

    /// <summary>
    /// Re-analyzes specific documents (a failed-file retry) and waits until their per-document
    /// analysis finished so callers observe the updated index status.
    /// </summary>
    public async Task<int> RetryFilesAsync(IEnumerable<Uri> uris, CancellationToken cancellationToken)
    {
        var tasks = new List<Task>();
        foreach (var uri in uris)
        {
            int version;
            lock (_gate)
            {
                if (!_documents.TryGetValue(uri.AbsoluteUri, out var document))
                {
                    continue;
                }

                version = document.Version;
                _indexStatus[uri.AbsoluteUri] = FileIndexStatus.Pending;
                _indexErrors.Remove(uri.AbsoluteUri);
            }

            tasks.Add(AnalyzeAndPublishAsync(uri, version, cancellationToken));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        ScheduleIndex("retry-files");
        return tasks.Count;
    }

    public void ClearCache()
    {
        LuaWorkspace workspace;
        lock (_gate)
        {
            workspace = _workspace;
            ClearAnalyses();
            InvalidateIndexNoLock();
        }

        // Clearing the shared workspace blocks on its operation gate while a running
        // analysis reports progress back into this workspace's gate; doing that
        // inside the lock inverted the acquisition order and could deadlock. The
        // caches above were already cleared under the gate, so the workspace clear
        // can safely wait outside it. A config swap may dispose the captured
        // instance mid-call; its cache dies with it.
        try
        {
            workspace.ClearCache();
        }
        catch (ObjectDisposedException)
        {
        }

        ScheduleIndex("clear-cache");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _indexCancellation?.Cancel();
            _indexCancellation?.Dispose();
            _workspace.Dispose();
            _declarationsReady.TrySetResult(true);
        }
    }

    /// <summary>
    /// Every annotation-declared <c>@class</c> with its declaring module and base class
    /// names, rebuilt when the cross-file declaration map changes. Drives inheritance-
    /// aware member navigation across modules.
    /// </summary>
    public ImmutableArray<WorkspaceClassDeclaration> GetClassDeclarations()
    {
        lock (_gate)
        {
            if (_classDeclarations is null || _classDeclarationsGeneration != _declarationsGeneration)
            {
                var builder = new List<WorkspaceClassDeclaration>();
                foreach (var pair in _perDocumentDeclarations)
                {
                    var moduleName = GetModuleIdentity(new Uri(pair.Key, UriKind.Absolute)).Name;
                    foreach (var declaration in pair.Value.Values)
                    {
                        if (declaration.Root is LuaClassAnnotationSyntax classAnnotation)
                        {
                            builder.Add(new WorkspaceClassDeclaration(
                                moduleName,
                                classAnnotation.Name,
                                [.. classAnnotation.BaseTypes.Select(static type =>
                                    type is LuaNamedTypeSyntax named ? named.Name : string.Empty)
                                    .Where(static name => !string.IsNullOrEmpty(name))]));
                        }
                    }
                }

                _classDeclarations = [.. builder
                    .OrderBy(static item => item.ModuleName, StringComparer.Ordinal)
                    .ThenBy(static item => item.Name, StringComparer.Ordinal)];
                _classDeclarationsGeneration = _declarationsGeneration;
            }

            return _classDeclarations.Value;
        }
    }

    /// <summary>
    /// Runtime members each annotation-declared class exposes through its declaring module's
    /// exports, rebuilt when the snapshot or the declaration map changes. Member and operator
    /// checks consult this so class-library patterns are not flagged as missing members.
    /// </summary>
    private ImmutableDictionary<string, ImmutableDictionary<string, Lunil.Analysis.LuaType>> GetExternalClassMembers()
    {
        LuaWorkspaceCompactSnapshot? snapshot;
        int declarationsGeneration;
        lock (_gate)
        {
            snapshot = _snapshot;
            if (snapshot is null)
            {
                return ImmutableDictionary<string, ImmutableDictionary<string, Lunil.Analysis.LuaType>>.Empty;
            }

            declarationsGeneration = _declarationsGeneration;
            if (_externalClassMembers is not null &&
                ReferenceEquals(_externalClassMembersSnapshot, snapshot) &&
                _externalClassMembersGeneration == declarationsGeneration)
            {
                return _externalClassMembers;
            }
        }

        // Built outside _gate: every input is an immutable snapshot, and the build
        // walks the whole export-graph universe — holding the gate through it froze
        // every concurrent navigation request for the build's duration.
        var computed = BuildExternalClassMembers(snapshot);

        lock (_gate)
        {
            // Publish only when the inputs did not move during the build; a racing
            // newer snapshot or declaration scan makes the entry stale on arrival
            // and the next caller rebuilds.
            if (ReferenceEquals(_snapshot, snapshot) && _declarationsGeneration == declarationsGeneration)
            {
                _externalClassMembers = computed;
                _externalClassMembersSnapshot = snapshot;
                _externalClassMembersGeneration = declarationsGeneration;
            }

            return computed;
        }
    }

    private ImmutableDictionary<string, ImmutableDictionary<string, Lunil.Analysis.LuaType>> BuildExternalClassMembers(
        LuaWorkspaceCompactSnapshot snapshot)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<string, Lunil.Analysis.LuaType>>(
            StringComparer.Ordinal);
        var declarations = GetClassDeclarations();
        var classesByModule = declarations
            .GroupBy(static item => item.ModuleName, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.ToArray(),
                StringComparer.Ordinal);
        var modulesByClass = declarations
            .GroupBy(static item => item.Name, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.First().ModuleName,
                StringComparer.Ordinal);
        // Mixin edges (`Class.mixin(Character, Movable)`) add the source class's
        // members to the target.
        var mixinTargets = GetClassMixins();
        var runtimeBases = GetRuntimeClassBases();

        // Index the export symbols by module once: the per-class walk below used to
        // rescan the entire symbol universe for every visited module
        // (O(declarations × modules × symbols) — seconds on real corpora).
        var symbolsByModule = new Dictionary<string, List<LuaWorkspaceExportSymbol>>(StringComparer.Ordinal);
        foreach (var symbol in snapshot.ExportGraph.Symbols)
        {
            if (symbol.IsExternal || symbol.Path.Length == 0)
            {
                continue;
            }

            if (!symbolsByModule.TryGetValue(symbol.ModuleName, out var moduleSymbols))
            {
                symbolsByModule[symbol.ModuleName] = moduleSymbols = [];
            }

            moduleSymbols.Add(symbol);
        }

        IEnumerable<string> ClassModules(string className)
        {
            if (!modulesByClass.TryGetValue(className, out var owningModule))
            {
                yield break;
            }

            yield return owningModule;
            if (mixinTargets.TryGetValue(className, out var sources))
            {
                foreach (var mixinSource in sources)
                {
                    if (modulesByClass.TryGetValue(mixinSource, out var sourceModule))
                    {
                        yield return sourceModule;
                    }
                }
            }
        }

        foreach (var declaration in declarations)
        {
            var members = ImmutableDictionary.CreateBuilder<string, Lunil.Analysis.LuaType>(StringComparer.Ordinal);
            // The class's own module first, then its base-class and mixin modules, so
            // inherited runtime members (extend/new on a Class base) resolve too.
            // Nearest wins.
            var visitedModules = new HashSet<string>(StringComparer.Ordinal);
            var visitedClasses = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<string>();
            foreach (var module in ClassModules(declaration.Name))
            {
                pending.Enqueue(module);
            }

            while (pending.Count > 0)
            {
                var module = pending.Dequeue();
                if (!visitedModules.Add(module))
                {
                    continue;
                }

                var classNamesInModule = classesByModule.GetValueOrDefault(module) is { Length: > 0 } moduleClasses
                    ? moduleClasses.Select(static item => item.Name).ToHashSet(StringComparer.Ordinal)
                    : null;
                foreach (var symbol in symbolsByModule.GetValueOrDefault(module) ?? [])
                {
                    var separator = symbol.Path.IndexOf('.');
                    if (separator < 0)
                    {
                        members.TryAdd(symbol.Path, symbol.Type);
                        continue;
                    }

                    // Namespace modules (`return { World = World }`) carry a class's
                    // members under the class's own name; expose them to the class.
                    if (classNamesInModule is not null &&
                        classNamesInModule.Contains(symbol.Path[..separator]))
                    {
                        var rest = symbol.Path[(separator + 1)..];
                        var nested = rest.IndexOf('.');
                        members.TryAdd(nested < 0 ? rest : rest[..nested], symbol.Type);
                    }
                }

                foreach (var @class in classesByModule.GetValueOrDefault(module) ?? [])
                {
                    foreach (var baseName in @class.BaseNames)
                    {
                        if (visitedClasses.Add(baseName))
                        {
                            foreach (var baseOwnerModule in ClassModules(baseName))
                            {
                                pending.Enqueue(baseOwnerModule);
                            }
                        }
                    }

                    // The runtime `local X = Y:extend(...)` edge continues chains the
                    // annotations leave undeclared (`---@class System` built from
                    // `Class:extend`), so `new` and friends resolve for subclasses.
                    if (runtimeBases.TryGetValue(@class.Name, out var runtimeBase) &&
                        visitedClasses.Add(runtimeBase))
                    {
                        foreach (var baseOwnerModule in ClassModules(runtimeBase))
                        {
                            pending.Enqueue(baseOwnerModule);
                        }
                    }
                }
            }

            builder[declaration.Name] = members.ToImmutable();
        }

        return builder.ToImmutable();
    }

    private ImmutableDictionary<string, string>? _runtimeClassBases;
    private ImmutableDictionary<string, string>? _runtimeClassModules;
    private int _runtimeClassBasesGeneration = -1;

    /// <summary>Serializes runtime-edge rebuilds so concurrent callers reuse one scan.</summary>
    private readonly object _runtimeEdgeBuildLock = new();

    /// <summary>
    /// Per-document edge scans, reused while a document's instance (and therefore its
    /// content) is unchanged. Entries pin the document instance only, which _documents
    /// already pins; trimming mutates an instance's buffers without changing identity,
    /// so trimmed documents never reload from disk for a scan.
    /// </summary>
    private Dictionary<string, (LspTextDocument Document, (string ClassName, string? BaseName)[] Edges)> _runtimeEdgeScans =
        new(StringComparer.Ordinal);

    /// <summary>Root export types per module, rebuilt with each compact snapshot.</summary>
    private ImmutableDictionary<string, LuaType>? _moduleTypes;
    private LuaWorkspaceCompactSnapshot? _moduleTypesSnapshot;

    /// <summary>
    /// Runtime class-library edges `local X = Y:extend(...)` keyed by the defined class
    /// name. Annotation chains may stop short of the library root (`---@class System`
    /// with no declared base while the code builds it from `Class:extend`), which would
    /// cut inherited members such as `new` off from every subclass. Unannotated
    /// subclasses chain too: their edges are recorded so a module's class table still
    /// reaches the library root's members.
    /// </summary>
    public ImmutableDictionary<string, string> GetRuntimeClassBases()
    {
        BuildRuntimeClassEdges();
        lock (_gate)
        {
            return _runtimeClassBases!;
        }
    }

    /// <summary>
    /// The module each runtime-extended class (`local X = Y:extend(...)`) is defined in,
    /// so module chains can visit classes the annotations never declare.
    /// </summary>
    public ImmutableDictionary<string, string> GetRuntimeClassModules()
    {
        BuildRuntimeClassEdges();
        lock (_gate)
        {
            return _runtimeClassModules!;
        }
    }

    private void BuildRuntimeClassEdges()
    {
        lock (_runtimeEdgeBuildLock)
        {
            int generation;
            (LspTextDocument Document, string ModuleName)[] entries;
            lock (_gate)
            {
                if (_runtimeClassBases is not null && _runtimeClassBasesGeneration == _documentSetGeneration)
                {
                    return;
                }

                generation = _documentSetGeneration;
                // Full-text scans run outside the gate; only the cheap identity
                // resolution stays under it, so the message loop never waits behind
                // a workspace-wide scan.
                entries = _documents.Values
                    .Select(document => (document, GetModuleIdentity(document.Uri).Name))
                    .ToArray();
            }

            var previousScans = _runtimeEdgeScans;
            var scans = new Dictionary<string, (LspTextDocument Document, (string ClassName, string? BaseName)[] Edges)>(
                StringComparer.Ordinal);
            var bases = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            var modules = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            var edges = new List<(string ClassName, string? BaseName)>();
            ImmutableDictionary<string, bool> factoryCalls;
            lock (_gate)
            {
                factoryCalls = _classFactoryCalls;
            }

            foreach (var (document, moduleName) in entries)
            {
                // A document whose instance is unchanged produced the same edges last
                // time — only new/edited documents (new instances) rescan. Rescanning
                // the whole corpus on every open/close used to put a multi-second
                // stall on the first navigation request after it.
                if (!previousScans.TryGetValue(document.Uri.AbsoluteUri, out var scan) ||
                    !ReferenceEquals(scan.Document, document))
                {
                    edges.Clear();
                    ScanRuntimeClassEdges(document.Utf8.Span, factoryCalls, edges);
                    scan = (document, [.. edges]);
                }

                scans[document.Uri.AbsoluteUri] = scan;
                foreach (var (className, baseName) in scan.Edges)
                {
                    // First definition wins, matching annotation declaration behavior.
                    // Factory definitions carry a null base: they register the class's
                    // module without an inheritance edge.
                    if (baseName is not null)
                    {
                        bases.TryAdd(className, baseName);
                    }

                    modules.TryAdd(className, moduleName);
                }
            }

            lock (_gate)
            {
                // Store the computed snapshot under the generation it was built from,
                // even when the document set moved on mid-scan: readers then get a
                // coherent (slightly stale) view instead of null, and the generation
                // mismatch makes the next caller rebuild from the newer set.
                _runtimeEdgeScans = scans;
                _runtimeClassBases = bases.ToImmutable();
                _runtimeClassModules = modules.ToImmutable();
                _runtimeClassBasesGeneration = generation;
            }
        }
    }

    /// <summary>
    /// Finds <c>local X = Y[:.]extend(</c> edges and configured class-factory definitions
    /// (<c>local X = class("Name", Base, ...)</c>) in raw UTF-8 source. A hand scan keeps
    /// the byte-canonical document representation usable (no Regex over materialized
    /// strings), skips recompiling a regex per rebuild, and rejects the keyword when
    /// it is embedded in a longer identifier — which the old pattern silently accepted.
    /// </summary>
    private static void ScanRuntimeClassEdges(
        ReadOnlySpan<byte> source,
        ImmutableDictionary<string, bool> factoryCalls,
        List<(string ClassName, string? BaseName)> edges)
    {
        var index = 0;
        while (index < source.Length)
        {
            var remaining = source[index..];
            var local = remaining.IndexOf("local"u8);
            if (local < 0)
            {
                return;
            }

            var cursor = index + local;
            index = cursor + 1;
            if (cursor > 0 && IsIdentifierByte(source[cursor - 1]))
            {
                continue;
            }

            cursor += 5;
            if (!SkipWhitespace(source, ref cursor, minimum: 1))
            {
                continue;
            }

            if (!TryReadIdentifier(source, ref cursor, out var className))
            {
                continue;
            }

            if (!SkipWhitespace(source, ref cursor, minimum: 0) || !At(source, ref cursor, (byte)'='))
            {
                continue;
            }

            if (!SkipWhitespace(source, ref cursor, minimum: 0) ||
                !TryReadIdentifier(source, ref cursor, out var rhs))
            {
                continue;
            }

            SkipWhitespace(source, ref cursor, minimum: 0);
            if (At(source, ref cursor, (byte)'(') && factoryCalls.TryGetValue(rhs, out var takesBases))
            {
                // A factory definition registers the class so cross-file chains can
                // resolve the module that declares it — under the local name (the shape
                // base arguments and extend edges reference) AND the declared string
                // name (the analyzer's prototypes are named by that string; the two
                // differ for dotted names like "AI.AbstractMoveAIAction").
                // "bases" factories also record each bare-identifier argument after the
                // name string as an inheritance edge under both names.
                if (ScanFactoryNameArgument(source, ref cursor, out var declaredName))
                {
                    var hasDeclared = declaredName.Length > 0 &&
                        !string.Equals(declaredName, className, StringComparison.Ordinal);
                    edges.Add((className, null));
                    if (hasDeclared)
                    {
                        edges.Add((declaredName, null));
                    }

                    if (takesBases)
                    {
                        foreach (var baseName in ScanFactoryBaseArguments(source, ref cursor))
                        {
                            edges.Add((className, baseName));
                            if (hasDeclared)
                            {
                                edges.Add((declaredName, baseName));
                            }
                        }
                    }
                }
            }
            else
            {
                if (!At(source, ref cursor, (byte)'.') && !At(source, ref cursor, (byte)':'))
                {
                    continue;
                }

                if (!source[cursor..].StartsWith("extend"u8))
                {
                    continue;
                }

                cursor += 6;
                SkipWhitespace(source, ref cursor, minimum: 0);
                if (At(source, ref cursor, (byte)'('))
                {
                    edges.Add((className, rhs));
                }
            }
        }
    }

    /// <summary>
    /// Consumes a factory call's first argument — a double-quoted string literal — and
    /// returns its value. The cursor sits on the first argument (the opening
    /// parenthesis was already consumed by <see cref="At"/>) and is advanced past the
    /// closing quote.
    /// </summary>
    private static bool ScanFactoryNameArgument(ReadOnlySpan<byte> source, ref int cursor, out string name)
    {
        name = string.Empty;
        if (!SkipWhitespace(source, ref cursor, minimum: 0) || !At(source, ref cursor, (byte)'"'))
        {
            return false;
        }

        var closing = source[cursor..].IndexOf((byte)'"');
        if (closing < 0)
        {
            return false;
        }

        name = System.Text.Encoding.UTF8.GetString(source.Slice(cursor, closing));
        cursor += closing + 1;
        return true;
    }

    /// <summary>
    /// Collects the bare-identifier base arguments of a "bases" factory call. Stops at
    /// the first compound or non-identifier argument — bases collected before it are
    /// kept. <see cref="At"/> consumes the separators it matches, so the loop
    /// deliberately leaves the cursor ON a separator for the next iteration.
    /// </summary>
    private static List<string> ScanFactoryBaseArguments(ReadOnlySpan<byte> source, ref int cursor)
    {
        var bases = new List<string>();
        while (cursor < source.Length)
        {
            SkipWhitespace(source, ref cursor, minimum: 0);
            if (At(source, ref cursor, (byte)')'))
            {
                return bases;
            }

            if (At(source, ref cursor, (byte)','))
            {
                continue;
            }

            if (TryReadIdentifier(source, ref cursor, out var baseName))
            {
                var after = cursor;
                SkipWhitespace(source, ref after, minimum: 0);
                if (after < source.Length &&
                    (source[after] == (byte)',' || source[after] == (byte)')'))
                {
                    bases.Add(baseName);
                    // Sit on the separator so the loop top consumes it.
                    cursor = after;
                    continue;
                }
            }

            return bases;
        }

        return bases;
    }

    /// <summary>
    /// Finds <c>[:.]mixin( X, Y</c> pairs in raw UTF-8 source, matching the previous
    /// mixin regex without materializing document text.
    /// </summary>
    private static void ScanClassMixins(
        ReadOnlySpan<byte> source,
        List<(string Target, string Source)> mixins)
    {
        var index = 0;
        while (index < source.Length)
        {
            var remaining = source[index..];
            var mixin = remaining.IndexOf("mixin"u8);
            if (mixin < 0)
            {
                return;
            }

            var cursor = index + mixin;
            index = cursor + 1;
            if (cursor == 0 || source[cursor - 1] is not ((byte)'.') and not ((byte)':'))
            {
                continue;
            }

            cursor += 5;
            if (!SkipWhitespace(source, ref cursor, minimum: 0) || !At(source, ref cursor, (byte)'('))
            {
                continue;
            }

            SkipWhitespace(source, ref cursor, minimum: 0);
            if (!TryReadIdentifier(source, ref cursor, out var target))
            {
                continue;
            }

            SkipWhitespace(source, ref cursor, minimum: 0);
            if (!At(source, ref cursor, (byte)','))
            {
                continue;
            }

            SkipWhitespace(source, ref cursor, minimum: 0);
            if (TryReadIdentifier(source, ref cursor, out var mixinSource))
            {
                mixins.Add((target, mixinSource));
            }
        }
    }

    private static bool IsIdentifierByte(byte value) =>
        value is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'Z' or
            >= (byte)'a' and <= (byte)'z' or (byte)'_';

    private static bool SkipWhitespace(ReadOnlySpan<byte> source, ref int cursor, int minimum)
    {
        var skipped = 0;
        while (cursor < source.Length && source[cursor] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r')
        {
            cursor++;
            skipped++;
        }

        return skipped >= minimum && cursor < source.Length;
    }

    private static bool At(ReadOnlySpan<byte> source, ref int cursor, byte value)
    {
        if (cursor >= source.Length || source[cursor] != value)
        {
            return false;
        }

        cursor++;
        return true;
    }

    private static bool TryReadIdentifier(ReadOnlySpan<byte> source, ref int cursor, out string identifier)
    {
        var start = cursor;
        if (cursor >= source.Length ||
            source[cursor] is not ((byte)'_' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'))
        {
            identifier = string.Empty;
            return false;
        }

        cursor++;
        while (cursor < source.Length && IsIdentifierByte(source[cursor]))
        {
            cursor++;
        }

        identifier = System.Text.Encoding.UTF8.GetString(source[start..cursor]);
        return true;
    }

    /// <summary>
    /// Root export types per module from the compact snapshot. These seed
    /// <c>require</c> results in per-document analysis, so cross-module member calls,
    /// constructor inference, and array element types keep real types instead of
    /// degrading to <c>any</c>. Caller must hold <see cref="_gate"/>.
    /// </summary>
    private ImmutableDictionary<string, LuaType> GetModuleTypesNoLock()
    {
        var snapshot = _snapshot;
        if (snapshot is null)
        {
            return ImmutableDictionary<string, LuaType>.Empty;
        }

        if (_moduleTypes is not null && ReferenceEquals(_moduleTypesSnapshot, snapshot))
        {
            return _moduleTypes;
        }

        var builder = ImmutableDictionary.CreateBuilder<string, LuaType>(StringComparer.Ordinal);
        foreach (var symbol in snapshot.ExportGraph.Symbols)
        {
            if (!symbol.IsExternal && symbol.Path.Length == 0)
            {
                builder.TryAdd(symbol.ModuleName, symbol.Type);
            }
        }

        _moduleTypes = builder.ToImmutable();
        _moduleTypesSnapshot = snapshot;
        return _moduleTypes;
    }

    /// <summary>
    /// A stable hash of every module's root export type, so analyses are invalidated
    /// when a required module's exported type changes — not only when class members do.
    /// Reads only the immutable snapshot; safe with or without <see cref="_gate"/>.
    /// </summary>
    private static ulong BuildModuleTypesHash(LuaWorkspaceCompactSnapshot snapshot)
    {
        unchecked
        {
            var hash = 1469598103934665603UL;
            foreach (var symbol in snapshot.ExportGraph.Symbols)
            {
                if (symbol.IsExternal || symbol.Path.Length != 0)
                {
                    continue;
                }

                foreach (var character in symbol.ModuleName)
                {
                    hash = (hash ^ character) * 1099511628211UL;
                }

                hash = (hash ^ '\n') * 1099511628211UL;
                foreach (var character in symbol.Type.DisplayName)
                {
                    hash = (hash ^ character) * 1099511628211UL;
                }

                hash = (hash ^ '\n') * 1099511628211UL;
            }

            return hash;
        }
    }

    private ImmutableDictionary<string, ImmutableArray<string>>? _classMixins;
    private int _classMixinsGeneration = -1;

    /// <summary>
    /// Mixin edges (`Class.mixin(Target, Source)`) keyed by the target class: the source
    /// classes whose members it exposes. Arguments are matched against declared class
    /// names, which is how the idiom is written in practice.
    /// </summary>
    public ImmutableDictionary<string, ImmutableArray<string>> GetClassMixins()
    {
        var declarations = GetClassDeclarations();
        int generation;
        LspTextDocument[] documents;
        lock (_gate)
        {
            if (_classMixins is not null && _classMixinsGeneration == _declarationsGeneration)
            {
                return _classMixins;
            }

            generation = _declarationsGeneration;
            documents = [.. _documents.Values];
        }

        var modulesByClass = declarations
            .GroupBy(static item => item.Name, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.First().ModuleName,
                StringComparer.Ordinal);
        var targets = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var mixinPairs = new List<(string Target, string Source)>();
        foreach (var document in documents)
        {
            mixinPairs.Clear();
            ScanClassMixins(document.Utf8.Span, mixinPairs);
            foreach (var (targetName, sourceName) in mixinPairs)
            {
                if (modulesByClass.ContainsKey(targetName) && modulesByClass.ContainsKey(sourceName))
                {
                    if (!targets.TryGetValue(targetName, out var sources))
                    {
                        targets[targetName] = sources = [];
                    }

                    sources.Add(sourceName);
                }
            }
        }

        var mixins = targets.ToImmutableDictionary(
            static pair => pair.Key, static pair => pair.Value.ToImmutableArray());
        lock (_gate)
        {
            // Store under the generation the scan used, exactly like the runtime
            // class edges: a concurrent bump leaves a coherent stale snapshot that
            // the next caller rebuilds from.
            _classMixins = mixins;
            _classMixinsGeneration = generation;
            return mixins;
        }
    }

    /// <summary>
    /// The annotation declaration of a named type (class, alias, or enum): the document
    /// that declares it and the span of its root annotation.
    /// </summary>
    public bool TryGetTypeDeclarationLocation(string name, out Uri uri, out Lunil.Core.Text.TextSpan span)
    {
        lock (_gate)
        {
            foreach (var pair in _perDocumentDeclarations)
            {
                if (pair.Value.TryGetValue(name, out var declaration))
                {
                    uri = new Uri(pair.Key, UriKind.Absolute);
                    span = declaration.Root.Span;
                    return true;
                }
            }
        }

        uri = null!;
        span = default;
        return false;
    }

    /// <summary>Returns the per-document index status counts for progress display.</summary>
    public JsonObject GetIndexStatus()
    {
        var failedFiles = new List<(string Uri, string? Error)>();
        var pendingFiles = new List<string>();
        var excludedFiles = new List<(string Uri, string Reason)>();
        int total, succeeded, failed, inProgress, pending;
        lock (_gate)
        {
            total = _indexStatus.Count;
            succeeded = 0;
            failed = 0;
            inProgress = 0;
            pending = 0;
            foreach (var pair in _indexStatus)
            {
                switch (pair.Value)
                {
                    case FileIndexStatus.Succeeded:
                        succeeded++;
                        break;
                    case FileIndexStatus.Failed:
                        failed++;
                        failedFiles.Add((pair.Key, _indexErrors.GetValueOrDefault(pair.Key)));
                        break;
                    case FileIndexStatus.InProgress:
                        inProgress++;
                        break;
                    case FileIndexStatus.Pending:
                        pending++;
                        pendingFiles.Add(pair.Key);
                        break;
                }
            }

            foreach (var pair in _excludedFiles)
            {
                excludedFiles.Add((pair.Key, pair.Value));
            }
        }

        return new JsonObject
        {
            ["total"] = total,
            ["analyzed"] = succeeded + failed,
            ["succeeded"] = succeeded,
            ["failed"] = failed,
            ["inProgress"] = inProgress,
            ["pending"] = pending,
            ["excluded"] = excludedFiles.Count,
            ["failedFiles"] = new JsonArray(failedFiles.Take(200).Select(static item => (JsonNode?)new JsonObject
            {
                ["uri"] = item.Uri,
                ["error"] = item.Error,
            }).ToArray()),
            ["pendingFiles"] = new JsonArray(pendingFiles.Take(200).Select(static item => (JsonNode?)item).ToArray()),
            ["excludedFiles"] = new JsonArray(excludedFiles
                .OrderBy(static item => item.Uri, StringComparer.Ordinal)
                .Take(200)
                .Select(static item => (JsonNode?)new JsonObject
                {
                    ["uri"] = item.Uri,
                    ["reason"] = item.Reason,
                }).ToArray()),
        };
    }

    /// <summary>Re-scans the type declarations of one document and refreshes the cross-file index.</summary>
    public void UpdateDocumentTypeDeclarations(string uri, string text)
    {
        var declarations = ScanTypeDeclarations(System.Text.Encoding.UTF8.GetBytes(text), uri);
        lock (_gate)
        {
            _perDocumentDeclarations[uri] = declarations;
            RebuildExternalTypeDeclarationsNoLock();
        }
    }

    /// <summary>Scans all loaded documents for type declarations. Cheap lexing + annotation parsing only.</summary>
    public void ScanAllTypeDeclarations()
    {
        KeyValuePair<string, LspTextDocument>[] documents;
        LspTextDocument[] openDocuments;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            documents = _documents.ToArray();
            openDocuments = _documents.Values.Where(static document => document.IsOpen).ToArray();
        }

        var results = new System.Collections.Concurrent.ConcurrentDictionary<string, ImmutableDictionary<string, LuaExternalTypeDeclaration>>(
            StringComparer.Ordinal);
        EnsureDocumentsLoaded(documents.Select(static pair => pair.Value).ToArray());
        ReportCorpusProgress(LuaWorkspaceProgressPhase.Declarations, 0, documents.Length);
        var scannedCount = 0;
        Parallel.ForEach(
            documents,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) },
            pair =>
            {
                results[pair.Key] = ScanTypeDeclarations(pair.Value.Utf8.Span, pair.Value.Uri.AbsoluteUri);
                var completed = Interlocked.Increment(ref scannedCount);
                if (completed % CorpusProgressInterval == 0 || completed == documents.Length)
                {
                    ReportCorpusProgress(LuaWorkspaceProgressPhase.Declarations, completed, documents.Length);
                }
            });

        lock (_gate)
        {
            foreach (var pair in results)
            {
                _perDocumentDeclarations[pair.Key] = pair.Value;
            }

            RebuildExternalTypeDeclarationsNoLock();
            _declarationsGeneration++;
            ClearAnalyses();
            InvalidateIndexNoLock();
            TrimClosedDocumentsOverBudget();
        }

        LogInfo(
            $"Lunil workspace: scanned {documents.Length} documents, {_externalTypeDeclarations.Count} external type declarations");

        SignalDeclarationsReady();

        // The declaration map may have grown since documents were first analyzed (for example the
        // workspace folder arrived late). Re-analyze open documents so diagnostics reflect the
        // complete cross-file map; stale in-flight publishes are dropped via the generation check.
        foreach (var document in openDocuments)
        {
            _ = AnalyzeAndPublishAsync(document.Uri, document.Version, CancellationToken.None);
        }
    }

    private ImmutableDictionary<string, LuaExternalTypeDeclaration> ScanTypeDeclarations(
        ReadOnlySpan<byte> utf8,
        string sourceName)
    {
        var source = LuaSourceDocument.FromBytes(utf8, sourceName);
        var lexing = LuaLexer.Lex(source.Text, _frontEnd.Options.Lexer with
        {
            LanguageVersion = _frontEnd.Options.LanguageVersion,
        });
        var annotations = LuaAnnotationParser.Parse(lexing, _frontEnd.Options.Annotations);
        return LuaExternalTypeDeclarations.Collect(annotations);
    }

    private void RebuildExternalTypeDeclarationsNoLock()
    {
        var builder = ImmutableDictionary.CreateBuilder<string, LuaExternalTypeDeclaration>(StringComparer.Ordinal);
        foreach (var pair in _perDocumentDeclarations.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            foreach (var declaration in pair.Value)
            {
                builder.TryAdd(declaration.Key, declaration.Value);
            }
        }

        var rebuilt = builder.ToImmutable();
        // Analyses produced while a declaration was missing (an early pass before its
        // document registered) resolved those names as unknown; a changed declaration
        // surface must invalidate the analysis cache just like a changed member surface.
        var signature = string.Join("\n", rebuilt.Keys.Order(StringComparer.Ordinal));
        if (!string.Equals(signature, _externalTypeDeclarationsSignature, StringComparison.Ordinal))
        {
            _externalTypeDeclarationsSignature = signature;
            _environmentGeneration++;
        }

        _externalTypeDeclarations = rebuilt;
    }

    private string? _externalTypeDeclarationsSignature;

    private async Task AnalyzeAndPublishAsync(Uri uri, int version, CancellationToken cancellationToken)
    {
        // Bound analysis concurrency: after the startup declaration gate lifts, every open document
        // resumes analysis at once; an unbounded burst would saturate the thread pool and stall
        // everything. A modest cap keeps the message loop responsive without throttling real use.
        await _analysisConcurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int declarationsGeneration;
            lock (_gate)
            {
                _indexStatus[uri.AbsoluteUri] = FileIndexStatus.InProgress;
                declarationsGeneration = _declarationsGeneration;
            }

            try
            {
                var analysis = await GetAnalysisAsync(uri, cancellationToken).ConfigureAwait(false);
                if (analysis is null || analysis.Document.Version != version)
                {
                    // Superseded by a newer edit; that edit's task owns the outcome.
                    return;
                }

                if (DiagnosticsPublished is not { } publish)
                {
                    // Analysis completed; there is simply no diagnostic sink attached.
                    lock (_gate)
                    {
                        _indexStatus[uri.AbsoluteUri] = FileIndexStatus.Succeeded;
                        _indexErrors.Remove(uri.AbsoluteUri);
                    }

                    return;
                }

                // If the cross-file declaration map changed while this analysis ran, a re-analysis
                // started after the change will publish the authoritative diagnostics. Drop this
                // potentially stale publish so it cannot overwrite the corrected result.
                lock (_gate)
                {
                    if (declarationsGeneration != _declarationsGeneration)
                    {
                        return;
                    }
                }

                var diagnostics = new JsonArray(analysis.Compilation.Diagnostics.Select(diagnostic =>
                    (JsonNode?)new JsonObject
                    {
                        ["range"] = ToJson(analysis.Document.ToRange(diagnostic.Span)),
                        ["severity"] = ToLspSeverity(diagnostic.Severity),
                        ["code"] = diagnostic.Code,
                        ["source"] = "lunil",
                        ["message"] = diagnostic.Message,
                        ["data"] = new JsonObject { ["phase"] = diagnostic.Phase.ToString() },
                    }).ToArray());
                await publish(uri, version, diagnostics).ConfigureAwait(false);
                lock (_gate)
                {
                    _indexStatus[uri.AbsoluteUri] = FileIndexStatus.Succeeded;
                    _indexErrors.Remove(uri.AbsoluteUri);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and
                not StackOverflowException and not AccessViolationException)
            {
                if (exception is OperationCanceledException || cancellationToken.IsCancellationRequested)
                {
                    // A cancelled round is not an analysis failure: restore the file to
                    // its retryable pending state instead of reporting "canceled" as an
                    // error.
                    lock (_gate)
                    {
                        _indexStatus[uri.AbsoluteUri] = FileIndexStatus.Pending;
                        _indexErrors.Remove(uri.AbsoluteUri);
                    }

                    return;
                }

                // Any failure must land in a retryable Failed state with its reason; a
                // fire-and-forget task that escapes would leave the document stuck in
                // InProgress forever with no visible cause.
                lock (_gate)
                {
                    _indexStatus[uri.AbsoluteUri] = FileIndexStatus.Failed;
                    _indexErrors[uri.AbsoluteUri] = exception.Message;
                }

                Console.Error.WriteLine(
                    $"Lunil workspace: analysis failed for {uri}: {exception.Message}");
            }
        }
        finally
        {
            _analysisConcurrency.Release();
        }
    }

    private void ScheduleIndex(string? reason = null)
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _indexCancellation?.Cancel();
            _indexCancellation?.Dispose();
            _indexCancellation = new CancellationTokenSource();
            token = _indexCancellation.Token;
        }

        LogInfo($"reindex scheduled ({reason ?? "unknown"}): generation {_documentSetGeneration}");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(75, token).ConfigureAwait(false);
                await ReindexNowAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and
                not StackOverflowException and not AccessViolationException)
            {
                // A debounced background rebuild must never surface as an unobserved
                // task exception; the next edit reschedules a fresh attempt anyway.
                Console.Error.WriteLine(
                    $"Lunil workspace: reindex failed: {exception}");
            }
        }, token);
    }

    private void LoadFolders(ImmutableArray<Uri> folders, string reason = "")
    {
        // Reading every .lua file serially is slow on large workspaces; parallelize the I/O so the
        // startup declaration gate lifts sooner and the first diagnostics appear faster.
        var paths = new List<string>();
        foreach (var folder in folders)
        {
            paths.AddRange(EnumerateLuaFiles(ToLocalPath(folder)));
        }

        WorkspaceFileFilter? filter;
        lock (_gate)
        {
            filter = _fileFilter;
        }

        var loaded = new System.Collections.Concurrent.ConcurrentDictionary<string, LspTextDocument>(StringComparer.Ordinal);
        var excluded = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        ReportCorpusProgress(LuaWorkspaceProgressPhase.Loading, 0, paths.Count);
        var readCount = 0;
        Parallel.ForEach(
            paths,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount) },
            path =>
            {
                try
                {
                    var uri = new Uri(Path.GetFullPath(path));
                    var bytes = File.ReadAllBytes(path);
                    // Excluded files (user patterns, generated data) never enter the document
                    // set: their bytes drop here, costing no residency or downstream analysis.
                    var reason = ClassifyExclusion(uri, bytes, filter, folders);
                    if (reason is not null)
                    {
                        excluded[uri.AbsoluteUri] = reason;
                        return;
                    }

                    loaded[uri.AbsoluteUri] = bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..]
                        ? new LspTextDocument(uri, 0, DecodeText(bytes), isOpen: false)
                        : new LspTextDocument(uri, 0, bytes, isOpen: false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
                finally
                {
                    var completed = Interlocked.Increment(ref readCount);
                    if (completed % CorpusProgressInterval == 0 || completed == paths.Count)
                    {
                        ReportCorpusProgress(LuaWorkspaceProgressPhase.Loading, completed, paths.Count);
                    }
                }
            });

        LogInfo(
            $"Lunil workspace: loading {folders.Length} folder(s){(reason.Length == 0 ? string.Empty : $" ({reason})")} -> {paths.Count} .lua files" +
            (excluded.IsEmpty
                ? string.Empty
                : $", {excluded.Count} excluded from analysis ({excluded.Values.Count(static reason => reason == WorkspaceFileFilter.DataExclusionReason)} auto-detected data, {excluded.Values.Count(static reason => reason == WorkspaceFileFilter.PatternExclusionReason)} by pattern)"));

        lock (_gate)
        {
            if (_disposed)
            {
                SignalDeclarationsReady();
                return;
            }

            // Folders changed while this load was in progress; a newer LoadFolders will apply the
            // current set. Do NOT release the declaration gate here: releasing with an empty/partial
            // map lets the first document analyses publish false LUA6001s before the folder scan
            // completes. The gate lifts when a real scan finishes or the first document is opened.
            if (!_folders.SequenceEqual(folders))
            {
                LogInfo("Lunil workspace: folders changed during load; deferring to newer load");
                return;
            }

            if (loaded.IsEmpty && excluded.IsEmpty)
            {
                LogInfo("Lunil workspace: no .lua files under registered folders; declaration gate stays closed until a document opens or a folder is added");
                // No documents under the registered folders. The gate stays closed until a document
                // is opened (its ancestor directory is scanned) or a folder with files is added.
                return;
            }

            foreach (var pair in loaded)
            {
                if (!_documents.TryGetValue(pair.Key, out var existing) || !existing.IsOpen)
                {
                    _documents[pair.Key] = pair.Value;
                }
            }

            // The exclusion map reflects this scan; documents opened mid-load win over exclusion.
            _excludedFiles.Clear();
            foreach (var pair in excluded)
            {
                if (!_documents.TryGetValue(pair.Key, out var existing) || !existing.IsOpen)
                {
                    _excludedFiles[pair.Key] = pair.Value;
                }
            }

            foreach (var pair in _documents)
            {
                _indexStatus.TryAdd(pair.Key, FileIndexStatus.Pending);
            }

            // Reject rebuilds that captured the document set before this merge; a
            // stale (empty or partial) result must never overwrite the snapshot.
            _documentSetGeneration++;
            // A snapshot restored from disk describes exactly this corpus (the
            // fingerprint matched), so the merge keeps it until a fresh round stores.
            InvalidateIndexNoLock(keepStaleSnapshot: _snapshotRestored);
            TrimClosedDocumentsOverBudget();
        }

        ScanAllTypeDeclarations();
        ScheduleIndex("folder-scan-merge");
    }

    /// <summary>
    /// Drops resident sources of least-recently-used closed documents until the
    /// residency budget holds. Trimmed documents reload transparently from disk.
    /// Caller holds <see cref="_gate"/>; trimming performs no I/O.
    /// </summary>
    private void TrimClosedDocumentsOverBudget()
    {
        var total = 0L;
        foreach (var document in _documents.Values)
        {
            total += document.ByteLength;
        }

        if (total <= MaximumDocumentResidencyBytes)
        {
            return;
        }

        foreach (var document in _documents.Values
                     .Where(static document => !document.IsOpen && document.Uri.IsFile && !document.IsTrimmed)
                     .OrderBy(static document => document.LastAccess)
                     .ToList())
        {
            if (total <= MaximumDocumentResidencyBytes)
            {
                break;
            }

            total -= document.ByteLength;
            document.Trim();
        }
    }

    /// <summary>Test hook: applies the residency budget immediately.</summary>
    internal void TrimClosedDocumentsForTest()
    {
        lock (_gate)
        {
            TrimClosedDocumentsOverBudget();
        }
    }

    /// <summary>
    /// Reloads trimmed documents in parallel before a full-corpus pass so rebuilds do
    /// not pay serialized disk reads, and marks everything as recently used.
    /// </summary>
    private static void EnsureDocumentsLoaded(LspTextDocument[] documents)
    {
        var trimmed = 0;
        foreach (var document in documents)
        {
            document.Touch();
            if (document.IsTrimmed)
            {
                trimmed++;
            }
        }

        if (trimmed == 0)
        {
            return;
        }

        Parallel.ForEach(
            documents.Where(static document => document.IsTrimmed),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount) },
            static document => _ = document.Utf8.Span.Length);
    }

    private void SignalDeclarationsReady()
    {
        lock (_gate)
        {
            if (!_declarationsReadySet)
            {
                _declarationsReadySet = true;
                _declarationsReady.TrySetResult(true);
            }
        }
    }

    internal void UpdateSuppressedDiagnosticCodes(IEnumerable<string> codes)
    {
        _suppressedDiagnosticCodes = codes.ToImmutableHashSet(StringComparer.Ordinal);
        lock (_gate)
        {
            InvalidateIndexNoLock();
        }

        ScheduleIndex("configure-suppressed-codes");
    }

    private LuaWorkspace CreateWorkspace(LuaHostAnalysisContract? hostContract) => new(new LuaWorkspaceOptions
    {
        HostContract = hostContract,
        RequireSearchPaths = _requireSearchPaths,
        ClassFactoryCalls = _classFactoryCalls,
        SuppressedDiagnosticCodes = _suppressedDiagnosticCodes,
        // Strongly retained, budget-bounded module caches back the incremental fast
        // paths: cache keys encode each module's content hash and every dependency's
        // export hash, and reusable snapshot projections mean unchanged modules never
        // need their full compiler models again — so the budget only has to cover the
        // changed-module working set between edits, not the whole corpus.
        RetainFullAnalysisCacheResults = true,
        MaximumCacheBytes = LanguageServerMemoryBudget.WorkspaceCacheBytes,
        // Leave headroom for interactive requests (hover, completion) so a full
        // background rebuild cannot saturate every core.
        MaximumParallelism = Math.Max(2, Environment.ProcessorCount - 2),
        DiskCacheDirectory = _snapshotCacheDirectory,
        Progress = new InlineProgress(progress => _ = ProgressReported?.Invoke(progress)),
    });

    /// <summary>
    /// The directory holding the persisted navigation snapshots. One file per workspace
    /// fingerprint (folders + require search paths + class factories + diagnostics
    /// policy): a matching snapshot loads at startup so search and navigation work
    /// before the first rebuild; anything else is pruned. Tests redirect this to a
    /// scratch directory.
    /// </summary>
    private string _snapshotCacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Lunil",
        "language-server-cache");

    /// <summary>Redirects the snapshot cache directory (test isolation).</summary>
    internal void UseSnapshotCacheDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        lock (_gate)
        {
            _snapshotCacheDirectory = directory;
        }
    }

    /// <summary>
    /// The analysis-engine identity that produced persisted snapshots. Module
    /// contributions are cached by source content alone, so a snapshot written by an
    /// older engine would be reused verbatim forever (stale export types, missing
    /// symbols) — every analysis-relevant assembly's MVID makes each rebuild a
    /// different consumer.
    /// </summary>
    private static readonly string AnalyzerStamp =
        string.Join("|",
            typeof(LuaWorkspace).Module.ModuleVersionId,
            typeof(Lunil.Analysis.LuaType).Module.ModuleVersionId,
            typeof(Lunil.EmmyLua.LuaAnnotationSyntax).Module.ModuleVersionId,
            typeof(Lunil.Compiler.LuaCompilationDiagnostic).Module.ModuleVersionId,
            typeof(Lunil.Semantics.Binding.LuaSymbolKind).Module.ModuleVersionId,
            typeof(Lunil.Syntax.Lexing.LuaTokenKind).Module.ModuleVersionId,
            typeof(LanguageServerWorkspace).Module.ModuleVersionId);

    private string SnapshotFingerprint()
    {
        // Caller holds _gate.
        unchecked
        {
            var hash = 1_469_598_103UL;
            void Mix(string value)
            {
                foreach (var character in value)
                {
                    hash = (hash ^ character) * 1_099_511_628_211UL;
                }

                hash = (hash ^ '\0') * 1_099_511_628_211UL;
            }

            Mix(AnalyzerStamp);
            foreach (var folder in _folders.OrderBy(static item => item.AbsoluteUri, StringComparer.Ordinal))
            {
                Mix(folder.AbsoluteUri);
            }

            foreach (var path in _requireSearchPaths)
            {
                Mix(path);
            }

            foreach (var pair in _classFactoryCalls.OrderBy(static item => item.Key, StringComparer.Ordinal))
            {
                Mix(pair.Key);
                Mix(pair.Value ? "bases" : "plain");
            }

            foreach (var code in _suppressedDiagnosticCodes.OrderBy(static item => item, StringComparer.Ordinal))
            {
                Mix(code);
            }

            Mix(_hostContract?.ContractId ?? string.Empty);
            return hash.ToString("X16", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private string SnapshotCachePath(string fingerprint) =>
        Path.Combine(_snapshotCacheDirectory, $"snapshot-{fingerprint}.bin");

    private static string SnapshotManifestPath(string snapshotPath) => snapshotPath + ".manifest";

    /// <summary>
    /// Hashes every .lua file's relative path and size under the workspace folders. The
    /// snapshot fingerprint only covers configuration, so a snapshot saved while a
    /// version-control sync was rewriting the corpus (partially analyzed modules) could
    /// otherwise be restored indefinitely on a loaded machine whose rounds never
    /// complete; any content churn must invalidate the snapshot on restore.
    /// </summary>
    private static string? ComputeCorpusManifestHash(ImmutableArray<Uri> folders)
    {
        try
        {
            var entries = new List<string>();
            for (var index = 0; index < folders.Length; index++)
            {
                var root = ToLocalPath(folders[index]);
                foreach (var path in Directory.EnumerateFiles(root, "*.lua", SearchOption.AllDirectories))
                {
                    entries.Add(
                        $"{index}/{Path.GetRelativePath(root, path).Replace('\\', '/')}|{new FileInfo(path).Length}");
                }
            }

            entries.Sort(StringComparer.Ordinal);
            var bytes = System.Text.Encoding.UTF8.GetBytes(string.Join("\n", entries));
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the snapshot's saved corpus manifest still matches the files on disk.
    /// When the folders cannot be enumerated (test fixtures, detached workspaces) the
    /// manifest could never have been written, so the snapshot is trusted; otherwise a
    /// missing or differing manifest (from before validation, mid-sync saves, or
    /// content churn) invalidates the snapshot.
    /// </summary>
    private static bool SnapshotManifestMatches(string snapshotPath, ImmutableArray<Uri> folders)
    {
        var current = ComputeCorpusManifestHash(folders);
        if (current is null)
        {
            return true;
        }

        try
        {
            var manifestPath = SnapshotManifestPath(snapshotPath);
            return File.Exists(manifestPath) && string.Equals(
                File.ReadAllText(manifestPath).Trim(),
                current,
                StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void DeleteSnapshotFilesNoLock(string snapshotPath)
    {
        // Caller holds _gate or runs on a worker; failures just leave the files for a
        // later prune.
        try
        {
            File.Delete(snapshotPath);
            File.Delete(SnapshotManifestPath(snapshotPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Loads the persisted navigation snapshot for the current workspace fingerprint, if
    /// one exists, and prunes snapshots saved for other fingerprints. Search, definition,
    /// references, and hierarchy work immediately; the first rebuild replaces it.
    /// </summary>
    private void TryLoadNavigationSnapshotNoLock()
    {
        // Caller holds _gate.
        if (SnapshotPersistenceDisabled || _snapshot is not null)
        {
            return;
        }

        var folders = _folders;
        var fingerprint = SnapshotFingerprint();
        var path = SnapshotCachePath(fingerprint);
        // Validate the corpus manifest BEFORE spending minutes loading hundreds of MB
        // that a content change has already invalidated.
        if (File.Exists(path) && !SnapshotManifestMatches(path, folders))
        {
            LogInfo("Lunil workspace: cached snapshot failed the corpus manifest check; rebuilding from scratch");
            DeleteSnapshotFilesNoLock(path);
            return;
        }

        var loaded = LuaWorkspaceCompactSnapshot.TryLoadNavigationSnapshot(path, fingerprint);
        if (loaded is not null)
        {
            _snapshot = loaded;
            _snapshotRestored = true;
            // Only a successful load proves this fingerprint is wanted; pruning on a
            // failed attempt would delete snapshots saved for other configurations
            // (configuration arrives after initialize, so early attempts fail).
            PruneOtherSnapshotFilesNoLock(path);
        }
    }

    private void PruneOtherSnapshotFilesNoLock(string keepPath)
    {
        // Caller holds _gate.
        try
        {
            if (Directory.Exists(_snapshotCacheDirectory))
            {
                var keepName = Path.GetFileName(keepPath);
                foreach (var other in Directory.GetFiles(_snapshotCacheDirectory, "snapshot-*"))
                {
                    var name = Path.GetFileName(other);
                    if (!string.Equals(name, keepName, StringComparison.Ordinal) &&
                        !string.Equals(name, Path.GetFileName(SnapshotManifestPath(keepPath)), StringComparison.Ordinal))
                    {
                        File.Delete(other);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Starts a background restore of the navigation snapshot: the file is hundreds of
    /// MB (or GB with type payloads and contributions) on large corpora, so it loads off
    /// the gate and swaps in atomically when the fingerprint still matches. Called when
    /// configuration settles (require paths, class factories) so the load overlaps editor
    /// startup. The rebuild round awaits <see cref="_prefetchTask"/> so the restored
    /// contributions cover the whole first round instead of racing it.
    /// </summary>
    private Task _prefetchTask = Task.CompletedTask;

    private void PrefetchNavigationSnapshot()
    {
        string fingerprint;
        string path;
        ImmutableArray<Uri> folders;
        lock (_gate)
        {
            if (SnapshotPersistenceDisabled || _snapshot is not null)
            {
                return;
            }

            fingerprint = SnapshotFingerprint();
            path = SnapshotCachePath(fingerprint);
            folders = _folders;
            // Suppress the synchronous lazy load for this fingerprint: an in-flight
            // request would otherwise block the gate for the whole read.
            _lastSnapshotLoadFingerprintAttempt = fingerprint;
        }

        _prefetchTask = Task.Run(() =>
        {
            // Validate the corpus manifest before the expensive read: content churn
            // since the save (version-control sync, partial writes) invalidates the
            // snapshot without loading it.
            if (File.Exists(path) && !SnapshotManifestMatches(path, folders))
            {
                LogInfo("Lunil workspace: cached snapshot failed the corpus manifest check; rebuilding from scratch");
                DeleteSnapshotFilesNoLock(path);
                return;
            }

            var loaded = LuaWorkspaceCompactSnapshot.TryLoadNavigationSnapshot(path, fingerprint);
            if (loaded is null)
            {
                return;
            }

            lock (_gate)
            {
                if (_snapshot is null &&
                    string.Equals(fingerprint, SnapshotFingerprint(), StringComparison.Ordinal))
                {
                    _snapshot = loaded;
                    _snapshotRestored = true;
                    PruneOtherSnapshotFilesNoLock(path);
                    // Restored contributions make the first round fully incremental:
                    // an unchanged corpus reuses every module projection verbatim.
                    if (!loaded.Contributions.IsEmpty)
                    {
                        _workspace.AdoptRestoredContributions(loaded);
                    }
                }
            }
        }, CancellationToken.None);
    }

    private static LuaCompilationResult CreateAnalysisCompilationResult(LuaFrontEndSnapshot snapshot) =>
        new(
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

    private void InvalidateIndexNoLock() => InvalidateIndexNoLock(keepStaleSnapshot: false);

    /// <summary>
    /// Bumps the index generation. Content-level invalidations (edits, a document's
    /// on-disk content changing) keep serving the previous snapshot until the
    /// replacement round stores — navigation during the rebuild window sees slightly
    /// stale data instead of losing all cross-file knowledge (the window is tens of
    /// seconds on large workspaces). Corpus- and config-level changes drop the
    /// snapshot: its module universe no longer matches the live corpus.
    /// </summary>
    private void InvalidateIndexNoLock(bool keepStaleSnapshot)
    {
        _generation++;
        if (!keepStaleSnapshot)
        {
            _snapshot = null;
            _snapshotRestored = false;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static IEnumerable<string> EnumerateLuaFiles(string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            IEnumerable<string> children;
            IEnumerable<string> files;
            try
            {
                children = Directory.EnumerateDirectories(directory).ToArray();
                files = Directory.EnumerateFiles(directory, "*.lua", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }

            foreach (var child in children.Where(child => !ExcludedDirectories.Contains(
                         Path.GetFileName(child),
                         StringComparer.OrdinalIgnoreCase)))
            {
                pending.Push(child);
            }
        }
    }

    private static bool IsUnderRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }

    private static bool PathsEqual(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Loads a document from disk as UTF-8 bytes. UTF-16 BOM files transcode through
    /// the string path (matching the previous <see cref="File.ReadAllText"/> behavior);
    /// everything else — the overwhelming case for Lua sources — stays byte-only.
    /// </summary>
    private static LspTextDocument LoadDiskDocument(Uri uri, int version)
    {
        var bytes = File.ReadAllBytes(ToLocalPath(uri));
        return bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..]
            ? new LspTextDocument(uri, version, DecodeText(bytes), isOpen: false)
            : new LspTextDocument(uri, version, bytes, isOpen: false);
    }

    private static string DecodeText(byte[] bytes) => bytes[1] == 0xFE
        ? System.Text.Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2)
        : System.Text.Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

    internal static JsonObject ToJson(LspRange range) => new()
    {
        ["start"] = new JsonObject { ["line"] = range.Start.Line, ["character"] = range.Start.Character },
        ["end"] = new JsonObject { ["line"] = range.End.Line, ["character"] = range.End.Character },
    };

    private static int ToLspSeverity(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => 1,
        DiagnosticSeverity.Warning => 2,
        DiagnosticSeverity.Information => 3,
        _ => 4,
    };

    private sealed class InlineProgress(Action<LuaWorkspaceProgress> report) : IProgress<LuaWorkspaceProgress>
    {
        public void Report(LuaWorkspaceProgress value) => report(value);
    }
}
