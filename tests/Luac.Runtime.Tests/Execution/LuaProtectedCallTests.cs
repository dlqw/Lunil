using Luac.Core.Text;
using Luac.Runtime.Execution;
using Luac.Runtime.Values;
using Luac.Semantics.Binding;
using Luac.Semantics.Lowering;
using Luac.Syntax.Parsing;

namespace Luac.Runtime.Tests.Execution;

public sealed class LuaProtectedCallTests
{
    [Fact]
    public void PCallReturnsSuccessAndAllLuaClosureResults()
    {
        var values = Execute("return pcall(function(a, b) return a + b, a * b end, 3, 4)");

        Assert.Equal(
            [
                LuaValue.FromBoolean(true),
                LuaValue.FromInteger(7),
                LuaValue.FromInteger(12),
            ],
            values);
    }

    [Fact]
    public void PCallCatchesRuntimeErrorsAsLuaValues()
    {
        var values = Execute("return pcall(function() return nil + 1 end)");

        Assert.Equal(LuaValue.FromBoolean(false), values[0]);
        Assert.Contains("Cannot apply Add", values[1].AsString().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void XpcallRunsALuaErrorHandlerOnTheExplicitFrameStack()
    {
        const string source = """
            local function fail() return nil + 1 end
            local function handler(error) return "handled: " .. error end
            return xpcall(fail, handler)
            """;

        var values = Execute(source);

        Assert.Equal(LuaValue.FromBoolean(false), values[0]);
        Assert.Contains("handled: Cannot apply Add", values[1].AsString().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void NestedProtectedCallsChooseTheNearestBoundary()
    {
        const string source = """
            local function inner()
                return pcall(function() return nil + 1 end)
            end
            return pcall(inner)
            """;

        Assert.Equal(
            [
                LuaValue.FromBoolean(true),
                LuaValue.FromBoolean(false),
            ],
            Execute(source).Take(2));
    }

    [Fact]
    public void XpcallReplacesHandlerFailuresWithThePucErrorValue()
    {
        const string source = """
            local function fail() return nil + 1 end
            local function handler() return nil + 1 end
            return xpcall(fail, handler)
            """;

        var values = Execute(source);

        Assert.Equal(LuaValue.FromBoolean(false), values[0]);
        Assert.Equal("error in error handling", values[1].AsString().ToString());
    }

    [Fact]
    public void XpcallRejectsANonFunctionHandlerBeforeEnteringProtection()
    {
        var values = Execute("return pcall(function() return xpcall(function() end, {}) end)");

        Assert.Equal(LuaValue.FromBoolean(false), values[0]);
        Assert.Contains("Bad argument #2", values[1].AsString().ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void BitwiseOperatorsDoNotCoerceNumericStrings()
    {
        var values = Execute("local value = '5'; return pcall(function() return value & 3 end)");

        Assert.Equal(LuaValue.FromBoolean(false), values[0]);
    }

    [Fact]
    public void UnprotectedErrorRetainsItsLuaErrorValueAtTheHostBoundary()
    {
        var state = CreateState();
        var module = Compile("return nil + 1");

        var exception = Assert.Throws<LuaRuntimeException>(() =>
            new LuaInterpreter().Execute(state, state.CreateMainClosure(module)));

        Assert.True(exception.HasErrorValue);
        Assert.Equal(LuaValueKind.String, exception.ErrorValue.Kind);
        Assert.Equal(LuaThreadStatus.Error, state.MainThread.Status);
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
        return state;
    }

    private static Luac.IR.Canonical.LuaIrModule Compile(string source)
    {
        var lowering = LuaLowerer.Lower(
            LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source))));
        Assert.Empty(lowering.Diagnostics);
        return Assert.IsType<Luac.IR.Canonical.LuaIrModule>(lowering.Module);
    }
}
