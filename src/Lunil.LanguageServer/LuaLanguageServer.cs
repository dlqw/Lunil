using System.Text.Json;
using System.Text.Json.Nodes;
using Lunil.Workspace;

namespace Lunil.LanguageServer;

internal sealed class LuaLanguageServer : IDisposable
{
    private readonly JsonRpcConnection _connection;
    private readonly LanguageServerWorkspace _workspace = new();
    private readonly LuaLanguageService _service;
    private readonly CancellationTokenSource _exit = new();
    private bool _initialized;
    private bool _shutdown;
    private bool _supportsWorkDoneProgress;
    private volatile bool _progressReady;
    private LuaWorkspaceProgressPhase? _progressPhase;

    public LuaLanguageServer(JsonRpcConnection connection)
    {
        _connection = connection;
        _service = new LuaLanguageService(_workspace);
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
            "lunil/builtinSource" => BuiltinLibrarySource(),
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
        _initialized = true;
        Console.Error.WriteLine(
            $"Lunil language server {GetVersion()} initialized with {folders.Count} workspace folder(s): " +
            string.Join(", ", folders.Select(static folder => folder.AbsoluteUri)));
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
                    "macro", "class", "type", "typeParameter", "enum", "string", "number"),
                ["tokenModifiers"] = new JsonArray("declaration", "readonly", "modification", "captured"),
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
        var item = parameters.GetProperty("textDocument");
        _workspace.Open(
            new Uri(item.GetProperty("uri").GetString()!, UriKind.Absolute),
            item.GetProperty("version").GetInt32(),
            item.GetProperty("text").GetString() ?? string.Empty);
        return null;
    }

    private JsonNode? DidChange(JsonElement parameters)
    {
        var item = parameters.GetProperty("textDocument");
        var uri = new Uri(item.GetProperty("uri").GetString()!, UriKind.Absolute);
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

    private JsonNode? DidSave(JsonElement parameters)
    {
        var uri = GetUri(parameters);
        if (parameters.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String &&
            _workspace.TryGetDocument(uri, out var document))
        {
            _workspace.Change(uri, document.Version + 1, [new LspTextChange(null, text.GetString()!)]);
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
                new Uri(change.GetProperty("uri").GetString()!, UriKind.Absolute),
                change.GetProperty("type").GetInt32());
        }

        return null;
    }

    private JsonNode? DidChangeConfiguration(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("settings", out var settings)) return null;
        var lunil = settings.TryGetProperty("lunil", out var nested) ? nested : settings;
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
                .Select(static item => new Uri(item.GetString()!, UriKind.Absolute))
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

    private static JsonObject BuiltinLibrarySource() => new()
    {
        ["uri"] = "lunil-builtin:lua",
        ["languageId"] = "lua",
        ["text"] = BuiltinLibrary.Load().Source,
    };

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

    private async Task PublishProgressAsync(LuaWorkspaceProgress progress)
    {
        const string token = "lunil-workspace-index";
        var status = _workspace.GetIndexStatus();
        if (!_progressReady)
        {
            await _connection.SendNotificationAsync("lunil/indexProgress", new JsonObject
            {
                ["phase"] = progress.Phase.ToString(),
                ["completed"] = progress.CompletedWorkItems,
                ["total"] = progress.TotalWorkItems,
                ["analyzed"] = status["analyzed"],
                ["succeeded"] = status["succeeded"],
                ["failed"] = status["failed"],
                ["inProgress"] = status["inProgress"],
                ["pending"] = status["pending"],
            }).ConfigureAwait(false);
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

    private static Uri GetUri(JsonElement parameters) => new(
        parameters.GetProperty("textDocument").GetProperty("uri").GetString()!,
        UriKind.Absolute);

    private static LspPosition ParsePosition(JsonElement element) => new(
        element.GetProperty("line").GetInt32(),
        element.GetProperty("character").GetInt32());

    private static LspRange ParseRange(JsonElement element) => new(
        ParsePosition(element.GetProperty("start")),
        ParsePosition(element.GetProperty("end")));

    private static string GetVersion() =>
        ProductVersion.Current;
}
