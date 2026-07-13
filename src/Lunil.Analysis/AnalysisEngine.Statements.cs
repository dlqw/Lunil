using System.Collections.Immutable;
using System.Text;
using Lunil.Core.Text;
using Lunil.EmmyLua;
using Lunil.Semantics.Binding;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

namespace Lunil.Analysis;

internal sealed partial class AnalysisEngine
{
    private FunctionSpecification BuildFunctionSpecification(
        LuaFunctionInfo function,
        FunctionSyntax syntax,
        ImmutableArray<LuaAnnotationSyntax> annotations)
    {
        var genericAnnotation = annotations.OfType<LuaGenericAnnotationSyntax>().LastOrDefault();
        var typeParameters = genericAnnotation?.Parameters.Select((item, index) =>
        {
            var constraint = item.Constraint is null
                ? null
                : _types.Resolve(item.Constraint);
            return new LuaGenericParameterType(item.Name, index, constraint);
        }).ToImmutableArray() ?? [];
        var parameterMap = typeParameters.ToDictionary(
            static item => item.Name,
            static item => (LuaType)item,
            StringComparer.Ordinal);
        var annotatedParameters = annotations.OfType<LuaParamAnnotationSyntax>()
            .GroupBy(static item => item.Name, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.Ordinal);
        var symbols = function.Symbols
            .Where(static symbol => symbol.Kind == LuaSymbolKind.Parameter)
            .OrderBy(static symbol => symbol.DeclaringSpan.Start)
            .ThenBy(static symbol => symbol.Id)
            .ToArray();
        var parameters = ImmutableArray.CreateBuilder<LuaFunctionParameter>();
        foreach (var symbol in symbols)
        {
            if (annotatedParameters.TryGetValue(symbol.Name, out var annotation))
            {
                parameters.Add(new LuaFunctionParameter(
                    symbol.Name,
                    _types.Resolve(annotation.Type, parameterMap),
                    annotation.IsOptional));
            }
            else
            {
                parameters.Add(new LuaFunctionParameter(symbol.Name, LuaTypes.Any));
                if (_context.Options.ReportImplicitAny && symbol.Name != "self")
                {
                    _context.AddDiagnostic(
                        "LUA6014",
                        symbol.DeclaringSpan,
                        $"Parameter '{symbol.Name}' has implicit type any.");
                }
            }
        }

        var vararg = annotations.OfType<LuaVarargAnnotationSyntax>().LastOrDefault();
        if (function.IsVarArg)
        {
            parameters.Add(new LuaFunctionParameter(
                "...",
                vararg is null ? LuaTypes.Any : _types.Resolve(vararg.Type, parameterMap),
                IsOptional: true,
                IsVararg: true));
        }

        var returnAnnotation = annotations.OfType<LuaReturnAnnotationSyntax>().LastOrDefault();
        LuaTypePack? expectedReturns = null;
        if (returnAnnotation is not null)
        {
            var head = returnAnnotation.Returns
                .Take(_context.Options.MaximumReturnPackLength)
                .Select(item => _types.Resolve(item.Type, parameterMap))
                .ToImmutableArray();
            expectedReturns = new LuaTypePack(head);
        }

        var primary = new LuaFunctionType(
            parameters.ToImmutable(),
            expectedReturns ?? LuaTypePack.Empty,
            typeParameters,
            syntax.HasImplicitSelf);
        var overloads = annotations.OfType<LuaOverloadAnnotationSyntax>()
            .Select(item => _types.Resolve(item.Type, parameterMap))
            .OfType<LuaFunctionType>()
            .ToImmutableArray();
        var valueType = overloads.IsEmpty
            ? (LuaType)primary
            : new LuaOverloadType([primary, .. overloads]);
        return new FunctionSpecification(
            primary,
            overloads,
            valueType,
            expectedReturns,
            returnAnnotation is not null);
    }

    private BlockResult AnalyzeLocalDeclaration(LuaSyntaxNode statement, FlowState state)
    {
        var names = statement.ChildNodes()
            .Where(static node => node.Kind == LuaSyntaxKind.AttributedName)
            .ToArray();
        var annotations = GetAnnotations(statement);
        var declaredAnnotations = annotations.OfType<LuaTypeAnnotationSyntax>()
            .LastOrDefault()?.Types ?? [];
        var expressionList = statement.ChildNodes().FirstOrDefault(static node =>
            node.Kind == LuaSyntaxKind.ExpressionList);
        var values = expressionList is null
            ? LuaTypePack.Empty
            : InferExpressionList(expressionList, state, annotations);
        for (var index = 0; index < names.Length; index++)
        {
            var token = names[index].ChildTokens().FirstOrDefault(static item =>
                item.Kind == LuaTokenKind.Identifier && !item.IsMissing);
            if (token is null || !_declarations.TryGetValue(token.Span, out var symbol))
            {
                continue;
            }

            var key = VariableKey.Local(symbol.Id);
            var value = expressionList is null ? LuaTypes.Nil : values.GetElementOrNil(index);
            var declared = index < declaredAnnotations.Length
                ? _types.Resolve(declaredAnnotations[index])
                : LuaTypes.Any;
            if (index < declaredAnnotations.Length)
            {
                _declaredTypes[key] = declared;
                CheckAssignable(value, declared, token.Span, $"initializer for '{symbol.Name}'");
            }
            else
            {
                _declaredTypes.TryAdd(key, LuaTypes.Any);
            }

            var flowValue = index < declaredAnnotations.Length &&
                value.Kind is LuaTypeKind.Any or LuaTypeKind.Unknown
                    ? declared
                    : value;
            state.Types[key] = flowValue;
            if (expressionList is not null)
            {
                state.Assigned.Add(key);
            }
            else
            {
                state.Assigned.Remove(key);
            }

            RecordSymbolInference(symbol, flowValue);
        }

        return BlockResult.Next(state);
    }

    private BlockResult AnalyzeFunctionDeclaration(
        LuaSyntaxNode statement,
        FlowState state,
        bool local)
    {
        if (!_functionIdsByOwnerSpan.TryGetValue(statement.Span, out var functionId))
        {
            return BlockResult.Next(state);
        }

        var type = AnalyzeFunction(functionId, GetAnnotations(statement));
        var identifier = statement.DescendantTokens().FirstOrDefault(static token =>
            token.Kind == LuaTokenKind.Identifier && !token.IsMissing);
        if (identifier is null)
        {
            return BlockResult.Next(state);
        }

        if (local && _declarations.TryGetValue(identifier.Span, out var symbol))
        {
            AssignVariable(VariableKey.Local(symbol.Id), symbol, type, identifier.Span, state);
        }
        else if (_references.TryGetValue(identifier.Span, out var reference))
        {
            var key = reference.ResolutionKind == LuaNameResolutionKind.Global
                ? VariableKey.Global(reference.Name)
                : VariableKey.Local(reference.Symbol.Id);
            AssignVariable(key, reference.Symbol, type, identifier.Span, state);
            var path = GetFunctionName(statement);
            if (path is not null)
            {
                state.Types[VariableKey.Global(path)] = type;
                state.Assigned.Add(VariableKey.Global(path));
                _globalTypes[path] = type;
            }
        }

        return BlockResult.Next(state);
    }

    private BlockResult AnalyzeAssignment(LuaSyntaxNode statement, FlowState state)
    {
        var variableList = statement.ChildNodes().Single(static node =>
            node.Kind == LuaSyntaxKind.VariableList);
        var expressionList = statement.ChildNodes().Single(static node =>
            node.Kind == LuaSyntaxKind.ExpressionList);
        var variables = variableList.ChildNodes().ToArray();
        foreach (var variable in variables)
        {
            PrepareAssignmentTarget(variable, state);
        }

        var values = InferExpressionList(expressionList, state, GetAnnotations(statement));
        for (var index = 0; index < variables.Length; index++)
        {
            AssignTarget(variables[index], values.GetElementOrNil(index), state);
        }

        return BlockResult.Next(state);
    }

    private BlockResult AnalyzeCallStatement(LuaSyntaxNode statement, FlowState state)
    {
        var call = statement.ChildNodes().Single();
        var result = InferExpressionPack(call, state);
        if (TryGetCalledGlobalIdentifier(call, out var name) &&
            string.Equals(name, "assert", StringComparison.Ordinal))
        {
            var argument = GetCallArguments(call).FirstOrDefault();
            if (argument is not null)
            {
                var narrowed = NarrowCondition(argument, state);
                CopyState(narrowed.TrueState, state);
            }
        }

        if (result.GetElementOrNil(0).Kind == LuaTypeKind.Never)
        {
            state.Reachable = false;
        }

        return BlockResult.Next(state);
    }

    private BlockResult AnalyzeReturn(LuaSyntaxNode statement, FlowState state)
    {
        var expressionList = statement.ChildNodes().FirstOrDefault(static node =>
            node.Kind == LuaSyntaxKind.ExpressionList);
        var returns = expressionList is null
            ? LuaTypePack.Empty
            : InferExpressionList(expressionList, state, GetAnnotations(statement));
        _currentFunction!.Returns.Add(returns);
        if (_currentFunction.ExpectedReturns is not null)
        {
            CheckPackAssignable(
                returns,
                _currentFunction.ExpectedReturns,
                statement.Span,
                "function return");
        }

        var unreachable = state.Clone();
        unreachable.Reachable = false;
        return BlockResult.Next(unreachable);
    }

    private BlockResult AnalyzeIf(
        LuaSyntaxNode statement,
        FlowState state,
        bool insideLoop)
    {
        var nodes = statement.ChildNodes().ToArray();
        var condition = nodes.First(static node => node.Kind != LuaSyntaxKind.Block &&
            node.Kind is not LuaSyntaxKind.ElseIfClause and not LuaSyntaxKind.ElseClause);
        _ = InferExpression(condition, state);
        var narrowed = NarrowCondition(condition, state);
        var outputs = new List<FlowState>();
        var breaks = new List<FlowState>();
        var thenBody = nodes.First(static node => node.Kind == LuaSyntaxKind.Block);
        var thenResult = AnalyzeBlock(thenBody, narrowed.TrueState, insideLoop);
        outputs.Add(thenResult.Fallthrough);
        breaks.AddRange(thenResult.Breaks);
        var falseState = narrowed.FalseState;

        foreach (var clause in nodes.Where(static node => node.Kind == LuaSyntaxKind.ElseIfClause))
        {
            var clauseCondition = clause.ChildNodes().First(static node =>
                node.Kind != LuaSyntaxKind.Block);
            _ = InferExpression(clauseCondition, falseState);
            var clauseNarrowed = NarrowCondition(clauseCondition, falseState);
            var clauseBody = clause.ChildNodes().Single(static node => node.Kind == LuaSyntaxKind.Block);
            var clauseResult = AnalyzeBlock(clauseBody, clauseNarrowed.TrueState, insideLoop);
            outputs.Add(clauseResult.Fallthrough);
            breaks.AddRange(clauseResult.Breaks);
            falseState = clauseNarrowed.FalseState;
        }

        var elseClause = nodes.FirstOrDefault(static node => node.Kind == LuaSyntaxKind.ElseClause);
        if (elseClause is null)
        {
            outputs.Add(falseState);
        }
        else
        {
            var elseBody = elseClause.ChildNodes().Single(static node => node.Kind == LuaSyntaxKind.Block);
            var elseResult = AnalyzeBlock(elseBody, falseState, insideLoop);
            outputs.Add(elseResult.Fallthrough);
            breaks.AddRange(elseResult.Breaks);
        }

        return new BlockResult(MergeStates(outputs, statement.Span), breaks);
    }

    private BlockResult AnalyzeWhile(LuaSyntaxNode statement, FlowState state)
    {
        var nodes = statement.ChildNodes().ToArray();
        var condition = nodes.First(static node => node.Kind != LuaSyntaxKind.Block);
        var body = nodes.Single(static node => node.Kind == LuaSyntaxKind.Block);
        var head = state.Clone();
        var loopBreaks = new List<FlowState>();
        var exit = state.Clone();
        for (var iteration = 0; iteration < _context.Options.MaximumFlowIterations; iteration++)
        {
            _currentFunction!.FlowIterations++;
            _ = InferExpression(condition, head);
            var narrowed = NarrowCondition(condition, head);
            exit = narrowed.FalseState;
            var bodyResult = AnalyzeBlock(body, narrowed.TrueState, insideLoop: true);
            loopBreaks.AddRange(bodyResult.Breaks);
            var candidate = MergeStates([state, bodyResult.Fallthrough], statement.Span);
            if (StatesEquivalent(head, candidate))
            {
                head = candidate;
                break;
            }

            head = WidenState(head, candidate, statement.Span);
            if (iteration == _context.Options.MaximumFlowIterations - 1)
            {
                _currentFunction.WasWidened = true;
                _context.AddDiagnostic(
                    "LUA6012",
                    statement.Span,
                    "Loop flow did not converge within the configured iteration budget; values were widened.");
            }
        }

        return new BlockResult(MergeStates([exit, .. loopBreaks], statement.Span), []);
    }

    private BlockResult AnalyzeRepeat(LuaSyntaxNode statement, FlowState state)
    {
        var nodes = statement.ChildNodes().ToArray();
        var body = nodes.Single(static node => node.Kind == LuaSyntaxKind.Block);
        var condition = nodes.Last(static node => node.Kind != LuaSyntaxKind.Block);
        var head = state.Clone();
        var exits = new List<FlowState>();
        for (var iteration = 0; iteration < _context.Options.MaximumFlowIterations; iteration++)
        {
            _currentFunction!.FlowIterations++;
            var bodyResult = AnalyzeBlock(body, head, insideLoop: true);
            exits.AddRange(bodyResult.Breaks);
            _ = InferExpression(condition, bodyResult.Fallthrough);
            var narrowed = NarrowCondition(condition, bodyResult.Fallthrough);
            exits.Add(narrowed.TrueState);
            var candidate = MergeStates([state, narrowed.FalseState], statement.Span);
            if (StatesEquivalent(head, candidate))
            {
                break;
            }

            head = WidenState(head, candidate, statement.Span);
            if (iteration == _context.Options.MaximumFlowIterations - 1)
            {
                _currentFunction.WasWidened = true;
                _context.AddDiagnostic(
                    "LUA6012",
                    statement.Span,
                    "Repeat-loop flow did not converge within the configured iteration budget; values were widened.");
            }
        }

        return new BlockResult(MergeStates(exits, statement.Span), []);
    }

    private BlockResult AnalyzeNumericFor(LuaSyntaxNode statement, FlowState state)
    {
        var body = statement.ChildNodes().Single(static node => node.Kind == LuaSyntaxKind.Block);
        var expressions = statement.ChildNodes().Where(node => !ReferenceEquals(node, body)).ToArray();
        foreach (var expression in expressions)
        {
            var type = InferExpression(expression, state);
            CheckAssignable(type, LuaTypes.Number, expression.Span, "numeric-for bound");
        }

        var token = statement.ChildTokens().FirstOrDefault(static item =>
            item.Kind == LuaTokenKind.Identifier && !item.IsMissing);
        var loopState = state.Clone();
        if (token is not null && _declarations.TryGetValue(token.Span, out var symbol))
        {
            var key = VariableKey.Local(symbol.Id);
            loopState.Types[key] = LuaTypes.Number;
            loopState.Assigned.Add(key);
            _declaredTypes[key] = LuaTypes.Number;
            RecordSymbolInference(symbol, LuaTypes.Number);
        }

        var bodyResult = AnalyzeBlock(body, loopState, insideLoop: true);
        return new BlockResult(
            MergeStates([state, bodyResult.Fallthrough, .. bodyResult.Breaks], statement.Span),
            []);
    }

    private BlockResult AnalyzeGenericFor(LuaSyntaxNode statement, FlowState state)
    {
        var nameList = statement.ChildNodes().Single(static node => node.Kind == LuaSyntaxKind.NameList);
        var expressions = statement.ChildNodes().Single(static node => node.Kind == LuaSyntaxKind.ExpressionList);
        var body = statement.ChildNodes().Single(static node => node.Kind == LuaSyntaxKind.Block);
        var iteratorPack = InferExpressionList(expressions, state, GetAnnotations(statement));
        var loopValues = InferGenericForValues(expressions, iteratorPack, state);
        var loopState = state.Clone();
        var names = nameList.ChildTokens().Where(static token =>
            token.Kind == LuaTokenKind.Identifier && !token.IsMissing).ToArray();
        for (var index = 0; index < names.Length; index++)
        {
            if (!_declarations.TryGetValue(names[index].Span, out var symbol))
            {
                continue;
            }

            var key = VariableKey.Local(symbol.Id);
            var type = loopValues.GetElementOrNil(index);
            if (type.Kind == LuaTypeKind.Nil)
            {
                type = LuaTypes.Any;
            }

            loopState.Types[key] = type;
            loopState.Assigned.Add(key);
            _declaredTypes[key] = type;
            RecordSymbolInference(symbol, type);
        }

        var bodyResult = AnalyzeBlock(body, loopState, insideLoop: true);
        return new BlockResult(
            MergeStates([state, bodyResult.Fallthrough, .. bodyResult.Breaks], statement.Span),
            []);
    }

    private LuaTypePack InferGenericForValues(
        LuaSyntaxNode expressions,
        LuaTypePack iteratorPack,
        FlowState state)
    {
        if (TryGetCalledGlobalIdentifier(expressions, out var iteratorName))
        {
            var call = expressions.ChildNodes().SingleOrDefault();
            var argument = call is null ? null : GetCallArguments(call).FirstOrDefault();
            var source = argument is null ? LuaTypes.Any : InferExpression(argument, state);
            if (string.Equals(iteratorName, "ipairs", StringComparison.Ordinal))
            {
                var element = source switch
                {
                    LuaArrayType array => array.ElementType,
                    LuaStructuralTableType table when table.ArrayElementType is not null =>
                        table.ArrayElementType,
                    LuaMapType map when _relations.IsAssignable(LuaTypes.Integer, map.KeyType) =>
                        map.ValueType,
                    _ => LuaTypes.Any,
                };
                return new LuaTypePack([LuaTypes.Integer, element]);
            }

            if (string.Equals(iteratorName, "pairs", StringComparison.Ordinal))
            {
                if (source is LuaMapType map)
                {
                    return new LuaTypePack([map.KeyType, map.ValueType]);
                }

                if (source is LuaStructuralTableType table)
                {
                    var keys = new List<LuaType>();
                    var values = new List<LuaType>();
                    keys.AddRange(table.Fields.Where(static item => item.Name is not null)
                        .Select(item => new LuaStringLiteralType(
                            Encoding.UTF8.GetBytes(item.Name!).ToImmutableArray())));
                    values.AddRange(table.Fields.Select(static item => item.ValueType));
                    if (table.ArrayElementType is not null)
                    {
                        keys.Add(LuaTypes.Integer);
                        values.Add(table.ArrayElementType);
                    }

                    if (table.MapKeyType is not null)
                    {
                        keys.Add(table.MapKeyType);
                    }

                    if (table.MapValueType is not null)
                    {
                        values.Add(table.MapValueType);
                    }

                    return new LuaTypePack([
                        keys.Count == 0 ? LuaTypes.Any : _relations.Union(keys),
                        values.Count == 0 ? LuaTypes.Any : _relations.Union(values),
                    ]);
                }

                return new LuaTypePack([LuaTypes.Any, LuaTypes.Any]);
            }
        }

        return iteratorPack.GetElementOrNil(0) switch
        {
            LuaFunctionType function => function.Returns,
            LuaOverloadType overload when !overload.Signatures.IsEmpty => overload.Signatures[0].Returns,
            _ => new LuaTypePack([], LuaTypes.Any),
        };
    }

    private void PrepareAssignmentTarget(LuaSyntaxNode target, FlowState state)
    {
        switch (target.Kind)
        {
            case LuaSyntaxKind.IndexExpression:
            case LuaSyntaxKind.MemberAccessExpression:
                foreach (var node in target.ChildNodes())
                {
                    _ = InferExpression(node, state);
                }

                break;
        }
    }

    private void AssignTarget(LuaSyntaxNode target, LuaType value, FlowState state)
    {
        if (target.Kind == LuaSyntaxKind.IdentifierExpression)
        {
            var token = target.ChildTokens().First(static item => item.Kind == LuaTokenKind.Identifier);
            if (!_references.TryGetValue(token.Span, out var reference))
            {
                return;
            }

            var key = reference.ResolutionKind == LuaNameResolutionKind.Global
                ? VariableKey.Global(reference.Name)
                : VariableKey.Local(reference.Symbol.Id);
            AssignVariable(key, reference.Symbol, value, token.Span, state);
            return;
        }

        if (target.Kind == LuaSyntaxKind.MemberAccessExpression)
        {
            AssignMember(target, value, state);
            return;
        }

        if (target.Kind == LuaSyntaxKind.IndexExpression)
        {
            AssignIndex(target, value, state);
        }
    }

    private void AssignVariable(
        VariableKey key,
        LuaSymbol symbol,
        LuaType value,
        TextSpan span,
        FlowState state)
    {
        if (_declaredTypes.TryGetValue(key, out var declared))
        {
            CheckAssignable(value, declared, span, $"assignment to '{symbol.Name}'");
        }

        state.Types[key] = value;
        state.Assigned.Add(key);
        if (key.IsGlobal)
        {
            _globalTypes[key.GlobalName!] = _globalTypes.TryGetValue(key.GlobalName!, out var previous)
                ? _relations.Union(previous, value)
                : value;
        }
        else
        {
            RecordSymbolInference(symbol, value);
        }
    }

    private void ApplyCasts(LuaSyntaxNode statement, FlowState state)
    {
        foreach (var cast in GetAnnotations(statement).OfType<LuaCastAnnotationSyntax>())
        {
            var matches = state.Types.Keys.Where(key =>
                key.IsGlobal
                    ? string.Equals(key.GlobalName, cast.Name, StringComparison.Ordinal)
                    : _semantics.Symbols.Any(symbol =>
                        symbol.Id == key.SymbolId &&
                        string.Equals(symbol.Name, cast.Name, StringComparison.Ordinal)))
                .OrderByDescending(static key => key.SymbolId)
                .ToArray();
            var key = matches.FirstOrDefault();
            if (matches.Length == 0)
            {
                key = VariableKey.Global(cast.Name);
            }

            var castType = _types.Resolve(cast.Type);
            var current = state.Types.GetValueOrDefault(key, LuaTypes.Any);
            var next = cast.Operation switch
            {
                LuaCastOperation.Add => _relations.Union(current, castType),
                LuaCastOperation.Remove => _relations.Exclude(current, castType),
                _ => castType,
            };
            if (next.Kind == LuaTypeKind.Never)
            {
                _context.AddDiagnostic(
                    "LUA6013",
                    cast.Span,
                    $"Cast of '{cast.Name}' produces the impossible type never.");
            }

            state.Types[key] = next;
            state.Assigned.Add(key);
        }
    }

    private string? GetFunctionName(LuaSyntaxNode statement)
    {
        var name = statement.ChildNodes().FirstOrDefault(static node =>
            node.Kind == LuaSyntaxKind.FunctionName);
        if (name is null)
        {
            return null;
        }

        var text = new StringBuilder();
        foreach (var token in name.Children.Where(static child => child.Token is not null)
                     .Select(static child => child.Token!))
        {
            text.Append(Encoding.UTF8.GetString(_semantics.Syntax.Source.GetSpan(token.Span)));
        }

        return text.ToString().Replace(':', '.');
    }
}
