using System.Collections.Immutable;
using Lunil.Core;
using Lunil.Core.Diagnostics;
using Lunil.Core.Text;
using Lunil.Syntax.Lexing;

namespace Lunil.Syntax.Parsing;

/// <summary>A lossless, error-tolerant parser for the complete Lua 5.4 grammar.</summary>
public static class LuaParser
{
    public static LuaParseResult Parse(
        SourceText source,
        LuaLexerOptions? lexerOptions = null,
        LuaParserOptions? parserOptions = null)
    {
        parserOptions ??= LuaParserOptions.Default with
        {
            LanguageVersion = lexerOptions?.LanguageVersion ?? LuaLanguageVersions.Default,
        };
        lexerOptions ??= LuaLexerOptions.Default with
        {
            LanguageVersion = parserOptions.LanguageVersion,
        };
        return Parse(LuaLexer.Lex(source, lexerOptions), parserOptions);
    }

    public static LuaParseResult Parse(
        LuaLexResult lexResult,
        LuaParserOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(lexResult);
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

        return new Implementation(lexResult, options).Parse();
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

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumRecursionDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumNodeCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumDiagnosticCount);
    }

    private sealed class Implementation
    {
        private const int MaximumLocalsPerDeclaration = 200;

        private readonly LuaLexResult _lexResult;
        private readonly LuaParserOptions _options;
        private readonly ImmutableArray<Diagnostic>.Builder _diagnostics;
        private readonly Stack<int> _functionStartPositions = [];
        private int _position;
        private int _recursionDepth;
        private int _nodeCount;
        private bool _nodeBudgetExceeded;

        public Implementation(LuaLexResult lexResult, LuaParserOptions options)
        {
            _lexResult = lexResult;
            _options = options;
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
            var root = CreateNode(LuaSyntaxKind.CompilationUnit, children, 0);
            return new LuaParseResult(_lexResult.Source, root, _diagnostics.ToImmutable())
            {
                LanguageVersion = _options.LanguageVersion,
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

                return CreateNode(LuaSyntaxKind.Block, statements, start);
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
            if (_options.LanguageVersion != LuaLanguageVersion.Lua55)
            {
                AddDiagnostic("LUA2013", children[0].Token!.Span,
                    "Global declarations are only available in Lua 5.5.");
            }

            while (Current.Kind is LuaTokenKind.Identifier or LuaTokenKind.Comma or
                LuaTokenKind.LessThan or LuaTokenKind.GreaterThan)
            {
                children.Add(Consume());
            }

            return CreateNode(LuaSyntaxKind.GlobalDeclarationStatement, children, start);
        }

        private LuaSyntaxNode ParseLabelStatement()
        {
            if (_options.LanguageVersion == LuaLanguageVersion.Lua51)
            {
                AddDiagnostic("LUA2011", Current.Span, "Labels are not available in Lua 5.1.");
            }

            var children = new List<LuaSyntaxElement>
            {
                Consume(),
                Match(LuaTokenKind.Identifier),
                Match(LuaTokenKind.DoubleColon),
            };
            return CreateNode(LuaSyntaxKind.LabelStatement, children);
        }

        private LuaSyntaxNode ParseGotoStatement()
        {
            if (_options.LanguageVersion == LuaLanguageVersion.Lua51)
            {
                AddDiagnostic("LUA2011", Current.Span, "goto is not available in Lua 5.1.");
            }

            var children = new List<LuaSyntaxElement>
            {
                Consume(),
                Match(LuaTokenKind.Identifier),
            };
            return CreateNode(LuaSyntaxKind.GotoStatement, children);
        }

        private LuaSyntaxNode ParseDoStatement()
        {
            var children = new List<LuaSyntaxElement>
            {
                Consume(),
                ParseBlock(LuaTokenKind.EndKeyword),
                Match(LuaTokenKind.EndKeyword),
            };
            return CreateNode(LuaSyntaxKind.DoStatement, children);
        }

        private LuaSyntaxNode ParseWhileStatement()
        {
            var children = new List<LuaSyntaxElement>
            {
                Consume(),
                ParseExpression(),
                Match(LuaTokenKind.DoKeyword),
                ParseBlock(LuaTokenKind.EndKeyword),
                Match(LuaTokenKind.EndKeyword),
            };
            return CreateNode(LuaSyntaxKind.WhileStatement, children);
        }

        private LuaSyntaxNode ParseRepeatStatement()
        {
            var children = new List<LuaSyntaxElement>
            {
                Consume(),
                ParseBlock(LuaTokenKind.UntilKeyword),
                Match(LuaTokenKind.UntilKeyword),
                ParseExpression(),
            };
            return CreateNode(LuaSyntaxKind.RepeatStatement, children);
        }

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
                var clause = new List<LuaSyntaxElement>
                {
                    Consume(),
                    ParseExpression(),
                    Match(LuaTokenKind.ThenKeyword),
                    ParseBlock(
                        LuaTokenKind.ElseIfKeyword,
                        LuaTokenKind.ElseKeyword,
                        LuaTokenKind.EndKeyword),
                };
                children.Add(CreateNode(LuaSyntaxKind.ElseIfClause, clause));
            }

            if (Current.Kind == LuaTokenKind.ElseKeyword)
            {
                var clause = new List<LuaSyntaxElement>
                {
                    Consume(),
                    ParseBlock(LuaTokenKind.EndKeyword),
                };
                children.Add(CreateNode(LuaSyntaxKind.ElseClause, clause));
            }

            children.Add(Match(LuaTokenKind.EndKeyword));
            return CreateNode(LuaSyntaxKind.IfStatement, children);
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
            return CreateNode(LuaSyntaxKind.NumericForStatement, children);
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

            var children = new List<LuaSyntaxElement>
            {
                forKeyword,
                CreateNode(LuaSyntaxKind.NameList, names),
                Match(LuaTokenKind.InKeyword),
                ParseExpressionList(),
                Match(LuaTokenKind.DoKeyword),
                ParseBlock(LuaTokenKind.EndKeyword),
                Match(LuaTokenKind.EndKeyword),
            };
            return CreateNode(LuaSyntaxKind.GenericForStatement, children);
        }

        private LuaSyntaxNode ParseFunctionDeclarationStatement()
        {
            var functionKeyword = Consume();
            var children = new List<LuaSyntaxElement>
            {
                functionKeyword,
                ParseFunctionName(),
                ParseFunctionBody(functionKeyword.Span.Start),
            };
            return CreateNode(LuaSyntaxKind.FunctionDeclarationStatement, children);
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

            return CreateNode(LuaSyntaxKind.FunctionName, children);
        }

        private LuaSyntaxNode ParseLocalStatement()
        {
            var localKeyword = Consume();
            if (Current.Kind == LuaTokenKind.FunctionKeyword)
            {
                var functionKeyword = Consume();
                var functionChildren = new List<LuaSyntaxElement>
                {
                    localKeyword,
                    functionKeyword,
                    Match(LuaTokenKind.Identifier),
                    ParseFunctionBody(functionKeyword.Span.Start),
                };
                return CreateNode(
                    LuaSyntaxKind.LocalFunctionDeclarationStatement,
                    functionChildren);
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

            return CreateNode(LuaSyntaxKind.LocalDeclarationStatement, children);
        }

        private LuaSyntaxNode ParseAttributedName()
        {
            var children = new List<LuaSyntaxElement>
            {
                Match(LuaTokenKind.Identifier),
            };

            if (Current.Kind == LuaTokenKind.LessThan)
            {
                var attributeStart = Current.Span.Start;
                children.Add(Consume());
                children.Add(Match(LuaTokenKind.Identifier));
                children.Add(Match(LuaTokenKind.GreaterThan));
                if (_options.LanguageVersion != LuaLanguageVersion.Lua54)
                {
                    AddDiagnostic(
                        "LUA2010",
                        TextSpan.FromBounds(attributeStart, Current.Span.Start),
                        "Local attributes are only available in Lua 5.4.");
                }
            }

            return CreateNode(LuaSyntaxKind.AttributedName, children);
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

            return CreateNode(LuaSyntaxKind.ReturnStatement, children);
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

                var children = new List<LuaSyntaxElement>
                {
                    CreateNode(LuaSyntaxKind.VariableList, variables),
                    Match(LuaTokenKind.Assign),
                    ParseExpressionList(),
                };
                return CreateNode(LuaSyntaxKind.AssignmentStatement, children);
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

            return CreateNode(LuaSyntaxKind.ExpressionList, children, start);
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
                    var unaryChildren = new List<LuaSyntaxElement>
                    {
                        Consume(),
                        ParseExpression(12),
                    };
                    if (_options.LanguageVersion is LuaLanguageVersion.Lua51 or LuaLanguageVersion.Lua52 &&
                        unaryToken.Kind == LuaTokenKind.Tilde)
                    {
                        AddDiagnostic(
                            "LUA2012",
                            unaryToken.Span,
                            "The bitwise-not operator is not available in the selected Lua version.");
                    }

                    left = CreateNode(LuaSyntaxKind.UnaryExpression, unaryChildren);
                }
                else
                {
                    left = ParsePrimaryExpression();
                }

                while (TryGetBinaryPrecedence(Current.Kind, out var leftPrecedence, out var rightPrecedence) &&
                       leftPrecedence > minimumPrecedence)
                {
                    var operatorToken = Current;
                    var binaryChildren = new List<LuaSyntaxElement>
                    {
                        left,
                        Consume(),
                        ParseExpression(rightPrecedence),
                    };
                    if (_options.LanguageVersion is LuaLanguageVersion.Lua51 or LuaLanguageVersion.Lua52 &&
                        operatorToken.Kind is LuaTokenKind.FloorDivide or LuaTokenKind.Ampersand or
                        LuaTokenKind.Pipe or LuaTokenKind.ShiftLeft or LuaTokenKind.ShiftRight or
                        LuaTokenKind.Tilde)
                    {
                        AddDiagnostic(
                            "LUA2012",
                            operatorToken.Span,
                            "This operator is not available in the selected Lua version.");
                    }

                    left = CreateNode(LuaSyntaxKind.BinaryExpression, binaryChildren);
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
            LuaTokenKind.NumericLiteral => CreateNode(
                LuaSyntaxKind.NumericLiteralExpression,
                [Consume()]),
            LuaTokenKind.StringLiteral or LuaTokenKind.LongStringLiteral => CreateNode(
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

        private LuaSyntaxNode ParseFunctionExpression()
        {
            var functionKeyword = Consume();
            var children = new List<LuaSyntaxElement>
            {
                functionKeyword,
                ParseFunctionBody(functionKeyword.Span.Start),
            };
            return CreateNode(LuaSyntaxKind.FunctionExpression, children);
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
                var children = new List<LuaSyntaxElement>
                {
                    Consume(),
                    ParseExpression(),
                    Match(LuaTokenKind.CloseParenthesis),
                };
                expression = CreateNode(LuaSyntaxKind.ParenthesizedExpression, children);
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

            return CreateNode(LuaSyntaxKind.ArgumentList, children, start);
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
            return CreateNode(LuaSyntaxKind.TableConstructorExpression, children);
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

            return CreateNode(LuaSyntaxKind.TableField, children);
        }

        private LuaSyntaxNode ParseFunctionBody(int functionStartPosition)
        {
            _functionStartPositions.Push(functionStartPosition);
            try
            {
                var children = new List<LuaSyntaxElement>
                {
                    Match(LuaTokenKind.OpenParenthesis),
                    ParseParameterList(),
                    Match(LuaTokenKind.CloseParenthesis),
                    ParseBlock(LuaTokenKind.EndKeyword),
                    Match(LuaTokenKind.EndKeyword),
                };
                return CreateNode(LuaSyntaxKind.FunctionBody, children);
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
                        break;
                    }

                    children.Add(Match(LuaTokenKind.Identifier));
                }
            }

            return CreateNode(LuaSyntaxKind.ParameterList, children, start);
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

            return CreateNode(LuaSyntaxKind.Error, children, start);
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
            IEnumerable<LuaSyntaxElement> children,
            int? emptyPosition = null)
        {
            _nodeCount++;
            if (!_nodeBudgetExceeded && _nodeCount > _options.MaximumNodeCount)
            {
                _nodeBudgetExceeded = true;
                AddDiagnostic(
                    "LUA2007",
                    Current.Span,
                    $"Syntax node count exceeds the configured {_options.MaximumNodeCount} limit.");
            }

            return new LuaSyntaxNode(kind, children, emptyPosition ?? Current.Span.Start);
        }

        private bool TryEnterRecursion(string construct)
        {
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

        private readonly record struct TokenSet(UInt128 Bits)
        {
            public static TokenSet Create(LuaTokenKind kind) => new(Bit(kind));

            public static TokenSet Create(
                LuaTokenKind kind0,
                LuaTokenKind kind1,
                LuaTokenKind kind2) =>
                new(Bit(kind0) | Bit(kind1) | Bit(kind2));

            public bool Contains(LuaTokenKind kind) => (Bits & Bit(kind)) != 0;

            private static UInt128 Bit(LuaTokenKind kind) => UInt128.One << (int)kind;
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
