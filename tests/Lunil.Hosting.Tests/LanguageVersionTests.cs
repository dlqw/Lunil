using Lunil.Core;
using Lunil.Hosting;
using Lunil.IR.Lua53;
using Lunil.Runtime;
using Lunil.Runtime.Execution;

namespace Lunil.Hosting.Tests;

public sealed class LanguageVersionTests
{
    [Fact]
    public void HostAlignsNestedCompilerWorkspaceAndStateOptions()
    {
        using var host = new LuaHost(new LuaHostOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua53,
            InstallStandardLibrary = false,
        });

        Assert.Equal(LuaLanguageVersion.Lua53, host.Options.LanguageVersion);
        Assert.Equal(LuaLanguageVersion.Lua53, host.Compiler.Options.LanguageVersion);
        Assert.Equal(LuaLanguageVersion.Lua53, host.Workspace.Options.LanguageVersion);
        Assert.Equal(LuaLanguageVersion.Lua53, host.State.LanguageVersion);
    }

    [Fact]
    public void Lua53RunsIntegerBitwiseGotoAndClosureSemantics()
    {
        using var host = new LuaHost(new LuaHostOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua53,
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
        });

        var result = host.RunUtf8(
            "local function add(value) return value + 1 end\n" +
            "local total = 0\n" +
            "for i = 1, 4 do total = total + (add(i) << 1) end\n" +
            "goto done\n" +
            "total = 0\n" +
            "::done::\n" +
            "return total, 5 & 3, 8 >> 1");

        Assert.True(result.Succeeded);
        Assert.Equal(28, result.Execution!.Values[0].AsInteger());
        Assert.Equal(1, result.Execution.Values[1].AsInteger());
        Assert.Equal(4, result.Execution.Values[2].AsInteger());
    }

    [Fact]
    public void Lua53BitwiseOperatorsUseMetamethodWhenOnlyOneOperandIsNumeric()
    {
        using var host = new LuaHost(new LuaHostOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua53,
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
        });

        var result = host.RunUtf8(
            "local value = setmetatable({x=10}, {__band=function(a,b) " +
            "return type(a)=='table' and a.x or b.x end})\n" +
            "return value & 12, 12 & value");

        Assert.True(result.Succeeded);
        Assert.Equal(10, result.Execution!.Values[0].AsInteger());
        Assert.Equal(10, result.Execution.Values[1].AsInteger());
    }

    [Fact]
    public void Lua53CanonicalWriterRoundTripsThroughLua53State()
    {
        using var host = new LuaHost(new LuaHostOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua53,
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
        });
        var compilation = host.CompileUtf8("return 6 * 7");
        Assert.True(compilation.Succeeded);
        var module = compilation.Module!;

        var bytes = Lua53CanonicalPrototypeWriter.Write(
            module,
            module.MainFunctionId);
        var closure = host.State.LoadBinaryChunk(bytes);
        var result = new LuaInterpreter().Execute(host.State, closure);

        Assert.Equal(LuaVmSignal.Completed, result.Signal);
        Assert.Equal(42, result.Values[0].AsInteger());
    }

    [Theory]
    [InlineData("local value = 7; return (value == 7) and 4 or 0", 4)]
    [InlineData("local sum = 0; for i = 1, 5 do sum = sum + i end; return sum", 15)]
    [InlineData("local function make(value) return function(add) return value + add end end; return make(10)(2)", 12)]
    [InlineData("local table = { [1] = 4, [2] = 5 }; return table[1] + table[2]", 9)]
    [InlineData("local function values(...) return ... end; return values(1, 2)", 1)]
    [InlineData("local function iterator(limit, control) control = control + 1; if control <= limit then return control, control * 2 end end; local sum = 0; for k, v in iterator, 4, 0 do sum = sum + k + v end; return sum", 30)]
    public void Lua53CanonicalWriterRoundTripsRepresentativePrograms(string source, long expected)
    {
        using var host = new LuaHost(new LuaHostOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua53,
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
        });
        var compilation = host.CompileUtf8(source);
        Assert.True(compilation.Succeeded);
        var module = compilation.Module!;
        var bytes = Lua53CanonicalPrototypeWriter.Write(module, module.MainFunctionId);
        var closure = host.State.LoadBinaryChunk(bytes);
        var result = new LuaInterpreter().Execute(host.State, closure);

        Assert.Equal(LuaVmSignal.Completed, result.Signal);
        Assert.Equal(expected, result.Values[0].Kind == Lunil.Runtime.Values.LuaValueKind.Boolean
            ? result.Values[0].IsTruthy ? 1 : 0
            : result.Values[0].AsInteger());
    }

    [Fact]
    public void Lua53DynamicLoadRetainsLanguageVersionAndRejectsLua54OnlySyntax()
    {
        using var host = new LuaHost(new LuaHostOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua53,
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
        });

        var result = host.RunUtf8(
            "local valid = assert(load('return 6 * 7'))\n" +
            "local invalid, message = load('local value <const> = 1')\n" +
            "return valid(), invalid, type(message)");

        Assert.True(result.Succeeded);
        Assert.Equal(42, result.Execution!.Values[0].AsInteger());
        Assert.True(result.Execution.Values[1].IsNil);
        Assert.Equal("string", result.Execution.Values[2].AsString().ToString());
    }

    [Fact]
    public void Lua53CachesClosuresWithIdenticalUpvalueBindings()
    {
        using var host = new LuaHost(new LuaHostOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua53,
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
        });

        var result = host.RunUtf8(
            "local shared = {}; local closures = {}\n" +
            "for i = 1, 2 do closures[i] = function() return shared end end\n" +
            "local distinct = {}\n" +
            "for i = 1, 2 do distinct[i] = function() return i end end\n" +
            "return closures[1] == closures[2], distinct[1] ~= distinct[2]");

        Assert.True(result.Succeeded);
        Assert.True(result.Execution!.Values[0].AsBoolean());
        Assert.True(result.Execution.Values[1].AsBoolean());
    }

    [Fact]
    public void Lua53StringDumpRoundTripsAClosure()
    {
        using var host = new LuaHost(new LuaHostOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua53,
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
        });

        var result = host.RunUtf8(
            "local function value() return 6 * 7 end\n" +
            "local copy = assert(load(string.dump(value, true)))\n" +
            "return copy()");

        Assert.True(result.Succeeded);
        Assert.Equal(42, result.Execution!.Values[0].AsInteger());
    }

    [Fact]
    public void Lua53StandardLibraryCapabilityMatrixMatchesLua53()
    {
        using var host = new LuaHost(new LuaHostOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua53,
        });

        var result = host.RunUtf8(
            "return type(warn), type(coroutine.close), type(math.type), " +
            "type(math.tointeger), type(table.move), type(table.pack), " +
            "type(string.pack), type(utf8.char), type(math.maxinteger), '12' | 3");

        Assert.True(result.Succeeded);
        var values = result.Execution!.Values;
        Assert.Equal("nil", values[0].AsString().ToString());
        Assert.Equal("nil", values[1].AsString().ToString());
        Assert.Equal("function", values[2].AsString().ToString());
        Assert.Equal("function", values[3].AsString().ToString());
        Assert.Equal("function", values[4].AsString().ToString());
        Assert.Equal("function", values[5].AsString().ToString());
        Assert.Equal("function", values[6].AsString().ToString());
        Assert.Equal("function", values[7].AsString().ToString());
        Assert.Equal("number", values[8].AsString().ToString());
        Assert.Equal(15, values[9].AsInteger());
    }

    [Fact]
    public void Lua53StandardLibraryRetainsVersionSpecificRandomAndUtf8Limits()
    {
        using var host = new LuaHost(new LuaHostOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua53,
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
        });

        var result = host.RunUtf8(
            "local seedResults = select('#', math.randomseed(123))\n" +
            "local randomZero = pcall(math.random, 0)\n" +
            "local utf8Extended = pcall(utf8.char, 0x110000)\n" +
            "local generational = pcall(collectgarbage, 'generational')\n" +
            "return seedResults, randomZero, utf8Extended, " +
            "string.find(utf8.charpattern, string.char(0xF4), 1, true) ~= nil, " +
            "string.find(utf8.charpattern, string.char(0xF5), 1, true) == nil, generational");

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Execution!.Values[0].AsInteger());
        Assert.False(result.Execution.Values[1].AsBoolean());
        Assert.False(result.Execution.Values[2].AsBoolean());
        Assert.True(result.Execution.Values[3].AsBoolean());
        Assert.True(result.Execution.Values[4].AsBoolean());
        Assert.False(result.Execution.Values[5].AsBoolean());
    }

    [Fact]
    public void HostInstallsLua53LibraryWithLua53OnlySurface()
    {
        using var host = new LuaHost(new LuaHostOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua53,
        });

        var result = host.RunUtf8(
            "return _VERSION, type(warn), type(coroutine.close), math.type(1), 5 // 2");

        Assert.Equal(LuaVmSignal.Completed, result.Execution?.Signal);
        Assert.True(result.Compilation.Succeeded);
        Assert.Equal("Lua 5.3", result.Execution!.Values[0].AsString().ToString());
        Assert.Equal("nil", result.Execution.Values[1].AsString().ToString());
        Assert.Equal("nil", result.Execution.Values[2].AsString().ToString());
        Assert.Equal("integer", result.Execution.Values[3].AsString().ToString());
        Assert.Equal(2, result.Execution.Values[4].AsInteger());
    }
}
