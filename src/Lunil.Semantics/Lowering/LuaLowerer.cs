using System.Collections.Immutable;
using System.Text;
using Lunil.Core;
using Lunil.Core.Diagnostics;
using Lunil.Core.Text;
using Lunil.IR.Canonical;
using Lunil.Semantics.Binding;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

namespace Lunil.Semantics.Lowering;

/// <summary>Lowers a valid bound Lua syntax tree into verified canonical register IR.</summary>
public static class LuaLowerer
{
    public static LuaLoweringResult Lower(LuaSemanticModel semanticModel)
    {
        ArgumentNullException.ThrowIfNull(semanticModel);
        if (semanticModel.Diagnostics.Any(static diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error))
        {
            return new LuaLoweringResult(null, semanticModel.Diagnostics);
        }

        return new Implementation(semanticModel).Lower();
    }

    private sealed class Implementation
    {
        private readonly LuaSemanticModel _model;
        private readonly LuaIrFunction?[] _functions;
        private readonly Dictionary<int, LuaFunctionInfo> _functionsById;
        private readonly Dictionary<TextSpan, LuaFunctionInfo> _nestedFunctionsBySpan;
        private readonly Dictionary<(int FunctionId, TextSpan Span), LuaSymbol>
            _declaredSymbolsByLocation;
        private readonly Dictionary<TextSpan, LuaNameReference> _references;
        private readonly ImmutableArray<Diagnostic>.Builder _diagnostics =
            ImmutableArray.CreateBuilder<Diagnostic>();
        private readonly HashSet<int> _compiledFunctions = [];

        public Implementation(LuaSemanticModel model)
        {
            _model = model;
            _functions = new LuaIrFunction[model.Functions.Length];
            _functionsById = model.Functions.ToDictionary(static function => function.Id);
            _nestedFunctionsBySpan = model.Functions
                .Where(static function => function.Id != 0)
                .ToDictionary(static function => function.Span);
            _declaredSymbolsByLocation = model.Symbols.ToDictionary(static symbol =>
                (symbol.FunctionId, symbol.DeclaringSpan));
            _references = model.References.ToDictionary(static reference => reference.Span);
        }

        public LuaLoweringResult Lower()
        {
            var mainBlock = _model.Syntax.Root.ChildNodes()
                .Single(static node => node.Kind == LuaSyntaxKind.Block);
            CompileFunction(_functionsById[0], mainBlock, null);

            if (_diagnostics.Count != 0)
            {
                return new LuaLoweringResult(null, _diagnostics.ToImmutable());
            }

            var module = new LuaIrModule
            {
                LanguageVersion = _model.LanguageVersion,
                MainFunctionId = 0,
                Functions = _functions.Select(static function =>
                    function ?? throw new InvalidOperationException("A bound function was not lowered."))
                    .ToImmutableArray(),
            };
            var verificationErrors = LuaIrVerifier.Verify(module);
            foreach (var error in verificationErrors)
            {
                _diagnostics.Add(new Diagnostic(
                    "LUA4002",
                    DiagnosticSeverity.Error,
                    error.ProgramCounter >= 0 && error.FunctionId >= 0
                        ? module.Functions[error.FunctionId].Instructions[error.ProgramCounter].Span
                        : default,
                    error.Message));
            }

            return _diagnostics.Count == 0
                ? new LuaLoweringResult(module, [])
                : new LuaLoweringResult(null, _diagnostics.ToImmutable());
        }

        private void CompileFunction(
            LuaFunctionInfo info,
            LuaSyntaxNode body,
            FunctionBuilder? parent)
        {
            if (!_compiledFunctions.Add(info.Id))
            {
                return;
            }

            var builder = new FunctionBuilder(this, info, parent);
            builder.LowerBody(body);
            _functions[info.Id] = builder.Build();
        }

        private LuaFunctionInfo FindNestedFunction(TextSpan span)
        {
            if (_nestedFunctionsBySpan.TryGetValue(span, out var function) &&
                !_compiledFunctions.Contains(function.Id))
            {
                return function;
            }

            throw new InvalidOperationException($"No uncompiled nested function exists at {span}.");
        }

        private LuaNameReference GetReference(LuaSyntaxToken token)
        {
            if (_references.TryGetValue(token.Span, out var reference))
            {
                return reference;
            }

            throw new InvalidOperationException($"No binding exists for the name at {token.Span}.");
        }

        private LuaSymbol GetDeclaredSymbol(LuaSyntaxToken token, int functionId) =>
            _declaredSymbolsByLocation.TryGetValue((functionId, token.Span), out var symbol)
                ? symbol
                : throw new InvalidOperationException(
                    $"No declared symbol exists for function {functionId} at {token.Span}.");

        private sealed class FunctionBuilder
        {
            private const int Lua54SetListFieldsPerFlush = 50;

            private readonly Implementation _owner;
            private readonly LuaFunctionInfo _info;
            private readonly FunctionBuilder? _parent;
            private readonly List<LuaIrInstruction> _instructions = [];
            private readonly List<LuaIrConstant> _constants = [];
            private readonly List<LuaIrUpvalue> _upvalues = [];
            private readonly Dictionary<int, int> _upvalueBySymbol = [];
            private readonly Dictionary<int, int> _symbolRegisters = [];
            private readonly Dictionary<int, LuaSymbol> _symbolsById;
            private readonly List<int> _activeSymbolIds = [];
            private readonly List<int> _activeSyntheticCloseRegisters = [];
            private readonly List<int> _activeToBeClosedRegisters = [];
            private readonly List<Scope> _scopes = [];
            private readonly List<GotoPatch> _gotos = [];
            private readonly List<PendingLocal> _locals = [];
            private readonly Dictionary<int, PendingLocal> _localBySymbol = [];
            private readonly Stack<LoopContext> _loops = [];
            private int _nextRegister;
            private int _localTop;
            private int _maximumRegister;
            private int _varArgTableRegister = -1;

            public FunctionBuilder(Implementation owner, LuaFunctionInfo info, FunctionBuilder? parent)
            {
                _owner = owner;
                _info = info;
                _parent = parent;
                _symbolsById = info.Symbols.ToDictionary(static symbol => symbol.Id);
                InitializeUpvalues();
                EnterScope();
                InitializeParameters();
            }

            public void LowerBody(LuaSyntaxNode body)
            {
                LowerBlock(body, createScope: false);
                var terminalOffset = body.Span.Length == 0 ? body.Span.Start : body.Span.End - 1;
                Emit(new LuaIrInstruction(
                    LuaIrOpcode.Return,
                    0,
                    0,
                    span: body.Span,
                    sourceLine: _owner._model.Syntax.Source.GetLocation(terminalOffset).Line + 1));
                ResolveGotos();
            }

            public LuaIrFunction Build()
            {
                var code = _instructions.ToImmutableArray();
                if (_maximumRegister > 255)
                {
                    _owner._diagnostics.Add(new Diagnostic(
                        "LUA4003",
                        DiagnosticSeverity.Error,
                        _info.Span,
                        "Function or expression needs too many registers."));
                }

                foreach (var local in _locals)
                {
                    local.EndProgramCounter ??= code.Length;
                }

                return new LuaIrFunction
                {
                    Id = _info.Id,
                    ParentFunctionId = _parent?._info.Id ?? -1,
                    Span = _info.Span,
                    LineDefined = _info.Id == 0
                        ? 0
                        : _owner._model.Syntax.Source.GetLocation(_info.Span.Start).Line + 1,
                    LastLineDefined = _info.Id == 0
                        ? 0
                        : _owner._model.Syntax.Source.GetLocation(_info.Span.End).Line + 1,
                    ParameterCount = _info.Symbols.Count(static symbol =>
                        symbol.Kind == LuaSymbolKind.Parameter),
                    IsVarArg = _info.IsVarArg,
                    RegisterCount = Math.Max(_maximumRegister, 1),
                    Constants = _constants.ToImmutableArray(),
                    Upvalues = _upvalues.ToImmutableArray(),
                    Instructions = code,
                    LocalVariables =
                    [
                        .. _locals.Select(local => new LuaIrLocalVariable(
                            Encoding.UTF8.GetBytes(local.Name).ToImmutableArray(),
                            local.StartProgramCounter,
                            local.EndProgramCounter!.Value)),
                    ],
                    BasicBlocks = LuaIrControlFlow.Build(code),
                };
            }

            private void InitializeUpvalues()
            {
                if (_parent is null)
                {
                    var environment = _info.Symbols.Single(static symbol =>
                        symbol.Kind == LuaSymbolKind.Environment);
                    AddUpvalue(environment, LuaIrUpvalueSourceKind.Environment, 0);
                    return;
                }

                foreach (var capture in _info.Captures)
                {
                    if (_parent._symbolRegisters.TryGetValue(capture.Id, out var register))
                    {
                        AddUpvalue(capture, LuaIrUpvalueSourceKind.Register, register);
                    }
                    else if (_parent._upvalueBySymbol.TryGetValue(capture.Id, out var upvalue))
                    {
                        AddUpvalue(capture, LuaIrUpvalueSourceKind.Upvalue, upvalue);
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"The parent function cannot provide captured symbol '{capture.Name}'.");
                    }
                }
            }

            private void AddUpvalue(
                LuaSymbol symbol,
                LuaIrUpvalueSourceKind sourceKind,
                int sourceIndex)
            {
                _upvalueBySymbol.Add(symbol.Id, _upvalues.Count);
                _upvalues.Add(new LuaIrUpvalue(symbol.Name, symbol.Id, sourceKind, sourceIndex));
            }

            private void InitializeParameters()
            {
                foreach (var parameter in _info.Symbols.Where(static symbol =>
                             symbol.Kind == LuaSymbolKind.Parameter))
                {
                    DeclareSymbol(parameter, markToBeClosed: false, parameter.DeclaringSpan);
                }

                var namedVarArg = _info.Symbols.SingleOrDefault(static symbol =>
                    symbol.Attribute == LuaLocalAttributeKind.VarArg);
                if (namedVarArg is not null)
                {
                    _varArgTableRegister = DeclareSymbol(
                        namedVarArg,
                        markToBeClosed: false,
                        namedVarArg.DeclaringSpan);
                    Emit(new LuaIrInstruction(
                        LuaIrOpcode.CreateVarArgTable,
                        _varArgTableRegister,
                        span: namedVarArg.DeclaringSpan));
                }
            }

            private void LowerBlock(
                LuaSyntaxNode block,
                bool createScope = true,
                bool terminalLabelsEndScope = true)
            {
                if (createScope)
                {
                    EnterScope();
                }

                var statements = block.ChildNodes().ToArray();
                var terminalScopeClosed = false;
                for (var index = 0; index < statements.Length; index++)
                {
                    var statement = statements[index];
                    if (terminalLabelsEndScope && !terminalScopeClosed &&
                        statement.Kind == LuaSyntaxKind.LabelStatement &&
                        statements[index..].All(static following =>
                            following.Kind is LuaSyntaxKind.LabelStatement or LuaSyntaxKind.EmptyStatement))
                    {
                        CloseCurrentScope(statement.Span);
                        DeactivateCurrentScope();
                        terminalScopeClosed = true;
                    }

                    LowerStatement(statement);
                    ResetTemporaries();
                }

                if (createScope)
                {
                    if (!terminalScopeClosed)
                    {
                        CloseCurrentScope(block.Span);
                    }

                    ExitScope();
                }
            }

            private void LowerStatement(LuaSyntaxNode statement)
            {
                switch (statement.Kind)
                {
                    case LuaSyntaxKind.EmptyStatement:
                    case LuaSyntaxKind.Error:
                        return;
                    case LuaSyntaxKind.AssignmentStatement:
                        LowerAssignment(statement);
                        return;
                    case LuaSyntaxKind.CallStatement:
                        {
                            var destination = Reserve(1);
                            LowerExpression(statement.ChildNodes().Single(), destination, 0);
                            return;
                        }
                    case LuaSyntaxKind.LabelStatement:
                        DefineLabel(statement);
                        return;
                    case LuaSyntaxKind.BreakStatement:
                        LowerBreak(statement);
                        return;
                    case LuaSyntaxKind.GotoStatement:
                        LowerGoto(statement);
                        return;
                    case LuaSyntaxKind.DoStatement:
                        LowerBlock(GetChild(statement, LuaSyntaxKind.Block));
                        return;
                    case LuaSyntaxKind.WhileStatement:
                        LowerWhile(statement);
                        return;
                    case LuaSyntaxKind.RepeatStatement:
                        LowerRepeat(statement);
                        return;
                    case LuaSyntaxKind.IfStatement:
                        LowerIf(statement);
                        return;
                    case LuaSyntaxKind.NumericForStatement:
                        LowerNumericFor(statement);
                        return;
                    case LuaSyntaxKind.GenericForStatement:
                        LowerGenericFor(statement);
                        return;
                    case LuaSyntaxKind.FunctionDeclarationStatement:
                        LowerFunctionDeclaration(statement);
                        return;
                    case LuaSyntaxKind.GlobalDeclarationStatement:
                        LowerGlobalDeclaration(statement);
                        return;
                    case LuaSyntaxKind.LocalFunctionDeclarationStatement:
                        LowerLocalFunction(statement);
                        return;
                    case LuaSyntaxKind.LocalDeclarationStatement:
                        LowerLocalDeclaration(statement);
                        return;
                    case LuaSyntaxKind.ReturnStatement:
                        LowerReturn(statement);
                        return;
                    default:
                        throw new InvalidOperationException($"Unexpected statement {statement.Kind}.");
                }
            }

            private void LowerAssignment(LuaSyntaxNode statement)
            {
                var variables = GetChild(statement, LuaSyntaxKind.VariableList).ChildNodes().ToArray();
                var targets = variables.Select(PrepareTarget).ToArray();
                var values = Reserve(variables.Length);
                var expressionList = GetChild(statement, LuaSyntaxKind.ExpressionList);
                LowerExpressionList(expressionList, values, variables.Length);
                var finalExpression = expressionList.ChildNodes().Last();
                var storeLine = SourceLineAt(finalExpression.Span.Length == 0
                    ? finalExpression.Span.Start
                    : finalExpression.Span.End - 1);
                for (var index = 0; index < targets.Length; index++)
                {
                    StoreTarget(targets[index], values + index, statement.Span, storeLine);
                }
            }

            private AssignmentTarget PrepareTarget(LuaSyntaxNode expression)
            {
                if (expression.Kind == LuaSyntaxKind.IdentifierExpression)
                {
                    var token = IdentifierToken(expression);
                    return PrepareNameTarget(_owner.GetReference(token), token);
                }

                var nodes = expression.ChildNodes().ToArray();
                var table = Reserve(1);
                LowerExpression(nodes[0], table, 1);
                var key = Reserve(1);
                if (expression.Kind == LuaSyntaxKind.MemberAccessExpression)
                {
                    LoadString(key, IdentifierToken(expression), expression.Span);
                }
                else
                {
                    LowerExpression(nodes[1], key, 1);
                }

                return AssignmentTarget.Table(table, key);
            }

            private AssignmentTarget PrepareNameTarget(
                LuaNameReference reference,
                LuaSyntaxToken token)
            {
                return reference.ResolutionKind switch
                {
                    LuaNameResolutionKind.Local =>
                        AssignmentTarget.Register(_symbolRegisters[reference.Symbol.Id]),
                    LuaNameResolutionKind.Upvalue =>
                        AssignmentTarget.Upvalue(_upvalueBySymbol[reference.Symbol.Id]),
                    LuaNameResolutionKind.Global => PrepareGlobalTarget(reference.Symbol, token),
                    _ => throw new InvalidOperationException("Unknown name resolution kind."),
                };
            }

            private AssignmentTarget PrepareGlobalTarget(LuaSymbol environment, LuaSyntaxToken token)
            {
                var firstInstruction = _instructions.Count;
                var table = Reserve(1);
                LoadSymbol(environment, table, token.Span);
                var key = Reserve(1);
                LoadString(key, token, token.Span);
                for (var index = firstInstruction; index < _instructions.Count; index++)
                {
                    _instructions[index] = _instructions[index] with { SourceLine = 0 };
                }

                return AssignmentTarget.Table(table, key);
            }

            private void StoreTarget(
                AssignmentTarget target,
                int source,
                TextSpan span,
                int sourceLine = 0)
            {
                switch (target.Kind)
                {
                    case AssignmentTargetKind.Register:
                        if (target.First != source)
                        {
                            Emit(new LuaIrInstruction(
                                LuaIrOpcode.Move,
                                target.First,
                                source,
                                span: span,
                                sourceLine: sourceLine));
                        }

                        break;
                    case AssignmentTargetKind.Upvalue:
                        Emit(new LuaIrInstruction(
                            LuaIrOpcode.SetUpvalue,
                            target.First,
                            source,
                            span: span,
                            sourceLine: sourceLine));
                        break;
                    case AssignmentTargetKind.Table:
                        Emit(new LuaIrInstruction(
                            LuaIrOpcode.SetTable,
                            target.First,
                            target.Second,
                            source,
                            span: span,
                            sourceLine: sourceLine));
                        break;
                }
            }

            private void LowerLocalDeclaration(LuaSyntaxNode statement)
            {
                var declarations = statement.ChildNodes()
                    .Where(static node => node.Kind == LuaSyntaxKind.AttributedName)
                    .ToArray();
                var baseRegister = _localTop;
                Reserve(declarations.Length);
                var initializer = statement.ChildNodes().FirstOrDefault(static node =>
                    node.Kind == LuaSyntaxKind.ExpressionList);
                if (initializer is null)
                {
                    Emit(new LuaIrInstruction(
                        LuaIrOpcode.LoadNil,
                        baseRegister,
                        declarations.Length,
                        span: statement.Span));
                }
                else
                {
                    LowerExpressionList(initializer, baseRegister, declarations.Length);
                }

                _nextRegister = baseRegister;
                foreach (var declaration in declarations)
                {
                    var token = IdentifierToken(declaration);
                    var symbol = _owner.GetDeclaredSymbol(token, _info.Id);
                    DeclareSymbol(
                        symbol,
                        symbol.Attribute == LuaLocalAttributeKind.ToBeClosed,
                        declaration.Span);
                }
            }

            private void LowerGlobalDeclaration(LuaSyntaxNode statement)
            {
                var directTokens = statement.ChildTokens().ToArray();
                if (directTokens.Any(static token => token.Kind == LuaTokenKind.FunctionKeyword))
                {
                    var token = directTokens.First(static candidate =>
                        candidate.Kind == LuaTokenKind.Identifier);
                    var target = PrepareNameTarget(_owner.GetReference(token), token);
                    var closure = Reserve(1);
                    LowerClosure(statement, GetChild(statement, LuaSyntaxKind.FunctionBody), closure);
                    CheckAndStoreGlobal(target, token, closure, statement.Span);
                    return;
                }

                var initializer = statement.ChildNodes().FirstOrDefault(static node =>
                    node.Kind == LuaSyntaxKind.ExpressionList);
                if (initializer is null)
                {
                    return;
                }

                var declarations = statement.ChildNodes()
                    .Where(static node => node.Kind == LuaSyntaxKind.AttributedName)
                    .Select(IdentifierToken)
                    .ToArray();
                var targets = declarations.Select(token =>
                    PrepareNameTarget(_owner.GetReference(token), token)).ToArray();
                var values = Reserve(declarations.Length);
                LowerExpressionList(initializer, values, declarations.Length);
                for (var index = declarations.Length - 1; index >= 0; index--)
                {
                    CheckAndStoreGlobal(
                        targets[index],
                        declarations[index],
                        values + index,
                        statement.Span);
                }
            }

            private void CheckAndStoreGlobal(
                AssignmentTarget target,
                LuaSyntaxToken name,
                int source,
                TextSpan span)
            {
                if (target.Kind != AssignmentTargetKind.Table)
                {
                    throw new InvalidOperationException("A global declaration must target _ENV.");
                }

                var current = Reserve(1);
                Emit(new LuaIrInstruction(
                    LuaIrOpcode.GetTable,
                    current,
                    target.First,
                    target.Second,
                    span: span));
                var nameConstant = GetOrAddConstant(LuaIrConstant.FromString(
                    _owner._model.Syntax.Source.GetSpan(name.Span)));
                Emit(new LuaIrInstruction(
                    LuaIrOpcode.ErrorIfNotNil,
                    current,
                    nameConstant,
                    span: name.Span));
                StoreTarget(target, source, span, SourceLineAt(name.Span.Start));
            }

            private void LowerLocalFunction(LuaSyntaxNode statement)
            {
                var token = statement.ChildTokens().First(static candidate =>
                    candidate.Kind == LuaTokenKind.Identifier);
                var symbol = _owner.GetDeclaredSymbol(token, _info.Id);
                var register = DeclareSymbol(symbol, markToBeClosed: false, statement.Span);
                LowerClosure(statement, GetChild(statement, LuaSyntaxKind.FunctionBody), register);
            }

            private void LowerFunctionDeclaration(LuaSyntaxNode statement)
            {
                var functionName = GetChild(statement, LuaSyntaxKind.FunctionName);
                var tokens = functionName.ChildTokens().Where(static token =>
                    token.Kind == LuaTokenKind.Identifier).ToArray();
                var closure = Reserve(1);
                LowerClosure(statement, GetChild(statement, LuaSyntaxKind.FunctionBody), closure);

                if (tokens.Length == 1)
                {
                    StoreTarget(
                        PrepareNameTarget(_owner.GetReference(tokens[0]), tokens[0]),
                        closure,
                        statement.Span);
                    return;
                }

                var table = Reserve(1);
                LoadName(_owner.GetReference(tokens[0]), tokens[0], table);
                for (var index = 1; index < tokens.Length - 1; index++)
                {
                    var key = Reserve(1);
                    LoadString(key, tokens[index], tokens[index].Span);
                    Emit(new LuaIrInstruction(LuaIrOpcode.GetTable, table, table, key, span: statement.Span));
                }

                var finalKey = Reserve(1);
                LoadString(finalKey, tokens[^1], tokens[^1].Span);
                Emit(new LuaIrInstruction(LuaIrOpcode.SetTable, table, finalKey, closure, span: statement.Span));
            }

            private void LowerClosure(LuaSyntaxNode functionNode, LuaSyntaxNode body, int destination)
            {
                var nested = _owner.FindNestedFunction(functionNode.Span);
                _owner.CompileFunction(nested, GetChild(body, LuaSyntaxKind.Block), this);
                Emit(new LuaIrInstruction(
                    LuaIrOpcode.Closure,
                    destination,
                    nested.Id,
                    span: body.ChildTokens().Last(static token =>
                        token.Kind == LuaTokenKind.EndKeyword).Span));
            }

            private void LowerReturn(LuaSyntaxNode statement)
            {
                var expressionList = statement.ChildNodes().FirstOrDefault(static child =>
                    child.Kind == LuaSyntaxKind.ExpressionList);
                if (expressionList is null)
                {
                    Emit(new LuaIrInstruction(LuaIrOpcode.Return, 0, 0, span: statement.Span));
                    return;
                }

                var expressions = expressionList.ChildNodes().ToArray();
                var first = Reserve(Math.Max(expressions.Length, 1));
                if (expressions.Length == 1 && expressions[0].Kind is
                    LuaSyntaxKind.CallExpression or LuaSyntaxKind.MethodCallExpression)
                {
                    LowerExpression(expressions[0], first, -1);
                    var call = _instructions[^1];
                    if (call.Opcode != LuaIrOpcode.Call)
                    {
                        throw new InvalidOperationException(
                            "A tail-call expression did not end in a call instruction.");
                    }

                    if (_activeToBeClosedRegisters.Count == 0)
                    {
                        _instructions[^1] = call with { Opcode = LuaIrOpcode.TailCall };
                    }
                    else
                    {
                        Emit(new LuaIrInstruction(
                            LuaIrOpcode.Return,
                            first,
                            -1,
                            span: statement.Span));
                    }

                    return;
                }

                var open = IsExpandable(expressions[^1]);
                LowerExpressionList(expressionList, first, open ? -1 : expressions.Length);
                Emit(new LuaIrInstruction(
                    LuaIrOpcode.Return,
                    first,
                    open ? -1 : expressions.Length,
                    span: statement.Span));
            }

            private void LowerIf(LuaSyntaxNode statement)
            {
                var endJumps = new List<int>();
                var nodes = statement.ChildNodes().ToArray();
                var thenSpan = statement.ChildTokens().First(static token =>
                    token.Kind == LuaTokenKind.ThenKeyword).Span;
                var endSpan = statement.ChildTokens().Last(static token =>
                    token.Kind == LuaTokenKind.EndKeyword).Span;
                LowerConditionalPart(nodes[0], nodes[1], endJumps, endSpan, thenSpan);
                foreach (var clause in nodes.Skip(2))
                {
                    if (clause.Kind == LuaSyntaxKind.ElseIfClause)
                    {
                        var clauseNodes = clause.ChildNodes().ToArray();
                        var clauseThenSpan = clause.ChildTokens().First(static token =>
                            token.Kind == LuaTokenKind.ThenKeyword).Span;
                        LowerConditionalPart(
                            clauseNodes[0], clauseNodes[1], endJumps, endSpan, clauseThenSpan);
                    }
                    else if (clause.Kind == LuaSyntaxKind.ElseClause)
                    {
                        LowerBlock(GetChild(clause, LuaSyntaxKind.Block));
                    }
                }

                var end = _instructions.Count;
                foreach (var jump in endJumps)
                {
                    PatchTarget(jump, end);
                }
            }

            private void LowerConditionalPart(
                LuaSyntaxNode condition,
                LuaSyntaxNode block,
                List<int> endJumps,
                TextSpan span,
                TextSpan controlSpan)
            {
                var test = Reserve(1);
                LowerExpression(condition, test, 1);
                var falseJump = Emit(new LuaIrInstruction(
                    LuaIrOpcode.JumpIfFalse,
                    test,
                    -1,
                    span: controlSpan));
                ResetTemporaries();
                LowerBlock(block);
                endJumps.Add(Emit(new LuaIrInstruction(LuaIrOpcode.Jump, b: -1, c: -1, span: span)));
                PatchTarget(falseJump, _instructions.Count);
            }

            private void LowerWhile(LuaSyntaxNode statement)
            {
                var nodes = statement.ChildNodes().ToArray();
                var conditionPc = _instructions.Count;
                var condition = Reserve(1);
                LowerExpression(nodes[0], condition, 1);
                var exitJump = Emit(new LuaIrInstruction(
                    LuaIrOpcode.JumpIfFalse,
                    condition,
                    -1,
                    span: nodes[0].Span));
                ResetTemporaries();
                var loop = new LoopContext(_scopes[^1], ActiveRegisters());
                _loops.Push(loop);
                LowerBlock(GetChild(statement, LuaSyntaxKind.Block));
                _loops.Pop();
                Emit(new LuaIrInstruction(LuaIrOpcode.Jump, b: conditionPc, c: -1, span: statement.Span));
                var end = _instructions.Count;
                PatchTarget(exitJump, end);
                PatchBreaks(loop, end);
            }

            private void LowerRepeat(LuaSyntaxNode statement)
            {
                EnterScope();
                var start = _instructions.Count;
                var loop = new LoopContext(_scopes[^1], ActiveRegisters());
                _loops.Push(loop);
                var nodes = statement.ChildNodes().ToArray();
                LowerBlock(
                    GetChild(statement, LuaSyntaxKind.Block),
                    createScope: false,
                    terminalLabelsEndScope: false);
                var condition = Reserve(1);
                LowerExpression(nodes[^1], condition, 1);
                var exit = Emit(new LuaIrInstruction(
                    LuaIrOpcode.JumpIfTrue,
                    condition,
                    -1,
                    span: nodes[^1].Span));
                CloseCurrentScope(statement.Span);
                Emit(new LuaIrInstruction(LuaIrOpcode.SetTop, _scopes[^1].EntryRegister));
                Emit(new LuaIrInstruction(LuaIrOpcode.Jump, b: start, c: -1, span: nodes[^1].Span));
                PatchTarget(exit, _instructions.Count);
                _loops.Pop();
                CloseCurrentScope(statement.Span);
                ExitScope();
                var end = _instructions.Count;
                PatchBreaks(loop, end);
            }

            private void LowerNumericFor(LuaSyntaxNode statement)
            {
                var nodes = statement.ChildNodes().ToArray();
                var body = GetChild(statement, LuaSyntaxKind.Block);
                var expressions = nodes.Where(node => !ReferenceEquals(node, body)).ToArray();
                EnterScope();
                var baseRegister = Reserve(4);
                LowerExpression(expressions[0], baseRegister, 1);
                LowerExpression(expressions[1], baseRegister + 1, 1);
                if (expressions.Length > 2)
                {
                    LowerExpression(expressions[2], baseRegister + 2, 1);
                }
                else
                {
                    LoadConstant(baseRegister + 2, LuaIrConstant.FromInteger(1), statement.Span);
                }

                _nextRegister = baseRegister + 3;
                var name = statement.ChildTokens().First(static token =>
                    token.Kind == LuaTokenKind.Identifier);
                var symbol = _owner.GetDeclaredSymbol(name, _info.Id);
                DeclareSymbol(symbol, markToBeClosed: false, name.Span);
                var prepare = Emit(new LuaIrInstruction(
                    LuaIrOpcode.NumericForPrepare,
                    baseRegister,
                    -1,
                    span: statement.Span));
                var bodyStart = _instructions.Count;
                var loop = new LoopContext(_scopes[^1], ActiveRegisters());
                _loops.Push(loop);
                // A numeric-for body is a fresh lexical scope on every iteration. Keeping the
                // body in the control scope leaves its locals live in the thread root set until
                // the loop exits, which is observable through finalizers and weak tables.
                LowerBlock(body);
                _loops.Pop();
                CloseCurrentScope(statement.Span, baseRegister + 3);
                Emit(new LuaIrInstruction(LuaIrOpcode.SetTop, baseRegister + 4));
                Emit(new LuaIrInstruction(
                    LuaIrOpcode.NumericForLoop,
                    baseRegister,
                    bodyStart,
                    span: statement.Span));
                var end = _instructions.Count;
                PatchTarget(prepare, end);
                PatchBreaks(loop, end);
                CloseCurrentScope(statement.Span);
                ExitScope();
            }

            private void LowerGenericFor(LuaSyntaxNode statement)
            {
                EnterScope();
                var expressionList = GetChild(statement, LuaSyntaxKind.ExpressionList);
                var iteratorExpression = expressionList.ChildNodes().First();
                var iteratorLine = SourceLineAt(iteratorExpression.Span.Start);
                var controlBase = Reserve(4);
                LowerExpressionList(expressionList, controlBase, 4);
                _nextRegister = controlBase + 4;
                _localTop = _nextRegister;
                if (_owner._model.LanguageVersion == LuaLanguageVersion.Lua54)
                {
                    Emit(new LuaIrInstruction(
                        LuaIrOpcode.MarkToBeClosed,
                        controlBase + 3,
                        span: statement.Span));
                    // Lua 5.4 models the fourth generic-for control register as an implicit
                    // to-be-closed local. Lua 5.3 has no close protocol for this register.
                    _activeSyntheticCloseRegisters.Add(controlBase + 3);
                    _activeToBeClosedRegisters.Add(controlBase + 3);
                }
                for (var index = 0; index < 4; index++)
                {
                    AddSyntheticLocal("(for state)");
                }

                var nameTokens = GetChild(statement, LuaSyntaxKind.NameList).ChildTokens()
                    .Where(static token => token.Kind == LuaTokenKind.Identifier).ToArray();
                var variableBase = _localTop;
                foreach (var token in nameTokens)
                {
                    DeclareSymbol(
                        _owner.GetDeclaredSymbol(token, _info.Id),
                        markToBeClosed: false,
                        token.Span);
                }

                var loopStart = _instructions.Count;
                var call = Reserve(3 + nameTokens.Length);
                Emit(new LuaIrInstruction(LuaIrOpcode.Move, call, controlBase, span: statement.Span));
                Emit(new LuaIrInstruction(LuaIrOpcode.Move, call + 1, controlBase + 1, span: statement.Span));
                Emit(new LuaIrInstruction(LuaIrOpcode.Move, call + 2, controlBase + 2, span: statement.Span));
                Emit(new LuaIrInstruction(
                    LuaIrOpcode.Call,
                    call,
                    2,
                    nameTokens.Length,
                    (int)LuaIrCallKind.ForIterator,
                    span: statement.Span,
                    sourceLine: iteratorLine));
                for (var index = 0; index < nameTokens.Length; index++)
                {
                    Emit(new LuaIrInstruction(
                        LuaIrOpcode.Move,
                        variableBase + index,
                        call + index,
                        span: statement.Span));
                }

                Emit(new LuaIrInstruction(LuaIrOpcode.Move, controlBase + 2, variableBase, span: statement.Span));
                var exit = Emit(new LuaIrInstruction(
                    LuaIrOpcode.JumpIfFalse,
                    variableBase,
                    -1,
                    span: statement.Span));
                ResetTemporaries();
                var loop = new LoopContext(_scopes[^1], ActiveRegisters());
                _loops.Push(loop);
                LowerBlock(GetChild(statement, LuaSyntaxKind.Block), createScope: false);
                _loops.Pop();
                CloseCurrentScope(statement.Span, variableBase);
                Emit(new LuaIrInstruction(LuaIrOpcode.SetTop, variableBase));
                Emit(new LuaIrInstruction(LuaIrOpcode.Jump, b: loopStart, c: -1, span: statement.Span));
                var end = _instructions.Count;
                PatchTarget(exit, end);
                PatchBreaks(loop, end);
                CloseCurrentScope(statement.Span);
                ExitScope();
            }

            private void LowerBreak(LuaSyntaxNode statement)
            {
                var loop = _loops.Peek();
                var closeBase = FindCloseBase(ActiveRegisters(), loop.ActiveSymbols);
                loop.Breaks.Add(Emit(new LuaIrInstruction(
                    LuaIrOpcode.Jump,
                    b: -1,
                    c: closeBase,
                    span: statement.Span)));
            }

            private void PatchBreaks(LoopContext loop, int target)
            {
                foreach (var jump in loop.Breaks)
                {
                    PatchTarget(jump, target);
                }
            }

            private void LowerGoto(LuaSyntaxNode statement)
            {
                var token = statement.ChildTokens().First(static token =>
                    token.Kind == LuaTokenKind.Identifier);
                var pc = Emit(new LuaIrInstruction(LuaIrOpcode.Jump, b: -1, c: -1, span: statement.Span));
                _gotos.Add(new GotoPatch(
                    ReadAscii(token),
                    pc,
                    _scopes[^1],
                    ActiveRegisters()));
            }

            private void DefineLabel(LuaSyntaxNode statement)
            {
                var token = statement.ChildTokens().First(static candidate =>
                    candidate.Kind == LuaTokenKind.Identifier);
                _scopes[^1].Labels.Add(
                    ReadAscii(token),
                    new Label(_instructions.Count, ActiveRegisters()));
            }

            private void ResolveGotos()
            {
                foreach (var @goto in _gotos)
                {
                    Label? label = null;
                    for (var scope = @goto.Scope; scope is not null; scope = scope.Parent)
                    {
                        if (scope.Labels.TryGetValue(@goto.Name, out label))
                        {
                            break;
                        }
                    }

                    if (label is null)
                    {
                        throw new InvalidOperationException($"Bound goto '{@goto.Name}' has no label.");
                    }

                    var instruction = _instructions[@goto.ProgramCounter];
                    _instructions[@goto.ProgramCounter] = instruction with
                    {
                        B = label.ProgramCounter,
                        C = FindCloseBase(@goto.ActiveSymbols, label.ActiveSymbols),
                    };
                }
            }

            private static int FindCloseBase(int[] source, int[] target)
            {
                var common = 0;
                while (common < source.Length && common < target.Length && source[common] == target[common])
                {
                    common++;
                }

                if (common == source.Length)
                {
                    return -1;
                }

                return source[common];
            }

            private int[] ActiveRegisters() =>
                [
                    .. _activeSymbolIds.Select(symbolId => _symbolRegisters[symbolId])
                        .Concat(_activeSyntheticCloseRegisters)
                        .Order(),
                ];

            private void LowerExpressionList(LuaSyntaxNode list, int destination, int resultCount)
            {
                var expressions = list.ChildNodes().ToArray();
                var fixedCount = resultCount < 0 ? expressions.Length : resultCount;
                EnsureRegisters(destination, Math.Max(fixedCount, 1));
                for (var index = 0; index < expressions.Length; index++)
                {
                    var isLast = index == expressions.Length - 1;
                    var requested = isLast
                        ? resultCount < 0 ? -1 : Math.Max(resultCount - index, 0)
                        : 1;
                    var target = index < fixedCount ? destination + index : Reserve(1);
                    LowerExpression(expressions[index], target, requested);
                }

            }

            private void LowerExpression(LuaSyntaxNode expression, int destination, int resultCount)
            {
                EnsureRegisters(destination, Math.Max(resultCount, 1));
                switch (expression.Kind)
                {
                    case LuaSyntaxKind.NilLiteralExpression:
                        Emit(new LuaIrInstruction(LuaIrOpcode.LoadNil, destination, Math.Max(resultCount, 1), span: expression.Span));
                        return;
                    case LuaSyntaxKind.FalseLiteralExpression:
                        LoadConstant(destination, LuaIrConstant.FromBoolean(false), expression.Span);
                        FillExtraNil(destination, resultCount, expression.Span);
                        return;
                    case LuaSyntaxKind.TrueLiteralExpression:
                        LoadConstant(destination, LuaIrConstant.FromBoolean(true), expression.Span);
                        FillExtraNil(destination, resultCount, expression.Span);
                        return;
                    case LuaSyntaxKind.NumericLiteralExpression:
                        LowerNumericLiteral(expression, destination);
                        FillExtraNil(destination, resultCount, expression.Span);
                        return;
                    case LuaSyntaxKind.StringLiteralExpression:
                        LoadString(destination, expression.ChildTokens().Single(), expression.Span);
                        FillExtraNil(destination, resultCount, expression.Span);
                        return;
                    case LuaSyntaxKind.IdentifierExpression:
                        {
                            var token = IdentifierToken(expression);
                            LoadName(_owner.GetReference(token), token, destination);
                            FillExtraNil(destination, resultCount, expression.Span);
                            return;
                        }
                    case LuaSyntaxKind.ParenthesizedExpression:
                        LowerExpression(expression.ChildNodes().Single(), destination, 1);
                        FillExtraNil(destination, resultCount, expression.Span);
                        return;
                    case LuaSyntaxKind.VarArgExpression:
                        Emit(new LuaIrInstruction(
                            LuaIrOpcode.VarArg,
                            destination,
                            resultCount,
                            _varArgTableRegister + 1,
                            span: expression.Span));
                        return;
                    case LuaSyntaxKind.UnaryExpression:
                        LowerUnary(expression, destination);
                        FillExtraNil(destination, resultCount, expression.Span);
                        return;
                    case LuaSyntaxKind.BinaryExpression:
                        LowerBinary(expression, destination);
                        FillExtraNil(destination, resultCount, expression.Span);
                        return;
                    case LuaSyntaxKind.FunctionExpression:
                        LowerClosure(expression, GetChild(expression, LuaSyntaxKind.FunctionBody), destination);
                        FillExtraNil(destination, resultCount, expression.Span);
                        return;
                    case LuaSyntaxKind.TableConstructorExpression:
                        LowerTable(expression, destination);
                        FillExtraNil(destination, resultCount, expression.Span);
                        return;
                    case LuaSyntaxKind.IndexExpression:
                    case LuaSyntaxKind.MemberAccessExpression:
                        LowerIndex(expression, destination);
                        FillExtraNil(destination, resultCount, expression.Span);
                        return;
                    case LuaSyntaxKind.CallExpression:
                    case LuaSyntaxKind.MethodCallExpression:
                        LowerCall(expression, destination, resultCount);
                        return;
                    case LuaSyntaxKind.Error:
                        Emit(new LuaIrInstruction(LuaIrOpcode.LoadNil, destination, Math.Max(resultCount, 1), span: expression.Span));
                        return;
                    default:
                        throw new InvalidOperationException($"Unexpected expression {expression.Kind}.");
                }
            }

            private void LowerNumericLiteral(LuaSyntaxNode expression, int destination)
            {
                var value = expression.ChildTokens().Single().Value;
                var constant = value switch
                {
                    LuaIntegerTokenValue integer when _owner._model.LanguageVersion is
                        LuaLanguageVersion.Lua51 or LuaLanguageVersion.Lua52 =>
                        LuaIrConstant.FromFloat(integer.Integer),
                    LuaIntegerTokenValue integer => LuaIrConstant.FromInteger(integer.Integer),
                    LuaFloatTokenValue floatingPoint => LuaIrConstant.FromFloat(floatingPoint.Float),
                    _ => throw new InvalidOperationException("A numeric token has no decoded value."),
                };
                LoadConstant(destination, constant, expression.Span);
            }

            private void LowerUnary(LuaSyntaxNode expression, int destination)
            {
                var operand = Reserve(1);
                LowerExpression(expression.ChildNodes().Single(), operand, 1);
                var op = expression.ChildTokens().Single().Kind switch
                {
                    LuaTokenKind.Minus => LuaIrUnaryOperator.Negate,
                    LuaTokenKind.Tilde => LuaIrUnaryOperator.BitwiseNot,
                    LuaTokenKind.NotKeyword => LuaIrUnaryOperator.LogicalNot,
                    LuaTokenKind.Length => LuaIrUnaryOperator.Length,
                    var kind => throw new InvalidOperationException($"Unexpected unary operator {kind}."),
                };
                Emit(new LuaIrInstruction(LuaIrOpcode.Unary, destination, operand, (int)op, span: expression.Span));
            }

            private void LowerBinary(LuaSyntaxNode expression, int destination)
            {
                var nodes = expression.ChildNodes().ToArray();
                var token = expression.ChildTokens().Single();
                if (token.Kind is LuaTokenKind.AndKeyword or LuaTokenKind.OrKeyword)
                {
                    LowerExpression(nodes[0], destination, 1);
                    var jump = Emit(new LuaIrInstruction(
                        token.Kind == LuaTokenKind.AndKeyword
                            ? LuaIrOpcode.JumpIfFalse
                            : LuaIrOpcode.JumpIfTrue,
                        destination,
                        -1,
                        span: token.Span));
                    LowerExpression(nodes[1], destination, 1);
                    PatchTarget(jump, _instructions.Count);
                    return;
                }

                var temporaryBase = _nextRegister;
                var leftStart = _instructions.Count;
                LowerExpression(nodes[0], destination, 1);
                _nextRegister = temporaryBase;
                var leftLine = SourceLineAt(nodes[0].Span.Start);
                var operatorLine = SourceLineAt(token.Span.Start);
                if (leftLine != operatorLine)
                {
                    for (var index = leftStart; index < _instructions.Count; index++)
                    {
                        if (_instructions[index].SourceLine == leftLine)
                        {
                            _instructions[index] = _instructions[index] with
                            {
                                SourceLine = operatorLine,
                            };
                        }
                    }
                }

                var right = Reserve(1);
                LowerExpression(nodes[1], right, 1);
                Emit(new LuaIrInstruction(
                    LuaIrOpcode.Binary,
                    destination,
                    destination,
                    right,
                    (int)GetBinaryOperator(token.Kind),
                    expression.Span,
                    sourceLine: operatorLine));
                _nextRegister = temporaryBase;
            }

            private static LuaIrBinaryOperator GetBinaryOperator(LuaTokenKind kind) => kind switch
            {
                LuaTokenKind.Plus => LuaIrBinaryOperator.Add,
                LuaTokenKind.Minus => LuaIrBinaryOperator.Subtract,
                LuaTokenKind.Star => LuaIrBinaryOperator.Multiply,
                LuaTokenKind.Slash => LuaIrBinaryOperator.Divide,
                LuaTokenKind.FloorDivide => LuaIrBinaryOperator.FloorDivide,
                LuaTokenKind.Percent => LuaIrBinaryOperator.Modulo,
                LuaTokenKind.Caret => LuaIrBinaryOperator.Power,
                LuaTokenKind.Concatenate => LuaIrBinaryOperator.Concatenate,
                LuaTokenKind.Equal => LuaIrBinaryOperator.Equal,
                LuaTokenKind.NotEqual => LuaIrBinaryOperator.NotEqual,
                LuaTokenKind.LessThan => LuaIrBinaryOperator.LessThan,
                LuaTokenKind.LessThanOrEqual => LuaIrBinaryOperator.LessThanOrEqual,
                LuaTokenKind.GreaterThan => LuaIrBinaryOperator.GreaterThan,
                LuaTokenKind.GreaterThanOrEqual => LuaIrBinaryOperator.GreaterThanOrEqual,
                LuaTokenKind.Ampersand => LuaIrBinaryOperator.BitwiseAnd,
                LuaTokenKind.Pipe => LuaIrBinaryOperator.BitwiseOr,
                LuaTokenKind.Tilde => LuaIrBinaryOperator.BitwiseXor,
                LuaTokenKind.ShiftLeft => LuaIrBinaryOperator.ShiftLeft,
                LuaTokenKind.ShiftRight => LuaIrBinaryOperator.ShiftRight,
                _ => throw new InvalidOperationException($"Unexpected binary operator {kind}."),
            };

            private void LowerIndex(LuaSyntaxNode expression, int destination)
            {
                var nodes = expression.ChildNodes().ToArray();
                var table = Reserve(1);
                var key = Reserve(1);
                LowerExpression(nodes[0], table, 1);
                if (expression.Kind == LuaSyntaxKind.MemberAccessExpression)
                {
                    LoadString(key, IdentifierToken(expression), expression.Span);
                }
                else
                {
                    LowerExpression(nodes[1], key, 1);
                }

                Emit(new LuaIrInstruction(LuaIrOpcode.GetTable, destination, table, key, span: expression.Span));
            }

            private void LowerCall(LuaSyntaxNode expression, int destination, int resultCount)
            {
                var nodes = expression.ChildNodes().ToArray();
                var argumentList = GetChild(expression, LuaSyntaxKind.ArgumentList);
                var callLine = SourceLineAt(
                    _owner._model.LanguageVersion == LuaLanguageVersion.Lua53
                        ? expression.Span.Start
                        : argumentList.Span.Start);
                var arguments = argumentList.ChildNodes().FirstOrDefault(static node =>
                    node.Kind == LuaSyntaxKind.ExpressionList)?.ChildNodes().ToArray() ??
                    argumentList.ChildNodes().Where(static node =>
                        node.Kind is LuaSyntaxKind.TableConstructorExpression or LuaSyntaxKind.StringLiteralExpression).ToArray();
                var implicitArgumentCount = expression.Kind == LuaSyntaxKind.MethodCallExpression ? 1 : 0;
                var baseRegister = destination;
                EnsureRegisters(
                    baseRegister,
                    1 + implicitArgumentCount + Math.Max(arguments.Length, 1));

                if (expression.Kind == LuaSyntaxKind.MethodCallExpression)
                {
                    LowerExpression(nodes[0], baseRegister + 1, 1);
                    var key = Reserve(1);
                    LoadString(key, IdentifierToken(expression), expression.Span);
                    Emit(new LuaIrInstruction(
                        LuaIrOpcode.GetTable,
                        baseRegister,
                        baseRegister + 1,
                        key,
                        span: expression.Span));
                }
                else
                {
                    LowerExpression(nodes[0], baseRegister, 1);
                }

                var openArguments = arguments.Length > 0 && IsExpandable(arguments[^1]);
                for (var index = 0; index < arguments.Length; index++)
                {
                    LowerExpression(
                        arguments[index],
                        baseRegister + 1 + implicitArgumentCount + index,
                        openArguments && index == arguments.Length - 1 ? -1 : 1);
                }

                Emit(new LuaIrInstruction(
                    LuaIrOpcode.Call,
                    baseRegister,
                    openArguments ? -1 : implicitArgumentCount + arguments.Length,
                    resultCount,
                    span: expression.Span,
                    sourceLine: callLine));
            }

            private void LowerTable(LuaSyntaxNode expression, int destination)
            {
                Emit(new LuaIrInstruction(LuaIrOpcode.NewTable, destination, span: expression.Span));
                var fields = expression.ChildNodes().Where(static node =>
                    node.Kind == LuaSyntaxKind.TableField).ToArray();
                var arrayIndex = 1;
                var fieldTemporaryBase = _nextRegister;
                for (var index = 0; index < fields.Length; index++)
                {
                    var field = fields[index];
                    var nodes = field.ChildNodes().ToArray();
                    var tokens = field.ChildTokens().ToArray();
                    if (tokens.Any(static token => token.Kind == LuaTokenKind.Assign))
                    {
                        var key = Reserve(1);
                        var value = Reserve(1);
                        if (tokens[0].Kind == LuaTokenKind.OpenBracket)
                        {
                            LowerExpression(nodes[0], key, 1);
                            LowerExpression(nodes[1], value, 1);
                        }
                        else
                        {
                            LoadString(key, tokens[0], tokens[0].Span);
                            LowerExpression(nodes[0], value, 1);
                        }

                        Emit(new LuaIrInstruction(LuaIrOpcode.SetTable, destination, key, value, span: field.Span));
                        _nextRegister = fieldTemporaryBase;
                        continue;
                    }

                    var isOpen = index == fields.Length - 1 && IsExpandable(nodes[0]);
                    int source;
                    if (isOpen)
                    {
                        // Lua 5.4 SETLIST consumes open results from a register window
                        // whose table is immediately before the current 50-field block.
                        // Keep that scratch window above all live registers while still
                        // reusing temporaries across arbitrarily large constructors.
                        var offset = (arrayIndex - 1) % Lua54SetListFieldsPerFlush + 1;
                        source = checked(fieldTemporaryBase + offset);
                        EnsureRegisters(source, 1);
                    }
                    else
                    {
                        source = Reserve(1);
                    }

                    LowerExpression(nodes[0], source, isOpen ? -1 : 1);
                    Emit(new LuaIrInstruction(
                        LuaIrOpcode.SetList,
                        destination,
                        arrayIndex,
                        source,
                        isOpen ? -1 : 1,
                        field.Span));
                    arrayIndex++;
                    _nextRegister = fieldTemporaryBase;
                }
            }

            private void LoadName(LuaNameReference reference, LuaSyntaxToken token, int destination)
            {
                if (reference.ResolutionKind == LuaNameResolutionKind.Global)
                {
                    var environment = Reserve(1);
                    LoadSymbol(reference.Symbol, environment, token.Span);
                    var key = Reserve(1);
                    LoadString(key, token, token.Span);
                    Emit(new LuaIrInstruction(
                        LuaIrOpcode.GetTable,
                        destination,
                        environment,
                        key,
                        span: token.Span));
                    return;
                }

                LoadSymbol(reference.Symbol, destination, token.Span);
            }

            private void LoadSymbol(LuaSymbol symbol, int destination, TextSpan span)
            {
                if (_symbolRegisters.TryGetValue(symbol.Id, out var register))
                {
                    if (destination != register)
                    {
                        Emit(new LuaIrInstruction(
                            LuaIrOpcode.Move,
                            destination,
                            register,
                            span: span));
                    }
                }
                else
                {
                    Emit(new LuaIrInstruction(
                        LuaIrOpcode.GetUpvalue,
                        destination,
                        _upvalueBySymbol[symbol.Id],
                        span: span));
                }
            }

            private void LoadString(int destination, LuaSyntaxToken token, TextSpan span)
            {
                ReadOnlySpan<byte> bytes = token.Value is LuaStringTokenValue value
                    ? value.Bytes.AsSpan()
                    : _owner._model.Syntax.Source.GetSpan(token.Span);
                LoadConstant(destination, LuaIrConstant.FromString(bytes), span);
            }

            private void LoadConstant(int destination, LuaIrConstant value, TextSpan span)
            {
                var index = GetOrAddConstant(value);
                Emit(new LuaIrInstruction(LuaIrOpcode.LoadConstant, destination, index, span: span));
            }

            private int GetOrAddConstant(LuaIrConstant value)
            {
                var index = FindConstant(value);
                if (index >= 0)
                {
                    return index;
                }

                index = _constants.Count;
                _constants.Add(value);
                return index;
            }

            private int FindConstant(LuaIrConstant value)
            {
                for (var index = 0; index < _constants.Count; index++)
                {
                    var candidate = _constants[index];
                    if (candidate.Kind == value.Kind && candidate.Integer == value.Integer &&
                        BitConverter.DoubleToInt64Bits(candidate.Float) ==
                        BitConverter.DoubleToInt64Bits(value.Float) &&
                        candidate.Bytes.AsSpan().SequenceEqual(value.Bytes.AsSpan()))
                    {
                        return index;
                    }
                }

                return -1;
            }

            private void FillExtraNil(int destination, int resultCount, TextSpan span)
            {
                if (resultCount > 1)
                {
                    Emit(new LuaIrInstruction(LuaIrOpcode.LoadNil, destination + 1, resultCount - 1, span: span));
                }
            }

            private int DeclareSymbol(LuaSymbol symbol, bool markToBeClosed, TextSpan span)
            {
                var register = Reserve(1);
                _symbolRegisters.Add(symbol.Id, register);
                _activeSymbolIds.Add(symbol.Id);
                _scopes[^1].DeclaredSymbols.Add(symbol.Id);
                var local = new PendingLocal(symbol.Name, _instructions.Count);
                _locals.Add(local);
                _localBySymbol.Add(symbol.Id, local);
                _localTop = _nextRegister;
                if (markToBeClosed)
                {
                    _activeToBeClosedRegisters.Add(register);
                    Emit(new LuaIrInstruction(LuaIrOpcode.MarkToBeClosed, register, span: span));
                }

                return register;
            }

            private void EnterScope()
            {
                _scopes.Add(new Scope(
                    _scopes.Count == 0 ? null : _scopes[^1],
                    _localTop,
                    _activeSymbolIds.Count,
                    _activeSyntheticCloseRegisters.Count,
                    _activeToBeClosedRegisters.Count));
            }

            private void AddSyntheticLocal(string name)
            {
                var local = new PendingLocal(name, _instructions.Count);
                _locals.Add(local);
                _scopes[^1].SyntheticLocals.Add(local);
            }

            private void CloseCurrentScope(TextSpan span, int? minimumRegister = null)
            {
                var scope = _scopes[^1];
                var lowerBound = minimumRegister ?? scope.EntryRegister;
                var closeBase = int.MaxValue;
                foreach (var symbolId in scope.DeclaredSymbols)
                {
                    if (!_symbolRegisters.TryGetValue(symbolId, out var register))
                    {
                        continue;
                    }

                    var symbol = _symbolsById[symbolId];
                    if (register >= lowerBound &&
                        (symbol.IsCaptured || symbol.Attribute == LuaLocalAttributeKind.ToBeClosed))
                    {
                        closeBase = Math.Min(closeBase, register);
                    }
                }

                for (var index = scope.EntryActiveSyntheticCloseRegisterCount;
                     index < _activeSyntheticCloseRegisters.Count;
                     index++)
                {
                    var register = _activeSyntheticCloseRegisters[index];
                    if (register >= lowerBound)
                    {
                        closeBase = Math.Min(closeBase, register);
                    }
                }

                for (var index = scope.EntryActiveToBeClosedRegisterCount;
                     index < _activeToBeClosedRegisters.Count;
                     index++)
                {
                    var register = _activeToBeClosedRegisters[index];
                    if (register >= lowerBound)
                    {
                        closeBase = Math.Min(closeBase, register);
                    }
                }

                if (closeBase != int.MaxValue)
                {
                    Emit(new LuaIrInstruction(LuaIrOpcode.Close, closeBase, span: span));
                }
            }

            private void DeactivateCurrentScope()
            {
                var scope = _scopes[^1];
                if (_localTop > scope.EntryRegister)
                {
                    Emit(new LuaIrInstruction(LuaIrOpcode.SetTop, scope.EntryRegister));
                }

                foreach (var symbolId in scope.DeclaredSymbols)
                {
                    if (_localBySymbol.Remove(symbolId, out var local))
                    {
                        local.EndProgramCounter = _instructions.Count;
                    }

                    _symbolRegisters.Remove(symbolId);
                }

                foreach (var local in scope.SyntheticLocals)
                {
                    local.EndProgramCounter ??= _instructions.Count;
                }

                if (_activeSymbolIds.Count > scope.EntryActiveSymbolCount)
                {
                    _activeSymbolIds.RemoveRange(
                        scope.EntryActiveSymbolCount,
                        _activeSymbolIds.Count - scope.EntryActiveSymbolCount);
                }

                if (_activeSyntheticCloseRegisters.Count >
                    scope.EntryActiveSyntheticCloseRegisterCount)
                {
                    _activeSyntheticCloseRegisters.RemoveRange(
                        scope.EntryActiveSyntheticCloseRegisterCount,
                        _activeSyntheticCloseRegisters.Count -
                            scope.EntryActiveSyntheticCloseRegisterCount);
                }

                if (_activeToBeClosedRegisters.Count > scope.EntryActiveToBeClosedRegisterCount)
                {
                    _activeToBeClosedRegisters.RemoveRange(
                        scope.EntryActiveToBeClosedRegisterCount,
                        _activeToBeClosedRegisters.Count - scope.EntryActiveToBeClosedRegisterCount);
                }

                _localTop = scope.EntryRegister;
                _nextRegister = _localTop;
            }

            private void ExitScope()
            {
                var scope = _scopes[^1];
                DeactivateCurrentScope();
                _scopes.RemoveAt(_scopes.Count - 1);
            }

            private void ResetTemporaries()
            {
                if (_nextRegister > _localTop)
                {
                    Emit(new LuaIrInstruction(LuaIrOpcode.SetTop, _localTop));
                }

                _nextRegister = _localTop;
            }

            private int Reserve(int count)
            {
                var result = _nextRegister;
                EnsureRegisters(result, count);
                return result;
            }

            private void EnsureRegisters(int start, int count)
            {
                _maximumRegister = Math.Max(_maximumRegister, checked(start + count));
                _nextRegister = Math.Max(_nextRegister, checked(start + count));
            }

            private int Emit(LuaIrInstruction instruction)
            {
                if (instruction.SourceLine == 0 && instruction.Span.Length > 0 && instruction.Span.Start <=
                    _owner._model.Syntax.Source.Length)
                {
                    instruction = instruction with
                    {
                        SourceLine = _owner._model.Syntax.Source
                            .GetLocation(instruction.Span.Start).Line + 1,
                    };
                }

                _instructions.Add(instruction);
                return _instructions.Count - 1;
            }

            private int SourceLineAt(int offset) =>
                _owner._model.Syntax.Source.GetLocation(Math.Clamp(
                    offset,
                    0,
                    _owner._model.Syntax.Source.Length)).Line + 1;

            private void PatchTarget(int programCounter, int target)
            {
                _instructions[programCounter] = _instructions[programCounter] with { B = target };
            }

            private static bool IsExpandable(LuaSyntaxNode expression) =>
                expression.Kind is LuaSyntaxKind.CallExpression or
                    LuaSyntaxKind.MethodCallExpression or LuaSyntaxKind.VarArgExpression;

            private sealed class PendingLocal(string name, int startProgramCounter)
            {
                public string Name { get; } = name;

                public int StartProgramCounter { get; } = startProgramCounter;

                public int? EndProgramCounter { get; set; }
            }

            private static LuaSyntaxNode GetChild(LuaSyntaxNode node, LuaSyntaxKind kind) =>
                node.ChildNodes().Single(child => child.Kind == kind);

            private static LuaSyntaxToken IdentifierToken(LuaSyntaxNode node) =>
                node.ChildTokens().First(token => token.Kind == LuaTokenKind.Identifier);

            private string ReadAscii(LuaSyntaxToken token) =>
                Encoding.ASCII.GetString(_owner._model.Syntax.Source.GetSpan(token.Span));

            private enum AssignmentTargetKind : byte
            {
                Register,
                Upvalue,
                Table,
            }

            private readonly record struct AssignmentTarget(
                AssignmentTargetKind Kind,
                int First,
                int Second)
            {
                public static AssignmentTarget Register(int register) =>
                    new(AssignmentTargetKind.Register, register, 0);

                public static AssignmentTarget Upvalue(int upvalue) =>
                    new(AssignmentTargetKind.Upvalue, upvalue, 0);

                public static AssignmentTarget Table(int table, int key) =>
                    new(AssignmentTargetKind.Table, table, key);
            }

            private sealed class Scope(
                Scope? parent,
                int entryRegister,
                int entryActiveSymbolCount,
                int entryActiveSyntheticCloseRegisterCount,
                int entryActiveToBeClosedRegisterCount)
            {
                public Scope? Parent { get; } = parent;

                public int EntryRegister { get; } = entryRegister;

                public int EntryActiveSymbolCount { get; } = entryActiveSymbolCount;

                public int EntryActiveSyntheticCloseRegisterCount { get; } =
                    entryActiveSyntheticCloseRegisterCount;

                public int EntryActiveToBeClosedRegisterCount { get; } =
                    entryActiveToBeClosedRegisterCount;

                public List<int> DeclaredSymbols { get; } = [];

                public List<PendingLocal> SyntheticLocals { get; } = [];

                public Dictionary<string, Label> Labels { get; } = new(StringComparer.Ordinal);
            }

            private sealed record Label(int ProgramCounter, int[] ActiveSymbols);

            private sealed record GotoPatch(
                string Name,
                int ProgramCounter,
                Scope Scope,
                int[] ActiveSymbols);

            private sealed class LoopContext(Scope scope, int[] activeSymbols)
            {
                public Scope Scope { get; } = scope;

                public int[] ActiveSymbols { get; } = activeSymbols;

                public List<int> Breaks { get; } = [];
            }
        }
    }
}
