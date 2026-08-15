using System.Collections.Immutable;
using System.Text;
using Lunil.Semantics.Binding;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

namespace Lunil.Analysis;

internal sealed partial class AnalysisEngine
{
    private ConditionStates NarrowCondition(LuaSyntaxNode expression, FlowState state)
    {
        if (expression.Kind == LuaSyntaxKind.ParenthesizedExpression)
        {
            return NarrowCondition(expression.ChildNodes().Single(), state);
        }

        if (expression.Kind == LuaSyntaxKind.UnaryExpression &&
            expression.ChildTokens().First().Kind == LuaTokenKind.NotKeyword)
        {
            var operand = NarrowCondition(expression.ChildNodes().Single(), state);
            return new ConditionStates(operand.FalseState, operand.TrueState);
        }

        if (expression.Kind == LuaSyntaxKind.BinaryExpression)
        {
            var nodes = expression.ChildNodes().ToArray();
            var operation = expression.ChildTokens().Single().Kind;
            if (operation == LuaTokenKind.AndKeyword)
            {
                var left = NarrowCondition(nodes[0], state);
                var right = NarrowCondition(nodes[1], left.TrueState);
                return new ConditionStates(
                    right.TrueState,
                    MergeStates([left.FalseState, right.FalseState], expression.Span));
            }

            if (operation == LuaTokenKind.OrKeyword)
            {
                var left = NarrowCondition(nodes[0], state);
                var right = NarrowCondition(nodes[1], left.FalseState);
                return new ConditionStates(
                    MergeStates([left.TrueState, right.TrueState], expression.Span),
                    right.FalseState);
            }

            if (operation is LuaTokenKind.Equal or LuaTokenKind.NotEqual)
            {
                return NarrowEquality(
                    nodes[0],
                    nodes[1],
                    operation == LuaTokenKind.Equal,
                    state,
                    expression.Span);
            }
        }

        if (TryGetCalledGlobalIdentifier(expression, out var called) &&
            string.Equals(called, "assert", StringComparison.Ordinal))
        {
            var argument = GetCallArguments(expression).FirstOrDefault();
            if (argument is not null)
            {
                return NarrowCondition(argument, state);
            }
        }

        if (TryGetVariableKey(expression, out var key, out _))
        {
            var current = state.TypeOf(
                key,
                _declaredTypes.GetValueOrDefault(key, LuaTypes.Any));
            return NarrowVariable(
                key,
                _relations.TruthyPart(current),
                _relations.FalsyPart(current),
                state,
                expression.Span);
        }

        if (TryGetAccessPath(expression, out var path))
        {
            var current = state.PathTypes.GetValueOrDefault(
                path,
                _expressionInferences.GetValueOrDefault(expression.Span, LuaTypes.Any));
            return NarrowPath(
                path,
                _relations.TruthyPart(current),
                _relations.FalsyPart(current),
                state,
                expression.Span);
        }

        var type = InferExpression(expression, state);
        var truthy = _relations.TruthyPart(type);
        var falsy = _relations.FalsyPart(type);
        var trueState = state.Clone();
        var falseState = state.Clone();
        if (truthy.Kind == LuaTypeKind.Never)
        {
            trueState.Reachable = false;
            ReportRedundantCondition(expression.Span, isAlwaysTrue: false);
        }

        if (falsy.Kind == LuaTypeKind.Never)
        {
            falseState.Reachable = false;
            ReportRedundantCondition(expression.Span, isAlwaysTrue: true);
        }

        return new ConditionStates(trueState, falseState);
    }

    private ConditionStates NarrowEquality(
        LuaSyntaxNode left,
        LuaSyntaxNode right,
        bool equal,
        FlowState state,
        Lunil.Core.Text.TextSpan span)
    {
        if (IsNil(left) && TryGetVariableKey(right, out var rightKey, out _))
        {
            return NarrowNil(rightKey, equal, state, span);
        }

        if (IsNil(left) && TryGetAccessPath(right, out var rightPath))
        {
            _ = InferExpression(right, state);
            return NarrowNilPath(rightPath, equal, state, span);
        }

        if (IsNil(right) && TryGetVariableKey(left, out var leftKey, out _))
        {
            return NarrowNil(leftKey, equal, state, span);
        }

        if (IsNil(right) && TryGetAccessPath(left, out var leftPath))
        {
            _ = InferExpression(left, state);
            return NarrowNilPath(leftPath, equal, state, span);
        }

        if (TryGetTypeTest(left, right, out var typeKey, out var testedType) ||
            TryGetTypeTest(right, left, out typeKey, out testedType))
        {
            var current = state.TypeOf(
                typeKey,
                _declaredTypes.GetValueOrDefault(typeKey, LuaTypes.Any));
            var matched = _relations.NarrowTo(current, testedType);
            var unmatched = _relations.Exclude(current, testedType);
            return equal
                ? NarrowVariable(typeKey, matched, unmatched, state, span)
                : NarrowVariable(typeKey, unmatched, matched, state, span);
        }

        if (TryGetDiscriminant(left, right, out var discriminantKey, out var member, out var literal) ||
            TryGetDiscriminant(right, left, out discriminantKey, out member, out literal))
        {
            return NarrowDiscriminant(
                discriminantKey,
                member,
                literal,
                equal,
                state,
                span);
        }

        if (TryGetVariableKey(left, out var key, out _) && TryGetLiteralType(right, out var rightLiteral) ||
            TryGetVariableKey(right, out key, out _) && TryGetLiteralType(left, out rightLiteral))
        {
            var current = state.TypeOf(
                key,
                _declaredTypes.GetValueOrDefault(key, LuaTypes.Any));
            var matched = _relations.NarrowTo(current, rightLiteral);
            var unmatched = _relations.Exclude(current, rightLiteral);
            return equal
                ? NarrowVariable(key, matched, unmatched, state, span)
                : NarrowVariable(key, unmatched, matched, state, span);
        }

        return new ConditionStates(state.Clone(), state.Clone());
    }

    private ConditionStates NarrowNil(
        VariableKey key,
        bool equal,
        FlowState state,
        Lunil.Core.Text.TextSpan span)
    {
        var current = state.TypeOf(
            key,
            _declaredTypes.GetValueOrDefault(key, LuaTypes.Any));
        var nil = _relations.NarrowTo(current, LuaTypes.Nil);
        var nonNil = _relations.RemoveNil(current);
        return equal
            ? NarrowVariable(key, nil, nonNil, state, span)
            : NarrowVariable(key, nonNil, nil, state, span);
    }

    private ConditionStates NarrowVariable(
        VariableKey key,
        LuaType trueType,
        LuaType falseType,
        FlowState state,
        Lunil.Core.Text.TextSpan span)
    {
        var trueState = state.Clone();
        var falseState = state.Clone();
        trueState.SetType(key, trueType);
        falseState.SetType(key, falseType);
        if (trueType.Kind == LuaTypeKind.Never)
        {
            trueState.Reachable = false;
            ReportRedundantCondition(span, isAlwaysTrue: false);
        }

        if (falseType.Kind == LuaTypeKind.Never)
        {
            falseState.Reachable = false;
            ReportRedundantCondition(span, isAlwaysTrue: true);
        }

        return new ConditionStates(trueState, falseState);
    }

    private ConditionStates NarrowNilPath(
        AccessPathKey path,
        bool equal,
        FlowState state,
        Lunil.Core.Text.TextSpan span)
    {
        var current = state.PathTypes.GetValueOrDefault(path, LuaTypes.Any);
        var nil = _relations.NarrowTo(current, LuaTypes.Nil);
        var nonNil = _relations.RemoveNil(current);
        return equal
            ? NarrowPath(path, nil, nonNil, state, span)
            : NarrowPath(path, nonNil, nil, state, span);
    }

    private ConditionStates NarrowPath(
        AccessPathKey path,
        LuaType trueType,
        LuaType falseType,
        FlowState state,
        Lunil.Core.Text.TextSpan span)
    {
        var trueState = state.Clone();
        var falseState = state.Clone();
        trueState.PathTypes[path] = trueType;
        falseState.PathTypes[path] = falseType;
        if (trueType.Kind == LuaTypeKind.Never)
        {
            trueState.Reachable = false;
            ReportRedundantCondition(span, isAlwaysTrue: false);
        }

        if (falseType.Kind == LuaTypeKind.Never)
        {
            falseState.Reachable = false;
            ReportRedundantCondition(span, isAlwaysTrue: true);
        }

        return new ConditionStates(trueState, falseState);
    }

    private ConditionStates NarrowDiscriminant(
        VariableKey key,
        string memberName,
        LuaLiteralType literal,
        bool equal,
        FlowState state,
        Lunil.Core.Text.TextSpan span)
    {
        var current = state.TypeOf(
            key,
            _declaredTypes.GetValueOrDefault(key, LuaTypes.Any));
        var members = current is LuaUnionType union ? union.Types : [current];
        var matching = new List<LuaType>();
        var remaining = new List<LuaType>();
        foreach (var candidate in members)
        {
            var item = _relations.FindField(candidate, memberName);
            if (item is not null &&
                (_relations.IsAssignable(item.ValueType, literal) ||
                 _relations.IsAssignable(literal, item.ValueType)))
            {
                matching.Add(NarrowField(candidate, memberName, literal));
            }
            else
            {
                remaining.Add(candidate);
            }
        }

        var matchedType = _relations.Union(matching);
        var remainingType = _relations.Union(remaining);
        return equal
            ? NarrowVariable(key, matchedType, remainingType, state, span)
            : NarrowVariable(key, remainingType, matchedType, state, span);
    }

    private LuaType NarrowField(LuaType type, string memberName, LuaType value)
    {
        if (type is not LuaStructuralTableType table)
        {
            return type;
        }

        return table with
        {
            Fields = [.. table.Fields.Select(item => string.Equals(
                item.Name,
                memberName,
                StringComparison.Ordinal)
                    ? item with { ValueType = _relations.NarrowTo(item.ValueType, value) }
                    : item)],
        };
    }

    private bool TryGetTypeTest(
        LuaSyntaxNode call,
        LuaSyntaxNode literal,
        out VariableKey key,
        out LuaType type)
    {
        key = default;
        type = LuaTypes.Unknown;
        if (!TryGetCalledGlobalIdentifier(call, out var called) ||
            !string.Equals(called, "type", StringComparison.Ordinal) ||
            !TryGetStringLiteral(literal, out var tag))
        {
            return false;
        }

        var argument = GetCallArguments(call).FirstOrDefault();
        if (argument is null || !TryGetVariableKey(argument, out key, out _))
        {
            return false;
        }

        type = tag switch
        {
            "nil" => LuaTypes.Nil,
            "boolean" => LuaTypes.Boolean,
            "number" => LuaTypes.Number,
            "string" => LuaTypes.String,
            "table" => LuaTypes.Table,
            "function" => LuaTypes.Function,
            "thread" => LuaTypes.Thread,
            "userdata" => LuaTypes.Userdata,
            _ => LuaTypes.Unknown,
        };
        return type.Kind != LuaTypeKind.Unknown;
    }

    private bool TryGetDiscriminant(
        LuaSyntaxNode member,
        LuaSyntaxNode literalExpression,
        out VariableKey key,
        out string memberName,
        out LuaLiteralType literal)
    {
        key = default;
        memberName = string.Empty;
        literal = null!;
        if (member.Kind != LuaSyntaxKind.MemberAccessExpression ||
            !TryGetLiteralType(literalExpression, out var literalType) ||
            literalType is not LuaLiteralType literalValue)
        {
            return false;
        }

        var baseExpression = member.ChildNodes().Single();
        if (!TryGetVariableKey(baseExpression, out key, out _))
        {
            return false;
        }

        memberName = GetTokenText(member.ChildTokens().Last(static token =>
            token.Kind == LuaTokenKind.Identifier));
        literal = literalValue;
        return true;
    }

    private static bool TryGetLiteralType(LuaSyntaxNode expression, out LuaType type)
    {
        type = expression.Kind switch
        {
            LuaSyntaxKind.NilLiteralExpression => LuaTypes.Nil,
            LuaSyntaxKind.FalseLiteralExpression => new LuaBooleanLiteralType(false),
            LuaSyntaxKind.TrueLiteralExpression => new LuaBooleanLiteralType(true),
            LuaSyntaxKind.NumericLiteralExpression => InferNumericLiteral(expression),
            LuaSyntaxKind.StringLiteralExpression => InferStringLiteral(expression),
            _ => LuaTypes.Unknown,
        };
        return type.Kind != LuaTypeKind.Unknown;
    }

    private static bool IsNil(LuaSyntaxNode expression) =>
        expression.Kind == LuaSyntaxKind.NilLiteralExpression;

    private static bool TryGetStringLiteral(LuaSyntaxNode expression, out string value)
    {
        if (InferStringLiteral(expression) is LuaStringLiteralType text)
        {
            value = DecodeLiteral(text);
            return true;
        }

        value = string.Empty;
        return false;
    }

    private LuaTypePack MergeReturnPacks(
        IEnumerable<LuaTypePack> packs,
        Lunil.Core.Text.TextSpan span)
    {
        var values = packs.ToArray();
        if (values.Length == 0)
        {
            return LuaTypePack.Empty;
        }

        var length = Math.Min(
            values.Max(static pack => pack.Head.Length),
            _context.Options.MaximumReturnPackLength);
        var head = ImmutableArray.CreateBuilder<LuaType>(length);
        for (var index = 0; index < length; index++)
        {
            head.Add(_relations.Union(values.Select(pack => pack.GetElementOrNil(index))));
        }

        var variadic = values.Where(static pack => pack.VariadicType is not null)
            .Select(static pack => pack.VariadicType!)
            .ToArray();
        return new LuaTypePack(
            head.ToImmutable(),
            variadic.Length == 0 ? null : _relations.Union(variadic));
    }

    private FlowState MergeStates(
        IEnumerable<FlowState> states,
        Lunil.Core.Text.TextSpan span)
    {
        var reachable = states.Where(static state => state.Reachable).ToArray();
        if (reachable.Length == 0)
        {
            var unreachable = states.FirstOrDefault()?.Clone() ?? CreateRootState();
            unreachable.Reachable = false;
            return unreachable;
        }

        var result = reachable[0].Clone();
        // Only overlay keys participate: every state shares the same frozen global
        // base, so unoverridden globals compare equal on both sides of the merge and
        // need no union pass. Overridden globals fall back to the base value in the
        // states that did not override them, matching full-map merge semantics.
        var keys = new HashSet<VariableKey>();
        foreach (var state in reachable)
        {
            foreach (var pair in state.OverlayTypes)
            {
                keys.Add(pair.Key);
            }
        }

        foreach (var key in keys)
        {
            var candidates = new List<LuaType>();
            foreach (var state in reachable)
            {
                if (state.TryGetType(key, out var type))
                {
                    candidates.Add(type);
                }
            }

            if (candidates.Count != 0)
            {
                result.SetType(key, _relations.Union(candidates));
            }
        }

        var paths = new HashSet<AccessPathKey>();
        foreach (var state in reachable)
        {
            foreach (var pair in state.PathTypes)
            {
                paths.Add(pair.Key);
            }
        }

        result.PathTypes.Clear();
        foreach (var path in paths)
        {
            var candidates = new List<LuaType>();
            foreach (var state in reachable)
            {
                if (state.PathTypes.TryGetValue(path, out var type))
                {
                    candidates.Add(type);
                }
            }

            if (candidates.Count == reachable.Length)
            {
                result.PathTypes[path] = _relations.Union(candidates);
            }
        }

        result.IntersectAssigned(reachable);
        result.Reachable = true;
        return result;
    }

    private FlowState WidenState(
        FlowState previous,
        FlowState candidate,
        Lunil.Core.Text.TextSpan span)
    {
        var result = candidate.Clone();
        foreach (var pair in previous.OverlayTypes)
        {
            if (candidate.TryGetType(pair.Key, out var next))
            {
                result.SetType(pair.Key, _relations.Union(pair.Value, next));
            }
        }

        return result;
    }

    private static bool StatesEquivalent(FlowState left, FlowState right)
    {
        if (left.Reachable != right.Reachable ||
            !left.AssignedSetEquals(right) ||
            left.PathTypes.Count != right.PathTypes.Count)
        {
            return false;
        }

        // Overlay entries whose value equals the shared base binding are equivalent
        // to an absent entry, so comparing the union of both overlays against each
        // state's effective lookup reproduces full-map equality.
        foreach (var pair in left.OverlayTypes)
        {
            if (!right.TryGetType(pair.Key, out var type) ||
                !string.Equals(pair.Value.DisplayName, type.DisplayName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        foreach (var pair in right.OverlayTypes)
        {
            if (!left.TryGetType(pair.Key, out var type) ||
                !string.Equals(pair.Value.DisplayName, type.DisplayName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        foreach (var pair in left.PathTypes)
        {
            if (!right.PathTypes.TryGetValue(pair.Key, out var type) ||
                !string.Equals(pair.Value.DisplayName, type.DisplayName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void CopyState(FlowState source, FlowState destination) =>
        destination.CopyFrom(source);

    private void CheckAssignable(
        LuaType source,
        LuaType target,
        Lunil.Core.Text.TextSpan span,
        string context)
    {
        if (!_context.TryAddConstraint(span) || _relations.IsAssignable(source, target))
        {
            return;
        }

        _context.AddDiagnostic(
            "LUA6003",
            span,
            $"Type '{source.DisplayName}' is not assignable to '{target.DisplayName}' for {context}.");
    }

    private void CheckPackAssignable(
        LuaTypePack source,
        LuaTypePack target,
        Lunil.Core.Text.TextSpan span,
        string context)
    {
        for (var index = 0; index < target.Head.Length; index++)
        {
            CheckAssignable(
                source.GetElementOrNil(index),
                target.Head[index],
                span,
                $"{context} value {index + 1}");
        }
    }

    private bool CheckNumericOperand(LuaType type, Lunil.Core.Text.TextSpan span)
    {
        if (!_context.TryAddConstraint(span) ||
            _relations.IsAssignable(type, LuaTypes.Number) || type.Kind == LuaTypeKind.Any)
        {
            return true;
        }

        _context.AddDiagnostic(
            "LUA6003",
            span,
            $"Numeric operation requires number, not '{type.DisplayName}'.");
        return false;
    }

    private bool CheckIntegerOperand(LuaType type, Lunil.Core.Text.TextSpan span)
    {
        if (!_context.TryAddConstraint(span) ||
            _relations.IsAssignable(type, LuaTypes.Integer) || type.Kind == LuaTypeKind.Any)
        {
            return true;
        }

        _context.AddDiagnostic(
            "LUA6003",
            span,
            $"Bitwise operation requires integer, not '{type.DisplayName}'.");
        return false;
    }

    private bool CheckConcatenationOperand(LuaType type, Lunil.Core.Text.TextSpan span)
    {
        if (!_context.TryAddConstraint(span) ||
            _relations.IsAssignable(type, LuaTypes.String) ||
            _relations.IsAssignable(type, LuaTypes.Number) ||
            type.Kind == LuaTypeKind.Any)
        {
            return true;
        }

        _context.AddDiagnostic(
            "LUA6003",
            span,
            $"Concatenation requires string or number, not '{type.DisplayName}'.");
        return false;
    }

    private bool CheckLengthOperand(LuaType type, Lunil.Core.Text.TextSpan span)
    {
        if (!_context.TryAddConstraint(span) ||
            _relations.IsAssignable(type, LuaTypes.String) ||
            _relations.IsAssignable(type, LuaTypes.Table) ||
            type.Kind == LuaTypeKind.Any)
        {
            return true;
        }

        _context.AddDiagnostic(
            "LUA6003",
            span,
            $"Length operation requires string or table, not '{type.DisplayName}'.");
        return false;
    }

    private bool CheckComparableOperands(
        LuaType left,
        LuaType right,
        Lunil.Core.Text.TextSpan span)
    {
        if (!_context.TryAddConstraint(span) ||
            left.Kind == LuaTypeKind.Any || right.Kind == LuaTypeKind.Any ||
            _relations.IsAssignable(left, LuaTypes.Number) &&
            _relations.IsAssignable(right, LuaTypes.Number) ||
            _relations.IsAssignable(left, LuaTypes.String) &&
            _relations.IsAssignable(right, LuaTypes.String))
        {
            return true;
        }

        _context.AddDiagnostic(
            "LUA6003",
            span,
            $"Ordered comparison requires two numbers or two strings, not " +
            $"'{left.DisplayName}' and '{right.DisplayName}'.");
        return false;
    }

    private static bool IsIntegerLike(LuaType type) => type.Kind == LuaTypeKind.Integer ||
        type is LuaIntegerLiteralType;

    private static bool IsShapeGrowth(LuaType previous, LuaType next)
    {
        // An empty placeholder snapshot carries no information; any later shape
        // (including the self-index prototype upgrade) replaces it.
        if (previous is LuaStructuralTableType { Fields.IsEmpty: true } or
            LuaPrototypeType { Shape: LuaStructuralTableType { Fields.IsEmpty: true } })
        {
            return true;
        }

        // A self-indexed class table becomes a prototype; compare through its shape. The
        // `X.__index = X` upgrade itself flips UsesSelfIndex on while preserving the
        // shape, and turning self-index ON must count as growth — unioning the upgrade
        // with the pre-upgrade snapshot rolls the cell back and swallows every later
        // member write (the class-library pattern `function Cls.new` broke this way).
        // Turning it back OFF is a downgrade and still unions.
        if (previous is LuaPrototypeType oldPrototype &&
            next is LuaPrototypeType newPrototype &&
            string.Equals(oldPrototype.Name, newPrototype.Name, StringComparison.Ordinal) &&
            (newPrototype.UsesSelfIndex || oldPrototype.UsesSelfIndex == newPrototype.UsesSelfIndex))
        {
            return IsShapeGrowth(oldPrototype.Shape, newPrototype.Shape);
        }

        if (previous is not LuaStructuralTableType oldTable ||
            next is not LuaStructuralTableType newTable)
        {
            return false;
        }

        foreach (var field in oldTable.Fields)
        {
            if (field.Name is null)
            {
                continue;
            }

            var replacement = newTable.Fields.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, field.Name, StringComparison.Ordinal));
            if (replacement is null ||
                !ReferenceEquals(replacement.ValueType, field.ValueType) &&
                !string.Equals(replacement.ValueType.DisplayName, field.ValueType.DisplayName,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void RecordSymbolInference(LuaSymbol symbol, LuaType type)
    {
        if (symbol.IsCaptured)
        {
            if (!_upvalueCells.TryGetValue(symbol.Id, out var cell))
            {
                cell = new UpvalueCellState(symbol, type);
                _upvalueCells.Add(symbol.Id, cell);
            }
            else if (IsShapeGrowth(cell.Type, type))
            {
                // Captured module/class tables grow member by member. Unioning two
                // growth stages of one table widens member lookups to unions (or the
                // empty placeholder) and hides every declared member; a pure growth
                // step replaces the previous snapshot losslessly.
                cell.Type = type;
            }
            else
            {
                cell.Type = _relations.Union(cell.Type, type);
            }

            if (_currentFunction is not null)
            {
                cell.Writers.Add(_currentFunction.FunctionId);
            }
        }

        if (type is LuaPrototypeType prototype)
        {
            _latestPrototypes[prototype.Name] = prototype;
            _symbolInferences[symbol.Id] = prototype;
            return;
        }

        _symbolInferences[symbol.Id] = _symbolInferences.TryGetValue(symbol.Id, out var previous)
            ? _relations.Union(previous, type)
            : type;
    }

    private void RecordExpressionInference(Lunil.Core.Text.TextSpan span, LuaType type)
    {
        _expressionInferences[span] = _expressionInferences.TryGetValue(span, out var previous)
            ? _relations.Union(previous, type)
            : type;
    }

    private void ReportUnreachableCode()
    {
        if (!_context.Options.ReportUnreachableCode)
        {
            return;
        }

        var spans = _graphs.SelectMany(static graph => graph.Blocks)
            .Where(static block => !block.IsReachable)
            .SelectMany(static block => block.StatementSpans)
            .Where(static span => span.Length != 0)
            .Distinct()
            .OrderBy(static span => span.Start);
        foreach (var span in spans)
        {
            _context.AddDiagnostic("LUA6009", span, "Statement is unreachable.");
        }
    }

    private void ReportRedundantCondition(Lunil.Core.Text.TextSpan span, bool isAlwaysTrue)
    {
        if (_context.Options.ReportRedundantConditions)
        {
            _context.AddDiagnostic(
                "LUA6016",
                span,
                $"Condition is always {(isAlwaysTrue ? "truthy" : "falsy")} under the current flow types.");
        }
    }

    private void InstallBuiltIns()
    {
        foreach (var pair in _hostGlobalTypes)
        {
            SetGlobalType(pair.Key, pair.Value);
        }

        SetGlobalType("type", new LuaFunctionType(
            [new LuaFunctionParameter("value", LuaTypes.Any)],
            new LuaTypePack([LuaTypes.String]),
            []));
        SetGlobalType("assert", new LuaFunctionType(
            [new LuaFunctionParameter("value", LuaTypes.Any),
             new LuaFunctionParameter("message", LuaTypes.Any, IsOptional: true)],
            new LuaTypePack([LuaTypes.Any]),
            []));
        SetGlobalType("tonumber", new LuaFunctionType(
            [new LuaFunctionParameter("value", LuaTypes.Any)],
            new LuaTypePack([_relations.Union(LuaTypes.Number, LuaTypes.Nil)]),
            []));
        SetGlobalType("tostring", new LuaFunctionType(
            [new LuaFunctionParameter("value", LuaTypes.Any)],
            new LuaTypePack([LuaTypes.String]),
            []));
        SetGlobalType("require", new LuaFunctionType(
            [new LuaFunctionParameter("module", LuaTypes.String)],
            new LuaTypePack([LuaTypes.Any]),
            []));
        SetGlobalType("print", new LuaFunctionType(
            [new LuaFunctionParameter("...", LuaTypes.Any, IsOptional: true, IsVararg: true)],
            LuaTypePack.Empty,
            []));
        SetGlobalType("error", new LuaFunctionType(
            [new LuaFunctionParameter("message", LuaTypes.Any)],
            new LuaTypePack([LuaTypes.Never]),
            []));
        SetGlobalType("setmetatable", new LuaFunctionType(
            [new LuaFunctionParameter("table", LuaTypes.Table),
             new LuaFunctionParameter("metatable", LuaTypes.Table)],
            new LuaTypePack([LuaTypes.Table]),
            []));
        SetGlobalType("getmetatable", new LuaFunctionType(
            [new LuaFunctionParameter("value", LuaTypes.Any)],
            new LuaTypePack([_relations.Union(LuaTypes.Table, LuaTypes.Nil)]),
            []));
        SetGlobalType("rawget", new LuaFunctionType(
            [new LuaFunctionParameter("table", LuaTypes.Table),
             new LuaFunctionParameter("index", LuaTypes.Any)],
            new LuaTypePack([LuaTypes.Any]),
            []));
        SetGlobalType("rawset", new LuaFunctionType(
            [new LuaFunctionParameter("table", LuaTypes.Table),
             new LuaFunctionParameter("index", LuaTypes.Any),
             new LuaFunctionParameter("value", LuaTypes.Any)],
            new LuaTypePack([LuaTypes.Table]),
            []));
    }

    private bool TryGetVariableKey(
        LuaSyntaxNode expression,
        out VariableKey key,
        out LuaSymbol symbol)
    {
        key = default;
        symbol = null!;
        if (expression.Kind != LuaSyntaxKind.IdentifierExpression)
        {
            return false;
        }

        var token = expression.ChildTokens().First(static item => item.Kind == LuaTokenKind.Identifier);
        if (!_references.TryGetValue(token.Span, out var reference))
        {
            return false;
        }

        symbol = reference.Symbol;
        key = reference.ResolutionKind == LuaNameResolutionKind.Global
            ? VariableKey.Global(reference.Name)
            : VariableKey.Local(reference.Symbol.Id);
        return true;
    }

    /// <summary>
    /// Detects when an expression is the module's global table (<c>_G</c> or <c>_ENV</c>) directly,
    /// so that writes like <c>_G.name = value</c> can additionally be recorded as a global assignment.
    /// </summary>
    private bool IsGlobalTableReference(LuaSyntaxNode expression, out LuaSymbol symbol)
    {
        symbol = null!;
        if (expression.Kind != LuaSyntaxKind.IdentifierExpression)
        {
            return false;
        }

        var token = expression.ChildTokens().First(static item => item.Kind == LuaTokenKind.Identifier);
        if (!_references.TryGetValue(token.Span, out var reference))
        {
            return false;
        }

        if (reference.ResolutionKind != LuaNameResolutionKind.Global ||
            reference.Symbol.Kind != LuaSymbolKind.Environment ||
            reference.Name is not ("_G" or "_ENV"))
        {
            return false;
        }

        symbol = reference.Symbol;
        return true;
    }

    private bool TryGetAccessPath(LuaSyntaxNode expression, out AccessPathKey path)
    {
        if (expression.Kind == LuaSyntaxKind.IdentifierExpression &&
            TryGetVariableKey(expression, out var key, out _))
        {
            path = new AccessPathKey(
                key.IsGlobal ? "g:" + key.GlobalName : "s:" + key.SymbolId,
                0);
            return true;
        }

        if (expression.Kind == LuaSyntaxKind.MemberAccessExpression)
        {
            var receiver = expression.ChildNodes().FirstOrDefault();
            var member = expression.ChildTokens().LastOrDefault(static token =>
                token.Kind == LuaTokenKind.Identifier && !token.IsMissing);
            if (receiver is not null && member is not null &&
                TryGetAccessPath(receiver, out var receiverPath))
            {
                path = new AccessPathKey(
                    receiverPath.Value + "." + GetTokenText(member),
                    receiverPath.HopCount + 1);
                return true;
            }
        }

        if (expression.Kind == LuaSyntaxKind.IndexExpression)
        {
            var nodes = expression.ChildNodes().ToArray();
            if (nodes.Length > 1 && nodes[1].TryGetConstantString(out var member) &&
                TryGetAccessPath(nodes[0], out var receiverPath))
            {
                path = new AccessPathKey(
                    receiverPath.Value + "." + member,
                    receiverPath.HopCount + 1);
                return true;
            }
        }

        path = default;
        return false;
    }

    private bool TryGetCalledIdentifier(LuaSyntaxNode expression, out string name)
    {
        if (expression.Kind == LuaSyntaxKind.ExpressionList)
        {
            var only = GetOnlyChildNodeOrDefault(expression);
            if (only is not null)
            {
                return TryGetCalledIdentifier(only, out name);
            }
        }

        if (expression.Kind is not (LuaSyntaxKind.CallExpression or LuaSyntaxKind.MethodCallExpression))
        {
            name = string.Empty;
            return false;
        }

        var callee = expression.ChildNodes().FirstOrDefault();
        if (callee?.Kind == LuaSyntaxKind.IdentifierExpression)
        {
            name = GetTokenText(callee.ChildTokens().Single());
            return true;
        }

        name = string.Empty;
        return false;
    }

    private bool TryGetCalledGlobalIdentifier(LuaSyntaxNode expression, out string name)
    {
        if (expression.Kind == LuaSyntaxKind.ExpressionList)
        {
            var only = GetOnlyChildNodeOrDefault(expression);
            if (only is not null)
            {
                return TryGetCalledGlobalIdentifier(only, out name);
            }
        }

        if (!TryGetCalledIdentifier(expression, out name))
        {
            return false;
        }

        var callee = expression.ChildNodes().FirstOrDefault();
        if (callee?.Kind != LuaSyntaxKind.IdentifierExpression)
        {
            return false;
        }

        var token = callee.ChildTokens().Single(static item => item.Kind == LuaTokenKind.Identifier);
        return _references.TryGetValue(token.Span, out var reference) &&
            reference.ResolutionKind == LuaNameResolutionKind.Global;
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

    private static LuaSyntaxNode? GetOnlyChildNodeOrDefault(LuaSyntaxNode node)
    {
        LuaSyntaxNode? only = null;
        foreach (var child in node.ChildNodes())
        {
            if (only is not null)
            {
                return null;
            }

            only = child;
        }

        return only;
    }

    private string GetTokenText(LuaSyntaxToken token) =>
        Encoding.UTF8.GetString(_semantics.Syntax.Source.GetSpan(token.Span));

    private static string DecodeLiteral(LuaStringLiteralType literal) =>
        Encoding.UTF8.GetString(literal.Value.AsSpan());

    private readonly record struct ConditionStates(FlowState TrueState, FlowState FalseState);
}
