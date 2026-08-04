using System.Collections.Immutable;
using Lunil.Core;
using Lunil.Semantics.Binding;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

namespace Lunil.Analysis;

internal sealed partial class AnalysisEngine
{
    private LuaType InferExpression(
        LuaSyntaxNode expression,
        FlowState state,
        ImmutableArray<Lunil.EmmyLua.LuaAnnotationSyntax> functionAnnotations = default)
    {
        if (_countedExpressionTypes.Add(expression.Span) &&
            !_context.TryCreateType(expression.Span, depth: 1))
        {
            return LuaTypes.Unknown;
        }

        var type = expression.Kind switch
        {
            LuaSyntaxKind.NilLiteralExpression => LuaTypes.Nil,
            LuaSyntaxKind.FalseLiteralExpression => new LuaBooleanLiteralType(false),
            LuaSyntaxKind.TrueLiteralExpression => new LuaBooleanLiteralType(true),
            LuaSyntaxKind.NumericLiteralExpression => InferNumericLiteral(expression),
            LuaSyntaxKind.StringLiteralExpression => InferStringLiteral(expression),
            LuaSyntaxKind.VarArgExpression => InferVarargPack().GetElementOrNil(0),
            LuaSyntaxKind.IdentifierExpression => InferIdentifier(expression, state),
            LuaSyntaxKind.ParenthesizedExpression => InferExpression(
                expression.ChildNodes().Single(),
                state),
            LuaSyntaxKind.UnaryExpression => InferUnary(expression, state),
            LuaSyntaxKind.BinaryExpression => InferBinary(expression, state),
            LuaSyntaxKind.FunctionExpression => InferFunctionExpression(
                expression,
                functionAnnotations.IsDefault ? [] : functionAnnotations),
            LuaSyntaxKind.TableConstructorExpression => InferTable(expression, state),
            LuaSyntaxKind.IndexExpression => InferIndex(expression, state),
            LuaSyntaxKind.MemberAccessExpression => InferMember(expression, state),
            LuaSyntaxKind.CallExpression or LuaSyntaxKind.MethodCallExpression =>
                InferCall(expression, state).GetElementOrNil(0),
            LuaSyntaxKind.ExpressionList => InferExpressionList(expression, state, []).GetElementOrNil(0),
            LuaSyntaxKind.Error => LuaTypes.Unknown,
            _ => InferComposite(expression, state),
        };
        RecordExpressionInference(expression.Span, type);
        return type;
    }

    private LuaTypePack InferExpressionPack(
        LuaSyntaxNode expression,
        FlowState state,
        ImmutableArray<Lunil.EmmyLua.LuaAnnotationSyntax> functionAnnotations = default)
    {
        var pack = expression.Kind switch
        {
            LuaSyntaxKind.CallExpression or LuaSyntaxKind.MethodCallExpression =>
                InferCall(expression, state),
            LuaSyntaxKind.VarArgExpression => InferVarargPack(),
            _ => new LuaTypePack([
                InferExpression(expression, state, functionAnnotations),
            ]),
        };
        if (!pack.Head.IsEmpty)
        {
            RecordExpressionInference(expression.Span, pack.Head[0]);
        }

        return pack;
    }

    private LuaTypePack InferExpressionList(
        LuaSyntaxNode expressionList,
        FlowState state,
        ImmutableArray<Lunil.EmmyLua.LuaAnnotationSyntax> functionAnnotations)
    {
        var expressions = expressionList.ChildNodes().ToArray();
        if (expressions.Length == 0)
        {
            return LuaTypePack.Empty;
        }

        var head = ImmutableArray.CreateBuilder<LuaType>();
        for (var index = 0; index < expressions.Length; index++)
        {
            var annotations = expressions[index].Kind == LuaSyntaxKind.FunctionExpression
                ? functionAnnotations
                : [];
            if (index == expressions.Length - 1)
            {
                var tail = InferExpressionPack(expressions[index], state, annotations);
                foreach (var item in tail.Head.Take(
                             Math.Max(0, _context.Options.MaximumReturnPackLength - head.Count)))
                {
                    head.Add(item);
                }

                return new LuaTypePack(head.ToImmutable(), tail.VariadicType);
            }

            head.Add(InferExpression(expressions[index], state, annotations));
        }

        return new LuaTypePack(head.ToImmutable());
    }

    private LuaType InferIdentifier(LuaSyntaxNode expression, FlowState state)
    {
        var token = expression.ChildTokens().First(static item => item.Kind == LuaTokenKind.Identifier);
        if (!_references.TryGetValue(token.Span, out var reference))
        {
            return LuaTypes.Unknown;
        }

        var key = reference.ResolutionKind == LuaNameResolutionKind.Global
            ? VariableKey.Global(reference.Name)
            : VariableKey.Local(reference.Symbol.Id);
        if (reference.ResolutionKind != LuaNameResolutionKind.Global &&
            reference.Symbol.IsCaptured)
        {
            if (!_upvalueCells.TryGetValue(reference.Symbol.Id, out var cell))
            {
                var initial = state.Types.GetValueOrDefault(
                    key,
                    _declaredTypes.GetValueOrDefault(key, LuaTypes.Any));
                cell = new UpvalueCellState(reference.Symbol, initial);
                _upvalueCells.Add(reference.Symbol.Id, cell);
            }

            cell.Readers.Add(_currentFunction?.FunctionId ?? 0);
            state.Types[key] = cell.Type;
        }
        if (!key.IsGlobal && !state.Assigned.Contains(key))
        {
            _context.AddDiagnostic(
                "LUA6008",
                token.Span,
                $"Local '{reference.Name}' may be read before an explicit assignment.");
        }

        if (state.Types.TryGetValue(key, out var type))
        {
            return type;
        }

        if (_declaredTypes.TryGetValue(key, out type))
        {
            return type;
        }

        if (key.IsGlobal)
        {
            if (_context.Options.ReportUnknownGlobals && _reportedUnknownGlobals.Add(reference.Name))
            {
                _context.AddDiagnostic(
                    "LUA6015",
                    token.Span,
                    $"Global '{reference.Name}' has no known static type.");
            }

            return LuaTypes.Any;
        }

        return LuaTypes.Any;
    }

    private static LuaType InferNumericLiteral(LuaSyntaxNode expression)
    {
        var token = expression.ChildTokens().FirstOrDefault();
        return token?.Value switch
        {
            LuaIntegerTokenValue integer => new LuaIntegerLiteralType(integer.Integer),
            LuaFloatTokenValue number => new LuaFloatLiteralType(number.Float),
            _ => LuaTypes.Number,
        };
    }

    private static LuaType InferStringLiteral(LuaSyntaxNode expression)
    {
        var token = expression.ChildTokens().FirstOrDefault();
        return token?.Value is LuaStringTokenValue text
            ? new LuaStringLiteralType(text.Bytes)
            : LuaTypes.String;
    }

    private LuaType InferUnary(LuaSyntaxNode expression, FlowState state)
    {
        var operand = expression.ChildNodes().Single();
        var operandType = InferExpression(operand, state);
        var operation = expression.ChildTokens().First().Kind;
        if (TryInferUnaryOperator(operandType, operation, operand.Span, out var operatorResult))
        {
            return operatorResult;
        }

        return operation switch
        {
            LuaTokenKind.NotKeyword => LuaTypes.Boolean,
            LuaTokenKind.Length when CheckLengthOperand(operandType, operand.Span) => LuaTypes.Integer,
            LuaTokenKind.Minus when IsIntegerLike(operandType) => LuaTypes.Integer,
            LuaTokenKind.Minus when CheckNumericOperand(operandType, operand.Span) => LuaTypes.Number,
            LuaTokenKind.Tilde when CheckIntegerOperand(operandType, operand.Span) => LuaTypes.Integer,
            _ => LuaTypes.Unknown,
        };
    }

    private LuaType InferBinary(LuaSyntaxNode expression, FlowState state)
    {
        var nodes = expression.ChildNodes().ToArray();
        var left = InferExpression(nodes[0], state);
        var operation = expression.ChildTokens().Single().Kind;
        if (operation == LuaTokenKind.AndKeyword)
        {
            var narrowed = NarrowCondition(nodes[0], state);
            var right = InferExpression(nodes[1], narrowed.TrueState);
            return _relations.Union(_relations.FalsyPart(left), right);
        }

        if (operation == LuaTokenKind.OrKeyword)
        {
            var narrowed = NarrowCondition(nodes[0], state);
            var right = InferExpression(nodes[1], narrowed.FalseState);
            return _relations.Union(_relations.TruthyPart(left), right);
        }

        var rightType = InferExpression(nodes[1], state);
        if (TryInferBinaryOperator(
                left,
                rightType,
                operation,
                expression.Span,
                out var operatorResult))
        {
            return operatorResult;
        }

        return operation switch
        {
            LuaTokenKind.Equal or LuaTokenKind.NotEqual => LuaTypes.Boolean,
            LuaTokenKind.LessThan or LuaTokenKind.LessThanOrEqual or
            LuaTokenKind.GreaterThan or LuaTokenKind.GreaterThanOrEqual when
                CheckComparableOperands(left, rightType, expression.Span) => LuaTypes.Boolean,
            LuaTokenKind.Concatenate when CheckConcatenationOperand(left, nodes[0].Span) &
                CheckConcatenationOperand(rightType, nodes[1].Span) => LuaTypes.String,
            LuaTokenKind.Ampersand or LuaTokenKind.Pipe or LuaTokenKind.Tilde or
            LuaTokenKind.ShiftLeft or LuaTokenKind.ShiftRight when
                CheckIntegerOperand(left, nodes[0].Span) &
                CheckIntegerOperand(rightType, nodes[1].Span) => LuaTypes.Integer,
            LuaTokenKind.Plus or LuaTokenKind.Minus or LuaTokenKind.Star or
            LuaTokenKind.Percent or LuaTokenKind.FloorDivide when
                CheckNumericOperand(left, nodes[0].Span) &
                CheckNumericOperand(rightType, nodes[1].Span) =>
                    IsIntegerLike(left) && IsIntegerLike(rightType)
                        ? LuaTypes.Integer
                        : LuaTypes.Number,
            LuaTokenKind.Slash or LuaTokenKind.Caret when
                CheckNumericOperand(left, nodes[0].Span) &
                CheckNumericOperand(rightType, nodes[1].Span) => LuaTypes.Number,
            _ => LuaTypes.Unknown,
        };
    }

    private LuaType InferFunctionExpression(
        LuaSyntaxNode expression,
        ImmutableArray<Lunil.EmmyLua.LuaAnnotationSyntax> annotations)
    {
        return _functionIdsByOwnerSpan.TryGetValue(expression.Span, out var functionId)
            ? AnalyzeFunction(functionId, annotations)
            : LuaTypes.Function;
    }

    private LuaStructuralTableType InferTable(LuaSyntaxNode expression, FlowState state)
    {
        var members = ImmutableArray.CreateBuilder<LuaTableField>();
        var arrayTypes = new List<LuaType>();
        var mapKeys = new List<LuaType>();
        var mapValues = new List<LuaType>();
        foreach (var item in expression.ChildNodes().Where(static node =>
                     node.Kind == LuaSyntaxKind.TableField))
        {
            var tokens = item.ChildTokens().ToArray();
            var nodes = item.ChildNodes().ToArray();
            if (tokens.FirstOrDefault()?.Kind == LuaTokenKind.OpenBracket && nodes.Length >= 2)
            {
                var key = InferExpression(nodes[0], state);
                var value = InferExpressionPack(nodes[^1], state).GetElementOrNil(0);
                var name = key is LuaStringLiteralType text ? DecodeLiteral(text) : null;
                members.Add(new LuaTableField(name, name is null ? key : null, value, false));
                mapKeys.Add(key);
                mapValues.Add(value);
            }
            else if (tokens.Length >= 2 && tokens[0].Kind == LuaTokenKind.Identifier &&
                tokens[1].Kind == LuaTokenKind.Assign)
            {
                var name = GetTokenText(tokens[0]);
                var value = InferExpressionPack(nodes.Single(), state).GetElementOrNil(0);
                members.Add(new LuaTableField(name, null, value, false));
            }
            else if (nodes.Length != 0)
            {
                var value = InferExpressionPack(nodes.Single(), state).GetElementOrNil(0);
                arrayTypes.Add(value);
                members.Add(new LuaTableField(null, null, value, false));
            }
        }

        return new LuaStructuralTableType(
            members.ToImmutable(),
            arrayTypes.Count == 0 ? null : _relations.Union(arrayTypes),
            mapKeys.Count == 0 ? null : _relations.Union(mapKeys),
            mapValues.Count == 0 ? null : _relations.Union(mapValues));
    }

    private LuaType InferMember(LuaSyntaxNode expression, FlowState state)
    {
        var target = expression.ChildNodes().Single();
        var targetType = InferExpression(target, state);
        targetType = ApplyPathNarrowing(target, targetType, state, expression.Span);
        var nameToken = expression.ChildTokens().Last(static token =>
            token.Kind == LuaTokenKind.Identifier);
        var name = GetTokenText(nameToken);
        var result = InferMemberType(targetType, name, expression.Span);
        RecordPathType(expression, result, state);
        return result;
    }

    private LuaType InferMemberType(LuaType target, string name, Lunil.Core.Text.TextSpan span)
    {
        if (target.Kind == LuaTypeKind.Any)
        {
            return LuaTypes.Any;
        }

        if (target.Kind == LuaTypeKind.Unknown)
        {
            return LuaTypes.Unknown;
        }

        if (target.Kind == LuaTypeKind.Never)
        {
            return LuaTypes.Never;
        }

        if (target is LuaPrototypeType { IsPrecise: false })
        {
            return LuaTypes.Any;
        }

        if (target.Kind == LuaTypeKind.Table)
        {
            return LuaTypes.Any;
        }

        if (TryInferEffectiveMember(target, name, out var effective))
        {
            return effective;
        }

        var item = _relations.FindField(target, name);
        if (item is not null)
        {
            return item.IsOptional
                ? _relations.Union(item.ValueType, LuaTypes.Nil)
                : item.ValueType;
        }

        if (target is LuaStructuralTableType { IsOpen: true })
        {
            return LuaTypes.Any;
        }

        if (target is LuaStructuralTableType { Fields.IsEmpty: true } empty &&
            empty.ArrayElementType is null && empty.MapKeyType is null)
        {
            return LuaTypes.Any;
        }

        if (target is LuaMetatableType { IsPrecise: true } classSelf &&
            classSelf.BaseType is LuaStructuralTableType { Fields.IsEmpty: true, ArrayElementType: null, MapKeyType: null })
        {
            return LuaTypes.Any;
        }

        if (target is LuaClassType emptyClass && IsEmptyClass(emptyClass))
        {
            return LuaTypes.Any;
        }

        _context.AddDiagnostic(
            "LUA6007",
            span,
            $"Type '{target.DisplayName}' has no known member '{name}'.");
        return LuaTypes.Unknown;
    }

    private bool IsEmptyClass(LuaClassType type)
    {
        var declaration = _types.Declarations.OfType<LuaClassDeclaration>()
            .FirstOrDefault(item => string.Equals(item.Name, type.Name, StringComparison.Ordinal));
        return declaration is { Fields.IsEmpty: true, BaseTypes.IsEmpty: true };
    }

    private LuaType InferIndex(LuaSyntaxNode expression, FlowState state)
    {
        var nodes = expression.ChildNodes().ToArray();
        var target = InferExpression(nodes[0], state);
        target = ApplyPathNarrowing(nodes[0], target, state, expression.Span);
        var index = InferExpression(nodes[1], state);
        if (target.Kind == LuaTypeKind.Any)
        {
            return LuaTypes.Any;
        }

        if (target is LuaMetatableType metatable &&
            TryInferMetatableIndex(metatable, index, out var metatableResult))
        {
            RecordPathType(expression, metatableResult, state);
            return metatableResult;
        }

        if (target is LuaArrayType array)
        {
            CheckAssignable(index, LuaTypes.Integer, nodes[1].Span, "array index");
            RecordPathType(expression, array.ElementType, state);
            return array.ElementType;
        }

        if (target is LuaMapType map)
        {
            CheckAssignable(index, map.KeyType, nodes[1].Span, "map index");
            RecordPathType(expression, map.ValueType, state);
            return map.ValueType;
        }

        if (target is LuaStructuralTableType table)
        {
            var isEmptyOpen = table.IsOpen ||
                (table.Fields.IsEmpty && table.ArrayElementType is null && table.MapKeyType is null);
            if (index is LuaStringLiteralType text)
            {
                var name = DecodeLiteral(text);
                var item = table.Fields.LastOrDefault(candidate =>
                    string.Equals(candidate.Name, name, StringComparison.Ordinal));
                if (item is not null)
                {
                    var fieldType = item.IsOptional
                        ? _relations.Union(item.ValueType, LuaTypes.Nil)
                        : item.ValueType;
                    RecordPathType(expression, fieldType, state);
                    return fieldType;
                }

                if (isEmptyOpen)
                {
                    return LuaTypes.Any;
                }
            }

            if (IsIntegerLike(index) && table.ArrayElementType is not null)
            {
                RecordPathType(expression, table.ArrayElementType, state);
                return table.ArrayElementType;
            }

            if (table.MapKeyType is not null && table.MapValueType is not null)
            {
                CheckAssignable(index, table.MapKeyType, nodes[1].Span, "table index");
                RecordPathType(expression, table.MapValueType, state);
                return table.MapValueType;
            }

            if (isEmptyOpen)
            {
                return LuaTypes.Any;
            }
        }

        if (target is LuaUnionType union)
        {
            var unionResult = _relations.Union(union.Types.Select(member => InferIndexedMember(
                member,
                index,
                nodes[1].Span)));
            RecordPathType(expression, unionResult, state);
            return unionResult;
        }

        if (target.Kind is LuaTypeKind.Unknown)
        {
            return LuaTypes.Unknown;
        }

        if (target.Kind is LuaTypeKind.Table or LuaTypeKind.Any or LuaTypeKind.Never)
        {
            return target.Kind == LuaTypeKind.Never ? LuaTypes.Never : LuaTypes.Any;
        }

        if (target is LuaMetatableType { IsPrecise: true } classSelf &&
            classSelf.BaseType is LuaStructuralTableType { Fields.IsEmpty: true, ArrayElementType: null, MapKeyType: null })
        {
            return LuaTypes.Any;
        }

        _context.AddDiagnostic(
            "LUA6007",
            expression.Span,
            $"Type '{target.DisplayName}' does not expose the indexed value statically.");
        return LuaTypes.Unknown;
    }

    private LuaType InferIndexedMember(
        LuaType target,
        LuaType index,
        Lunil.Core.Text.TextSpan span)
    {
        if (target is LuaArrayType array && IsIntegerLike(index))
        {
            return array.ElementType;
        }

        if (target is LuaMapType map && _relations.IsAssignable(index, map.KeyType))
        {
            return map.ValueType;
        }

        if (index is LuaStringLiteralType text)
        {
            return _relations.FindField(target, DecodeLiteral(text))?.ValueType ?? LuaTypes.Unknown;
        }

        return LuaTypes.Unknown;
    }

    private LuaTypePack InferCall(LuaSyntaxNode expression, FlowState state)
    {
        var nodes = expression.ChildNodes().ToArray();
        LuaSyntaxNode? calleeNode;
        LuaSyntaxNode? receiver = null;
        LuaType? receiverType = null;
        string? memberName = null;
        LuaType callee = LuaTypes.Unknown;
        var arguments = GetCallArguments(expression).ToList();
        if (expression.Kind == LuaSyntaxKind.MethodCallExpression)
        {
            receiver = nodes.FirstOrDefault(static node => node.Kind != LuaSyntaxKind.ArgumentList);
            calleeNode = receiver;
            var methodToken = expression.ChildTokens().LastOrDefault(static token =>
                token.Kind == LuaTokenKind.Identifier);
            if (receiver is not null)
            {
                receiverType = InferExpression(receiver, state);
                arguments.Insert(0, receiver);
            }

            if (receiverType is not null && methodToken is not null && !methodToken.IsMissing)
            {
                memberName = GetTokenText(methodToken);
                callee = InferMemberType(receiverType, memberName, methodToken.Span);
            }
        }
        else
        {
            calleeNode = nodes.FirstOrDefault(static node => node.Kind != LuaSyntaxKind.ArgumentList);
            if (calleeNode is not null)
            {
                callee = InferExpression(calleeNode, state);
                if (calleeNode.Kind == LuaSyntaxKind.MemberAccessExpression)
                {
                    receiver = calleeNode.ChildNodes().FirstOrDefault();
                    receiverType = receiver is not null &&
                        _expressionInferences.TryGetValue(receiver.Span, out var inferredReceiver)
                            ? inferredReceiver
                            : LuaTypes.Unknown;
                    var memberToken = calleeNode.ChildTokens().LastOrDefault(static token =>
                        token.Kind == LuaTokenKind.Identifier);
                    if (memberToken is not null && !memberToken.IsMissing)
                    {
                        memberName = GetTokenText(memberToken);
                    }
                }
            }
        }

        var isGlobalCall = TryGetCalledGlobalIdentifier(expression, out var calledName);
        if (isGlobalCall)
        {
            var special = InferSpecialCall(expression, calledName, state);
            if (special is not null)
            {
                var specialSignatures = GetCallSignatures(callee);
                var status = specialSignatures.IsEmpty
                    ? LuaCallResolutionStatus.Dynamic
                    : LuaCallResolutionStatus.Resolved;
                string? reason = specialSignatures.IsEmpty
                    ? LuaCallUnresolvedReasons.CalleeSignatureIsDynamic
                    : null;
                if (string.Equals(calledName, "require", StringComparison.Ordinal) &&
                    !TryGetStaticModuleRequest(expression, out _))
                {
                    status = LuaCallResolutionStatus.Dynamic;
                    reason = LuaCallUnresolvedReasons.ModuleRequestIsDynamic;
                }

                RecordCallSite(
                    expression,
                    calleeNode,
                    callee,
                    receiver,
                    receiverType,
                    memberName,
                    status,
                    reason);
                return special;
            }
        }

        if (InferHostCall(expression, state, out var hostResult))
        {
            RecordCallSite(
                expression,
                calleeNode,
                callee,
                receiver,
                receiverType,
                memberName,
                LuaCallResolutionStatus.Resolved,
                unresolvedReason: null);
            return hostResult;
        }

        var argumentTypes = arguments.Select(argument => InferExpression(argument, state)).ToImmutableArray();
        if (callee is LuaMetatableType && TryGetMetamethodSignatures(callee, "__call", out _))
        {
            argumentTypes = [callee, .. argumentTypes];
        }
        var signatures = GetCallSignatures(callee);
        if (signatures.IsEmpty)
        {
            if (callee.Kind is LuaTypeKind.Any or LuaTypeKind.Unknown or LuaTypeKind.Function)
            {
                InvalidateEscapedMetatables(arguments, state);
                RecordCallSite(
                    expression,
                    calleeNode,
                    callee,
                    receiver,
                    receiverType,
                    memberName,
                    LuaCallResolutionStatus.Dynamic,
                    LuaCallUnresolvedReasons.CalleeSignatureIsDynamic);
                return new LuaTypePack([], LuaTypes.Any);
            }

            _context.AddDiagnostic(
                "LUA6004",
                expression.Span,
                $"Value of type '{callee.DisplayName}' is not callable.");
            RecordCallSite(
                expression,
                calleeNode,
                callee,
                receiver,
                receiverType,
                memberName,
                LuaCallResolutionStatus.Unresolved,
                LuaCallUnresolvedReasons.CalleeIsNotCallable);
            return new LuaTypePack([LuaTypes.Unknown]);
        }

        var instantiated = signatures
            .Select(signature => InstantiateFunction(signature, argumentTypes, expression.Span))
            .ToArray();
        var selected = instantiated.FirstOrDefault(signature =>
            IsCallCompatible(signature, argumentTypes)) ?? instantiated[0];
        if (expression.Kind == LuaSyntaxKind.MethodCallExpression &&
            !selected.HasImplicitSelf &&
            (selected.Parameters.IsEmpty ||
             !string.Equals(selected.Parameters[0].Name, "self", StringComparison.Ordinal)))
        {
            _context.AddDiagnostic(
                "LUA6017",
                expression.Span,
                "Colon call supplies an implicit self argument to a function that was declared without self.");
        }
        else if (expression.Kind == LuaSyntaxKind.CallExpression &&
                 calleeNode?.Kind == LuaSyntaxKind.MemberAccessExpression &&
                 selected.HasImplicitSelf &&
                 argumentTypes.Length < selected.Parameters.Count(static parameter =>
                     !parameter.IsOptional && !parameter.IsVararg))
        {
            _context.AddDiagnostic(
                "LUA6018",
                expression.Span,
                "Dot call omits the implicit self argument required by a colon-declared method.");
        }

        CheckCall(selected, argumentTypes, expression.Span);
        RecordCallSite(
            expression,
            calleeNode,
            callee,
            receiver,
            receiverType,
            memberName,
            LuaCallResolutionStatus.Resolved,
            unresolvedReason: null);
        return selected.Returns;
    }

    private LuaTypePack? InferSpecialCall(
        LuaSyntaxNode expression,
        string calledName,
        FlowState state)
    {
        var arguments = GetCallArguments(expression).ToArray();
        switch (calledName)
        {
            case "type":
                return new LuaTypePack([
                    arguments.Length == 0
                        ? LuaTypes.String
                        : InferTypeTag(InferExpression(arguments[0], state)),
                ]);
            case "assert":
                if (arguments.Length == 0)
                {
                    return new LuaTypePack([LuaTypes.Any]);
                }

                return new LuaTypePack([
                    _relations.TruthyPart(InferExpression(arguments[0], state)),
                    .. arguments.Skip(1).Select(argument => InferExpression(argument, state)),
                ]);
            case "tonumber":
                foreach (var argument in arguments)
                {
                    _ = InferExpression(argument, state);
                }

                return new LuaTypePack([_relations.Union(LuaTypes.Number, LuaTypes.Nil)]);
            case "tostring":
                foreach (var argument in arguments)
                {
                    _ = InferExpression(argument, state);
                }

                return new LuaTypePack([LuaTypes.String]);
            case "error":
                foreach (var argument in arguments)
                {
                    _ = InferExpression(argument, state);
                }

                return new LuaTypePack([LuaTypes.Never]);
            case "require":
                var argumentTypes = arguments
                    .Select(argument => InferExpression(argument, state))
                    .ToArray();
                if (argumentTypes.FirstOrDefault() is LuaStringLiteralType moduleName &&
                    (_environment.ModuleTypes.TryGetValue(
                         DecodeLiteral(moduleName),
                         out var moduleType) ||
                     _hostModuleTypes.TryGetValue(
                         DecodeLiteral(moduleName),
                         out moduleType)))
                {
                    return new LuaTypePack([moduleType]);
                }

                return new LuaTypePack([LuaTypes.Any]);
            case "setmetatable":
                return InferSetMetatable(expression.Span, arguments, state);
            case "getmetatable":
                return InferGetMetatable(arguments, state);
            case "rawget":
                return InferRawGet(arguments, state);
            case "rawset":
                return InferRawSet(arguments, state);
            case "pcall":
            case "xpcall":
                foreach (var argument in arguments)
                {
                    _ = InferExpression(argument, state);
                }

                return new LuaTypePack([LuaTypes.Boolean], LuaTypes.Any);
            case "select":
                foreach (var argument in arguments)
                {
                    _ = InferExpression(argument, state);
                }

                return new LuaTypePack([], LuaTypes.Any);
            default:
                return null;
        }
    }

    private ImmutableArray<LuaFunctionType> GetCallSignatures(LuaType type)
    {
        var builder = ImmutableArray.CreateBuilder<LuaFunctionType>();
        AddCallSignatures(type, builder);
        return builder.ToImmutable();
    }

    private void AddCallSignatures(
        LuaType type,
        ImmutableArray<LuaFunctionType>.Builder destination)
    {
        switch (type)
        {
            case LuaMetatableType metatable:
                if (TryGetMetamethodSignatures(metatable, "__call", out var metamethods))
                {
                    destination.AddRange(metamethods);
                }

                break;
            case LuaFunctionType function:
                destination.Add(function);
                break;
            case LuaOverloadType overload:
                destination.AddRange(overload.Signatures);
                break;
            case LuaCallableType callable:
                destination.AddRange(callable.Signatures);
                break;
            case LuaUnionType union:
                foreach (var member in union.Types)
                {
                    AddCallSignatures(member, destination);
                }

                break;
            case LuaClassType @class:
                var declaration = _types.Declarations.OfType<LuaClassDeclaration>()
                    .FirstOrDefault(item => string.Equals(
                        item.Name,
                        @class.Name,
                        StringComparison.Ordinal));
                if (declaration is not null)
                {
                    var substitutions = declaration.TypeParameters
                        .Select((parameter, index) => (parameter.Name, Type: index < @class.TypeArguments.Length
                            ? @class.TypeArguments[index]
                            : (LuaType)parameter))
                        .ToDictionary(
                            static pair => pair.Name,
                            static pair => pair.Type,
                            StringComparer.Ordinal);
                    destination.AddRange(declaration.CallSignatures.Select(signature =>
                        (LuaFunctionType)_relations.Substitute(signature, substitutions)));
                }

                break;
        }
    }

    private LuaFunctionType InstantiateFunction(
        LuaFunctionType function,
        ImmutableArray<LuaType> arguments,
        Lunil.Core.Text.TextSpan span)
    {
        if (function.TypeParameters.IsEmpty)
        {
            return function;
        }

        if (!_context.TryInstantiateGeneric(span))
        {
            return function with
            {
                Parameters = [.. function.Parameters.Select(item => item with { Type = LuaTypes.Unknown })],
                Returns = new LuaTypePack([], LuaTypes.Unknown),
            };
        }

        var substitutions = new Dictionary<string, LuaType>(StringComparer.Ordinal);
        for (var index = 0; index < Math.Min(function.Parameters.Length, arguments.Length); index++)
        {
            InferGenericArguments(function.Parameters[index].Type, arguments[index], substitutions);
        }

        foreach (var parameter in function.TypeParameters)
        {
            if (!substitutions.ContainsKey(parameter.Name))
            {
                substitutions[parameter.Name] = parameter.Constraint ?? LuaTypes.Unknown;
            }
        }

        return (LuaFunctionType)_relations.Substitute(function, substitutions);
    }

    private void InferGenericArguments(
        LuaType parameter,
        LuaType argument,
        Dictionary<string, LuaType> substitutions)
    {
        switch (parameter)
        {
            case LuaGenericParameterType generic:
                substitutions[generic.Name] = substitutions.TryGetValue(generic.Name, out var current)
                    ? _relations.Union(current, argument)
                    : argument;
                break;
            case LuaArrayType parameterArray when argument is LuaArrayType argumentArray:
                InferGenericArguments(parameterArray.ElementType, argumentArray.ElementType, substitutions);
                break;
            case LuaMapType parameterMap when argument is LuaMapType argumentMap:
                InferGenericArguments(parameterMap.KeyType, argumentMap.KeyType, substitutions);
                InferGenericArguments(parameterMap.ValueType, argumentMap.ValueType, substitutions);
                break;
            case LuaClassType parameterClass when argument is LuaClassType argumentClass &&
                parameterClass.Name == argumentClass.Name:
                foreach (var pair in parameterClass.TypeArguments.Zip(
                    argumentClass.TypeArguments,
                    static (first, second) => (First: first, Second: second)))
                {
                    InferGenericArguments(pair.First, pair.Second, substitutions);
                }

                break;
        }
    }

    private bool IsCallCompatible(
        LuaFunctionType function,
        ImmutableArray<LuaType> arguments)
    {
        var required = function.Parameters.Count(static item => !item.IsOptional && !item.IsVararg);
        if (arguments.Length < required ||
            arguments.Length > function.Parameters.Length &&
            !function.Parameters.Any(static item => item.IsVararg))
        {
            return false;
        }

        for (var index = 0; index < arguments.Length; index++)
        {
            var parameter = index < function.Parameters.Length
                ? function.Parameters[index]
                : function.Parameters.Last(static item => item.IsVararg);
            if (!_relations.IsAssignable(arguments[index], parameter.Type))
            {
                return false;
            }
        }

        return true;
    }

    private void CheckCall(
        LuaFunctionType function,
        ImmutableArray<LuaType> arguments,
        Lunil.Core.Text.TextSpan span)
    {
        var required = function.Parameters.Count(static item => !item.IsOptional && !item.IsVararg);
        if (arguments.Length < required ||
            arguments.Length > function.Parameters.Length &&
            !function.Parameters.Any(static item => item.IsVararg))
        {
            _context.AddDiagnostic(
                "LUA6006",
                span,
                $"Call supplies {arguments.Length} argument(s), but the selected signature expects " +
                $"{required}..{(function.Parameters.Any(static item => item.IsVararg) ? "many" : function.Parameters.Length)}.");
        }

        for (var index = 0; index < Math.Min(arguments.Length, function.Parameters.Length); index++)
        {
            CheckAssignable(
                arguments[index],
                function.Parameters[index].Type,
                span,
                $"call argument {index + 1}");
        }
    }

    private LuaType InferComposite(LuaSyntaxNode expression, FlowState state)
    {
        LuaType type = LuaTypes.Unknown;
        foreach (var node in expression.ChildNodes())
        {
            type = InferExpression(node, state);
        }

        return type;
    }

    private LuaTypePack InferVarargPack()
    {
        var parameter = _currentFunction?.Type.Parameters.LastOrDefault(static item => item.IsVararg);
        return parameter is null
            ? new LuaTypePack([], LuaTypes.Any)
            : new LuaTypePack([], parameter.Type);
    }

    private LuaType InferTypeTag(LuaType type)
    {
        if (type is LuaUnionType union)
        {
            return _relations.Union(union.Types.Select(InferTypeTag));
        }

        var name = type switch
        {
            LuaBooleanLiteralType => "boolean",
            LuaIntegerLiteralType or LuaFloatLiteralType => "number",
            LuaStringLiteralType => "string",
            _ => type.Kind switch
            {
                LuaTypeKind.Nil => "nil",
                LuaTypeKind.Boolean => "boolean",
                LuaTypeKind.Integer or LuaTypeKind.Float or LuaTypeKind.Number => "number",
                LuaTypeKind.String => "string",
                LuaTypeKind.Array or LuaTypeKind.Map or LuaTypeKind.StructuralTable or
                    LuaTypeKind.Table or LuaTypeKind.Class or LuaTypeKind.Metatable or
                    LuaTypeKind.Prototype => "table",
                LuaTypeKind.Function or LuaTypeKind.Overload or LuaTypeKind.Callable => "function",
                LuaTypeKind.Thread => "thread",
                LuaTypeKind.Userdata => "userdata",
                _ => null,
            },
        };
        return name is null
            ? LuaTypes.String
            : new LuaStringLiteralType(System.Text.Encoding.UTF8.GetBytes(name).ToImmutableArray());
    }

    private void AssignMember(LuaSyntaxNode target, LuaType value, FlowState state)
    {
        var baseExpression = target.ChildNodes().Single();
        var nameToken = target.ChildTokens().Last(static token => token.Kind == LuaTokenKind.Identifier);
        var name = GetTokenText(nameToken);
        var baseType = InferExpression(baseExpression, state);
        var hasVariable = TryGetVariableKey(baseExpression, out var key, out var symbol);
        var constraintType = hasVariable
            ? _declaredTypes.GetValueOrDefault(key, LuaTypes.Any)
            : baseType;
        var existing = _relations.FindField(constraintType, name);
        if (existing is null && baseType is LuaPrototypeType)
        {
            existing = _relations.FindField(baseType, name);
        }
        if (existing is not null)
        {
            CheckAssignable(value, existing.ValueType, target.Span, $"member '{name}'");
        }

        if (hasVariable)
        {
            LuaType next;
            if (name == "__index" && ReferenceEquals(baseType, value))
            {
                next = baseType is LuaPrototypeType prototype
                    ? prototype with { UsesSelfIndex = true }
                    : new LuaPrototypeType(
                        symbol.Name,
                        baseType,
                        GetPrototypeBaseTypes(baseType),
                        UsesSelfIndex: true);
            }
            else
            {
                next = AssignEffectiveMember(baseType, name, value, target.Span);
            }

            AssignVariable(key, symbol, next, target.Span, state);
            PropagateTableMutation(state, baseType, next, key);
            if (IsGlobalTableReference(baseExpression, out var environmentSymbol))
            {
                AssignVariable(VariableKey.Global(name), environmentSymbol, value, nameToken.Span, state);
            }
        }
    }

    private void AssignIndex(LuaSyntaxNode target, LuaType value, FlowState state)
    {
        var nodes = target.ChildNodes().ToArray();
        var baseType = InferExpression(nodes[0], state);
        var indexType = InferExpression(nodes[1], state);
        LuaType next = baseType;
        switch (baseType)
        {
            case LuaPrototypeType prototype when indexType is LuaStringLiteralType text:
                next = prototype with
                {
                    Shape = AddOrReplaceField(prototype.Shape, DecodeLiteral(text), value),
                };
                break;
            case LuaPrototypeType prototype:
                next = prototype with { IsPrecise = false };
                break;
            case LuaArrayType array:
                CheckAssignable(indexType, LuaTypes.Integer, nodes[1].Span, "array index");
                CheckAssignable(value, array.ElementType, target.Span, "array element");
                break;
            case LuaMapType map:
                CheckAssignable(indexType, map.KeyType, nodes[1].Span, "map index");
                CheckAssignable(value, map.ValueType, target.Span, "map value");
                break;
            case LuaStructuralTableType table when indexType is LuaStringLiteralType text:
                next = AddOrReplaceField(table, DecodeLiteral(text), value);
                break;
            case LuaStructuralTableType table:
                next = table with
                {
                    MapKeyType = table.MapKeyType is null
                        ? indexType
                        : _relations.Union(table.MapKeyType, indexType),
                    MapValueType = table.MapValueType is null
                        ? value
                        : _relations.Union(table.MapValueType, value),
                };
                break;
            case LuaPrimitiveType primitive when primitive.Kind is LuaTypeKind.Any or LuaTypeKind.Table:
                next = new LuaMapType(indexType, value);
                break;
        }

        if (TryGetVariableKey(nodes[0], out var key, out var symbol))
        {
            AssignVariable(key, symbol, next, target.Span, state);
            PropagateTableMutation(state, baseType, next, key);
            if (indexType is LuaStringLiteralType text &&
                IsGlobalTableReference(nodes[0], out var environmentSymbol))
            {
                AssignVariable(VariableKey.Global(DecodeLiteral(text)), environmentSymbol, value, target.Span, state);
            }
        }
    }

    private static LuaType AddOrReplaceField(LuaType type, string name, LuaType value)
    {
        if (type is LuaPrototypeType prototype)
        {
            return prototype with { Shape = AddOrReplaceField(prototype.Shape, name, value) };
        }

        var table = type as LuaStructuralTableType ?? new LuaStructuralTableType([], IsOpen: true);
        var items = table.Fields.Where(item => !string.Equals(
            item.Name,
            name,
            StringComparison.Ordinal)).ToImmutableArray();
        return table with
        {
            Fields = [.. items, new LuaTableField(name, null, value, false)],
        };
    }

    private bool TryInferUnaryOperator(
        LuaType operand,
        LuaTokenKind operation,
        Lunil.Core.Text.TextSpan span,
        out LuaType result)
    {
        var name = operation switch
        {
            LuaTokenKind.Minus => "unm",
            LuaTokenKind.Tilde => "bnot",
            LuaTokenKind.Length => "len",
            _ => null,
        };
        return TryInferOperator(operand, LuaTypes.Nil, name, span, unary: true, out result);
    }

    private bool TryInferBinaryOperator(
        LuaType left,
        LuaType right,
        LuaTokenKind operation,
        Lunil.Core.Text.TextSpan span,
        out LuaType result)
    {
        var name = operation switch
        {
            LuaTokenKind.Plus => "add",
            LuaTokenKind.Minus => "sub",
            LuaTokenKind.Star => "mul",
            LuaTokenKind.Slash => "div",
            LuaTokenKind.FloorDivide => "idiv",
            LuaTokenKind.Percent => "mod",
            LuaTokenKind.Caret => "pow",
            LuaTokenKind.Ampersand => "band",
            LuaTokenKind.Pipe => "bor",
            LuaTokenKind.Tilde => "bxor",
            LuaTokenKind.ShiftLeft => "shl",
            LuaTokenKind.ShiftRight => "shr",
            LuaTokenKind.Concatenate => "concat",
            LuaTokenKind.Equal or LuaTokenKind.NotEqual => "eq",
            LuaTokenKind.LessThan or LuaTokenKind.GreaterThan => "lt",
            LuaTokenKind.LessThanOrEqual or LuaTokenKind.GreaterThanOrEqual => "le",
            _ => null,
        };
        return TryInferOperator(left, right, name, span, unary: false, out result) ||
            TryInferOperator(right, left, name, span, unary: false, out result);
    }

    private bool TryInferOperator(
        LuaType owner,
        LuaType operand,
        string? name,
        Lunil.Core.Text.TextSpan span,
        bool unary,
        out LuaType result)
    {
        result = LuaTypes.Unknown;
        if (name is null)
        {
            return false;
        }

        if (owner is LuaAliasType alias)
        {
            owner = alias.Target;
        }

        if (owner is LuaMetatableType &&
            !(name == "len" && _semantics.LanguageVersion == LuaLanguageVersion.Lua51) &&
            TryGetMetamethodSignatures(owner, "__" + name, out var metamethods) &&
            !metamethods.IsEmpty)
        {
            var metamethodSignature = metamethods[0];
            if (!unary && metamethodSignature.Parameters.Length > 1)
            {
                CheckAssignable(operand, metamethodSignature.Parameters[1].Type, span, $"operator '{name}' operand");
            }

            result = metamethodSignature.Returns.GetElementOrNil(0);
            return true;
        }

        if (owner is not LuaClassType @class)
        {
            return false;
        }

        var declaration = _types.Declarations.OfType<LuaClassDeclaration>()
            .FirstOrDefault(item => string.Equals(item.Name, @class.Name, StringComparison.Ordinal));
        if (declaration is null || !declaration.Operators.TryGetValue(name, out var signature))
        {
            return false;
        }

        var substitutions = declaration.TypeParameters
            .Select((parameter, index) => (parameter.Name, Type: index < @class.TypeArguments.Length
                ? @class.TypeArguments[index]
                : (LuaType)parameter))
            .ToDictionary(static pair => pair.Name, static pair => pair.Type, StringComparer.Ordinal);
        signature = (LuaFunctionType)_relations.Substitute(signature, substitutions);
        if (!unary && !signature.Parameters.IsEmpty)
        {
            CheckAssignable(operand, signature.Parameters[0].Type, span, $"operator '{name}' operand");
        }

        result = signature.Returns.GetElementOrNil(0);
        return true;
    }

    private bool TryInferEffectiveMember(LuaType target, string name, out LuaType result)
    {
        var raw = _relations.FindField(target, name);
        if (raw is not null)
        {
            result = raw.IsOptional
                ? _relations.Union(raw.ValueType, LuaTypes.Nil)
                : raw.ValueType;
            return true;
        }

        return TryInferEffectiveMember(
            target,
            name,
            new HashSet<LuaType>(LuaTypeReferenceComparer.Instance),
            depth: 0,
            out result);
    }

    private bool TryInferEffectiveMember(
        LuaType target,
        string name,
        HashSet<LuaType> visiting,
        int depth,
        out LuaType result)
    {
        result = LuaTypes.Unknown;
        if (depth >= MaximumMetatableLookupDepth || !visiting.Add(target))
        {
            return false;
        }

        try
        {
            if (target is LuaUnionType union)
            {
                var resolved = union.Types
                    .Select(member => TryInferEffectiveMember(member, name, visiting, depth + 1, out var item)
                        ? item
                        : LuaTypes.Unknown)
                    .ToArray();
                result = _relations.Union(resolved);
                return resolved.Any(static item => item.Kind != LuaTypeKind.Unknown);
            }

            if (target is not LuaMetatableType metatable)
            {
                return false;
            }

            if (!metatable.IsPrecise)
            {
                result = LuaTypes.Any;
                return true;
            }

            var raw = _relations.FindField(metatable.BaseType, name);
            if (raw is not null)
            {
                result = raw.IsOptional
                    ? _relations.Union(raw.ValueType, LuaTypes.Nil)
                    : raw.ValueType;
                return true;
            }

            var index = GetMetatableField(metatable.MetatableType, "__index");
            if (index is null)
            {
                if (!metatable.IsPrecise)
                {
                    result = LuaTypes.Any;
                    return true;
                }

                return false;
            }

            if (TryGetFunctionReturns(index.ValueType, out result))
            {
                return true;
            }

            var inherited = _relations.FindField(index.ValueType, name);
            if (inherited is not null)
            {
                result = inherited.IsOptional
                    ? _relations.Union(inherited.ValueType, LuaTypes.Nil)
                    : inherited.ValueType;
                return true;
            }

            if (TryInferEffectiveMember(index.ValueType, name, visiting, depth + 1, out result))
            {
                return true;
            }

            if (metatable.BaseType is LuaStructuralTableType { IsOpen: true })
            {
                result = LuaTypes.Any;
                return true;
            }

            if (index.ValueType.Kind is LuaTypeKind.Any or LuaTypeKind.Unknown)
            {
                result = index.ValueType.Kind == LuaTypeKind.Any ? LuaTypes.Any : LuaTypes.Unknown;
                return true;
            }

            return false;
        }
        finally
        {
            visiting.Remove(target);
        }
    }

    private bool TryInferMetatableIndex(
        LuaMetatableType metatable,
        LuaType index,
        out LuaType result)
    {
        if (!metatable.IsPrecise)
        {
            result = LuaTypes.Any;
            return true;
        }

        if (TryInferRawIndex(metatable.BaseType, index, out result))
        {
            return true;
        }

        var metamethod = GetMetatableField(metatable.MetatableType, "__index");
        if (metamethod is not null)
        {
            if (TryGetFunctionReturns(metamethod.ValueType, out result))
            {
                return true;
            }

            if (TryInferRawIndex(metamethod.ValueType, index, out result))
            {
                return true;
            }

            if (index is LuaStringLiteralType literal &&
                TryInferEffectiveMember(metamethod.ValueType, DecodeLiteral(literal), out result))
            {
                return true;
            }
        }

        if (!metatable.IsPrecise)
        {
            result = LuaTypes.Any;
            return true;
        }

        result = LuaTypes.Unknown;
        return false;
    }

    private bool TryInferRawIndex(LuaType target, LuaType index, out LuaType result)
    {
        if (target is LuaMetatableType metatable)
        {
            target = metatable.BaseType;
        }

        switch (target)
        {
            case LuaArrayType array when IsIntegerLike(index):
                result = array.ElementType;
                return true;
            case LuaMapType map when _relations.IsAssignable(index, map.KeyType):
                result = map.ValueType;
                return true;
            case LuaStructuralTableType table when index is LuaStringLiteralType text:
                var field = table.Fields.LastOrDefault(candidate =>
                    string.Equals(candidate.Name, DecodeLiteral(text), StringComparison.Ordinal));
                if (field is not null)
                {
                    result = field.IsOptional
                        ? _relations.Union(field.ValueType, LuaTypes.Nil)
                        : field.ValueType;
                    return true;
                }

                break;
            case LuaStructuralTableType table when IsIntegerLike(index) && table.ArrayElementType is not null:
                result = table.ArrayElementType;
                return true;
            case LuaStructuralTableType table when table.MapKeyType is not null && table.MapValueType is not null &&
                _relations.IsAssignable(index, table.MapKeyType):
                result = table.MapValueType;
                return true;
            case LuaPrimitiveType primitive when primitive.Kind == LuaTypeKind.Any:
                result = LuaTypes.Any;
                return true;
        }

        result = LuaTypes.Unknown;
        return false;
    }

    private static bool TryGetFunctionReturns(LuaType type, out LuaType result)
    {
        switch (type)
        {
            case LuaFunctionType function:
                result = function.Returns.GetElementOrNil(0);
                return true;
            case LuaOverloadType overload when !overload.Signatures.IsEmpty:
                result = overload.Signatures[0].Returns.GetElementOrNil(0);
                return true;
            case LuaCallableType callable when !callable.Signatures.IsEmpty:
                result = callable.Signatures[0].Returns.GetElementOrNil(0);
                return true;
            default:
                result = LuaTypes.Unknown;
                return false;
        }
    }

    private bool TryGetMetamethodSignatures(
        LuaType type,
        string name,
        out ImmutableArray<LuaFunctionType> signatures)
    {
        if (type is LuaMetatableType metatable)
        {
            var field = GetMetatableField(metatable.MetatableType, name);
            signatures = field?.ValueType switch
            {
                LuaFunctionType function => [function],
                LuaOverloadType overload => overload.Signatures,
                LuaCallableType callable => callable.Signatures,
                _ => [],
            };
            return !signatures.IsEmpty;
        }

        signatures = [];
        return false;
    }

    private LuaTypePack InferSetMetatable(
        Lunil.Core.Text.TextSpan callSpan,
        LuaSyntaxNode[] arguments,
        FlowState state)
    {
        if (arguments.Length == 0)
        {
            return new LuaTypePack([LuaTypes.Any]);
        }

        var original = InferExpression(arguments[0], state);
        var metatable = arguments.Length > 1
            ? InferExpression(arguments[1], state)
            : LuaTypes.Nil;
        var precise = original.Kind is not (LuaTypeKind.Any or LuaTypeKind.Unknown) &&
            metatable.Kind is not (LuaTypeKind.Any or LuaTypeKind.Unknown);
        var effective = new LuaMetatableType(
            original is LuaMetatableType existing ? existing.BaseType : original,
            metatable,
            precise);
        LuaType next = effective;
        _metatableFacts.Add(new LuaMetatableFact(
            callSpan,
            effective.BaseType,
            metatable,
            effective,
            precise));
        if (TryGetVariableKey(arguments[0], out var key, out var symbol))
        {
            AssignVariable(key, symbol, next, arguments[0].Span, state);
            PropagateTableMutation(state, original, next, key);
        }

        return new LuaTypePack([next]);
    }

    private LuaTypePack InferGetMetatable(LuaSyntaxNode[] arguments, FlowState state)
    {
        if (arguments.Length == 0)
        {
            return new LuaTypePack([LuaTypes.Nil]);
        }

        var target = InferExpression(arguments[0], state);
        if (target is not LuaMetatableType metatable)
        {
            return new LuaTypePack([target.Kind == LuaTypeKind.Any ? LuaTypes.Any : LuaTypes.Nil]);
        }

        var protection = GetMetatableField(metatable.MetatableType, "__metatable");
        return new LuaTypePack([protection?.ValueType ?? metatable.MetatableType]);
    }

    private LuaTypePack InferRawGet(LuaSyntaxNode[] arguments, FlowState state)
    {
        if (arguments.Length < 2)
        {
            foreach (var argument in arguments)
            {
                _ = InferExpression(argument, state);
            }

            return new LuaTypePack([LuaTypes.Any]);
        }

        var target = InferExpression(arguments[0], state);
        var index = InferExpression(arguments[1], state);
        return new LuaTypePack([
            TryInferRawIndex(target, index, out var value) ? value : LuaTypes.Nil,
        ]);
    }

    private LuaTypePack InferRawSet(LuaSyntaxNode[] arguments, FlowState state)
    {
        if (arguments.Length < 3)
        {
            foreach (var argument in arguments)
            {
                _ = InferExpression(argument, state);
            }

            return new LuaTypePack([LuaTypes.Any]);
        }

        var original = InferExpression(arguments[0], state);
        var index = InferExpression(arguments[1], state);
        var value = InferExpression(arguments[2], state);
        var next = AssignRawIndexType(original, index, value);
        if (TryGetVariableKey(arguments[0], out var key, out var symbol))
        {
            AssignVariable(key, symbol, next, arguments[0].Span, state);
            PropagateTableMutation(state, original, next, key);
        }

        return new LuaTypePack([next]);
    }

    private LuaType AssignEffectiveMember(
        LuaType type,
        string name,
        LuaType value,
        Lunil.Core.Text.TextSpan span)
    {
        if (type is LuaPrototypeType prototype)
        {
            return prototype with { Shape = AddOrReplaceField(prototype.Shape, name, value) };
        }

        if (type is not LuaMetatableType metatable)
        {
            return AddOrReplaceField(type, name, value);
        }

        if (_relations.FindField(metatable.BaseType, name) is not null)
        {
            return metatable with { BaseType = AddOrReplaceField(metatable.BaseType, name, value) };
        }

        var newIndex = GetMetatableField(metatable.MetatableType, "__newindex");
        if (newIndex?.ValueType is LuaFunctionType function)
        {
            if (function.Parameters.Length > 2)
            {
                CheckAssignable(value, function.Parameters[2].Type, span, "__newindex value");
            }

            return metatable;
        }

        if (newIndex is not null && newIndex.ValueType.Kind is
            LuaTypeKind.StructuralTable or LuaTypeKind.Map or LuaTypeKind.Metatable)
        {
            var updated = AddOrReplaceField(newIndex.ValueType, name, value);
            return metatable with
            {
                MetatableType = AddOrReplaceField(metatable.MetatableType, "__newindex", updated),
            };
        }

        return metatable with { BaseType = AddOrReplaceField(metatable.BaseType, name, value) };
    }

    private LuaType AssignRawIndexType(LuaType type, LuaType index, LuaType value)
    {
        if (type is LuaMetatableType metatable)
        {
            return metatable with { BaseType = AssignRawIndexType(metatable.BaseType, index, value) };
        }

        if (index is LuaStringLiteralType text)
        {
            return AddOrReplaceField(type, DecodeLiteral(text), value);
        }

        return type switch
        {
            LuaStructuralTableType table => table with
            {
                MapKeyType = table.MapKeyType is null ? index : _relations.Union(table.MapKeyType, index),
                MapValueType = table.MapValueType is null ? value : _relations.Union(table.MapValueType, value),
            },
            LuaArrayType array when IsIntegerLike(index) =>
                new LuaArrayType(_relations.Union(array.ElementType, value)),
            LuaMapType map => new LuaMapType(
                _relations.Union(map.KeyType, index),
                _relations.Union(map.ValueType, value)),
            _ => new LuaMapType(index, value),
        };
    }

    private void PropagateTableMutation(
        FlowState state,
        LuaType previous,
        LuaType next,
        VariableKey assignedKey)
    {
        if (ReferenceEquals(previous, next))
        {
            return;
        }

        // Only composite, shareable shapes participate in table mutation. Primitive
        // singletons such as any/unknown/table/number are shared by every untyped
        // variable; replacing them by reference would conflate unrelated variables.
        if (previous.Kind is not (LuaTypeKind.StructuralTable or LuaTypeKind.Prototype or
            LuaTypeKind.Metatable or LuaTypeKind.Map or LuaTypeKind.Array or
            LuaTypeKind.Union or LuaTypeKind.Class or LuaTypeKind.Callable or
            LuaTypeKind.Overload))
        {
            return;
        }

        foreach (var key in state.Types.Keys.ToArray())
        {
            if (key == assignedKey)
            {
                continue;
            }

            var current = state.Types[key];
            var replaced = ReplaceTypeReference(current, previous, next, depth: 0);
            if (ReferenceEquals(current, replaced))
            {
                continue;
            }

            state.Types[key] = replaced;
            if (!key.IsGlobal)
            {
                var symbol = _semantics.Symbols.FirstOrDefault(candidate => candidate.Id == key.SymbolId);
                if (symbol is not null)
                {
                    RecordSymbolInference(symbol, replaced);
                }
            }
        }
    }

    private LuaType ReplaceTypeReference(LuaType current, LuaType previous, LuaType next, int depth)
    {
        if (ReferenceEquals(current, previous))
        {
            return next;
        }

        if (depth >= MaximumMetatableLookupDepth)
        {
            return current is LuaMetatableType metatable ? metatable with { IsPrecise = false } : current;
        }

        return current switch
        {
            LuaMetatableType metatable => metatable with
            {
                BaseType = ReplaceTypeReference(metatable.BaseType, previous, next, depth + 1),
                MetatableType = ReplaceTypeReference(metatable.MetatableType, previous, next, depth + 1),
            },
            LuaPrototypeType prototype => prototype with
            {
                Shape = ReplaceTypeReference(prototype.Shape, previous, next, depth + 1),
                BaseTypes = [.. prototype.BaseTypes.Select(item =>
                    ReplaceTypeReference(item, previous, next, depth + 1))],
            },
            LuaUnionType union => _relations.Union(union.Types.Select(member =>
                ReplaceTypeReference(member, previous, next, depth + 1))),
            LuaStructuralTableType table => table with
            {
                Fields = [.. table.Fields.Select(field => field with
                {
                    KeyType = field.KeyType is null
                        ? null
                        : ReplaceTypeReference(field.KeyType, previous, next, depth + 1),
                    ValueType = ReplaceTypeReference(field.ValueType, previous, next, depth + 1),
                })],
            },
            LuaFunctionType function => function with
            {
                Parameters = [.. function.Parameters.Select(parameter => parameter with
                {
                    Type = ReplaceTypeReference(parameter.Type, previous, next, depth + 1),
                })],
                Returns = (LuaTypePack)ReplaceTypeReference(
                    function.Returns,
                    previous,
                    next,
                    depth + 1),
            },
            LuaTypePack pack => pack with
            {
                Head = [.. pack.Head.Select(item =>
                    ReplaceTypeReference(item, previous, next, depth + 1))],
                VariadicType = pack.VariadicType is null
                    ? null
                    : ReplaceTypeReference(pack.VariadicType, previous, next, depth + 1),
            },
            LuaOverloadType overload => overload with
            {
                Signatures = [.. overload.Signatures.Select(signature =>
                    (LuaFunctionType)ReplaceTypeReference(signature, previous, next, depth + 1))],
            },
            _ => current,
        };
    }

    private void InvalidateEscapedMetatables(IEnumerable<LuaSyntaxNode> arguments, FlowState state)
    {
        foreach (var argument in arguments)
        {
            if (!TryGetVariableKey(argument, out var key, out var symbol) ||
                !state.Types.TryGetValue(key, out var current))
            {
                continue;
            }

            var widened = current switch
            {
                LuaMetatableType { IsPrecise: true } metatable =>
                    (LuaType)(metatable with { IsPrecise = false }),
                LuaPrototypeType { IsPrecise: true } prototype =>
                    prototype with { IsPrecise = false },
                _ => current,
            };
            if (ReferenceEquals(current, widened))
            {
                continue;
            }

            AssignVariable(key, symbol, widened, argument.Span, state);
            PropagateTableMutation(state, current, widened, key);
        }
    }

    private LuaType ApplyPathNarrowing(
        LuaSyntaxNode target,
        LuaType inferred,
        FlowState state,
        Lunil.Core.Text.TextSpan accessSpan)
    {
        if (!TryGetAccessPath(target, out var path))
        {
            return inferred;
        }

        var wasNarrowed = state.PathTypes.TryGetValue(path, out var narrowed);
        var input = wasNarrowed ? narrowed! : inferred;
        var containsNil = input.Kind == LuaTypeKind.Nil ||
            input is LuaUnionType union && union.Types.Any(static item => item.Kind == LuaTypeKind.Nil);
        var result = containsNil ? _relations.RemoveNil(input) : input;
        if (containsNil)
        {
            _context.AddDiagnostic(
                "LUA6020",
                accessSpan,
                $"Path '{path.Value}' may be nil before this access.");
            if (result.Kind == LuaTypeKind.Never)
            {
                result = LuaTypes.Unknown;
            }
        }

        _nilPaths.Add(new LuaNilPathFact(
            accessSpan,
            path.Value,
            path.HopCount,
            input,
            result,
            wasNarrowed));
        return result;
    }

    private void RecordPathType(LuaSyntaxNode expression, LuaType type, FlowState state)
    {
        if (TryGetAccessPath(expression, out var path))
        {
            state.PathTypes.TryAdd(path, type);
        }
    }

    private LuaTableField? GetMetatableField(LuaType metatable, string name)
    {
        if (metatable is LuaPrototypeType prototype)
        {
            if (_latestPrototypes.TryGetValue(prototype.Name, out var latest))
            {
                prototype = latest;
                metatable = latest;
            }

            if (name == "__index" && prototype.UsesSelfIndex)
            {
                return new LuaTableField("__index", null, prototype, false, true);
            }
        }

        return _relations.FindField(metatable, name);
    }

    private ImmutableArray<LuaType> GetPrototypeBaseTypes(LuaType type)
    {
        if (type is not LuaMetatableType metatable)
        {
            return [];
        }

        var index = GetMetatableField(metatable.MetatableType, "__index");
        return index is null ? [] : [index.ValueType];
    }
}
