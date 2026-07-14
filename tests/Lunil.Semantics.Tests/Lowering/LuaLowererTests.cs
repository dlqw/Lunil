using Lunil.Core.Text;
using Lunil.IR.Canonical;
using Lunil.IR.Lua54;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.Semantics.Tests.Lowering;

public sealed class LuaLowererTests
{
    [Fact]
    public void LowersLocalsShortCircuitAndMultipleResultsToVerifiedIr()
    {
        var result = Lower("local a, b = 1, 2; return a and b, a or b");

        Assert.Empty(result.Diagnostics);
        Assert.True(result.Succeeded);
        var module = Assert.IsType<LuaIrModule>(result.Module);
        Assert.Empty(LuaIrVerifier.Verify(module));
        Assert.Contains(module.Functions[0].Instructions, static instruction =>
            instruction.Opcode == LuaIrOpcode.JumpIfFalse);
        Assert.Contains(module.Functions[0].Instructions, static instruction =>
            instruction.Opcode == LuaIrOpcode.JumpIfTrue);
    }

    [Fact]
    public void EncodesClosureCaptureSourcesAcrossMultipleLevels()
    {
        const string source = """
            local x = 10
            return function(a)
                local y = 20
                return function(b) return x + y + a + b end
            end
            """;

        var result = Lower(source);

        var module = Assert.IsType<LuaIrModule>(result.Module);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(3, module.Functions.Length);
        Assert.Contains(module.Functions[1].Upvalues, upvalue =>
            upvalue.Name == "x" && upvalue.SourceKind == LuaIrUpvalueSourceKind.Register);
        Assert.Contains(module.Functions[2].Upvalues, upvalue =>
            upvalue.Name == "x" && upvalue.SourceKind == LuaIrUpvalueSourceKind.Upvalue);
        Assert.Contains(module.Functions[2].Upvalues, upvalue =>
            upvalue.Name == "y" && upvalue.SourceKind == LuaIrUpvalueSourceKind.Register);
    }

    [Fact]
    public void LowersCompleteControlFlowAndTableForms()
    {
        const string source = """
            local sum = 0
            local t = { 1, 2, key = 3, [4] = 4 }
            for i = 1, 5 do
                if i == 3 then goto continue end
                sum = sum + t[i]
                ::continue::
            end
            while sum < 20 do sum = sum + 1 end
            repeat sum = sum - 1 until sum == 10
            return sum, t.key
            """;

        var result = Lower(source);

        var module = Assert.IsType<LuaIrModule>(result.Module);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(LuaIrVerifier.Verify(module));
        Assert.Contains(module.Functions[0].Instructions, static instruction =>
            instruction.Opcode == LuaIrOpcode.NumericForPrepare);
        Assert.Contains(module.Functions[0].Instructions, static instruction =>
            instruction.Opcode == LuaIrOpcode.SetTable);
    }

    [Fact]
    public void ReusesTemporaryRegistersAcrossLargeTableConstructors()
    {
        var entries = string.Join(",", Enumerable.Range(1, 300));

        var result = Lower($"local t={{{entries}}} return t[1],t[300]");

        var module = Assert.IsType<LuaIrModule>(result.Module);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(LuaIrVerifier.Verify(module));
        Assert.InRange(module.Functions[0].RegisterCount, 1, 255);
        Assert.Equal(300, module.Functions[0].Instructions.Count(static instruction =>
            instruction.Opcode == LuaIrOpcode.SetList));
    }

    [Fact]
    public void GotoLeavingNestedGenericForLoopsClosesImplicitIteratorVariables()
    {
        const string source = """
            local function iterator()
                return function() return nil end, nil, nil, setmetatable({}, {__close=function() end})
            end
            for i in iterator() do
                for j in iterator() do
                    goto done
                end
            end
            ::done::
            return true
            """;
        var result = Lower(source);

        var module = Assert.IsType<LuaIrModule>(result.Module);
        Assert.Empty(result.Diagnostics);
        var function = module.Functions[0];
        var firstIteratorClose = function.Instructions
            .Where(static instruction => instruction.Opcode == LuaIrOpcode.MarkToBeClosed)
            .Min(static instruction => instruction.A);
        Assert.Contains(function.Instructions, instruction =>
            instruction.Opcode == LuaIrOpcode.Jump && instruction.C == firstIteratorClose);
        Assert.NotEmpty(Lua54CanonicalPrototypeWriter.Write(
            module,
            module.MainFunctionId,
            stripDebugInformation: false));
    }

    [Fact]
    public void LowersMainChunkEnvironmentReadsAndWritesAsUpvalues()
    {
        var result = Lower("local original = _ENV; _ENV = nil; return original, _ENV");

        var module = Assert.IsType<LuaIrModule>(result.Module);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(LuaIrVerifier.Verify(module));
        Assert.Contains(module.Functions[0].Instructions, static instruction =>
            instruction.Opcode == LuaIrOpcode.GetUpvalue);
        Assert.Contains(module.Functions[0].Instructions, static instruction =>
            instruction.Opcode == LuaIrOpcode.SetUpvalue);
    }

    [Fact]
    public void MatchesPucLineInfoForMultilineBinaryAssignments()
    {
        var result = Lower("local b={10}\na=b[1]\n +\n b[1]\nb=4");

        var function = Assert.IsType<LuaIrModule>(result.Module).Functions[0];
        Assert.Empty(result.Diagnostics);
        Assert.DoesNotContain(function.Instructions, static instruction =>
            instruction.SourceLine == 2);
        Assert.Equal(3, function.Instructions.First(static instruction =>
            instruction.Opcode == LuaIrOpcode.Binary).SourceLine);
        Assert.Equal(4, function.Instructions.First(static instruction =>
            instruction.Opcode == LuaIrOpcode.SetTable).SourceLine);
    }

    [Fact]
    public void LowersDirectReturnedCallsAsTailCalls()
    {
        var result = Lower(
            "local function f(...) return ... end " +
            "local function g(...) return f(...) end " +
            "local function h(...) return (f(...)) end");

        var module = Assert.IsType<LuaIrModule>(result.Module);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(LuaIrVerifier.Verify(module));
        Assert.Contains(module.Functions[2].Instructions, static instruction =>
            instruction.Opcode == LuaIrOpcode.TailCall);
        Assert.DoesNotContain(module.Functions[2].Instructions, static instruction =>
            instruction.Opcode == LuaIrOpcode.Call);
        Assert.Contains(module.Functions[3].Instructions, static instruction =>
            instruction.Opcode == LuaIrOpcode.Call);
        Assert.DoesNotContain(module.Functions[3].Instructions, static instruction =>
            instruction.Opcode == LuaIrOpcode.TailCall);
    }

    [Fact]
    public void DoesNotTailCallWhileAToBeClosedVariableIsActive()
    {
        var result = Lower(
            "local function target() return 1 end " +
            "local function wrapper() local value <close> = nil return target() end");

        var wrapper = Assert.IsType<LuaIrModule>(result.Module).Functions[2];
        Assert.Empty(result.Diagnostics);
        Assert.Contains(wrapper.Instructions, static instruction =>
            instruction.Opcode == LuaIrOpcode.Call);
        Assert.Contains(wrapper.Instructions, static instruction =>
            instruction.Opcode == LuaIrOpcode.Return && instruction.B == -1);
        Assert.DoesNotContain(wrapper.Instructions, static instruction =>
            instruction.Opcode == LuaIrOpcode.TailCall);
    }

    [Fact]
    public void ReusesTemporariesAcrossLongBinaryChains()
    {
        var expression = string.Join("+", Enumerable.Range(1, 300));
        var result = Lower($"return {expression}");

        var function = Assert.IsType<LuaIrModule>(result.Module).Functions[0];
        Assert.Empty(result.Diagnostics);
        Assert.Empty(LuaIrVerifier.Verify(result.Module!));
        Assert.InRange(function.RegisterCount, 1, 8);
    }

    [Fact]
    public void RefusesToLowerInvalidBoundProgram()
    {
        var result = Lower("break");

        Assert.False(result.Succeeded);
        Assert.Null(result.Module);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "LUA3005");
    }

    private static LuaLoweringResult Lower(string source) => LuaLowerer.Lower(
        LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source))));
}
