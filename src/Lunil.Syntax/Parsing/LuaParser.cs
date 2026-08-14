using System.Collections.Immutable;
using Lunil.Core;
using Lunil.Core.Diagnostics;
using Lunil.Core.Text;
using Lunil.Syntax.Lexing;

namespace Lunil.Syntax.Parsing;

/// <summary>A lossless, error-tolerant parser for the versioned Lua 5.1–5.5 grammar.</summary>
public static class LuaParser
{
    public static LuaParseResult Parse(
        SourceText source,
        LuaLexerOptions? lexerOptions = null,
        LuaParserOptions? parserOptions = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        parserOptions ??= LuaParserOptions.Default with
        {
            LanguageVersion = lexerOptions?.LanguageVersion ?? LuaLanguageVersions.Default,
        };
        lexerOptions ??= LuaLexerOptions.Default with
        {
            LanguageVersion = parserOptions.LanguageVersion,
        };
        return Parse(LuaLexer.Lex(source, lexerOptions), parserOptions, cancellationToken);
    }

    public static LuaParseResult Parse(
        LuaLexResult lexResult,
        LuaParserOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        LunilGuard.NotNull(lexResult);
        options ??= LuaParserOptions.Default with
        {
            LanguageVersion = lexResult.LanguageVersion,
        };
        ValidateOptions(options);
        if (options.LanguageVersion != lexResult.LanguageVersion)
        {
            throw new ArgumentException(
                "The lexer and parser language versions must match.",
                nameof(options));
        }

        return new Implementation(lexResult, options, cancellationToken).Parse();
    }

    public static LuaParseResult ParseIncremental(
        LuaParseResult previous,
        LuaTextChange change,
        LuaLexerOptions? lexerOptions = null,
        LuaParserOptions? parserOptions = null,
        CancellationToken cancellationToken = default)
    {
        LunilGuard.NotNull(previous);
        LunilGuard.NotNull(change);
        cancellationToken.ThrowIfCancellationRequested();
        var newSource = change.Apply(previous.Source);
        lexerOptions ??= previous.Configuration.Lexer;
        parserOptions ??= previous.Configuration.Parser;
        if (lexerOptions != previous.Configuration.Lexer ||
            parserOptions != previous.Configuration.Parser)
        {
            return FullIncrementalReparse(
                newSource,
                change,
                lexerOptions,
                parserOptions,
                "configuration-changed",
                cancellationToken);
        }

        if (!change.IsUtf8BoundarySafe(previous.Source))
        {
            return FullIncrementalReparse(
                newSource,
                change,
                lexerOptions,
                parserOptions,
                "unsafe-utf8-boundary",
                cancellationToken);
        }

        var rootChildren = previous.Root.Children;
        if (rootChildren.Length != 2 ||
            rootChildren[0].Node is not { Kind: LuaSyntaxKind.Block } previousBlock ||
            rootChildren[1].Token is null)
        {
            return FullIncrementalReparse(
                newSource,
                change,
                lexerOptions,
                parserOptions,
                "noncanonical-root",
                cancellationToken);
        }

        var previousStatements = previousBlock.Children;
        var prefixCount = 0;
        while (prefixCount < previousStatements.Length &&
               previousStatements[prefixCount].Node is { } statement &&
               statement.FullSpan.End <= change.Span.Start)
        {
            prefixCount++;
        }

        var reparseStart = prefixCount < previousStatements.Length
            ? previousStatements[prefixCount].Node!.FullSpan.Start
            : rootChildren[1].Token!.FullSpan.Start;
        if (reparseStart > change.Span.Start)
        {
            reparseStart = change.Span.Start;
            prefixCount = 0;
        }

        var fragmentSource = new SourceText(newSource.AsSpan()[reparseStart..]);
        var fragmentLexerOptions = reparseStart == 0
            ? lexerOptions
            : lexerOptions with
            {
                AcceptUtf8ByteOrderMark = false,
                AcceptShebang = false,
            };
        var fragment = Parse(
            fragmentSource,
            fragmentLexerOptions,
            parserOptions,
            cancellationToken);
        var shiftedFragmentRoot = fragment.Root.WithPositionDelta(reparseStart);
        var fragmentRootChildren = shiftedFragmentRoot.Children;
        var fragmentBlock = fragmentRootChildren[0].Node!;
        var combinedStatements = ImmutableArray.CreateBuilder<LuaSyntaxElement>(
            prefixCount + fragmentBlock.Children.Length);
        for (var index = 0; index < prefixCount; index++)
        {
            combinedStatements.Add(previousStatements[index]);
        }

        combinedStatements.AddRange(fragmentBlock.Children);
        var combinedBlock = new LuaSyntaxNode(
            LuaSyntaxKind.Block,
            combinedStatements.MoveToImmutable(),
            0);
        var combinedRootChildren = ImmutableArray.CreateBuilder<LuaSyntaxElement>(2);
        combinedRootChildren.Add(combinedBlock);
        combinedRootChildren.Add(fragmentRootChildren[1].Token!);
        var combinedRoot = new LuaSyntaxNode(
            LuaSyntaxKind.CompilationUnit,
            combinedRootChildren.MoveToImmutable(),
            0);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(previous.Diagnostics.Where(diagnostic =>
            diagnostic.Span.End <= reparseStart));
        diagnostics.AddRange(fragment.Diagnostics.Select(diagnostic => new Diagnostic(
            diagnostic.Code,
            diagnostic.Severity,
            new TextSpan(checked(diagnostic.Span.Start + reparseStart), diagnostic.Span.Length),
            diagnostic.Message)));
        var finalDiagnostics = diagnostics.Count <= parserOptions.MaximumDiagnosticCount
            ? diagnostics.ToImmutable()
            : diagnostics.Take(parserOptions.MaximumDiagnosticCount).ToImmutableArray();

        CountReused(previousStatements, prefixCount, out var reusedNodes, out var reusedTokens);
        return new LuaParseResult(newSource, combinedRoot, finalDiagnostics)
        {
            LanguageVersion = parserOptions.LanguageVersion,
            Configuration = new LuaParseConfiguration(lexerOptions, parserOptions),
            CompactMetrics = fragment.CompactMetrics,
            IncrementalMetrics = new LuaIncrementalParseMetrics(
                WasFullReparse: false,
                Reason: "top-level-suffix",
                ChangedOldSpan: change.Span,
                ReparsedNewSpan: new TextSpan(reparseStart, newSource.Length - reparseStart),
                ReusedNodeCount: reusedNodes,
                ReusedTokenCount: reusedTokens),
        };
    }

    private static LuaParseResult FullIncrementalReparse(
        SourceText newSource,
        LuaTextChange change,
        LuaLexerOptions lexerOptions,
        LuaParserOptions parserOptions,
        string reason,
        CancellationToken cancellationToken)
    {
        var result = Parse(newSource, lexerOptions, parserOptions, cancellationToken);
        return result with
        {
            IncrementalMetrics = new LuaIncrementalParseMetrics(
                WasFullReparse: true,
                Reason: reason,
                ChangedOldSpan: change.Span,
                ReparsedNewSpan: new TextSpan(0, newSource.Length),
                ReusedNodeCount: 0,
                ReusedTokenCount: 0),
        };
    }

    private static void CountReused(
        ImmutableArray<LuaSyntaxElement> statements,
        int count,
        out int nodeCount,
        out int tokenCount)
    {
        nodeCount = 0;
        tokenCount = 0;
        var stack = new Stack<LuaSyntaxElement>();
        for (var index = 0; index < count; index++)
        {
            stack.Push(statements[index]);
        }

        while (stack.Count != 0)
        {
            var element = stack.Pop();
            if (element.Token is not null)
            {
                tokenCount++;
                continue;
            }

            if (element.Node is not { } node)
            {
                continue;
            }

            nodeCount++;
            foreach (var child in node.Children)
            {
                stack.Push(child);
            }
        }
    }

    private static void ValidateOptions(LuaParserOptions options)
    {
        if (!LuaLanguageVersions.IsKnown(options.LanguageVersion))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.LanguageVersion,
                "The parser language version is invalid.");
        }

        LunilGuard.Positive(options.MaximumRecursionDepth);
        LunilGuard.Positive(options.MaximumNodeCount);
        LunilGuard.Positive(options.MaximumDiagnosticCount);
        if (options.MaximumRecursionDepth > LuaParserOptions.MaximumSupportedRecursionDepth)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaximumRecursionDepth,
                $"Parser recursion depth cannot exceed {LuaParserOptions.MaximumSupportedRecursionDepth}.");
        }
    }

    private sealed class Implementation
    {
        private const int MaximumLocalsPerDeclaration = 200;

        private readonly LuaLexResult _lexResult;
        private readonly LuaParserOptions _options;
        private readonly LuaGrammarFeatures _grammarFeatures;
        private readonly CancellationToken _cancellationToken;
        private readonly ImmutableArray<Diagnostic>.Builder _diagnostics;
        private readonly Stack<int> _functionStartPositions = [];
        private int _position;
        private int _recursionDepth;
        private int _nodeCount;
        private int _consumedTokenCount;
        private bool _nodeBudgetExceeded;

        public Implementation(
            LuaLexResult lexResult,
            LuaParserOptions options,
            CancellationToken cancellationToken)
        {
            _lexResult = lexResult;
            _options = options;
            _grammarFeatures = LuaGrammarFeatureTable.Get(options.LanguageVersion);
            _cancellationToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            _diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            _diagnostics.AddRange(lexResult.Diagnostics.Take(options.MaximumDiagnosticCount));
        }

        public LuaParseResult Parse()
        {
            var children = new List<LuaSyntaxElement>
            {
                ParseBlock(LuaTokenKind.EndOfFile),
                Match(LuaTokenKind.EndOfFile),
            };
            var root = CreateNodeFromList(LuaSyntaxKind.CompilationUnit, children, 0);
            var diagnostics = _diagnostics.ToImmutable();
            var result = _options.UseCompactSyntaxArena
                ? new LuaParseResult(
                    _lexResult.Source,
                    LuaSyntaxArena.Create(root, _nodeCount, _lexResult.Tokens.Length),
                    diagnostics)
                : new LuaParseResult(_lexResult.Source, root, diagnostics);
            return result with
            {
                LanguageVersion = _options.LanguageVersion,
                Configuration = new LuaParseConfiguration(_lexResult.Options, _options),
            };
        }

        private LuaSyntaxNode ParseBlock(LuaTokenKind terminator) =>
            ParseBlock(TokenSet.Create(terminator));

        private LuaSyntaxNode ParseBlock(
            LuaTokenKind terminator0,
            LuaTokenKind terminator1,
            LuaTokenKind terminator2) =>
            ParseBlock(TokenSet.Create(terminator0, terminator1, terminator2));

        private LuaSyntaxNode ParseBlock(TokenSet terminators)
        {
            var start = Current.Span.Start;
            if (!TryEnterRecursion("block"))
            {
                return CreateNode(
                    LuaSyntaxKind.Block,
                    [CreateMissingToken(LuaTokenKind.BadToken)],
                    start);
            }

            try
            {
                var statements = new List<LuaSyntaxElement>();
                var sawReturn = false;

                while (Current.Kind != LuaTokenKind.EndOfFile && !terminators.Contains(Current.Kind))
                {
                    if (_nodeBudgetExceeded)
                    {
                        statements.Add(ConsumeErrorUntil(terminators));
                        break;
                    }

                    if (sawReturn)
                    {
                        AddDiagnostic(
                            "LUA2008",
                            Current.Span,
                            "A return statement must be the final statement in its block.");
                        sawReturn = false;
                    }

                    var previous = _position;
                    var statement = ParseStatement();
                    statements.Add(statement);
                    sawReturn = statement.Kind == LuaSyntaxKind.ReturnStatement;

                    if (_position == previous)
                    {
                        statements.Add(CreateNode(
                            LuaSyntaxKind.Error,
                            [Consume()],
                            Current.Span.Start));
                    }
                }

                return CreateNodeFromList(LuaSyntaxKind.Block, statements, start);
            }
            finally
            {
                _recursionDepth--;
            }
        }

        private LuaSyntaxNode ParseStatement() => Current.Kind switch
        {
            LuaTokenKind.Semicolon => CreateNode(
                LuaSyntaxKind.EmptyStatement,
                [Consume()],
                Current.Span.Start),
            LuaTokenKind.DoubleColon => ParseLabelStatement(),
            LuaTokenKind.BreakKeyword => CreateNode(
                LuaSyntaxKind.BreakStatement,
                [Consume()],
                Current.Span.Start),
            LuaTokenKind.GotoKeyword => ParseGotoStatement(),
            LuaTokenKind.DoKeyword => ParseDoStatement(),
            LuaTokenKind.WhileKeyword => ParseWhileStatement(),
            LuaTokenKind.RepeatKeyword => ParseRepeatStatement(),
            LuaTokenKind.IfKeyword => ParseIfStatement(),
            LuaTokenKind.ForKeyword => ParseForStatement(),
            LuaTokenKind.FunctionKeyword => ParseFunctionDeclarationStatement(),
            LuaTokenKind.GlobalKeyword => ParseGlobalDeclarationStatement(),
            LuaTokenKind.LocalKeyword => ParseLocalStatement(),
            LuaTokenKind.ReturnKeyword => ParseReturnStatement(),
            LuaTokenKind.Identifier or LuaTokenKind.OpenParenthesis =>
                ParseAssignmentOrCallStatement(),
            LuaTokenKind.BadToken => CreateNode(
                LuaSyntaxKind.Error,
                [Consume()],
                Current.Span.Start),
            _ => ParseUnexpectedStatement(),
        };

        private LuaSyntaxNode ParseGlobalDeclarationStatement()
        {
            var start = Current.Span.Start;
            var children = new List<LuaSyntaxElement> { Consume() };
            if (!_grammarFeatures.SupportsGlobalDeclarations)
            {
                AddDiagnostic("LUA2013", children[0].Token!.Span,
                    "Global declarations are only available in Lua 5.5.");
            }

            if (Current.Kind == LuaTokenKind.FunctionKeyword)
            {
                children.Add(Consume());
                children.Add(Match(LuaTokenKind.Identifier));
                children.Add(ParseFunctionBody(children[^2].Token!.Span.Start));
                return CreateNodeFromList(LuaSyntaxKind.GlobalDeclarationStatement, children, start);
            }

            if (Current.Kind == LuaTokenKind.LessThan)
            {
                children.Add(Consume());
                var attribute = Match(LuaTokenKind.Identifier);
                children.Add(attribute);
                children.Add(Match(LuaTokenKind.GreaterThan));
                ValidateAttribute(attribute);
            }

            if (Current.Kind == LuaTokenKind.Star)
            {
                children.Add(Consume());
                return CreateNodeFromList(LuaSyntaxKind.GlobalDeclarationStatement, children, start);
            }

            children.Add(ParseGlobalAttributedName());
            while (Current.Kind == LuaTokenKind.Comma)
            {
                children.Add(Consume());
                children.Add(ParseGlobalAttributedName());
            }

            if (Current.Kind == LuaTokenKind.Assign)
            {
                children.Add(Consume());
                children.Add(ParseExpressionList());
            }

            return CreateNodeFromList(LuaSyntaxKind.GlobalDeclarationStatement, children, start);
        }

        private LuaSyntaxNode ParseGlobalAttributedName()
        {
            var children = new List<LuaSyntaxElement> { Match(LuaTokenKind.Identifier) };
            if (Current.Kind == LuaTokenKind.LessThan)
            {
                children.Add(Consume());
                var attribute = Match(LuaTokenKind.Identifier);
                children.Add(attribute);
                children.Add(Match(LuaTokenKind.GreaterThan));
                ValidateAttribute(attribute);
            }

            return CreateNodeFromList(LuaSyntaxKind.AttributedName, children);
        }

        private LuaSyntaxNode ParseLabelStatement()
        {
            if (!_grammarFeatures.SupportsGotoAndLabels)
            {
                AddDiagnostic("LUA2011", Current.Span, "Labels are not available in Lua 5.1.");
            }

            return CreateNode(
                LuaSyntaxKind.LabelStatement,
                [
                    Consume(),
                    Match(LuaTokenKind.Identifier),
                    Match(LuaTokenKind.DoubleColon),
                ]);
        }

        private LuaSyntaxNode ParseGotoStatement()
        {
            if (!_grammarFeatures.SupportsGotoAndLabels)
            {
                AddDiagnostic("LUA2011", Current.Span, "goto is not available in Lua 5.1.");
            }

            return CreateNode(
                LuaSyntaxKind.GotoStatement,
                [
                    Consume(),
                    Match(LuaTokenKind.Identifier),
                ]);
        }

        private LuaSyntaxNode ParseDoStatement() => CreateNode(
            LuaSyntaxKind.DoStatement,
            [
                Consume(),
                ParseBlock(LuaTokenKind.EndKeyword),
                Match(LuaTokenKind.EndKeyword),
            ]);

        private LuaSyntaxNode ParseWhileStatement() => CreateNode(
            LuaSyntaxKind.WhileStatement,
            [
                Consume(),
                ParseExpression(),
                Match(LuaTokenKind.DoKeyword),
                ParseBlock(LuaTokenKind.EndKeyword),
                Match(LuaTokenKind.EndKeyword),
            ]);

        private LuaSyntaxNode ParseRepeatStatement() => CreateNode(
            LuaSyntaxKind.RepeatStatement,
            [
                Consume(),
                ParseBlock(LuaTokenKind.UntilKeyword),
                Match(LuaTokenKind.UntilKeyword),
                ParseExpression(),
            ]);

        private LuaSyntaxNode ParseIfStatement()
        {
            var children = new List<LuaSyntaxElement>
            {
                Consume(),
                ParseExpression(),
                Match(LuaTokenKind.ThenKeyword),
                ParseBlock(
                    LuaTokenKind.ElseIfKeyword,
                    LuaTokenKind.ElseKeyword,
                    LuaTokenKind.EndKeyword),
            };

            while (Current.Kind == LuaTokenKind.ElseIfKeyword)
            {
                children.Add(CreateNode(
                    LuaSyntaxKind.ElseIfClause,
                    [
                        Consume(),
                        ParseExpression(),
                        Match(LuaTokenKind.ThenKeyword),
                        ParseBlock(
                            LuaTokenKind.ElseIfKeyword,
                            LuaTokenKind.ElseKeyword,
                            LuaTokenKind.EndKeyword),
                    ]));
            }

            if (Current.Kind == LuaTokenKind.ElseKeyword)
            {
                children.Add(CreateNode(
                    LuaSyntaxKind.ElseClause,
                    [
                        Consume(),
                        ParseBlock(LuaTokenKind.EndKeyword),
                    ]));
            }

            children.Add(Match(LuaTokenKind.EndKeyword));
            return CreateNodeFromList(LuaSyntaxKind.IfStatement, children);
        }

        private LuaSyntaxNode ParseForStatement()
        {
            var forKeyword = Consume();
            var firstName = Match(LuaTokenKind.Identifier);
            return Current.Kind == LuaTokenKind.Assign
                ? ParseNumericForStatement(forKeyword, firstName)
                : ParseGenericForStatement(forKeyword, firstName);
        }

        private LuaSyntaxNode ParseNumericForStatement(
            LuaSyntaxToken forKeyword,
            LuaSyntaxToken name)
        {
            var children = new List<LuaSyntaxElement>
            {
                forKeyword,
                name,
                Consume(),
                ParseExpression(),
                Match(LuaTokenKind.Comma),
                ParseExpression(),
            };

            if (Current.Kind == LuaTokenKind.Comma)
            {
                children.Add(Consume());
                children.Add(ParseExpression());
            }

            children.Add(Match(LuaTokenKind.DoKeyword));
            children.Add(ParseBlock(LuaTokenKind.EndKeyword));
            children.Add(Match(LuaTokenKind.EndKeyword));
            return CreateNodeFromList(LuaSyntaxKind.NumericForStatement, children);
        }

        private LuaSyntaxNode ParseGenericForStatement(
            LuaSyntaxToken forKeyword,
            LuaSyntaxToken firstName)
        {
            var names = new List<LuaSyntaxElement> { firstName };
            while (Current.Kind == LuaTokenKind.Comma)
            {
                names.Add(Consume());
                names.Add(Match(LuaTokenKind.Identifier));
            }

            return CreateNode(
                LuaSyntaxKind.GenericForStatement,
                [
                    forKeyword,
                    CreateNodeFromList(LuaSyntaxKind.NameList, names),
                    Match(LuaTokenKind.InKeyword),
                    ParseExpressionList(),
                    Match(LuaTokenKind.DoKeyword),
                    ParseBlock(LuaTokenKind.EndKeyword),
                    Match(LuaTokenKind.EndKeyword),
                ]);
        }

        private LuaSyntaxNode ParseFunctionDeclarationStatement()
        {
            var functionKeyword = Consume();
            return CreateNode(
                LuaSyntaxKind.FunctionDeclarationStatement,
                [
                    functionKeyword,
                    ParseFunctionName(),
                    ParseFunctionBody(functionKeyword.Span.Start),
                ]);
        }

        private LuaSyntaxNode ParseFunctionName()
        {
            var children = new List<LuaSyntaxElement>
            {
                Match(LuaTokenKind.Identifier),
            };

            while (Current.Kind == LuaTokenKind.Dot)
            {
                children.Add(Consume());
                children.Add(Match(LuaTokenKind.Identifier));
            }

            if (Current.Kind == LuaTokenKind.Colon)
            {
                children.Add(Consume());
                children.Add(Match(LuaTokenKind.Identifier));
            }

            return CreateNodeFromList(LuaSyntaxKind.FunctionName, children);
        }

        private LuaSyntaxNode ParseLocalStatement()
        {
            var localKeyword = Consume();
            if (Current.Kind == LuaTokenKind.FunctionKeyword)
            {
                var functionKeyword = Consume();
                return CreateNode(
                    LuaSyntaxKind.LocalFunctionDeclarationStatement,
                    [
                        localKeyword,
                        functionKeyword,
                        Match(LuaTokenKind.Identifier),
                        ParseFunctionBody(functionKeyword.Span.Start),
                    ]);
            }

            var children = new List<LuaSyntaxElement> { localKeyword, ParseAttributedName() };
            var localCount = 1;
            while (Current.Kind == LuaTokenKind.Comma)
            {
                var comma = Consume();
                children.Add(comma);
                localCount++;
                if (localCount == MaximumLocalsPerDeclaration + 1)
                {
                    AddDiagnostic(
                        "LUA2009",
                        comma.Span,
                        GetTooManyLocalsMessage());
                }

                children.Add(ParseAttributedName());
            }

            if (Current.Kind == LuaTokenKind.Assign)
            {
                children.Add(Consume());
                children.Add(ParseExpressionList());
            }

            return CreateNodeFromList(LuaSyntaxKind.LocalDeclarationStatement, children);
        }

        private LuaSyntaxNode ParseAttributedName()
        {
            var children = new List<LuaSyntaxElement>();
            if (_grammarFeatures.SupportsPrefixAttributes &&
                Current.Kind == LuaTokenKind.LessThan)
            {
                children.Add(Consume());
                var prefixAttribute = Match(LuaTokenKind.Identifier);
                children.Add(prefixAttribute);
                children.Add(Match(LuaTokenKind.GreaterThan));
                ValidateAttribute(prefixAttribute);
                children.Add(Match(LuaTokenKind.Identifier));
                return CreateNodeFromList(LuaSyntaxKind.AttributedName, children);
            }

            children.Add(Match(LuaTokenKind.Identifier));

            if (Current.Kind == LuaTokenKind.LessThan)
            {
                var attributeStart = Current.Span.Start;
                children.Add(Consume());
                var attribute = Match(LuaTokenKind.Identifier);
                children.Add(attribute);
                children.Add(Match(LuaTokenKind.GreaterThan));
                if (!_grammarFeatures.SupportsLocalAttributes)
                {
                    AddDiagnostic(
                        "LUA2010",
                        TextSpan.FromBounds(attributeStart, Current.Span.Start),
                        "Local attributes require Lua 5.4 or later.");
                }
                else
                {
                    ValidateAttribute(attribute);
                }
            }

            return CreateNodeFromList(LuaSyntaxKind.AttributedName, children);
        }

        private LuaSyntaxNode ParseReturnStatement()
        {
            var children = new List<LuaSyntaxElement> { Consume() };
            if (Current.Kind is not (LuaTokenKind.Semicolon or LuaTokenKind.EndOfFile) &&
                !IsBlockTerminator(Current.Kind))
            {
                children.Add(ParseExpressionList());
            }

            if (Current.Kind == LuaTokenKind.Semicolon)
            {
                children.Add(Consume());
            }

            return CreateNodeFromList(LuaSyntaxKind.ReturnStatement, children);
        }

        private LuaSyntaxNode ParseAssignmentOrCallStatement()
        {
            var first = ParseSuffixedExpression();
            if (Current.Kind is LuaTokenKind.Assign or LuaTokenKind.Comma)
            {
                var variables = new List<LuaSyntaxElement> { first };
                ValidateAssignable(first);
                while (Current.Kind == LuaTokenKind.Comma)
                {
                    variables.Add(Consume());
                    var variable = ParseSuffixedExpression();
                    ValidateAssignable(variable);
                    variables.Add(variable);
                }

                return CreateNode(
                    LuaSyntaxKind.AssignmentStatement,
                    [
                        CreateNodeFromList(LuaSyntaxKind.VariableList, variables),
                        Match(LuaTokenKind.Assign),
                        ParseExpressionList(),
                    ]);
            }

            if (first.Kind is LuaSyntaxKind.CallExpression or LuaSyntaxKind.MethodCallExpression)
            {
                return CreateNode(LuaSyntaxKind.CallStatement, [first]);
            }

            AddDiagnostic(
                "LUA2004",
                first.Span,
                "A statement must be an assignment or a function call.");
            return CreateNode(LuaSyntaxKind.Error, [first]);
        }

        private LuaSyntaxNode ParseReturnExpressionError()
        {
            // Recursion-limit recovery must make progress. In particular, a deeply
            // nested table constructor otherwise re-enters its field loop at the same
            // opening brace forever. Keep EOF synthetic, but consume one real token so
            // enclosing productions can unwind deterministically.
            return Current.Kind == LuaTokenKind.EndOfFile
                ? CreateNode(LuaSyntaxKind.Error, [CreateMissingToken(LuaTokenKind.BadToken)])
                : CreateNode(LuaSyntaxKind.Error, [Consume()]);
        }

        private LuaSyntaxNode ParseUnexpectedStatement()
        {
            var token = Consume();
            AddDiagnostic("LUA2002", token.Span, $"Unexpected token {token.Kind} in a statement.");
            return CreateNode(LuaSyntaxKind.Error, [token]);
        }

        private LuaSyntaxNode ParseExpressionList()
        {
            var start = Current.Span.Start;
            var children = new List<LuaSyntaxElement> { ParseExpression() };
            while (Current.Kind == LuaTokenKind.Comma)
            {
                children.Add(Consume());
                children.Add(ParseExpression());
            }

            return CreateNodeFromList(LuaSyntaxKind.ExpressionList, children, start);
        }

        private LuaSyntaxNode ParseExpression(int minimumPrecedence = 0)
        {
            if (!TryEnterRecursion("expression"))
            {
                return ParseReturnExpressionError();
            }

            try
            {
                LuaSyntaxNode left;
                if (IsUnaryOperator(Current.Kind))
                {
                    var unaryToken = Current;
                    if (!_grammarFeatures.SupportsBitwiseOperators &&
                        unaryToken.Kind == LuaTokenKind.Tilde)
                    {
                        AddDiagnostic(
                            "LUA2012",
                            unaryToken.Span,
                            "The bitwise-not operator is not available in the selected Lua version.");
                    }

                    left = CreateNode(
                        LuaSyntaxKind.UnaryExpression,
                        [
                            Consume(),
                            ParseExpression(12),
                        ]);
                }
                else
                {
                    left = ParsePrimaryExpression();
                }

                while (TryGetBinaryPrecedence(Current.Kind, out var leftPrecedence, out var rightPrecedence) &&
                       leftPrecedence > minimumPrecedence)
                {
                    var operatorToken = Current;
                    if ((!_grammarFeatures.SupportsFloorDivision &&
                         operatorToken.Kind == LuaTokenKind.FloorDivide) ||
                        (!_grammarFeatures.SupportsBitwiseOperators &&
                         operatorToken.Kind is LuaTokenKind.Ampersand or LuaTokenKind.Pipe or
                             LuaTokenKind.ShiftLeft or LuaTokenKind.ShiftRight or LuaTokenKind.Tilde))
                    {
                        AddDiagnostic(
                            "LUA2012",
                            operatorToken.Span,
                            "This operator is not available in the selected Lua version.");
                    }

                    left = CreateNode(
                        LuaSyntaxKind.BinaryExpression,
                        [
                            left,
                            Consume(),
                            ParseExpression(rightPrecedence),
                        ]);
                }

                return left;
            }
            finally
            {
                _recursionDepth--;
            }
        }

        private LuaSyntaxNode ParsePrimaryExpression() => Current.Kind switch
        {
            LuaTokenKind.NilKeyword => CreateNode(
                LuaSyntaxKind.NilLiteralExpression,
                [Consume()]),
            LuaTokenKind.FalseKeyword => CreateNode(
                LuaSyntaxKind.FalseLiteralExpression,
                [Consume()]),
            LuaTokenKind.TrueKeyword => CreateNode(
                LuaSyntaxKind.TrueLiteralExpression,
                [Consume()]),
            LuaTokenKind.NumericLiteral => ParseNumericLiteralExpression(),
            LuaTokenKind.StringLiteral => ParseStringLiteralExpression(),
            LuaTokenKind.LongStringLiteral => CreateNode(
                LuaSyntaxKind.StringLiteralExpression,
                [Consume()]),
            LuaTokenKind.VarArg => CreateNode(
                LuaSyntaxKind.VarArgExpression,
                [Consume()]),
            LuaTokenKind.FunctionKeyword => ParseFunctionExpression(),
            LuaTokenKind.OpenBrace => ParseTableConstructor(),
            LuaTokenKind.Identifier or LuaTokenKind.OpenParenthesis => ParseSuffixedExpression(),
            _ => ParseMissingExpression(),
        };

        private LuaSyntaxNode ParseNumericLiteralExpression()
        {
            var token = Consume();
            var text = _lexResult.Source.GetSpan(token.Span);
            if (!_grammarFeatures.SupportsHexadecimalFloats &&
                text.Length >= 2 && text[0] == (byte)'0' && text[1] is (byte)'x' or (byte)'X' &&
                (text.IndexOf((byte)'.') >= 0 || text.IndexOf((byte)'p') >= 0 ||
                 text.IndexOf((byte)'P') >= 0))
            {
                AddDiagnostic(
                    "LUA2015",
                    token.Span,
                    "Hexadecimal floating-point literals require Lua 5.2 or later.");
            }

            return CreateNode(LuaSyntaxKind.NumericLiteralExpression, [token]);
        }

        private LuaSyntaxNode ParseStringLiteralExpression()
        {
            var token = Consume();
            var text = _lexResult.Source.GetSpan(token.Span);
            for (var index = 1; index + 1 < text.Length; index++)
            {
                if (text[index] != (byte)'\\')
                {
                    continue;
                }

                var escape = text[++index];
                var supported = escape switch
                {
                    (byte)'x' => _grammarFeatures.SupportsHexadecimalStringEscapes,
                    (byte)'z' => _grammarFeatures.SupportsWhitespaceEatingStringEscape,
                    (byte)'u' => _grammarFeatures.SupportsUnicodeStringEscapes,
                    _ => true,
                };
                if (!supported)
                {
                    var requiredVersion = escape == (byte)'u' ? "5.3" : "5.2";
                    AddDiagnostic(
                        "LUA2016",
                        token.Span,
                        $"The \\{(char)escape} string escape requires Lua {requiredVersion} or later.");
                }
            }

            return CreateNode(LuaSyntaxKind.StringLiteralExpression, [token]);
        }

        private LuaSyntaxNode ParseFunctionExpression()
        {
            var functionKeyword = Consume();
            return CreateNode(
                LuaSyntaxKind.FunctionExpression,
                [
                    functionKeyword,
                    ParseFunctionBody(functionKeyword.Span.Start),
                ]);
        }

        private LuaSyntaxNode ParseSuffixedExpression()
        {
            LuaSyntaxNode expression;
            if (Current.Kind == LuaTokenKind.Identifier)
            {
                expression = CreateNode(LuaSyntaxKind.IdentifierExpression, [Consume()]);
            }
            else if (Current.Kind == LuaTokenKind.OpenParenthesis)
            {
                expression = CreateNode(
                    LuaSyntaxKind.ParenthesizedExpression,
                    [
                        Consume(),
                        ParseExpression(),
                        Match(LuaTokenKind.CloseParenthesis),
                    ]);
            }
            else
            {
                return ParseMissingExpression();
            }

            while (true)
            {
                if (Current.Kind == LuaTokenKind.OpenBracket)
                {
                    expression = CreateNode(
                        LuaSyntaxKind.IndexExpression,
                        [
                            expression,
                            Consume(),
                            ParseExpression(),
                            Match(LuaTokenKind.CloseBracket),
                        ]);
                }
                else if (Current.Kind == LuaTokenKind.Dot)
                {
                    expression = CreateNode(
                        LuaSyntaxKind.MemberAccessExpression,
                        [expression, Consume(), Match(LuaTokenKind.Identifier)]);
                }
                else if (Current.Kind == LuaTokenKind.Colon)
                {
                    expression = CreateNode(
                        LuaSyntaxKind.MethodCallExpression,
                        [
                            expression,
                            Consume(),
                            Match(LuaTokenKind.Identifier),
                            ParseArgumentList(),
                        ]);
                }
                else if (CanStartArguments(Current.Kind))
                {
                    expression = CreateNode(
                        LuaSyntaxKind.CallExpression,
                        [expression, ParseArgumentList()]);
                }
                else
                {
                    break;
                }
            }

            return expression;
        }

        private LuaSyntaxNode ParseArgumentList()
        {
            var start = Current.Span.Start;
            var children = new List<LuaSyntaxElement>();
            if (Current.Kind == LuaTokenKind.OpenParenthesis)
            {
                children.Add(Consume());
                if (Current.Kind != LuaTokenKind.CloseParenthesis)
                {
                    children.Add(ParseExpressionList());
                }

                children.Add(Match(LuaTokenKind.CloseParenthesis));
            }
            else if (Current.Kind == LuaTokenKind.OpenBrace)
            {
                children.Add(ParseTableConstructor());
            }
            else if (Current.Kind is LuaTokenKind.StringLiteral or LuaTokenKind.LongStringLiteral)
            {
                children.Add(CreateNode(LuaSyntaxKind.StringLiteralExpression, [Consume()]));
            }
            else
            {
                AddExpectedDiagnostic(LuaTokenKind.OpenParenthesis);
                children.Add(CreateMissingToken(LuaTokenKind.OpenParenthesis));
                children.Add(CreateMissingToken(LuaTokenKind.CloseParenthesis));
            }

            return CreateNodeFromList(LuaSyntaxKind.ArgumentList, children, start);
        }

        private LuaSyntaxNode ParseTableConstructor()
        {
            var children = new List<LuaSyntaxElement> { Consume() };
            while (Current.Kind is not (LuaTokenKind.CloseBrace or LuaTokenKind.EndOfFile))
            {
                var previous = _position;
                children.Add(ParseTableField());
                if (Current.Kind is LuaTokenKind.Comma or LuaTokenKind.Semicolon)
                {
                    children.Add(Consume());
                }
                else if (Current.Kind != LuaTokenKind.CloseBrace)
                {
                    AddExpectedDiagnostic(LuaTokenKind.Comma);
                    children.Add(CreateMissingToken(LuaTokenKind.Comma));
                }

                if (_position == previous &&
                    Current.Kind is not (LuaTokenKind.CloseBrace or LuaTokenKind.EndOfFile))
                {
                    children.Add(CreateNode(LuaSyntaxKind.Error, [Consume()]));
                }
            }

            children.Add(Match(LuaTokenKind.CloseBrace));
            return CreateNodeFromList(LuaSyntaxKind.TableConstructorExpression, children);
        }

        private LuaSyntaxNode ParseTableField()
        {
            var children = new List<LuaSyntaxElement>();
            if (Current.Kind == LuaTokenKind.OpenBracket)
            {
                children.Add(Consume());
                children.Add(ParseExpression());
                children.Add(Match(LuaTokenKind.CloseBracket));
                children.Add(Match(LuaTokenKind.Assign));
                children.Add(ParseExpression());
            }
            else if (Current.Kind == LuaTokenKind.Identifier && Peek(1).Kind == LuaTokenKind.Assign)
            {
                children.Add(Consume());
                children.Add(Consume());
                children.Add(ParseExpression());
            }
            else
            {
                children.Add(ParseExpression());
            }

            return CreateNodeFromList(LuaSyntaxKind.TableField, children);
        }

        private LuaSyntaxNode ParseFunctionBody(int functionStartPosition)
        {
            _functionStartPositions.Push(functionStartPosition);
            try
            {
                return CreateNode(
                    LuaSyntaxKind.FunctionBody,
                    [
                        Match(LuaTokenKind.OpenParenthesis),
                        ParseParameterList(),
                        Match(LuaTokenKind.CloseParenthesis),
                        ParseBlock(LuaTokenKind.EndKeyword),
                        Match(LuaTokenKind.EndKeyword),
                    ]);
            }
            finally
            {
                _functionStartPositions.Pop();
            }
        }

        private string GetTooManyLocalsMessage()
        {
            const string prefix =
                "too many local variables (limit is 200) in ";
            if (_functionStartPositions.Count == 0)
            {
                return prefix + "main function";
            }

            var functionLine = _lexResult.Source
                .GetLocation(_functionStartPositions.Peek()).Line + 1;
            return $"{prefix}function at line {functionLine}";
        }

        private LuaSyntaxNode ParseParameterList()
        {
            var start = Current.Span.Start;
            var children = new List<LuaSyntaxElement>();
            if (Current.Kind == LuaTokenKind.VarArg)
            {
                children.Add(Consume());
                if (_grammarFeatures.SupportsNamedVarargs &&
                    Current.Kind == LuaTokenKind.Identifier)
                {
                    children.Add(Consume());
                }
            }
            else if (Current.Kind == LuaTokenKind.Identifier)
            {
                children.Add(Consume());
                while (Current.Kind == LuaTokenKind.Comma)
                {
                    children.Add(Consume());
                    if (Current.Kind == LuaTokenKind.VarArg)
                    {
                        children.Add(Consume());
                        if (_grammarFeatures.SupportsNamedVarargs &&
                            Current.Kind == LuaTokenKind.Identifier)
                        {
                            children.Add(Consume());
                        }

                        break;
                    }

                    children.Add(Match(LuaTokenKind.Identifier));
                }
            }

            return CreateNodeFromList(LuaSyntaxKind.ParameterList, children, start);
        }

        private LuaSyntaxNode ParseMissingExpression()
        {
            AddDiagnostic("LUA2003", Current.Span, "Expected a Lua expression.");
            LuaSyntaxToken token;
            if (Current.Kind == LuaTokenKind.EndOfFile || IsExpressionTerminator(Current.Kind))
            {
                token = CreateMissingToken(LuaTokenKind.BadToken);
            }
            else
            {
                token = Consume();
            }

            return CreateNode(LuaSyntaxKind.Error, [token], token.Span.Start);
        }

        private LuaSyntaxNode ConsumeErrorUntil(TokenSet terminators)
        {
            var start = Current.Span.Start;
            var children = new List<LuaSyntaxElement>();
            while (Current.Kind != LuaTokenKind.EndOfFile && !terminators.Contains(Current.Kind))
            {
                children.Add(Consume());
            }

            return CreateNodeFromList(LuaSyntaxKind.Error, children, start);
        }

        private void ValidateAttribute(LuaSyntaxToken attribute)
        {
            if (attribute.IsMissing || !_grammarFeatures.SupportsLocalAttributes)
            {
                return;
            }

            var name = _lexResult.Source.GetSpan(attribute.Span);
            if (!name.SequenceEqual("const"u8) && !name.SequenceEqual("close"u8))
            {
                AddDiagnostic(
                    "LUA2014",
                    attribute.Span,
                    "Unknown variable attribute; expected 'const' or 'close'.");
            }
        }

        private void ValidateAssignable(LuaSyntaxNode expression)
        {
            if (expression.Kind is not (
                LuaSyntaxKind.IdentifierExpression or
                LuaSyntaxKind.IndexExpression or
                LuaSyntaxKind.MemberAccessExpression))
            {
                AddDiagnostic("LUA2005", expression.Span, "Expression is not assignable.");
            }
        }

        private LuaSyntaxNode CreateNode(
            LuaSyntaxKind kind,
            ReadOnlySpan<LuaSyntaxElement> children,
            int? emptyPosition = null)
        {
            CheckNodeBudget();
            return new LuaSyntaxNode(
                kind,
                ImmutableArray.Create(children),
                emptyPosition ?? Current.Span.Start);
        }

        private LuaSyntaxNode CreateNodeFromList(
            LuaSyntaxKind kind,
            List<LuaSyntaxElement> children,
            int? emptyPosition = null)
        {
            CheckNodeBudget();
            // An exact-capacity builder plus MoveToImmutable produces one array of the
            // final size; ToImmutableArray() over an IEnumerable would re-enumerate and
            // grow a second buffer.
            var builder = ImmutableArray.CreateBuilder<LuaSyntaxElement>(children.Count);
            foreach (var child in children)
            {
                builder.Add(child);
            }

            return new LuaSyntaxNode(kind, builder.MoveToImmutable(), emptyPosition ?? Current.Span.Start);
        }

        private void CheckNodeBudget()
        {
            _nodeCount++;
            if ((_nodeCount & 0xff) == 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
            }

            if (!_nodeBudgetExceeded && _nodeCount > _options.MaximumNodeCount)
            {
                _nodeBudgetExceeded = true;
                AddDiagnostic(
                    "LUA2007",
                    Current.Span,
                    $"Syntax node count exceeds the configured {_options.MaximumNodeCount} limit.");
            }
        }

        private bool TryEnterRecursion(string construct)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (_recursionDepth >= _options.MaximumRecursionDepth)
            {
                AddDiagnostic(
                    "LUA2006",
                    Current.Span,
                    $"Parser recursion limit reached while parsing a {construct}.");
                return false;
            }

            _recursionDepth++;
            return true;
        }

        private LuaSyntaxToken Match(LuaTokenKind expected)
        {
            if (Current.Kind == expected)
            {
                return Consume();
            }

            AddExpectedDiagnostic(expected);
            return CreateMissingToken(expected);
        }

        private void AddExpectedDiagnostic(LuaTokenKind expected) =>
            AddDiagnostic(
                "LUA2001",
                Current.Span,
                $"Expected token {expected}, but found {Current.Kind}.");

        private LuaSyntaxToken CreateMissingToken(LuaTokenKind kind) => new(
            kind,
            new TextSpan(Current.Span.Start, 0),
            [])
        {
            IsMissing = true,
        };

        private LuaSyntaxToken Consume()
        {
            _consumedTokenCount++;
            if ((_consumedTokenCount & 0xff) == 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
            }

            var token = Current;
            if (_position < _lexResult.Tokens.Length - 1)
            {
                _position++;
            }

            return token;
        }

        private LuaSyntaxToken Peek(int offset)
        {
            var index = Math.Min(_position + offset, _lexResult.Tokens.Length - 1);
            return _lexResult.Tokens[index];
        }

        private LuaSyntaxToken Current => Peek(0);

        private readonly record struct TokenSet(ulong LowBits, ulong HighBits)
        {
            public static TokenSet Create(LuaTokenKind kind) => default(TokenSet).Add(kind);

            public static TokenSet Create(
                LuaTokenKind kind0,
                LuaTokenKind kind1,
                LuaTokenKind kind2) =>
                default(TokenSet).Add(kind0).Add(kind1).Add(kind2);

            public bool Contains(LuaTokenKind kind)
            {
                var value = (int)kind;
                return value switch
                {
                    >= 0 and < 64 => (LowBits & (1UL << value)) != 0,
                    >= 64 and < 128 => (HighBits & (1UL << (value - 64))) != 0,
                    _ => false,
                };
            }

            private TokenSet Add(LuaTokenKind kind)
            {
                var value = (int)kind;
                return value switch
                {
                    >= 0 and < 64 => this with { LowBits = LowBits | (1UL << value) },
                    >= 64 and < 128 => this with { HighBits = HighBits | (1UL << (value - 64)) },
                    _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Token sets support up to 128 token kinds."),
                };
            }
        }

        private void AddDiagnostic(string code, TextSpan span, string message)
        {
            if (_diagnostics.Count < _options.MaximumDiagnosticCount)
            {
                _diagnostics.Add(new Diagnostic(code, DiagnosticSeverity.Error, span, message));
            }
        }

        private static bool CanStartArguments(LuaTokenKind kind) =>
            kind is LuaTokenKind.OpenParenthesis or
                LuaTokenKind.OpenBrace or
                LuaTokenKind.StringLiteral or
                LuaTokenKind.LongStringLiteral;

        private static bool IsUnaryOperator(LuaTokenKind kind) =>
            kind is LuaTokenKind.NotKeyword or LuaTokenKind.Length or
                LuaTokenKind.Minus or LuaTokenKind.Tilde;

        private static bool IsBlockTerminator(LuaTokenKind kind) =>
            kind is LuaTokenKind.ElseIfKeyword or LuaTokenKind.ElseKeyword or
                LuaTokenKind.EndKeyword or LuaTokenKind.UntilKeyword;

        private static bool IsExpressionTerminator(LuaTokenKind kind) =>
            kind is LuaTokenKind.Comma or LuaTokenKind.Semicolon or
                LuaTokenKind.CloseParenthesis or LuaTokenKind.CloseBracket or
                LuaTokenKind.CloseBrace or LuaTokenKind.ThenKeyword or
                LuaTokenKind.DoKeyword or LuaTokenKind.InKeyword or
                LuaTokenKind.ElseIfKeyword or LuaTokenKind.ElseKeyword or
                LuaTokenKind.EndKeyword or LuaTokenKind.UntilKeyword;

        private static bool TryGetBinaryPrecedence(
            LuaTokenKind kind,
            out int left,
            out int right)
        {
            (left, right) = kind switch
            {
                LuaTokenKind.OrKeyword => (1, 1),
                LuaTokenKind.AndKeyword => (2, 2),
                LuaTokenKind.LessThan or LuaTokenKind.LessThanOrEqual or
                    LuaTokenKind.GreaterThan or LuaTokenKind.GreaterThanOrEqual or
                    LuaTokenKind.Equal or LuaTokenKind.NotEqual => (3, 3),
                LuaTokenKind.Pipe => (4, 4),
                LuaTokenKind.Tilde => (5, 5),
                LuaTokenKind.Ampersand => (6, 6),
                LuaTokenKind.ShiftLeft or LuaTokenKind.ShiftRight => (7, 7),
                LuaTokenKind.Concatenate => (9, 8),
                LuaTokenKind.Plus or LuaTokenKind.Minus => (10, 10),
                LuaTokenKind.Star or LuaTokenKind.Slash or LuaTokenKind.FloorDivide or
                    LuaTokenKind.Percent => (11, 11),
                LuaTokenKind.Caret => (14, 13),
                _ => (0, 0),
            };

            return left != 0;
        }
    }
}
