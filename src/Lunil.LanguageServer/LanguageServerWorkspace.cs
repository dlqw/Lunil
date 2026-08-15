using System.Collections.Immutable;
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
    private static readonly Lazy<bool> CompilerWarmup = new(() =>
    {
        _ = new LuaCompiler().CompileUtf8("return nil", "@lunil/language-server-warmup.lua");
        return true;
    }, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly string[] ExcludedDirectories =
        [".git", ".svn", "bin", "obj", "node_modules", ".vscode", ".idea"];
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
    /** Insertion-order keys bounding <see cref="_analyses"/>; oldest entries evict first. */
    private readonly LinkedList<string> _analysisOrder = new();
    private const int MaximumCachedAnalyses = 256;
    private ImmutableDictionary<string, Uri>? _uriByModuleName;
    private int _uriIndexGeneration = -1;
    private int _documentSetGeneration;
    private ImmutableArray<Uri> _folders = [];
    private LuaWorkspace _workspace;
    private LuaHostAnalysisContract? _hostContract;
    private ImmutableHashSet<string> _suppressedDiagnosticCodes =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
    private LuaWorkspaceCompactSnapshot? _snapshot;
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

    public LanguageServerWorkspace()
    {
        _ = CompilerWarmup.Value;
        _workspace = CreateWorkspace(hostContract: null);
    }

    public Func<Uri, int?, JsonArray, Task>? DiagnosticsPublished { get; set; }

    public Func<LuaWorkspaceProgress, Task>? ProgressReported { get; set; }

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

        ScheduleIndex();
        _ = Task.Run(() => LoadFolders(normalized));
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

        ScheduleIndex();
    }

    public void Open(Uri uri, int version, string text)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _documents[uri.AbsoluteUri] = new LspTextDocument(uri, version, text);
            RemoveAnalysis(uri.AbsoluteUri);
            _indexStatus[uri.AbsoluteUri] = FileIndexStatus.Pending;
            _indexErrors.Remove(uri.AbsoluteUri);
            _documentSetGeneration++;
            InvalidateIndexNoLock();
        }

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
        ScheduleIndex();
    }

    /// <summary>Collects type declarations from a document's directory and each ancestor directory.</summary>
    private void ScanAncestorDeclarations(Uri uri)
    {
        // Best-effort scan; failures on inaccessible roots must never block document analysis.
        try
        {
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
                        if (!added.ContainsKey(fileUri.AbsoluteUri))
                        {
                            added[fileUri.AbsoluteUri] = ScanTypeDeclarations(File.ReadAllText(path), fileUri.AbsoluteUri);
                        }
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

            _documents[uri.AbsoluteUri] = document.Apply(version, changes);
            RemoveAnalysis(uri.AbsoluteUri);
            _indexStatus[uri.AbsoluteUri] = FileIndexStatus.Pending;
            _indexErrors.Remove(uri.AbsoluteUri);
            InvalidateIndexNoLock();
        }

        var changedText = _documents.TryGetValue(uri.AbsoluteUri, out var changedDocument)
            ? changedDocument.Text
            : null;
        if (changedText is not null)
        {
            UpdateDocumentTypeDeclarations(uri.AbsoluteUri, changedText);
        }

        _ = AnalyzeAndPublishAsync(uri, version, CancellationToken.None);
        ScheduleIndex();
        return true;
    }

    public void Close(Uri uri)
    {
        LspTextDocument? disk = null;
        if (uri.IsFile && File.Exists(ToLocalPath(uri)))
        {
            disk = new LspTextDocument(uri, 0, File.ReadAllText(ToLocalPath(uri)), isOpen: false);
        }

        lock (_gate)
        {
            if (disk is null)
            {
                _documents.Remove(uri.AbsoluteUri);
                _indexStatus.Remove(uri.AbsoluteUri);
                _indexErrors.Remove(uri.AbsoluteUri);
                DocumentRemoved?.Invoke(uri);
            }
            else
            {
                _documents[uri.AbsoluteUri] = disk;
                _indexStatus[uri.AbsoluteUri] = FileIndexStatus.Pending;
                _indexErrors.Remove(uri.AbsoluteUri);
            }

            RemoveAnalysis(uri.AbsoluteUri);
            _documentSetGeneration++;
            InvalidateIndexNoLock();
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

        if (DiagnosticsPublished is { } publish)
        {
            _ = publish(uri, null, []);
        }

        ScheduleIndex();
    }

    public void WatchedFileChanged(Uri uri, int changeType)
    {
        lock (_gate)
        {
            if (_documents.TryGetValue(uri.AbsoluteUri, out var existing) && existing.IsOpen)
            {
                return;
            }

            if (changeType == 3 || !uri.IsFile || !File.Exists(ToLocalPath(uri)))
            {
                _documents.Remove(uri.AbsoluteUri);
                _indexStatus.Remove(uri.AbsoluteUri);
                _indexErrors.Remove(uri.AbsoluteUri);
                DocumentRemoved?.Invoke(uri);
            }
            else
            {
                _documents[uri.AbsoluteUri] = new LspTextDocument(
                    uri,
                    0,
                    File.ReadAllText(ToLocalPath(uri)),
                    isOpen: false);
                _indexStatus[uri.AbsoluteUri] = FileIndexStatus.Pending;
                _indexErrors.Remove(uri.AbsoluteUri);
            }

            RemoveAnalysis(uri.AbsoluteUri);
            _documentSetGeneration++;
            InvalidateIndexNoLock();
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

        ScheduleIndex();
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

        ScheduleIndex();
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
        lock (_gate)
        {
            if (!_documents.TryGetValue(uri.AbsoluteUri, out document!))
            {
                return null;
            }

            environmentGeneration = _environmentGeneration;
            if (_analyses.TryGetValue(uri.AbsoluteUri, out var cached) &&
                cached.Document.Version == document.Version && cached.Document.Text == document.Text &&
                cached.EnvironmentGeneration == environmentGeneration)
            {
                return cached;
            }
        }

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
        bool declarationsReady;
        lock (_gate)
        {
            declarationsReady = _declarationsReadySet;
        }

        if (!declarationsReady)
        {
            await _declarationsReady.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        var source = LuaSourceDocument.FromBytes(document.Utf8.Span, document.Uri.AbsoluteUri);
        var environment = new LuaAnalysisEnvironment
        {
            HostContract = hostContract,
            ExternalTypeDeclarations = _externalTypeDeclarations,
            ExternalClassMembers = GetExternalClassMembers(),
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

    /// <summary>Stores an analysis and keeps the cache bounded by insertion recency.</summary>
    private void StoreAnalysis(string key, LanguageDocumentAnalysis analysis)
    {
        _analyses[key] = analysis;
        _analysisOrder.Remove(key);
        _analysisOrder.AddLast(key);
        while (_analysisOrder.Count > MaximumCachedAnalyses && _analysisOrder.First is { } oldest)
        {
            _analysisOrder.RemoveFirst();
            _analyses.Remove(oldest.Value);
        }
    }

    private void RemoveAnalysis(string key)
    {
        _analyses.Remove(key);
        _analysisOrder.Remove(key);
    }

    private void ClearAnalyses()
    {
        _analyses.Clear();
        _analysisOrder.Clear();
    }

    public LuaWorkspaceCompactSnapshot? GetSnapshot()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }

    public Uri? GetUri(string moduleName)
    {
        lock (_gate)
        {
            // The module-name reverse index is rebuilt only when the document set or the
            // folder layout changes; reference-heavy requests query it in tight loops.
            if (_uriByModuleName is null || _uriIndexGeneration != _documentSetGeneration)
            {
                var builder = ImmutableDictionary.CreateBuilder<string, Uri>(StringComparer.Ordinal);
                foreach (var document in _documents.Values)
                {
                    var name = GetModuleIdentity(document.Uri).Name;
                    builder.TryAdd(name, document.Uri);
                }

                _uriByModuleName = builder.ToImmutable();
                _uriIndexGeneration = _documentSetGeneration;
            }

            return _uriByModuleName.TryGetValue(moduleName, out var uri) ? uri : null;
        }
    }

    public LuaModuleIdentity GetModuleIdentity(Uri uri)
    {
        var path = uri.IsFile ? Path.GetFullPath(ToLocalPath(uri)) : uri.AbsolutePath;
        Uri? owner;
        lock (_gate)
        {
            owner = _folders.Where(folder => IsUnderRoot(path, ToLocalPath(folder)))
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
        int generation;
        LuaWorkspace workspace;
        ImmutableArray<LuaWorkspaceDocument> documents;
        lock (_gate)
        {
            generation = _generation;
            workspace = _workspace;
            documents = [.. _documents.Values.Select(document => new LuaWorkspaceDocument(
                GetModuleIdentity(document.Uri),
                LuaSourceDocument.FromBytes(document.Utf8.Span, document.Uri.AbsoluteUri)))];
        }

        LuaWorkspaceCompactSnapshot snapshot;
        try
        {
            snapshot = await workspace.AnalyzeCompactAsync(documents, cancellationToken).ConfigureAwait(false);
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

            snapshot = await workspace.AnalyzeCompactAsync(documents, cancellationToken).ConfigureAwait(false);
        }

        List<LspTextDocument>? openDocuments = null;
        lock (_gate)
        {
            if (generation == _generation && ReferenceEquals(workspace, _workspace))
            {
                _snapshot = snapshot;
                // Analyses produced before this snapshot existed were denied workspace
                // member knowledge; when the exported class-member surface changed, their
                // cache entries are stale and open documents are re-published with it.
                var signature = BuildClassMemberSignatureNoLock(snapshot);
                if (!string.Equals(signature, _externalClassMemberSignature, StringComparison.Ordinal))
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
            }
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
    private string BuildClassMemberSignatureNoLock(LuaWorkspaceCompactSnapshot snapshot)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var declaration in GetClassDeclarations())
        {
            builder.Append(declaration.ModuleName).Append(':').Append(declaration.Name).Append('=');
            foreach (var symbol in snapshot.ExportGraph.Symbols)
            {
                if (!symbol.IsExternal &&
                    string.Equals(symbol.ModuleName, declaration.ModuleName, StringComparison.Ordinal) &&
                    symbol.Path.Length > 0 &&
                    !symbol.Path.Contains('.', StringComparison.Ordinal))
                {
                    builder.Append(symbol.Path).Append(',');
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
        ScheduleIndex();
        return tasks.Count;
    }

    public void ClearCache()
    {
        lock (_gate)
        {
            _workspace.ClearCache();
            ClearAnalyses();
            InvalidateIndexNoLock();
        }

        ScheduleIndex();
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
        lock (_gate)
        {
            var snapshot = _snapshot;
            if (snapshot is null)
            {
                return ImmutableDictionary<string, ImmutableDictionary<string, Lunil.Analysis.LuaType>>.Empty;
            }

            if (_externalClassMembers is not null &&
                ReferenceEquals(_externalClassMembersSnapshot, snapshot) &&
                _externalClassMembersGeneration == _declarationsGeneration)
            {
                return _externalClassMembers;
            }

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
            // members to the target. Arguments are matched against declared class names,
            // which is how the idiom is written in practice.
            var mixinTargets = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var mixinPattern = new System.Text.RegularExpressions.Regex(
                @"[.:]mixin\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*,\s*([A-Za-z_][A-Za-z0-9_]*)",
                System.Text.RegularExpressions.RegexOptions.Compiled);
            foreach (var document in _documents.Values)
            {
                foreach (System.Text.RegularExpressions.Match match in mixinPattern.Matches(document.Text))
                {
                    var targetName = match.Groups[1].Value;
                    var sourceName = match.Groups[2].Value;
                    if (modulesByClass.ContainsKey(targetName) && modulesByClass.ContainsKey(sourceName))
                    {
                        if (!mixinTargets.TryGetValue(targetName, out var sources))
                        {
                            mixinTargets[targetName] = sources = [];
                        }

                        sources.Add(sourceName);
                    }
                }
            }

            IEnumerable<string> ClassModules(string className)
            {
                if (!modulesByClass.TryGetValue(className, out var owningModule))
                {
                    yield break;
                }

                yield return owningModule;
                foreach (var mixinSource in mixinTargets.GetValueOrDefault(className) ?? [])
                {
                    if (modulesByClass.TryGetValue(mixinSource, out var sourceModule))
                    {
                        yield return sourceModule;
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

                    foreach (var symbol in snapshot.ExportGraph.Symbols)
                    {
                        if (symbol.IsExternal ||
                            !string.Equals(symbol.ModuleName, module, StringComparison.Ordinal) ||
                            symbol.Path.Contains('.', StringComparison.Ordinal) ||
                            symbol.Path.Length == 0)
                        {
                            continue;
                        }

                        members.TryAdd(symbol.Path, symbol.Type);
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
                    }
                }

                builder[declaration.Name] = members.ToImmutable();
            }

            _externalClassMembers = builder.ToImmutable();
            _externalClassMembersSnapshot = snapshot;
            _externalClassMembersGeneration = _declarationsGeneration;
            return _externalClassMembers;
        }
    }

    /// <summary>Returns the per-document index status counts for progress display.</summary>
    public JsonObject GetIndexStatus()    {
        var failedFiles = new List<(string Uri, string? Error)>();
        var pendingFiles = new List<string>();
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
        }

        return new JsonObject
        {
            ["total"] = total,
            ["analyzed"] = succeeded + failed,
            ["succeeded"] = succeeded,
            ["failed"] = failed,
            ["inProgress"] = inProgress,
            ["pending"] = pending,
            ["failedFiles"] = new JsonArray(failedFiles.Take(200).Select(static item => (JsonNode?)new JsonObject
            {
                ["uri"] = item.Uri,
                ["error"] = item.Error,
            }).ToArray()),
            ["pendingFiles"] = new JsonArray(pendingFiles.Take(200).Select(static item => (JsonNode?)item).ToArray()),
        };
    }

    /// <summary>Re-scans the type declarations of one document and refreshes the cross-file index.</summary>
    public void UpdateDocumentTypeDeclarations(string uri, string text)
    {
        var declarations = ScanTypeDeclarations(text, uri);
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
        Parallel.ForEach(
            documents,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2) },
            pair => results[pair.Key] = ScanTypeDeclarations(pair.Value.Text, pair.Value.Uri.AbsoluteUri));

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

    private ImmutableDictionary<string, LuaExternalTypeDeclaration> ScanTypeDeclarations(string text, string sourceName)
    {
        var source = LuaSourceDocument.FromUtf8(text, sourceName);
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

        _externalTypeDeclarations = builder.ToImmutable();
    }

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

    private void ScheduleIndex()
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
                Console.Error.WriteLine($"Lunil workspace: reindex failed: {exception.Message}");
            }
        }, token);
    }

    private void LoadFolders(ImmutableArray<Uri> folders)
    {
        // Reading every .lua file serially is slow on large workspaces; parallelize the I/O so the
        // startup declaration gate lifts sooner and the first diagnostics appear faster.
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
                    loaded[uri.AbsoluteUri] = new LspTextDocument(
                        uri,
                        0,
                        File.ReadAllText(path),
                        isOpen: false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            });

        LogInfo(
            $"Lunil workspace: loading {folders.Length} folder(s) -> {paths.Count} .lua files");

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

            if (loaded.IsEmpty)
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

            foreach (var pair in _documents)
            {
                _indexStatus.TryAdd(pair.Key, FileIndexStatus.Pending);
            }

            InvalidateIndexNoLock();
        }

        ScanAllTypeDeclarations();
        ScheduleIndex();
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

        ScheduleIndex();
    }

    private LuaWorkspace CreateWorkspace(LuaHostAnalysisContract? hostContract) => new(new LuaWorkspaceOptions
    {
        HostContract = hostContract,
        SuppressedDiagnosticCodes = _suppressedDiagnosticCodes,
        DiskCacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Lunil",
            "language-server-cache"),
        Progress = new InlineProgress(progress => _ = ProgressReported?.Invoke(progress)),
    });

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

    private void InvalidateIndexNoLock()
    {
        _generation++;
        _snapshot = null;
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
