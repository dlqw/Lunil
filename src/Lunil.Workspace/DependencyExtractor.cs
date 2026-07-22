using System.Collections.Immutable;
using Lunil.Compiler;
using Lunil.Semantics.Binding;
using Lunil.Syntax.Parsing;

namespace Lunil.Workspace;

internal static class DependencyExtractor
{
    public static ImmutableArray<DiscoveredDependency> Extract(LuaCompilationResult compilation)
    {
        var references = compilation.SemanticModel.References
            .GroupBy(static reference => reference.Span)
            .ToDictionary(static group => group.Key, static group => group.Last());
        var dependencies = ImmutableArray.CreateBuilder<DiscoveredDependency>();
        new RequireCallWalker(references, dependencies).Visit(compilation.Syntax.Root);

        return dependencies
            .OrderBy(static dependency => dependency.Span.Start)
            .ThenBy(static dependency => dependency.RequestedName, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private sealed class RequireCallWalker(
        IReadOnlyDictionary<Lunil.Core.Text.TextSpan, LuaNameReference> references,
        ImmutableArray<DiscoveredDependency>.Builder dependencies) : LuaSyntaxWalker
    {
        public override void VisitCallExpression(LuaCallExpressionSyntax node)
        {
            if (!node.IsMethodCall &&
                node.Callee?.TryGetIdentifierToken(out var token) == true &&
                references.TryGetValue(token.Span, out var reference) &&
                reference.ResolutionKind == LuaNameResolutionKind.Global &&
                string.Equals(reference.Name, "require", StringComparison.Ordinal))
            {
                var argument = node.Arguments.FirstOrDefault();
                if (argument?.TryGetConstantString(out var requestedName) == true &&
                    !string.IsNullOrWhiteSpace(requestedName))
                {
                    dependencies.Add(new DiscoveredDependency(
                        requestedName,
                        LuaModuleDependencyKind.Static,
                        node.Span));
                }
                else
                {
                    dependencies.Add(new DiscoveredDependency(
                        "<dynamic>",
                        LuaModuleDependencyKind.Dynamic,
                        node.Span));
                }
            }

            base.VisitCallExpression(node);
        }
    }
}

internal sealed record DiscoveredDependency(
    string RequestedName,
    LuaModuleDependencyKind Kind,
    Lunil.Core.Text.TextSpan Span);
