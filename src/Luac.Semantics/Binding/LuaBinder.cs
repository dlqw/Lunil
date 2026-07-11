using System.Collections.Immutable;
using System.Text;
using Luac.Core.Diagnostics;
using Luac.Core.Text;
using Luac.Syntax.Lexing;
using Luac.Syntax.Parsing;

namespace Luac.Semantics.Binding;

/// <summary>
/// Binds Lua's annotation-independent lexical semantics, including locals,
/// captures, _ENV, labels, gotos, varargs, and local attributes.
/// </summary>
public static class LuaBinder
{
    public static LuaSemanticModel Bind(
        LuaParseResult syntax,
        LuaBinderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(syntax);
        options ??= LuaBinderOptions.Default;
        ValidateOptions(options);
        return new Implementation(syntax, options).Bind();
    }

    private static void ValidateOptions(LuaBinderOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumActiveLocalsPerFunction);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumUpvaluesPerFunction);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumDiagnosticCount);
    }

    private sealed class Implementation
    {
        private readonly LuaParseResult _syntax;
        private readonly LuaBinderOptions _options;
        private readonly ImmutableArray<Diagnostic>.Builder _diagnostics;
        private readonly List<LuaSymbol> _symbols = [];
        private readonly List<LuaNameReference> _references = [];
        private readonly List<LuaFunctionInfo> _functions = [];
        private readonly List<LuaSymbol> _activeSymbols = [];
        private FunctionContext _currentFunction = null!;
        private ScopeFrame _currentScope = null!;
        private int _nextSymbolId;
        private int _nextFunctionId;
        private int _loopDepth;

        public Implementation(LuaParseResult syntax, LuaBinderOptions options)
        {
            _syntax = syntax;
            _options = options;
            _diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            _diagnostics.AddRange(syntax.Diagnostics.Take(options.MaximumDiagnosticCount));
        }

        public LuaSemanticModel Bind()
        {
            var mainBlock = _syntax.Root.ChildNodes()
                .Single(static node => node.Kind == LuaSyntaxKind.Block);
            _currentFunction = new FunctionContext(
                _nextFunctionId++,
                parent: null,
                isVarArg: true,
                _syntax.Root.Span,
                activeSymbolBase: 0);
            _currentScope = new ScopeFrame(parent: null, depth: 0, entryActiveSymbolCount: 0);

            var environment = CreateSymbol(
                "_ENV",
                LuaSymbolKind.Environment,
                LuaLocalAttributeKind.None,
                new TextSpan(0, 0));
            ActivateSymbol(environment);

            BindBlock(mainBlock, createScope: false);
            ResolveGotos(_currentFunction);
            CompleteFunction(_currentFunction);

            return new LuaSemanticModel(
                _syntax,
                _diagnostics.ToImmutable(),
                _symbols.ToImmutableArray(),
                _references.OrderBy(static reference => reference.Span.Start).ToImmutableArray(),
                _functions.OrderBy(static function => function.Id).ToImmutableArray());
        }

        private void BindBlock(LuaSyntaxNode block, bool createScope = true)
        {
            ScopeFrame? previousScope = null;
            if (createScope)
            {
                previousScope = EnterScope();
            }

            try
            {
                var statements = block.ChildNodes().ToArray();
                for (var index = 0; index < statements.Length; index++)
                {
                    var statement = statements[index];
                    var terminalLabel = statement.Kind == LuaSyntaxKind.LabelStatement &&
                        statements[(index + 1)..].All(static following =>
                            following.Kind is LuaSyntaxKind.LabelStatement or LuaSyntaxKind.EmptyStatement);
                    BindStatement(statement, terminalLabel);
                }
            }
            finally
            {
                if (createScope)
                {
                    ExitScope(previousScope!);
                }
            }
        }

        private void BindStatement(LuaSyntaxNode statement, bool terminalLabel) // NOSONAR: exhaustive grammar dispatcher
        {
            switch (statement.Kind)
            {
                case LuaSyntaxKind.EmptyStatement:
                    break;
                case LuaSyntaxKind.AssignmentStatement:
                    BindAssignment(statement);
                    break;
                case LuaSyntaxKind.CallStatement:
                    BindExpression(statement.ChildNodes().Single());
                    break;
                case LuaSyntaxKind.LabelStatement:
                    BindLabel(statement, terminalLabel);
                    break;
                case LuaSyntaxKind.BreakStatement:
                    if (_loopDepth == 0)
                    {
                        AddDiagnostic(
                            "LUA3005",
                            statement.Span,
                            "Break statement is outside a loop.");
                    }

                    break;
                case LuaSyntaxKind.GotoStatement:
                    BindGoto(statement);
                    break;
                case LuaSyntaxKind.DoStatement:
                    BindBlock(GetDirectChild(statement, LuaSyntaxKind.Block));
                    break;
                case LuaSyntaxKind.WhileStatement:
                    BindWhile(statement);
                    break;
                case LuaSyntaxKind.RepeatStatement:
                    BindRepeat(statement);
                    break;
                case LuaSyntaxKind.IfStatement:
                    BindIf(statement);
                    break;
                case LuaSyntaxKind.NumericForStatement:
                    BindNumericFor(statement);
                    break;
                case LuaSyntaxKind.GenericForStatement:
                    BindGenericFor(statement);
                    break;
                case LuaSyntaxKind.FunctionDeclarationStatement:
                    BindFunctionDeclaration(statement);
                    break;
                case LuaSyntaxKind.LocalFunctionDeclarationStatement:
                    BindLocalFunctionDeclaration(statement);
                    break;
                case LuaSyntaxKind.LocalDeclarationStatement:
                    BindLocalDeclaration(statement);
                    break;
                case LuaSyntaxKind.ReturnStatement:
                    foreach (var child in statement.ChildNodes())
                    {
                        BindExpression(child);
                    }

                    break;
                case LuaSyntaxKind.Error:
                    foreach (var child in statement.ChildNodes())
                    {
                        BindExpression(child);
                    }

                    break;
            }
        }

        private void BindAssignment(LuaSyntaxNode statement)
        {
            var variableList = GetDirectChild(statement, LuaSyntaxKind.VariableList);
            foreach (var variable in variableList.ChildNodes())
            {
                BindAssignmentTarget(variable);
            }

            var expressionList = GetDirectChild(statement, LuaSyntaxKind.ExpressionList);
            BindExpression(expressionList);
        }

        private void BindAssignmentTarget(LuaSyntaxNode expression)
        {
            switch (expression.Kind)
            {
                case LuaSyntaxKind.IdentifierExpression:
                    BindIdentifier(expression, isWrite: true);
                    break;
                case LuaSyntaxKind.IndexExpression:
                    foreach (var child in expression.ChildNodes())
                    {
                        BindExpression(child);
                    }

                    break;
                case LuaSyntaxKind.MemberAccessExpression:
                    BindExpression(expression.ChildNodes().First());
                    break;
                default:
                    BindExpression(expression);
                    break;
            }
        }

        private void BindWhile(LuaSyntaxNode statement)
        {
            var nodes = statement.ChildNodes().ToArray();
            BindExpression(nodes[0]);
            _loopDepth++;
            try
            {
                BindBlock(nodes.Single(static node => node.Kind == LuaSyntaxKind.Block));
            }
            finally
            {
                _loopDepth--;
            }
        }

        private void BindRepeat(LuaSyntaxNode statement)
        {
            var previousScope = EnterScope();
            try
            {
                var nodes = statement.ChildNodes().ToArray();
                _loopDepth++;
                try
                {
                    BindBlock(
                        nodes.Single(static node => node.Kind == LuaSyntaxKind.Block),
                        createScope: false);
                }
                finally
                {
                    _loopDepth--;
                }

                BindExpression(nodes.Last(static node => node.Kind != LuaSyntaxKind.Block));
            }
            finally
            {
                ExitScope(previousScope);
            }
        }

        private void BindIf(LuaSyntaxNode statement)
        {
            foreach (var child in statement.ChildNodes())
            {
                switch (child.Kind)
                {
                    case LuaSyntaxKind.Block:
                        BindBlock(child);
                        break;
                    case LuaSyntaxKind.ElseIfClause:
                        BindConditionalClause(child);
                        break;
                    case LuaSyntaxKind.ElseClause:
                        BindBlock(GetDirectChild(child, LuaSyntaxKind.Block));
                        break;
                    default:
                        BindExpression(child);
                        break;
                }
            }
        }

        private void BindConditionalClause(LuaSyntaxNode clause)
        {
            foreach (var child in clause.ChildNodes())
            {
                if (child.Kind == LuaSyntaxKind.Block)
                {
                    BindBlock(child);
                }
                else
                {
                    BindExpression(child);
                }
            }
        }

        private void BindNumericFor(LuaSyntaxNode statement)
        {
            var body = GetDirectChild(statement, LuaSyntaxKind.Block);
            foreach (var child in statement.ChildNodes().Where(node => !ReferenceEquals(node, body)))
            {
                BindExpression(child);
            }

            var previousScope = EnterScope();
            try
            {
                var name = statement.ChildTokens().FirstOrDefault(static token =>
                    token.Kind == LuaTokenKind.Identifier);
                DeclareToken(name, LuaSymbolKind.NumericForVariable, LuaLocalAttributeKind.None);
                _loopDepth++;
                try
                {
                    BindBlock(body, createScope: false);
                }
                finally
                {
                    _loopDepth--;
                }
            }
            finally
            {
                ExitScope(previousScope);
            }
        }

        private void BindGenericFor(LuaSyntaxNode statement)
        {
            var nameList = GetDirectChild(statement, LuaSyntaxKind.NameList);
            var expressionList = GetDirectChild(statement, LuaSyntaxKind.ExpressionList);
            var body = GetDirectChild(statement, LuaSyntaxKind.Block);
            BindExpression(expressionList);

            var previousScope = EnterScope();
            try
            {
                foreach (var token in nameList.ChildTokens().Where(static token =>
                             token.Kind == LuaTokenKind.Identifier))
                {
                    DeclareToken(token, LuaSymbolKind.GenericForVariable, LuaLocalAttributeKind.None);
                }

                _loopDepth++;
                try
                {
                    BindBlock(body, createScope: false);
                }
                finally
                {
                    _loopDepth--;
                }
            }
            finally
            {
                ExitScope(previousScope);
            }
        }

        private void BindFunctionDeclaration(LuaSyntaxNode statement)
        {
            var functionName = GetDirectChild(statement, LuaSyntaxKind.FunctionName);
            var nameTokens = functionName.ChildTokens().ToArray();
            var firstName = nameTokens.FirstOrDefault(static token =>
                token.Kind == LuaTokenKind.Identifier);
            if (firstName is not null && !firstName.IsMissing)
            {
                var isMember = nameTokens.Any(static token =>
                    token.Kind is LuaTokenKind.Dot or LuaTokenKind.Colon);
                BindNameToken(firstName, isWrite: !isMember);
            }

            var hasImplicitSelf = nameTokens.Any(static token => token.Kind == LuaTokenKind.Colon);
            BindNestedFunction(
                GetDirectChild(statement, LuaSyntaxKind.FunctionBody),
                statement.Span,
                hasImplicitSelf);
        }

        private void BindLocalFunctionDeclaration(LuaSyntaxNode statement)
        {
            var name = statement.ChildTokens().FirstOrDefault(static token =>
                token.Kind == LuaTokenKind.Identifier);
            DeclareToken(name, LuaSymbolKind.Local, LuaLocalAttributeKind.None);
            BindNestedFunction(
                GetDirectChild(statement, LuaSyntaxKind.FunctionBody),
                statement.Span,
                hasImplicitSelf: false);
        }

        private void BindLocalDeclaration(LuaSyntaxNode statement)
        {
            var initializer = statement.ChildNodes().FirstOrDefault(static node =>
                node.Kind == LuaSyntaxKind.ExpressionList);
            if (initializer is not null)
            {
                BindExpression(initializer);
            }

            var declarations = statement.ChildNodes()
                .Where(static node => node.Kind == LuaSyntaxKind.AttributedName)
                .Select(ReadAttributedName)
                .ToArray();
            var closeCount = 0;
            foreach (var declaration in declarations)
            {
                if (declaration.Attribute == LuaLocalAttributeKind.ToBeClosed && ++closeCount > 1)
                {
                    AddDiagnostic(
                        "LUA3004",
                        declaration.NameToken.Span,
                        "A local declaration cannot contain multiple to-be-closed variables.");
                }

                DeclareToken(declaration.NameToken, LuaSymbolKind.Local, declaration.Attribute);
            }
        }

        private AttributedName ReadAttributedName(LuaSyntaxNode node)
        {
            var tokens = node.ChildTokens().ToArray();
            var name = tokens.FirstOrDefault(static token => token.Kind == LuaTokenKind.Identifier)
                ?? CreateSyntheticToken(node.Span.Start);
            var attribute = LuaLocalAttributeKind.None;

            if (tokens.Any(static token => token.Kind == LuaTokenKind.LessThan))
            {
                var attributeToken = tokens
                    .Where(static token => token.Kind == LuaTokenKind.Identifier)
                    .Skip(1)
                    .FirstOrDefault();
                if (attributeToken is not null && !attributeToken.IsMissing)
                {
                    attribute = GetName(attributeToken) switch
                    {
                        "const" => LuaLocalAttributeKind.Constant,
                        "close" => LuaLocalAttributeKind.ToBeClosed,
                        var unknown => ReportUnknownAttribute(attributeToken, unknown),
                    };
                }
            }

            return new AttributedName(name, attribute);
        }

        private LuaLocalAttributeKind ReportUnknownAttribute(LuaSyntaxToken token, string name)
        {
            AddDiagnostic(
                "LUA3003",
                token.Span,
                $"Unknown local variable attribute '{name}'. Expected 'const' or 'close'.");
            return LuaLocalAttributeKind.None;
        }

        private void BindExpression(LuaSyntaxNode expression) // NOSONAR: exhaustive syntax dispatcher
        {
            switch (expression.Kind)
            {
                case LuaSyntaxKind.IdentifierExpression:
                    BindIdentifier(expression, isWrite: false);
                    break;
                case LuaSyntaxKind.VarArgExpression:
                    if (!_currentFunction.IsVarArg)
                    {
                        AddDiagnostic(
                            "LUA3001",
                            expression.Span,
                            "Cannot use '...' outside a vararg function.");
                    }

                    break;
                case LuaSyntaxKind.FunctionExpression:
                    BindNestedFunction(
                        GetDirectChild(expression, LuaSyntaxKind.FunctionBody),
                        expression.Span,
                        hasImplicitSelf: false);
                    break;
                case LuaSyntaxKind.FunctionBody:
                    break;
                default:
                    foreach (var child in expression.ChildNodes())
                    {
                        BindExpression(child);
                    }

                    break;
            }
        }

        private void BindIdentifier(LuaSyntaxNode expression, bool isWrite)
        {
            var token = expression.ChildTokens().FirstOrDefault(static candidate =>
                candidate.Kind == LuaTokenKind.Identifier);
            if (token is not null && !token.IsMissing)
            {
                BindNameToken(token, isWrite);
            }
        }

        private void BindNameToken(LuaSyntaxToken token, bool isWrite)
        {
            var name = GetName(token);
            var symbol = FindActiveSymbol(name);
            LuaNameResolutionKind resolutionKind;

            if (symbol is not null)
            {
                resolutionKind = symbol.FunctionId == _currentFunction.Id
                    ? LuaNameResolutionKind.Local
                    : LuaNameResolutionKind.Upvalue;
                if (resolutionKind == LuaNameResolutionKind.Upvalue)
                {
                    Capture(symbol);
                }

                if (isWrite && symbol.IsReadOnly)
                {
                    AddDiagnostic(
                        "LUA3002",
                        token.Span,
                        $"Cannot assign to read-only local variable '{name}'.");
                }
            }
            else
            {
                symbol = FindActiveSymbol("_ENV")
                    ?? throw new InvalidOperationException("The implicit _ENV symbol is missing.");
                resolutionKind = LuaNameResolutionKind.Global;
                if (symbol.FunctionId != _currentFunction.Id)
                {
                    Capture(symbol);
                }
            }

            _references.Add(new LuaNameReference(
                token.Span,
                name,
                resolutionKind,
                symbol,
                isWrite));
        }

        private void BindNestedFunction(
            LuaSyntaxNode functionBody,
            TextSpan functionSpan,
            bool hasImplicitSelf)
        {
            var previousFunction = _currentFunction;
            var previousScope = _currentScope;
            var previousLoopDepth = _loopDepth;
            var activeBase = _activeSymbols.Count;
            var parameters = GetDirectChild(functionBody, LuaSyntaxKind.ParameterList);
            var isVarArg = parameters.ChildTokens().Any(static token =>
                token.Kind == LuaTokenKind.VarArg);

            var function = new FunctionContext(
                _nextFunctionId++,
                previousFunction,
                isVarArg,
                functionSpan,
                activeBase);
            _currentFunction = function;
            _currentScope = new ScopeFrame(parent: null, depth: 0, activeBase);
            _loopDepth = 0;

            try
            {
                if (hasImplicitSelf)
                {
                    var selfSpan = parameters.Span;
                    var self = CreateSymbol(
                        "self",
                        LuaSymbolKind.Parameter,
                        LuaLocalAttributeKind.None,
                        new TextSpan(selfSpan.Start, 0));
                    ActivateSymbol(self);
                }

                foreach (var parameter in parameters.ChildTokens().Where(static token =>
                             token.Kind == LuaTokenKind.Identifier && !token.IsMissing))
                {
                    DeclareToken(parameter, LuaSymbolKind.Parameter, LuaLocalAttributeKind.None);
                }

                BindBlock(
                    GetDirectChild(functionBody, LuaSyntaxKind.Block),
                    createScope: false);
                ResolveGotos(function);
                CompleteFunction(function);
            }
            finally
            {
                if (_activeSymbols.Count > activeBase)
                {
                    _activeSymbols.RemoveRange(activeBase, _activeSymbols.Count - activeBase);
                }

                _currentFunction = previousFunction;
                _currentScope = previousScope;
                _loopDepth = previousLoopDepth;
            }
        }

        private void BindLabel(LuaSyntaxNode statement, bool terminalLabel)
        {
            var token = statement.ChildTokens().FirstOrDefault(static candidate =>
                candidate.Kind == LuaTokenKind.Identifier);
            if (token is null || token.IsMissing)
            {
                return;
            }

            var name = GetName(token);
            if (_currentScope.Labels.ContainsKey(name))
            {
                AddDiagnostic(
                    "LUA3006",
                    token.Span,
                    $"Label '{name}' is already defined in this block.");
                return;
            }

            var active = terminalLabel
                ? _activeSymbols.Take(_currentScope.EntryActiveSymbolCount).ToImmutableArray()
                : _activeSymbols.ToImmutableArray();
            var label = new LabelRecord(name, token.Span, _currentScope, active);
            _currentScope.Labels.Add(name, label);
        }

        private void BindGoto(LuaSyntaxNode statement)
        {
            var token = statement.ChildTokens().FirstOrDefault(static candidate =>
                candidate.Kind == LuaTokenKind.Identifier);
            if (token is null || token.IsMissing)
            {
                return;
            }

            _currentFunction.Gotos.Add(new GotoRecord(
                GetName(token),
                token.Span,
                _currentScope,
                _activeSymbols.ToImmutableArray()));
        }

        private void ResolveGotos(FunctionContext function)
        {
            foreach (var @goto in function.Gotos)
            {
                LabelRecord? label = null;
                for (var scope = @goto.Scope; scope is not null; scope = scope.Parent)
                {
                    if (scope.Labels.TryGetValue(@goto.Name, out label))
                    {
                        break;
                    }
                }

                if (label is null)
                {
                    AddDiagnostic(
                        "LUA3007",
                        @goto.Span,
                        $"No visible label '{@goto.Name}' for goto.");
                    continue;
                }

                var gotoSymbols = @goto.ActiveSymbols.Select(static symbol => symbol.Id).ToHashSet();
                var entered = label.ActiveSymbols.FirstOrDefault(symbol => !gotoSymbols.Contains(symbol.Id));
                if (entered is not null)
                {
                    AddDiagnostic(
                        "LUA3008",
                        @goto.Span,
                        $"Goto '{@goto.Name}' jumps into the scope of local '{entered.Name}'.");
                }
            }
        }

        private void Capture(LuaSymbol symbol)
        {
            symbol.IsCaptured = true;
            for (var function = _currentFunction;
                 function.Id != symbol.FunctionId;
                 function = function.Parent
                     ?? throw new InvalidOperationException("Invalid function capture chain."))
            {
                if (function.CaptureIds.Add(symbol.Id))
                {
                    function.Captures.Add(symbol);
                    if (function.Captures.Count > _options.MaximumUpvaluesPerFunction)
                    {
                        AddDiagnostic(
                            "LUA3010",
                            symbol.DeclaringSpan,
                            $"Function exceeds the configured {_options.MaximumUpvaluesPerFunction} upvalue limit.");
                    }
                }
            }
        }

        private LuaSymbol? DeclareToken(
            LuaSyntaxToken? token,
            LuaSymbolKind kind,
            LuaLocalAttributeKind attribute)
        {
            if (token is null || token.IsMissing)
            {
                return null;
            }

            var symbol = CreateSymbol(GetName(token), kind, attribute, token.Span);
            ActivateSymbol(symbol);
            return symbol;
        }

        private LuaSymbol CreateSymbol(
            string name,
            LuaSymbolKind kind,
            LuaLocalAttributeKind attribute,
            TextSpan span)
        {
            var symbol = new LuaSymbol(
                _nextSymbolId++,
                name,
                kind,
                attribute,
                span,
                _currentFunction.Id,
                _currentScope.Depth);
            _symbols.Add(symbol);
            _currentFunction.Symbols.Add(symbol);
            return symbol;
        }

        private void ActivateSymbol(LuaSymbol symbol)
        {
            _activeSymbols.Add(symbol);
            var activeInFunction = _activeSymbols.Count(candidate =>
                candidate.FunctionId == _currentFunction.Id &&
                candidate.Kind != LuaSymbolKind.Environment);
            if (activeInFunction > _options.MaximumActiveLocalsPerFunction)
            {
                AddDiagnostic(
                    "LUA3009",
                    symbol.DeclaringSpan,
                    $"Function exceeds the configured {_options.MaximumActiveLocalsPerFunction} active-local limit.");
            }
        }

        private LuaSymbol? FindActiveSymbol(string name)
        {
            for (var index = _activeSymbols.Count - 1; index >= 0; index--)
            {
                if (string.Equals(_activeSymbols[index].Name, name, StringComparison.Ordinal))
                {
                    return _activeSymbols[index];
                }
            }

            return null;
        }

        private ScopeFrame EnterScope()
        {
            var previous = _currentScope;
            _currentScope = new ScopeFrame(
                previous,
                previous.Depth + 1,
                _activeSymbols.Count);
            return previous;
        }

        private void ExitScope(ScopeFrame previous)
        {
            var count = _activeSymbols.Count - _currentScope.EntryActiveSymbolCount;
            if (count > 0)
            {
                _activeSymbols.RemoveRange(_currentScope.EntryActiveSymbolCount, count);
            }

            _currentScope = previous;
        }

        private void CompleteFunction(FunctionContext function)
        {
            _functions.Add(new LuaFunctionInfo(
                function.Id,
                function.Span,
                function.IsVarArg,
                function.Symbols.ToImmutableArray(),
                function.Captures.ToImmutableArray()));
        }

        private string GetName(LuaSyntaxToken token) =>
            Encoding.ASCII.GetString(_syntax.Source.GetSpan(token.Span));

        private static LuaSyntaxNode GetDirectChild(LuaSyntaxNode node, LuaSyntaxKind kind) =>
            node.ChildNodes().Single(child => child.Kind == kind);

        private static LuaSyntaxToken CreateSyntheticToken(int position) => new(
            LuaTokenKind.Identifier,
            new TextSpan(position, 0),
            [])
        {
            IsMissing = true,
        };

        private void AddDiagnostic(string code, TextSpan span, string message)
        {
            if (_diagnostics.Count < _options.MaximumDiagnosticCount)
            {
                _diagnostics.Add(new Diagnostic(code, DiagnosticSeverity.Error, span, message));
            }
        }

        private sealed class FunctionContext
        {
            public FunctionContext(
                int id,
                FunctionContext? parent,
                bool isVarArg,
                TextSpan span,
                int activeSymbolBase)
            {
                Id = id;
                Parent = parent;
                IsVarArg = isVarArg;
                Span = span;
                ActiveSymbolBase = activeSymbolBase;
            }

            public int Id { get; }

            public FunctionContext? Parent { get; }

            public bool IsVarArg { get; }

            public TextSpan Span { get; }

            public int ActiveSymbolBase { get; }

            public List<LuaSymbol> Symbols { get; } = [];

            public List<LuaSymbol> Captures { get; } = [];

            public HashSet<int> CaptureIds { get; } = [];

            public List<GotoRecord> Gotos { get; } = [];
        }

        private sealed class ScopeFrame
        {
            public ScopeFrame(ScopeFrame? parent, int depth, int entryActiveSymbolCount)
            {
                Parent = parent;
                Depth = depth;
                EntryActiveSymbolCount = entryActiveSymbolCount;
            }

            public ScopeFrame? Parent { get; }

            public int Depth { get; }

            public int EntryActiveSymbolCount { get; }

            public Dictionary<string, LabelRecord> Labels { get; } =
                new(StringComparer.Ordinal);
        }

        private sealed record LabelRecord(
            string Name,
            TextSpan Span,
            ScopeFrame Scope,
            ImmutableArray<LuaSymbol> ActiveSymbols);

        private sealed record GotoRecord(
            string Name,
            TextSpan Span,
            ScopeFrame Scope,
            ImmutableArray<LuaSymbol> ActiveSymbols);

        private sealed record AttributedName(
            LuaSyntaxToken NameToken,
            LuaLocalAttributeKind Attribute);
    }
}
