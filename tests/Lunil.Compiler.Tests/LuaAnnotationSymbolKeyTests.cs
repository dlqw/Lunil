using Lunil.EmmyLua;

namespace Lunil.Compiler.Tests;

public sealed class LuaAnnotationSymbolKeyTests
{
    [Fact]
    public void NamedAnnotationKeysSurviveUnrelatedEditsAndResolve()
    {
        var original = Compile("""
            ---@class Player
            ---@alias Player string
            ---@enum Player: string
            return 1
            """);
        var edited = Compile("""
            -- unrelated documentation
            ---@alias Other integer
            ---@class Player
            ---@alias Player string
            ---@enum Player: string
            return 1
            """);

        var originalDeclarations = NamedDeclarations(original);
        var editedDeclarations = NamedDeclarations(edited)
            .Where(static annotation => GetName(annotation) == "Player")
            .ToArray();

        Assert.Equal(3, originalDeclarations.Length);
        Assert.Equal(3, editedDeclarations.Length);
        for (var index = 0; index < originalDeclarations.Length; index++)
        {
            var originalKey = original.GetAnnotationKey(originalDeclarations[index], "game/main");
            var editedKey = edited.GetAnnotationKey(editedDeclarations[index], "game/main");

            Assert.Equal(originalKey, editedKey);
            Assert.Same(
                editedDeclarations[index],
                edited.ResolveAnnotationKey(originalKey, "game/main"));
        }

        Assert.Equal(
            3,
            originalDeclarations
                .Select(annotation => original.GetAnnotationKey(annotation, "game/main"))
                .Distinct()
                .Count());
    }

    [Fact]
    public void AnnotationKeysRejectUnsupportedAndForeignNodes()
    {
        var compilation = Compile("---@type string\nlocal value");
        var unsupported = Assert.Single(compilation.Annotations.Annotations);
        Assert.Throws<ArgumentException>(() =>
            compilation.GetAnnotationKey(unsupported, "game/main"));

        var foreignCompilation = Compile("---@class Foreign\nlocal value");
        var foreign = Assert.Single(foreignCompilation.Annotations.Annotations);
        Assert.Throws<ArgumentException>(() =>
            compilation.GetAnnotationKey(foreign, "game/main"));
    }

    private static LuaCompilationResult Compile(string source) =>
        new LuaCompiler().CompileUtf8(source, "game/main.lua");

    private static LuaAnnotationSyntax[] NamedDeclarations(LuaCompilationResult compilation) =>
        compilation.Annotations.Annotations
            .Where(static annotation => annotation is
                LuaClassAnnotationSyntax or
                LuaAliasAnnotationSyntax or
                LuaEnumAnnotationSyntax)
            .ToArray();

    private static string GetName(LuaAnnotationSyntax annotation) => annotation switch
    {
        LuaClassAnnotationSyntax @class => @class.Name,
        LuaAliasAnnotationSyntax alias => alias.Name,
        LuaEnumAnnotationSyntax @enum => @enum.Name,
        _ => throw new ArgumentOutOfRangeException(nameof(annotation)),
    };
}
