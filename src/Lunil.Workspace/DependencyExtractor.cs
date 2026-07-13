using System.Collections.Immutable;
using System.Text;
using Lunil.Compiler;
using Lunil.Semantics.Binding;
using Lunil.Syntax.Lexing;
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
        foreach (var call in compilation.Syntax.Root.DescendantNodes().Prepend(compilation.Syntax.Root)
                     .Where(static node => node.Kind == LuaSyntaxKind.CallExpression))
        {
            var callee = call.ChildNodes().FirstOrDefault();
            if (callee?.Kind != LuaSyntaxKind.IdentifierExpression)
            {
                continue;
            }

            var token = callee.ChildTokens().SingleOrDefault(static item =>
                item.Kind == LuaTokenKind.Identifier && !item.IsMissing);
            if (token is null || !references.TryGetValue(token.Span, out var reference) ||
                reference.ResolutionKind != LuaNameResolutionKind.Global ||
                !string.Equals(reference.Name, "require", StringComparison.Ordinal))
            {
                continue;
            }

            var argument = GetCallArguments(call).FirstOrDefault();
            if (argument is not null && TryGetStringLiteral(argument, out var requestedName))
            {
                dependencies.Add(new DiscoveredDependency(
                    requestedName,
                    LuaModuleDependencyKind.Static,
                    call.Span));
            }
            else
            {
                dependencies.Add(new DiscoveredDependency(
                    "<dynamic>",
                    LuaModuleDependencyKind.Dynamic,
                    call.Span));
            }
        }

        return dependencies
            .OrderBy(static dependency => dependency.Span.Start)
            .ThenBy(static dependency => dependency.RequestedName, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static IEnumerable<LuaSyntaxNode> GetCallArguments(LuaSyntaxNode expression)
    {
        var argumentList = expression.ChildNodes().FirstOrDefault(static node =>
            node.Kind == LuaSyntaxKind.ArgumentList);
        if (argumentList is null)
        {
            return [];
        }

        var expressionList = argumentList.ChildNodes().FirstOrDefault(static node =>
            node.Kind == LuaSyntaxKind.ExpressionList);
        return expressionList is not null
            ? expressionList.ChildNodes()
            : argumentList.ChildNodes();
    }

    private static bool TryGetStringLiteral(LuaSyntaxNode expression, out string value)
    {
        value = string.Empty;
        if (expression.Kind != LuaSyntaxKind.StringLiteralExpression)
        {
            return false;
        }

        var token = expression.ChildTokens().SingleOrDefault();
        if (token?.Value is not LuaStringTokenValue text)
        {
            return false;
        }

        value = Encoding.UTF8.GetString(text.Bytes.AsSpan());
        return !string.IsNullOrWhiteSpace(value);
    }
}

internal sealed record DiscoveredDependency(
    string RequestedName,
    LuaModuleDependencyKind Kind,
    Lunil.Core.Text.TextSpan Span);
