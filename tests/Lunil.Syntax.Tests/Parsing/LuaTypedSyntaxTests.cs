using Lunil.Core;
using Lunil.Core.Text;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

namespace Lunil.Syntax.Tests.Parsing;

public sealed class LuaTypedSyntaxTests
{
    [Fact]
    public void CallFacadeCoversParenthesizedAndShorthandArguments()
    {
        var source = SourceText.FromUtf8(
            "require('alpha'); require \"beta\"; consume { value = 1 }");
        var root = LuaParser.Parse(source).Root;
        var calls = CollectCalls(root);

        Assert.Equal(3, calls.Count);
        Assert.All(calls, static call => Assert.True(call.IsComplete));
        Assert.All(calls, static call => Assert.False(call.IsMethodCall));

        Assert.True(calls[0].Callee!.TryGetIdentifierToken(out var require));
        Assert.Equal(LuaTokenKind.Identifier, require.Kind);
        Assert.Equal("require", require.GetText(source));
        Assert.True(calls[0].Arguments[0].TryGetConstantString(out var alpha));
        Assert.Equal("alpha", alpha);
        Assert.True(calls[1].Arguments[0].TryGetConstantString(out var beta));
        Assert.Equal("beta", beta);
        Assert.Equal(LuaSyntaxKind.TableConstructorExpression, calls[2].Arguments[0].Kind);
    }

    [Fact]
    public void CallAndMemberFacadesDescribeColonAndDotAccess()
    {
        var root = Parse("object.child:run(1)");
        var call = Assert.Single(CollectCalls(root));

        Assert.True(call.IsMethodCall);
        var method = Assert.IsType<LuaMemberAccessExpressionSyntax>(call.Callee);
        Assert.True(method.IsColonAccess);
        Assert.False(method.MemberName!.IsMissing);
        Assert.Equal(LuaTokenKind.Identifier, method.MemberName.Kind);
        Assert.Equal(LuaSyntaxKind.MemberAccessExpression, method.Receiver!.Kind);

        Assert.True(method.Receiver.Node.TryGetMemberAccessExpression(out var member));
        Assert.False(member.IsColonAccess);
        Assert.True(member.Receiver!.TryGetIdentifierToken(out _));
        Assert.False(member.MemberName!.IsMissing);
    }

    [Fact]
    public void FunctionFacadeCoversDeclarationKindsAndOwners()
    {
        const string source = """
            function module.child:run(first, second, ...)
                return first
            end
            local function local_run(value) return value end
            local expression = function(item) return item end
            global function exported(value) return value end
            """;
        var options = LuaParserOptions.Default with { LanguageVersion = LuaLanguageVersion.Lua55 };
        var lexerOptions = LuaLexerOptions.Default with { LanguageVersion = LuaLanguageVersion.Lua55 };
        var result = LuaParser.Parse(SourceText.FromUtf8(source), lexerOptions, options);
        var functions = CollectFunctions(result.Root);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(4, functions.Count);

        var declared = functions[0];
        Assert.False(declared.IsLocal);
        Assert.False(declared.IsGlobal);
        Assert.False(declared.IsExpression);
        Assert.True(declared.HasImplicitSelf);
        Assert.Equal(3, declared.Name!.Segments.Length);
        Assert.Equal(2, declared.Parameters!.Parameters.Length);
        Assert.True(declared.Parameters.HasVarArg);
        Assert.NotNull(declared.Body);
        Assert.True(declared.IsComplete);

        Assert.True(functions[1].IsLocal);
        Assert.Single(functions[1].Name!.Segments);
        Assert.True(functions[2].IsExpression);
        Assert.Null(functions[2].Name);
        Assert.True(functions[3].IsGlobal);
        Assert.Single(functions[3].Name!.Segments);
    }

    [Fact]
    public void FacadesExposeIncompleteRecoveryWithoutThrowing()
    {
        var calls = CollectCalls(Parse("require("));
        var functions = CollectFunctions(Parse("function broken(value"));

        Assert.False(Assert.Single(calls).IsComplete);
        Assert.False(Assert.Single(functions).IsComplete);

        var missingMember = Parse("return object.").DescendantNodes().Single(static node =>
            node.Kind == LuaSyntaxKind.MemberAccessExpression);
        Assert.True(missingMember.TryGetMemberAccessExpression(out var member));
        Assert.True(member.MemberName!.IsMissing);
        Assert.False(member.IsComplete);

        var shapeLessCall = new LuaSyntaxNode(LuaSyntaxKind.CallExpression, []);
        Assert.True(shapeLessCall.TryGetCallExpression(out var call));
        Assert.Null(call.Callee);
        Assert.Empty(call.Arguments);
        Assert.False(call.IsComplete);

        var calleeOnlyCall = new LuaSyntaxNode(
            LuaSyntaxKind.CallExpression,
            [Parse("return f").DescendantNodes().Single(static node =>
                node.Kind == LuaSyntaxKind.IdentifierExpression)]);
        Assert.True(calleeOnlyCall.TryGetCallExpression(out var calleeOnly));
        Assert.NotNull(calleeOnly.Callee);
        Assert.False(calleeOnly.IsComplete);

        var shapeLessFunction = new LuaSyntaxNode(
            LuaSyntaxKind.FunctionDeclarationStatement,
            []);
        Assert.True(shapeLessFunction.TryGetFunctionDeclaration(out var function));
        Assert.Null(function.Name);
        Assert.Null(function.Parameters);
        Assert.Null(function.Body);
        Assert.False(function.IsComplete);
    }

    [Fact]
    public void ConstantStringFacadeDecodesLuaBytesOnlyWhenTheyAreValidUtf8()
    {
        var expressions = Parse("return 'line\\nvalue', [=[long]=], '\\255'")
            .DescendantNodes()
            .Where(static node => node.Kind == LuaSyntaxKind.StringLiteralExpression)
            .Select(static node =>
            {
                Assert.True(node.TryGetExpression(out var expression));
                return expression;
            })
            .ToArray();

        Assert.True(expressions[0].TryGetConstantString(out var escaped));
        Assert.Equal("line\nvalue", escaped);
        Assert.True(expressions[1].TryGetConstantString(out var longString));
        Assert.Equal("long", longString);
        Assert.False(expressions[2].TryGetConstantString(out _));
    }

    [Fact]
    public void WalkerAndVisitorDispatchTypedNodesWhilePreservingGenericTreeAccess()
    {
        var root = Parse("local function f() return api.value:read('x') end");
        var walker = new RecordingWalker();

        walker.Visit(root);

        Assert.Equal(1, walker.FunctionCount);
        Assert.Equal(1, walker.CallCount);
        Assert.Equal(1, walker.MemberCount);

        var callNode = root.DescendantNodes().Single(static node =>
            node.Kind == LuaSyntaxKind.MethodCallExpression);
        var visitor = new KindVisitor();
        Assert.Equal("call", visitor.Visit(callNode));
        Assert.Equal("node", visitor.Visit(root));
    }

    private static LuaSyntaxNode Parse(string source) =>
        LuaParser.Parse(SourceText.FromUtf8(source)).Root;

    private static List<LuaCallExpressionSyntax> CollectCalls(LuaSyntaxNode root)
    {
        var walker = new CollectingWalker();
        walker.Visit(root);
        return walker.Calls;
    }

    private static List<LuaFunctionDeclarationSyntax> CollectFunctions(LuaSyntaxNode root)
    {
        var walker = new CollectingWalker();
        walker.Visit(root);
        return walker.Functions;
    }

    private sealed class CollectingWalker : LuaSyntaxWalker
    {
        public List<LuaCallExpressionSyntax> Calls { get; } = [];

        public List<LuaFunctionDeclarationSyntax> Functions { get; } = [];

        public override void VisitCallExpression(LuaCallExpressionSyntax node)
        {
            Calls.Add(node);
            base.VisitCallExpression(node);
        }

        public override void VisitFunctionDeclaration(LuaFunctionDeclarationSyntax node)
        {
            Functions.Add(node);
            base.VisitFunctionDeclaration(node);
        }
    }

    private sealed class RecordingWalker : LuaSyntaxWalker
    {
        public int FunctionCount { get; private set; }

        public int CallCount { get; private set; }

        public int MemberCount { get; private set; }

        public override void VisitFunctionDeclaration(LuaFunctionDeclarationSyntax node)
        {
            FunctionCount++;
            base.VisitFunctionDeclaration(node);
        }

        public override void VisitCallExpression(LuaCallExpressionSyntax node)
        {
            CallCount++;
            base.VisitCallExpression(node);
        }

        public override void VisitMemberAccessExpression(LuaMemberAccessExpressionSyntax node)
        {
            MemberCount++;
            base.VisitMemberAccessExpression(node);
        }
    }

    private sealed class KindVisitor : LuaSyntaxVisitor<string>
    {
        public override string VisitCallExpression(LuaCallExpressionSyntax node) => "call";

        public override string DefaultVisit(LuaSyntaxNode node) => "node";
    }
}
