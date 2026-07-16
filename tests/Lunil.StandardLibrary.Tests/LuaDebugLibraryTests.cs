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
    public void TracebackLevelZeroIncludesTheNativeTracebackFrame()
    {
        var values = Execute(
            "local st,msg=(function() return pcall end)()(debug.traceback) " +
            "return debug.traceback('trace',0),debug.traceback('trace'),st,msg");

        Assert.Contains("'debug.traceback'", values[0].AsString().ToString());
        Assert.DoesNotContain("'debug.traceback'", values[1].AsString().ToString());
        Assert.True(values[2].AsBoolean());
        Assert.Contains("'pcall'", values[3].AsString().ToString());
    }

    [Fact]
    public void SuspendedCoroutineTracebackIncludesAndCanSkipTheYieldFrame()
    {
        var state = new LuaState();
        LuaStandardLibrary.InstallBasic(state);
        LuaStandardLibrary.InstallTable(state);
        LuaStandardLibrary.InstallCoroutine(state);
        LuaStandardLibrary.InstallDebug(state);

        var values = Execute(
            state,
            "local function f(x) if x>0 then f(x-1) else " +
            "coroutine.yield(debug.getinfo(1,'l').currentline) end end " +
            "local co=coroutine.create(function() f(1) end) " +
            "local _,line=coroutine.resume(co) " +
            "local info=debug.getinfo(co,1,'Sl') " +
            "local name,value=debug.getlocal(co,1,1) " +
            "return debug.traceback(co,nil,0),debug.traceback(co,nil,1)," +
            "info.what,name,value,info.currentline,line,debug.getlocal(co,1,5)");

        Assert.Contains("yield", values[0].AsString().ToString());
        Assert.Contains("'f'", values[0].AsString().ToString());
        Assert.DoesNotContain("yield", values[1].AsString().ToString());
        Assert.Equal("Lua", values[2].AsString().ToString());
        Assert.Equal("x", values[3].AsString().ToString());
        Assert.Equal(0, values[4].AsInteger());
        Assert.Equal(values[6], values[5]);
        Assert.Equal(7, values.Length);
    }

    [Fact]
    public void TracebackTruncatesDeepStacksToPucHeadAndTailLimits()
    {
        var values = Execute(
            "local function deep(n) if n==0 then return debug.traceback('message',1) " +
            "else return (deep(n-1)) end end local result=deep(40) return result");

        var traceback = values[0].AsString().ToString();
        Assert.Contains("(skipping ", traceback);
        Assert.Equal(23, traceback.Count(static character => character == '\n'));
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
    public void UpvalueOperationsReportNoNameForStrippedClosures()
    {
        var state = new LuaState();
        LuaStandardLibrary.InstallAll(state);
        var values = Execute(
            state,
            "local function root() local environmentProbe=print local value=12 " +
            "return function() return value end end " +
            "local copy=assert(load(string.dump(root,true))) local inner=copy() " +
            "local name,value=debug.getupvalue(inner,1) " +
            "local setname=debug.setupvalue(inner,1,13) " +
            "local copy2=assert(load(string.dump(copy))) " +
            "local name2,value2=debug.getupvalue(copy2,1) " +
            "return name,value,setname,inner(),name2,type(value2)");

        Assert.Equal("(no name)", values[0].AsString().ToString());
        Assert.Equal(12, values[1].AsInteger());
        Assert.Equal("(no name)", values[2].AsString().ToString());
        Assert.Equal(13, values[3].AsInteger());
        Assert.Equal("(no name)", values[4].AsString().ToString());
        Assert.Equal("table", values[5].AsString().ToString());
    }

    [Fact]
    public void ActiveLinesIncludeTheDefinitionLineForSingleLineFunctions()
    {
        var values = Execute(
            "local same=function(a,b,...) end " +
            "local si=debug.getinfo(same,'SL') " +
            "local multi=function()\n local x=1\n return x\nend " +
            "local mi=debug.getinfo(multi,'SL') " +
            "return si.linedefined,si.lastlinedefined,si.activelines[si.linedefined]," +
            "mi.activelines[mi.linedefined]");

        Assert.Equal(values[0], values[1]);
        Assert.True(values[2].AsBoolean());
        Assert.True(values[3].IsNil);
    }

    [Fact]
    public void FunctionLocalInspectionReturnsOnlyParameterNames()
    {
        var values = Execute(
            "local function f(a,b,...) local c,d end " +
            "return debug.getlocal(f,1),debug.getlocal(f,2),debug.getlocal(f,3)");

        Assert.Equal("a", values[0].AsString().ToString());
        Assert.Equal("b", values[1].AsString().ToString());
        Assert.Equal(2, values.Length);
    }

    [Fact]
    public void ActiveVarargsCanBeMutatedThroughSetLocal()
    {
        var values = Execute(
            "local function f(a,...) " +
            "local function set(x) return debug.setlocal(2,-1,x) end " +
            "local name=set(42) return name,... end " +
            "return f(1,2,3)");

        Assert.Equal("(vararg)", values[0].AsString().ToString());
        Assert.Equal(42, values[1].AsInteger());
        Assert.Equal(3, values[2].AsInteger());
    }

    [Fact]
    public void LevelZeroExposesNativeCallTemporaries()
    {
        var values = Execute(
            "local n1,v1=debug.getlocal(0,1) " +
            "local n2,v2=debug.getlocal(0,2) " +
            "return n1,v1,n2,v2,debug.getlocal(0,3)");

        Assert.Equal("(C temporary)", values[0].AsString().ToString());
        Assert.Equal(0, values[1].AsInteger());
        Assert.Equal("(C temporary)", values[2].AsString().ToString());
        Assert.Equal(2, values[3].AsInteger());
        Assert.Equal(4, values.Length);
    }

    [Fact]
    public void LuaTemporariesUseLogicalDebugSlots()
    {
        var values = Execute(
            "local before,missing " +
            "local function f() before=select(2,debug.getlocal(2,3)) " +
            "missing=debug.getlocal(2,4) debug.setlocal(2,3,10) return 20 end " +
            "local function g(a,b) return (a+1)+f() end " +
            "return g(0,0),before,missing");

        Assert.Equal(30, values[0].AsInteger());
        Assert.Equal(1, values[1].AsInteger());
        Assert.True(values[2].IsNil);
    }

    [Fact]
    public void LocalFunctionTargetIsTemporaryUntilItsClosureInstructionCompletes()
    {
        var values = Execute(
            "local co=assert(load[[\n" +
            "  local A = function ()\n" +
            "    return x\n" +
            "  end\n" +
            "  return\n" +
            "]]) local before,after " +
            "debug.sethook(function(e,l) " +
            "if l==3 then before=debug.getlocal(2,1) " +
            "elseif l==4 then after=debug.getlocal(2,1) end end,'l') " +
            "co() debug.sethook() return before,after");

        Assert.Equal("(temporary)", values[0].AsString().ToString());
        Assert.Equal("A", values[1].AsString().ToString());
    }

    [Fact]
    public void UserValueOperationsReportInvalidSlotsWithoutErrors()
    {
        var state = new LuaState();
        LuaStandardLibrary.InstallBasic(state);
        LuaStandardLibrary.InstallDebug(state);
        state.SetGlobal("u", LuaValue.FromUserdata(state.CreateUserdata(userValueCount: 1)));

        var values = Execute(
            state,
            "local v,ok=debug.getuservalue(u,2) " +
            "return v,ok,debug.setuservalue(u,10,2)");

        Assert.True(values[0].IsNil);
        Assert.False(values[1].AsBoolean());
        Assert.True(values[2].IsNil);
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
    public void HookFramesAreVisibleToDebugInfoAndTraceback()
    {
        var values = Execute(
            "local kind,trace " +
            "debug.sethook(function() kind=debug.getinfo(1).namewhat " +
            "trace=debug.traceback() debug.sethook() end,'l') " +
            "\nlocal x=1 return kind,trace");

        Assert.Equal("hook", values[0].AsString().ToString());
        Assert.Contains("hook", values[1].AsString().ToString());
    }

    [Fact]
    public void HookRegistryUsesWeakThreadKeys()
    {
        var values = Execute(
            "local hooks=debug.getregistry()._HOOKKEY " +
            "return type(hooks),getmetatable(hooks).__mode");

        Assert.Equal("table", values[0].AsString().ToString());
        Assert.Equal("k", values[1].AsString().ToString());
    }

    [Fact]
    public void NativeCallsExposeTheirFunctionToCallHooks()
    {
        var values = Execute(
            "local seen=false " +
            "debug.sethook(function(e) if e=='call' then " +
            "local i=debug.getinfo(2,'f') if i.func==type then seen=true end end end,'c') " +
            "type(1) debug.sethook() return seen");

        Assert.True(values[0].AsBoolean());
    }

    [Fact]
    public void ErrorCloseHandlersExposeProtectedNativeBoundaryInfo()
    {
        var state = new LuaState();
        LuaStandardLibrary.InstallBasic(state);
        LuaStandardLibrary.InstallTable(state);
        LuaStandardLibrary.InstallCoroutine(state);
        LuaStandardLibrary.InstallDebug(state);

        var values = Execute(
            state,
            "local received,info " +
            "local function foo() " +
            "local value <close> = setmetatable({}, {__close=function(_,err) " +
            "received=err info=debug.getinfo(2) end}) " +
            "error(43) end " +
            "local co=coroutine.create(function() return pcall(foo) end) " +
            "local resumed,ok,err=coroutine.resume(co) " +
            "return resumed,ok,err,received,info.what,info.func==pcall,info.currentline");

        Assert.True(values[0].AsBoolean());
        Assert.False(values[1].AsBoolean());
        Assert.Equal(43, values[2].AsInteger());
        Assert.Equal(43, values[3].AsInteger());
        Assert.Equal("C", values[4].AsString().ToString());
        Assert.True(values[5].AsBoolean());
        Assert.Equal(-1, values[6].AsInteger());
    }

    [Fact]
    public void DebugInfoNamesLuaMetamethodFrames()
    {
        var values = Execute(
            "local function f() local i=debug.getinfo(1,'n') " +
            "return i.name..':'..i.namewhat end " +
            "local a=setmetatable({}, {__add=f,__index=f}) " +
            "local c=setmetatable({}, {__call=f}) " +
            "return a+a,a.x,c()");

        Assert.Equal("add:metamethod", values[0].AsString().ToString());
        Assert.Equal("index:metamethod", values[1].AsString().ToString());
        Assert.Equal("call:metamethod", values[2].AsString().ToString());
    }

    [Fact]
    public void DebugInfoNamesCloseAndFinalizerFrames()
    {
        var values = Execute(
            "local closeName,closeWhat,gcName,gcWhat " +
            "do local value <close> = setmetatable({}, {__close=function() " +
            "local i=debug.getinfo(1,'n') closeName,closeWhat=i.name,i.namewhat end}) end " +
            "do setmetatable({}, {__gc=function() local i=debug.getinfo(1,'n') " +
            "gcName,gcWhat=i.name,i.namewhat end}) end " +
            "collectgarbage('collect') collectgarbage('collect') " +
            "return closeName,closeWhat,gcName,gcWhat");

        Assert.Equal("close", values[0].AsString().ToString());
        Assert.Equal("metamethod", values[1].AsString().ToString());
        Assert.Equal("__gc", values[2].AsString().ToString());
        Assert.Equal("metamethod", values[3].AsString().ToString());
    }

    [Fact]
    public void DebugInfoNamesGenericForIteratorFrames()
    {
        var state = new LuaState();
        LuaStandardLibrary.InstallAll(state);
        var values = Execute(
            state,
            "local f=assert(load([[local name,namewhat " +
            "local function iter() local i=debug.getinfo(1,'n') " +
            "name,namewhat=i.name,i.namewhat end " +
            "for value in iter do end return name,namewhat]])) " +
            "f=assert(load(string.dump(f))) return f()");

        Assert.Equal("for iterator", values[0].AsString().ToString());
        Assert.Equal("for iterator", values[1].AsString().ToString());
    }

    [Fact]
    public void CountOneHooksAdvancePastTheHookedInstruction()
    {
        var values = Execute(
            "local count=0 debug.sethook(function() count=count+1 end,'',1) " +
            "count=0 for i=1,1000 do end local result=count " +
            "debug.sethook() return result");

        Assert.InRange(values[0].AsInteger(), 1001, 1011);
    }

    [Fact]
    public void CountFourHooksIncludeLoopSetupInstructions()
    {
        var values = Execute(
            "local count=0 debug.sethook(function() count=count+1 end,'',4) " +
            "count=0 for i=1,1000 do end " +
            "local ok=250<count and count<255 " +
            "debug.sethook() return ok,count");

        Assert.True(values[0].AsBoolean());
        Assert.InRange(values[1].AsInteger(), 251, 254);
    }

    [Fact]
    public void TailCallsDoNotAlsoEmitOrdinaryCallHooks()
    {
        var values = Execute(
            "local events={} " +
            "local function f() end " +
            "local function g() return f() end " +
            "debug.sethook(function(e) events[#events+1]=e end,'cr') " +
            "g() debug.sethook() return table.concat(events,',')");

        Assert.Equal(
            "return,call,tail call,return,call",
            values[0].AsString().ToString());
    }

    [Fact]
    public void HooksDoNotOverwriteOpenMultipleResults()
    {
        var values = Execute(
            "local a={} for i=1,100 do a[i]=i end " +
            "debug.sethook(function() end,'',1) " +
            "local t={table.unpack(a)} debug.sethook() return #t,t[100]");

        Assert.Equal(100, values[0].AsInteger());
        Assert.Equal(100, values[1].AsInteger());
    }

    [Fact]
    public void ACallHookCanInstallALineHookForThePendingCallee()
    {
        var values = Execute(
            "local seen " +
            "local function f() local x=1 end " +
            "debug.sethook(function() debug.sethook(function(e) " +
            "seen=e debug.sethook() end,'l') end,'c') " +
            "f() return seen");

        Assert.Equal("line", values[0].AsString().ToString());
    }

    [Fact]
    public void LuaCallHooksAreFollowedByTheCalleeEntryLineHook()
    {
        var state = new LuaState();
        LuaStandardLibrary.InstallAll(state);
        var values = Execute(
            state,
            "local co=coroutine.create(function(x)\n" +
            " local a=1\n" +
            " coroutine.yield(debug.getinfo(1,'l'))\n" +
            " return a\n" +
            "end)\n" +
            "local lines={} local function hook(_,line) if line then lines[#lines+1]=line end end\n" +
            "debug.sethook(co,hook,'lc') local _,info=coroutine.resume(co,10)\n" +
            "return #lines,lines[1],lines[2],info.currentline");

        Assert.Equal(2, values[0].AsInteger());
        Assert.Equal(values[3].AsInteger() - 1, values[1].AsInteger());
        Assert.Equal(values[3].AsInteger(), values[2].AsInteger());
    }

    [Fact]
    public void HooksExposeTransferredNativeArgumentsAndResults()
    {
        var values = Execute(
            "local on=false local inp,out " +
            "debug.sethook(function(e) if not on then return end " +
            "local ar=debug.getinfo(2,'r') local t={} " +
            "for i=ar.ftransfer,ar.ftransfer+ar.ntransfer-1 do " +
            "t[#t+1]=select(2,debug.getlocal(2,i)) end " +
            "if e=='return' then out=t else inp=t end end,'cr') " +
            "on=true type(3) on=false debug.sethook() return inp[1],out[1]");

        Assert.Equal(3, values[0].AsInteger());
        Assert.Equal("number", values[1].AsString().ToString());
    }

    [Fact]
    public void TailCallHooksExposeFixedArgumentsAndReturnedValues()
    {
        var values = Execute(
            "local on=false local inp,out " +
            "debug.sethook(function(e) if not on then return end " +
            "local ar=debug.getinfo(2,'r') local t={} " +
            "for i=ar.ftransfer,ar.ftransfer+ar.ntransfer-1 do " +
            "t[#t+1]=select(2,debug.getlocal(2,i)) end " +
            "if e=='return' then out=t else inp=t end end,'cr') " +
            "local function target(a,...) return ... end " +
            "local function caller() on=true return target(20,10,0) end " +
            "caller() on=false debug.sethook() " +
            "return inp[1],#inp,out[1],out[2],#out");

        Assert.Equal(20, values[0].AsInteger());
        Assert.Equal(1, values[1].AsInteger());
        Assert.Equal(10, values[2].AsInteger());
        Assert.Equal(0, values[3].AsInteger());
        Assert.Equal(2, values[4].AsInteger());
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
            "local d=trace('for i=1,4 do a=1 end') " +
            "local e=trace('local a\\na=1\\nwhile a<=3 do\\n a=a+1\\nend') " +
            "local f=trace(\"for i,v in pairs{'a','b'} do\\n a=tostring(i)..v\\nend\") " +
            "return a,b,c,d,e,f");

        Assert.Equal("2,3,4,7", values[0].AsString().ToString());
        Assert.Equal("2,3,2,4,5,6", values[1].AsString().ToString());
        Assert.Equal("1,3,4,3,4", values[2].AsString().ToString());
        Assert.Equal("1,1,1,1", values[3].AsString().ToString());
        Assert.Equal("1,2,3,4,3,4,3,4,3,5", values[4].AsString().ToString());
        Assert.Equal("1,2,1,2,1,3", values[5].AsString().ToString());
    }

    [Fact]
    public void StrippedFunctionsEmitANilLineHookAtEntry()
    {
        var state = new LuaState();
        LuaStandardLibrary.InstallAll(state);
        var values = Execute(
            state,
            "local function foo()\n" +
            " local a=1\n" +
            " local b=2\n" +
            " return b\n" +
            "end\n" +
            "local s=assert(load(string.dump(foo,true)))\n" +
            "local line=true\n" +
            "debug.sethook(function(e,l) assert(e=='line'); line=l end,'l')\n" +
            "local result=s(); debug.sethook(nil)\n" +
            "return result,line");

        Assert.Equal(2, values[0].AsInteger());
        Assert.True(values[1].IsNil);
    }

    private static LuaValue[] Execute(string source)
    {
        var state = new LuaState();
        LuaStandardLibrary.InstallBasic(state);
        LuaStandardLibrary.InstallTable(state);
        LuaStandardLibrary.InstallDebug(state);
        return Execute(state, source);
    }

    private static LuaValue[] Execute(LuaState state, string source)
    {
        var lowering = LuaLowerer.Lower(
            LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source))));
        Assert.Empty(lowering.Diagnostics);
        return new LuaInterpreter().Execute(state, state.CreateMainClosure(lowering.Module!))
            .Values.ToArray();
    }
}
