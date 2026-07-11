using System.Text;
using Lunil.Core.Text;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.StandardLibrary.Tests;

public sealed class LuaBasicLibraryTests
{
    [Fact]
    public void InstallsGlobalsConversionsRawOperationsAndSelect()
    {
        var values = Execute(
            "local t={1,2}; rawset(t,'x',3); " +
            "return _G==_G,_VERSION,type(1),type(1.0),tostring(1.0)," +
            "tonumber('0x10'),tonumber('ff',16),select('#',1,nil,3)," +
            "select(-1,1,nil,3),rawget(t,'x'),rawlen(t),rawequal(t,t)");

        Assert.True(values[0].AsBoolean());
        Assert.Equal("Lua 5.4", values[1].AsString().ToString());
        Assert.Equal("number", values[2].AsString().ToString());
        Assert.Equal("number", values[3].AsString().ToString());
        Assert.Equal("1.0", values[4].AsString().ToString());
        Assert.Equal(16, values[5].AsInteger());
        Assert.Equal(255, values[6].AsInteger());
        Assert.Equal(3, values[7].AsInteger());
        Assert.Equal(3, values[8].AsInteger());
        Assert.Equal(3, values[9].AsInteger());
        Assert.Equal(2, values[10].AsInteger());
        Assert.True(values[11].AsBoolean());
    }

    [Fact]
    public void AssertErrorsAndProtectedMetatablesMatchLuaBehavior()
    {
        var values = Execute(
            "local mt={__metatable='locked'}; local t=setmetatable({},mt); " +
            "local ok1,e1=pcall(assert,false,'boom'); " +
            "local ok2,e2=pcall(setmetatable,t,{}); " +
            "return ok1,e1,ok2,e2~=nil,getmetatable(t)");

        Assert.False(values[0].AsBoolean());
        Assert.Equal("boom", values[1].AsString().ToString());
        Assert.False(values[2].AsBoolean());
        Assert.True(values[3].AsBoolean());
        Assert.Equal("locked", values[4].AsString().ToString());
    }

    [Fact]
    public void PairsAndIpairsHonorLua54MetamethodRules()
    {
        var values = Execute(
            "local object=setmetatable({}, {" +
            "__pairs=function() return next,{a=4},nil end," +
            "__index=function(_,i) if i<=2 then return i*3 end end}); " +
            "local pairValue=0; for _,v in pairs(object) do pairValue=pairValue+v end; " +
            "local arrayValue=0; for _,v in ipairs(object) do arrayValue=arrayValue+v end; " +
            "return pairValue,arrayValue");

        Assert.Equal([LuaValue.FromInteger(4), LuaValue.FromInteger(9)], values);
    }

    [Fact]
    public void PairsMetamethodCanYieldButTostringCannot()
    {
        var state = CreateState();
        state.InstallCoroutineModule();
        var values = Execute(
            state,
            "local pairObject=setmetatable({}, {__pairs=function() " +
            "coroutine.yield('pause'); return next,{a=5},nil end}); " +
            "local pairCo=coroutine.create(function() local sum=0; " +
            "for _,v in pairs(pairObject) do sum=sum+v end; return sum end); " +
            "local r1,y=coroutine.resume(pairCo); local r2,sum=coroutine.resume(pairCo); " +
            "local stringObject=setmetatable({}, {__tostring=function() coroutine.yield() end}); " +
            "local stringCo=coroutine.create(function() local ok,e=pcall(tostring,stringObject); " +
            "return ok,e~=nil end); local r3,ok,hasError=coroutine.resume(stringCo); " +
            "return r1,y,r2,sum,r3,ok,hasError");

        Assert.Equal(
            [
                LuaValue.FromBoolean(true),
                LuaValue.FromString(state.Strings.GetOrCreate("pause"u8)),
                LuaValue.FromBoolean(true),
                LuaValue.FromInteger(5),
                LuaValue.FromBoolean(true),
                LuaValue.FromBoolean(false),
                LuaValue.FromBoolean(true),
            ],
            values);
    }

    [Fact]
    public void TostringAndPrintCallMetamethodAndUseNonYieldableBoundary()
    {
        var console = new RecordingConsole();
        var state = CreateState(console: console);
        var values = Execute(
            state,
            "local value=setmetatable({}, {__tostring=function() return 'custom' end}); " +
            "print('a',value,2.0); return tostring(value)");

        Assert.Equal("custom", values[0].AsString().ToString());
        Assert.Equal("a\tcustom\t2.0\n", Encoding.UTF8.GetString(console.Output.ToArray()));
    }

    [Fact]
    public void ProtectedCallsSupportTailPositionResumableNativeTargets()
    {
        var values = Execute(
            "local good=setmetatable({}, {__tostring=function() return 'ok' end}); " +
            "local bad=setmetatable({}, {__tostring=function() return nil end}); " +
            "local function success() return pcall(tostring,good) end; " +
            "local function failure() return xpcall(tostring,function() return 'handled' end,bad) end; " +
            "local a,b=success(); local c,d=failure(); return a,b,c,d");

        Assert.True(values[0].AsBoolean());
        Assert.Equal("ok", values[1].AsString().ToString());
        Assert.False(values[2].AsBoolean());
        Assert.Equal("handled", values[3].AsString().ToString());
    }

    [Fact]
    public void LoadSupportsTextEnvironmentAndNonYieldableReader()
    {
        var values = Execute(
            "local f,e=load('return x+1','chunk','t',{x=4}); " +
            "local parts={'return ', '6*7'}; local i=0; " +
            "local g,ge=load(function() i=i+1; return parts[i] end); " +
            "return f(),e,g(),ge");

        Assert.Equal(5, values[0].AsInteger());
        Assert.True(values[1].IsNil);
        Assert.Equal(42, values[2].AsInteger());
        Assert.True(values[3].IsNil);
    }

    [Fact]
    public void LoadReaderCannotYieldAcrossNativeReaderBoundary()
    {
        var state = CreateState();
        state.InstallCoroutineModule();
        var values = Execute(
            state,
            "local co=coroutine.create(function() local ok,e=pcall(load,function() " +
            "coroutine.yield('bad'); return nil end); return ok,e~=nil end); " +
            "return coroutine.resume(co)");

        Assert.Equal(
            [LuaValue.FromBoolean(true), LuaValue.FromBoolean(false), LuaValue.FromBoolean(true)],
            values);
    }

    [Fact]
    public void LoadFileAndDoFileUseInjectedFileSystem()
    {
        var fileSystem = new RecordingFileSystem(new Dictionary<string, byte[]>
        {
            ["value.lua"] = "return 21*2"u8.ToArray(),
        });
        var state = CreateState(fileSystem: fileSystem);

        var values = Execute(
            state,
            "local f,e=loadfile('value.lua'); return f(),e,dofile('value.lua')");

        Assert.Equal(42, values[0].AsInteger());
        Assert.True(values[1].IsNil);
        Assert.Equal(42, values[2].AsInteger());
        Assert.Equal(["value.lua", "value.lua"], fileSystem.ReadPaths);
    }

    [Fact]
    public void WarnAndCollectGarbageExposeStateServices()
    {
        var state = CreateState();
        LuaValue warning = default;
        state.WarningRaised += value => warning = value;

        var values = Execute(
            state,
            "warn('@on'); warn('hello',12); collectgarbage('stop'); " +
            "local stopped=collectgarbage('isrunning'); " +
            "collectgarbage('restart'); return stopped,collectgarbage('isrunning')," +
            "collectgarbage('generational')");

        Assert.Equal("hello12", warning.AsString().ToString());
        Assert.False(values[0].AsBoolean());
        Assert.True(values[1].AsBoolean());
        Assert.Equal("incremental", values[2].AsString().ToString());
    }

    private static LuaValue[] Execute(string source) => Execute(CreateState(), source);

    private static LuaState CreateState(
        ILuaFileSystem? fileSystem = null,
        ILuaConsole? console = null)
    {
        var state = new LuaState();
        LuaStandardLibrary.InstallBasic(
            state,
            new LuaStandardLibraryOptions
            {
                FileSystem = fileSystem ?? new RecordingFileSystem(
                    new Dictionary<string, byte[]>()),
                Console = console ?? new RecordingConsole(),
            });
        return state;
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

    private sealed class RecordingFileSystem(IReadOnlyDictionary<string, byte[]> files)
        : ILuaFileSystem
    {
        public List<string> ReadPaths { get; } = [];

        public byte[] ReadAllBytes(string path)
        {
            ReadPaths.Add(path);
            return files.TryGetValue(path, out var bytes)
                ? bytes.ToArray()
                : throw new FileNotFoundException(path);
        }
    }

    private sealed class RecordingConsole : ILuaConsole
    {
        public MemoryStream Output { get; } = new();

        public byte[] StandardInput { get; init; } = [];

        public byte[] ReadStandardInput() => StandardInput.ToArray();

        public void Write(ReadOnlyMemory<byte> bytes) => Output.Write(bytes.Span);

        public void WriteLine() => Output.WriteByte((byte)'\n');
    }
}
