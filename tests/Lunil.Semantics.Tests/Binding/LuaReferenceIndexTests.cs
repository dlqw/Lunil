using Lunil.Core;
using Lunil.Core.Text;
using Lunil.Semantics.Binding;
using Lunil.Syntax.Parsing;

namespace Lunil.Semantics.Tests.Binding;

public sealed class LuaReferenceIndexTests
{
    [Fact]
    public void FindsReferencesByBoundSymbolWithoutMixingShadowedLocals()
    {
        var model = Bind("""
            local value = 1
            do
                local value = 2
                print(value)
            end
            return value
            """);
        var values = model.Symbols
            .Where(symbol => symbol.Name == "value")
            .OrderBy(symbol => symbol.DeclaringSpan.Start)
            .ToArray();

        Assert.Equal(2, values.Length);
        var outerReferences = model.FindReferences(values[0]);
        var innerReferences = model.FindReferences(values[1]);
        Assert.Single(outerReferences);
        Assert.Single(innerReferences);
        Assert.NotEqual(outerReferences[0].Span, innerReferences[0].Span);
        Assert.Same(values[0], outerReferences[0].Symbol);
        Assert.Same(values[1], innerReferences[0].Symbol);
    }

    [Fact]
    public void FindsImplicitEnvironmentGlobalsByNameAndRejectsForeignSymbols()
    {
        var model = Bind("missing = missing or 1");
        var references = model.FindGlobalReferences("missing");

        Assert.Equal(2, references.Length);
        Assert.Contains(references, static reference => reference.IsWrite);
        Assert.Contains(references, static reference => !reference.IsWrite);
        Assert.All(references, static reference =>
            Assert.Equal(LuaNameResolutionKind.Global, reference.ResolutionKind));

        var foreign = Bind("local foreign = 1").Symbols.Single(symbol => symbol.Name == "foreign");
        Assert.Throws<ArgumentException>(() => model.FindReferences(foreign));
        Assert.Throws<ArgumentException>(() => model.FindGlobalReferences(" "));
    }

    [Fact]
    public void ExplicitGlobalSymbolsUseTheGlobalNameIndex()
    {
        var options = LuaBinderOptions.Default with
        {
            LanguageVersion = LuaLanguageVersion.Lua55,
        };
        var model = Bind("global value; value = 1; return value", options);
        var symbol = Assert.Single(model.Symbols, candidate => candidate.Kind == LuaSymbolKind.Global);
        var references = model.FindReferences(symbol);

        Assert.Equal(3, references.Length);
        Assert.Equal(2, references.Count(static reference => reference.IsWrite));
        Assert.All(references, static reference =>
            Assert.Equal(LuaNameResolutionKind.Global, reference.ResolutionKind));
    }

    private static LuaSemanticModel Bind(string source, LuaBinderOptions? options = null) =>
        LuaBinder.Bind(
            LuaParser.Parse(
                SourceText.FromUtf8(source),
                lexerOptions: null,
                parserOptions: new LuaParserOptions
                {
                    LanguageVersion = options?.LanguageVersion ?? LuaLanguageVersions.Default,
                }),
            options);
}
