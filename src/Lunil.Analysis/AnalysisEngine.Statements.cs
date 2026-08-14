using System.Collections.Immutable;
using System.Text;
using Lunil.Core;
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
        ImmutableArray<LuaAnnotationSyntax> annotations,
        LuaType? implicitSelfType)
    {
        LuaGenericAnnotationSyntax? genericAnnotation = null;
        LuaParamAnnotationSyntax? lastParameterAnnotation = null;
        LuaVarargAnnotationSyntax? vararg = null;
        LuaReturnAnnotationSyntax? returnAnnotation = null;
        var overloads = ImmutableArray<LuaFunctionType>.Empty.ToBuilder();
        foreach (var annotation in annotations)
        {
            switch (annotation)
            {
                case LuaGenericAnnotationSyntax generic:
                    genericAnnotation = generic;
                    break;
                case LuaParamAnnotationSyntax parameter:
                    lastParameterAnnotation = parameter;
                    break;
                case LuaVarargAnnotationSyntax variadic:
                    vararg = variadic;
                    break;
                case LuaReturnAnnotationSyntax returns:
                    returnAnnotation = returns;
                    break;
                case LuaOverloadAnnotationSyntax overload when
                    _types.Resolve(overload.Type) is LuaFunctionType signature:
                    overloads.Add(signature);
                    break;
            }
        }

        var typeParameters = genericAnnotation?.Parameters.Select((item, index) =>
        {
            var constraint = item.Constraint is null
                ? null
                : _types.Resolve(item.Constraint);
            return new LuaGenericParameterType(item.Name, index, constraint);
        }).ToImmutableArray() ?? [];
        var parameterMap = new Dictionary<string, LuaType>(StringComparer.Ordinal);
        foreach (var parameter in typeParameters)
        {
            parameterMap[parameter.Name] = parameter;
        }

        // Later duplicate @param annotations win, matching GroupBy(...).Last().
        var annotatedParameters = new Dictionary<string, LuaParamAnnotationSyntax>(
            StringComparer.Ordinal);
        foreach (var annotation in annotations)
        {
            if (annotation is LuaParamAnnotationSyntax parameter)
            {
                annotatedParameters[parameter.Name] = parameter;
            }
        }

        // Binder symbols normally arrive in source order; verify that cheaply and
        // fall back to an explicit sort so the result always matches
        // OrderBy(DeclaringSpan.Start).ThenBy(Id).
        var parameterSymbols = new List<LuaSymbol>();
        var sorted = true;
        LuaSymbol? previous = null;
        foreach (var symbol in function.Symbols)
        {
            if (symbol.Kind != LuaSymbolKind.Parameter)
            {
                continue;
            }

            if (previous is not null &&
                (symbol.DeclaringSpan.Start < previous.DeclaringSpan.Start ||
                 symbol.DeclaringSpan.Start == previous.DeclaringSpan.Start &&
                 symbol.Id < previous.Id))
            {
                sorted = false;
            }

            previous = symbol;
            parameterSymbols.Add(symbol);
        }

        if (!sorted)
        {
            parameterSymbols.Sort(static (left, right) =>
            {
                var byStart = left.DeclaringSpan.Start.CompareTo(right.DeclaringSpan.Start);
                return byStart != 0 ? byStart : left.Id.CompareTo(right.Id);
            });
        }

        var parameters = ImmutableArray.CreateBuilder<LuaFunctionParameter>();
        foreach (var symbol in parameterSymbols)
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
                var parameterType = symbol.Name == "self" && implicitSelfType is not null
                    ? implicitSelfType
                    : LuaTypes.Any;
                parameters.Add(new LuaFunctionParameter(symbol.Name, parameterType));
                if (_context.Options.ReportImplicitAny && symbol.Name != "self")
                {
                    _context.AddDiagnostic(
                        "LUA6014",
                        symbol.DeclaringSpan,
                        $"Parameter '{symbol.Name}' has implicit type any.");
                }
            }
        }

        if (function.IsVarArg)
        {
            parameters.Add(new LuaFunctionParameter(
                "...",
                vararg is null ? LuaTypes.Any : _types.Resolve(vararg.Type, parameterMap),
                IsOptional: true,
                IsVararg: true));
        }

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
        var overloadArray = overloads.ToImmutable();
        var valueType = overloadArray.IsEmpty
            ? (LuaType)primary
            : new LuaOverloadType([primary, .. overloadArray]);
        return new FunctionSpecification(
            primary,
            overloadArray,
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
        var classAnnotation = annotations.OfType<LuaClassAnnotationSyntax>().LastOrDefault();
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

            // A type annotation describes the storage location, not only the initializer.
            // Keep the declared union available to flow analysis so later writes performed
            // outside the current statement (including host writes and upvalue writes) are
            // not incorrectly ruled out by a literal initializer such as nil.
            var flowValue = index < declaredAnnotations.Length ? declared : value;
            var classDeclaration = _types.Declarations.OfType<LuaClassDeclaration>()
                .LastOrDefault(item => string.Equals(
                    item.Name,
                    classAnnotation?.Name ?? symbol.Name,
                    StringComparison.Ordinal));
            if (index == 0 && classDeclaration is not null &&
                (classAnnotation is not null ||
                 string.Equals(classDeclaration.Name, symbol.Name, StringComparison.Ordinal)))
            {
                flowValue = CreateAnnotatedPrototype(
                    classDeclaration,
                    flowValue,
                    token.Span);
            }
            state.SetType(key, flowValue);
            if (expressionList is not null)
            {
                state.MarkAssigned(key);
            }
            else
            {
                state.UnmarkAssigned(key);
            }

            RecordSymbolInference(symbol, flowValue);
        }

        return BlockResult.Next(state);
    }

    private LuaPrototypeType CreateAnnotatedPrototype(
        LuaClassDeclaration declaration,
        LuaType value,
        TextSpan span)
    {
        LuaType shape = value;
        foreach (var field in declaration.Fields)
        {
            var runtime = _relations.FindField(value, field.Name!);
            if (runtime is not null &&
                !_relations.IsAssignable(runtime.ValueType, field.ValueType) &&
                !_relations.IsAssignable(field.ValueType, runtime.ValueType))
            {
                _context.AddDiagnostic(
                    "LUA6019",
                    span,
                    $"Runtime prototype member '{field.Name}' conflicts with its class annotation type '{field.ValueType.DisplayName}'.");
            }

            if (runtime is null)
            {
                shape = AddOrReplaceField(shape, field.Name!, field.ValueType);
            }
        }

        return new LuaPrototypeType(
            declaration.Name,
            shape,
            declaration.BaseTypes,
            UsesSelfIndex: false);
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

        var functionName = statement.ChildNodes().FirstOrDefault(static node =>
            node.Kind == LuaSyntaxKind.FunctionName);
        var nameTokens = (functionName?.ChildTokens() ?? statement.ChildTokens())
            .Where(static token => token.Kind == LuaTokenKind.Identifier && !token.IsMissing)
            .ToArray();
        var identifier = nameTokens.FirstOrDefault();
        if (identifier is null)
        {
            return BlockResult.Next(state);
        }

        if (local && _declarations.TryGetValue(identifier.Span, out var symbol))
        {
            var type = AnalyzeFunction(functionId, GetAnnotations(statement));
            AssignVariable(VariableKey.Local(symbol.Id), symbol, type, identifier.Span, state);
        }
        else if (_references.TryGetValue(identifier.Span, out var reference))
        {
            var key = reference.ResolutionKind == LuaNameResolutionKind.Global
                ? VariableKey.Global(reference.Name)
                : VariableKey.Local(reference.Symbol.Id);
            var root = state.TypeOf(
                key,
                _declaredTypes.GetValueOrDefault(key, LuaTypes.Any));
            var isMember = nameTokens.Length > 1;
            var receiver = isMember
                ? ResolvePrototypePath(root, nameTokens.Skip(1).SkipLast(1))
                : root;
            var hasImplicitSelf = functionName?.ChildTokens().Any(static token =>
                token.Kind == LuaTokenKind.Colon) == true;
            var methodName = nameTokens.LastOrDefault() is { } methodToken
                ? GetTokenText(methodToken)
                : string.Empty;
            var implicitSelf = hasImplicitSelf
                ? IsConstructorName(methodName)
                    ? receiver
                    : new LuaMetatableType(
                        new LuaStructuralTableType([], IsOpen: true),
                        receiver,
                        receiver.Kind is not (LuaTypeKind.Any or LuaTypeKind.Unknown))
                : null;
            var type = AnalyzeFunction(
                functionId,
                GetAnnotations(statement),
                implicitSelf);
            if (isMember)
            {
                var memberPath = nameTokens.Skip(1).Select(GetTokenText).ToArray();
                var next = AddOrReplacePrototypePath(root, memberPath, type);
                AssignVariable(key, reference.Symbol, next, identifier.Span, state);
                PropagateTableMutation(state, root, next, key);
            }
            else
            {
                AssignVariable(key, reference.Symbol, type, identifier.Span, state);
            }

            var path = GetFunctionName(statement);
            if (path is not null)
            {
                state.SetType(VariableKey.Global(path), type);
                state.MarkAssigned(VariableKey.Global(path));
                SetGlobalType(path, type);
            }
        }

        return BlockResult.Next(state);
    }

    private LuaType ResolvePrototypePath(LuaType root, IEnumerable<LuaSyntaxToken> members)
    {
        var current = root;
        foreach (var member in members)
        {
            var field = _relations.FindField(current, GetTokenText(member));
            if (field is null)
            {
                return LuaTypes.Any;
            }

            current = field.ValueType;
        }

        return current;
    }

    private static LuaType AddOrReplacePrototypePath(
        LuaType root,
        string[] members,
        LuaType value)
    {
        if (members.Length == 0)
        {
            return value;
        }

        var name = members[0];
        if (members.Length == 1)
        {
            return AddOrReplaceField(root, name, value);
        }

        LuaType shape = root is LuaPrototypeType prototype ? prototype.Shape : root;
        var table = shape as LuaStructuralTableType ?? new LuaStructuralTableType([], IsOpen: true);
        var existing = table.Fields.LastOrDefault(field =>
            string.Equals(field.Name, name, StringComparison.Ordinal))?.ValueType ??
            new LuaStructuralTableType([], IsOpen: true);
        var nested = AddOrReplacePrototypePath(existing, members.Skip(1).ToArray(), value);
        return AddOrReplaceField(root, name, nested);
    }

    private static bool IsConstructorName(string name) => name is
        "new" or "create" or "constructor" or "ctor";

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
            loopState.SetType(key, LuaTypes.Number);
            loopState.MarkAssigned(key);
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

            loopState.SetType(key, type);
            loopState.MarkAssigned(key);
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
            var call = GetOnlyChildNodeOrDefault(expressions);
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
                if (source is LuaMetatableType metatable &&
                    _semantics.LanguageVersion != LuaLanguageVersion.Lua51 &&
                    TryGetMetamethodSignatures(metatable, "__pairs", out var pairs) &&
                    !pairs.IsEmpty &&
                    pairs[0].Returns.GetElementOrNil(0) is LuaFunctionType iterator)
                {
                    return iterator.Returns;
                }

                if (source is LuaMetatableType wrapped)
                {
                    source = wrapped.BaseType;
                }

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

        state.SetType(key, value);
        state.MarkAssigned(key);
        var pathPrefix = key.IsGlobal ? "g:" + key.GlobalName : "s:" + key.SymbolId;
        foreach (var path in state.PathTypes.Keys.Where(path =>
                     string.Equals(path.Value, pathPrefix, StringComparison.Ordinal) ||
                     path.Value.StartsWith(pathPrefix + ".", StringComparison.Ordinal)).ToArray())
        {
            state.PathTypes.Remove(path);
        }
        if (value is LuaPrototypeType prototype)
        {
            _latestPrototypes[prototype.Name] = prototype;
        }
        if (key.IsGlobal)
        {
            SetGlobalType(
                key.GlobalName!,
                _globalTypes.TryGetLatest(key.GlobalName!, out var previous)
                    ? _relations.Union(previous, value)
                    : value);
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
            var matches = new List<VariableKey>();
            foreach (var key in state.EnumerateTypeKeys())
            {
                if (key.IsGlobal
                    ? string.Equals(key.GlobalName, cast.Name, StringComparison.Ordinal)
                    : _symbolsById.TryGetValue(key.SymbolId, out var symbol) &&
                        string.Equals(symbol.Name, cast.Name, StringComparison.Ordinal))
                {
                    matches.Add(key);
                }
            }

            matches.Sort(static (left, right) => right.SymbolId.CompareTo(left.SymbolId));
            var castKey = matches.Count > 0 ? matches[0] : VariableKey.Global(cast.Name);

            var castType = _types.Resolve(cast.Type);
            var current = state.TypeOf(castKey, LuaTypes.Any);
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

            state.SetType(castKey, next);
            state.MarkAssigned(castKey);
        }
    }

    private string? GetFunctionName(LuaSyntaxNode statement)
    {
        if (_functionNamesByOwnerSpan.TryGetValue(statement.Span, out var cached))
        {
            return cached;
        }

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

        var result = text.ToString().Replace(':', '.');
        _functionNamesByOwnerSpan[statement.Span] = result;
        return result;
    }
}
