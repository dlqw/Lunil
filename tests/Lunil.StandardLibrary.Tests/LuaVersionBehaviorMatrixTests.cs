using System.Text;
using Lunil.Core;
using Lunil.Core.Text;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.StandardLibrary.Tests;

/// <summary>
/// Version-aware standard-library and error behavior matrix (not just API surface presence).
/// </summary>
public sealed class LuaVersionBehaviorMatrixTests
{
    public static IEnumerable<object[]> Versions() =>
        Enum.GetValues<LuaLanguageVersion>().Select(version => new object[] { version });

    [Theory]
    [MemberData(nameof(Versions))]
    public void ErrorArgumentMessagesUseOneBasedIndexes(LuaLanguageVersion version)
    {
        var state = CreateState(version);
        var ex = Assert.ThrowsAny<Exception>(() => Execute(state, "return select()"));
        Assert.Contains("bad argument", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void CoroutineCloseSurfaceMatchesProfile(LuaLanguageVersion version)
    {
        var state = CreateState(version);
        var features = LuaVersionFeatureTable.Get(version);
        var module = state.GetGlobal("coroutine").AsTable();
        var close = module.Get(Key(state, "close"));
        Assert.Equal(features.HasCoroutineClose, close.Kind == LuaValueKind.Function);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void WarnSurfaceMatchesProfile(LuaLanguageVersion version)
    {
        var state = CreateState(version);
        var features = LuaVersionFeatureTable.Get(version);
        Assert.Equal(features.HasWarnLibrary, state.GetGlobal("warn").Kind == LuaValueKind.Function);
    }

    [Theory]
    [InlineData(LuaLanguageVersion.Lua51)]
    [InlineData(LuaLanguageVersion.Lua52)]
    public void LegacyVersionsRejectFloorDivisionAndBitwiseAtParse(LuaLanguageVersion version)
    {
        var syntax = LuaParser.Parse(
            SourceText.FromUtf8("return 5 // 2"),
            parserOptions: new LuaParserOptions { LanguageVersion = version });
        Assert.NotEmpty(syntax.Diagnostics);

        var bitwise = LuaParser.Parse(
            SourceText.FromUtf8("return 1 & 2"),
            parserOptions: new LuaParserOptions { LanguageVersion = version });
        Assert.NotEmpty(bitwise.Diagnostics);
    }

    [Theory]
    [InlineData(LuaLanguageVersion.Lua53)]
    [InlineData(LuaLanguageVersion.Lua54)]
    [InlineData(LuaLanguageVersion.Lua55)]
    public void ModernVersionsExecuteIntegerAndBitwiseOps(LuaLanguageVersion version)
    {
        var state = CreateState(version);
        var values = Execute(state, "return 5 // 2, 5 & 3, 1 << 3");
        Assert.Equal(2, values[0].AsInteger());
        Assert.Equal(1, values[1].AsInteger());
        Assert.Equal(8, values[2].AsInteger());
    }

    [Fact]
    public void Lua51LoadstringAndModuleEnvironmentContract()
    {
        var state = CreateState(LuaLanguageVersion.Lua51);
        var values = Execute(
            state,
            """
            local f = assert(loadstring("return x"))
            setfenv(f, { x = 11 })
            module("m", package.seeall)
            y = 12
            return f(), m.y, package.loaded.m == m
            """);
        Assert.Equal(11, values[0].AsFloat());
        Assert.Equal(12, values[1].AsFloat());
        Assert.True(values[2].IsTruthy);
    }

    [Fact]
    public void Lua53FinalizerErrorIsSynchronousAndLua54IsNot()
    {
        Assert.True(LuaVersionFeatureTable.Get(LuaLanguageVersion.Lua53).SynchronousFinalizerErrors);
        Assert.False(LuaVersionFeatureTable.Get(LuaLanguageVersion.Lua54).SynchronousFinalizerErrors);
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void DebugSetCStackLimitMatchesProfile(LuaLanguageVersion version)
    {
        var state = CreateState(version);
        var features = LuaVersionFeatureTable.Get(version);
        var debug = state.GetGlobal("debug").AsTable();
        Assert.Equal(
            features.HasDebugSetCStackLimit,
            debug.Get(Key(state, "setcstacklimit")).Kind == LuaValueKind.Function);
    }

    private static LuaState CreateState(LuaLanguageVersion version)
    {
        var state = new LuaState(new LuaStateOptions { LanguageVersion = version });
        LuaStandardLibrary.InstallAll(state);
        return state;
    }

    private static LuaValue Key(LuaState state, string name) =>
        LuaValue.FromString(state.Strings.GetOrCreate(Encoding.UTF8.GetBytes(name)));

    private static LuaValue[] Execute(LuaState state, string source)
    {
        var syntax = LuaParser.Parse(
            SourceText.FromUtf8(source),
            parserOptions: new LuaParserOptions { LanguageVersion = state.LanguageVersion });
        var semantic = LuaBinder.Bind(
            syntax,
            LuaBinderOptions.Default with { LanguageVersion = state.LanguageVersion });
        Assert.Empty(semantic.Diagnostics);
        var lowering = LuaLowerer.Lower(semantic);
        Assert.Empty(lowering.Diagnostics);
        var result = new LuaInterpreter().Execute(state, state.CreateMainClosure(lowering.Module!));
        Assert.Equal(LuaVmSignal.Completed, result.Signal);
        return result.Values.ToArray();
    }
}
