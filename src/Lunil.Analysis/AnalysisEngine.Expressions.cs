using System.Collections.Immutable;
using Lunil.Core;
using Lunil.Core.Text;
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
                var initial = state.TypeOf(
                    key,
                    _declaredTypes.GetValueOrDefault(key, LuaTypes.Any));
                cell = new UpvalueCellState(reference.Symbol, initial);
                _upvalueCells.Add(reference.Symbol.Id, cell);
            }

            cell.Readers.Add(_currentFunction?.FunctionId ?? 0);
            state.SetType(key, cell.Type);
        }
        if (!key.IsGlobal && !state.IsAssigned(key))
        {
            _context.AddDiagnostic(
                "LUA6008",
                token.Span,
                $"Local '{reference.Name}' may be read before an explicit assignment.");
        }

        if (state.TryGetType(key, out var type))
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

        // An any/unknown operand can carry an invisible metamethod and can produce any
        // result: claiming `number` would poison every later member access on it.
        var dynamicOperand = left.Kind is LuaTypeKind.Any or LuaTypeKind.Unknown ||
            rightType.Kind is LuaTypeKind.Any or LuaTypeKind.Unknown;
        return operation switch
        {
            LuaTokenKind.Equal or LuaTokenKind.NotEqual => LuaTypes.Boolean,
            LuaTokenKind.LessThan or LuaTokenKind.LessThanOrEqual or
            LuaTokenKind.GreaterThan or LuaTokenKind.GreaterThanOrEqual when
                !dynamicOperand && !CheckComparableOperands(left, rightType, expression.Span) =>
                    LuaTypes.Boolean,
            LuaTokenKind.LessThan or LuaTokenKind.LessThanOrEqual or
            LuaTokenKind.GreaterThan or LuaTokenKind.GreaterThanOrEqual => LuaTypes.Boolean,
            LuaTokenKind.Concatenate when dynamicOperand => LuaTypes.Any,
            LuaTokenKind.Concatenate when CheckConcatenationOperand(left, nodes[0].Span) &
                CheckConcatenationOperand(rightType, nodes[1].Span) => LuaTypes.String,
            LuaTokenKind.Ampersand or LuaTokenKind.Pipe or LuaTokenKind.Tilde or
            LuaTokenKind.ShiftLeft or LuaTokenKind.ShiftRight when dynamicOperand => LuaTypes.Any,
            LuaTokenKind.Ampersand or LuaTokenKind.Pipe or LuaTokenKind.Tilde or
            LuaTokenKind.ShiftLeft or LuaTokenKind.ShiftRight when
                CheckIntegerOperand(left, nodes[0].Span) &
                CheckIntegerOperand(rightType, nodes[1].Span) => LuaTypes.Integer,
            LuaTokenKind.Plus or LuaTokenKind.Minus or LuaTokenKind.Star or
            LuaTokenKind.Percent or LuaTokenKind.FloorDivide when dynamicOperand => LuaTypes.Any,
            LuaTokenKind.Plus or LuaTokenKind.Minus or LuaTokenKind.Star or
            LuaTokenKind.Percent or LuaTokenKind.FloorDivide when
                CheckNumericOperand(left, nodes[0].Span) &
                CheckNumericOperand(rightType, nodes[1].Span) =>
                    IsIntegerLike(left) && IsIntegerLike(rightType)
                        ? LuaTypes.Integer
                        : LuaTypes.Number,
            LuaTokenKind.Slash or LuaTokenKind.Caret when dynamicOperand => LuaTypes.Any,
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

        void AddOrReplaceNamedMember(string name, LuaType value)
        {
            for (var index = 0; index < members.Count; index++)
            {
                if (!string.Equals(members[index].Name, name, StringComparison.Ordinal))
                {
                    continue;
                }

                members[index] = new LuaTableField(name, null, value, false);
                return;
            }

            if (members.Count < MaximumStructuralTableFieldGrowth)
            {
                members.Add(new LuaTableField(name, null, value, false));
            }
        }

        foreach (var item in expression.ChildNodes().Where(static node =>
                     node.Kind == LuaSyntaxKind.TableField))
        {
            // One pass over the field's children classifies tokens and nodes without
            // the per-field array materialization the previous form paid; generated
            // data literals carry tens of thousands of fields.
            LuaSyntaxToken? firstToken = null;
            LuaSyntaxToken? secondToken = null;
            LuaSyntaxNode? firstNode = null;
            LuaSyntaxNode? lastNode = null;
            var nodeCount = 0;
            foreach (var child in item.Children)
            {
                if (child.IsToken)
                {
                    if (firstToken is null)
                    {
                        firstToken = child.Token;
                    }
                    else if (secondToken is null)
                    {
                        secondToken = child.Token;
                    }
                }
                else if (child.Node is { } childNode)
                {
                    firstNode ??= childNode;
                    lastNode = childNode;
                    nodeCount++;
                }
            }

            if (firstToken?.Kind == LuaTokenKind.OpenBracket && nodeCount >= 2)
            {
                var key = InferExpression(firstNode!, state);
                var value = InferExpressionPack(lastNode!, state).GetElementOrNil(0);
                if (key is LuaStringLiteralType text)
                {
                    AddOrReplaceNamedMember(DecodeLiteral(text), value);
                }
                else if (members.Count < MaximumStructuralTableFieldGrowth)
                {
                    members.Add(new LuaTableField(null, key, value, false));
                }

                mapKeys.Add(key);
                mapValues.Add(value);
            }
            else if (firstToken?.Kind == LuaTokenKind.Identifier &&
                secondToken?.Kind == LuaTokenKind.Assign &&
                nodeCount == 1)
            {
                var name = GetTokenText(firstToken);
                var value = InferExpressionPack(firstNode!, state).GetElementOrNil(0);
                AddOrReplaceNamedMember(name, value);
            }
            else if (nodeCount == 1)
            {
                var value = InferExpressionPack(firstNode!, state).GetElementOrNil(0);
                arrayTypes.Add(value);
                if (members.Count < MaximumStructuralTableFieldGrowth)
                {
                    members.Add(new LuaTableField(null, null, value, false));
                }
            }
        }

        return new LuaStructuralTableType(
            members.ToImmutable(),
            FoldTypes(arrayTypes),
            FoldTypes(mapKeys),
            FoldTypes(mapValues));
    }

    /// <summary>
    /// Folds collected types through pairwise unions instead of one bulk union:
    /// the union normalizes to its member cap on every step, so the fold stays
    /// linear for generated data literals with tens of thousands of entries
    /// while producing the same normalized result.
    /// </summary>
    private LuaType? FoldTypes(List<LuaType> types) =>
        types.Count == 0 ? null : types.Aggregate((left, right) => _relations.Union(left, right));

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

        // Annotated class fields and runtime exports are authoritative for class values
        // and instances before the metatable walk degrades to any/unknown: fields live
        // in base-class annotations and constructors this document cannot see. A member
        // that resolves to unknown/any defers to the walk (local runtime shapes may know
        // more than the workspace snapshot did).
        if (target is LuaMetatableType or LuaClassType or LuaPrototypeType &&
            TryGetExternalClassMember(target, name, out var annotated) &&
            annotated.Kind is not (LuaTypeKind.Unknown or LuaTypeKind.Any))
        {
            return annotated;
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
            // The instance's runtime fields live in constructors this document cannot
            // see, but annotated class fields (including inherited ones) are still
            // authoritative for it; everything else stays permissive.
            return TryGetExternalClassMember(target, name, out var annotatedSelf)
                ? annotatedSelf
                : LuaTypes.Any;
        }

        if (target is LuaClassType emptyClass && IsEmptyClass(emptyClass))
        {
            return LuaTypes.Any;
        }

        if (TryGetExternalClassMember(target, name, out var external))
        {
            return external;
        }

        // A class object is a table with a metatable chain (and mixins); annotations not
        // covering a member is the norm for class libraries, not an error. An unbound
        // generic parameter's members are unresolved for this instantiation, and a map
        // accepts runtime-attached members.
        if (target is LuaPrototypeType or LuaGenericParameterType or LuaMapType)
        {
            return LuaTypes.Unknown;
        }

        // An instance of a known class can carry fields attached at runtime
        // (`self.speed = options.speed` in a constructor the local document cannot see).
        if (target is LuaMetatableType { MetatableType: LuaPrototypeType instanceClass } &&
            (_types.Declarations.OfType<LuaClassDeclaration>().Any(declaration =>
                 string.Equals(declaration.Name, instanceClass.Name, StringComparison.Ordinal)) ||
             _environment.ExternalClassMembers.ContainsKey(instanceClass.Name)))
        {
            return LuaTypes.Unknown;
        }

        _context.AddDiagnostic(
            "LUA6007",
            span,
            $"Type '{target.DisplayName}' has no known member '{name}'.");
        return LuaTypes.Unknown;
    }

    /// <summary>
    /// Resolves a member through the workspace's runtime knowledge of the receiver's class:
    /// annotation-declared classes expose members their declaring module writes at runtime
    /// (extend/new/mixin, metamethods), which the local document cannot see.
    /// </summary>
    private bool TryGetExternalClassMember(LuaType target, string name, out LuaType member)
    {
        member = LuaTypes.Unknown;
        string? className = target switch
        {
            LuaClassType @class => @class.Name,
            LuaPrototypeType prototype => prototype.Name,
            LuaMetatableType { MetatableType: LuaPrototypeType meta } => meta.Name,
            _ => null,
        };
        if (className is null)
        {
            return false;
        }

        if (_environment.ExternalClassMembers.TryGetValue(className, out var members) &&
            members.TryGetValue(name, out member!))
        {
            return true;
        }

        // An instance's annotated class declares fields the runtime shape has not grown
        // yet; annotations are authoritative for them, including fields declared on a
        // base class (`---@field bus EventBus` on the base of a subclass). A field the
        // pass could not resolve (unknown) defers to the runtime shape below.
        foreach (var declared in ClassChainDeclarations(className))
        {
            if (declared.Fields.FirstOrDefault(field =>
                    string.Equals(field.Name, name, StringComparison.Ordinal)) is { } declaredField &&
                declaredField.ValueType.Kind is not (LuaTypeKind.Unknown or LuaTypeKind.Any))
            {
                member = declaredField.ValueType;
                return true;
            }
        }

        // The local document may know the class's runtime shape even without workspace
        // knowledge (single-file sessions). Open shapes fabricate unknown members for
        // any name; only a real field type wins here.
        if (_latestPrototypes.TryGetValue(className, out var latest))
        {
            var field = _relations.FindField(latest, name);
            if (field is not null &&
                field.ValueType.Kind is not (LuaTypeKind.Unknown or LuaTypeKind.Any))
            {
                member = field.ValueType;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The annotated class declarations from a class up through its declared base chain:
    /// local declarations first, external ones resolved lazily by name, so inherited
    /// annotation fields and operators resolve for subclass instances.
    /// </summary>
    private IEnumerable<LuaClassDeclaration> ClassChainDeclarations(string className)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        pending.Enqueue(className);
        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            if (!visited.Add(current))
            {
                continue;
            }

            var declaration = _types.Declarations.OfType<LuaClassDeclaration>()
                    .FirstOrDefault(item => string.Equals(item.Name, current, StringComparison.Ordinal)) ??
                _types.ResolveDeclarationByName(current) as LuaClassDeclaration;
            if (declaration is null)
            {
                continue;
            }

            yield return declaration;
            foreach (var baseType in declaration.BaseTypes)
            {
                var baseName = baseType switch
                {
                    LuaClassType @class => @class.Name,
                    LuaPrototypeType prototype => prototype.Name,
                    _ => null,
                };
                if (baseName is not null)
                {
                    pending.Enqueue(baseName);
                }
            }
        }
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
            // Lua coerces float indexes with integer values; only clearly non-number
            // indexes are worth reporting.
            if (index.Kind is not (LuaTypeKind.Integer or LuaTypeKind.Float or
                LuaTypeKind.Number or LuaTypeKind.Any or LuaTypeKind.Unknown))
            {
                CheckAssignable(index, LuaTypes.Integer, nodes[1].Span, "array index");
            }

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

            // A non-literal string index over named fields reads one of the field values:
            // level lookup tables (`order[level]`) and shuffled arrays stay typed instead
            // of degrading to an opaque unknown.
            if ((index.Kind is LuaTypeKind.String or LuaTypeKind.Any or LuaTypeKind.Unknown or
                    LuaTypeKind.Alias or LuaTypeKind.Union) &&
                table.Fields.Any(static field => field.Name is not null))
            {
                var fieldValues = table.Fields
                    .Where(static field => field.Name is not null)
                    .Select(static field => field.ValueType);
                if (table.MapValueType is not null)
                {
                    fieldValues = fieldValues.Append(table.MapValueType);
                }

                var fieldUnion = _relations.Union(fieldValues);
                RecordPathType(expression, fieldUnion, state);
                return fieldUnion;
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

        if (target.Kind is LuaTypeKind.StructuralTable or LuaTypeKind.Array or
                LuaTypeKind.Map or LuaTypeKind.Table or LuaTypeKind.Class or
                LuaTypeKind.Prototype or LuaTypeKind.Metatable)
        {
            // Dynamically indexing a table reads one of its values; which one is unknown,
            // but the access itself is not an error.
            var dynamic = InferIndexedMember(target, index, nodes[1].Span);
            var result = dynamic.Kind == LuaTypeKind.Unknown ? LuaTypes.Any : dynamic;
            RecordPathType(expression, result, state);
            return result;
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
            if (callee.Kind is LuaTypeKind.Any or LuaTypeKind.Unknown or LuaTypeKind.Function ||
                callee is LuaAliasType)
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
                // A dynamic constructor call (`Unannotated:new(...)` whose `new` member
                // cannot resolve) still yields an instance of the receiver, so arrays
                // of mixed annotated/unannotated instances keep their member types.
                return ApplyConstructorInference(new LuaTypePack([], LuaTypes.Any), memberName, receiverType);
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
            !selected.Parameters.Any(static parameter => parameter.IsVararg) &&
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
        return ApplyConstructorInference(selected.Returns, memberName, receiverType);
    }

    /// <summary>
    /// Unannotated constructor idiom: calling a member named new/create on a shaped
    /// receiver returns an instance whose members resolve through the receiver's
    /// table, so metatable classes built without annotations still provide instance
    /// types for completion, hover, and navigation.
    /// </summary>
    private static LuaTypePack ApplyConstructorInference(
        LuaTypePack returns,
        string? memberName,
        LuaType? receiverType)
    {
        if (memberName is not ("new" or "create") ||
            receiverType is null ||
            receiverType.Kind is not (LuaTypeKind.StructuralTable or
                LuaTypeKind.Prototype or
                LuaTypeKind.Metatable or
                LuaTypeKind.Map))
        {
            return returns;
        }

        var head = returns.Head.FirstOrDefault();
        var rebuild = head is null || head.Kind is LuaTypeKind.Any or LuaTypeKind.Unknown ||
            // A generic library constructor (`Class:new` returning
            // `setmetatable({}, self)`) reports an empty-storage metatable over the
            // library's own class table — here `Class`, not the subclass the call is
            // made on. Rebuild over the receiver so subclass instances keep their
            // class; only the empty-storage pattern is rewritten, so constructors
            // returning populated tables keep their precise fields.
            head is LuaMetatableType snapshot &&
            !HasShapeMembers(snapshot.BaseType) &&
            HasShapeMembers(receiverType);
        if (!rebuild)
        {
            return returns;
        }

        var instance = new LuaMetatableType(
            new LuaStructuralTableType([], IsOpen: true),
            receiverType,
            IsPrecise: false);
        var rebuilt = ImmutableArray.CreateBuilder<LuaType>(returns.Head.Length);
        rebuilt.Add(instance);
        for (var index = 1; index < returns.Head.Length; index++)
        {
            rebuilt.Add(returns.Head[index]);
        }

        return new LuaTypePack(rebuilt.ToImmutable(), returns.VariadicType);
    }

    private static bool HasShapeMembers(LuaType? type) => type switch
    {
        LuaStructuralTableType table => table.Fields.Any(static field => field.Name != "__index"),
        LuaPrototypeType prototype => HasShapeMembers(prototype.Shape),
        LuaMetatableType metatable =>
            HasShapeMembers(metatable.BaseType) || HasShapeMembers(metatable.MetatableType),
        LuaUnionType union => union.Types.Any(HasShapeMembers),
        _ => false,
    };

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
                return TryInferClassFactoryCall(calledName, arguments, state);
        }
    }

    /// <summary>
    /// Infers a workspace-configured class-factory call: <c>local X = class("Name", Base, ...)</c>
    /// (a "bases" factory, whose arguments after the name are base classes) or
    /// <c>local X = singleton("Name")</c> (a plain definition). The string-literal first
    /// argument names the class; the result is a self-indexed prototype (the framework's
    /// <c>cls.__index = cls</c> protocol), so later member writes define methods,
    /// <c>X.new()</c> produces instances, and member lookup walks the base chain —
    /// subclass members win over base members, giving virtual-function semantics.
    /// </summary>
    private LuaTypePack? TryInferClassFactoryCall(
        string calledName,
        LuaSyntaxNode[] arguments,
        FlowState state)
    {
        if (!_environment.ClassFactoryCalls.TryGetValue(calledName, out var takesBases))
        {
            return null;
        }

        // A dynamic name (or no arguments) is a regular call, not a class definition.
        if (arguments.Length == 0 ||
            InferExpression(arguments[0], state) is not LuaStringLiteralType nameLiteral)
        {
            return null;
        }

        var className = DecodeLiteral(nameLiteral);
        var bases = ImmutableArray.CreateBuilder<LuaType>();
        if (takesBases)
        {
            foreach (var argument in arguments.Skip(1))
            {
                var baseType = InferExpression(argument, state);
                // Only class-shaped values act as bases; flags and option tables that
                // some factories accept must not pollute the inheritance chain.
                if (baseType.Kind is LuaTypeKind.StructuralTable or LuaTypeKind.Prototype or
                    LuaTypeKind.Metatable)
                {
                    bases.Add(baseType);
                }
            }
        }
        else
        {
            foreach (var argument in arguments.Skip(1))
            {
                _ = InferExpression(argument, state);
            }
        }

        return new LuaTypePack([
            new LuaPrototypeType(
                className,
                new LuaStructuralTableType([], IsOpen: true),
                [.. bases],
                UsesSelfIndex: true),
        ]);
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
            PropagateTableMutation(state, baseType, next, key, target.Span);
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
                if (value.Kind != LuaTypeKind.Nil)
                {
                    CheckAssignable(indexType, LuaTypes.Integer, nodes[1].Span, "array index");
                    CheckAssignable(value, array.ElementType, target.Span, "array element");
                }

                break;
            case LuaMapType map:
                if (value.Kind != LuaTypeKind.Nil)
                {
                    // Writing nil is deletion and always legal.
                    CheckAssignable(indexType, map.KeyType, nodes[1].Span, "map index");
                    CheckAssignable(value, map.ValueType, target.Span, "map value");
                }

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
            PropagateTableMutation(state, baseType, next, key, target.Span);
            if (indexType is LuaStringLiteralType text &&
                IsGlobalTableReference(nodes[0], out var environmentSymbol))
            {
                AssignVariable(VariableKey.Global(DecodeLiteral(text)), environmentSymbol, value, target.Span, state);
            }
        }
    }

    private LuaType AddOrReplaceField(LuaType type, string name, LuaType value)
    {
        if (type is LuaPrototypeType prototype)
        {
            return prototype with { Shape = AddOrReplaceField(prototype.Shape, name, value) };
        }

        var table = type as LuaStructuralTableType ?? new LuaStructuralTableType([], IsOpen: true);
        var fields = table.Fields;
        for (var index = 0; index < fields.Length; index++)
        {
            if (!string.Equals(fields[index].Name, name, StringComparison.Ordinal))
            {
                continue;
            }

            var replaced = new LuaTableField[fields.Length];
            var target = 0;
            for (var source = 0; source < fields.Length; source++)
            {
                if (source != index)
                {
                    replaced[target++] = fields[source];
                }
            }

            replaced[^1] = new LuaTableField(name, null, value, false);
            return table with { Fields = [.. replaced] };
        }

        if (fields.Length >= MaximumStructuralTableFieldGrowth)
        {
            // Beyond the growth cap the shape stops tracking individual members
            // and absorbs writes into the map key/value unions instead: generated
            // data files accumulate tens of thousands of members, and keeping
            // per-member precision there costs a quadratic rebuild per write for
            // lookups the map types already answer. Named members already stored
            // keep their precision; only new growth degrades. Once a map union
            // has widened past a small bound it collapses to any: every further
            // member widens it by reference anyway, and re-walking a growing
            // union per write is itself quadratic.
            var mapValue = table.MapValueType is null
                ? value
                : table.MapValueType is LuaUnionType { Types.Length: >= 8 }
                    ? LuaTypes.Any
                    : _relations.Union(table.MapValueType, value);
            var mapKey = table.MapKeyType is null
                ? LuaTypes.String
                : table.MapKeyType is LuaUnionType { Types.Length: >= 8 }
                    ? LuaTypes.Any
                    : _relations.Union(table.MapKeyType, LuaTypes.String);
            return table with { MapKeyType = mapKey, MapValueType = mapValue };
        }

        var grown = new LuaTableField[fields.Length + 1];
        for (var index = 0; index < fields.Length; index++)
        {
            grown[index] = fields[index];
        }

        grown[^1] = new LuaTableField(name, null, value, false);
        return table with { Fields = [.. grown] };
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
            return TryInferRuntimeMetamethod(owner, name, out result);
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

    /// <summary>
    /// Satisfies an operator through a runtime metamethod (<c>Vec2.__add = function...</c>)
    /// the class's module writes. The declaring document is not part of this analysis, so
    /// the workspace's exported member types (or the local prototype in single-file
    /// sessions) provide the signature.
    /// </summary>
    private bool TryInferRuntimeMetamethod(LuaType owner, string name, out LuaType result)
    {
        result = LuaTypes.Unknown;
        string? className = owner switch
        {
            LuaClassType @class => @class.Name,
            LuaPrototypeType prototype => prototype.Name,
            _ => null,
        };
        if (className is null)
        {
            return false;
        }

        var metamethodName = "__" + name;
        if (_environment.ExternalClassMembers.TryGetValue(className, out var members))
        {
            if (members.TryGetValue(metamethodName, out var signature) &&
                FirstSignatureReturn(signature, out result))
            {
                return true;
            }

            return false;
        }

        if (_latestPrototypes.TryGetValue(className, out var localPrototype) &&
            _relations.FindField(localPrototype, metamethodName) is { } field &&
            FirstSignatureReturn(field.ValueType, out result))
        {
            return true;
        }

        return false;
    }

    private static bool FirstSignatureReturn(LuaType signature, out LuaType result)
    {
        switch (signature)
        {
            case LuaFunctionType function:
                result = function.Returns.GetElementOrNil(0);
                return true;
            case LuaOverloadType overload when overload.Signatures.Length > 0:
                result = overload.Signatures[0].Returns.GetElementOrNil(0);
                return true;
            default:
                result = LuaTypes.Unknown;
                return false;
        }
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
            PropagateTableMutation(state, original, next, key, callSpan);
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
            PropagateTableMutation(state, original, next, key, arguments[0].Span);
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

        if (type is LuaMetatableType metatable)
        {
            return AssignEffectiveMetatableMember(metatable, name, value, span);
        }

        // Writing a member of a name-only class or enum value cannot refine the type; the
        // declared fields already describe it and a narrow runtime shape would hide them.
        if (type.Kind is LuaTypeKind.Class or LuaTypeKind.Enum)
        {
            return LuaTypes.Any;
        }

        // Growing an unknown value produces an open shape: later fields are expected.
        var grown = AddOrReplaceField(type, name, value);
        return grown is LuaStructuralTableType { IsOpen: false } closed
            ? closed with { IsOpen = true }
            : grown;
    }

    private LuaMetatableType AssignEffectiveMetatableMember(
        LuaMetatableType metatable,
        string name,
        LuaType value,
        Lunil.Core.Text.TextSpan span)
    {

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
        VariableKey assignedKey,
        TextSpan span)
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

        var replacer = new TypeReferenceReplacer(previous, next, _relations, _context);
        var overlayKeys = new List<VariableKey>();
        foreach (var pair in state.OverlayTypes)
        {
            if (pair.Key != assignedKey)
            {
                overlayKeys.Add(pair.Key);
            }
        }

        foreach (var key in overlayKeys)
        {
            ReplaceAliasedType(state, replacer, key);
            if (replacer.BudgetExceeded)
            {
                ReportTableMutationBudget(span);
                return;
            }
        }

        // The global base universe can only embed the mutated table object when
        // that object is itself reachable from a value published through the
        // versioned global table. Function-local tables never qualify, so the
        // expensive base scan is gated on publication instead of running for
        // every member write.
        if (!IsGloballyPublishedType(previous))
        {
            return;
        }

        foreach (var key in state.EnumerateGlobalBaseKeys())
        {
            ReplaceAliasedType(state, replacer, key);
            if (replacer.BudgetExceeded)
            {
                ReportTableMutationBudget(span);
                return;
            }
        }
    }

    private void ReplaceAliasedType(FlowState state, TypeReferenceReplacer replacer, VariableKey key)
    {
        var current = state.TypeOf(key, LuaTypes.Any);
        var replaced = replacer.Replace(current);
        if (ReferenceEquals(current, replaced))
        {
            return;
        }

        state.SetType(key, replaced);
        if (!key.IsGlobal &&
            _symbolsById.TryGetValue(key.SymbolId, out var symbol))
        {
            RecordSymbolInference(symbol, replaced);
        }
    }

    private void ReportTableMutationBudget(TextSpan span) =>
        _context.ReportTableMutationBudget(span);

    /// <summary>
    /// Replaces every reference to one type object inside composite type graphs and
    /// returns the original node when the graph does not contain it. Results are
    /// memoized by node identity so shared sub-graphs are visited once per
    /// propagation, and unchanged children keep their references so callers can
    /// detect no-op replacements with reference equality.
    /// </summary>
    private sealed class TypeReferenceReplacer(
        LuaType previous,
        LuaType next,
        LuaTypeRelations relations,
        LuaAnalysisContext context)
    {
        private readonly Dictionary<LuaType, LuaType> _results = new(LunilReferenceEqualityComparer.Instance);

        public bool BudgetExceeded { get; private set; }

        public LuaType Replace(LuaType current) => Replace(current, depth: 0, out _);

        // A node is memoizable only when its whole subtree was resolved without
        // hitting the depth cap or the propagation budget: those truncations are
        // depth-sensitive, so their results must not be reused at other depths.
        private LuaType Replace(LuaType current, int depth, out bool complete)
        {
            if (ReferenceEquals(current, previous))
            {
                complete = true;
                return next;
            }

            if (_results.TryGetValue(current, out var cached))
            {
                complete = true;
                return cached;
            }

            if (!context.TryVisitTableMutationNode())
            {
                BudgetExceeded = true;
                complete = false;
                return current;
            }

            if (depth >= MaximumMetatableLookupDepth)
            {
                complete = false;
                return current is LuaMetatableType metatable ? metatable with { IsPrecise = false } : current;
            }

            var result = ReplaceCore(current, depth, out complete);
            if (complete)
            {
                _results[current] = result;
            }

            return result;
        }

        private LuaType ReplaceCore(LuaType current, int depth, out bool complete)
        {
            switch (current)
            {
                case LuaMetatableType metatable:
                    {
                        var baseType = Replace(metatable.BaseType, depth + 1, out var baseComplete);
                        var metaType = Replace(metatable.MetatableType, depth + 1, out var metaComplete);
                        complete = baseComplete && metaComplete;
                        return ReferenceEquals(baseType, metatable.BaseType) &&
                               ReferenceEquals(metaType, metatable.MetatableType)
                            ? metatable
                            : metatable with { BaseType = baseType, MetatableType = metaType };
                    }

                case LuaPrototypeType prototype:
                    {
                        var shape = Replace(prototype.Shape, depth + 1, out var shapeComplete);
                        var baseTypes = ReplaceAll(prototype.BaseTypes, depth + 1, out var baseComplete, out var basesChanged);
                        complete = shapeComplete && baseComplete;
                        return !basesChanged && ReferenceEquals(shape, prototype.Shape)
                            ? prototype
                            : prototype with { Shape = shape, BaseTypes = baseTypes };
                    }

                case LuaUnionType union:
                    {
                        var members = ReplaceAll(union.Types, depth + 1, out complete, out var changed);
                        return changed ? relations.Union(members) : union;
                    }

                case LuaStructuralTableType table:
                    {
                        complete = true;
                        var changed = false;
                        var fields = new LuaTableField[table.Fields.Length];
                        for (var index = 0; index < table.Fields.Length; index++)
                        {
                            var field = table.Fields[index];
                            var keyComplete = true;
                            var keyType = field.KeyType;
                            if (keyType is not null)
                            {
                                keyType = Replace(keyType, depth + 1, out keyComplete);
                            }

                            var valueType = Replace(field.ValueType, depth + 1, out var valueComplete);
                            complete &= keyComplete && valueComplete;
                            if (ReferenceEquals(keyType, field.KeyType) &&
                                ReferenceEquals(valueType, field.ValueType))
                            {
                                fields[index] = field;
                                continue;
                            }

                            changed = true;
                            fields[index] = field with { KeyType = keyType, ValueType = valueType };
                        }

                        return changed ? table with { Fields = [.. fields] } : table;
                    }

                case LuaFunctionType function:
                    {
                        complete = true;
                        var changed = false;
                        var parameters = new LuaFunctionParameter[function.Parameters.Length];
                        for (var index = 0; index < function.Parameters.Length; index++)
                        {
                            var parameter = function.Parameters[index];
                            var type = Replace(parameter.Type, depth + 1, out var typeComplete);
                            complete &= typeComplete;
                            if (ReferenceEquals(type, parameter.Type))
                            {
                                parameters[index] = parameter;
                                continue;
                            }

                            changed = true;
                            parameters[index] = parameter with { Type = type };
                        }

                        var returns = (LuaTypePack)Replace(function.Returns, depth + 1, out var returnsComplete);
                        complete &= returnsComplete;
                        return !changed && ReferenceEquals(returns, function.Returns)
                            ? function
                            : function with { Parameters = [.. parameters], Returns = returns };
                    }

                case LuaTypePack pack:
                    {
                        var head = ReplaceAll(pack.Head, depth + 1, out complete, out var headChanged);
                        var variadicComplete = true;
                        var variadic = pack.VariadicType;
                        if (variadic is not null)
                        {
                            variadic = Replace(variadic, depth + 1, out variadicComplete);
                            complete &= variadicComplete;
                        }

                        return !headChanged && ReferenceEquals(variadic, pack.VariadicType)
                            ? pack
                            : pack with { Head = head, VariadicType = variadic };
                    }

                case LuaOverloadType overload:
                    {
                        complete = true;
                        var changed = false;
                        var signatures = new LuaFunctionType[overload.Signatures.Length];
                        for (var index = 0; index < overload.Signatures.Length; index++)
                        {
                            var signature = overload.Signatures[index];
                            var replaced = Replace(signature, depth + 1, out var signatureComplete);
                            complete &= signatureComplete;
                            if (ReferenceEquals(replaced, signature))
                            {
                                signatures[index] = signature;
                                continue;
                            }

                            changed = true;
                            signatures[index] = (LuaFunctionType)replaced;
                        }

                        return changed ? overload with { Signatures = [.. signatures] } : overload;
                    }

                default:
                    complete = true;
                    return current;
            }
        }

        private ImmutableArray<LuaType> ReplaceAll(
            ImmutableArray<LuaType> items,
            int depth,
            out bool complete,
            out bool changed)
        {
            complete = true;
            changed = false;
            LuaType[]? rewritten = null;
            for (var index = 0; index < items.Length; index++)
            {
                var item = items[index];
                var replaced = Replace(item, depth, out var itemComplete);
                complete &= itemComplete;
                if (ReferenceEquals(replaced, item))
                {
                    continue;
                }

                rewritten ??= [.. items];
                rewritten[index] = replaced;
                changed = true;
            }

            return changed ? [.. rewritten!] : items;
        }
    }

    private void InvalidateEscapedMetatables(IEnumerable<LuaSyntaxNode> arguments, FlowState state)
    {
        foreach (var argument in arguments)
        {
            if (!TryGetVariableKey(argument, out var key, out var symbol) ||
                !state.TryGetType(key, out var current))
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
            PropagateTableMutation(state, current, widened, key, argument.Span);
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
