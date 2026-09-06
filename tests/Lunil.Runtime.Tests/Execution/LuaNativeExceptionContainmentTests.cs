using System.Collections.Immutable;
using Lunil.Core;
using Lunil.Core.Text;
using Lunil.IR.Canonical;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.Runtime.Tests.Execution;

public sealed class LuaNativeExceptionContainmentTests
{
    [Fact]
    public void ProtectedCallCatchesAClrExceptionFromANativeBody()
    {
        const string source = """
            local ok, err = pcall(boom)
            return ok, err
            """;

        var values = Execute(source);
        Assert.Equal(LuaValue.FromBoolean(false), values[0]);
        Assert.Equal(
            "System.IO.IOException: disk full",
            values[1].AsString().ToString());
    }

    [Fact]
    public void ProtectedCallCatchesAClrExceptionFromAResumableNativeBody()
    {
        const string source = """
            local ok, err = pcall(step_boom)
            return ok, err
            """;

        var values = Execute(source);
        Assert.Equal(LuaValue.FromBoolean(false), values[0]);
        Assert.Equal(
            "System.InvalidOperationException: step failed",
            values[1].AsString().ToString());
    }

    [Fact]
    public void TheStateStaysUsableAfterAContainedNativeError()
    {
        const string source = """
            local ok = pcall(boom)
            local ok2, value = pcall(function() return 42 end)
            return ok, ok2, value
            """;

        var values = Execute(source);
        Assert.Equal(LuaValue.FromBoolean(false), values[0]);
        Assert.Equal(LuaValue.FromBoolean(true), values[1]);
        Assert.Equal(42, values[2].AsInteger());
    }

    [Fact]
    public void LuaRuntimeExceptionErrorValuesStillPassThroughUnchanged()
    {
        const string source = """
            local ok, err = pcall(raise, "boom")
            return ok, err
            """;

        var values = Execute(source);
        Assert.Equal(LuaValue.FromBoolean(false), values[0]);
        Assert.Equal("boom", values[1].AsString().ToString());
    }

    [Fact]
    public void UnprotectedNativeClrExceptionSurfacesAsALuaRuntimeException()
    {
        const string source = "boom()";

        var state = CreateState();

        // The CLR boundary must observe a Lua runtime exception carrying the converted
        // error, never the raw host exception type.
        var exception = Assert.Throws<LuaRuntimeException>(
            () => new LuaInterpreter().Execute(state, state.CreateMainClosure(Compile(source))));
        Assert.Equal(
            "System.IO.IOException: disk full",
            exception.Message);
    }

    private static LuaState CreateState()
    {
        var state = new LuaState();
        state.InstallProtectedCallFunctions();
        state.SetGlobal(
            "raise",
            LuaValue.FromFunction(new LuaNativeFunction(
                "raise",
                static (_, arguments) => throw new LuaRuntimeException(arguments[0]))));
        state.SetGlobal(
            "boom",
            LuaValue.FromFunction(new LuaNativeFunction(
                "boom",
                static (_, _) => throw new IOException("disk full"))));
        state.SetGlobal(
            "step_boom",
            LuaValue.FromFunction(new LuaNativeFunction(
                "step_boom",
                StepBoomBody)));
        return state;
    }

    private static LuaNativeStep StepBoomBody(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values) =>
        continuationId == 0
            ? throw new InvalidOperationException("step failed")
            : LuaNativeStep.Completed();

    private static LuaValue[] Execute(string source)
    {
        var state = CreateState();
        return new LuaInterpreter()
            .Execute(state, state.CreateMainClosure(Compile(source)))
            .Values.ToArray();
    }

    private static Lunil.IR.Canonical.LuaIrModule Compile(
        string source,
        LuaLanguageVersion languageVersion = LuaLanguageVersion.Lua54)
    {
        var lowering = LuaLowerer.Lower(
            LuaBinder.Bind(
                LuaParser.Parse(
                    SourceText.FromUtf8(source),
                    parserOptions: new LuaParserOptions { LanguageVersion = languageVersion })));
        Assert.Empty(lowering.Diagnostics);
        return Assert.IsType<Lunil.IR.Canonical.LuaIrModule>(lowering.Module);
    }
}
