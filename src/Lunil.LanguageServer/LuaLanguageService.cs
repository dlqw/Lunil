using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lunil.Analysis;
using Lunil.Semantics.Binding;
using Lunil.Workspace;

namespace Lunil.LanguageServer;

internal sealed partial class LuaLanguageService(LanguageServerWorkspace workspace)
{
    private static readonly ImmutableArray<string> Keywords =
    [
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto",
        "if", "in", "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while",
    ];
    private readonly ConcurrentDictionary<string, SemanticTokenState> _semanticTokens =
        new(StringComparer.Ordinal);

    public async Task<JsonNode?> CompletionAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(parameters, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return new JsonArray();
        }

        var items = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var keyword in Keywords)
        {
            items[keyword] = CompletionItem(keyword, 14, "Lua keyword");
        }

        foreach (var symbol in context.Analysis.Compilation.SemanticModel.Symbols.Where(static symbol =>
                     symbol.Kind != LuaSymbolKind.Environment))
        {
            var type = GetType(context.Analysis, symbol)?.DisplayName ?? symbol.Kind.ToString();
            items[symbol.Name] = CompletionItem(
                symbol.Name,
                type.StartsWith("fun(", StringComparison.Ordinal) ? 3 : 6,
                type);
        }

        var snapshot = workspace.GetSnapshot();
        if (snapshot is not null)
        {
            foreach (var symbol in snapshot.ExportGraph.Symbols)
            {
                items.TryAdd(symbol.Name, CompletionItem(
                    symbol.Name,
                    symbol.Kind == LuaWorkspaceExportKind.Function ? 3 : 6,
                    $"{symbol.ModuleName}.{symbol.Path}: {symbol.Type.DisplayName}"));
            }
        }

        return new JsonObject
        {
            ["isIncomplete"] = snapshot is null,
            ["items"] = new JsonArray(items.Values.OrderBy(static item => item["label"]!.GetValue<string>(),
                StringComparer.Ordinal).Select(static item => (JsonNode)item).ToArray()),
        };
    }

    public async Task<JsonNode?> HoverAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(parameters, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return null;
        }

        var reference = FindReference(context.Analysis, context.ByteOffset);
        if (reference is null)
        {
            return null;
        }

        var type = GetType(context.Analysis, reference.Symbol)?.DisplayName ?? "unknown";
        var capture = reference.ResolutionKind == LuaNameResolutionKind.Upvalue ? " captured upvalue" : string.Empty;
        return new JsonObject
        {
            ["contents"] = new JsonObject
            {
                ["kind"] = "markdown",
                ["value"] = $"```lua\n{reference.Name}: {type}\n```\n{reference.ResolutionKind}{capture}",
            },
            ["range"] = LanguageServerWorkspace.ToJson(context.Analysis.Document.ToRange(reference.Span)),
        };
    }

    public async Task<JsonNode?> SignatureHelpAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(parameters, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return null;
        }

        var before = context.Analysis.Document.Text.AsSpan(0,
            context.Analysis.Document.ToCharOffset(context.Position));
        var match = SignatureTargetRegex().Match(before.ToString());
        if (!match.Success)
        {
            return null;
        }

        var name = match.Groups["name"].Value;
        var symbol = context.Analysis.Compilation.SemanticModel.Symbols.LastOrDefault(symbol =>
            string.Equals(symbol.Name, name, StringComparison.Ordinal));
        var label = symbol is null ? $"{name}(...)" : GetType(context.Analysis, symbol)?.DisplayName ?? $"{name}(...)";
        var activeParameter = match.Groups["args"].Value.Count(static character => character == ',');
        return new JsonObject
        {
            ["signatures"] = new JsonArray(new JsonObject
            {
                ["label"] = label,
                ["documentation"] = "Inferred by Lunil flow analysis.",
            }),
            ["activeSignature"] = 0,
            ["activeParameter"] = activeParameter,
        };
    }

    public async Task<JsonNode?> DefinitionAsync(
        JsonElement parameters,
        bool implementation,
        CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(parameters, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return null;
        }

        var snapshot = workspace.GetSnapshot();
        var call = snapshot?.CallBindings.Edges.FirstOrDefault(edge =>
            edge.SourceModuleName == context.Analysis.Module.Name && Contains(edge.Span, context.ByteOffset));
        if (call is not null)
        {
            var external = implementation ? call.ExternalImplementation : call.ExternalDefinition;
            if (external is not null)
            {
                return ExternalLocation(external);
            }

            if (call.TargetSymbolKey is { } targetKey)
            {
                var target = snapshot!.ExportGraph.Symbols.FirstOrDefault(symbol => symbol.Key == targetKey);
                if (target is not null && workspace.GetUri(target.ModuleName) is { } targetUri &&
                    workspace.TryGetDocument(targetUri, out var targetDocument))
                {
                    return Location(targetUri, targetDocument.ToRange(target.DefinitionSpan));
                }
            }
        }

        var reference = FindReference(context.Analysis, context.ByteOffset);
        if (reference is null || reference.Symbol.Kind == LuaSymbolKind.Environment)
        {
            return null;
        }

        var span = NormalizeDeclaringSpan(reference.Symbol, context.Analysis.Document);
        return Location(context.Analysis.Document.Uri, context.Analysis.Document.ToRange(span));
    }

    public async Task<JsonNode?> ReferencesAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(parameters, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return new JsonArray();
        }

        var reference = FindReference(context.Analysis, context.ByteOffset);
        if (reference is null || reference.Symbol.Kind == LuaSymbolKind.Environment)
        {
            return new JsonArray();
        }

        var includeDeclaration = parameters.TryGetProperty("context", out var referenceContext) &&
            referenceContext.TryGetProperty("includeDeclaration", out var include) && include.GetBoolean();
        var locations = GetReferenceLocations(context.Analysis, reference, includeDeclaration);
        return new JsonArray(locations.Select(static location => (JsonNode)location).ToArray());
    }

    public async Task<JsonNode?> PrepareRenameAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(parameters, cancellationToken).ConfigureAwait(false);
        var reference = context is null ? null : FindReference(context.Analysis, context.ByteOffset);
        if (context is null || reference is null || reference.Symbol.Kind == LuaSymbolKind.Environment ||
            reference.Symbol.IsReadOnly ||
            reference.ResolutionKind == LuaNameResolutionKind.Global && workspace.GetSnapshot() is null)
        {
            return null;
        }

        return new JsonObject
        {
            ["range"] = LanguageServerWorkspace.ToJson(context.Analysis.Document.ToRange(reference.Span)),
            ["placeholder"] = reference.Name,
        };
    }

    public async Task<JsonNode?> RenameAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var newName = parameters.GetProperty("newName").GetString() ?? string.Empty;
        if (!IdentifierRegex().IsMatch(newName) || Keywords.Contains(newName))
        {
            throw new JsonRpcException(-32602, "The new name is not a valid non-keyword Lua identifier.");
        }

        var context = await GetContextAsync(parameters, cancellationToken).ConfigureAwait(false);
        var reference = context is null ? null : FindReference(context.Analysis, context.ByteOffset);
        if (context is null || reference is null || reference.Symbol.IsReadOnly ||
            reference.Symbol.Kind == LuaSymbolKind.Environment)
        {
            return null;
        }

        if (context.Analysis.Compilation.SemanticModel.Symbols.Any(symbol =>
                symbol.FunctionId == reference.Symbol.FunctionId && symbol.Name == newName &&
                symbol.Id != reference.Symbol.Id))
        {
            throw new JsonRpcException(-32803, $"Rename would collide with '{newName}' in the same function.");
        }

        if (reference.ResolutionKind == LuaNameResolutionKind.Global && workspace.GetSnapshot() is null)
        {
            throw new JsonRpcException(-32803, "Workspace indexing is not complete; a global rename would be partial.");
        }

        var locations = GetReferenceLocations(context.Analysis, reference, includeDeclaration: true);
        var changes = new JsonObject();
        foreach (var group in locations.GroupBy(location => location["uri"]!.GetValue<string>(), StringComparer.Ordinal))
        {
            changes[group.Key] = new JsonArray(group.Select(location => (JsonNode)new JsonObject
            {
                ["range"] = location["range"]!.DeepClone(),
                ["newText"] = newName,
            }).ToArray());
        }

        return new JsonObject { ["changes"] = changes };
    }

    public async Task<JsonNode?> DocumentSymbolsAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(parameters, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return new JsonArray();
        }

        return new JsonArray(context.Analysis.Compilation.SemanticModel.Symbols
            .Where(static symbol => symbol.Kind != LuaSymbolKind.Environment)
            .OrderBy(static symbol => symbol.DeclaringSpan.Start)
            .Select(symbol => (JsonNode)new JsonObject
            {
                ["name"] = symbol.Name,
                ["detail"] = GetType(context.Analysis, symbol)?.DisplayName,
                ["kind"] = GetSymbolKind(symbol, context.Analysis),
                ["range"] = LanguageServerWorkspace.ToJson(context.Analysis.Document.ToRange(
                    NormalizeDeclaringSpan(symbol, context.Analysis.Document))),
                ["selectionRange"] = LanguageServerWorkspace.ToJson(context.Analysis.Document.ToRange(
                    NormalizeDeclaringSpan(symbol, context.Analysis.Document))),
            }).ToArray());
    }

    public JsonNode WorkspaceSymbols(string query)
    {
        var result = new JsonArray();
        var snapshot = workspace.GetSnapshot();
        if (snapshot is null)
        {
            return result;
        }

        foreach (var symbol in snapshot.ExportGraph.Symbols.Where(symbol =>
                     symbol.Name.Contains(query, StringComparison.OrdinalIgnoreCase) && !symbol.IsExternal))
        {
            var uri = workspace.GetUri(symbol.ModuleName);
            if (uri is null || !workspace.TryGetDocument(uri, out var document))
            {
                continue;
            }

            result.Add(new JsonObject
            {
                ["name"] = symbol.Name,
                ["kind"] = symbol.Kind == LuaWorkspaceExportKind.Function ? 12 : 13,
                ["location"] = Location(uri, document.ToRange(symbol.DefinitionSpan)),
                ["containerName"] = symbol.ModuleName,
            });
        }

        return result;
    }

    public async Task<JsonNode?> SemanticTokensAsync(
        JsonElement parameters,
        bool delta,
        CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(parameters, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return new JsonObject { ["data"] = new JsonArray() };
        }

        var data = BuildSemanticTokens(context.Analysis);
        var resultId = context.Analysis.Document.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" +
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(data.AsSpan())))[..12];
        var key = context.Analysis.Document.Uri.AbsoluteUri;
        var previousId = parameters.TryGetProperty("previousResultId", out var previous) ? previous.GetString() : null;
        JsonNode result;
        if (delta && _semanticTokens.TryGetValue(key, out var old) && old.ResultId == previousId)
        {
            result = new JsonObject
            {
                ["resultId"] = resultId,
                ["edits"] = new JsonArray(new JsonObject
                {
                    ["start"] = 0,
                    ["deleteCount"] = old.Data.Length,
                    ["data"] = new JsonArray(data.Select(static value => (JsonNode?)value).ToArray()),
                }),
            };
        }
        else
        {
            result = new JsonObject
            {
                ["resultId"] = resultId,
                ["data"] = new JsonArray(data.Select(static value => (JsonNode?)value).ToArray()),
            };
        }

        _semanticTokens[key] = new SemanticTokenState(resultId, data);
        return result;
    }

    public async Task<JsonNode?> InlayHintsAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(parameters, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return new JsonArray();
        }

        return new JsonArray(context.Analysis.Compilation.Analysis.Symbols
            .Where(static info => info.Symbol.Kind is LuaSymbolKind.Local or LuaSymbolKind.Parameter &&
                info.InferredType.Kind is not LuaTypeKind.Any and not LuaTypeKind.Unknown)
            .Select(info => (JsonNode)new JsonObject
            {
                ["position"] = Position(context.Analysis.Document.ToPosition(
                    NormalizeDeclaringSpan(info.Symbol, context.Analysis.Document).End)),
                ["label"] = ": " + info.InferredType.DisplayName,
                ["kind"] = 1,
                ["paddingLeft"] = true,
            }).ToArray());
    }

    public JsonNode FoldingRanges(Uri uri)
    {
        if (!workspace.TryGetDocument(uri, out var document))
        {
            return new JsonArray();
        }

        var lines = document.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var stack = new Stack<int>();
        var result = new JsonArray();
        for (var line = 0; line < lines.Length; line++)
        {
            var value = lines[line].TrimStart();
            if (Regex.IsMatch(value, @"\b(function|then|do|repeat)\b"))
            {
                stack.Push(line);
            }

            if ((Regex.IsMatch(value, @"^(end|until)\b")) && stack.TryPop(out var start) && line > start)
            {
                result.Add(new JsonObject { ["startLine"] = start, ["endLine"] = line, ["kind"] = "region" });
            }
        }

        return result;
    }

    public JsonNode SelectionRanges(JsonElement parameters)
    {
        var uri = GetUri(parameters);
        if (!workspace.TryGetDocument(uri, out var document) ||
            !parameters.TryGetProperty("positions", out var positions))
        {
            return new JsonArray();
        }

        var result = new JsonArray();
        foreach (var positionElement in positions.EnumerateArray())
        {
            var position = GetPosition(positionElement);
            var offset = document.ToCharOffset(position);
            var start = offset;
            var end = offset;
            while (start > 0 && IsIdentifierCharacter(document.Text[start - 1])) start--;
            while (end < document.Text.Length && IsIdentifierCharacter(document.Text[end])) end++;
            var word = new LspRange(document.ToPosition(Encoding.UTF8.GetByteCount(document.Text.AsSpan(0, start))),
                document.ToPosition(Encoding.UTF8.GetByteCount(document.Text.AsSpan(0, end))));
            var line = new LspRange(new LspPosition(position.Line, 0),
                new LspPosition(position.Line, int.MaxValue));
            result.Add(new JsonObject
            {
                ["range"] = LanguageServerWorkspace.ToJson(word),
                ["parent"] = new JsonObject { ["range"] = LanguageServerWorkspace.ToJson(line) },
            });
        }

        return result;
    }

    public JsonNode CodeActions(JsonElement parameters)
    {
        var uri = GetUri(parameters);
        if (!workspace.TryGetDocument(uri, out var document) ||
            !parameters.TryGetProperty("context", out var context) ||
            !context.TryGetProperty("diagnostics", out var diagnostics))
        {
            return new JsonArray();
        }

        var result = new JsonArray();
        foreach (var diagnostic in diagnostics.EnumerateArray())
        {
            var code = diagnostic.TryGetProperty("code", out var codeElement) ? codeElement.ToString() : null;
            if (string.IsNullOrWhiteSpace(code)) continue;
            var range = ParseRange(diagnostic.GetProperty("range"));
            var insertion = new LspRange(new LspPosition(range.Start.Line, 0), new LspPosition(range.Start.Line, 0));
            result.Add(new JsonObject
            {
                ["title"] = $"Suppress {code} on the next line",
                ["kind"] = "quickfix",
                ["diagnostics"] = new JsonArray(JsonNode.Parse(diagnostic.GetRawText())),
                ["edit"] = new JsonObject
                {
                    ["changes"] = new JsonObject
                    {
                        [uri.AbsoluteUri] = new JsonArray(new JsonObject
                        {
                            ["range"] = LanguageServerWorkspace.ToJson(insertion),
                            ["newText"] = $"---@diagnostic disable-next-line: {code}\n",
                        }),
                    },
                },
            });
        }

        return result;
    }

    public async Task<JsonNode?> PrepareCallHierarchyAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(parameters, cancellationToken).ConfigureAwait(false);
        if (context is null) return null;
        var function = context.Analysis.Compilation.SemanticModel.Functions
            .Where(function => Contains(function.Span, context.ByteOffset))
            .OrderBy(static function => function.Span.Length).FirstOrDefault();
        if (function is null) return null;
        var key = context.Analysis.Compilation.SemanticModel.GetFunctionKey(function, context.Analysis.Module).Value;
        return new JsonArray(CallHierarchyItem(
            key,
            function.Id == 0 ? context.Analysis.Module.Name : $"function#{function.Id}",
            context.Analysis.Document.Uri,
            context.Analysis.Document.ToRange(function.Span)));
    }

    public JsonNode IncomingCalls(JsonElement parameters) => CallHierarchyEdges(parameters, incoming: true);

    public JsonNode OutgoingCalls(JsonElement parameters) => CallHierarchyEdges(parameters, incoming: false);

    private JsonArray CallHierarchyEdges(JsonElement parameters, bool incoming)
    {
        var item = parameters.GetProperty("item");
        var key = item.GetProperty("data").GetString();
        var snapshot = workspace.GetSnapshot();
        if (key is null || snapshot is null) return new JsonArray();
        var result = new JsonArray();
        var calls = snapshot.CallBindings.Edges.Where(call => incoming
            ? call.TargetFunctionKey == key
            : GetContainingFunctionKey(snapshot, call) == key);
        foreach (var call in calls)
        {
            var uri = workspace.GetUri(call.SourceModuleName);
            if (uri is null || !workspace.TryGetDocument(uri, out var document)) continue;
            var sourceKey = GetContainingFunctionKey(snapshot, call) ?? call.SourceModuleName;
            var hierarchyItem = CallHierarchyItem(sourceKey, call.SourceModuleName, uri, document.ToRange(call.Span));
            result.Add(new JsonObject
            {
                [incoming ? "from" : "to"] = hierarchyItem,
                [incoming ? "fromRanges" : "fromRanges"] = new JsonArray(
                    LanguageServerWorkspace.ToJson(document.ToRange(call.Span))),
            });
        }

        return result;
    }

    private ImmutableArray<JsonObject> GetReferenceLocations(
        LanguageDocumentAnalysis analysis,
        LuaNameReference reference,
        bool includeDeclaration)
    {
        var builder = ImmutableArray.CreateBuilder<JsonObject>();
        if (reference.ResolutionKind == LuaNameResolutionKind.Global && workspace.GetSnapshot() is { } snapshot)
        {
            var key = analysis.Compilation.SemanticModel.GetSymbolKey(reference.Symbol, analysis.Module);
            foreach (var item in snapshot.FindReferences(key))
            {
                var uri = workspace.GetUri(item.Module.Name);
                if (uri is not null && workspace.TryGetDocument(uri, out var document))
                {
                    builder.Add(Location(uri, document.ToRange(item.Span)));
                }
            }
        }
        else
        {
            foreach (var item in analysis.Compilation.SemanticModel.References.Where(item =>
                         item.Symbol.Id == reference.Symbol.Id))
            {
                builder.Add(Location(analysis.Document.Uri, analysis.Document.ToRange(item.Span)));
            }
        }

        if (includeDeclaration)
        {
            builder.Add(Location(analysis.Document.Uri, analysis.Document.ToRange(
                NormalizeDeclaringSpan(reference.Symbol, analysis.Document))));
        }

        return builder.DistinctBy(static location => location.ToJsonString()).ToImmutableArray();
    }

    private static ImmutableArray<int> BuildSemanticTokens(LanguageDocumentAnalysis analysis)
    {
        var tokens = analysis.Compilation.SemanticModel.References
            .Select(reference =>
            {
                var range = analysis.Document.ToRange(reference.Span);
                var type = reference.Symbol.Kind switch
                {
                    LuaSymbolKind.Parameter => 1,
                    LuaSymbolKind.Global => 3,
                    _ => GetType(analysis, reference.Symbol) is LuaFunctionType ? 2 : 0,
                };
                var modifiers = (reference.IsWrite ? 1 : 0) |
                    (reference.Symbol.IsReadOnly ? 2 : 0) |
                    (reference.Symbol.IsCaptured ? 4 : 0);
                return (range.Start.Line, range.Start.Character,
                    Math.Max(1, range.End.Character - range.Start.Character), type, modifiers);
            })
            .OrderBy(static token => token.Line).ThenBy(static token => token.Character).ToArray();
        var builder = ImmutableArray.CreateBuilder<int>(tokens.Length * 5);
        var previousLine = 0;
        var previousCharacter = 0;
        foreach (var token in tokens)
        {
            var lineDelta = token.Line - previousLine;
            var characterDelta = lineDelta == 0 ? token.Character - previousCharacter : token.Character;
            builder.Add(lineDelta);
            builder.Add(characterDelta);
            builder.Add(token.Item3);
            builder.Add(token.type);
            builder.Add(token.modifiers);
            previousLine = token.Line;
            previousCharacter = token.Character;
        }

        return builder.MoveToImmutable();
    }

    private async Task<DocumentContext?> GetContextAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var uri = GetUri(parameters);
        var analysis = await workspace.GetAnalysisAsync(uri, cancellationToken).ConfigureAwait(false);
        if (analysis is null) return null;
        var position = GetPosition(parameters.GetProperty("position"));
        return new DocumentContext(analysis, position, analysis.Document.ToByteOffset(position));
    }

    private static LuaNameReference? FindReference(LanguageDocumentAnalysis analysis, int offset) =>
        analysis.Compilation.SemanticModel.References.Where(reference => Contains(reference.Span, offset))
            .OrderBy(static reference => reference.Span.Length).FirstOrDefault();

    private static LuaType? GetType(LanguageDocumentAnalysis analysis, LuaSymbol symbol) =>
        analysis.Compilation.Analysis.Symbols.FirstOrDefault(info => info.Symbol.Id == symbol.Id)?.InferredType;

    private static int GetSymbolKind(LuaSymbol symbol, LanguageDocumentAnalysis analysis) =>
        GetType(analysis, symbol) is LuaFunctionType ? 12 : symbol.Kind == LuaSymbolKind.Parameter ? 26 : 13;

    private static Lunil.Core.Text.TextSpan NormalizeDeclaringSpan(LuaSymbol symbol, LspTextDocument document) =>
        symbol.DeclaringSpan.Length > 0
            ? symbol.DeclaringSpan
            : new Lunil.Core.Text.TextSpan(symbol.DeclaringSpan.Start, Encoding.UTF8.GetByteCount(symbol.Name));

    private static bool Contains(Lunil.Core.Text.TextSpan span, int offset) =>
        offset >= span.Start && offset <= span.End;

    private static JsonObject CompletionItem(string label, int kind, string detail) => new()
    {
        ["label"] = label,
        ["kind"] = kind,
        ["detail"] = detail,
        ["sortText"] = label,
    };

    private static JsonObject Location(Uri uri, LspRange range) => new()
    {
        ["uri"] = uri.AbsoluteUri,
        ["range"] = LanguageServerWorkspace.ToJson(range),
    };

    private static JsonObject ExternalLocation(LuaHostSourceLocation source) => new()
    {
        ["uri"] = source.Uri,
        ["range"] = LanguageServerWorkspace.ToJson(new LspRange(
            new LspPosition(Math.Max(0, source.Line), Math.Max(0, source.Column)),
            new LspPosition(Math.Max(0, source.Line), Math.Max(0, source.Column)))),
    };

    private static JsonObject Position(LspPosition position) => new()
    {
        ["line"] = position.Line,
        ["character"] = position.Character,
    };

    private static Uri GetUri(JsonElement parameters)
    {
        var document = parameters.TryGetProperty("textDocument", out var value) ? value : parameters;
        return new Uri(document.GetProperty("uri").GetString()!, UriKind.Absolute);
    }

    private static LspPosition GetPosition(JsonElement element) => new(
        element.GetProperty("line").GetInt32(),
        element.GetProperty("character").GetInt32());

    private static LspRange ParseRange(JsonElement element) => new(
        GetPosition(element.GetProperty("start")),
        GetPosition(element.GetProperty("end")));

    private static bool IsIdentifierCharacter(char character) => character == '_' || char.IsLetterOrDigit(character);

    private static string? GetContainingFunctionKey(
        LuaWorkspaceCompactSnapshot snapshot,
        LuaWorkspaceModuleCallBinding call)
    {
        var module = snapshot.GetModule(call.SourceModuleName);
        return module?.ExportedSymbols.FirstOrDefault(symbol => symbol.FunctionKey is not null)?.FunctionKey;
    }

    private static JsonObject CallHierarchyItem(string key, string name, Uri uri, LspRange range) => new()
    {
        ["name"] = name,
        ["kind"] = 12,
        ["uri"] = uri.AbsoluteUri,
        ["range"] = LanguageServerWorkspace.ToJson(range),
        ["selectionRange"] = LanguageServerWorkspace.ToJson(range),
        ["data"] = key,
    };

    [GeneratedRegex(@"(?<name>[A-Za-z_][A-Za-z0-9_\.:]*)\s*\((?<args>[^()]*)$")]
    private static partial Regex SignatureTargetRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex IdentifierRegex();

    private sealed record DocumentContext(
        LanguageDocumentAnalysis Analysis,
        LspPosition Position,
        int ByteOffset);

    private sealed record SemanticTokenState(string ResultId, ImmutableArray<int> Data);
}
