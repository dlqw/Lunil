using System.Collections.Immutable;
using System.Text;
using Lunil.Core;
using Lunil.Core.Diagnostics;
using Lunil.Core.Text;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

namespace Lunil.Semantics.Binding;

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
        LunilGuard.NotNull(syntax);
        options ??= LuaBinderOptions.Default with
        {
            LanguageVersion = syntax.LanguageVersion,
        };
        ValidateOptions(options);
        if (options.LanguageVersion != syntax.LanguageVersion)
        {
            throw new ArgumentException(
                "The parser and binder language versions must match.",
                nameof(options));
        }

        return new Implementation(syntax, options).Bind();
    }

    private static void ValidateOptions(LuaBinderOptions options)
    {
        if (!LuaLanguageVersions.IsKnown(options.LanguageVersion))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.LanguageVersion,
                "The binder language version is invalid.");
        }

        LunilGuard.Positive(options.MaximumActiveLocalsPerFunction);
        LunilGuard.Positive(options.MaximumUpvaluesPerFunction);
        LunilGuard.Positive(options.MaximumDiagnosticCount);
    }

    private sealed class Implementation
    {
        private readonly LuaParseResult _syntax;
        private readonly LuaBinderOptions _options;
        private readonly ImmutableArray<Diagnostic>.Builder _diagnostics;
        private readonly List<LuaSymbol> _symbols = [];
        private readonly List<LuaNameReference> _references = [];
        private readonly List<LuaMemberReference> _memberReferences = [];
        private readonly List<LuaCodeReference> _codeReferences = [];
        private readonly List<LuaFunctionInfo> _functions = [];
        private readonly List<LuaSymbol> _activeSymbols = [];
        private readonly LuaNameInterner _names = new();
        private readonly Dictionary<string, LuaSymbol> _activeSymbolsByName =
            new(StringComparer.Ordinal);
        private readonly List<ActiveSymbolUndo> _activeSymbolUndo = [];
        private LuaSymbol? _lastGlobalWildcard;
        private int _explicitGlobalContextCount;
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
            _currentScope = new ScopeFrame(
                parent: null,
                depth: 0,
                entryActiveSymbolCount: 0,
                entryActiveLocalCount: 0);

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
                _functions.OrderBy(static function => function.Id).ToImmutableArray())
            {
                MemberReferences = _memberReferences
                    .OrderBy(static reference => reference.Span.Start)
                    .ThenBy(static reference => reference.Span.Length)
                    .ToImmutableArray(),
                UnifiedReferences = _codeReferences
                    .OrderBy(static reference => reference.Span.Start)
                    .ThenBy(static reference => reference.Span.Length)
                    .ThenBy(static reference => reference.Kind)
                    .ToImmutableArray(),
            };
        }

        private void BindBlock(
            LuaSyntaxNode block,
            bool createScope = true,
            bool terminalLabelsEndScope = true)
        {
            ScopeFrame? previousScope = null;
            if (createScope)
            {
                previousScope = EnterScope();
            }

            try
            {
                var children = block.Children;
                var statements = new LuaSyntaxNode[children.Length];
                var statementCount = 0;
                foreach (var child in children)
                {
                    if (child.Node is { } node)
                    {
                        statements[statementCount++] = node;
                    }
                }

                // A trailing label ends the scope only when every following statement is
                // itself a label or empty; one backward pass marks those suffixes.
                var suffixIsLabelOnly = new bool[statementCount + 1];
                suffixIsLabelOnly[statementCount] = true;
                for (var index = statementCount - 1; index >= 0; index--)
                {
                    suffixIsLabelOnly[index] = suffixIsLabelOnly[index + 1] &&
                        statements[index].Kind is
                            LuaSyntaxKind.LabelStatement or LuaSyntaxKind.EmptyStatement;
                }

                for (var index = 0; index < statementCount; index++)
                {
                    var statement = statements[index];
                    var terminalLabel = terminalLabelsEndScope &&
                        statement.Kind == LuaSyntaxKind.LabelStatement &&
                        suffixIsLabelOnly[index + 1];
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
                case LuaSyntaxKind.GlobalDeclarationStatement:
                    BindGlobalDeclaration(statement);
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
                    BindIndexExpression(expression, LuaReferenceAccess.Write);
                    break;
                case LuaSyntaxKind.MemberAccessExpression:
                    BindMemberExpression(expression, LuaReferenceAccess.Write);
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
                        createScope: false,
                        terminalLabelsEndScope: false);
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

            BindFunctionNameMembers(nameTokens);

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
            // A Lua 5.5 prefixed attribute applies to every name in the declaration
            // list, not only the name it precedes.
            var listAttribute = declarations.Length > 0 && declarations[0].IsPrefix
                ? declarations[0].Attribute
                : LuaLocalAttributeKind.None;
            var closeCount = 0;
            for (var index = 0; index < declarations.Length; index++)
            {
                var declaration = declarations[index];
                var attribute = index > 0 && declaration.Attribute == LuaLocalAttributeKind.None
                    ? listAttribute
                    : declaration.Attribute;
                if (attribute == LuaLocalAttributeKind.ToBeClosed && ++closeCount > 1)
                {
                    AddDiagnostic(
                        "LUA3004",
                        declaration.NameToken.Span,
                        "A local declaration cannot contain multiple to-be-closed variables.");
                }

                DeclareToken(declaration.NameToken, LuaSymbolKind.Local, attribute);
            }
        }

        private void BindGlobalDeclaration(LuaSyntaxNode statement)
        {
            var directTokens = statement.ChildTokens().ToArray();
            var prefixAttribute = ReadGlobalPrefixAttribute(directTokens);
            if (directTokens.Any(static token => token.Kind == LuaTokenKind.FunctionKeyword))
            {
                var name = directTokens.FirstOrDefault(static token =>
                    token.Kind == LuaTokenKind.Identifier);
                if (name is not null && !name.IsMissing)
                {
                    DeclareGlobalToken(name, LuaLocalAttributeKind.None);
                }

                BindNestedFunction(
                    GetDirectChild(statement, LuaSyntaxKind.FunctionBody),
                    statement.Span,
                    hasImplicitSelf: false);
                return;
            }

            var initializer = statement.ChildNodes().FirstOrDefault(static node =>
                node.Kind == LuaSyntaxKind.ExpressionList);
            if (initializer is not null)
            {
                BindExpression(initializer);
            }

            if (directTokens.Any(static token => token.Kind == LuaTokenKind.Star))
            {
                var star = directTokens.First(static token => token.Kind == LuaTokenKind.Star);
                var wildcard = CreateSymbol(
                    "*",
                    LuaSymbolKind.GlobalWildcard,
                    prefixAttribute,
                    star.Span);
                ActivateSymbol(wildcard);
                return;
            }

            foreach (var declaration in statement.ChildNodes()
                         .Where(static node => node.Kind == LuaSyntaxKind.AttributedName)
                         .Select(ReadAttributedName))
            {
                var attribute = declaration.Attribute == LuaLocalAttributeKind.None
                    ? prefixAttribute
                    : declaration.Attribute;
                if (attribute == LuaLocalAttributeKind.ToBeClosed)
                {
                    AddDiagnostic(
                        "LUA3013",
                        declaration.NameToken.Span,
                        "global variables cannot be to-be-closed");
                    attribute = LuaLocalAttributeKind.None;
                }

                DeclareGlobalToken(declaration.NameToken, attribute);
            }
        }

        private LuaLocalAttributeKind ReadGlobalPrefixAttribute(LuaSyntaxToken[] tokens)
        {
            var lessThan = Array.FindIndex(tokens, static token =>
                token.Kind == LuaTokenKind.LessThan);
            if (lessThan < 0 || lessThan + 1 >= tokens.Length ||
                tokens[lessThan + 1].Kind != LuaTokenKind.Identifier)
            {
                return LuaLocalAttributeKind.None;
            }

            var token = tokens[lessThan + 1];
            return GetName(token) switch
            {
                "const" => LuaLocalAttributeKind.Constant,
                "close" => LuaLocalAttributeKind.ToBeClosed,
                var unknown => ReportUnknownAttribute(token, unknown),
            };
        }

        private void DeclareGlobalToken(LuaSyntaxToken token, LuaLocalAttributeKind attribute)
        {
            if (GetName(token) == "_ENV")
            {
                AddDiagnostic("LUA3014", token.Span, "'_ENV' cannot be declared as a global variable");
                return;
            }

            var symbol = DeclareToken(token, LuaSymbolKind.Global, attribute);
            if (symbol is null)
            {
                return;
            }

            var environment = FindActiveEnvironment()
                ?? throw new InvalidOperationException("The implicit _ENV symbol is missing.");
            _references.Add(new LuaNameReference(
                token.Span,
                GetName(token),
                LuaNameResolutionKind.Global,
                environment,
                IsWrite: true));
        }

        private AttributedName ReadAttributedName(LuaSyntaxNode node)
        {
            var tokens = node.ChildTokens().ToArray();
            if (tokens.Length > 0 && tokens[0].Kind == LuaTokenKind.LessThan)
            {
                // Lua 5.5 prefixed form: the parser emits [<, attribute, >, name], so
                // the attribute precedes the name and applies to the whole list.
                var attributeToken = tokens
                    .Skip(1)
                    .FirstOrDefault(static token => token.Kind == LuaTokenKind.Identifier);
                var prefixedName = tokens
                    .LastOrDefault(static token => token.Kind == LuaTokenKind.Identifier)
                    ?? CreateSyntheticToken(node.Span.Start);
                return new AttributedName(prefixedName, ReadAttributeKind(attributeToken), IsPrefix: true);
            }

            var name = tokens.FirstOrDefault(static token => token.Kind == LuaTokenKind.Identifier)
                ?? CreateSyntheticToken(node.Span.Start);
            var attribute = LuaLocalAttributeKind.None;

            if (tokens.Any(static token => token.Kind == LuaTokenKind.LessThan))
            {
                var attributeToken = tokens
                    .Where(static token => token.Kind == LuaTokenKind.Identifier)
                    .Skip(1)
                    .FirstOrDefault();
                attribute = ReadAttributeKind(attributeToken);
            }

            return new AttributedName(name, attribute);
        }

        private LuaLocalAttributeKind ReadAttributeKind(LuaSyntaxToken? attributeToken)
        {
            if (attributeToken is null || attributeToken.IsMissing)
            {
                return LuaLocalAttributeKind.None;
            }

            return GetName(attributeToken) switch
            {
                "const" => LuaLocalAttributeKind.Constant,
                "close" => LuaLocalAttributeKind.ToBeClosed,
                var unknown => ReportUnknownAttribute(attributeToken, unknown),
            };
        }

        private LuaLocalAttributeKind ReportUnknownAttribute(LuaSyntaxToken token, string name)
        {
            AddDiagnostic(
                "LUA3003",
                token.Span,
                $"unknown attribute '{name}'");
            return LuaLocalAttributeKind.None;
        }

        private void BindExpression(
            LuaSyntaxNode expression,
            LuaReferenceAccess access = LuaReferenceAccess.Read) // NOSONAR: exhaustive syntax dispatcher
        {
            switch (expression.Kind)
            {
                case LuaSyntaxKind.IdentifierExpression:
                    BindIdentifier(expression, access);
                    break;
                case LuaSyntaxKind.MemberAccessExpression:
                    BindMemberExpression(expression, access);
                    break;
                case LuaSyntaxKind.IndexExpression:
                    BindIndexExpression(expression, access);
                    break;
                case LuaSyntaxKind.CallExpression:
                    BindCallExpression(expression);
                    break;
                case LuaSyntaxKind.MethodCallExpression:
                    BindMethodCallExpression(expression);
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

        private void BindIdentifier(LuaSyntaxNode expression, bool isWrite) =>
            BindIdentifier(
                expression,
                isWrite ? LuaReferenceAccess.Write : LuaReferenceAccess.Read);

        private void BindIdentifier(LuaSyntaxNode expression, LuaReferenceAccess access)
        {
            var token = expression.ChildTokens().FirstOrDefault(static candidate =>
                candidate.Kind == LuaTokenKind.Identifier);
            if (token is not null && !token.IsMissing)
            {
                BindNameToken(token, access);
            }
        }

        private void BindCallExpression(LuaSyntaxNode expression)
        {
            var children = expression.ChildNodes().ToArray();
            var callee = children.FirstOrDefault(static child => child.Kind != LuaSyntaxKind.ArgumentList);
            if (callee is not null)
            {
                BindExpression(callee, LuaReferenceAccess.Read | LuaReferenceAccess.Call);
            }

            foreach (var arguments in children.Where(static child => child.Kind == LuaSyntaxKind.ArgumentList))
            {
                BindExpression(arguments);
            }
        }

        private void BindMethodCallExpression(LuaSyntaxNode expression)
        {
            BindMemberExpression(
                expression,
                LuaReferenceAccess.Read | LuaReferenceAccess.Call | LuaReferenceAccess.MethodCall);
            foreach (var arguments in expression.ChildNodes().Where(static child =>
                         child.Kind == LuaSyntaxKind.ArgumentList))
            {
                BindExpression(arguments);
            }
        }

        private void BindMemberExpression(LuaSyntaxNode expression, LuaReferenceAccess access)
        {
            var receiver = expression.ChildNodes().FirstOrDefault(static child =>
                child.Kind != LuaSyntaxKind.ArgumentList);
            if (receiver is not null)
            {
                BindExpression(receiver);
            }

            var member = expression.ChildTokens().LastOrDefault(static token =>
                token.Kind == LuaTokenKind.Identifier);
            RecordMemberReference(
                member?.Span ?? new TextSpan(expression.Span.End, 0),
                member is { IsMissing: false } ? GetName(member) : null,
                LuaReferenceKind.Member,
                access,
                receiver?.Span ?? new TextSpan(expression.Span.Start, 0),
                indexSpan: null,
                member is { IsMissing: false }
                    ? LuaReferenceResolutionKind.MemberCandidate
                    : LuaReferenceResolutionKind.Incomplete,
                member is { IsMissing: false } ? "member-name" : "incomplete-member-name");
        }

        private void BindIndexExpression(LuaSyntaxNode expression, LuaReferenceAccess access)
        {
            var children = expression.ChildNodes().ToArray();
            var receiver = children.FirstOrDefault();
            var index = children.Skip(1).FirstOrDefault();
            if (receiver is not null)
            {
                BindExpression(receiver);
            }

            if (index is not null)
            {
                BindExpression(index);
            }

            var candidate = string.Empty;
            var hasCandidate = index is not null && index.TryGetConstantString(out candidate);
            RecordMemberReference(
                index?.Span ?? new TextSpan(expression.Span.End, 0),
                hasCandidate ? candidate : null,
                LuaReferenceKind.Index,
                access,
                receiver?.Span ?? new TextSpan(expression.Span.Start, 0),
                index?.Span,
                index is null
                    ? LuaReferenceResolutionKind.Incomplete
                    : hasCandidate
                        ? LuaReferenceResolutionKind.LiteralIndexCandidate
                        : LuaReferenceResolutionKind.DynamicIndex,
                index is null ? "incomplete-index" : hasCandidate ? "literal-string-index" : "dynamic-index");
        }

        private void BindFunctionNameMembers(LuaSyntaxToken[] tokens)
        {
            var identifiers = tokens
                .Where(static token => token.Kind == LuaTokenKind.Identifier && !token.IsMissing)
                .ToArray();
            if (identifiers.Length < 2)
            {
                return;
            }

            var receiverStart = identifiers[0].Span.Start;
            for (var index = 1; index < identifiers.Length; index++)
            {
                var member = identifiers[index];
                var access = index == identifiers.Length - 1
                    ? LuaReferenceAccess.Write
                    : LuaReferenceAccess.Read;
                RecordMemberReference(
                    member.Span,
                    GetName(member),
                    LuaReferenceKind.Member,
                    access,
                    TextSpan.FromBounds(receiverStart, member.Span.Start),
                    indexSpan: null,
                    LuaReferenceResolutionKind.MemberCandidate,
                    "function-declaration-member");
            }
        }

        private void RecordMemberReference(
            TextSpan span,
            string? name,
            LuaReferenceKind kind,
            LuaReferenceAccess access,
            TextSpan receiverSpan,
            TextSpan? indexSpan,
            LuaReferenceResolutionKind resolutionKind,
            string reason)
        {
            if (!_options.CollectCodeReferences)
            {
                return;
            }

            var reference = new LuaMemberReference(
                span,
                name,
                kind,
                access,
                receiverSpan,
                indexSpan,
                _currentFunction.Id,
                resolutionKind,
                reason);
            _memberReferences.Add(reference);
            _codeReferences.Add(new LuaCodeReference(
                span,
                name,
                kind,
                access,
                receiverSpan,
                indexSpan,
                _currentFunction.Id,
                LexicalReference: null,
                CandidateName: name,
                resolutionKind,
                reason));
        }

        private void BindNameToken(LuaSyntaxToken token, bool isWrite)
        {
            BindNameToken(
                token,
                isWrite ? LuaReferenceAccess.Write : LuaReferenceAccess.Read);
        }

        private void BindNameToken(LuaSyntaxToken token, LuaReferenceAccess access)
        {
            var isWrite = (access & LuaReferenceAccess.Write) != 0;
            var name = GetName(token);
            var symbol = FindActiveSymbol(name);
            LuaNameResolutionKind resolutionKind;

            if (symbol is { Kind: LuaSymbolKind.Global })
            {
                if (isWrite && symbol.IsReadOnly)
                {
                    AddDiagnostic(
                        "LUA3002",
                        token.Span,
                        $"attempt to assign to const variable '{name}'");
                }

                symbol = FindActiveEnvironment()
                    ?? throw new InvalidOperationException("The implicit _ENV symbol is missing.");
                resolutionKind = LuaNameResolutionKind.Global;
                if (symbol.FunctionId != _currentFunction.Id)
                {
                    Capture(symbol);
                }
            }
            else if (symbol is not null)
            {
                resolutionKind = symbol.Kind == LuaSymbolKind.Environment ||
                    symbol.FunctionId != _currentFunction.Id
                    ? LuaNameResolutionKind.Upvalue
                    : LuaNameResolutionKind.Local;
                if (resolutionKind == LuaNameResolutionKind.Upvalue &&
                    symbol.FunctionId != _currentFunction.Id)
                {
                    Capture(symbol);
                }

                if (isWrite && symbol.IsReadOnly)
                {
                    AddDiagnostic(
                        "LUA3002",
                        token.Span,
                        $"attempt to assign to const variable '{name}'");
                }
            }
            else
            {
                var wildcard = _lastGlobalWildcard;
                var hasExplicitGlobalContext = _explicitGlobalContextCount > 0;
                if (wildcard is not null && isWrite && wildcard.IsReadOnly)
                {
                    AddDiagnostic(
                        "LUA3002",
                        token.Span,
                        $"attempt to assign to const variable '{name}'");
                }
                else if (wildcard is null && hasExplicitGlobalContext &&
                         _options.LanguageVersion == LuaLanguageVersion.Lua55)
                {
                    AddDiagnostic(
                        "LUA3015",
                        token.Span,
                        $"no variable '{name}' declared");
                }

                symbol = FindActiveEnvironment()
                    ?? throw new InvalidOperationException("The implicit _ENV symbol is missing.");
                resolutionKind = LuaNameResolutionKind.Global;
                if (symbol.FunctionId != _currentFunction.Id)
                {
                    Capture(symbol);
                }
            }

            var reference = new LuaNameReference(
                token.Span,
                name,
                resolutionKind,
                symbol,
                isWrite);
            _references.Add(reference);
            if (_options.CollectCodeReferences)
            {
                _codeReferences.Add(new LuaCodeReference(
                    token.Span,
                    name,
                    LuaReferenceKind.Name,
                    access,
                    ReceiverSpan: null,
                    IndexSpan: null,
                    _currentFunction.Id,
                    reference,
                    CandidateName: name,
                    LuaReferenceResolutionKind.LexicalSymbol,
                    "lexical-symbol"));
            }
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
            _currentScope = new ScopeFrame(
                parent: null,
                depth: 0,
                activeBase,
                entryActiveLocalCount: 0);
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

                var parameterTokens = parameters.ChildTokens().ToArray();
                var varArgIndex = Array.FindIndex(parameterTokens, static token =>
                    token.Kind == LuaTokenKind.VarArg);
                for (var index = 0; index < parameterTokens.Length; index++)
                {
                    var parameter = parameterTokens[index];
                    if (parameter.Kind != LuaTokenKind.Identifier || parameter.IsMissing)
                    {
                        continue;
                    }

                    var isNamedVarArg = varArgIndex >= 0 && index > varArgIndex;
                    DeclareToken(
                        parameter,
                        isNamedVarArg ? LuaSymbolKind.Local : LuaSymbolKind.Parameter,
                        isNamedVarArg ? LuaLocalAttributeKind.VarArg : LuaLocalAttributeKind.None);
                }

                BindBlock(
                    GetDirectChild(functionBody, LuaSyntaxKind.Block),
                    createScope: false);
                ResolveGotos(function);
                CompleteFunction(function);
            }
            finally
            {
                DeactivateActiveSymbols(activeBase);
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
            var existing = FindDuplicateLabel(name);
            if (existing is not null)
            {
                var originalLine = _syntax.Source.GetLocation(existing.Span.Start).Line + 1;
                AddDiagnostic(
                    "LUA3006",
                    token.Span,
                    $"label '{name}' already defined on line {originalLine}");
                return;
            }

            var active = terminalLabel
                ? _activeSymbols.Take(_currentScope.EntryActiveSymbolCount).ToImmutableArray()
                : _activeSymbols.ToImmutableArray();
            var label = new LabelRecord(name, token.Span, _currentScope, active);
            _currentScope.EnsureLabels().Add(name, label);
        }

        private LabelRecord? FindDuplicateLabel(string name)
        {
            if (_currentScope.Labels is not null &&
                _currentScope.Labels.TryGetValue(name, out var current))
            {
                return current;
            }

            if (_options.LanguageVersion is not
                (LuaLanguageVersion.Lua54 or LuaLanguageVersion.Lua55))
            {
                return null;
            }

            for (var scope = _currentScope.Parent; scope is not null; scope = scope.Parent)
            {
                if (scope.Labels is not null && scope.Labels.TryGetValue(name, out var inherited))
                {
                    return inherited;
                }
            }

            return null;
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
                    if (scope.Labels is not null && scope.Labels.TryGetValue(@goto.Name, out label))
                    {
                        break;
                    }
                }

                if (label is null)
                {
                    var gotoLine = _syntax.Source.GetLocation(@goto.Span.Start).Line + 1;
                    AddDiagnostic(
                        "LUA3007",
                        @goto.Span,
                        $"no visible label '{@goto.Name}' for <goto> at line {gotoLine}");
                    continue;
                }

                var gotoSymbols = @goto.ActiveSymbols.Select(static symbol => symbol.Id).ToHashSet();
                var entered = label.ActiveSymbols.FirstOrDefault(symbol => !gotoSymbols.Contains(symbol.Id));
                if (entered is not null)
                {
                    var gotoLine = _syntax.Source.GetLocation(@goto.Span.Start).Line + 1;
                    AddDiagnostic(
                        "LUA3008",
                        @goto.Span,
                        $"<goto {@goto.Name}> at line {gotoLine} jumps into the scope of " +
                        $"local '{entered.Name}'");
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
                    if (function.Captures.Count == _options.MaximumUpvaluesPerFunction + 1)
                    {
                        var functionLine = _syntax.Source.GetLocation(function.Span.Start).Line + 1;
                        AddDiagnostic(
                            "LUA3010",
                            function.Span,
                            $"too many upvalues (limit is {_options.MaximumUpvaluesPerFunction}) " +
                            $"in function at line {functionLine}");
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
            // Undo entries grow in lockstep with _activeSymbols so scope exits can
            // restore the by-name index, wildcard pointer, and global counters by
            // popping the same number of entries.
            _activeSymbolUndo.Add(new ActiveSymbolUndo(
                symbol.Name,
                _activeSymbolsByName.TryGetValue(symbol.Name, out var previous) ? previous : null,
                _lastGlobalWildcard,
                symbol.Kind is LuaSymbolKind.Global or LuaSymbolKind.GlobalWildcard));
            _activeSymbolsByName[symbol.Name] = symbol;
            _lastGlobalWildcard = symbol.Kind == LuaSymbolKind.GlobalWildcard ? symbol : _lastGlobalWildcard;
            if (symbol.Kind is LuaSymbolKind.Global or LuaSymbolKind.GlobalWildcard)
            {
                _explicitGlobalContextCount++;
            }

            _activeSymbols.Add(symbol);
            if (symbol.Kind is LuaSymbolKind.Environment or
                LuaSymbolKind.Global or LuaSymbolKind.GlobalWildcard)
            {
                return;
            }

            var activeInFunction = ++_currentFunction.ActiveLocalCount;
            if (activeInFunction == _options.MaximumActiveLocalsPerFunction + 1)
            {
                var functionLine = _syntax.Source.GetLocation(_currentFunction.Span.Start).Line + 1;
                AddDiagnostic(
                    "LUA3009",
                    _currentFunction.Span,
                    $"too many local variables (limit is {_options.MaximumActiveLocalsPerFunction}) " +
                    $"in function at line {functionLine}");
            }
        }

        private void DeactivateActiveSymbols(int fromIndex)
        {
            if (_activeSymbols.Count > fromIndex)
            {
                _activeSymbols.RemoveRange(fromIndex, _activeSymbols.Count - fromIndex);
            }

            // Restore in reverse activation order so stacked shadowing of one name
            // unwinds to the correct outer binding.
            for (var index = _activeSymbolUndo.Count - 1; index >= fromIndex; index--)
            {
                var undo = _activeSymbolUndo[index];
                if (undo.PreviousByName is null)
                {
                    _activeSymbolsByName.Remove(undo.Name);
                }
                else
                {
                    _activeSymbolsByName[undo.Name] = undo.PreviousByName;
                }

                _lastGlobalWildcard = undo.PreviousWildcard;
                if (undo.CountedAsExplicitGlobal)
                {
                    _explicitGlobalContextCount--;
                }
            }

            if (_activeSymbolUndo.Count > fromIndex)
            {
                _activeSymbolUndo.RemoveRange(fromIndex, _activeSymbolUndo.Count - fromIndex);
            }
        }

        private LuaSymbol? FindActiveSymbol(string name) =>
            _activeSymbolsByName.TryGetValue(name, out var symbol) ? symbol : null;

        private LuaSymbol? FindActiveEnvironment() =>
            // A lexical local named _ENV shadows the implicit environment upvalue.
            // Keep the lookup here (rather than in every global reference branch) so
            // nested functions and explicit global declarations share one boundary.
            _activeSymbolsByName.TryGetValue("_ENV", out var symbol) ? symbol : null;

        private ScopeFrame EnterScope()
        {
            var previous = _currentScope;
            _currentScope = new ScopeFrame(
                previous,
                previous.Depth + 1,
                _activeSymbols.Count,
                _currentFunction.ActiveLocalCount);
            return previous;
        }

        private void ExitScope(ScopeFrame previous)
        {
            DeactivateActiveSymbols(_currentScope.EntryActiveSymbolCount);
            _currentFunction.ActiveLocalCount = _currentScope.EntryActiveLocalCount;
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
            _names.Intern(_syntax.Source.GetSpan(token.Span));

        /// <summary>
        /// Interns identifier names by their UTF-8 source bytes. Lua identifiers are
        /// ASCII, so a small open-addressing table keyed by a byte hash lets repeated
        /// occurrences of one name share a single string instead of allocating per use.
        /// </summary>
        private sealed class LuaNameInterner
        {
            private string?[] _slots = new string[64];
            private int _count;

            public string Intern(ReadOnlySpan<byte> name)
            {
                var slot = FindSlot(name);
                if (_slots[slot] is { } existing)
                {
                    return existing;
                }

                var result = Encoding.ASCII.GetString(name);
                _slots[slot] = result;
                _count++;
                if (_count * 4 >= _slots.Length * 3)
                {
                    Rehash(_slots.Length * 2);
                }

                return result;
            }

            private int FindSlot(ReadOnlySpan<byte> name)
            {
                var mask = (uint)_slots.Length - 1;
                var probe = HashBytes(name) & mask;
                while (_slots[probe] is { } existing && !BytesMatch(existing, name))
                {
                    probe = (probe + 1) & mask;
                }

                return (int)probe;
            }

            private int FindSlot(string value)
            {
                var mask = (uint)_slots.Length - 1;
                var probe = HashChars(value) & mask;
                while (_slots[probe] is not null)
                {
                    probe = (probe + 1) & mask;
                }

                return (int)probe;
            }

            private void Rehash(int newSize)
            {
                var previous = _slots;
                _slots = new string[newSize];
                foreach (var entry in previous)
                {
                    if (entry is not null)
                    {
                        _slots[FindSlot(entry)] = entry;
                    }
                }
            }

            private static bool BytesMatch(string existing, ReadOnlySpan<byte> name)
            {
                if (existing.Length != name.Length)
                {
                    return false;
                }

                for (var index = 0; index < name.Length; index++)
                {
                    if (existing[index] != name[index])
                    {
                        return false;
                    }
                }

                return true;
            }

            private static uint HashBytes(ReadOnlySpan<byte> name)
            {
                var hash = (uint)name.Length;
                foreach (var byteValue in name)
                {
                    hash = (hash ^ byteValue) * 16777619u;
                }

                return hash;
            }

            private static uint HashChars(string value)
            {
                var hash = (uint)value.Length;
                foreach (var character in value)
                {
                    hash = (hash ^ character) * 16777619u;
                }

                return hash;
            }
        }

        private readonly record struct ActiveSymbolUndo(
            string Name,
            LuaSymbol? PreviousByName,
            LuaSymbol? PreviousWildcard,
            bool CountedAsExplicitGlobal);

        private static LuaSyntaxNode GetDirectChild(LuaSyntaxNode node, LuaSyntaxKind kind)
        {
            LuaSyntaxNode? match = null;
            foreach (var child in node.Children)
            {
                if (child.Node is { } candidate && candidate.Kind == kind)
                {
                    if (match is not null)
                    {
                        throw new InvalidOperationException(
                            $"Multiple children of kind {kind} exist under {node.Kind}.");
                    }

                    match = candidate;
                }
            }

            return match ?? throw new InvalidOperationException(
                $"No child of kind {kind} exists under {node.Kind}.");
        }

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

            public int ActiveLocalCount { get; set; }

            public List<LuaSymbol> Symbols { get; } = [];

            public List<LuaSymbol> Captures { get; } = [];

            public HashSet<int> CaptureIds { get; } = [];

            public List<GotoRecord> Gotos { get; } = [];
        }

        private sealed class ScopeFrame
        {
            public ScopeFrame(
                ScopeFrame? parent,
                int depth,
                int entryActiveSymbolCount,
                int entryActiveLocalCount)
            {
                Parent = parent;
                Depth = depth;
                EntryActiveSymbolCount = entryActiveSymbolCount;
                EntryActiveLocalCount = entryActiveLocalCount;
            }

            public ScopeFrame? Parent { get; }

            public int Depth { get; }

            public int EntryActiveSymbolCount { get; }

            public int EntryActiveLocalCount { get; }

            /// <summary>Label table for this scope, allocated on first label use.</summary>
            public Dictionary<string, LabelRecord>? Labels { get; private set; }

            public Dictionary<string, LabelRecord> EnsureLabels() =>
                Labels ??= new Dictionary<string, LabelRecord>(StringComparer.Ordinal);
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
            LuaLocalAttributeKind Attribute,
            bool IsPrefix = false);
    }
}
