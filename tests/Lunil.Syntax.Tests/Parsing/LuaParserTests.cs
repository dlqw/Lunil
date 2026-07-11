using Lunil.Core.Text;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

namespace Lunil.Syntax.Tests.Parsing;

public sealed class LuaParserTests
{
    [Fact]
    public void ParsesCompleteLua54StatementAndExpressionGrammar()
    {
        const string sourceText = """
            ;
            ::start::
            local constant <const>, closer <close> = 1, make()
            local x = constant
            local function f(a, b, ...)
                if ... then
                    return { [a] = b, name = "x"; a + b }
                elseif false then
                    return nil
                else
                    return function() end
                end
            end
            function module.sub:method(value)
                ::again::
                do x = x + value end
                while x < 10 do x = x + 1; break end
                repeat x = x - 1 until x == 0
                for i = 1, 10, 2 do print(i) end
                for k, v in pairs({ 1, 2 }) do print(k, v) end
                goto again
            end
            return f(...)
            """;

        var result = LuaParser.Parse(SourceText.FromUtf8(sourceText));

        Assert.Empty(result.Diagnostics);
        Assert.Equal(LuaSyntaxKind.CompilationUnit, result.Root.Kind);
        Assert.Contains(
            result.Root.DescendantNodes(),
            node => node.Kind == LuaSyntaxKind.NumericForStatement);
        Assert.Contains(
            result.Root.DescendantNodes(),
            node => node.Kind == LuaSyntaxKind.GenericForStatement);
        AssertNoMissingTokens(result.Root);
    }

    [Fact]
    public void TreeContainsEveryRealLexerTokenExactlyOnce()
    {
        var source = SourceText.FromUtf8(" -- lead\nlocal x = {a = 1, [2] = 3}; return x -- tail");
        var lex = LuaLexer.Lex(source);

        var result = LuaParser.Parse(lex);
        var parsedTokens = result.Root.DescendantTokens()
            .Where(static token => !token.IsMissing)
            .ToArray();

        Assert.Empty(result.Diagnostics);
        Assert.Equal(lex.Tokens, parsedTokens);
        Assert.Equal(new TextSpan(0, source.Length), result.Root.FullSpan);
    }

    [Fact]
    public void BinaryOperatorsFollowPucLua54Precedence()
    {
        var result = LuaParser.Parse(SourceText.FromUtf8(
            "return 1 + 2 * 3 ^ 4 .. 5 and 6 or 7"));

        Assert.Empty(result.Diagnostics);
        var expression = GetReturnedExpression(result.Root);
        AssertBinaryOperator(expression, LuaTokenKind.OrKeyword);

        var andExpression = AssertNode(expression.Children[0], LuaSyntaxKind.BinaryExpression);
        AssertBinaryOperator(andExpression, LuaTokenKind.AndKeyword);

        var concatenate = AssertNode(andExpression.Children[0], LuaSyntaxKind.BinaryExpression);
        AssertBinaryOperator(concatenate, LuaTokenKind.Concatenate);

        var addition = AssertNode(concatenate.Children[0], LuaSyntaxKind.BinaryExpression);
        AssertBinaryOperator(addition, LuaTokenKind.Plus);

        var multiplication = AssertNode(addition.Children[2], LuaSyntaxKind.BinaryExpression);
        AssertBinaryOperator(multiplication, LuaTokenKind.Star);

        var power = AssertNode(multiplication.Children[2], LuaSyntaxKind.BinaryExpression);
        AssertBinaryOperator(power, LuaTokenKind.Caret);
    }

    [Fact]
    public void ConcatenationAndPowerAreRightAssociative()
    {
        var concatenation = LuaParser.Parse(SourceText.FromUtf8("return 1 .. 2 .. 3"));
        var power = LuaParser.Parse(SourceText.FromUtf8("return 2 ^ 3 ^ 4"));

        var concatRoot = GetReturnedExpression(concatenation.Root);
        AssertBinaryOperator(concatRoot, LuaTokenKind.Concatenate);
        AssertBinaryOperator(
            AssertNode(concatRoot.Children[2], LuaSyntaxKind.BinaryExpression),
            LuaTokenKind.Concatenate);

        var powerRoot = GetReturnedExpression(power.Root);
        AssertBinaryOperator(powerRoot, LuaTokenKind.Caret);
        AssertBinaryOperator(
            AssertNode(powerRoot.Children[2], LuaSyntaxKind.BinaryExpression),
            LuaTokenKind.Caret);
    }

    [Fact]
    public void PowerBindsMoreTightlyThanUnaryMinusOnTheLeft()
    {
        var result = LuaParser.Parse(SourceText.FromUtf8("return -2 ^ 2, 2 ^ -3"));

        Assert.Empty(result.Diagnostics);
        var expressionList = GetReturnStatement(result.Root).ChildNodes().Single();
        var expressions = expressionList.ChildNodes().ToArray();

        Assert.Equal(LuaSyntaxKind.UnaryExpression, expressions[0].Kind);
        Assert.Equal(LuaSyntaxKind.BinaryExpression, expressions[0].ChildNodes().Single().Kind);
        Assert.Equal(LuaSyntaxKind.BinaryExpression, expressions[1].Kind);
        Assert.Equal(LuaSyntaxKind.UnaryExpression, expressions[1].ChildNodes().Last().Kind);
    }

    [Fact]
    public void ParsesChainedPrefixAndCallSuffixes()
    {
        var result = LuaParser.Parse(SourceText.FromUtf8("object.field[1]:method 'x' { y = 1 }()"));

        Assert.Empty(result.Diagnostics);
        var statement = result.Root.ChildNodes().Single().ChildNodes().Single();
        Assert.Equal(LuaSyntaxKind.CallStatement, statement.Kind);
        Assert.Equal(LuaSyntaxKind.CallExpression, statement.ChildNodes().Single().Kind);
    }

    [Fact]
    public void InvalidAssignmentTargetProducesDedicatedDiagnostic()
    {
        var result = LuaParser.Parse(SourceText.FromUtf8("f() = 1"));

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "LUA2005");
        Assert.Equal(
            LuaSyntaxKind.AssignmentStatement,
            result.Root.ChildNodes().Single().ChildNodes().Single().Kind);
    }

    [Fact]
    public void MissingTokensAreSynthesizedWithoutLosingSourceTokens()
    {
        var source = SourceText.FromUtf8("local = 1\nif then return, ");
        var lex = LuaLexer.Lex(source);

        var result = LuaParser.Parse(lex);
        var parsedRealTokens = result.Root.DescendantTokens()
            .Where(static token => !token.IsMissing)
            .ToArray();

        Assert.NotEmpty(result.Diagnostics);
        Assert.Contains(result.Root.DescendantTokens(), static token => token.IsMissing);
        Assert.Equal(lex.Tokens, parsedRealTokens);
    }

    [Fact]
    public void RecursionBudgetStopsDeeplyNestedExpressions()
    {
        var source = new string('(', 300) + "1" + new string(')', 300);
        var options = LuaParserOptions.Default with
        {
            MaximumRecursionDepth = 20,
            MaximumDiagnosticCount = 100,
        };

        var result = LuaParser.Parse(SourceText.FromUtf8(source), parserOptions: options);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "LUA2006");
        Assert.Equal(LuaTokenKind.EndOfFile, result.Root.DescendantTokens().Last().Kind);
    }

    [Fact]
    public void NodeBudgetCollapsesRemainingInputIntoLosslessErrorNode()
    {
        var source = string.Join(';', Enumerable.Repeat("a = 1", 100));
        var options = LuaParserOptions.Default with { MaximumNodeCount = 10 };

        var result = LuaParser.Parse(SourceText.FromUtf8(source), parserOptions: options);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "LUA2007");
        Assert.Equal(LuaTokenKind.EndOfFile, result.Root.DescendantTokens().Last().Kind);
    }

    private static LuaSyntaxNode GetReturnedExpression(LuaSyntaxNode root)
    {
        var expressionList = GetReturnStatement(root).ChildNodes().Single();
        return expressionList.ChildNodes().Single();
    }

    private static LuaSyntaxNode GetReturnStatement(LuaSyntaxNode root) =>
        root.ChildNodes().Single().ChildNodes().Single();

    private static LuaSyntaxNode AssertNode(
        LuaSyntaxElement element,
        LuaSyntaxKind expectedKind)
    {
        var node = Assert.IsType<LuaSyntaxNode>(element.Node);
        Assert.Equal(expectedKind, node.Kind);
        return node;
    }

    private static void AssertBinaryOperator(LuaSyntaxNode node, LuaTokenKind expected)
    {
        Assert.Equal(LuaSyntaxKind.BinaryExpression, node.Kind);
        Assert.Equal(expected, node.Children[1].Token?.Kind);
    }

    private static void AssertNoMissingTokens(LuaSyntaxNode root) =>
        Assert.DoesNotContain(root.DescendantTokens(), static token => token.IsMissing);
}
