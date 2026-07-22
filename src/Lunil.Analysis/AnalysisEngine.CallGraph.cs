using System.Collections.Immutable;
using Lunil.Core.Text;
using Lunil.Semantics.Binding;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

namespace Lunil.Analysis;

internal sealed partial class AnalysisEngine
{
    private readonly Dictionary<CallSiteKey, LuaCallSite> _callSites = [];
    private readonly Dictionary<int, int> _functionIdsByDeclarationSymbol = [];
    private readonly Dictionary<string, int> _functionIdsByGlobalName =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _ambiguousGlobalFunctionNames = new(StringComparer.Ordinal);

    private LuaCallGraph BuildCallGraph() => new(
        _semantics.Functions
            .Select(static function => function.Id)
            .Order()
            .ToImmutableArray(),
        _callSites.Values
            .OrderBy(static site => site.Span.Start)
            .ThenBy(static site => site.Span.Length)
            .ThenBy(static site => site.ContainingFunctionId)
            .ToImmutableArray());

    private void BuildFunctionTargetIndex()
    {
        foreach (var pair in _functionSyntax.Where(static pair => pair.Key != 0))
        {
            var functionId = pair.Key;
            var owner = pair.Value.Owner;
            var identifier = owner.DescendantTokens().FirstOrDefault(static token =>
                token.Kind == LuaTokenKind.Identifier && !token.IsMissing);
            if (identifier is null)
            {
                continue;
            }

            if (_declarations.TryGetValue(identifier.Span, out var symbol))
            {
                _functionIdsByDeclarationSymbol[symbol.Id] = functionId;
            }

            if (owner.Kind is LuaSyntaxKind.FunctionDeclarationStatement or
                LuaSyntaxKind.GlobalDeclarationStatement)
            {
                var globalName = GetFunctionName(owner);
                if (globalName is not null &&
                    !globalName.Contains('.', StringComparison.Ordinal) &&
                    !globalName.Contains(':', StringComparison.Ordinal))
                {
                    AddGlobalFunctionTarget(globalName, functionId);
                }
            }
        }

        foreach (var declaration in _semantics.Syntax.Root.DescendantNodes().Where(static node =>
                     node.Kind == LuaSyntaxKind.LocalDeclarationStatement))
        {
            var names = declaration.ChildNodes()
                .Where(static node => node.Kind == LuaSyntaxKind.AttributedName)
                .ToArray();
            var expressionList = declaration.ChildNodes().FirstOrDefault(static node =>
                node.Kind == LuaSyntaxKind.ExpressionList);
            var values = expressionList?.ChildNodes().ToArray() ?? [];
            if (names.Length != 1 || values.Length != 1 ||
                values[0].Kind != LuaSyntaxKind.FunctionExpression ||
                !_functionIdsByOwnerSpan.TryGetValue(values[0].Span, out var functionId))
            {
                continue;
            }

            var identifier = names[0].ChildTokens().FirstOrDefault(static token =>
                token.Kind == LuaTokenKind.Identifier && !token.IsMissing);
            if (identifier is not null && _declarations.TryGetValue(identifier.Span, out var symbol))
            {
                _functionIdsByDeclarationSymbol[symbol.Id] = functionId;
            }
        }
    }

    private void AddGlobalFunctionTarget(string name, int functionId)
    {
        if (_ambiguousGlobalFunctionNames.Contains(name))
        {
            return;
        }

        if (_functionIdsByGlobalName.TryGetValue(name, out var existing) && existing != functionId)
        {
            _functionIdsByGlobalName.Remove(name);
            _ambiguousGlobalFunctionNames.Add(name);
            return;
        }

        _functionIdsByGlobalName[name] = functionId;
    }

    private void RecordCallSite(
        LuaSyntaxNode expression,
        LuaSyntaxNode? calleeNode,
        LuaType calleeType,
        LuaSyntaxNode? receiver,
        LuaType? receiverType,
        string? memberName,
        LuaCallResolutionStatus resolutionStatus,
        string? unresolvedReason)
    {
        var containingFunctionId = _currentFunction?.FunctionId ?? 0;
        var directReference = expression.Kind == LuaSyntaxKind.MethodCallExpression
            ? null
            : GetDirectReference(calleeNode);
        var directSymbol = directReference?.Symbol.Kind == LuaSymbolKind.Environment
            ? null
            : directReference?.Symbol;
        var targetFunctionId = resolutionStatus == LuaCallResolutionStatus.Resolved
            ? GetTargetFunctionId(directReference)
            : null;
        var kind = GetCallKind(expression, calleeNode, calleeType);
        var calleeSpan = GetCalleeSpan(expression, calleeNode, receiver);
        var memberTarget = receiver is not null && memberName is not null
            ? new LuaMemberTarget(receiver.Span, memberName, receiverType ?? LuaTypes.Unknown)
            : null;
        string? moduleRequest = null;
        if (TryGetCalledGlobalIdentifier(expression, out var calledName) &&
            string.Equals(calledName, "require", StringComparison.Ordinal) &&
            TryGetStaticModuleRequest(expression, out var request))
        {
            moduleRequest = request;
        }

        var site = new LuaCallSite(
            expression.Span,
            containingFunctionId,
            calleeSpan,
            kind,
            directSymbol,
            directReference?.Name,
            calleeType,
            memberTarget,
            moduleRequest,
            targetFunctionId,
            resolutionStatus,
            unresolvedReason);
        var key = new CallSiteKey(containingFunctionId, expression.Span);
        _callSites[key] = _callSites.TryGetValue(key, out var previous)
            ? MergeCallSites(previous, site)
            : site;
    }

    private LuaNameReference? GetDirectReference(LuaSyntaxNode? calleeNode)
    {
        if (calleeNode?.Kind != LuaSyntaxKind.IdentifierExpression)
        {
            return null;
        }

        var token = calleeNode.ChildTokens().FirstOrDefault(static candidate =>
            candidate.Kind == LuaTokenKind.Identifier && !candidate.IsMissing);
        return token is not null && _references.TryGetValue(token.Span, out var reference)
            ? reference
            : null;
    }

    private int? GetTargetFunctionId(LuaNameReference? reference)
    {
        if (reference is null)
        {
            return null;
        }

        if (reference.ResolutionKind != LuaNameResolutionKind.Global &&
            _functionIdsByDeclarationSymbol.TryGetValue(reference.Symbol.Id, out var localTarget))
        {
            return localTarget;
        }

        return reference.ResolutionKind == LuaNameResolutionKind.Global &&
            _functionIdsByGlobalName.TryGetValue(reference.Name, out var globalTarget)
                ? globalTarget
                : null;
    }

    private static LuaCallKind GetCallKind(
        LuaSyntaxNode expression,
        LuaSyntaxNode? calleeNode,
        LuaType calleeType)
    {
        if (expression.Kind == LuaSyntaxKind.MethodCallExpression)
        {
            return LuaCallKind.Method;
        }

        if (calleeNode?.Kind == LuaSyntaxKind.MemberAccessExpression)
        {
            return LuaCallKind.Member;
        }

        if (calleeType.Kind is LuaTypeKind.Callable or LuaTypeKind.Class)
        {
            return LuaCallKind.Callable;
        }

        if (calleeNode?.Kind == LuaSyntaxKind.IdentifierExpression)
        {
            return LuaCallKind.Direct;
        }

        return calleeType.Kind == LuaTypeKind.Overload
            ? LuaCallKind.Callable
            : LuaCallKind.Dynamic;
    }

    private void CompleteCallSiteProjection()
    {
        foreach (var expression in _semantics.Syntax.Root.DescendantNodes().Where(static node =>
                     node.Kind is LuaSyntaxKind.CallExpression or LuaSyntaxKind.MethodCallExpression))
        {
            var functionId = GetContainingFunctionId(expression.Span);
            var key = new CallSiteKey(functionId, expression.Span);
            if (_callSites.ContainsKey(key))
            {
                continue;
            }

            var nodes = expression.ChildNodes().ToArray();
            var receiver = expression.Kind == LuaSyntaxKind.MethodCallExpression
                ? nodes.FirstOrDefault(static node => node.Kind != LuaSyntaxKind.ArgumentList)
                : null;
            var calleeNode = expression.Kind == LuaSyntaxKind.MethodCallExpression
                ? receiver
                : nodes.FirstOrDefault(static node => node.Kind != LuaSyntaxKind.ArgumentList);
            string? memberName = null;
            if (expression.Kind == LuaSyntaxKind.MethodCallExpression ||
                calleeNode?.Kind == LuaSyntaxKind.MemberAccessExpression)
            {
                var member = (expression.Kind == LuaSyntaxKind.MethodCallExpression
                        ? expression
                        : calleeNode)
                    ?.ChildTokens()
                    .LastOrDefault(static token =>
                        token.Kind == LuaTokenKind.Identifier && !token.IsMissing);
                if (member is not null)
                {
                    memberName = GetTokenText(member);
                }

                receiver ??= calleeNode?.ChildNodes().FirstOrDefault();
            }

            var calleeSpan = GetCalleeSpan(expression, calleeNode, receiver);
            var calleeType = _expressionInferences.GetValueOrDefault(calleeSpan, LuaTypes.Unknown);
            var receiverType = receiver is not null
                ? _expressionInferences.GetValueOrDefault(receiver.Span, LuaTypes.Unknown)
                : null;
            var directReference = expression.Kind == LuaSyntaxKind.MethodCallExpression
                ? null
                : GetDirectReference(calleeNode);
            var directSymbol = directReference?.Symbol.Kind == LuaSymbolKind.Environment
                ? null
                : directReference?.Symbol;
            string? moduleRequest = null;
            if (TryGetCalledGlobalIdentifier(expression, out var calledName) &&
                string.Equals(calledName, "require", StringComparison.Ordinal) &&
                TryGetStaticModuleRequest(expression, out var request))
            {
                moduleRequest = request;
            }

            _callSites.Add(key, new LuaCallSite(
                expression.Span,
                functionId,
                calleeSpan,
                GetCallKind(expression, calleeNode, calleeType),
                directSymbol,
                directReference?.Name,
                calleeType,
                receiver is not null && memberName is not null
                    ? new LuaMemberTarget(receiver.Span, memberName, receiverType ?? LuaTypes.Unknown)
                    : null,
                moduleRequest,
                TargetFunctionId: null,
                LuaCallResolutionStatus.Dynamic,
                LuaCallUnresolvedReasons.CallWasNotAnalyzed));
        }
    }

    private int GetContainingFunctionId(TextSpan span) =>
        _semantics.Functions
            .Where(function => function.Span.Start <= span.Start && function.Span.End >= span.End)
            .OrderBy(static function => function.Span.Length)
            .ThenByDescending(static function => function.Id)
            .Select(static function => function.Id)
            .FirstOrDefault();

    private static TextSpan GetCalleeSpan(
        LuaSyntaxNode expression,
        LuaSyntaxNode? calleeNode,
        LuaSyntaxNode? receiver)
    {
        if (expression.Kind != LuaSyntaxKind.MethodCallExpression)
        {
            return calleeNode?.Span ?? new TextSpan(expression.Span.Start, 0);
        }

        var member = expression.ChildTokens().LastOrDefault(static token =>
            token.Kind == LuaTokenKind.Identifier && !token.IsMissing);
        return receiver is not null && member is not null
            ? TextSpan.FromBounds(receiver.Span.Start, member.Span.End)
            : new TextSpan(expression.Span.Start, 0);
    }

    private static bool TryGetStaticModuleRequest(LuaSyntaxNode expression, out string request)
    {
        var argument = GetCallArguments(expression).FirstOrDefault();
        if (argument is not null && argument.TryGetConstantString(out request))
        {
            return true;
        }

        request = string.Empty;
        return false;
    }

    private LuaCallSite MergeCallSites(LuaCallSite previous, LuaCallSite current)
    {
        var status = (LuaCallResolutionStatus)Math.Max(
            (int)previous.ResolutionStatus,
            (int)current.ResolutionStatus);
        var memberTarget = previous.MemberTarget is not null && current.MemberTarget is not null
            ? previous.MemberTarget with
            {
                ReceiverType = _relations.Union(
                    previous.MemberTarget.ReceiverType,
                    current.MemberTarget.ReceiverType),
            }
            : previous.MemberTarget ?? current.MemberTarget;
        return previous with
        {
            Kind = previous.Kind == current.Kind ? previous.Kind : LuaCallKind.Dynamic,
            DirectSymbol = ReferenceEquals(previous.DirectSymbol, current.DirectSymbol)
                ? previous.DirectSymbol
                : null,
            DirectName = string.Equals(previous.DirectName, current.DirectName, StringComparison.Ordinal)
                ? previous.DirectName
                : null,
            CalleeType = _relations.Union(previous.CalleeType, current.CalleeType),
            MemberTarget = memberTarget,
            ModuleRequest = string.Equals(
                previous.ModuleRequest,
                current.ModuleRequest,
                StringComparison.Ordinal)
                    ? previous.ModuleRequest
                    : null,
            TargetFunctionId = previous.TargetFunctionId == current.TargetFunctionId
                ? previous.TargetFunctionId
                : null,
            ResolutionStatus = status,
            UnresolvedReason = status == current.ResolutionStatus
                ? current.UnresolvedReason
                : previous.UnresolvedReason,
        };
    }

    private readonly record struct CallSiteKey(int FunctionId, TextSpan Span);
}
