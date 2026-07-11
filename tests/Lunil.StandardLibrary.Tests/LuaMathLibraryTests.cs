using Lunil.Core.Text;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.StandardLibrary.Tests;

public sealed class LuaMathLibraryTests
{
    [Fact]
    public void InstallsConstantsAndNumericFunctionsWithLuaTags()
    {
        var values = Execute(
            "local i,f=math.modf(-3.25); return math.abs(-7), math.abs(-1.5), " +
            "math.floor(3.8), math.ceil(-3.8), i, f, math.tointeger('12'), math.type(1.0)");

        Assert.Equal(LuaValue.FromInteger(7), values[0]);
        Assert.Equal(LuaValue.FromFloat(1.5), values[1]);
        Assert.Equal(LuaValue.FromInteger(3), values[2]);
        Assert.Equal(LuaValue.FromInteger(-3), values[3]);
        Assert.Equal(LuaValue.FromInteger(-3), values[4]);
        Assert.Equal(LuaValue.FromFloat(-0.25), values[5]);
        Assert.Equal(LuaValue.FromInteger(12), values[6]);
        Assert.Equal("float", values[7].AsString().ToString());
    }

    [Fact]
    public void MinAndMaxUseLuaLessThanMetamethod()
    {
        var state = new LuaState();
        LuaStandardLibrary.InstallMath(state);
        state.SetGlobal(
            "setmetatable",
            LuaValue.FromFunction(new LuaNativeFunction(
                "setmetatable",
                static (_, arguments) =>
                {
                    arguments[0].AsTable().SetMetatable(arguments[1].AsTable());
                    return [arguments[0]];
                })));

        var values = Execute(
            state,
            "local mt={__lt=function(a,b) return a.n < b.n end}; " +
            "local a=setmetatable({n=4},mt); local b=setmetatable({n=2},mt); " +
            "return math.min(a,b).n, math.max(a,b).n");

        Assert.Equal([LuaValue.FromInteger(2), LuaValue.FromInteger(4)], values);
    }

    [Fact]
    public void MinComparatorCannotYieldAcrossPucCompatibleNativeBoundary()
    {
        var state = new LuaState();
        LuaStandardLibrary.InstallMath(state);
        state.InstallCoroutineModule();
        state.InstallProtectedCallFunctions();
        state.SetGlobal(
            "setmetatable",
            LuaValue.FromFunction(new LuaNativeFunction(
                "setmetatable",
                static (_, arguments) =>
                {
                    arguments[0].AsTable().SetMetatable(arguments[1].AsTable());
                    return [arguments[0]];
                })));

        var values = Execute(
            state,
            "local mt={__lt=function() coroutine.yield(1) end}; " +
            "local a=setmetatable({},mt); local b=setmetatable({},mt); " +
            "local co=coroutine.create(function() " +
            "local ok,err=pcall(function() return math.min(a,b) end); return ok,err~=nil end); " +
            "return coroutine.resume(co)");

        Assert.Equal(
            [LuaValue.FromBoolean(true), LuaValue.FromBoolean(false), LuaValue.FromBoolean(true)],
            values);
    }

    [Fact]
    public void RandomSequenceMatchesPucLua548Xoshiro256StarStar()
    {
        var values = Execute(
            "math.randomseed(123,456); return math.random(), math.random(1,10), " +
            "math.random(0), math.random(-9223372036854775808,9223372036854775807)");

        Assert.Equal(0.59438554497681495, values[0].AsFloat());
        Assert.Equal(3, values[1].AsInteger());
        Assert.Equal(-7518772449294522146, values[2].AsInteger());
        Assert.Equal(78436196238586190, values[3].AsInteger());
    }

    [Fact]
    public void HandlesIntegerBoundariesLikePucLua()
    {
        var values = Execute(
            "return math.abs(math.mininteger), math.fmod(math.mininteger,-1), " +
            "math.ult(-1,0), math.floor(math.huge), math.modf(math.huge)");

        Assert.Equal(long.MinValue, values[0].AsInteger());
        Assert.Equal(0, values[1].AsInteger());
        Assert.False(values[2].AsBoolean());
        Assert.Equal(double.PositiveInfinity, values[3].AsFloat());
        Assert.Equal(double.PositiveInfinity, values[4].AsFloat());
        Assert.Equal(0, values[5].AsFloat());
    }

    private static LuaValue[] Execute(string source)
    {
        var state = new LuaState();
        LuaStandardLibrary.InstallMath(state);
        return Execute(state, source);
    }

    private static LuaValue[] Execute(LuaState state, string source)
    {
        var lowering = LuaLowerer.Lower(
            LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source))));
        Assert.Empty(lowering.Diagnostics);
        var result = new LuaInterpreter().Execute(state, state.CreateMainClosure(lowering.Module!));
        return result.Values.ToArray();
    }
}
