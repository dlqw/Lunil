using System.Collections.Immutable;
using System.Text;
using Lunil.Analysis;
using Lunil.Core.Text;
using Lunil.Semantics.Binding;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

namespace Lunil.Workspace;

internal static class WorkspaceSymbolGraphBuilder
{
    public static string? GetDirectReExportTarget(LuaWorkspaceModuleResult module) =>
        FindDirectReExport(module)?.Target?.Name;

    public static ImmutableArray<LuaWorkspaceExportSymbol> BuildModuleSymbols(
        LuaWorkspaceModuleResult module)
    {
        var symbols = new List<LuaWorkspaceExportSymbol>();
        AddModuleSymbols(module, symbols);
        return [.. symbols
            .OrderBy(static item => item.Path, StringComparer.Ordinal)
            .ThenBy(static item => item.Key, StringComparer.Ordinal)];
    }

    public static (LuaWorkspaceExportGraph Exports, LuaWorkspaceModuleCallBindings Calls,
        ImmutableDictionary<string, ImmutableArray<LuaWorkspaceExportSymbol>> ModuleSymbols) Build(
        ImmutableArray<LuaWorkspaceModuleResult> modules,
        LuaHostAnalysisContract? hostContract)
    {
        var symbols = new List<LuaWorkspaceExportSymbol>();
        var edges = new List<LuaWorkspaceExportEdge>();
        foreach (var module in modules)
        {
            symbols.AddRange(BuildModuleSymbols(module));
        }

        AddHostSymbols(hostContract, symbols);
        var lookup = symbols
            .GroupBy(static symbol => (symbol.ModuleName, symbol.Path, symbol.IsExternal))
            .ToDictionary(static group => group.Key, static group => group.First());
        var luaSymbolsByModule = symbols
            .Where(static symbol => !symbol.IsExternal)
            .GroupBy(static symbol => symbol.ModuleName, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.ToImmutableArray(),
                StringComparer.Ordinal);
        foreach (var module in modules)
        {
            var reExport = FindDirectReExport(module);
            if (reExport?.Target is null)
            {
                continue;
            }

            foreach (var symbol in luaSymbolsByModule.GetValueOrDefault(module.Identity.Name, []))
            {
                if (!lookup.TryGetValue((reExport.Target.Name, symbol.Path, false), out var target))
                {
                    continue;
                }

                var updated = symbol with
                {
                    TargetKey = target.Key,
                    IsReExport = true,
                    FunctionKey = target.FunctionKey,
                };
                symbols[symbols.IndexOf(symbol)] = updated;
                edges.Add(new LuaWorkspaceExportEdge(updated.Key, target.Key, "re-export"));
            }
        }

        lookup = symbols
            .GroupBy(static symbol => (symbol.ModuleName, symbol.Path, symbol.IsExternal))
            .ToDictionary(static group => group.Key, static group => group.First());
        AddAnalysisFactEdges(modules, hostContract, lookup, edges);

        var orderedSymbols = symbols
            .OrderBy(static item => item.ModuleName, StringComparer.Ordinal)
            .ThenBy(static item => item.Path, StringComparer.Ordinal)
            .ThenBy(static item => item.Key, StringComparer.Ordinal)
            .ToImmutableArray();
        var graph = new LuaWorkspaceExportGraph(
            orderedSymbols,
            [.. edges.OrderBy(static item => item.SourceKey, StringComparer.Ordinal)]);
        var calls = BuildCalls(modules, graph, hostContract);
        var byModule = orderedSymbols
            .Where(static item => !item.IsExternal)
            .GroupBy(static item => item.ModuleName, StringComparer.Ordinal)
            .ToImmutableDictionary(
                static group => group.Key,
                static group => group.ToImmutableArray(),
                StringComparer.Ordinal);
        return (graph, calls, byModule);
    }

    private static void AddModuleSymbols(
        LuaWorkspaceModuleResult module,
        List<LuaWorkspaceExportSymbol> destination)
    {
        var rootSpan = FindRootReturnSpan(module);
        AddTypeSymbols(
            module.Identity.Name,
            module.ExportedType,
            path: string.Empty,
            rootSpan,
            isExternal: false,
            externalSource: null,
            module,
            destination,
            new HashSet<LuaType>(ReferenceEqualityComparer.Instance),
            depth: 0);

        foreach (var callback in module.Compilation.Analysis.CallbackRegistrations)
        {
            LuaType type = callback.CallbackFunctionId is { } functionId
                ? (LuaType?)module.Compilation.Analysis.Functions.FirstOrDefault(item =>
                    item.FunctionId == functionId)?.Type ?? LuaTypes.Function
                : LuaTypes.Function;
            var definition = callback.CallbackFunctionId is { } id
                ? module.Compilation.SemanticModel.Functions.FirstOrDefault(item => item.Id == id)?.Span ??
                    callback.CallbackSpan
                : callback.CallbackSpan;
            var path = "$callback/" + callback.Span.Start;
            destination.Add(CreateSymbol(
                module.Identity.Name,
                path,
                LuaWorkspaceExportKind.Callback,
                type,
                definition,
                false,
                false,
                null));
        }

        foreach (var access in module.Compilation.Analysis.PersistenceAccesses)
        {
            var schemaPath = "$persistence-schema/" + access.SchemaId + "/" + access.SchemaVersion;
            if (!destination.Any(symbol =>
                    symbol.ModuleName == module.Identity.Name && symbol.Path == schemaPath))
            {
                destination.Add(CreateSymbol(
                    module.Identity.Name,
                    schemaPath,
                    LuaWorkspaceExportKind.Persistence,
                    access.ValueType,
                    access.Span,
                    false,
                    false,
                    null));
            }

            var path = "$persistence/" + access.SchemaId + "/" + access.Operation + "/" +
                access.Span.Start;
            destination.Add(CreateSymbol(
                module.Identity.Name,
                path,
                LuaWorkspaceExportKind.Persistence,
                access.ValueType,
                access.Span,
                false,
                access.IsDynamicKey,
                null));
        }
    }

    private static void AddHostSymbols(
        LuaHostAnalysisContract? contract,
        List<LuaWorkspaceExportSymbol> destination)
    {
        if (contract is null)
        {
            return;
        }

        foreach (var module in contract.Modules.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            AddTypeSymbols(
                module.Key,
                LuaHostAnalysisContract.ToLuaType(module.Value),
                string.Empty,
                default,
                isExternal: true,
                externalSource: null,
                moduleResult: null,
                destination,
                new HashSet<LuaType>(ReferenceEqualityComparer.Instance),
                depth: 0,
                contract.ContractId);

            var prefix = module.Key + ".";
            foreach (var function in contract.Functions.Values.Where(function =>
                         function.Path.StartsWith(prefix, StringComparison.Ordinal)))
            {
                var modulePath = function.Path[prefix.Length..];
                var index = destination.FindIndex(symbol =>
                    symbol.IsExternal && symbol.ModuleName == module.Key && symbol.Path == modulePath);
                if (index >= 0)
                {
                    destination[index] = destination[index] with
                    {
                        ExternalSource = function.Source,
                        FunctionKey = CreateHostFunctionKey(contract.ContractId, function.Path),
                    };
                }
            }
        }

        var hostModule = "$host:" + contract.ContractId;
        foreach (var function in contract.Functions.Values.OrderBy(static item => item.Path, StringComparer.Ordinal))
        {
            var type = LuaHostAnalysisContract.ToLuaType(new LuaHostTypeDescriptor
            {
                Kind = LuaHostTypeKind.Function,
                Parameters = function.Parameters,
                Returns = function.Returns,
                HasVariadicParameters = function.HasVariadicParameters,
                HasVariadicReturns = function.HasVariadicReturns,
            });
            destination.Add(CreateSymbol(
                hostModule,
                function.Path,
                LuaWorkspaceExportKind.Function,
                type,
                default,
                true,
                false,
                function.Source,
                contract.ContractId) with
            {
                FunctionKey = CreateHostFunctionKey(contract.ContractId, function.Path),
            });
        }
    }

    private static void AddTypeSymbols(
        string moduleName,
        LuaType type,
        string path,
        TextSpan rootSpan,
        bool isExternal,
        LuaHostSourceLocation? externalSource,
        LuaWorkspaceModuleResult? moduleResult,
        List<LuaWorkspaceExportSymbol> destination,
        HashSet<LuaType> visiting,
        int depth,
        string? contractId = null)
    {
        if (depth >= 32 || !visiting.Add(type))
        {
            destination.Add(CreateSymbol(moduleName, path, LuaWorkspaceExportKind.Dynamic,
                LuaTypes.Any, rootSpan, isExternal, true, externalSource, contractId));
            return;
        }

        try
        {
            if (type is LuaUnionType union)
            {
                var candidates = union.Types.Where(static item => item.Kind is not
                    (LuaTypeKind.Nil or LuaTypeKind.Boolean or LuaTypeKind.Literal)).ToArray();
                if (candidates.Length == 1)
                {
                    AddTypeSymbols(moduleName, candidates[0], path, rootSpan, isExternal,
                        externalSource, moduleResult, destination, visiting, depth + 1, contractId);
                    return;
                }
            }

            var kind = GetExportKind(type, path);
            var span = moduleResult is null ? rootSpan : FindDefinitionSpan(moduleResult, path, rootSpan);
            var symbol = CreateSymbol(moduleName, path, kind, type, span, isExternal,
                type.Kind is LuaTypeKind.Any or LuaTypeKind.Unknown, externalSource, contractId);
            if (kind == LuaWorkspaceExportKind.Function)
            {
                symbol = symbol with
                {
                    FunctionKey = isExternal
                        ? null
                        : FindFunctionKey(moduleResult!, span),
                };
            }

            destination.Add(symbol);

            var shape = type switch
            {
                LuaPrototypeType prototype => prototype.Shape,
                LuaMetatableType metatable => metatable.BaseType,
                _ => type,
            };
            if (shape is LuaStructuralTableType table)
            {
                foreach (var field in table.Fields.Where(static item => item.Name is not null)
                             .OrderBy(static item => item.Name, StringComparer.Ordinal))
                {
                    var childPath = string.IsNullOrEmpty(path) ? field.Name! : path + "." + field.Name;
                    AddTypeSymbols(moduleName, field.ValueType, childPath, rootSpan, isExternal,
                        externalSource, moduleResult, destination, visiting, depth + 1, contractId);
                }

                if (table.IsOpen)
                {
                    var dynamicPath = string.IsNullOrEmpty(path) ? "*" : path + ".*";
                    destination.Add(CreateSymbol(moduleName, dynamicPath, LuaWorkspaceExportKind.Dynamic,
                        LuaTypes.Any, span, isExternal, true, externalSource, contractId));
                }
            }
        }
        finally
        {
            visiting.Remove(type);
        }
    }

    private static LuaWorkspaceModuleCallBindings BuildCalls(
        ImmutableArray<LuaWorkspaceModuleResult> modules,
        LuaWorkspaceExportGraph exports,
        LuaHostAnalysisContract? hostContract)
    {
        var calls = new List<LuaWorkspaceModuleCallBinding>();
        var exportLookup = exports.Symbols
            .GroupBy(static symbol => (symbol.IsExternal, symbol.ModuleName, symbol.Path))
            .ToDictionary(static group => group.Key, static group => group.First());
        var exportsByModule = exports.Symbols
            .GroupBy(static symbol => (symbol.IsExternal, symbol.ModuleName))
            .ToDictionary(static group => group.Key, static group => group.ToImmutableArray());
        foreach (var module in modules)
        {
            var aliases = BuildRequireAliases(module);
            var referencesBySpan = module.Compilation.SemanticModel.References
                .GroupBy(static reference => reference.Span)
                .ToDictionary(static group => group.Key, static group => group.First());
            var callNodes = module.Compilation.SemanticModel.Syntax.Root.DescendantNodes()
                .Where(static node => node.Kind is LuaSyntaxKind.CallExpression or
                    LuaSyntaxKind.MethodCallExpression)
                .GroupBy(static node => node.Span)
                .ToDictionary(static group => group.Key, static group => group.First());
            foreach (var call in module.Compilation.Analysis.CallGraph.Edges)
            {
                if (!TryGetModuleCall(
                        module,
                        call,
                        aliases,
                        callNodes,
                        referencesBySpan,
                        out var dependency,
                        out var memberPath,
                        out var blockedReason))
                {
                    continue;
                }

                var external = dependency.Kind == LuaModuleDependencyKind.Host;
                // Resolve through the search-path-resolved target when present, so the
                // export lookup keys on the module identity rather than the raw require
                // string (e.g. `Utils.HttpUtils` -> `scripts.client.Utils.HttpUtils`).
                var resolvedModuleName = dependency.Target?.Name ?? dependency.RequestedName;
                var target = blockedReason is null &&
                    exportLookup.TryGetValue((external, resolvedModuleName, memberPath), out var found)
                        ? found
                        : null;
                var candidates = blockedReason is not null
                    ? []
                    : target is null
                    ? exportsByModule.GetValueOrDefault((external, resolvedModuleName), [])
                        .Where(symbol =>
                            (symbol.Name == memberPath.Split('.').LastOrDefault() || symbol.IsDynamic))
                        .Select(static symbol => symbol.Key).OrderBy(static key => key, StringComparer.Ordinal)
                        .ToImmutableArray()
                    : target.IsDynamic ? [target.Key] : [];
                var status = blockedReason is not null
                    ? LuaWorkspaceBindingStatus.Unresolved
                    : target is not null && !target.IsDynamic
                    ? LuaWorkspaceBindingStatus.Resolved
                    : candidates.Length != 0
                        ? LuaWorkspaceBindingStatus.Dynamic
                        : LuaWorkspaceBindingStatus.Unresolved;
                calls.Add(new LuaWorkspaceModuleCallBinding(
                    module.Identity.Name,
                    call.Span,
                    call.ContainingFunctionId,
                    dependency.RequestedName,
                    memberPath,
                    target?.Key,
                    target?.Kind == LuaWorkspaceExportKind.Function ? target.FunctionKey : null,
                    candidates,
                    status,
                    blockedReason ?? (status == LuaWorkspaceBindingStatus.Resolved ? null :
                        status == LuaWorkspaceBindingStatus.Dynamic ? "dynamic-export-candidate" :
                        "exported-member-not-found"),
                    target is { IsExternal: false } ? target.DefinitionSpan : null,
                    target?.ExternalSource,
                    target?.ExternalSource is { ImplementationUri: not null } source
                        ? source with { Uri = source.ImplementationUri! }
                        : null));
            }

            foreach (var effect in module.Compilation.Analysis.HostEffects)
            {
                if (hostContract is null)
                {
                    continue;
                }

                var hostModule = "$host:" + hostContract.ContractId;
                var target = exports.Find(hostModule, effect.FunctionPath);
                calls.Add(new LuaWorkspaceModuleCallBinding(
                    module.Identity.Name,
                    effect.Span,
                    GetContainingFunctionId(module, effect.Span),
                    hostModule,
                    effect.FunctionPath,
                    target?.Key,
                    target?.FunctionKey,
                    [],
                    LuaWorkspaceBindingStatus.Resolved,
                    null,
                    null,
                    effect.Source,
                    effect.Source is { ImplementationUri: not null } source
                        ? source with { Uri = source.ImplementationUri! }
                        : null));
            }
        }

        return new LuaWorkspaceModuleCallBindings([.. calls
            .GroupBy(static call => (call.SourceModuleName, call.Span))
            .Select(static group => group.First())
            .OrderBy(static call => call.SourceModuleName, StringComparer.Ordinal)
            .ThenBy(static call => call.Span.Start)]);
    }

    private static Dictionary<int, RequireAlias> BuildRequireAliases(
        LuaWorkspaceModuleResult module)
    {
        var result = new Dictionary<int, RequireAlias>();
        var root = module.Compilation.SemanticModel.Syntax.Root;
        var writesBySymbol = module.Compilation.SemanticModel.UnifiedReferences
            .Where(static reference => reference.LexicalReference is not null &&
                reference.Access.HasFlag(LuaReferenceAccess.Write))
            .GroupBy(static reference => reference.LexicalReference!.Symbol.Id)
            .ToDictionary(
                static group => group.Key,
                static group => group.Select(static reference => reference.Span.Start)
                    .OrderBy(static start => start)
                    .ToImmutableArray());
        foreach (var dependency in module.Dependencies.Where(static item => item.Kind is
                     LuaModuleDependencyKind.Static or LuaModuleDependencyKind.Host))
        {
            var declaration = root.DescendantNodes().FirstOrDefault(node =>
                node.Kind == LuaSyntaxKind.LocalDeclarationStatement &&
                node.Span.Start <= dependency.Span.Start && node.Span.End >= dependency.Span.End);
            var name = declaration?.DescendantTokens().FirstOrDefault(static token =>
                token.Kind == LuaTokenKind.Identifier && !token.IsMissing);
            var symbol = name is null ? null : module.Compilation.SemanticModel.Symbols.FirstOrDefault(item =>
                item.DeclaringSpan == name.Span);
            if (symbol is not null)
            {
                result[symbol.Id] = new RequireAlias(
                    dependency,
                    writesBySymbol.GetValueOrDefault(symbol.Id, []));
            }
        }

        return result;
    }

    private static bool TryGetModuleCall(
        LuaWorkspaceModuleResult module,
        LuaCallSite call,
        Dictionary<int, RequireAlias> aliases,
        Dictionary<TextSpan, LuaSyntaxNode> callNodes,
        Dictionary<TextSpan, LuaNameReference> referencesBySpan,
        out LuaModuleDependency dependency,
        out string memberPath,
        out string? blockedReason)
    {
        dependency = null!;
        memberPath = string.Empty;
        blockedReason = null;
        callNodes.TryGetValue(call.Span, out var node);
        if (node is null || !TryGetCalleePath(
                module,
                node,
                referencesBySpan,
                out var rootSymbol,
                out var directRequire,
                out memberPath))
        {
            return false;
        }

        if (rootSymbol is { } symbolId && aliases.TryGetValue(symbolId, out var alias))
        {
            dependency = alias.Dependency;
            var dependencyEnd = alias.Dependency.Span.End;
            var wasReassigned = alias.WriteStarts.Any(start =>
                start > dependencyEnd && start < call.Span.Start);
            if (wasReassigned)
            {
                blockedReason = "module-alias-reassigned";
            }

            return true;
        }

        if (directRequire is { } requireSpan)
        {
            var directDependency = module.Dependencies.FirstOrDefault(item =>
                item.Kind is LuaModuleDependencyKind.Static or LuaModuleDependencyKind.Host &&
                item.Span == requireSpan);
            if (directDependency is null)
            {
                return false;
            }

            dependency = directDependency;
            return true;
        }

        return false;
    }

    private static bool TryGetCalleePath(
        LuaWorkspaceModuleResult module,
        LuaSyntaxNode call,
        Dictionary<TextSpan, LuaNameReference> referencesBySpan,
        out int? rootSymbol,
        out TextSpan? directRequire,
        out string path)
    {
        rootSymbol = null;
        directRequire = null;
        path = string.Empty;
        var members = new List<string>();
        LuaSyntaxNode? current;
        if (call.Kind == LuaSyntaxKind.MethodCallExpression)
        {
            current = call.ChildNodes().FirstOrDefault(static item => item.Kind != LuaSyntaxKind.ArgumentList);
            var method = call.ChildTokens().LastOrDefault(static token =>
                token.Kind == LuaTokenKind.Identifier && !token.IsMissing);
            if (method is not null)
            {
                members.Add(GetText(module, method));
            }
        }
        else
        {
            current = call.ChildNodes().FirstOrDefault(static item => item.Kind != LuaSyntaxKind.ArgumentList);
        }

        while (current?.Kind is LuaSyntaxKind.MemberAccessExpression or LuaSyntaxKind.IndexExpression)
        {
            if (current.Kind == LuaSyntaxKind.MemberAccessExpression)
            {
                var member = current.ChildTokens().LastOrDefault(static token =>
                    token.Kind == LuaTokenKind.Identifier && !token.IsMissing);
                if (member is null)
                {
                    return false;
                }

                members.Add(GetText(module, member));
                current = current.ChildNodes().FirstOrDefault();
            }
            else
            {
                var nodes = current.ChildNodes().ToArray();
                if (nodes.Length < 2 || !nodes[1].TryGetConstantString(out var member))
                {
                    return false;
                }

                members.Add(member);
                current = nodes[0];
            }
        }

        members.Reverse();
        path = string.Join(".", members);
        if (current?.Kind == LuaSyntaxKind.IdentifierExpression)
        {
            var token = current.ChildTokens().FirstOrDefault(static item => item.Kind == LuaTokenKind.Identifier);
            var reference = token is not null && referencesBySpan.TryGetValue(token.Span, out var resolved)
                ? resolved
                : null;
            rootSymbol = reference?.Symbol.Id;
            return rootSymbol.HasValue;
        }

        if (current?.Kind == LuaSyntaxKind.CallExpression &&
            module.Dependencies.Any(item => item.Span == current.Span))
        {
            directRequire = current.Span;
            return true;
        }

        return false;
    }

    private static LuaWorkspaceExportKind GetExportKind(LuaType type, string path) => type switch
    {
        LuaFunctionType or LuaOverloadType or LuaCallableType => LuaWorkspaceExportKind.Function,
        LuaPrototypeType or LuaClassType => LuaWorkspaceExportKind.Class,
        LuaAliasType => LuaWorkspaceExportKind.Alias,
        _ when string.IsNullOrEmpty(path) => LuaWorkspaceExportKind.Module,
        _ when type.Kind is LuaTypeKind.Any or LuaTypeKind.Unknown => LuaWorkspaceExportKind.Dynamic,
        _ => LuaWorkspaceExportKind.Field,
    };

    private static LuaWorkspaceExportSymbol CreateSymbol(
        string moduleName,
        string path,
        LuaWorkspaceExportKind kind,
        LuaType type,
        TextSpan span,
        bool external,
        bool dynamic,
        LuaHostSourceLocation? source,
        string? contractId = null)
    {
        var canonicalPath = string.IsNullOrEmpty(path) ? "$module" : path;
        var key = external
            ? "host:" + contractId + "/" + moduleName + "::" + canonicalPath
            : "lua:" + moduleName + "::" + canonicalPath;
        return new LuaWorkspaceExportSymbol(
            key,
            moduleName,
            path,
            string.IsNullOrEmpty(path) ? moduleName : path.Split('.').Last(),
            kind,
            type,
            span,
            null,
            false,
            external,
            dynamic,
            source);
    }

    private static void AddAnalysisFactEdges(
        ImmutableArray<LuaWorkspaceModuleResult> modules,
        LuaHostAnalysisContract? contract,
        Dictionary<(string ModuleName, string Path, bool IsExternal), LuaWorkspaceExportSymbol> lookup,
        List<LuaWorkspaceExportEdge> destination)
    {
        var hostModule = contract is null ? null : "$host:" + contract.ContractId;
        foreach (var module in modules)
        {
            foreach (var callback in module.Compilation.Analysis.CallbackRegistrations)
            {
                if (!lookup.TryGetValue(
                        (module.Identity.Name, "$callback/" + callback.Span.Start, false),
                        out var callbackSymbol))
                {
                    continue;
                }

                if (hostModule is not null &&
                    lookup.TryGetValue((hostModule, callback.FunctionPath, true), out var hostTarget))
                {
                    destination.Add(new LuaWorkspaceExportEdge(
                        callbackSymbol.Key,
                        hostTarget.Key,
                        "callback-registration"));
                }

                if (callback.UnsubscribeFunction is { } unsubscribe && hostModule is not null &&
                    lookup.TryGetValue((hostModule, unsubscribe, true), out var unsubscribeTarget))
                {
                    destination.Add(new LuaWorkspaceExportEdge(
                        callbackSymbol.Key,
                        unsubscribeTarget.Key,
                        "callback-unsubscribe"));
                }
            }

            foreach (var access in module.Compilation.Analysis.PersistenceAccesses)
            {
                var accessPath = "$persistence/" + access.SchemaId + "/" + access.Operation + "/" +
                    access.Span.Start;
                var schemaPath = "$persistence-schema/" + access.SchemaId + "/" + access.SchemaVersion;
                if (!lookup.TryGetValue((module.Identity.Name, accessPath, false), out var accessSymbol))
                {
                    continue;
                }

                if (lookup.TryGetValue((module.Identity.Name, schemaPath, false), out var schemaSymbol))
                {
                    destination.Add(new LuaWorkspaceExportEdge(
                        accessSymbol.Key,
                        schemaSymbol.Key,
                        "persistence-schema"));
                }

                if (hostModule is not null &&
                    lookup.TryGetValue((hostModule, access.FunctionPath, true), out var persistenceTarget))
                {
                    destination.Add(new LuaWorkspaceExportEdge(
                        accessSymbol.Key,
                        persistenceTarget.Key,
                        "persistence-access"));
                }

                if (access.MigrationFunction is { } migration && hostModule is not null &&
                    lookup.TryGetValue((hostModule, migration, true), out var migrationTarget))
                {
                    destination.Add(new LuaWorkspaceExportEdge(
                        schemaSymbol?.Key ?? accessSymbol.Key,
                        migrationTarget.Key,
                        "persistence-migration"));
                }
            }
        }
    }

    private static string CreateHostFunctionKey(string contractId, string path) =>
        "host-function:" + contractId + "::" + path;

    private static LuaModuleDependency? FindDirectReExport(LuaWorkspaceModuleResult module)
    {
        var root = module.Compilation.SemanticModel.Syntax.Root;
        return module.Dependencies.FirstOrDefault(dependency =>
            dependency.Kind == LuaModuleDependencyKind.Static && dependency.Target is not null &&
            root.DescendantNodes().Any(node => node.Kind == LuaSyntaxKind.ReturnStatement &&
                node.Span.Start <= dependency.Span.Start && node.Span.End >= dependency.Span.End));
    }

    private static TextSpan FindRootReturnSpan(LuaWorkspaceModuleResult module) =>
        FindMainReturnStatement(module.Compilation.SemanticModel.Syntax.Root)?.Span ?? default;

    private static LuaSyntaxNode? FindMainReturnStatement(LuaSyntaxNode root) =>
        root.ChildNodes().FirstOrDefault(static node => node.Kind == LuaSyntaxKind.Block)?
            .ChildNodes().LastOrDefault(static node => node.Kind == LuaSyntaxKind.ReturnStatement);

    private static TextSpan FindDefinitionSpan(
        LuaWorkspaceModuleResult module,
        string path,
        TextSpan fallback)
    {
        if (string.IsNullOrEmpty(path))
        {
            return fallback;
        }

        var segments = path.Split('.');
        var name = segments[^1];
        var syntax = module.Compilation.SemanticModel.Syntax.Root;
        var returned = FindMainReturnStatement(syntax)?
            .ChildNodes().FirstOrDefault(static node => node.Kind == LuaSyntaxKind.ExpressionList)?
            .ChildNodes().FirstOrDefault();
        if (returned is not null && TryFindTablePath(module, returned, segments, 0, out var tableValue))
        {
            return tableValue.Span;
        }

        foreach (var owner in syntax.DescendantNodes().Where(static node => node.Kind is
                     LuaSyntaxKind.FunctionDeclarationStatement or LuaSyntaxKind.LocalFunctionDeclarationStatement or
                     LuaSyntaxKind.GlobalDeclarationStatement))
        {
            var functionName = owner.ChildNodes().FirstOrDefault(static node =>
                node.Kind == LuaSyntaxKind.FunctionName);
            var declaredPath = functionName is null
                ? []
                : functionName.DescendantTokens()
                    .Where(static token => token.Kind == LuaTokenKind.Identifier && !token.IsMissing)
                    .Select(token => GetText(module, token))
                    .ToArray();
            if (EndsWithPath(declaredPath, segments))
            {
                return owner.Span;
            }
        }

        foreach (var assignment in syntax.DescendantNodes().Where(static node =>
                     node.Kind == LuaSyntaxKind.AssignmentStatement))
        {
            var variables = assignment.ChildNodes().FirstOrDefault(static node =>
                node.Kind == LuaSyntaxKind.VariableList)?.ChildNodes().ToArray() ?? [];
            var values = assignment.ChildNodes().FirstOrDefault(static node =>
                node.Kind == LuaSyntaxKind.ExpressionList)?.ChildNodes().ToArray() ?? [];
            for (var index = 0; index < Math.Min(variables.Length, values.Length); index++)
            {
                if (TryGetExpressionPath(module, variables[index], out var assignedPath) &&
                    EndsWithPath(assignedPath, segments))
                {
                    return values[index].Span;
                }
            }
        }

        var write = module.Compilation.SemanticModel.MemberReferences.FirstOrDefault(reference =>
            reference.Access.HasFlag(LuaReferenceAccess.Write) &&
            string.Equals(reference.Name, name, StringComparison.Ordinal));
        return write?.Span ?? fallback;
    }

    private static bool TryFindTablePath(
        LuaWorkspaceModuleResult module,
        LuaSyntaxNode expression,
        IReadOnlyList<string> path,
        int index,
        out LuaSyntaxNode value)
    {
        value = null!;
        if (expression.Kind != LuaSyntaxKind.TableConstructorExpression || index >= path.Count)
        {
            return false;
        }

        foreach (var field in expression.ChildNodes().Where(static node => node.Kind == LuaSyntaxKind.TableField))
        {
            var key = field.ChildTokens().FirstOrDefault(static token =>
                token.Kind == LuaTokenKind.Identifier && !token.IsMissing);
            if (key is null || !string.Equals(GetText(module, key), path[index], StringComparison.Ordinal))
            {
                continue;
            }

            var fieldValue = field.ChildNodes().LastOrDefault();
            if (fieldValue is null)
            {
                return false;
            }

            if (index == path.Count - 1)
            {
                value = fieldValue;
                return true;
            }

            return TryFindTablePath(module, fieldValue, path, index + 1, out value);
        }

        return false;
    }

    private static bool TryGetExpressionPath(
        LuaWorkspaceModuleResult module,
        LuaSyntaxNode expression,
        out string[] path)
    {
        var segments = new List<string>();
        var current = expression;
        while (current.Kind is LuaSyntaxKind.MemberAccessExpression or LuaSyntaxKind.IndexExpression)
        {
            if (current.Kind == LuaSyntaxKind.MemberAccessExpression)
            {
                var member = current.ChildTokens().LastOrDefault(static token =>
                    token.Kind == LuaTokenKind.Identifier && !token.IsMissing);
                if (member is null)
                {
                    path = [];
                    return false;
                }

                segments.Add(GetText(module, member));
                current = current.ChildNodes().First();
            }
            else
            {
                var children = current.ChildNodes().ToArray();
                if (children.Length < 2 || !children[1].TryGetConstantString(out var member))
                {
                    path = [];
                    return false;
                }

                segments.Add(member);
                current = children[0];
            }
        }

        if (current.Kind != LuaSyntaxKind.IdentifierExpression)
        {
            path = [];
            return false;
        }

        var root = current.ChildTokens().FirstOrDefault(static token =>
            token.Kind == LuaTokenKind.Identifier && !token.IsMissing);
        if (root is null)
        {
            path = [];
            return false;
        }

        segments.Add(GetText(module, root));
        segments.Reverse();
        path = [.. segments];
        return true;
    }

    private static bool EndsWithPath(string[] candidate, string[] path)
    {
        if (candidate.Length < path.Length)
        {
            return false;
        }

        var offset = candidate.Length - path.Length;
        for (var index = 0; index < path.Length; index++)
        {
            if (!string.Equals(candidate[offset + index], path[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string? FindFunctionKey(LuaWorkspaceModuleResult module, TextSpan definitionSpan)
    {
        var model = module.Compilation.SemanticModel;
        var function = model.Functions
            .Where(static candidate => candidate.Id != 0)
            .Where(candidate => candidate.Span == definitionSpan ||
                definitionSpan.Start <= candidate.Span.Start && definitionSpan.End >= candidate.Span.End)
            .OrderBy(candidate => candidate.Span == definitionSpan ? 0 : 1)
            .ThenBy(static candidate => candidate.Span.Length)
            .FirstOrDefault();
        return function is null ? null : model.GetFunctionKey(function, module.Identity).Value;
    }

    private static int GetContainingFunctionId(LuaWorkspaceModuleResult module, TextSpan span) =>
        module.Compilation.SemanticModel.Functions
            .Where(function => function.Span.Start <= span.Start && function.Span.End >= span.End)
            .OrderBy(static function => function.Span.Length)
            .Select(static function => function.Id)
            .FirstOrDefault();

    private static string GetText(LuaWorkspaceModuleResult module, LuaSyntaxToken token) =>
        Encoding.UTF8.GetString(
            module.Compilation.Source.Text.AsSpan().Slice(token.Span.Start, token.Span.Length));

    private sealed class ReferenceEqualityComparer : IEqualityComparer<LuaType>
    {
        public static ReferenceEqualityComparer Instance { get; } = new();

        public bool Equals(LuaType? x, LuaType? y) => ReferenceEquals(x, y);

        public int GetHashCode(LuaType obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    private sealed record RequireAlias(
        LuaModuleDependency Dependency,
        ImmutableArray<int> WriteStarts);
}
