using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Lunil.Analysis;
using Lunil.Compiler;
using Lunil.Core.Diagnostics;
using Lunil.Semantics.Binding;
using Lunil.Workspace;

namespace Lunil.LanguageServer;

internal sealed record LanguageDocumentAnalysis(
    LspTextDocument Document,
    LuaModuleIdentity Module,
    LuaCompilationResult Compilation);

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
    private readonly LuaFrontEndSession _frontEnd = new();
    private readonly Dictionary<string, LspTextDocument> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LanguageDocumentAnalysis> _analyses = new(StringComparer.Ordinal);
    private ImmutableArray<Uri> _folders = [];
    private LuaWorkspace _workspace;
    private LuaHostAnalysisContract? _hostContract;
    private ImmutableHashSet<string> _suppressedDiagnosticCodes =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.Ordinal);
    private LuaWorkspaceCompactSnapshot? _snapshot;
    private CancellationTokenSource? _indexCancellation;
    private int _generation;
    private bool _disposed;

    public LanguageServerWorkspace()
    {
        _ = CompilerWarmup.Value;
        _workspace = CreateWorkspace(hostContract: null);
    }

    public Func<Uri, int?, JsonArray, Task>? DiagnosticsPublished { get; set; }

    public Func<LuaWorkspaceProgress, Task>? ProgressReported { get; set; }

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
            .Select(static uri => new Uri(Path.GetFullPath(uri.LocalPath) + Path.DirectorySeparatorChar))
            .Distinct()
            .ToImmutableArray();
        lock (_gate)
        {
            ThrowIfDisposed();
            _folders = normalized;
            foreach (var key in _documents.Where(pair => !pair.Value.IsOpen &&
                         !normalized.Any(folder => IsUnderRoot(pair.Value.Uri.LocalPath, folder.LocalPath)))
                     .Select(static pair => pair.Key).ToArray())
            {
                _documents.Remove(key);
                _analyses.Remove(key);
            }

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
        var root = Path.GetFullPath(folder.LocalPath);
        lock (_gate)
        {
            _folders = [.. _folders.Where(item => !PathsEqual(item.LocalPath, root))];
            foreach (var key in _documents.Where(pair => !pair.Value.IsOpen &&
                         IsUnderRoot(pair.Value.Uri.LocalPath, root)).Select(static pair => pair.Key).ToArray())
            {
                _documents.Remove(key);
                _analyses.Remove(key);
            }

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
            _analyses.Remove(uri.AbsoluteUri);
            InvalidateIndexNoLock();
        }

        _ = AnalyzeAndPublishAsync(uri, version, CancellationToken.None);
        ScheduleIndex();
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
            _analyses.Remove(uri.AbsoluteUri);
            InvalidateIndexNoLock();
        }

        _ = AnalyzeAndPublishAsync(uri, version, CancellationToken.None);
        ScheduleIndex();
        return true;
    }

    public void Close(Uri uri)
    {
        LspTextDocument? disk = null;
        if (uri.IsFile && File.Exists(uri.LocalPath))
        {
            disk = new LspTextDocument(uri, 0, File.ReadAllText(uri.LocalPath), isOpen: false);
        }

        lock (_gate)
        {
            if (disk is null)
            {
                _documents.Remove(uri.AbsoluteUri);
            }
            else
            {
                _documents[uri.AbsoluteUri] = disk;
            }

            _analyses.Remove(uri.AbsoluteUri);
            InvalidateIndexNoLock();
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

            if (changeType == 3 || !uri.IsFile || !File.Exists(uri.LocalPath))
            {
                _documents.Remove(uri.AbsoluteUri);
            }
            else
            {
                _documents[uri.AbsoluteUri] = new LspTextDocument(
                    uri,
                    0,
                    File.ReadAllText(uri.LocalPath),
                    isOpen: false);
            }

            _analyses.Remove(uri.AbsoluteUri);
            InvalidateIndexNoLock();
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
            _analyses.Clear();
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
                _analyses.Remove(document.Uri.AbsoluteUri);
            }

            InvalidateIndexNoLock();
        }
    }

    public async Task<LanguageDocumentAnalysis?> GetAnalysisAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        LspTextDocument document;
        lock (_gate)
        {
            if (!_documents.TryGetValue(uri.AbsoluteUri, out document!))
            {
                return null;
            }

            if (_analyses.TryGetValue(uri.AbsoluteUri, out var cached) &&
                cached.Document.Version == document.Version && cached.Document.Text == document.Text)
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
        var source = LuaSourceDocument.FromUtf8(document.Text, document.Uri.AbsoluteUri);
        var environment = hostContract is null
            ? LuaAnalysisEnvironment.Empty
            : new LuaAnalysisEnvironment { HostContract = hostContract };
        var compilation = CreateAnalysisCompilationResult(_frontEnd.Process(
            source,
            LuaFrontEndStage.Analysis,
            environment,
            cancellationToken));
        var result = new LanguageDocumentAnalysis(document, module, compilation);
        lock (_gate)
        {
            if (_documents.TryGetValue(uri.AbsoluteUri, out var current) &&
                current.Version == document.Version && current.Text == document.Text)
            {
                _analyses[uri.AbsoluteUri] = result;
                return result;
            }
        }

        return null;
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
            return _documents.Values.FirstOrDefault(document =>
                string.Equals(GetModuleIdentity(document.Uri).Name, moduleName, StringComparison.Ordinal))?.Uri;
        }
    }

    public LuaModuleIdentity GetModuleIdentity(Uri uri)
    {
        var path = uri.IsFile ? Path.GetFullPath(uri.LocalPath) : uri.AbsolutePath;
        Uri? owner;
        lock (_gate)
        {
            owner = _folders.Where(folder => IsUnderRoot(path, folder.LocalPath))
                .OrderByDescending(static folder => folder.LocalPath.Length)
                .FirstOrDefault();
        }

        var relative = owner is null ? Path.GetFileName(path) : Path.GetRelativePath(owner.LocalPath, path);
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
                LuaSourceDocument.FromUtf8(document.Text, document.Uri.AbsoluteUri)))];
        }

        var snapshot = await workspace.AnalyzeCompactAsync(documents, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (generation == _generation && ReferenceEquals(workspace, _workspace))
            {
                _snapshot = snapshot;
            }
        }
    }

    public void ClearCache()
    {
        lock (_gate)
        {
            _workspace.ClearCache();
            _analyses.Clear();
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
        }
    }

    private async Task AnalyzeAndPublishAsync(Uri uri, int version, CancellationToken cancellationToken)
    {
        try
        {
            var analysis = await GetAnalysisAsync(uri, cancellationToken).ConfigureAwait(false);
            if (analysis is null || analysis.Document.Version != version || DiagnosticsPublished is not { } publish)
            {
                return;
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
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
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
        }, token);
    }

    private void LoadFolders(ImmutableArray<Uri> folders)
    {
        var loaded = new Dictionary<string, LspTextDocument>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            foreach (var path in EnumerateLuaFiles(folder.LocalPath))
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
            }
        }

        lock (_gate)
        {
            if (_disposed || !_folders.SequenceEqual(folders))
            {
                return;
            }

            if (loaded.Count == 0)
            {
                // 空文件夹或无可加载文档时不得失效索引：无效失效会推进 generation 并使
                // 进行中的 ReindexNowAsync 因 generation 失配丢弃快照写入（偶发竞态）。
                return;
            }

            foreach (var pair in loaded)
            {
                if (!_documents.TryGetValue(pair.Key, out var existing) || !existing.IsOpen)
                {
                    _documents[pair.Key] = pair.Value;
                }
            }

            InvalidateIndexNoLock();
        }

        ScheduleIndex();
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
