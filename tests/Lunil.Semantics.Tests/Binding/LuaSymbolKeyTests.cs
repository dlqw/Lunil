using Lunil.Core.Text;
using Lunil.Semantics.Binding;
using Lunil.Syntax.Parsing;

namespace Lunil.Semantics.Tests.Binding;

public sealed class LuaSymbolKeyTests
{
    [Fact]
    public void WhitespaceCommentsAndUnrelatedDeclarationsPreserveKeys()
    {
        var original = Bind("""
            local stable = 1
            function outer(argument)
                local inner = argument
                return inner
            end
            """);
        var edited = Bind("""
            -- inserted documentation
            local unrelated = 0
            local stable = 1
            function outer(argument)
                -- body comment
                local inner = argument
                return inner
            end
            """);

        foreach (var name in new[] { "stable", "argument", "inner" })
        {
            var before = FindSymbol(original, name);
            var after = FindSymbol(edited, name);
            Assert.Equal(
                original.GetSymbolKey(before, "game/main"),
                edited.GetSymbolKey(after, "game/main"));
        }

        var originalFunction = original.Functions.Single(function => function.Id == 1);
        var editedFunction = edited.Functions.Single(function => function.Id == 1);
        Assert.Equal(
            original.GetFunctionKey(originalFunction, "game/main"),
            edited.GetFunctionKey(editedFunction, "game/main"));
    }

    [Fact]
    public void ShadowedLocalsAndNestedFunctionOwnersRemainDistinct()
    {
        var model = Bind("""
            do local value = 1 end
            do local value = 2 end
            local function outer()
                local function inner() return 1 end
            end
            """);

        var values = model.Symbols.Where(symbol => symbol.Name == "value").ToArray();
        Assert.Equal(2, values.Length);
        Assert.NotEqual(
            model.GetSymbolKey(values[0], "game/main"),
            model.GetSymbolKey(values[1], "game/main"));

        var inner = model.Functions.Single(function => function.Id == 2);
        var moved = Bind("local function other() local function inner() return 1 end end");
        var movedInner = moved.Functions.Single(function => function.Id == 2);
        Assert.NotEqual(
            model.GetFunctionKey(inner, "game/main"),
            moved.GetFunctionKey(movedInner, "game/main"));
    }

    [Fact]
    public void FunctionDeclarationKindsAndModulesParticipateInIdentity()
    {
        var model = Bind("function same() end; local function same() end");
        var functions = model.Functions.Where(function => function.Id != 0).ToArray();

        Assert.Equal(2, functions.Length);
        Assert.NotEqual(
            model.GetFunctionKey(functions[0], "game/main"),
            model.GetFunctionKey(functions[1], "game/main"));
        Assert.NotEqual(
            model.GetFunctionKey(functions[0], "game/main"),
            model.GetFunctionKey(functions[0], "game/other"));
    }

    [Fact]
    public void SerializedKeysResolveOnlyInTheMatchingModuleSnapshot()
    {
        var model = Bind("local value = 1");
        var symbol = FindSymbol(model, "value");
        var key = model.GetSymbolKey(symbol, "game/main");
        var serialized = new LuaSymbolKey(key.Value);

        Assert.True(LuaSymbolKey.TryParse(serialized.Value, out var parsed));
        Assert.Equal(key, parsed);
        Assert.Same(symbol, model.ResolveSymbolKey(parsed, "game/main"));
        Assert.Null(model.ResolveSymbolKey(parsed, "other/main"));
        Assert.Null(model.ResolveFunctionKey(parsed, "game/main"));
        Assert.False(LuaSymbolKey.TryParse("lunil-symbol-v1|invalid", out _));
        Assert.False(LuaSymbolKey.TryParse(
            "lunil-symbol-v1|symbol|game%2fmain|Local|main%2Fscope%3A0|value|0",
            out _));
        Assert.False(LuaSymbolKey.TryParse(
            "lunil-symbol-v1|annotation|game%2Fmain|field||value|0",
            out _));
        Assert.False(LuaSymbolKey.TryParse(
            "lunil-symbol-v1|symbol|game%2Fmain|Local|main%2Fscope%3A0|value|00",
            out _));
        Assert.Throws<ArgumentException>(() => new LuaSymbolKey("lunil-symbol-v1|invalid"));
        Assert.Equal(string.Empty, default(LuaSymbolKey).ToString());
    }

    [Fact]
    public void AnnotationKeysDistinguishDeclarationClassesAndAreSerializable()
    {
        var @class = LuaSymbolKey.CreateAnnotation("game/main", "class", "Player");
        var alias = LuaSymbolKey.CreateAnnotation("game/main", "alias", "Player");
        var edited = LuaSymbolKey.CreateAnnotation("game/main", "class", "Player");

        Assert.NotEqual(@class, alias);
        Assert.Equal(@class, edited);
        Assert.True(LuaSymbolKey.TryParse(@class.Value, out _));
    }

    private static LuaSemanticModel Bind(string source) =>
        LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source)));

    private static LuaSymbol FindSymbol(LuaSemanticModel model, string name) =>
        Assert.Single(model.Symbols, symbol => symbol.Name == name);
}
