using Lunil.Core.Text;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.StandardLibrary.Tests;

public sealed class LuaOsLibraryTests
{
    [Fact]
    public void SystemOperatingSystemOnlyAdvertisesTheImplementedPortableLocale()
    {
        Assert.Equal("C", SystemLuaOperatingSystem.Instance.SetLocale(null, "all"));
        Assert.Equal("C", SystemLuaOperatingSystem.Instance.SetLocale("", "numeric"));
        Assert.Equal("C", SystemLuaOperatingSystem.Instance.SetLocale("C", "time"));
        Assert.Null(SystemLuaOperatingSystem.Instance.SetLocale("pt_BR", "all"));
    }

    [Fact]
    public void TimeDateDifferenceAndLocaleUseInjectedOperatingSystem()
    {
        var operatingSystem = new MemoryOperatingSystem
        {
            Now = new DateTimeOffset(2024, 2, 29, 13, 5, 7, TimeSpan.Zero),
        };
        var values = Execute(new MemoryFileSystem(), operatingSystem,
            "local now=os.time(); local t=os.date('!*t',now); " +
            "local x={year=2023,month=13,day=1,hour=0}; local stamp=os.time(x); " +
            "return now,t.year,t.month,t.day,t.wday,t.yday,t.isdst," +
            "os.date('!%Y-%m-%d %H:%M:%S %j %u %%',now)," +
            "os.difftime(now,now-2),x.year,x.month,x.day,os.setlocale(nil,'time')");

        Assert.Equal(operatingSystem.Now.ToUnixTimeSeconds(), values[0].AsInteger());
        Assert.Equal(2024, values[1].AsInteger());
        Assert.Equal(2, values[2].AsInteger());
        Assert.Equal(29, values[3].AsInteger());
        Assert.Equal(5, values[4].AsInteger());
        Assert.Equal(60, values[5].AsInteger());
        Assert.False(values[6].AsBoolean());
        Assert.Equal("2024-02-29 13:05:07 060 4 %", values[7].AsString().ToString());
        Assert.Equal(2, values[8].AsFloat());
        Assert.Equal(2024, values[9].AsInteger());
        Assert.Equal(1, values[10].AsInteger());
        Assert.Equal(1, values[11].AsInteger());
        Assert.Equal("C", values[12].AsString().ToString());
    }

    [Fact]
    public void ExecuteEnvironmentRenameRemoveAndTemporaryNamePreserveLuaTuples()
    {
        var files = new MemoryFileSystem();
        files.Files["old"] = [1];
        var operatingSystem = new MemoryOperatingSystem();
        var values = Execute(files, operatingSystem,
            "local shell=os.execute(); local ok,kind,status=os.execute('good'); " +
            "local bad,bkind,bstatus=os.execute('bad'); local rn=os.rename('old','new'); " +
            "local rm=os.remove('new'); return shell,ok,kind,status,bad,bkind,bstatus," +
            "rn,rm,os.getenv('HOME'),os.getenv('ABSENT'),os.tmpname()");

        Assert.True(values[0].AsBoolean());
        Assert.True(values[1].AsBoolean());
        Assert.Equal("exit", values[2].AsString().ToString());
        Assert.Equal(0, values[3].AsInteger());
        Assert.True(values[4].IsNil);
        Assert.Equal("exit", values[5].AsString().ToString());
        Assert.Equal(7, values[6].AsInteger());
        Assert.True(values[7].AsBoolean());
        Assert.True(values[8].AsBoolean());
        Assert.Equal("/home/test", values[9].AsString().ToString());
        Assert.True(values[10].IsNil);
        Assert.Equal("tmp-1", values[11].AsString().ToString());
    }

    [Fact]
    public void TimeUsesNonYieldableIndexAndNewIndexCallbackStateMachine()
    {
        var values = Execute(new MemoryFileSystem(), new MemoryOperatingSystem
        {
            Now = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
        },
            "local source={year=2024,month=2,day=29,hour=1,min=2,sec=3} " +
            "local writes={} local proxy=setmetatable({}, {" +
            "__index=function(_,k) return source[k] end," +
            "__newindex=function(_,k,v) writes[k]=v end}) " +
            "local stamp=os.time(proxy) " +
            "local co=coroutine.create(function() local p=setmetatable({}, {" +
            "__index=function() coroutine.yield() end}); os.time(p) end) " +
            "local ok,err=coroutine.resume(co) " +
            "return os.date('!%Y-%m-%d %H:%M:%S',stamp),writes.year,writes.yday," +
            "writes.isdst,ok,err~=nil");

        Assert.Equal("2024-02-29 01:02:03", values[0].AsString().ToString());
        Assert.Equal(2024, values[1].AsInteger());
        Assert.Equal(60, values[2].AsInteger());
        Assert.False(values[3].AsBoolean());
        Assert.False(values[4].AsBoolean());
        Assert.True(values[5].AsBoolean());
    }

    private static LuaValue[] Execute(
        MemoryFileSystem files,
        MemoryOperatingSystem operatingSystem,
        string source)
    {
        var state = new LuaState();
        LuaStandardLibrary.InstallBasic(state, new LuaStandardLibraryOptions
        {
            FileSystem = files,
            OperatingSystem = operatingSystem,
            Environment = new MemoryEnvironment(),
        });
        LuaStandardLibrary.InstallOs(state);
        LuaStandardLibrary.InstallCoroutine(state);
        var lowering = LuaLowerer.Lower(
            LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source))));
        Assert.Empty(lowering.Diagnostics);
        return new LuaInterpreter().Execute(state, state.CreateMainClosure(lowering.Module!))
            .Values.ToArray();
    }

    private sealed class MemoryOperatingSystem : ILuaOperatingSystem
    {
        public double Clock => 12.5;
        public DateTimeOffset Now { get; set; }
        public TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public LuaExecuteResult Execute(string? command) => command switch
        {
            null => new(true, "exit", 0),
            "good" => new(true, "exit", 0),
            _ => new(true, "exit", 7),
        };
        public Stream OpenPipe(string command, bool read, out ILuaPipeProcess process) =>
            throw new NotSupportedException();
        public void Terminate(int status, bool closeState) { }
        public string? SetLocale(string? locale, string category) => "C";
    }

    private sealed class MemoryFileSystem : ILuaFileSystem
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public byte[] ReadAllBytes(string path) => Files[path];
        public bool FileExists(string path) => Files.ContainsKey(path);
        public string CreateTemporaryName() => "tmp-1";
        public void Delete(string path)
        {
            if (!Files.Remove(path))
            {
                throw new FileNotFoundException(path);
            }
        }
        public void Move(string source, string destination)
        {
            Files[destination] = Files[source];
            Files.Remove(source);
        }
    }

    private sealed class MemoryEnvironment : ILuaEnvironment
    {
        public string? GetEnvironmentVariable(string name) => name == "HOME" ? "/home/test" : null;
    }
}
