namespace Lunil.Syntax.Parsing;

/// <summary>Dispatches a lossless syntax node to typed facade methods.</summary>
/// <typeparam name="TResult">The result produced by a visit.</typeparam>
public abstract class LuaSyntaxVisitor<TResult>
{
    /// <summary>Visits a node, returning the default result for <see langword="null"/>.</summary>
    public virtual TResult? Visit(LuaSyntaxNode? node)
    {
        if (node is null)
        {
            return default;
        }

        if (node.TryGetCallExpression(out var call))
        {
            return VisitCallExpression(call);
        }

        if (node.TryGetFunctionDeclaration(out var function))
        {
            return VisitFunctionDeclaration(function);
        }

        if (node.TryGetMemberAccessExpression(out var member))
        {
            return VisitMemberAccessExpression(member);
        }

        if (node.TryGetExpression(out var expression))
        {
            return VisitExpression(expression);
        }

        return DefaultVisit(node);
    }

    /// <summary>Visits a normal call or colon method call.</summary>
    public virtual TResult? VisitCallExpression(LuaCallExpressionSyntax node) =>
        VisitExpression(node);

    /// <summary>Visits a named, local, global, or anonymous function.</summary>
    public virtual TResult? VisitFunctionDeclaration(LuaFunctionDeclarationSyntax node) =>
        DefaultVisit(node.Node);

    /// <summary>Visits dot member access.</summary>
    public virtual TResult? VisitMemberAccessExpression(LuaMemberAccessExpressionSyntax node) =>
        VisitExpression(node);

    /// <summary>Visits an expression without a more specific override.</summary>
    public virtual TResult? VisitExpression(LuaExpressionSyntax node) =>
        DefaultVisit(node.Node);

    /// <summary>Visits a node without a typed facade.</summary>
    public virtual TResult? DefaultVisit(LuaSyntaxNode node) => default;
}

/// <summary>
/// Walks lossless syntax nodes in source order and exposes typed callbacks for common embedded
/// analysis scenarios. Override a callback and call its base implementation to continue walking.
/// </summary>
public abstract class LuaSyntaxWalker
{
    /// <summary>Visits a node and recursively walks its children by default.</summary>
    public virtual void Visit(LuaSyntaxNode? node)
    {
        if (node is null)
        {
            return;
        }

        if (node.TryGetCallExpression(out var call))
        {
            VisitCallExpression(call);
        }
        else if (node.TryGetFunctionDeclaration(out var function))
        {
            VisitFunctionDeclaration(function);
        }
        else if (node.TryGetMemberAccessExpression(out var member))
        {
            VisitMemberAccessExpression(member);
        }
        else if (node.TryGetExpression(out var expression))
        {
            VisitExpression(expression);
        }
        else
        {
            DefaultVisit(node);
        }
    }

    /// <summary>Visits a normal call or colon method call.</summary>
    public virtual void VisitCallExpression(LuaCallExpressionSyntax node) =>
        DefaultVisit(node.Node);

    /// <summary>Visits a named, local, global, or anonymous function.</summary>
    public virtual void VisitFunctionDeclaration(LuaFunctionDeclarationSyntax node) =>
        DefaultVisit(node.Node);

    /// <summary>Visits dot member access.</summary>
    public virtual void VisitMemberAccessExpression(LuaMemberAccessExpressionSyntax node) =>
        DefaultVisit(node.Node);

    /// <summary>Visits an expression without a more specific override.</summary>
    public virtual void VisitExpression(LuaExpressionSyntax node) =>
        DefaultVisit(node.Node);

    /// <summary>Visits a node without a typed facade by recursively walking its child nodes.</summary>
    public virtual void DefaultVisit(LuaSyntaxNode node)
    {
        foreach (var child in node.ChildNodes())
        {
            Visit(child);
        }
    }
}
