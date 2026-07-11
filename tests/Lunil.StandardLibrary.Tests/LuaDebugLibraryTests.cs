using Lunil.Core.Text;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.StandardLibrary.Tests;

public sealed class LuaDebugLibraryTests
{
    [Fact]
    public void StackInfoLocalsRegistryAndTracebackExposeRuntimeFrames()
    {
        var values = Execute(
            "local function inspect(a)\n" +
            " local x=20\n" +
            " local n,v=debug.getlocal(1,2)\n" +
            " local changed=debug.setlocal(1,2,30)\n" +
            " local info=debug.getinfo(1,'flSutL')\n" +
            " return n,v,changed,x,info.what,info.nparams,info.isvararg," +
            "info.currentline,info.activelines[5],debug.traceback('trace')\n" +
            "end\nreturn inspect(10)");

        Assert.Equal("x", values[0].AsString().ToString());
        Assert.Equal(20, values[1].AsInteger());
        Assert.Equal("x", values[2].AsString().ToString());
        Assert.Equal(30, values[3].AsInteger());
        Assert.Equal("Lua", values[4].AsString().ToString());
        Assert.Equal(1, values[5].AsInteger());
        Assert.False(values[6].AsBoolean());
        Assert.True(values[7].AsInteger() >= 4);
        Assert.True(values[8].AsBoolean());
        Assert.Contains("stack traceback:", values[9].AsString().ToString());
    }

    [Fact]
    public void UpvaluesCanBeInspectedMutatedIdentifiedAndJoined()
    {
        var values = Execute(
            "local function make(v) return function(x) v=v+x return v end end " +
            "local a,b=make(1),make(10); local name,value=debug.getupvalue(a,1); " +
            "local same0=debug.upvalueid(a,1)==debug.upvalueid(b,1); " +
            "debug.setupvalue(a,1,5); local av=a(1); debug.upvaluejoin(a,1,b,1); " +
            "local same1=debug.upvalueid(a,1)==debug.upvalueid(b,1); local bv=b(2); " +
            "return name,value,same0,av,same1,bv,a(0),debug.getregistry()~=nil");

        Assert.Equal("v", values[0].AsString().ToString());
        Assert.Equal(1, values[1].AsInteger());
        Assert.False(values[2].AsBoolean());
        Assert.Equal(6, values[3].AsInteger());
        Assert.True(values[4].AsBoolean());
        Assert.Equal(12, values[5].AsInteger());
        Assert.Equal(12, values[6].AsInteger());
        Assert.True(values[7].AsBoolean());
    }

    [Fact]
    public void HooksReportCallReturnLineAndCountWithoutReentry()
    {
        var values = Execute(
            "local events={} debug.sethook(function(e,l) events[#events+1]=e end,'crl',2) " +
            "local function f(x) local y=x+1 return y end local result=f(4) " +
            "debug.sethook() local seen={} for _,e in ipairs(events) do seen[e]=true end " +
            "local h,m,c=debug.gethook(); return result,seen.call,seen['return'],seen.line," +
            "seen.count,h,m,c,#events");

        Assert.Equal(5, values[0].AsInteger());
        Assert.True(values[1].AsBoolean());
        Assert.True(values[2].AsBoolean());
        Assert.True(values[3].AsBoolean());
        Assert.True(values[4].AsBoolean());
        Assert.True(values[5].IsNil);
        Assert.True(values[6].IsNil);
        Assert.True(values[7].IsNil);
        Assert.True(values[8].AsInteger() > 0);
    }

    [Fact]
    public void LineHooksMatchPucControlFlowSequences()
    {
        var values = Execute(
            "local function trace(s) local lines={} " +
            "local function h(e,l) if e=='line' then lines[#lines+1]=l end end " +
            "debug.sethook(h,'l'); assert(load(s))(); debug.sethook(); " +
            "return table.concat(lines,',') end " +
            "local a=trace('if\\ntrue\\nthen\\n a=1\\nelse\\n a=2\\nend') " +
            "local b=trace('local function foo()\\nend\\nfoo()\\nA=1\\nA=2\\nA=3') " +
            "local c=trace('a=1\\nrepeat\\n a=a+1\\nuntil a==3') " +
            "return a,b,c");

        Assert.Equal("2,3,4,7", values[0].AsString().ToString());
        Assert.Equal("2,3,2,4,5,6", values[1].AsString().ToString());
        Assert.Equal("1,3,4,3,4", values[2].AsString().ToString());
    }

    private static LuaValue[] Execute(string source)
    {
        var state = new LuaState();
        LuaStandardLibrary.InstallBasic(state);
        LuaStandardLibrary.InstallTable(state);
        LuaStandardLibrary.InstallDebug(state);
        var lowering = LuaLowerer.Lower(
            LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source))));
        Assert.Empty(lowering.Diagnostics);
        return new LuaInterpreter().Execute(state, state.CreateMainClosure(lowering.Module!))
            .Values.ToArray();
    }
}
