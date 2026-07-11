using Luac.Core.Text;
using Luac.IR.Canonical;
using Luac.Semantics.Binding;
using Luac.Semantics.Lowering;
using Luac.Syntax.Parsing;

namespace Luac.Semantics.Tests.Lowering;

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
