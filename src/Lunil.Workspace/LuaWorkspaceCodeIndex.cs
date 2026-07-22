using System.Collections.Immutable;
using Lunil.Analysis;
using Lunil.Core.Text;
using Lunil.Semantics.Binding;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

namespace Lunil.Workspace;

/// <summary>One workspace-qualified read or write reference.</summary>
public sealed record LuaWorkspaceReference(
    LuaModuleIdentity Module,
    string SourceIdentity,
    TextSpan Span,
    int ContainingFunctionId,
    LuaSymbolKey ContainingFunctionKey,
    string Name,
    bool IsWrite,
    LuaNameResolutionKind ResolutionKind,
    LuaSymbolKey? TargetKey);

/// <summary>One stable workspace-qualified function node.</summary>
public sealed record LuaWorkspaceFunction(
    LuaModuleIdentity Module,
    string SourceIdentity,
    int FunctionId,
    LuaSymbolKey FunctionKey);

/// <summary>One workspace-qualified call edge with optional module/export projection.</summary>
public sealed record LuaWorkspaceCallSite(
    LuaModuleIdentity Module,
    string SourceIdentity,
    LuaCallSite Site,
    LuaSymbolKey ContainingFunctionKey,
    LuaSymbolKey? TargetFunctionKey,
    LuaModuleIdentity? TargetModule,
    string? TargetExportName);

/// <summary>Immutable workspace-wide call graph projection.</summary>
public sealed record LuaWorkspaceCallGraph(
    ImmutableArray<LuaWorkspaceFunction> Functions,
    ImmutableArray<LuaWorkspaceCallSite> Edges);

/// <summary>Builds deterministic reference and call-graph projections from a workspace result.</summary>
public static class LuaWorkspaceCodeIndex
{
    /// <summary>Finds references to a stable symbol key across the workspace snapshot.</summary>
    public static ImmutableArray<LuaWorkspaceReference> FindReferences(
        this LuaWorkspaceResult workspace,
        LuaSymbolKey key)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var result = ImmutableArray.CreateBuilder<LuaWorkspaceReference>();
        foreach (var module in workspace.Modules)
        {
            var model = module.Compilation.SemanticModel;
            var symbol = model.ResolveSymbolKey(key, module.Identity);
            if (symbol is null)
            {
                continue;
            }

            foreach (var reference in model.FindReferences(symbol))
            {
                result.Add(ProjectReference(module, reference, key));
            }
        }

        return result
            .OrderBy(static reference => reference.Module.Name, StringComparer.Ordinal)
            .ThenBy(static reference => reference.Span.Start)
            .ThenBy(static reference => reference.Span.Length)
            .ToImmutableArray();
    }

    /// <summary>Finds all implicit or explicit references to a global name.</summary>
    public static ImmutableArray<LuaWorkspaceReference> FindGlobalReferences(
        this LuaWorkspaceResult workspace,
        string name)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return workspace.Modules
            .SelectMany(module => module.Compilation.SemanticModel
                .FindGlobalReferences(name)
                .Select(reference => ProjectReference(module, reference)))
            .OrderBy(static reference => reference.Module.Name, StringComparer.Ordinal)
            .ThenBy(static reference => reference.Span.Start)
            .ThenBy(static reference => reference.Span.Length)
            .ToImmutableArray();
    }

    /// <summary>Builds a stable workspace-wide call graph from module analysis results.</summary>
    public static LuaWorkspaceCallGraph GetCallGraph(this LuaWorkspaceResult workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        var functions = ImmutableArray.CreateBuilder<LuaWorkspaceFunction>();
        var edges = ImmutableArray.CreateBuilder<LuaWorkspaceCallSite>();
        foreach (var module in workspace.Modules)
        {
            var model = module.Compilation.SemanticModel;
            var functionsById = model.Functions.ToDictionary(static function => function.Id);
            foreach (var function in model.Functions)
            {
                functions.Add(new LuaWorkspaceFunction(
                    module.Identity,
                    module.SourceIdentity,
                    function.Id,
                    model.GetFunctionKey(function, module.Identity)));
            }

            var projection = BuildModuleProjectionIndex(module);
            foreach (var site in module.Compilation.Analysis.CallGraph.Edges)
            {
                var containing = functionsById[site.ContainingFunctionId];
                var target = site.TargetFunctionId is int targetId
                    ? functionsById[targetId]
                    : null;
                var targetModule = ResolveTargetModule(site, projection);
                edges.Add(new LuaWorkspaceCallSite(
                    module.Identity,
                    module.SourceIdentity,
                    site,
                    model.GetFunctionKey(containing, module.Identity),
                    target is not null ? model.GetFunctionKey(target, module.Identity) : null,
                    targetModule,
                    targetModule is not null ? site.MemberTarget?.Name : null));
            }
        }

        return new LuaWorkspaceCallGraph(
            functions
                .OrderBy(static function => function.Module.Name, StringComparer.Ordinal)
                .ThenBy(static function => function.FunctionId)
                .ToImmutableArray(),
            edges
                .OrderBy(static edge => edge.Module.Name, StringComparer.Ordinal)
                .ThenBy(static edge => edge.Site.Span.Start)
                .ThenBy(static edge => edge.Site.Span.Length)
                .ToImmutableArray());
    }

    private static LuaWorkspaceReference ProjectReference(
        LuaWorkspaceModuleResult module,
        LuaNameReference reference,
        LuaSymbolKey? targetOverride = null)
    {
        var model = module.Compilation.SemanticModel;
        var containing = GetContainingFunction(model, reference.Span);
        LuaSymbolKey? targetKey = targetOverride ??
            (reference.Symbol.Kind == LuaSymbolKind.Environment
                ? null
                : model.GetSymbolKey(reference.Symbol, module.Identity));
        return new LuaWorkspaceReference(
            module.Identity,
            module.SourceIdentity,
            reference.Span,
            containing.Id,
            model.GetFunctionKey(containing, module.Identity),
            reference.Name,
            reference.IsWrite,
            reference.ResolutionKind,
            targetKey);
    }

    private static LuaFunctionInfo GetContainingFunction(LuaSemanticModel model, TextSpan span) =>
        model.Functions
            .Where(function => function.Span.Start <= span.Start && function.Span.End >= span.End)
            .OrderBy(static function => function.Span.Length)
            .ThenByDescending(static function => function.Id)
            .FirstOrDefault()
        ?? GetFunction(model, 0);

    private static LuaFunctionInfo GetFunction(LuaSemanticModel model, int functionId) =>
        model.Functions.First(function => function.Id == functionId);

    private static ModuleProjectionIndex BuildModuleProjectionIndex(
        LuaWorkspaceModuleResult module)
    {
        var model = module.Compilation.SemanticModel;
        var requestsByCallSpan = module.Compilation.Analysis.CallGraph.Edges
            .Where(static site => site.ModuleRequest is not null)
            .GroupBy(static site => site.Span)
            .ToImmutableDictionary(
                static group => group.Key,
                static group => group.Last().ModuleRequest!);
        var writtenSymbols = model.References
            .Where(static reference => reference.IsWrite)
            .Select(static reference => reference.Symbol.Id)
            .ToImmutableHashSet();
        var aliases = ImmutableDictionary.CreateBuilder<int, string>();
        foreach (var declaration in model.Syntax.Root.DescendantNodes().Where(static node =>
                     node.Kind == LuaSyntaxKind.LocalDeclarationStatement))
        {
            var names = declaration.ChildNodes()
                .Where(static node => node.Kind == LuaSyntaxKind.AttributedName)
                .ToArray();
            var expressionList = declaration.ChildNodes().FirstOrDefault(static node =>
                node.Kind == LuaSyntaxKind.ExpressionList);
            var values = expressionList?.ChildNodes().ToArray() ?? [];
            for (var index = 0; index < Math.Min(names.Length, values.Length); index++)
            {
                if (!requestsByCallSpan.TryGetValue(values[index].Span, out var request))
                {
                    continue;
                }

                var identifier = names[index].ChildTokens().FirstOrDefault(static token =>
                    token.Kind == LuaTokenKind.Identifier && !token.IsMissing);
                var symbol = identifier is not null
                    ? model.Symbols.FirstOrDefault(candidate => candidate.DeclaringSpan == identifier.Span)
                    : null;
                if (symbol is not null && !writtenSymbols.Contains(symbol.Id))
                {
                    aliases[symbol.Id] = request;
                }
            }
        }

        var nodesBySpan = model.Syntax.Root.DescendantNodes()
            .GroupBy(static node => node.Span)
            .ToImmutableDictionary(
                static group => group.Key,
                static group => group.ToImmutableArray());
        var referencesBySpan = model.References
            .GroupBy(static reference => reference.Span)
            .ToImmutableDictionary(
                static group => group.Key,
                static group => group.Last());
        var targetsByRequest = module.Dependencies
            .Where(static dependency => dependency.Kind == LuaModuleDependencyKind.Static)
            .GroupBy(static dependency => dependency.RequestedName, StringComparer.Ordinal)
            .ToImmutableDictionary(
                static group => group.Key,
                static group =>
                {
                    var targets = group
                        .Where(static dependency => dependency.Target is not null)
                        .Select(static dependency => dependency.Target!)
                        .Distinct()
                        .ToArray();
                    return targets.Length == 1 ? targets[0] : null;
                },
                StringComparer.Ordinal);
        return new ModuleProjectionIndex(
            requestsByCallSpan,
            aliases.ToImmutable(),
            nodesBySpan,
            referencesBySpan,
            targetsByRequest);
    }

    private static LuaModuleIdentity? ResolveTargetModule(
        LuaCallSite site,
        ModuleProjectionIndex projection)
    {
        var request = site.ModuleRequest;
        if (request is null && site.MemberTarget is not null)
        {
            request = ResolveReceiverModuleRequest(
                site.MemberTarget.ReceiverSpan,
                projection);
        }

        if (request is null)
        {
            return null;
        }

        return projection.TargetsByRequest.GetValueOrDefault(request);
    }

    private static string? ResolveReceiverModuleRequest(
        TextSpan receiverSpan,
        ModuleProjectionIndex projection)
    {
        var receiver = projection.NodesBySpan.TryGetValue(receiverSpan, out var candidates)
            ? candidates.FirstOrDefault(static node => node.Kind is
                LuaSyntaxKind.IdentifierExpression or
                LuaSyntaxKind.CallExpression or
                LuaSyntaxKind.MethodCallExpression or
                LuaSyntaxKind.ParenthesizedExpression)
            : null;
        if (receiver is null)
        {
            return null;
        }

        while (receiver.Kind == LuaSyntaxKind.ParenthesizedExpression)
        {
            receiver = receiver.ChildNodes().FirstOrDefault();
            if (receiver is null)
            {
                return null;
            }
        }

        if (receiver.Kind is LuaSyntaxKind.CallExpression or LuaSyntaxKind.MethodCallExpression)
        {
            return projection.RequestsByCallSpan.GetValueOrDefault(receiver.Span);
        }

        if (receiver.Kind != LuaSyntaxKind.IdentifierExpression)
        {
            return null;
        }

        var token = receiver.ChildTokens().FirstOrDefault(static candidate =>
            candidate.Kind == LuaTokenKind.Identifier && !candidate.IsMissing);
        var reference = token is not null &&
            projection.ReferencesBySpan.TryGetValue(token.Span, out var resolved)
                ? resolved
                : null;
        return reference is not null &&
            projection.Aliases.TryGetValue(reference.Symbol.Id, out var request)
            ? request
            : null;
    }

    private sealed record ModuleProjectionIndex(
        ImmutableDictionary<TextSpan, string> RequestsByCallSpan,
        ImmutableDictionary<int, string> Aliases,
        ImmutableDictionary<TextSpan, ImmutableArray<LuaSyntaxNode>> NodesBySpan,
        ImmutableDictionary<TextSpan, LuaNameReference> ReferencesBySpan,
        ImmutableDictionary<string, LuaModuleIdentity?> TargetsByRequest);
}
