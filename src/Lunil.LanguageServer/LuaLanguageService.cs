using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lunil.Analysis;
using Lunil.Core.Text;
using Lunil.EmmyLua;
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
    /** Insertion-order keys used to bound the semantic token cache. */
    private readonly ConcurrentQueue<string> _semanticTokenOrder = new();
    private const int MaximumCachedSemanticTokens = 256;

    public async Task<JsonNode?> CompletionAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(parameters, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return new JsonArray();
        }

        var before = context.Analysis.Document.Text.AsSpan(0,
            context.Analysis.Document.ToCharOffset(context.Position)).ToString();
        var contextual = BuildContextualCompletion(context.Analysis, context.ByteOffset, before);
        if (contextual.Handled)
        {
            return new JsonObject
            {
                ["isIncomplete"] = false,
                ["items"] = new JsonArray(contextual.Items.Select(static item => (JsonNode)CompletionItem(
                    item.Label, item.Kind, item.Detail, item.SortText)).ToArray()),
            };
        }

        // Generic context: file-local symbols first, workspace exports next, keywords last.
        var items = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var symbol in context.Analysis.Compilation.SemanticModel.Symbols.Where(static symbol =>
                     symbol.Kind != LuaSymbolKind.Environment))
        {
            var type = GetType(context.Analysis, symbol)?.DisplayName ?? symbol.Kind.ToString();
            var isFunction = type.StartsWith("fun(", StringComparison.Ordinal);
            items[symbol.Name] = CompletionItem(
                symbol.Name,
                isFunction ? 3 : 6,
                type,
                (isFunction ? "1" : "2") + symbol.Name);
        }

        var snapshot = workspace.GetSnapshot();
        if (snapshot is not null)
        {
            foreach (var symbol in snapshot.ExportGraph.Symbols.Where(static symbol =>
                         !symbol.Path.Contains('.', StringComparison.Ordinal)))
            {
                items.TryAdd(symbol.Name, CompletionItem(
                    symbol.Name,
                    symbol.Kind == LuaWorkspaceExportKind.Function ? 3 : 6,
                    $"{symbol.ModuleName}.{symbol.Path}: {symbol.Type.DisplayName}",
                    (symbol.Kind == LuaWorkspaceExportKind.Function ? "1" : "2") + symbol.Name));
            }
        }

        foreach (var keyword in Keywords)
        {
            items.TryAdd(keyword, CompletionItem(keyword, 14, "Lua keyword", "3" + keyword));
        }

        return new JsonObject
        {
            ["isIncomplete"] = snapshot is null,
            ["items"] = new JsonArray(items.Values.OrderBy(static item => item["sortText"]!.GetValue<string>(),
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
            // Hover over a member reference shows the inferred member type.
            var codeReference = FindCodeReference(context.Analysis, context.ByteOffset);
            if (codeReference is not null && IsNamedMember(codeReference))
            {
                var memberType = ResolveMemberType(context.Analysis, codeReference);
                if (memberType is null)
                {
                    return null;
                }

                return HoverResult(
                    $"```lua\n{codeReference.Name}: {memberType.DisplayName}\n```\nmember",
                    context.Analysis.Document.ToRange(codeReference.Span));
            }

            // The cursor may sit on a declaration token (`local Movable = {}`), which the
            // binder does not record as a reference.
            var declared = FindDeclaredSymbolAt(context.Analysis, context.ByteOffset);
            if (declared is not null)
            {
                var declaredSpan = NormalizeDeclaringSpan(declared, context.Analysis.Document);
                if (TryResolveClassValueModule(context.Analysis, declared, out var declaredModule) &&
                    TryBuildClassHover(declaredModule, declared.Name) is { } declaredHover)
                {
                    return HoverResult(declaredHover, context.Analysis.Document.ToRange(declaredSpan));
                }

                var declaredType = GetType(context.Analysis, declared)?.DisplayName ?? "unknown";
                return HoverResult(
                    $"```lua\n{declared.Name}: {declaredType}\n```\ndeclaration",
                    context.Analysis.Document.ToRange(declaredSpan));
            }

            return null;
        }

        // Class values (require aliases and the defining module's exported class local)
        // hover with their inheritance chain and member list.
        if (TryResolveClassValueModule(context.Analysis, reference.Symbol, out var classModule) &&
            TryBuildClassHover(classModule, reference.Symbol.Name) is { } classHover)
        {
            return HoverResult(classHover, context.Analysis.Document.ToRange(reference.Span));
        }

        var type = GetType(context.Analysis, reference.Symbol)?.DisplayName ?? "unknown";
        var capture = reference.ResolutionKind == LuaNameResolutionKind.Upvalue ? " captured upvalue" : string.Empty;
        return HoverResult(
            $"```lua\n{reference.Name}: {type}\n```\n{reference.ResolutionKind}{capture}",
            context.Analysis.Document.ToRange(reference.Span));
    }

    private static JsonObject HoverResult(string value, LspRange range) => new()
    {
        ["contents"] = new JsonObject
        {
            ["kind"] = "markdown",
            ["value"] = value,
        },
        ["range"] = LanguageServerWorkspace.ToJson(range),
    };

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

        // Cursor inside a require("...") string opens the required module.
        if (FindRequireAt(context.Analysis, context.ByteOffset) is { } required)
        {
            if (workspace.GetUri(required.ModuleName) is { } moduleUri)
            {
                return Location(moduleUri, new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)));
            }

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

        // Member references (.field, :method, ["key"]) resolve through module exports
        // and same-file member writes; lexical name references continue below.
        var codeReference = FindCodeReference(context.Analysis, context.ByteOffset);
        if (codeReference is not null && IsNamedMember(codeReference))
        {
            var memberDefinition = ResolveMemberDefinition(context.Analysis, codeReference);
            return memberDefinition is null
                ? null
                : Location(memberDefinition.Uri, memberDefinition.Range);
        }

        var reference = FindReference(context.Analysis, context.ByteOffset);
        if (reference is not null && reference.Symbol.Kind == LuaSymbolKind.Environment)
        {
            // Globals fold into the implicit _ENV symbol. Jump to the first write site,
            // which is the global's definition, preferring the workspace index.
            if (workspace.GetSnapshot() is { } globalSnapshot)
            {
                var write = globalSnapshot.FindGlobalReferences(reference.Name)
                    .Where(static item => item.IsWrite)
                    .OrderBy(static item => item.Module.Name, StringComparer.Ordinal)
                    .ThenBy(static item => item.Span.Start)
                    .FirstOrDefault();
                if (write is not null && workspace.GetUri(write.Module.Name) is { } uri &&
                    workspace.TryGetDocument(uri, out var document))
                {
                    return Location(uri, document.ToRange(write.Span));
                }
            }

            var localWrite = context.Analysis.Compilation.SemanticModel.References
                .Where(item => item.Symbol.Kind == LuaSymbolKind.Environment && item.IsWrite &&
                    string.Equals(item.Name, reference.Name, StringComparison.Ordinal))
                .OrderBy(static item => item.Span.Start)
                .FirstOrDefault();
            return localWrite is null
                ? null
                : Location(context.Analysis.Document.Uri, context.Analysis.Document.ToRange(localWrite.Span));
        }

        // A require alias passes through to the exported class value's definition (for
        // example the `local Character = GameEntity:extend(...)` class line).
        var aliases = BuildRequireAliases(context.Analysis);
        if (reference is not null &&
            aliases.TryGetValue(reference.Symbol.Id, out var aliasModule) &&
            await TryGetClassDeclarationLocationAsync(aliasModule, cancellationToken)
                .ConfigureAwait(false) is { } aliasLocation)
        {
            return aliasLocation;
        }

        // The cursor may sit on the declaration itself (`local Movable = {}`), which the
        // binder does not record as a reference.
        if (reference is null)
        {
            var declared = FindDeclaredSymbolAt(context.Analysis, context.ByteOffset);
            if (declared is null)
            {
                return null;
            }

            if (aliases.TryGetValue(declared.Id, out var declaredAliasModule) &&
                await TryGetClassDeclarationLocationAsync(declaredAliasModule, cancellationToken)
                    .ConfigureAwait(false) is { } declaredAliasLocation)
            {
                return declaredAliasLocation;
            }

            return Location(context.Analysis.Document.Uri, context.Analysis.Document.ToRange(
                NormalizeDeclaringSpan(declared, context.Analysis.Document)));
        }

        var span = NormalizeDeclaringSpan(reference.Symbol, context.Analysis.Document);
        return Location(context.Analysis.Document.Uri, context.Analysis.Document.ToRange(span));
    }

    /// <summary>
    /// The declaration location of a module's exported class value: the
    /// `local Character = GameEntity:extend(...)` line when its name is known, otherwise
    /// the root export's span.
    /// </summary>
    private async Task<JsonObject?> TryGetClassDeclarationLocationAsync(
        string module,
        CancellationToken cancellationToken)
    {
        if (FindModuleRootExport(module) is not { } root ||
            workspace.GetUri(root.ModuleName) is not { } uri ||
            !workspace.TryGetDocument(uri, out var document))
        {
            return null;
        }

        if (root.Type is LuaPrototypeType { Name: { Length: > 0 } className } &&
            await workspace.GetAnalysisAsync(uri, cancellationToken).ConfigureAwait(false) is { } analysis)
        {
            var declared = analysis.Compilation.SemanticModel.Symbols.FirstOrDefault(symbol =>
                string.Equals(symbol.Name, className, StringComparison.Ordinal) &&
                symbol.DeclaringSpan.Length > 0);
            if (declared is not null)
            {
                return Location(uri, document.ToRange(NormalizeDeclaringSpan(declared, document)));
            }
        }

        return root.DefinitionSpan.Length > 0
            ? Location(uri, document.ToRange(root.DefinitionSpan))
            : null;
    }

    /// <summary>
    /// A class-value hover: the class name, its declared inheritance chain, and the
    /// members its module (and each base module) expose, names only.
    /// </summary>
    private string? TryBuildClassHover(string module, string symbolName)
    {
        var snapshot = workspace.GetSnapshot();
        if (snapshot is null)
        {
            return null;
        }

        var root = FindModuleRootExport(module);
        var className = root?.Type is LuaPrototypeType prototype && prototype.Name.Length > 0
            ? prototype.Name
            : symbolName;
        var markdown = new StringBuilder();
        markdown.Append("```lua\n").Append(className).Append("\n```\nmodule ").Append(module);

        var declarations = workspace.GetClassDeclarations();
        var bases = CollectBaseClassNames(declarations, className);
        if (bases.Count > 0)
        {
            markdown.Append("\n\n**Inherits**: ").Append(string.Join(" > ", bases.Prepend(className)));
        }

        var first = true;
        foreach (var chainModule in CollectChainModules(declarations, module))
        {
            var names = snapshot.ExportGraph.Symbols
                .Where(symbol => !symbol.IsExternal &&
                    string.Equals(symbol.ModuleName, chainModule, StringComparison.Ordinal) &&
                    symbol.Path.Length > 0 &&
                    !symbol.Path.Contains('.', StringComparison.Ordinal))
                .Select(static symbol => symbol.Path)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToList();
            if (names.Count == 0)
            {
                continue;
            }

            var label = first ? "Members" : $"Inherited ({chainModule})";
            first = false;
            markdown.Append("\n\n**").Append(label).Append("**: ");
            const int shown = 12;
            markdown.Append(string.Join(", ", names.Take(shown)));
            if (names.Count > shown)
            {
                markdown.Append(", … +").Append(names.Count - shown);
            }
        }

        return markdown.ToString();
    }

    /// <summary>The declared base class names above a class, outermost last.</summary>
    private static List<string> CollectBaseClassNames(
        ImmutableArray<WorkspaceClassDeclaration> declarations,
        string className)
    {
        var declarationsByClass = declarations
            .GroupBy(static item => item.Name, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.First(),
                StringComparer.Ordinal);
        var bases = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = className;
        while (declarationsByClass.TryGetValue(current, out var declaration) && visited.Add(current))
        {
            if (declaration.BaseNames.FirstOrDefault(declarationsByClass.ContainsKey) is { } next)
            {
                bases.Add(next);
                current = next;
            }
            else
            {
                break;
            }
        }

        return bases;
    }

    /// <summary>The module's root export symbol (its returned value), if indexed.</summary>
    private LuaWorkspaceExportSymbol? FindModuleRootExport(string moduleName)
    {
        var snapshot = workspace.GetSnapshot();
        return snapshot?.ExportGraph.Symbols.FirstOrDefault(symbol =>
            !symbol.IsExternal &&
            string.Equals(symbol.ModuleName, moduleName, StringComparison.Ordinal) &&
            symbol.Path.Length == 0);
    }

    public async Task<JsonNode?> ReferencesAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(parameters, cancellationToken).ConfigureAwait(false);
        if (context is null)
        {
            return new JsonArray();
        }

        // A require("...") string reports every require site of that module.
        if (FindRequireAt(context.Analysis, context.ByteOffset) is { } required)
        {
            var requireBuilder = ImmutableArray.CreateBuilder<JsonObject>();
            requireBuilder.Add(Location(context.Analysis.Document.Uri,
                context.Analysis.Document.ToRange(required.StringSpan)));
            if (workspace.GetSnapshot() is { } snapshot)
            {
                foreach (var dependency in snapshot.Graph.Dependencies.Where(dependency =>
                             string.Equals(dependency.RequestedName, required.ModuleName, StringComparison.Ordinal)))
                {
                    AddSnapshotLocation(requireBuilder, dependency.Source.Name, dependency.Span);
                }
            }

            return LocationsToJsonArray(requireBuilder);
        }

        var reference = FindReference(context.Analysis, context.ByteOffset);
        if (reference is not null && reference.Symbol.Kind == LuaSymbolKind.Environment)
        {
            // Globals fold into the implicit _ENV symbol; the workspace index keeps
            // them addressable by name across every module.
            var globalBuilder = ImmutableArray.CreateBuilder<JsonObject>();
            if (workspace.GetSnapshot() is { } snapshot)
            {
                foreach (var item in snapshot.FindGlobalReferences(reference.Name))
                {
                    AddSnapshotLocation(globalBuilder, item.Module.Name, item.Span);
                }
            }
            else
            {
                foreach (var item in context.Analysis.Compilation.SemanticModel.References.Where(item =>
                             item.Symbol.Kind == LuaSymbolKind.Environment &&
                             string.Equals(item.Name, reference.Name, StringComparison.Ordinal)))
                {
                    globalBuilder.Add(Location(context.Analysis.Document.Uri,
                        context.Analysis.Document.ToRange(item.Span)));
                }
            }

            return LocationsToJsonArray(globalBuilder);
        }

        var symbol = reference?.Symbol ??
            (reference is null ? FindDeclaredSymbolAt(context.Analysis, context.ByteOffset) : null);
        if (symbol is not null)
        {
            var includeDeclaration = parameters.TryGetProperty("context", out var referenceContext) &&
                referenceContext.TryGetProperty("includeDeclaration", out var include) && include.GetBoolean();
            var builder = ImmutableArray.CreateBuilder<JsonObject>();
            foreach (var item in context.Analysis.Compilation.SemanticModel.References.Where(item =>
                         item.Symbol.Id == symbol.Id))
            {
                builder.Add(Location(context.Analysis.Document.Uri,
                    context.Analysis.Document.ToRange(item.Span)));
            }

            if (includeDeclaration)
            {
                builder.Add(Location(context.Analysis.Document.Uri, context.Analysis.Document.ToRange(
                    NormalizeDeclaringSpan(symbol, context.Analysis.Document))));
            }

            // A class value (require alias or the exported class local) also reports its
            // declaration site and every require of the module across the workspace.
            if (workspace.GetSnapshot() is { } classSnapshot &&
                TryResolveClassValueModule(context.Analysis, symbol, out var classModule))
            {
                if (await TryGetClassDeclarationLocationAsync(classModule, cancellationToken)
                        .ConfigureAwait(false) is { } declarationLocation)
                {
                    builder.Add(declarationLocation);
                }

                foreach (var dependency in classSnapshot.Graph.Dependencies.Where(dependency =>
                             string.Equals(dependency.RequestedName, classModule, StringComparison.Ordinal)))
                {
                    AddSnapshotLocation(builder, dependency.Source.Name, dependency.Span);
                }
            }

            return LocationsToJsonArray(builder);
        }

        // Member references: same-file occurrences plus cross-module hits when the member
        // resolves to a workspace export (require alias receiver, the defining module's
        // own table, or a unique same-named export).
        var codeReference = FindCodeReference(context.Analysis, context.ByteOffset);
        if (codeReference is not null && IsNamedMember(codeReference))
        {
            var name = codeReference.Name!;
            var builder = ImmutableArray.CreateBuilder<JsonObject>();
            foreach (var item in context.Analysis.Compilation.SemanticModel.UnifiedReferences.Where(item =>
                         string.Equals(item.Name, name, StringComparison.Ordinal)))
            {
                builder.Add(Location(context.Analysis.Document.Uri,
                    context.Analysis.Document.ToRange(item.Span)));
            }

            var snapshot = workspace.GetSnapshot();
            var exported = snapshot is null ? null : TryResolveMemberExport(context.Analysis, codeReference);
            if (exported is not null && snapshot is not null)
            {
                // The definition itself is reported so "find references" on a member
                // shows where it is defined, matching editor expectations.
                if (workspace.GetUri(exported.ModuleName) is { } exportUri &&
                    workspace.TryGetDocument(exportUri, out var exportDocument))
                {
                    builder.Add(Location(exportUri, exportDocument.ToRange(exported.DefinitionSpan)));
                }

                // Precise cross-file call sites resolved against the export graph, then
                // every same-named member reference (reads, writes, and calls through
                // receivers the binder could not resolve).
                foreach (var call in snapshot.FindCallsToExport(exported.Key))
                {
                    AddSnapshotLocation(builder, call.SourceModuleName, call.Span);
                }

                foreach (var item in snapshot.FindMemberReferences(name))
                {
                    AddSnapshotLocation(builder, item.Module.Name, item.Span);
                }
            }

            return LocationsToJsonArray(builder);
        }

        return new JsonArray();
    }

    private void AddSnapshotLocation(ImmutableArray<JsonObject>.Builder builder, string moduleName, Lunil.Core.Text.TextSpan span)
    {
        if (workspace.GetUri(moduleName) is { } uri && workspace.TryGetDocument(uri, out var document))
        {
            builder.Add(Location(uri, document.ToRange(span)));
        }
    }

    private static JsonArray LocationsToJsonArray(ImmutableArray<JsonObject>.Builder builder) => new(
        DeduplicateLocations(builder).Select(static location => (JsonNode)location).ToArray());

    private static IEnumerable<JsonObject> DeduplicateLocations(IEnumerable<JsonObject> locations) =>
        locations.DistinctBy(static location => (
            location["uri"]!.GetValue<string>(),
            location["range"]!["start"]!["line"]!.GetValue<int>(),
            location["range"]!["start"]!["character"]!.GetValue<int>(),
            location["range"]!["end"]!["line"]!.GetValue<int>(),
            location["range"]!["end"]!["character"]!.GetValue<int>()));

    public async Task<JsonNode?> PrepareRenameAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var context = await GetContextAsync(parameters, cancellationToken).ConfigureAwait(false);
        var reference = context is null ? null : FindReference(context.Analysis, context.ByteOffset);
        var symbol = reference?.Symbol ??
            (context is null ? null : FindDeclaredSymbolAt(context.Analysis, context.ByteOffset));
        if (context is null || symbol is null || symbol.Kind == LuaSymbolKind.Environment ||
            symbol.IsReadOnly ||
            reference?.ResolutionKind == LuaNameResolutionKind.Global && workspace.GetSnapshot() is null)
        {
            return null;
        }

        var span = reference?.Span ?? NormalizeDeclaringSpan(symbol, context.Analysis.Document);
        return new JsonObject
        {
            ["range"] = LanguageServerWorkspace.ToJson(context.Analysis.Document.ToRange(span)),
            ["placeholder"] = symbol.Name,
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
        var symbol = reference?.Symbol ??
            (context is null ? null : FindDeclaredSymbolAt(context.Analysis, context.ByteOffset));
        if (context is null || symbol is null || symbol.IsReadOnly ||
            symbol.Kind == LuaSymbolKind.Environment)
        {
            return null;
        }

        if (context.Analysis.Compilation.SemanticModel.Symbols.Any(existing =>
                existing.FunctionId == symbol.FunctionId && existing.Name == newName &&
                existing.Id != symbol.Id))
        {
            throw new JsonRpcException(-32803, $"Rename would collide with '{newName}' in the same function.");
        }

        if (reference?.ResolutionKind == LuaNameResolutionKind.Global && workspace.GetSnapshot() is null)
        {
            throw new JsonRpcException(-32803, "Workspace indexing is not complete; a global rename would be partial.");
        }

        var locations = reference is null
            ? GetSymbolReferenceLocations(context.Analysis, symbol, includeDeclaration: true)
            : GetReferenceLocations(context.Analysis, reference, includeDeclaration: true);
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

        var builder = ImmutableArray.CreateBuilder<JsonObject>();
        foreach (var symbol in context.Analysis.Compilation.SemanticModel.Symbols
                     .Where(static symbol => symbol.Kind != LuaSymbolKind.Environment)
                     .OrderBy(static symbol => symbol.DeclaringSpan.Start))
        {
            builder.Add(new JsonObject
            {
                ["name"] = symbol.Name,
                ["detail"] = GetType(context.Analysis, symbol)?.DisplayName,
                ["kind"] = GetSymbolKind(symbol, context.Analysis),
                ["range"] = LanguageServerWorkspace.ToJson(context.Analysis.Document.ToRange(
                    NormalizeDeclaringSpan(symbol, context.Analysis.Document))),
                ["selectionRange"] = LanguageServerWorkspace.ToJson(context.Analysis.Document.ToRange(
                    NormalizeDeclaringSpan(symbol, context.Analysis.Document))),
            });
        }

        // Table-assigned functions (function M.f, function C:m, M.f = function) are
        // member writes, not lexical symbols; surface them so the outline reflects
        // the module's real API without annotations.
        var seenMembers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in context.Analysis.Compilation.SemanticModel.UnifiedReferences
                     .Where(static reference => reference.Kind == LuaReferenceKind.Member &&
                         reference.Access.HasFlag(LuaReferenceAccess.Write) &&
                         !string.IsNullOrEmpty(reference.Name))
                     .OrderBy(static reference => reference.Span.Start))
        {
            if (!seenMembers.Add(member.Name!))
            {
                continue;
            }

            var memberType = ResolveMemberType(context.Analysis, member);
            if (memberType is not (LuaFunctionType or LuaOverloadType))
            {
                continue;
            }

            var range = context.Analysis.Document.ToRange(member.Span);
            builder.Add(new JsonObject
            {
                ["name"] = member.Name!,
                ["detail"] = memberType.DisplayName,
                ["kind"] = 12,
                ["range"] = LanguageServerWorkspace.ToJson(range),
                ["selectionRange"] = LanguageServerWorkspace.ToJson(range),
            });
        }

        return new JsonArray(builder.ToArray());
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
        // The document version identifies the analyzed content, so it doubles as the
        // result identity; a content hash would re-cost a full pass over every request.
        var resultId = "v" + context.Analysis.Document.Version.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        var key = context.Analysis.Document.Uri.AbsoluteUri;
        var previousId = parameters.TryGetProperty("previousResultId", out var previous) ? previous.GetString() : null;
        var cached = _semanticTokens.TryGetValue(key, out var old) ? old : null;
        JsonNode result;
        if (delta && cached is not null && cached.ResultId == previousId)
        {
            if (data.AsSpan().SequenceEqual(cached.Data.AsSpan()))
            {
                // Tokens are byte-identical to what the client already holds; an empty
                // edit set avoids resending the full array on every no-change request.
                return new JsonObject
                {
                    ["resultId"] = cached.ResultId,
                    ["edits"] = new JsonArray(),
                };
            }

            result = new JsonObject
            {
                ["resultId"] = resultId,
                ["edits"] = new JsonArray(new JsonObject
                {
                    ["start"] = 0,
                    ["deleteCount"] = cached.Data.Length,
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

        StoreSemanticTokens(key, new SemanticTokenState(resultId, data));
        return result;
    }

    private void StoreSemanticTokens(string key, SemanticTokenState state)
    {
        _semanticTokens[key] = state;
        _semanticTokenOrder.Enqueue(key);
        while (_semanticTokenOrder.Count > MaximumCachedSemanticTokens &&
               _semanticTokenOrder.TryDequeue(out var oldest))
        {
            // A racing store may have replaced the entry; removing the fresh state only
            // costs the client a full resend, never correctness.
            _semanticTokens.TryRemove(oldest, out _);
        }
    }

    /// <summary>Drops cached semantic tokens for documents that left the workspace.</summary>
    internal void ForgetSemanticTokens(Uri uri) => _semanticTokens.TryRemove(uri.AbsoluteUri, out _);

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
        if (reference.ResolutionKind == LuaNameResolutionKind.Global && workspace.GetSnapshot() is { } snapshot)
        {
            var builder = ImmutableArray.CreateBuilder<JsonObject>();
            var key = analysis.Compilation.SemanticModel.GetSymbolKey(reference.Symbol, analysis.Module);
            foreach (var item in snapshot.FindReferences(key))
            {
                var uri = workspace.GetUri(item.Module.Name);
                if (uri is not null && workspace.TryGetDocument(uri, out var document))
                {
                    builder.Add(Location(uri, document.ToRange(item.Span)));
                }
            }

            if (includeDeclaration)
            {
                builder.Add(Location(analysis.Document.Uri, analysis.Document.ToRange(
                    NormalizeDeclaringSpan(reference.Symbol, analysis.Document))));
            }

            return DeduplicateLocations(builder).ToImmutableArray();
        }

        return GetSymbolReferenceLocations(analysis, reference.Symbol, includeDeclaration);
    }

    /// <summary>Same-file references of a symbol plus its declaration.</summary>
    private static ImmutableArray<JsonObject> GetSymbolReferenceLocations(
        LanguageDocumentAnalysis analysis,
        LuaSymbol symbol,
        bool includeDeclaration)
    {
        var builder = ImmutableArray.CreateBuilder<JsonObject>();
        foreach (var item in analysis.Compilation.SemanticModel.References.Where(item =>
                     item.Symbol.Id == symbol.Id))
        {
            builder.Add(Location(analysis.Document.Uri, analysis.Document.ToRange(item.Span)));
        }

        if (includeDeclaration)
        {
            builder.Add(Location(analysis.Document.Uri, analysis.Document.ToRange(
                NormalizeDeclaringSpan(symbol, analysis.Document))));
        }

        return DeduplicateLocations(builder).ToImmutableArray();
    }

    private const int MacroTokenType = 5;
    private const int ClassTokenType = 6;
    private const int TypeTokenType = 7;
    private const int TypeParameterTokenType = 8;
    private const int EnumTokenType = 9;
    private const int StringTokenType = 10;
    private const int NumberTokenType = 11;
    private const int DeclarationTokenModifier = 1;

    private static ImmutableArray<int> BuildSemanticTokens(LanguageDocumentAnalysis analysis)
    {
        // Name references and member/index references are both highlighted; member
        // tokens distinguish method calls, plain calls, and plain field accesses.
        // Parsed annotations contribute their own tokens — @tag keywords, declared
        // names, and type expressions — so LuaLS/EmmyLua directives get the same
        // structural highlighting as the code they document.
        var tokens = analysis.Compilation.SemanticModel.UnifiedReferences
            .Select(reference =>
            {
                var range = analysis.Document.ToRange(reference.Span);
                int type;
                int modifiers = (reference.Access.HasFlag(LuaReferenceAccess.Write) ? 4 : 0) |
                    (reference.LexicalReference?.Symbol.IsReadOnly == true ? 2 : 0) |
                    (reference.LexicalReference?.Symbol.IsCaptured == true ? 8 : 0);
                if (reference.LexicalReference is { } lexical)
                {
                    type = lexical.Symbol.Kind switch
                    {
                        LuaSymbolKind.Parameter => 1,
                        LuaSymbolKind.Global => 3,
                        _ => GetType(analysis, lexical.Symbol) is LuaFunctionType ? 2 : 0,
                    };
                }
                else
                {
                    type = reference.Access switch
                    {
                        var access when access.HasFlag(LuaReferenceAccess.MethodCall) => 4,
                        var access when access.HasFlag(LuaReferenceAccess.Call) => 2,
                        _ => 3,
                    };
                }

                return (Line: range.Start.Line, Character: range.Start.Character,
                    Length: Math.Max(1, range.End.Character - range.Start.Character), Type: type,
                    Modifiers: modifiers);
            })
            .Concat(BuildAnnotationTokens(analysis))
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
            builder.Add(token.Length);
            builder.Add(token.Type);
            builder.Add(token.Modifiers);
            previousLine = token.Line;
            previousCharacter = token.Character;
        }

        return builder.MoveToImmutable();
    }

    /// <summary>
    /// Highlights annotation directives: the <c>@tag</c> keyword as a macro, declared
    /// class/field/param names with the declaration modifier, and every type expression.
    /// </summary>
    private static List<(int Line, int Character, int Length, int Type, int Modifiers)> BuildAnnotationTokens(
        LanguageDocumentAnalysis analysis)
    {
        var annotationDocument = analysis.Compilation.Annotations;
        if (annotationDocument is null || annotationDocument.Annotations.IsDefaultOrEmpty)
        {
            return [];
        }

        var tokens = new List<(int Line, int Character, int Length, int Type, int Modifiers)>();

        void Walk(LuaTypeSyntax? type)
        {
            switch (type)
            {
                case null:
                    break;
                case LuaNamedTypeSyntax:
                    // The span covers the dotted name and its generic arguments; nested
                    // arguments are left to the parent token so spans never overlap.
                    Emit(annotationDocument, analysis.Document, type.Span, TypeTokenType, 0, tokens);
                    break;
                case LuaLiteralTypeSyntax literal:
                    Emit(
                        annotationDocument,
                        analysis.Document,
                        literal.Span,
                        literal.Kind switch
                        {
                            LuaTypeLiteralKind.Text => StringTokenType,
                            LuaTypeLiteralKind.Number => NumberTokenType,
                            _ => TypeTokenType,
                        },
                        0,
                        tokens);
                    break;
                case LuaFunctionTypeSyntax function:
                    // 'fun' starts the function type span; the keyword is highlighted as a
                    // function so signature types read like code.
                    Emit(
                        annotationDocument,
                        analysis.Document,
                        new TextSpan(function.Span.Start, 3),
                        2,
                        0,
                        tokens);
                    foreach (var parameter in function.Parameters)
                    {
                        Emit(annotationDocument, analysis.Document, parameter.NameSpan, 1, 0, tokens);
                        Walk(parameter.Type);
                    }

                    foreach (var returnType in function.Returns)
                    {
                        Walk(returnType);
                    }

                    break;
                case LuaTableTypeSyntax table:
                    foreach (var field in table.Fields)
                    {
                        Emit(annotationDocument, analysis.Document, field.NameSpan, 3, 0, tokens);
                        Walk(field.KeyType);
                        Walk(field.ValueType);
                    }

                    break;
                case LuaUnionTypeSyntax union:
                    foreach (var member in union.Types)
                    {
                        Walk(member);
                    }

                    break;
                case LuaIntersectionTypeSyntax intersection:
                    foreach (var member in intersection.Types)
                    {
                        Walk(member);
                    }

                    break;
                case LuaNullableTypeSyntax nullable:
                    Walk(nullable.Type);
                    break;
                case LuaArrayTypeSyntax array:
                    Walk(array.ElementType);
                    break;
                case LuaTupleTypeSyntax tuple:
                    foreach (var element in tuple.Elements)
                    {
                        Walk(element);
                    }

                    break;
                case LuaVarargTypeSyntax vararg:
                    Walk(vararg.ElementType);
                    break;
            }
        }

        foreach (var annotation in annotationDocument.Annotations)
        {
            Emit(annotationDocument, analysis.Document, annotation.TagSpan, MacroTokenType, 0, tokens);
            switch (annotation)
            {
                case LuaClassAnnotationSyntax classAnnotation:
                    Emit(annotationDocument, analysis.Document, classAnnotation.NameSpan, ClassTokenType,
                        DeclarationTokenModifier, tokens);
                    foreach (var baseType in classAnnotation.BaseTypes)
                    {
                        Walk(baseType);
                    }

                    break;
                case LuaFieldAnnotationSyntax fieldAnnotation:
                    Emit(annotationDocument, analysis.Document, fieldAnnotation.NameSpan, 3,
                        DeclarationTokenModifier, tokens);
                    Walk(fieldAnnotation.Type);
                    break;
            case LuaAliasAnnotationSyntax aliasAnnotation:
                Emit(annotationDocument, analysis.Document, aliasAnnotation.NameSpan, TypeTokenType,
                    DeclarationTokenModifier, tokens);
                Walk(aliasAnnotation.Type);
                break;
            case LuaEnumAnnotationSyntax enumAnnotation:
                Emit(annotationDocument, analysis.Document, enumAnnotation.NameSpan, EnumTokenType,
                    DeclarationTokenModifier, tokens);
                Walk(enumAnnotation.KeyType);
                break;
                case LuaParamAnnotationSyntax paramAnnotation:
                    Emit(annotationDocument, analysis.Document, paramAnnotation.NameSpan, 1,
                        DeclarationTokenModifier, tokens);
                    Walk(paramAnnotation.Type);
                    break;
                case LuaTypeAnnotationSyntax typeAnnotation:
                    foreach (var type in typeAnnotation.Types)
                    {
                        Walk(type);
                    }

                    break;
                case LuaVarargAnnotationSyntax varargAnnotation:
                    Walk(varargAnnotation.Type);
                    break;
                case LuaReturnAnnotationSyntax returnAnnotation:
                    foreach (var returned in returnAnnotation.Returns)
                    {
                        Emit(annotationDocument, analysis.Document, returned.NameSpan, 1, 0, tokens);
                        Walk(returned.Type);
                    }

                    break;
                case LuaGenericAnnotationSyntax genericAnnotation:
                    foreach (var parameter in genericAnnotation.Parameters)
                    {
                        Emit(annotationDocument, analysis.Document, parameter.NameSpan, TypeParameterTokenType,
                            DeclarationTokenModifier, tokens);
                        Walk(parameter.Constraint);
                    }

                    break;
                case LuaOverloadAnnotationSyntax overloadAnnotation:
                    Walk(overloadAnnotation.Type);
                    break;
                case LuaAliasContinuationAnnotationSyntax continuation:
                    Walk(continuation.Type);
                    break;
                case LuaCastAnnotationSyntax castAnnotation:
                    Emit(annotationDocument, analysis.Document, castAnnotation.NameSpan, 0, 0, tokens);
                    Walk(castAnnotation.Type);
                    break;
            }
        }

        return tokens;

        static void Emit(
            LuaAnnotationDocument document,
            LspTextDocument text,
            TextSpan span,
            int type,
            int modifiers,
            List<(int Line, int Character, int Length, int Type, int Modifiers)> tokens)
        {
            if (span.Length <= 0 || span.End > text.ByteLength)
            {
                return;
            }

            var range = text.ToRange(span);
            if (range.End.Line != range.Start.Line || range.End.Character <= range.Start.Character)
            {
                return;
            }

            tokens.Add((range.Start.Line, range.Start.Character,
                range.End.Character - range.Start.Character, type, modifiers));
        }
    }

    private async Task<DocumentContext?> GetContextAsync(JsonElement parameters, CancellationToken cancellationToken)
    {
        var uri = GetUri(parameters);
        var analysis = await workspace.GetAnalysisAsync(uri, cancellationToken).ConfigureAwait(false);
        if (analysis is null) return null;
        // Requests such as documentSymbol and semanticTokens/full carry no cursor position.
        // Default to the document start instead of failing the whole request.
        var position = parameters.TryGetProperty("position", out var positionElement)
            ? GetPosition(positionElement)
            : new LspPosition(0, 0);
        return new DocumentContext(analysis, position, analysis.Document.ToByteOffset(position));
    }

    private static LuaNameReference? FindReference(LanguageDocumentAnalysis analysis, int offset) =>
        analysis.Compilation.SemanticModel.References.Where(reference => Contains(reference.Span, offset))
            .OrderBy(static reference => reference.Span.Length).FirstOrDefault();

    private static LuaType? GetType(LanguageDocumentAnalysis analysis, LuaSymbol symbol) =>
        ServiceCaches.Get(analysis).SymbolTypes.TryGetValue(symbol.Id, out var type)
            ? type
            : null;

    private static int GetSymbolKind(LuaSymbol symbol, LanguageDocumentAnalysis analysis) =>
        GetType(analysis, symbol) is LuaFunctionType ? 12 : symbol.Kind == LuaSymbolKind.Parameter ? 26 : 13;

    private static Lunil.Core.Text.TextSpan NormalizeDeclaringSpan(LuaSymbol symbol, LspTextDocument document) =>
        symbol.DeclaringSpan.Length > 0
            ? symbol.DeclaringSpan
            : new Lunil.Core.Text.TextSpan(symbol.DeclaringSpan.Start, Encoding.UTF8.GetByteCount(symbol.Name));

    private static bool Contains(Lunil.Core.Text.TextSpan span, int offset) =>
        offset >= span.Start && offset <= span.End;

    private static JsonObject CompletionItem(string label, int kind, string detail, string? sortText = null) => new()
    {
        ["label"] = label,
        ["kind"] = kind,
        ["detail"] = detail,
        ["sortText"] = sortText ?? label,
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
