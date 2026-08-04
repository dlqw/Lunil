using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lunil.Analysis;

namespace Lunil.LanguageServer.Tests;

public sealed class LanguageServerTests
{
    [Fact]
    public void Utf16PositionsRoundTripUtf8BytesAndIncrementalChanges()
    {
        var document = new LspTextDocument(new Uri("file:///unicode.lua"), 1, "a😀b\r\nç");

        Assert.Equal(5, document.ToByteOffset(new LspPosition(0, 3)));
        Assert.Equal(new LspPosition(0, 3), document.ToPosition(5));
        Assert.Equal(new LspPosition(1, 1), document.ToPosition(document.ByteLength));

        var updated = document.Apply(2,
        [
            new LspTextChange(
                new LspRange(new LspPosition(0, 3), new LspPosition(0, 4)),
                "value"),
        ]);
        Assert.Equal("a😀value\r\nç", updated.Text);
        Assert.Equal(2, updated.Version);
    }

    [Fact]
    public void PositionInsideSurrogatePairClampsToCodePointBoundary()
    {
        var document = new LspTextDocument(new Uri("file:///unicode.lua"), 1, "😀");

        Assert.Equal(0, document.ToByteOffset(new LspPosition(0, 1)));
        Assert.Equal(new LspPosition(0, 0), document.ToPosition(2));
    }

    [Fact]
    public async Task WorkspaceRejectsStaleVersionsAndKeepsUnsavedOverlay()
    {
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([]);
        var uri = new Uri("file:///overlay.lua");
        workspace.Open(uri, 4, "local value = 1\nreturn value");

        Assert.False(workspace.Change(uri, 4, [new LspTextChange(null, "return nil")]));
        Assert.True(workspace.Change(uri, 5,
        [
            new LspTextChange(
                new LspRange(new LspPosition(0, 6), new LspPosition(0, 11)),
                "answer"),
            new LspTextChange(
                new LspRange(new LspPosition(1, 7), new LspPosition(1, 12)),
                "answer"),
        ]));

        var analysis = await workspace.GetAnalysisAsync(uri, CancellationToken.None);
        Assert.NotNull(analysis);
        Assert.Equal(5, analysis.Document.Version);
        Assert.Contains(analysis.Compilation.SemanticModel.Symbols, static symbol => symbol.Name == "answer");
    }

    [Fact]
    public async Task DocumentProvidersUseAnalysisOnlyFrontEndWithoutAHostContract()
    {
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([]);
        var uri = new Uri("file:///injected-globals.lua");
        workspace.Open(
            uri,
            1,
            "local function render(value) return NativeBridge.draw(value) end\n" +
            "local result = render(InjectedGameState.value)\n" +
            "return result");
        var service = new LuaLanguageService(workspace);
        var parameters = Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
            position = new { line = 1, character = 8 },
        });

        var analysis = await workspace.GetAnalysisAsync(uri, CancellationToken.None);
        var symbols = await service.DocumentSymbolsAsync(parameters, CancellationToken.None);
        var semanticTokens = await service.SemanticTokensAsync(parameters, false, CancellationToken.None);
        var inlayHints = await service.InlayHintsAsync(parameters, CancellationToken.None);

        Assert.NotNull(analysis);
        Assert.True(analysis.Compilation.IsAnalysisOnly);
        Assert.Equal(Lunil.Compiler.LuaFrontEndStage.Analysis, analysis.Compilation.FrontEndSnapshot!.Stage);
        Assert.Null(analysis.Compilation.Module);
        Assert.NotEmpty(symbols!.AsArray());
        Assert.NotEmpty(semanticTokens!["data"]!.AsArray());
        Assert.NotEmpty(inlayHints!.AsArray());
    }

    [Fact]
    public async Task DocumentSymbolsAndSemanticTokensWorkWithoutACursorPosition()
    {
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([]);
        var uri = new Uri("file:///no-position.lua");
        workspace.Open(uri, 1, "local function render() return 1 end\nreturn render()");
        var service = new LuaLanguageService(workspace);
        var parameters = Element(new { textDocument = new { uri = uri.AbsoluteUri } });

        var symbols = await service.DocumentSymbolsAsync(parameters, CancellationToken.None);
        var semanticTokens = await service.SemanticTokensAsync(parameters, false, CancellationToken.None);

        Assert.NotEmpty(symbols!.AsArray());
        Assert.NotEmpty(semanticTokens!["data"]!.AsArray());
    }

    [Fact]
    public async Task HoverReferencesAndCapturedLocalRenameUseStableBinding()
    {
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([]);
        var uri = new Uri("file:///capture.lua");
        workspace.Open(uri, 1, "local value = 1\nlocal function read() return value end\nreturn value");
        var service = new LuaLanguageService(workspace);
        var parameters = Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
            position = new { line = 1, character = 29 },
            context = new { includeDeclaration = true },
        });

        var hover = await service.HoverAsync(parameters, CancellationToken.None);
        var references = await service.ReferencesAsync(parameters, CancellationToken.None);
        var renameParameters = Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
            position = new { line = 1, character = 29 },
            newName = "captured",
        });
        var rename = await service.RenameAsync(renameParameters, CancellationToken.None);

        Assert.Contains("Upvalue", hover!.ToJsonString(), StringComparison.Ordinal);
        Assert.True(references!.AsArray().Count >= 2);
        Assert.Contains("captured", rename!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SemanticTokenDeltaReplacesPriorVersionDeterministically()
    {
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([]);
        var uri = new Uri("file:///tokens.lua");
        workspace.Open(uri, 1, "local value = 1\nreturn value");
        var service = new LuaLanguageService(workspace);
        var parameters = Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
            position = new { line = 0, character = 0 },
        });
        var full = await service.SemanticTokensAsync(parameters, false, CancellationToken.None);
        var previousId = full!["resultId"]!.GetValue<string>();
        Assert.True(workspace.Change(uri, 2,
            [new LspTextChange(new LspRange(new LspPosition(0, 6), new LspPosition(0, 11)), "answer")]));
        var deltaParameters = Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
            position = new { line = 0, character = 0 },
            previousResultId = previousId,
        });

        var delta = await service.SemanticTokensAsync(deltaParameters, true, CancellationToken.None);

        Assert.NotEmpty(delta!["edits"]!.AsArray());
    }

    [Fact]
    public async Task CompactIndexSupportsCrossModuleWorkspaceSymbolsAndReferences()
    {
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([]);
        var moduleUri = new Uri("file:///service.lua");
        var appUri = new Uri("file:///app.lua");
        workspace.Open(moduleUri, 1, "local M = {}\nfunction M.run() return 1 end\nreturn M");
        workspace.Open(appUri, 1, "local service = require('service')\nreturn service.run()");

        await workspace.ReindexNowAsync(CancellationToken.None);
        var snapshot = workspace.GetSnapshot();

        Assert.NotNull(snapshot);
        Assert.Contains(snapshot.ExportGraph.Symbols, static symbol => symbol.Name == "run");
        Assert.Contains(snapshot.CallBindings.Edges, static edge => edge.MemberPath == "run");
    }

    [Fact]
    public async Task HostContractDefinitionsAndImplementationsMapToExternalSources()
    {
        var number = new LuaHostTypeDescriptor { Kind = LuaHostTypeKind.Number };
        var contract = new LuaHostContractBuilder("lsp-host")
            .AddFunction(new LuaHostFunctionContract
            {
                Path = "game.run",
                Returns = [number],
                Source = new LuaHostSourceLocation
                {
                    Uri = "cpp://engine/game#run",
                    ImplementationUri = "cpp-implementation://engine/game#run",
                    Line = 4,
                    Column = 2,
                },
            })
            .Build();
        using var workspace = new LanguageServerWorkspace();
        workspace.Initialize([]);
        workspace.ConfigureHostContract(contract.ToJson(), path: null);
        var uri = new Uri("file:///host.lua");
        workspace.Open(uri, 1, "return game.run()");
        await workspace.ReindexNowAsync(CancellationToken.None);
        var service = new LuaLanguageService(workspace);
        var parameters = Element(new
        {
            textDocument = new { uri = uri.AbsoluteUri },
            position = new { line = 0, character = 13 },
        });

        var definition = await service.DefinitionAsync(parameters, false, CancellationToken.None);
        var implementation = await service.DefinitionAsync(parameters, true, CancellationToken.None);

        Assert.Equal("cpp://engine/game#run", definition!["uri"]!.GetValue<string>());
        Assert.Equal("cpp-implementation://engine/game#run", implementation!["uri"]!.GetValue<string>());
    }

    [Fact]
    public async Task JsonRpcCancellationReturnsLspCancellationError()
    {
        var request = Frame("""{"jsonrpc":"2.0","id":7,"method":"slow","params":{}}""");
        var cancel = Frame("""{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":7}}""");
        await using var input = new MemoryStream(request.Concat(cancel).ToArray());
        await using var output = new MemoryStream();
        await using var connection = new JsonRpcConnection(input, output);

        await connection.RunAsync(async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), token);
            return JsonValue.Create(true);
        });

        var payload = ReadFirstPayload(output.ToArray());
        using var response = JsonDocument.Parse(payload);
        Assert.Equal(-32800, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task JsonRpcInternalErrorsWriteFullExceptionToLocalDiagnosticStream()
    {
        var request = Frame(
            """{"jsonrpc":"2.0","id":11,"method":"textDocument/semanticTokens/full","params":{}}""");
        await using var input = new MemoryStream(request);
        await using var output = new MemoryStream();
        using var errors = new StringWriter();
        await using var connection = new JsonRpcConnection(input, output, errors);

        await connection.RunAsync((_, _) =>
            throw new KeyNotFoundException("The analysis key was not present."));

        var payload = ReadFirstPayload(output.ToArray());
        using var response = JsonDocument.Parse(payload);
        var error = response.RootElement.GetProperty("error");
        Assert.Equal(-32603, error.GetProperty("code").GetInt32());
        Assert.Equal("KeyNotFoundException: The analysis key was not present.",
            error.GetProperty("data").GetString());
        Assert.Contains("method=textDocument/semanticTokens/full", errors.ToString(), StringComparison.Ordinal);
        Assert.Contains("System.Collections.Generic.KeyNotFoundException", errors.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("The analysis key was not present.", errors.ToString(), StringComparison.Ordinal);
        Assert.Contains(" at ", errors.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonRpcNotificationInternalErrorsAreLoggedWithoutStoppingTheConnection()
    {
        var notification = Frame(
            """{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{}}""");
        var request = Frame(
            """{"jsonrpc":"2.0","id":12,"method":"lunil/reindexWorkspace","params":{}}""");
        await using var input = new MemoryStream(notification.Concat(request).ToArray());
        await using var output = new MemoryStream();
        using var errors = new StringWriter();
        await using var connection = new JsonRpcConnection(input, output, errors);

        await connection.RunAsync((message, _) => message.IsNotification
            ? throw new KeyNotFoundException("The notification analysis key was not present.")
            : Task.FromResult<JsonNode?>(JsonValue.Create(true)));

        var payload = ReadFirstPayload(output.ToArray());
        using var response = JsonDocument.Parse(payload);
        Assert.True(response.RootElement.GetProperty("result").GetBoolean());
        Assert.Contains("method=textDocument/didOpen", errors.ToString(), StringComparison.Ordinal);
        Assert.Contains("The notification analysis key was not present.", errors.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAdvertisesLsp317Capabilities()
    {
        await using var input = new MemoryStream();
        await using var output = new MemoryStream();
        await using var connection = new JsonRpcConnection(input, output);
        using var server = new LuaLanguageServer(connection);
        var request = new JsonRpcRequest("initialize", Element("""{"workspaceFolders":[]}"""),
            Element("1"));

        var result = await server.DispatchAsync(request, CancellationToken.None);

        Assert.Equal("utf-16", result!["capabilities"]!["positionEncoding"]!.GetValue<string>());
        Assert.True(result["capabilities"]!["renameProvider"]!["prepareProvider"]!.GetValue<bool>());
        Assert.True(result["capabilities"]!["semanticTokensProvider"]!["full"]!["delta"]!.GetValue<bool>());
    }

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static byte[] Frame(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        return Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n").Concat(payload).ToArray();
    }

    private static byte[] ReadFirstPayload(byte[] framed)
    {
        var separator = Encoding.ASCII.GetBytes("\r\n\r\n");
        var index = framed.AsSpan().IndexOf(separator);
        Assert.True(index >= 0);
        return framed[(index + separator.Length)..];
    }
}
