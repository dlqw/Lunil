using System.Collections.Immutable;
using Luac.Core.Text;
using Luac.IR.Canonical;
using Luac.Runtime.Execution;
using Luac.Runtime.Values;
using Luac.Semantics.Binding;
using Luac.Semantics.Lowering;
using Luac.Syntax.Parsing;

namespace Luac.Runtime.Tests.Execution;

public sealed class LuaCloseTests
{
    [Fact]
    public void ClosesVariablesInReverseOrderWithNilErrorOnNormalExit()
    {
        const string source = """
            local log = ""
            local mt = {}
            function mt.__close(self, error)
                log = log .. self.name .. (error and "E" or "N")
            end
            do
                local first <close> = setmetatable({ name = "a" }, mt)
                local second <close> = setmetatable({ name = "b" }, mt)
            end
            return log
            """;

        Assert.Equal("bNaN", Execute(source)[0].AsString().ToString());
    }

    [Fact]
    public void ErrorUnwindPassesTheErrorToLuaClosureClosers()
    {
        const string source = """
            local log = ""
            local mt = {}
            function mt.__close(_, error) log = error and "error" or "nil" end
            local ok = pcall(function()
                local value <close> = setmetatable({}, mt)
                raise("body")
            end)
            return ok, log
            """;

        var values = Execute(source);
        Assert.Equal(LuaValue.FromBoolean(false), values[0]);
        Assert.Equal("error", values[1].AsString().ToString());
    }

    [Fact]
    public void CloserErrorReplacesTheOriginalError()
    {
        const string source = """
            local mt = {}
            function mt.__close() raise("close") end
            return pcall(function()
                local value <close> = setmetatable({}, mt)
                raise("body")
            end)
            """;

        var values = Execute(source);

        Assert.Equal(LuaValue.FromBoolean(false), values[0]);
        Assert.Equal("close", values[1].AsString().ToString());
    }

    [Fact]
    public void ReturnValuesAreSnapshottedBeforeClosersRun()
    {
        const string source = """
            local result = 1
            local mt = {}
            function mt.__close() result = 2 end
            local value <close> = setmetatable({}, mt)
            return result
            """;

        Assert.Equal([LuaValue.FromInteger(1)], Execute(source));
    }

    [Theory]
    [InlineData("nil")]
    [InlineData("false")]
    public void NilAndFalseCloseValuesAreIgnored(string value)
    {
        Assert.Equal(
            [LuaValue.FromInteger(1)],
            Execute($"local value <close> = {value}; return 1"));
    }

    [Fact]
    public void RejectsNonClosableValuesAtDeclarationTime()
    {
        const string source = """
            local reached = false
            local ok = pcall(function()
                local value <close> = 1
                reached = true
            end)
            return ok, reached
            """;

        Assert.Equal(
            [LuaValue.FromBoolean(false), LuaValue.FromBoolean(false)],
            Execute(source));
    }

    [Fact]
    public void TailCallRunsLuaClosersBeforeInvokingTheSnapshottedCall()
    {
        const string source = """
            local mt = {}
            function mt.__close() record("close") end
            local value <close> = setmetatable({}, mt)
            return target(42)
            """;
        var events = new List<string>();
        var state = CreateState();
        state.SetGlobal("record", LuaValue.FromFunction(new LuaNativeFunction(
            "record",
            (_, arguments) =>
            {
                events.Add(arguments[0].AsString().ToString());
                return [];
            })));
        state.SetGlobal("target", LuaValue.FromFunction(new LuaNativeFunction(
            "target",
            (_, arguments) =>
            {
                events.Add("target");
                return [arguments[0]];
            })));
        var module = RewriteFinalCallAsTailCall(Compile(source));

        var values = new LuaInterpreter()
            .Execute(state, state.CreateMainClosure(module))
            .Values.ToArray();

        Assert.Equal(["close", "target"], events);
        Assert.Equal([LuaValue.FromInteger(42)], values);
    }

    private static LuaValue[] Execute(string source)
    {
        var state = CreateState();
        return new LuaInterpreter()
            .Execute(state, state.CreateMainClosure(Compile(source)))
            .Values.ToArray();
    }

    private static LuaState CreateState()
    {
        var state = new LuaState();
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
        state.SetGlobal(
            "raise",
            LuaValue.FromFunction(new LuaNativeFunction(
                "raise",
                static (_, arguments) => throw new LuaRuntimeException(arguments[0]))));
        return state;
    }

    private static Luac.IR.Canonical.LuaIrModule Compile(string source)
    {
        var lowering = LuaLowerer.Lower(
            LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source))));
        Assert.Empty(lowering.Diagnostics);
        return Assert.IsType<Luac.IR.Canonical.LuaIrModule>(lowering.Module);
    }

    private static LuaIrModule RewriteFinalCallAsTailCall(LuaIrModule module)
    {
        var functions = module.Functions.ToArray();
        var functionIndex = Array.FindIndex(
            functions,
            static function => function.Instructions.Any(
                static instruction => instruction.Opcode == LuaIrOpcode.MarkToBeClosed));
        Assert.True(functionIndex >= 0);
        var function = functions[functionIndex];
        var instructions = function.Instructions.ToArray();
        var callIndex = instructions.Length - 2;
        for (; callIndex >= 0; callIndex--)
        {
            if (instructions[callIndex].Opcode == LuaIrOpcode.Call &&
                callIndex + 1 < instructions.Length &&
                instructions[callIndex + 1].Opcode == LuaIrOpcode.Return)
            {
                break;
            }
        }

        Assert.True(callIndex >= 0);
        instructions[callIndex] = instructions[callIndex] with { Opcode = LuaIrOpcode.TailCall };
        var code = instructions.ToImmutableArray();
        functions[functionIndex] = function with
        {
            Instructions = code,
            BasicBlocks = LuaIrControlFlow.Build(code),
        };
        return module with { Functions = functions.ToImmutableArray() };
    }

}
