using System.Collections.Immutable;
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
        var token = expression.ChildTokens().Single();
        return token.Value switch
        {
            LuaIntegerTokenValue integer => new LuaIntegerLiteralType(integer.Integer),
            LuaFloatTokenValue number => new LuaFloatLiteralType(number.Float),
            _ => LuaTypes.Number,
        };
    }

    private static LuaType InferStringLiteral(LuaSyntaxNode expression)
    {
        var token = expression.ChildTokens().Single();
        return token.Value is LuaStringTokenValue text
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
        var nameToken = expression.ChildTokens().Last(static token =>
            token.Kind == LuaTokenKind.Identifier);
        var name = GetTokenText(nameToken);
        return InferMemberType(targetType, name, expression.Span);
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

        var item = _relations.FindField(target, name);
        if (item is not null)
        {
            return item.IsOptional
                ? _relations.Union(item.ValueType, LuaTypes.Nil)
                : item.ValueType;
        }

        _context.AddDiagnostic(
            "LUA6007",
            span,
            $"Type '{target.DisplayName}' has no known member '{name}'.");
        return LuaTypes.Unknown;
    }

    private LuaType InferIndex(LuaSyntaxNode expression, FlowState state)
    {
        var nodes = expression.ChildNodes().ToArray();
        var target = InferExpression(nodes[0], state);
        var index = InferExpression(nodes[1], state);
        if (target.Kind == LuaTypeKind.Any)
        {
            return LuaTypes.Any;
        }

        if (target is LuaArrayType array)
        {
            CheckAssignable(index, LuaTypes.Integer, nodes[1].Span, "array index");
            return array.ElementType;
        }

        if (target is LuaMapType map)
        {
            CheckAssignable(index, map.KeyType, nodes[1].Span, "map index");
            return map.ValueType;
        }

        if (target is LuaStructuralTableType table)
        {
            if (index is LuaStringLiteralType text)
            {
                var name = DecodeLiteral(text);
                var item = table.Fields.LastOrDefault(candidate =>
                    string.Equals(candidate.Name, name, StringComparison.Ordinal));
                if (item is not null)
                {
                    return item.IsOptional
                        ? _relations.Union(item.ValueType, LuaTypes.Nil)
                        : item.ValueType;
                }
            }

            if (IsIntegerLike(index) && table.ArrayElementType is not null)
            {
                return table.ArrayElementType;
            }

            if (table.MapKeyType is not null && table.MapValueType is not null)
            {
                CheckAssignable(index, table.MapKeyType, nodes[1].Span, "table index");
                return table.MapValueType;
            }
        }

        if (target is LuaUnionType union)
        {
            return _relations.Union(union.Types.Select(member => InferIndexedMember(
                member,
                index,
                nodes[1].Span)));
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
        if (TryGetCalledGlobalIdentifier(expression, out var calledName))
        {
            var special = InferSpecialCall(expression, calledName, state);
            if (special is not null)
            {
                return special;
            }
        }

        var nodes = expression.ChildNodes().ToArray();
        LuaType callee;
        var arguments = GetCallArguments(expression).ToList();
        if (expression.Kind == LuaSyntaxKind.MethodCallExpression)
        {
            var receiver = nodes[0];
            var receiverType = InferExpression(receiver, state);
            var methodToken = expression.ChildTokens().Last(static token =>
                token.Kind == LuaTokenKind.Identifier);
            callee = InferMemberType(receiverType, GetTokenText(methodToken), methodToken.Span);
            arguments.Insert(0, receiver);
        }
        else
        {
            callee = InferExpression(nodes[0], state);
        }

        var argumentTypes = arguments.Select(argument => InferExpression(argument, state)).ToImmutableArray();
        var signatures = GetCallSignatures(callee);
        if (signatures.IsEmpty)
        {
            if (callee.Kind is LuaTypeKind.Any or LuaTypeKind.Unknown or LuaTypeKind.Function)
            {
                return new LuaTypePack([], LuaTypes.Any);
            }

            _context.AddDiagnostic(
                "LUA6004",
                expression.Span,
                $"Value of type '{callee.DisplayName}' is not callable.");
            return new LuaTypePack([LuaTypes.Unknown]);
        }

        var instantiated = signatures
            .Select(signature => InstantiateFunction(signature, argumentTypes, expression.Span))
            .ToArray();
        var selected = instantiated.FirstOrDefault(signature =>
            IsCallCompatible(signature, argumentTypes)) ?? instantiated[0];
        CheckCall(selected, argumentTypes, expression.Span);
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
                    _environment.ModuleTypes.TryGetValue(
                        DecodeLiteral(moduleName),
                        out var moduleType))
                {
                    return new LuaTypePack([moduleType]);
                }

                return new LuaTypePack([LuaTypes.Any]);
            case "setmetatable":
                return new LuaTypePack([
                    arguments.Length == 0 ? LuaTypes.Any : InferExpression(arguments[0], state),
                ]);
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
                foreach (var pair in parameterClass.TypeArguments.Zip(argumentClass.TypeArguments))
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
                    LuaTypeKind.Table or LuaTypeKind.Class => "table",
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
        if (existing is not null)
        {
            CheckAssignable(value, existing.ValueType, target.Span, $"member '{name}'");
        }

        if (hasVariable)
        {
            var next = AddOrReplaceField(baseType, name, value);
            AssignVariable(key, symbol, next, target.Span, state);
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
        }
    }

    private static LuaType AddOrReplaceField(LuaType type, string name, LuaType value)
    {
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
}
