using Lunil.Core;
using Lunil.Core.Text;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

namespace Lunil.Syntax.Tests.Parsing;

public sealed class LuaParserRecoveryAndCorpusTests
{
    public static IEnumerable<object[]> Versions() =>
        Enum.GetValues<LuaLanguageVersion>().Select(static version => new object[] { version });

    [Theory]
    [MemberData(nameof(Versions))]
    public void CommonSuffixTableLongBracketAndLiteralCorpusIsLossless(
        LuaLanguageVersion version)
    {
        const string Source = """
            local object = { [1] = 0xff, name = "line\n", [ [==[key]==] ] = 1e-3; 42 }
            return object.name[1]:method("x") { nested = { true, false, nil } }()
            """;
        var lexing = LuaLexer.Lex(
            SourceText.FromUtf8(Source),
            LuaLexerOptions.Default with { LanguageVersion = version });

        var parsing = LuaParser.Parse(
            lexing,
            LuaParserOptions.Default with { LanguageVersion = version });

        Assert.Empty(parsing.Diagnostics);
        Assert.Equal(
            lexing.Tokens,
            parsing.Root.DescendantTokens().Where(static token => !token.IsMissing));
        Assert.Contains(parsing.Root.DescendantNodes(), static node =>
            node.Kind == LuaSyntaxKind.MethodCallExpression);
        Assert.Contains(parsing.Root.DescendantNodes(), static node =>
            node.Kind == LuaSyntaxKind.TableConstructorExpression);
    }

    [Fact]
    public void RecoveryDiagnosticsAndTokenProjectionAreDeterministic()
    {
        var source = SourceText.FromUtf8(
            "local < = {],,,\nif ( then goto end\nreturn function(...,) { [ = } end");

        var first = LuaParser.Parse(source);
        var second = LuaParser.Parse(source);

        Assert.Equal(
            first.Diagnostics.Select(static diagnostic =>
                (diagnostic.Code, diagnostic.Severity, diagnostic.Span, diagnostic.Message)),
            second.Diagnostics.Select(static diagnostic =>
                (diagnostic.Code, diagnostic.Severity, diagnostic.Span, diagnostic.Message)));
        Assert.Equal(
            first.Root.DescendantTokens().Select(static token =>
                (token.Kind, token.Span, token.IsMissing)),
            second.Root.DescendantTokens().Select(static token =>
                (token.Kind, token.Span, token.IsMissing)));
        Assert.Equal(LuaTokenKind.EndOfFile, first.Root.DescendantTokens().Last().Kind);
    }

    [Fact]
    public void VeryLongSuffixChainUsesIterativeProgress()
    {
        var source = "return root" + string.Concat(Enumerable.Repeat(".field[1]", 10_000));

        var result = LuaParser.Parse(SourceText.FromUtf8(source));

        Assert.Empty(result.Diagnostics);
        Assert.Equal(LuaSyntaxKind.CompilationUnit, result.Root.Kind);
    }

    [Fact]
    public void CancellationIsObservedBeforeParserWork()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => LuaParser.Parse(
            SourceText.FromUtf8("return 1"),
            cancellationToken: cancellation.Token));
    }

    [Fact]
    public void RecursionConfigurationCannotDisableTheHardSafetyBoundary()
    {
        var options = LuaParserOptions.Default with
        {
            MaximumRecursionDepth = LuaParserOptions.MaximumSupportedRecursionDepth + 1,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => LuaParser.Parse(
            SourceText.FromUtf8("return 1"),
            parserOptions: options));
    }

    [Fact]
    public void UnknownLua54AttributeHasDedicatedDiagnostic()
    {
        var result = LuaParser.Parse(SourceText.FromUtf8("local value <vendor> = 1"));

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "LUA2014");
    }
}
