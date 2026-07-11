using Lunil.Core.Text;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.StandardLibrary.Tests;

public sealed class LuaTableAndStringLibraryTests
{
    [Fact]
    public void CallbackStateMachinesSurviveDeterministicGcStressFuzz()
    {
        var state = new LuaState(new LuaStateOptions
        {
            Heap = LuaHeapOptions.Default with { StressEveryAllocation = true },
        });
        LuaStandardLibrary.InstallAll(state);
        var source = "for seed=1,40 do " +
            "math.randomseed(seed,~seed); local t={} " +
            "for i=1,24 do t[i]=math.random(-1000,1000) end " +
            "local nested=false; table.sort(t,function(a,b) collectgarbage(); " +
            "if not nested then nested=true; local u={2,1}; table.sort(u); assert(u[1]==1) end " +
            "return a<b end); for i=2,#t do assert(t[i-1]<=t[i]) end " +
            "local calls=0; local out,n=string.gsub('ab12cd34','(%a+)(%d+)',function(a,b) " +
            "collectgarbage(); calls=calls+1; local inner=string.gsub(a,'.',string.upper); " +
            "return inner..b end); assert(out=='AB12CD34' and n==2 and calls==2) " +
            "local co=coroutine.create(function() string.gsub('a','.',function() coroutine.yield() end) end); " +
            "local ok=coroutine.resume(co); assert(not ok) end return true";
        var lowering = LuaLowerer.Lower(
            LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source))));
        Assert.Empty(lowering.Diagnostics);

        var result = new LuaInterpreter(new LuaInterpreterOptions
        {
            MaximumInstructionCount = 10_000_000,
        }).Execute(state, state.CreateMainClosure(lowering.Module!));

        Assert.True(result.Values[0].AsBoolean());
    }

    [Fact]
    public void TableFunctionsCoverMutationMovementAndRanges()
    {
        var values = Execute(
            "local t=table.pack('a','b',nil,'d'); table.insert(t,2,'x'); " +
            "local removed=table.remove(t,4); table.move(t,1,3,2,t); " +
            "return t.n,removed,table.concat(t,',',1,4),table.unpack({7,8,9},2,3)");

        Assert.Equal(4, values[0].AsInteger());
        Assert.True(values[1].IsNil);
        Assert.Equal("a,a,x,b", values[2].AsString().ToString());
        Assert.Equal(8, values[3].AsInteger());
        Assert.Equal(9, values[4].AsInteger());
    }

    [Fact]
    public void TableCallbacksAreNonYieldableAndSortComparatorWorks()
    {
        var values = Execute(
            "local t={3,1,2}; table.sort(t,function(a,b) return a<b end); " +
            "local object=setmetatable({}, {__len=function() return 2 end," +
            "__index=function(_,i) return i*4 end}); " +
            "local co=coroutine.create(function() return table.concat(setmetatable({}, {" +
            "__len=function() return 1 end,__index=function() coroutine.yield(); return 'x' end})) end); " +
            "local ok,e=coroutine.resume(co); return t[1],t[2],t[3],table.concat(object,':'),ok,e~=nil");

        Assert.Equal(1, values[0].AsInteger());
        Assert.Equal(2, values[1].AsInteger());
        Assert.Equal(3, values[2].AsInteger());
        Assert.Equal("4:8", values[3].AsString().ToString());
        Assert.False(values[4].AsBoolean());
        Assert.True(values[5].AsBoolean());
    }

    [Fact]
    public void TableSortUsesLua54PartitionOrderAndRejectsInvalidComparators()
    {
        var values = Execute(
            "local t={4,1,3,2}; local calls={} " +
            "table.sort(t,function(a,b) calls[#calls+1]=a..':'..b; return a<b end); " +
            "local bad={1,2,3,4}; local ok,e=pcall(table.sort,bad,function() return true end); " +
            "local emptyOk=pcall(table.sort,{},42); " +
            "return table.concat(t,','),table.concat(calls,','),ok,e,emptyOk");

        Assert.Equal("1,2,3,4", values[0].AsString().ToString());
        Assert.Equal("2:4,1:2,3:2,2:3,2:1,4:3", values[1].AsString().ToString());
        Assert.False(values[2].AsBoolean());
        Assert.Equal("invalid order function for sorting", values[3].AsString().ToString());
        Assert.True(values[4].AsBoolean());
    }

    [Fact]
    public void TableSortPreservesMetamethodOrderReentrancyAndYieldBarrier()
    {
        var values = Execute(
            "local b={4,1,3,2}; local log={} " +
            "local p=setmetatable({}, {__len=function() log[#log+1]='len'; return 4 end," +
            "__index=function(_,i) log[#log+1]='g'..i; return b[i] end," +
            "__newindex=function(_,i,v) log[#log+1]='s'..i..'='..v; b[i]=v end}) " +
            "table.sort(p,function(a,c) log[#log+1]='c'..a..'<'..c; return a<c end); " +
            "local outer={3,1,2}; local inner={2,1}; local nested=false " +
            "table.sort(outer,function(a,c) if not nested then nested=true; table.sort(inner) end return a<c end); " +
            "local co=coroutine.create(function() table.sort({2,1},function(a,c) coroutine.yield(); return a<c end) end); " +
            "local ok,e=coroutine.resume(co); " +
            "return table.concat(log,';'),table.concat(b,','),table.concat(outer,',')," +
            "table.concat(inner,','),ok,e~=nil");

        Assert.Equal(
            "len;g1;g4;c2<4;s1=2;s4=4;g2;g1;c1<2;s2=2;s1=1;g2;g3;" +
            "s2=3;s3=2;g2;c3<2;g2;c2<3;g1;c2<1;s3=3;s2=2;g3;g4;c4<3",
            values[0].AsString().ToString());
        Assert.Equal("1,2,3,4", values[1].AsString().ToString());
        Assert.Equal("1,2,3", values[2].AsString().ToString());
        Assert.Equal("1,2", values[3].AsString().ToString());
        Assert.False(values[4].AsBoolean());
        Assert.True(values[5].AsBoolean());
    }

    [Fact]
    public void TableSortHandlesLargeReverseOrderedArraysWithoutClrRecursion()
    {
        var values = Execute(
            "local t={} for i=1,4096 do t[i]=4097-i end table.sort(t) " +
            "local good=true for i=1,4096 do if t[i]~=i then good=false; break end end " +
            "return good,t[1],t[4096]");

        Assert.True(values[0].AsBoolean());
        Assert.Equal(1, values[1].AsInteger());
        Assert.Equal(4096, values[2].AsInteger());
    }

    [Fact]
    public void StringBytePatternAndIteratorFunctionsMatchLuaSemantics()
    {
        var values = Execute(
            "local a,b,c=string.byte('abc',1,3); " +
            "local s,e,x,y=string.find('id=42','(%a+)=(%d+)'); " +
            "local m=string.match('(a(b)c)','%b()'); local sum='' " +
            "for w in string.gmatch('one two','%a+') do sum=sum..w end " +
            "return a,b,c,string.char(65,0,255),string.sub('abcdef',-3,-1)," +
            "string.reverse('abc'),string.lower('A Z'),string.upper('a z'),s,e,x,y,m,sum");

        Assert.Equal(97, values[0].AsInteger());
        Assert.Equal(98, values[1].AsInteger());
        Assert.Equal(99, values[2].AsInteger());
        Assert.Equal(new byte[] { 65, 0, 255 }, values[3].AsString().ToArray());
        Assert.Equal("def", values[4].AsString().ToString());
        Assert.Equal("cba", values[5].AsString().ToString());
        Assert.Equal("a z", values[6].AsString().ToString());
        Assert.Equal("A Z", values[7].AsString().ToString());
        Assert.Equal(1, values[8].AsInteger());
        Assert.Equal(5, values[9].AsInteger());
        Assert.Equal("id", values[10].AsString().ToString());
        Assert.Equal("42", values[11].AsString().ToString());
        Assert.Equal("(a(b)c)", values[12].AsString().ToString());
        Assert.Equal("onetwo", values[13].AsString().ToString());
    }

    [Fact]
    public void GsubSupportsTemplatesTablesAndNonYieldableCallbacks()
    {
        var values = Execute(
            "local a,n=string.gsub('a1 b2','(%a)(%d)','%2%1'); " +
            "local b,m=string.gsub('a b','%a',{a='A'}); " +
            "local co=coroutine.create(function() return string.gsub('x','.',function() coroutine.yield(); return 'y' end) end); " +
            "local ok,e=coroutine.resume(co); return a,n,b,m,ok,e~=nil");

        Assert.Equal("1a 2b", values[0].AsString().ToString());
        Assert.Equal(2, values[1].AsInteger());
        Assert.Equal("A b", values[2].AsString().ToString());
        Assert.Equal(2, values[3].AsInteger());
        Assert.False(values[4].AsBoolean());
        Assert.True(values[5].AsBoolean());
    }

    [Fact]
    public void FormatAndPackRoundTripBinaryValues()
    {
        var values = Execute(
            "local packed=string.pack('<i4I2fdc3z',-12,65530,1.5,2.25,'xy','end'); " +
            "local a,b,c,d,e,f,next=string.unpack('<i4I2fdc3z',packed); " +
            "return string.format('%04d %#x %.2f %q',12,31,1.25,'a\\n')," +
            "a,b,c,d,e,f,next,#packed,string.packsize('<i4I2fdc3')");

        Assert.Equal("0012 0x1f 1.25 \"a\\\n\"", values[0].AsString().ToString());
        Assert.Equal(-12, values[1].AsInteger());
        Assert.Equal(65530, values[2].AsInteger());
        Assert.Equal(1.5, values[3].AsFloat());
        Assert.Equal(2.25, values[4].AsFloat());
        Assert.Equal(new byte[] { (byte)'x', (byte)'y', 0 }, values[5].AsString().ToArray());
        Assert.Equal("end", values[6].AsString().ToString());
        Assert.Equal(values[8].AsInteger() + 1, values[7].AsInteger());
        Assert.Equal(21, values[9].AsInteger());
    }

    [Fact]
    public void FormatUsesLuaTolStringWithPerInvocationNonYieldableState()
    {
        var values = Execute(
            "local nested='' local object=setmetatable({}, {__tostring=function() " +
            "nested=string.format('[%s]',setmetatable({}, {__tostring=function() return 'inner' end})); " +
            "return 'outer' end}) " +
            "local plain=string.format('a=%s b=%s',object,true); " +
            "local zero=setmetatable({}, {__tostring=function() return 'x\\0y' end}); " +
            "local raw=string.format('%s',zero); local modifiedOk,modifiedError=pcall(string.format,'%5s',zero); " +
            "local co=coroutine.create(function() return string.format('%s',setmetatable({}, {" +
            "__tostring=function() coroutine.yield(); return 'never' end})) end); " +
            "local yieldOk,yieldError=coroutine.resume(co); " +
            "return plain,nested,raw,modifiedOk,modifiedError,yieldOk,yieldError~=nil");

        Assert.Equal("a=outer b=true", values[0].AsString().ToString());
        Assert.Equal("[inner]", values[1].AsString().ToString());
        Assert.Equal(new byte[] { (byte)'x', 0, (byte)'y' }, values[2].AsString().ToArray());
        Assert.False(values[3].AsBoolean());
        Assert.Contains("string contains zeros", values[4].AsString().ToString());
        Assert.False(values[5].AsBoolean());
        Assert.True(values[6].AsBoolean());
    }

    [Fact]
    public void FormatCoversLua54IntegerFloatLiteralAndValidationRules()
    {
        var values = Execute(
            "local q=string.format('%q','\"\\\\\\n\\r\\0\\1'..'2'..string.char(31)..'x'..string.char(127)); " +
            "local qmod,qerr=pcall(string.format,'%1q','x'); " +
            "local bad,baderr=pcall(string.format,'%123d',1); " +
            "local bads,badserr=pcall(string.format,'%#s','x'); " +
            "return string.format('%.0d',0),string.format('%5.0d',0),string.format('%08.5d',12)," +
            "string.format('%u',-1),string.format('%#o',8),string.format('%#.0o',0)," +
            "string.format('%#08x',31),string.format('%8.4x',31),string.format('%#.0f',1)," +
            "string.format('%#.0g',1),string.format('%#.3g',12),string.format('%a',1.5)," +
            "string.format('%A',-0.0),q,string.format('%q',-9223372036854775807-1),qmod,qerr,bad,baderr,bads,badserr");

        Assert.Equal(string.Empty, values[0].AsString().ToString());
        Assert.Equal("     ", values[1].AsString().ToString());
        Assert.Equal("   00012", values[2].AsString().ToString());
        Assert.Equal("18446744073709551615", values[3].AsString().ToString());
        Assert.Equal("010", values[4].AsString().ToString());
        Assert.Equal(string.Empty, values[5].AsString().ToString());
        Assert.Equal("0x00001f", values[6].AsString().ToString());
        Assert.Equal("    001f", values[7].AsString().ToString());
        Assert.Equal("1.", values[8].AsString().ToString());
        Assert.Equal("1.", values[9].AsString().ToString());
        Assert.Equal("12.0", values[10].AsString().ToString());
        Assert.Equal("0x1.8p+0", values[11].AsString().ToString());
        Assert.Equal("-0X0P+0", values[12].AsString().ToString());
        Assert.Equal(
            new byte[]
            {
                34, 92, 34, 92, 92, 92, 10, 92, 49, 51, 92, 48, 92, 48, 48, 49, 50,
                92, 51, 49, 120, 92, 49, 50, 55, 34,
            },
            values[13].AsString().ToArray());
        Assert.Equal("0x8000000000000000", values[14].AsString().ToString());
        Assert.False(values[15].AsBoolean());
        Assert.Contains("specifier '%q' cannot have modifiers", values[16].AsString().ToString());
        Assert.False(values[17].AsBoolean());
        Assert.Contains("invalid conversion specification: '%123d'", values[18].AsString().ToString());
        Assert.False(values[19].AsBoolean());
        Assert.Contains("invalid conversion specification: '%#s'", values[20].AsString().ToString());
    }

    [Fact]
    public void PackAlignmentOptionConsumesExactlyItsImmediateSuccessor()
    {
        var values = Execute(
            "local spaced,spacedError=pcall(string.packsize,'X h'); " +
            "local repeated,repeatedError=pcall(string.packsize,'XXh'); " +
            "local fixed,fixedError=pcall(string.packsize,'Xc4'); " +
            "return string.packsize('!8bXh'),string.packsize('bXx'),string.packsize('Xi4B')," +
            "#string.pack('c0',''),spaced,spacedError,repeated,repeatedError,fixed,fixedError");

        Assert.Equal(2, values[0].AsInteger());
        Assert.Equal(1, values[1].AsInteger());
        Assert.Equal(1, values[2].AsInteger());
        Assert.Equal(0, values[3].AsInteger());
        Assert.False(values[4].AsBoolean());
        Assert.Contains("invalid next option for option 'X'", values[5].AsString().ToString());
        Assert.False(values[6].AsBoolean());
        Assert.Contains("invalid next option for option 'X'", values[7].AsString().ToString());
        Assert.False(values[8].AsBoolean());
        Assert.Contains("invalid next option for option 'X'", values[9].AsString().ToString());
    }

    [Fact]
    public void StringDumpRoundTripsCanonicalClosuresControlFlowVarargsAndNestedPrototypes()
    {
        var values = Execute(
            "local function f(a,...) local total=a " +
            "for i=1,3 do if i%2==0 then total=total+i else total=total-i end end " +
            "local t={1,named=4,...}; return total,t end " +
            "local copy=assert(load(string.dump(f))); local total,t=copy(10,'x','y') " +
            "local function factory(x) return function(y) return x+y end end " +
            "local factoryCopy=assert(load(string.dump(factory,true))); local add=factoryCopy(7) " +
            "local function chain() local function pass(...) return ... end " +
            "local function pair() return 'p','q' end return {0,pass(pair())} end " +
            "local chainCopy=assert(load(string.dump(chain))); local c=chainCopy() " +
            "local nativeOk,nativeError=pcall(string.dump,print) " +
            "local typeOk,typeError=pcall(string.dump,42) " +
            "return total,t[1],t[2],t[3],t.named,add(5),#string.dump(f)>0,c[1],c[2],c[3]," +
            "nativeOk,nativeError,typeOk,typeError");

        Assert.Equal(8, values[0].AsInteger());
        Assert.Equal(1, values[1].AsInteger());
        Assert.Equal("x", values[2].AsString().ToString());
        Assert.Equal("y", values[3].AsString().ToString());
        Assert.Equal(4, values[4].AsInteger());
        Assert.Equal(12, values[5].AsInteger());
        Assert.True(values[6].AsBoolean());
        Assert.Equal(0, values[7].AsInteger());
        Assert.Equal("p", values[8].AsString().ToString());
        Assert.Equal("q", values[9].AsString().ToString());
        Assert.False(values[10].AsBoolean());
        Assert.Equal("unable to dump given function", values[11].AsString().ToString());
        Assert.False(values[12].AsBoolean());
        Assert.Contains("function expected, got number", values[13].AsString().ToString());
    }

    [Fact]
    public void PatternVmRestoresCapturesAndCoversEmptyNulBracketAndMalformedCases()
    {
        var values = Execute(
            "local positions={} for p in string.gmatch('ab','()') do positions[#positions+1]=p end " +
            "local inserted,n=string.gsub('ab','()','<>'); " +
            "local backed=string.match('aaa','(a*)a%1'); " +
            "local bracket=string.match('x]y','[]]'); local nul=string.match('a\\0\\0b','%z+'); " +
            "local zero,zeroError=pcall(string.match,'abc','%0'); " +
            "local unfinished,unfinishedError=pcall(string.match,'abc','('); " +
            "local missing,missingError=pcall(string.match,'abc','[abc'); " +
            "return table.concat(positions,','),inserted,n,backed,bracket,nul,zero,zeroError," +
            "unfinished,unfinishedError,missing,missingError");

        Assert.Equal("1,2,3", values[0].AsString().ToString());
        Assert.Equal("<>a<>b<>", values[1].AsString().ToString());
        Assert.Equal(3, values[2].AsInteger());
        Assert.Equal("a", values[3].AsString().ToString());
        Assert.Equal("]", values[4].AsString().ToString());
        Assert.Equal(new byte[] { 0, 0 }, values[5].AsString().ToArray());
        Assert.False(values[6].AsBoolean());
        Assert.Equal("invalid capture index %0", values[7].AsString().ToString());
        Assert.False(values[8].AsBoolean());
        Assert.Equal("unfinished capture", values[9].AsString().ToString());
        Assert.False(values[10].AsBoolean());
        Assert.Equal("malformed pattern (missing ']')", values[11].AsString().ToString());
    }

    private static LuaValue[] Execute(string source)
    {
        var state = new LuaState();
        LuaStandardLibrary.InstallBasic(state);
        LuaStandardLibrary.InstallCoroutine(state);
        LuaStandardLibrary.InstallTable(state);
        LuaStandardLibrary.InstallString(state);
        var lowering = LuaLowerer.Lower(
            LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source))));
        Assert.Empty(lowering.Diagnostics);
        return new LuaInterpreter()
            .Execute(state, state.CreateMainClosure(lowering.Module!))
            .Values
            .ToArray();
    }
}
