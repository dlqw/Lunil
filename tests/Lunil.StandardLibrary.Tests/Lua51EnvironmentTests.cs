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

public sealed class Lua51EnvironmentTests
{
    [Fact]
    public void GetFEnvAndSetFEnvChangeChunkGlobalsIndependently()
    {
        var state = CreateState();
        var values = Execute(
            state,
            """
            local env = { value = 41 }
            local function reader()
              return value
            end
            setfenv(reader, env)
            env.value = 42
            return getfenv(reader).value, reader(), type(getfenv(1))
            """);
        Assert.Equal(42, values[0].AsFloat());
        Assert.Equal(42, values[1].AsFloat());
        Assert.Equal("table", values[2].AsString().ToString());
    }

    [Fact]
    public void ModuleAppliesSeeAllAndSetsCallerEnvironment()
    {
        var state = CreateState();
        var values = Execute(
            state,
            """
            module("demo", package.seeall)
            exported = 7
            return demo.exported, demo.type == type, package.loaded.demo == demo
            """);
        Assert.Equal(7, values[0].AsFloat());
        Assert.True(values[1].IsTruthy);
        Assert.True(values[2].IsTruthy);
    }

    [Fact]
    public void ModuleUsesNestedGlobalPathAndLuaOptionCallbacks()
    {
        var state = CreateState();
        var values = Execute(
            state,
            """
            local root = _G
            local loaded = package.loaded
            root.existing = { nested = { seed = 3 } }
            local function decorate(m)
              m.decorated = true
              m.caller_env_was_set = getfenv(2) == m
            end
            local result_count = select('#', module("existing.nested", decorate))
            exported = 7
            return root.existing.nested.seed, root.existing.nested.exported,
              root.existing.nested.decorated, root.existing.nested.caller_env_was_set,
              loaded["existing.nested"] == root.existing.nested, result_count
            """);
        Assert.Equal(3, values[0].AsFloat());
        Assert.Equal(7, values[1].AsFloat());
        Assert.True(values[2].IsTruthy);
        Assert.True(values[3].IsTruthy);
        Assert.True(values[4].IsTruthy);
        Assert.Equal(0, values[5].AsInteger());
    }

    [Fact]
    public void ThreadEnvironmentControlsNewLua51ChunksAndSetFEnvZeroReturnsNothing()
    {
        var state = CreateState();
        var values = Execute(
            state,
            """
            local original = _G
            local env = { marker = 17, assert = assert, loadstring = loadstring }
            local set_count = select('#', setfenv(0, env))
            local loaded = assert(loadstring("return marker"))
            local active = getfenv(0) == env
            setfenv(0, original)
            return set_count, active, loaded()
            """);
        Assert.Equal(0, values[0].AsInteger());
        Assert.True(values[1].IsTruthy);
        Assert.Equal(17, values[2].AsFloat());
    }

    [Fact]
    public void SetFEnvDoesNotMutateSiblingSharedEnvByIdentity()
    {
        var state = CreateState();
        var values = Execute(
            state,
            """
            local function make()
              return function() return x end
            end
            local a = make()
            local b = make()
            setfenv(a, { x = 1 })
            setfenv(b, { x = 2 })
            return a(), b()
            """);
        Assert.Equal(1, values[0].AsFloat());
        Assert.Equal(2, values[1].AsFloat());
    }

    [Fact]
    public void DebugGetFEnvAndSetFEnvAreInstalledOnlyForLua51()
    {
        var lua51 = CreateState(LuaLanguageVersion.Lua51);
        var debug51 = lua51.GetGlobal("debug").AsTable();
        Assert.Equal(LuaValueKind.Function, debug51.Get(Key(lua51, "getfenv")).Kind);
        Assert.Equal(LuaValueKind.Function, debug51.Get(Key(lua51, "setfenv")).Kind);

        var lua54 = CreateState(LuaLanguageVersion.Lua54);
        var debug54 = lua54.GetGlobal("debug").AsTable();
        Assert.Equal(LuaValueKind.Nil, debug54.Get(Key(lua54, "getfenv")).Kind);
        Assert.Equal(LuaValueKind.Nil, debug54.Get(Key(lua54, "setfenv")).Kind);
        Assert.Equal(LuaValueKind.Nil, lua54.GetGlobal("getfenv").Kind);
    }

    private static LuaState CreateState(LuaLanguageVersion version = LuaLanguageVersion.Lua51)
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
