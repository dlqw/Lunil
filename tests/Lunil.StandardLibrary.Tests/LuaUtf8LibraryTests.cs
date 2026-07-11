using Lunil.Core.Text;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.StandardLibrary.Tests;

public sealed class LuaUtf8LibraryTests
{
    [Fact]
    public void EncodesDecodesCountsAndOffsetsUtf8Sequences()
    {
        var values = Execute(
            "local s=utf8.char(0x41,0x20ac,0x1f600); " +
            "local a,b,c=utf8.codepoint(s,1,-1); " +
            "return #s,utf8.len(s),a,b,c,utf8.offset(s,2),utf8.offset(s,-1)");

        Assert.Equal(
            [
                LuaValue.FromInteger(8),
                LuaValue.FromInteger(3),
                LuaValue.FromInteger(0x41),
                LuaValue.FromInteger(0x20ac),
                LuaValue.FromInteger(0x1f600),
                LuaValue.FromInteger(2),
                LuaValue.FromInteger(5),
            ],
            values);
    }

    [Fact]
    public void StrictAndLaxModesMatchLuaExtendedUtf8Rules()
    {
        var values = Execute(
            "local s=utf8.char(0x110000); local n,p=utf8.len(s); " +
            "return n,p,utf8.len(s,1,-1,true),utf8.codepoint(s,1,-1,true)");

        Assert.True(values[0].IsNil);
        Assert.Equal(1, values[1].AsInteger());
        Assert.Equal(1, values[2].AsInteger());
        Assert.Equal(0x110000, values[3].AsInteger());
    }

    [Fact]
    public void CodesReturnsGenericForIterator()
    {
        var values = Execute(
            "local total=0; for p,c in utf8.codes(utf8.char(65,8364)) do " +
            "total=total+p+c end; return total");

        Assert.Equal([LuaValue.FromInteger(8432)], values);
    }

    [Fact]
    public void StringArgumentsAcceptLuaNumbers()
    {
        var values = Execute(
            "local total=0; for _,c in utf8.codes(123) do total=total+c end; " +
            "return utf8.len(123),total");

        Assert.Equal([LuaValue.FromInteger(3), LuaValue.FromInteger(150)], values);
    }

    [Fact]
    public void LenReportsExactInvalidBytePosition()
    {
        var state = new LuaState();
        LuaStandardLibrary.InstallUtf8(state);
        state.SetGlobal(
            "bad",
            LuaValue.FromString(state.Strings.GetOrCreate([0x41, 0xe2, 0x28, 0xa1])));

        var values = Execute(state, "local n,p=utf8.len(bad); return n,p");

        Assert.True(values[0].IsNil);
        Assert.Equal(2, values[1].AsInteger());
    }

    private static LuaValue[] Execute(string source)
    {
        var state = new LuaState();
        LuaStandardLibrary.InstallUtf8(state);
        return Execute(state, source);
    }

    private static LuaValue[] Execute(LuaState state, string source)
    {
        var lowering = LuaLowerer.Lower(
            LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source))));
        Assert.Empty(lowering.Diagnostics);
        return new LuaInterpreter()
            .Execute(state, state.CreateMainClosure(lowering.Module!))
            .Values
            .ToArray();
    }
}
