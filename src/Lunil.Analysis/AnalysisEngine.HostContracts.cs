using System.Collections.Immutable;
using Lunil.Semantics.Binding;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

namespace Lunil.Analysis;

internal sealed partial class AnalysisEngine
{
    private bool InferHostCall(
        LuaSyntaxNode expression,
        FlowState state,
        out LuaTypePack result)
    {
        result = LuaTypePack.Empty;
        if (_environment.HostContract is not { } contract ||
            !TryGetStaticCallPath(expression, out var path) ||
            !contract.Functions.TryGetValue(path, out var function))
        {
            return false;
        }

        var arguments = GetCallArguments(expression).ToArray();
        var argumentTypes = arguments.Select(argument => InferExpression(argument, state)).ToImmutableArray();
        var signatures = new[]
            {
                CreateHostSignature(
                    function.Parameters,
                    function.Returns,
                    function.HasVariadicParameters,
                    function.HasVariadicReturns),
            }
            .Concat(function.Overloads.Select(overload => CreateHostSignature(
                overload.Parameters,
                overload.Returns,
                overload.HasVariadicParameters,
                overload.HasVariadicReturns)))
            .ToArray();
        var signature = signatures.FirstOrDefault(candidate =>
            IsCallCompatible(candidate, argumentTypes)) ?? signatures[0];
        CheckCall(signature, argumentTypes, expression.Span);
        _hostEffects.Add(new LuaHostEffectFact(
            path,
            expression.Span,
            function.Effects,
            function.Source));

        if (function.Callback is { } callback &&
            callback.ParameterIndex >= 0 && callback.ParameterIndex < arguments.Length)
        {
            var callbackExpression = arguments[callback.ParameterIndex];
            var functionId = GetCallbackFunctionId(callbackExpression);
            var escapes = callback.Retention == LuaHostCallbackRetentionKind.Stored ||
                callback.Invocation != LuaHostCallbackInvocationKind.Synchronous;
            _callbackRegistrations.Add(new LuaCallbackRegistrationFact(
                path,
                expression.Span,
                callbackExpression.Span,
                functionId,
                callback.Invocation,
                callback.Cardinality,
                callback.Retention,
                callback.UnsubscribeFunction,
                escapes));
            if (escapes && functionId is { } escapingFunction)
            {
                var info = _semantics.Functions.FirstOrDefault(candidate =>
                    candidate.Id == escapingFunction);
                if (info is not null)
                {
                    foreach (var capture in info.Captures)
                    {
                        if (!_upvalueCells.TryGetValue(capture.Id, out var cell))
                        {
                            cell = new UpvalueCellState(
                                capture,
                                _symbolInferences.GetValueOrDefault(capture.Id, LuaTypes.Any));
                            _upvalueCells.Add(capture.Id, cell);
                        }

                        cell.Escapes = true;
                    }
                }
            }
        }

        if (function.Persistence is { } persistence)
        {
            string? key = null;
            var keyIndex = persistence.KeyParameterIndex ?? -1;
            var dynamicKey = persistence.KeyParameterIndex.HasValue;
            if (keyIndex >= 0 && keyIndex < arguments.Length &&
                arguments[keyIndex].TryGetConstantString(out var constantKey))
            {
                key = constantKey;
                dynamicKey = false;
            }

            _persistenceAccesses.Add(new LuaPersistenceAccessFact(
                path,
                expression.Span,
                persistence.Operation,
                key,
                dynamicKey,
                persistence.SchemaId,
                persistence.SchemaVersion,
                LuaHostAnalysisContract.ToLuaType(persistence.ValueType),
                persistence.MissingReturnsNil,
                persistence.MigrationFunction));
        }

        result = signature.Returns;
        if (function.Persistence is
            { Operation: LuaPersistenceOperationKind.Read, MissingReturnsNil: true } &&
            !result.Head.IsEmpty)
        {
            result = result with
            {
                Head = result.Head.SetItem(0, _relations.Union(result.Head[0], LuaTypes.Nil)),
            };
        }

        return true;
    }

    private static LuaFunctionType CreateHostSignature(
        ImmutableArray<LuaHostParameterContract> parameters,
        ImmutableArray<LuaHostTypeDescriptor> returns,
        bool hasVariadicParameters,
        bool hasVariadicReturns) => new(
        [.. parameters.Select((parameter, index) => new LuaFunctionParameter(
            parameter.Name,
            LuaHostAnalysisContract.ToLuaType(parameter.Type),
            parameter.IsOptional,
            hasVariadicParameters && index == parameters.Length - 1))],
        new LuaTypePack(
            [.. returns.Select(LuaHostAnalysisContract.ToLuaType)],
            hasVariadicReturns ? LuaTypes.Any : null),
        []);

    private bool TryGetStaticCallPath(LuaSyntaxNode expression, out string path)
    {
        if (expression.Kind == LuaSyntaxKind.MethodCallExpression)
        {
            var receiver = expression.ChildNodes().FirstOrDefault(static node =>
                node.Kind != LuaSyntaxKind.ArgumentList);
            var member = expression.ChildTokens().LastOrDefault(static token =>
                token.Kind == LuaTokenKind.Identifier && !token.IsMissing);
            if (receiver is not null && member is not null &&
                TryGetStaticValuePath(receiver, out var receiverPath))
            {
                path = receiverPath + "." + GetTokenText(member);
                return true;
            }
        }
        else if (expression.Kind == LuaSyntaxKind.CallExpression)
        {
            var callee = expression.ChildNodes().FirstOrDefault(static node =>
                node.Kind != LuaSyntaxKind.ArgumentList);
            if (callee is not null && TryGetStaticValuePath(callee, out path))
            {
                return true;
            }
        }

        path = string.Empty;
        return false;
    }

    private bool TryGetStaticValuePath(LuaSyntaxNode expression, out string path)
    {
        while (expression.Kind == LuaSyntaxKind.ParenthesizedExpression)
        {
            expression = expression.ChildNodes().Single();
        }

        if (expression.Kind == LuaSyntaxKind.IdentifierExpression)
        {
            var token = expression.ChildTokens().FirstOrDefault(static candidate =>
                candidate.Kind == LuaTokenKind.Identifier && !candidate.IsMissing);
            if (token is not null &&
                _references.TryGetValue(token.Span, out var reference) &&
                reference.ResolutionKind == LuaNameResolutionKind.Global)
            {
                path = reference.Name;
                return true;
            }
        }
        else if (expression.Kind == LuaSyntaxKind.MemberAccessExpression)
        {
            var receiver = expression.ChildNodes().FirstOrDefault();
            var member = expression.ChildTokens().LastOrDefault(static token =>
                token.Kind == LuaTokenKind.Identifier && !token.IsMissing);
            if (receiver is not null && member is not null &&
                TryGetStaticValuePath(receiver, out var receiverPath))
            {
                path = receiverPath + "." + GetTokenText(member);
                return true;
            }
        }
        else if (expression.Kind == LuaSyntaxKind.IndexExpression)
        {
            var nodes = expression.ChildNodes().ToArray();
            if (nodes.Length > 1 && nodes[1].TryGetConstantString(out var member) &&
                TryGetStaticValuePath(nodes[0], out var receiverPath))
            {
                path = receiverPath + "." + member;
                return true;
            }
        }

        path = string.Empty;
        return false;
    }

    private int? GetCallbackFunctionId(LuaSyntaxNode expression)
    {
        if (expression.Kind == LuaSyntaxKind.FunctionExpression &&
            _functionIdsByOwnerSpan.TryGetValue(expression.Span, out var expressionFunction))
        {
            return expressionFunction;
        }

        if (expression.Kind == LuaSyntaxKind.IdentifierExpression)
        {
            var token = expression.ChildTokens().FirstOrDefault(static candidate =>
                candidate.Kind == LuaTokenKind.Identifier && !candidate.IsMissing);
            if (token is not null && _references.TryGetValue(token.Span, out var reference))
            {
                if (reference.ResolutionKind == LuaNameResolutionKind.Global)
                {
                    return _functionIdsByGlobalName.GetValueOrDefault(reference.Name, -1) is var global &&
                        global >= 0 ? global : null;
                }

                return _functionIdsByDeclarationSymbol.GetValueOrDefault(reference.Symbol.Id, -1) is var local &&
                    local >= 0 ? local : null;
            }
        }

        return null;
    }
}
