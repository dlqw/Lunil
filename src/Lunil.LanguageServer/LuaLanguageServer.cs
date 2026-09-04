using System.Text.Json;
using System.Text.Json.Nodes;
using Lunil.Workspace;

namespace Lunil.LanguageServer;

internal sealed class LuaLanguageServer : IDisposable
{
    private readonly JsonRpcConnection _connection;
    private readonly LanguageServerWorkspace _workspace = new();
    private readonly ServerLocalization _localization = new();
    private readonly LuaLanguageService _service;
    private readonly CancellationTokenSource _exit = new();
    private bool _initialized;
    private bool _shutdown;
    private bool _supportsWorkDoneProgress;
    private volatile bool _progressReady;
    private LuaWorkspaceProgressPhase? _progressPhase;
    private readonly SemaphoreSlim _progressGate = new(1, 1);

    public LuaLanguageServer(JsonRpcConnection connection)
    {
        _connection = connection;
        _service = new LuaLanguageService(_workspace, _localization);
        _workspace.DiagnosticsPublished = PublishDiagnosticsAsync;
        _workspace.ProgressReported = PublishProgressAsync;
        // Per-document caches follow their documents out of the workspace.
        _workspace.DocumentRemoved += uri => _service.ForgetSemanticTokens(uri);
        // Informational lifecycle messages go to the client log; stderr would surface
        // as errors in the editor's output channel.
        _workspace.InfoLogged = message =>
        {
            if (!_initialized)
            {
                Console.Error.WriteLine(message);
                return;
            }

            _ = _connection.SendNotificationAsync("window/logMessage", new JsonObject
            {
                ["type"] = 3,
                ["message"] = message,
            });
        };
    }

    public CancellationToken ExitToken => _exit.Token;

    /// <summary>Exposes the workspace for diagnostics and tests.</summary>
    internal LanguageServerWorkspace Workspace => _workspace;

    public int ExitCode => _shutdown ? 0 : 1;

    public async Task<JsonNode?> DispatchAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        if (request.Method == "initialize")
        {
            return Initialize(request.Parameters);
        }

        if (request.Method == "exit")
        {
            _exit.Cancel();
            return null;
        }

        if (!_initialized && request.Method != "initialized")
        {
            throw new JsonRpcException(-32002, "Server has not been initialized.");
        }

        if (_shutdown && request.Method != "exit")
        {
            throw new JsonRpcException(-32600, "Server is shutting down.");
        }

        return request.Method switch
        {
            "initialized" => Initialized(),
            "shutdown" => Shutdown(),
            "textDocument/didOpen" => DidOpen(request.Parameters),
            "textDocument/didChange" => DidChange(request.Parameters),
            "textDocument/didClose" => DidClose(request.Parameters),
            "textDocument/didSave" => DidSave(request.Parameters),
            "workspace/didChangeWorkspaceFolders" => DidChangeWorkspaceFolders(request.Parameters),
            "workspace/didChangeWatchedFiles" => DidChangeWatchedFiles(request.Parameters),
            "workspace/didChangeConfiguration" => DidChangeConfiguration(request.Parameters),
            "textDocument/completion" => await _service.CompletionAsync(request.Parameters, cancellationToken)
                .ConfigureAwait(false),
            "textDocument/hover" => await _service.HoverAsync(request.Parameters, cancellationToken)
                .ConfigureAwait(false),
            "textDocument/signatureHelp" => await _service.SignatureHelpAsync(request.Parameters, cancellationToken)
                .ConfigureAwait(false),
            "textDocument/definition" or "textDocument/declaration" or "textDocument/typeDefinition" =>
                await _service.DefinitionAsync(request.Parameters, implementation: false, cancellationToken)
                    .ConfigureAwait(false),
            "textDocument/implementation" => await _service.DefinitionAsync(
                request.Parameters, implementation: true, cancellationToken).ConfigureAwait(false),
            "textDocument/references" => await ReferencesAsync(request.Parameters, cancellationToken)
                .ConfigureAwait(false),
            "textDocument/prepareRename" => await _service.PrepareRenameAsync(request.Parameters, cancellationToken)
                .ConfigureAwait(false),
            "textDocument/rename" => await _service.RenameAsync(request.Parameters, cancellationToken)
                .ConfigureAwait(false),
            "textDocument/documentSymbol" => await _service.DocumentSymbolsAsync(
                request.Parameters, cancellationToken).ConfigureAwait(false),
            "workspace/symbol" => _service.WorkspaceSymbols(
                request.Parameters.TryGetProperty("query", out var query) ? query.GetString() ?? string.Empty : string.Empty),
            "textDocument/semanticTokens/full" => await _service.SemanticTokensAsync(
                request.Parameters, delta: false, cancellationToken).ConfigureAwait(false),
            "textDocument/semanticTokens/full/delta" => await _service.SemanticTokensAsync(
                request.Parameters, delta: true, cancellationToken).ConfigureAwait(false),
            "textDocument/inlayHint" => await _service.InlayHintsAsync(request.Parameters, cancellationToken)
                .ConfigureAwait(false),
            "textDocument/foldingRange" => _service.FoldingRanges(GetUri(request.Parameters)),
            "textDocument/selectionRange" => _service.SelectionRanges(request.Parameters),
            "textDocument/codeAction" => _service.CodeActions(request.Parameters),
            "textDocument/prepareCallHierarchy" => await _service.PrepareCallHierarchyAsync(
                request.Parameters, cancellationToken).ConfigureAwait(false),
            "callHierarchy/incomingCalls" => _service.IncomingCalls(request.Parameters),
            "callHierarchy/outgoingCalls" => _service.OutgoingCalls(request.Parameters),
            "lunil/reindex" => await ReindexAsync(request.Parameters, cancellationToken).ConfigureAwait(false),
            "lunil/clearCache" => ClearCache(),
            "lunil/indexStatus" => _workspace.GetIndexStatus(),
            "lunil/classHierarchy" => await _service.ClassHierarchyAsync(request.Parameters, cancellationToken)
                .ConfigureAwait(false),
            "lunil/builtinSource" => BuiltinLibrarySource(request.Parameters),
            "lunil/virtualHostDocument" => VirtualHostDocument(),
            "$/setTrace" or "$/cancelRequest" => null,
            _ when request.IsNotification => null,
            _ => throw new JsonRpcException(-32601, $"Method not found: {request.Method}"),
        };
    }

    public void Dispose()
    {
        _workspace.Dispose();
        _exit.Dispose();
    }

    private JsonObject Initialize(JsonElement parameters)
    {
        if (_initialized)
        {
            throw new JsonRpcException(-32600, "Initialize may only be requested once.");
        }

        var folders = new List<Uri>();
        _supportsWorkDoneProgress = parameters.TryGetProperty("capabilities", out var clientCapabilities) &&
            clientCapabilities.TryGetProperty("window", out var windowCapabilities) &&
            windowCapabilities.TryGetProperty("workDoneProgress", out var workDoneProgress) &&
            workDoneProgress.ValueKind == JsonValueKind.True;
        if (parameters.TryGetProperty("workspaceFolders", out var workspaceFolders) &&
            workspaceFolders.ValueKind == JsonValueKind.Array)
        {
            folders.AddRange(workspaceFolders.EnumerateArray()
                .Select(static folder => new Uri(folder.GetProperty("uri").GetString()!, UriKind.Absolute)));
        }
        else if (parameters.TryGetProperty("rootUri", out var rootUri) &&
            rootUri.ValueKind == JsonValueKind.String)
        {
            folders.Add(new Uri(rootUri.GetString()!, UriKind.Absolute));
        }
        else if (parameters.TryGetProperty("rootPath", out var rootPath) &&
            rootPath.ValueKind == JsonValueKind.String)
        {
            folders.Add(new Uri(Path.GetFullPath(rootPath.GetString()!)));
        }

        _workspace.Initialize(folders);
        if (parameters.TryGetProperty("initializationOptions", out var initializationOptions) &&
            initializationOptions.TryGetProperty("locale", out var localeElement) &&
            localeElement.ValueKind == JsonValueKind.String &&
            ServerLocalization.TryParse(localeElement.GetString(), out var locale))
        {
            _localization.Locale = locale;
        }

        _initialized = true;
        // Informational: window/logMessage renders as Info in the editor's output
        // channel, unlike stderr which VS Code categorizes as an error regardless of
        // content.
        _ = _connection.SendNotificationAsync("window/logMessage", new JsonObject
        {
            ["type"] = 3, // Info
            ["message"] = $"Lunil language server {GetVersion()} initialized with " +
                $"{folders.Count} workspace folder(s): " +
                string.Join(", ", folders.Select(static folder => folder.AbsoluteUri)),
        });
        return new JsonObject
        {
            ["capabilities"] = Capabilities(),
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "Lunil Language Server",
                ["version"] = GetVersion(),
            },
        };
    }

    private static JsonObject Capabilities() => new()
    {
        ["positionEncoding"] = "utf-16",
        ["textDocumentSync"] = new JsonObject
        {
            ["openClose"] = true,
            ["change"] = 2,
            ["save"] = new JsonObject { ["includeText"] = false },
        },
        ["completionProvider"] = new JsonObject
        {
            ["resolveProvider"] = false,
            ["triggerCharacters"] = new JsonArray(".", ":", "@"),
        },
        ["hoverProvider"] = true,
        ["signatureHelpProvider"] = new JsonObject
        {
            ["triggerCharacters"] = new JsonArray("(", ","),
            ["retriggerCharacters"] = new JsonArray(","),
        },
        ["definitionProvider"] = true,
        ["declarationProvider"] = true,
        ["typeDefinitionProvider"] = true,
        ["implementationProvider"] = true,
        ["referencesProvider"] = true,
        ["renameProvider"] = new JsonObject { ["prepareProvider"] = true },
        ["documentSymbolProvider"] = true,
        ["workspaceSymbolProvider"] = true,
        ["semanticTokensProvider"] = new JsonObject
        {
            ["legend"] = new JsonObject
            {
                // Indexes are positional: annotation token kinds append after the code
                // reference kinds and must never be inserted before them.
                ["tokenTypes"] = new JsonArray(
                    "variable", "parameter", "function", "property", "method",
                    "macro", "class", "type", "typeParameter", "enum", "string", "number",
                    "namespace", "enumMember"),
                ["tokenModifiers"] = new JsonArray(
                    "declaration", "readonly", "modification", "captured", "defaultLibrary"),
            },
            ["range"] = false,
            ["full"] = new JsonObject { ["delta"] = true },
        },
        ["inlayHintProvider"] = true,
        ["callHierarchyProvider"] = true,
        ["foldingRangeProvider"] = true,
        ["selectionRangeProvider"] = true,
        ["codeActionProvider"] = new JsonObject
        {
            ["codeActionKinds"] = new JsonArray("quickfix"),
            ["resolveProvider"] = false,
        },
        ["workspace"] = new JsonObject
        {
            ["workspaceFolders"] = new JsonObject
            {
                ["supported"] = true,
                ["changeNotifications"] = true,
            },
        },
    };

    private JsonNode? Initialized()
    {
        if (_supportsWorkDoneProgress)
        {
            _ = CreateProgressTokenAsync();
        }

        return null;
    }

    private JsonNode? Shutdown()
    {
        _shutdown = true;
        return null;
    }

    private JsonNode? DidOpen(JsonElement parameters)
    {
        var item = RequireTextDocument(parameters);
        _workspace.Open(
            LanguageServerWorkspace.CanonicalUri(new Uri(item.GetProperty("uri").GetString()!, UriKind.Absolute)),
            item.GetProperty("version").GetInt32(),
            item.GetProperty("text").GetString() ?? string.Empty);
        return null;
    }

    private JsonNode? DidChange(JsonElement parameters)
    {
        var item = RequireTextDocument(parameters);
        var uri = LanguageServerWorkspace.CanonicalUri(
            new Uri(item.GetProperty("uri").GetString()!, UriKind.Absolute));
        var changes = parameters.GetProperty("contentChanges").EnumerateArray().Select(change =>
            new LspTextChange(
                change.TryGetProperty("range", out var range) ? ParseRange(range) : null,
                change.GetProperty("text").GetString() ?? string.Empty)).ToArray();
        if (!_workspace.Change(uri, item.GetProperty("version").GetInt32(), changes))
        {
            _ = _connection.SendNotificationAsync("window/logMessage", new JsonObject
            {
                ["type"] = 2,
                ["message"] = $"Ignored stale or unopened document update for {uri}.",
            });
        }

        return null;
    }

    private JsonNode? DidClose(JsonElement parameters)
    {
        _workspace.Close(GetUri(parameters));
        return null;
    }

    private static JsonElement RequireTextDocument(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("textDocument", out var item) ||
            item.ValueKind != JsonValueKind.Object)
        {
            throw new JsonRpcException(-32602, "Invalid params: textDocument is required.");
        }

        return item;
    }

    private JsonNode? DidSave(JsonElement parameters)
    {
        var uri = GetUri(parameters);
        if (parameters.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String &&
            _workspace.TryGetDocument(uri, out var document))
        {
            // The client owns the version counter: a save whose text already matches
            // the synchronized document must not fabricate a version bump that would
            // desynchronize later didChange notifications.
            if (!string.Equals(document.Text, text.GetString(), StringComparison.Ordinal))
            {
                _workspace.Change(uri, document.Version + 1,
                    [new LspTextChange(null, text.GetString()!)]);
            }
        }

        return null;
    }

    private JsonNode? DidChangeWorkspaceFolders(JsonElement parameters)
    {
        var @event = parameters.GetProperty("event");
        if (@event.TryGetProperty("removed", out var removed))
        {
            foreach (var folder in removed.EnumerateArray())
            {
                _workspace.RemoveFolder(new Uri(folder.GetProperty("uri").GetString()!, UriKind.Absolute));
            }
        }

        if (@event.TryGetProperty("added", out var added))
        {
            foreach (var folder in added.EnumerateArray())
            {
                _workspace.AddFolder(new Uri(folder.GetProperty("uri").GetString()!, UriKind.Absolute));
            }
        }

        return null;
    }

    private JsonNode? DidChangeWatchedFiles(JsonElement parameters)
    {
        foreach (var change in parameters.GetProperty("changes").EnumerateArray())
        {
            _workspace.WatchedFileChanged(
                LanguageServerWorkspace.CanonicalUri(new Uri(change.GetProperty("uri").GetString()!, UriKind.Absolute)),
                change.GetProperty("type").GetInt32());
        }

        return null;
    }

    private JsonNode? DidChangeConfiguration(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("settings", out var settings)) return null;
        var lunil = settings.TryGetProperty("lunil", out var nested) ? nested : settings;
        if (lunil.TryGetProperty("locale", out var localeElement) &&
            localeElement.ValueKind == JsonValueKind.String &&
            ServerLocalization.TryParse(localeElement.GetString(), out var locale))
        {
            _localization.Locale = locale;
        }

        if (lunil.TryGetProperty("workspace", out var workspace) &&
            workspace.TryGetProperty("library", out var libraryElement) &&
            libraryElement.ValueKind == JsonValueKind.Array)
        {
            _workspace.ConfigureLibraryFolders(libraryElement.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString()));
        }

        if (lunil.TryGetProperty("require", out var require) &&
            require.TryGetProperty("searchPaths", out var searchPathsElement) &&
            searchPathsElement.ValueKind == JsonValueKind.Array)
        {
            _workspace.ConfigureRequireSearchPaths(searchPathsElement.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString()));
        }

        if (lunil.TryGetProperty("analysis", out var analysis))
        {
            var excludePatterns = analysis.TryGetProperty("exclude", out var excludeElement) &&
                excludeElement.ValueKind == JsonValueKind.Array
                ? excludeElement.EnumerateArray()
                    .Where(static item => item.ValueKind == JsonValueKind.String)
                    .Select(static item => item.GetString())
                : null;
            bool? autoDetect = analysis.TryGetProperty("autoDetectDataFiles", out var autoDetectElement) &&
                autoDetectElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? autoDetectElement.GetBoolean()
                : null;
            _workspace.ConfigureAnalysisExclusions(excludePatterns, autoDetect);

            if (analysis.TryGetProperty("classFactories", out var factoriesElement) &&
                factoriesElement.ValueKind == JsonValueKind.Array)
            {
                var factories = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var entry in factoriesElement.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Object &&
                        entry.TryGetProperty("name", out var nameElement) &&
                        nameElement.ValueKind == JsonValueKind.String)
                    {
                        factories[nameElement.GetString()!] =
                            entry.TryGetProperty("baseArguments", out var baseArgumentsElement) &&
                            baseArgumentsElement.ValueKind == JsonValueKind.True;
                    }
                    else if (entry.ValueKind == JsonValueKind.String)
                    {
                        // A bare name is a factory whose arguments after the class name
                        // are not base classes (singleton/interface styles).
                        factories[entry.GetString()!] = false;
                    }
                }

                _workspace.ConfigureClassFactoryCalls(factories);
            }
        }

        var json = lunil.TryGetProperty("hostContractJson", out var jsonElement) &&
            jsonElement.ValueKind == JsonValueKind.String ? jsonElement.GetString() : null;
        var path = lunil.TryGetProperty("hostContractPath", out var pathElement) &&
            pathElement.ValueKind == JsonValueKind.String ? pathElement.GetString() : null;
        _workspace.ConfigureHostContract(json, path);
        if (lunil.TryGetProperty("server", out var server) &&
            server.TryGetProperty("suppressedDiagnosticCodes", out var codesElement) &&
            codesElement.ValueKind == JsonValueKind.Array)
        {
            var codes = codesElement.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString()!)
                .ToArray();
            _workspace.UpdateSuppressedDiagnosticCodes(codes);
        }

        return null;
    }

    private async Task<JsonNode?> ReferencesAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var result = await _service.ReferencesAsync(parameters, cancellationToken).ConfigureAwait(false);
        if (parameters.TryGetProperty("partialResultToken", out var token) && result is JsonArray locations)
        {
            const int chunkSize = 512;
            for (var index = 0; index < locations.Count; index += chunkSize)
            {
                var chunk = new JsonArray(locations.Skip(index).Take(chunkSize)
                    .Select(static node => node?.DeepClone()).ToArray());
                await _connection.SendNotificationAsync("$/progress", new JsonObject
                {
                    ["token"] = JsonNode.Parse(token.GetRawText()),
                    ["value"] = chunk,
                }, cancellationToken).ConfigureAwait(false);
            }

            return new JsonArray();
        }

        return result;
    }

    private async Task<JsonNode?> ReindexAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        // Optional targeted retries: { "files": [uri, ...] } re-analyzes specific documents
        // (a failed-file retry), and { "retryFailed": true } re-analyzes everything that failed.
        var files = parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("files", out var filesElement) &&
            filesElement.ValueKind == JsonValueKind.Array
            ? filesElement.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => LanguageServerWorkspace.CanonicalUri(
                    new Uri(item.GetString()!, UriKind.Absolute)))
                .ToArray()
            : null;
        var retryFailed = parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("retryFailed", out var retryElement) &&
            retryElement.ValueKind == JsonValueKind.True;
        if (files is { Length: > 0 })
        {
            await _workspace.RetryFilesAsync(files, cancellationToken).ConfigureAwait(false);
        }
        else if (retryFailed)
        {
            await _workspace.RetryFilesAsync(_workspace.GetFailedDocuments(), cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            // An explicit reindex also refreshes library stub folders from disk, so
            // editing host-API declarations needs no restart.
            _workspace.ReloadLibraryFolders();
            await _workspace.ReindexNowAsync(cancellationToken).ConfigureAwait(false);
        }

        return new JsonObject
        {
            ["modules"] = _workspace.GetSnapshot()?.Modules.Length ?? 0,
            ["references"] = _workspace.GetSnapshot()?.Metrics.IndexedReferenceCount ?? 0,
        };
    }

    private JsonObject ClearCache()
    {
        _workspace.ClearCache();
        return new JsonObject { ["cleared"] = true };
    }

    /// <summary>
    /// The readonly source of one builtin library page: `{ "document": "math" }`
    /// (or `"math.lua"`); without a document name, the base globals page.
    /// </summary>
    private static JsonObject BuiltinLibrarySource(JsonElement parameters)
    {
        var name = parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("document", out var documentElement) &&
            documentElement.ValueKind == JsonValueKind.String
                ? documentElement.GetString()
                : "base";
        if (name is null || !BuiltinLibrary.Value.TryGetDocument(name, out var document))
        {
            throw new JsonRpcException(-32602, $"Unknown builtin library document: {name}");
        }

        return new JsonObject
        {
            ["uri"] = document.Uri,
            ["languageId"] = "lua",
            ["text"] = document.Source,
        };
    }

    private JsonObject VirtualHostDocument()
    {
        var snapshot = _workspace.GetSnapshot();
        var lines = snapshot?.ExportGraph.Symbols.Where(static symbol => symbol.IsExternal)
            .OrderBy(static symbol => symbol.ModuleName, StringComparer.Ordinal)
            .ThenBy(static symbol => symbol.Path, StringComparer.Ordinal)
            .Select(static symbol => $"{symbol.ModuleName}.{symbol.Path}: {symbol.Type.DisplayName}") ?? [];
        return new JsonObject
        {
            ["uri"] = "lunil-host:/contract.lua",
            ["languageId"] = "lua",
            ["text"] = string.Join('\n', lines),
        };
    }

    private Task PublishDiagnosticsAsync(Uri uri, int? version, JsonArray diagnostics) =>
        _connection.SendNotificationAsync("textDocument/publishDiagnostics", new JsonObject
        {
            ["uri"] = uri.AbsoluteUri,
            ["version"] = version,
            ["diagnostics"] = diagnostics,
        });

    /// <summary>
    /// Publishes one progress event on both channels: the custom <c>lunil/indexProgress</c>
    /// notification that drives the editor status bar (always, with real n/total counts),
    /// and the standard window work-done progress when the client created the token.
    /// Serialized so concurrent corpus workers cannot interleave events.
    /// </summary>
    private async Task PublishProgressAsync(LuaWorkspaceProgress progress)
    {
        const string token = "lunil-workspace-index";
        try
        {
            await _progressGate.WaitAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            var status = _workspace.GetIndexStatus();
            await _connection.SendNotificationAsync(
                "lunil/indexProgress",
                BuildIndexProgressPayload(progress, status)).ConfigureAwait(false);

            if (!_progressReady)
            {
                return;
            }

            if (_progressPhase != progress.Phase)
            {
                if (_progressPhase is not null)
                {
                    await _connection.SendNotificationAsync("$/progress", new JsonObject
                    {
                        ["token"] = token,
                        ["value"] = new JsonObject { ["kind"] = "end", ["message"] = _progressPhase + " complete" },
                    }).ConfigureAwait(false);
                }

                _progressPhase = progress.Phase;
                await _connection.SendNotificationAsync("$/progress", new JsonObject
                {
                    ["token"] = token,
                    ["value"] = new JsonObject { ["kind"] = "begin", ["title"] = "Lunil " + progress.Phase },
                }).ConfigureAwait(false);
            }

            var percentage = progress.TotalWorkItems == 0 ? 100 :
                (int)Math.Min(100, 100L * progress.CompletedWorkItems / progress.TotalWorkItems);
            await _connection.SendNotificationAsync("$/progress", new JsonObject
            {
                ["token"] = token,
                ["value"] = new JsonObject
                {
                    ["kind"] = "report",
                    ["percentage"] = percentage,
                    ["message"] = $"{progress.CompletedWorkItems}/{progress.TotalWorkItems}",
                },
            }).ConfigureAwait(false);
        }
        finally
        {
            _progressGate.Release();
        }
    }

    /// <summary>
    /// Builds the <c>lunil/indexProgress</c> payload. The status counts are extracted as
    /// primitives on purpose: reusing the JsonNode values from <see cref="LanguageServerWorkspace.GetIndexStatus"/>
    /// inside a new JsonObject throws "The node already has a parent", and since progress
    /// publishing is fire-and-forget, that exception silently swallowed every progress
    /// event — the status bar never showed indexing progress at all.
    /// </summary>
    /// <remarks>
    /// The per-document status ledger flips to Succeeded only when a whole compact round
    /// stores its snapshot, so during a full rebuild it reads "everything pending" — which
    /// next to a live n/total counter looks like a stall. When the event carries round
    /// progress, done/remaining are derived from it so the detail line agrees with the
    /// main counter; the ledger is used only when there is no round in flight.
    /// </remarks>
    internal static JsonObject BuildIndexProgressPayload(
        LuaWorkspaceProgress progress,
        JsonObject status)
    {
        var ledgerSucceeded = (int?)status["succeeded"] ?? 0;
        var ledgerFailed = (int?)status["failed"] ?? 0;
        var ledgerInProgress = (int?)status["inProgress"] ?? 0;
        var ledgerPending = (int?)status["pending"] ?? 0;
        var inRound = progress.TotalWorkItems > 0;
        return new JsonObject
        {
            ["phase"] = progress.Phase.ToString(),
            ["completed"] = progress.CompletedWorkItems,
            ["total"] = progress.TotalWorkItems,
            ["analyzed"] = inRound ? progress.CompletedWorkItems : (int?)status["analyzed"] ?? 0,
            ["succeeded"] = inRound ? progress.CompletedWorkItems : ledgerSucceeded,
            ["failed"] = inRound ? 0 : ledgerFailed,
            ["inProgress"] = inRound ? 0 : ledgerInProgress,
            ["pending"] = inRound
                ? Math.Max(0, progress.TotalWorkItems - progress.CompletedWorkItems)
                : ledgerPending,
            ["excluded"] = (int?)status["excluded"] ?? 0,
        };
    }

    private async Task CreateProgressTokenAsync()
    {
        try
        {
            _ = await _connection.SendRequestAsync("window/workDoneProgress/create", new JsonObject
            {
                ["token"] = "lunil-workspace-index",
            }, _exit.Token).ConfigureAwait(false);
            _progressReady = true;
        }
        catch (Exception exception) when (exception is JsonRpcException or OperationCanceledException)
        {
            _progressReady = false;
        }
    }

    private static Uri GetUri(JsonElement parameters) => LanguageServerWorkspace.CanonicalUri(new(
        parameters.GetProperty("textDocument").GetProperty("uri").GetString()!,
        UriKind.Absolute));

    private static LspPosition ParsePosition(JsonElement element) => new(
        element.GetProperty("line").GetInt32(),
        element.GetProperty("character").GetInt32());

    private static LspRange ParseRange(JsonElement element) => new(
        ParsePosition(element.GetProperty("start")),
        ParsePosition(element.GetProperty("end")));

    private static string GetVersion() =>
        ProductVersion.Current;
}
