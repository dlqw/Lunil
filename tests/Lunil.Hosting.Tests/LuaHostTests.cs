using System.Text;
using Lunil.Runtime.Execution;

namespace Lunil.Hosting.Tests;

public sealed class LuaHostTests
{
    [Fact]
    public void RunCompilesAndExecutesThroughOnePublicBoundary()
    {
        var host = new LuaHost();

        var result = host.RunUtf8(
            "local total=0; for i=1,10 do total=total+i end; return total",
            "@scripts/sum.lua");

        Assert.True(result.Succeeded);
        Assert.True(result.CompilationSucceeded);
        Assert.True(result.ExecutionStarted);
        Assert.Equal(LuaVmSignal.Completed, result.Execution!.Signal);
        Assert.Equal(55, Assert.Single(result.Execution.Values).AsInteger());
    }

    [Fact]
    public void ReusableHostPreservesStateAcrossCompilations()
    {
        var host = new LuaHost();

        var first = host.RunUtf8("answer=41");
        var second = host.RunUtf8("return answer+1");

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(42, Assert.Single(second.Execution!.Values).AsInteger());
    }

    [Fact]
    public void CompileFailureDoesNotStartExecution()
    {
        var host = new LuaHost();

        var result = host.RunUtf8("local =");

        Assert.False(result.Succeeded);
        Assert.False(result.CompilationSucceeded);
        Assert.False(result.ExecutionStarted);
        Assert.Null(result.Execution);
    }

    [Fact]
    public void RestrictedProfileDeniesFileSystemAndCapturesConsole()
    {
        var host = new LuaHost(LuaHostOptions.Restricted);

        var result = host.RunUtf8(
            "local file,err=io.open('secret','r'); print('sandbox'); " +
            "return file==nil,err~=nil,os.getenv('HOME')==nil");

        Assert.True(result.Succeeded);
        Assert.All(result.Execution!.Values, value => Assert.True(value.AsBoolean()));
        Assert.NotNull(host.BufferedConsole);
        Assert.Equal("sandbox\n", Encoding.UTF8.GetString(
            host.BufferedConsole!.GetStandardOutput()));
    }

    [Fact]
    public void DeterministicProfileFixesTimeZoneClockAndHashSeed()
    {
        var first = new LuaHost(LuaHostOptions.Deterministic);
        var second = new LuaHost(LuaHostOptions.Deterministic);

        var result = first.RunUtf8(
            "return os.clock(),os.time(),os.date('!%Y-%m-%d %H:%M:%S',os.time())");

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Execution!.Values[0].AsFloat());
        Assert.Equal(0, result.Execution.Values[1].AsInteger());
        Assert.Equal("1970-01-01 00:00:00", result.Execution.Values[2].AsString().ToString());
        Assert.Equal(0, first.State.Heap.HashSeed);
        Assert.Equal(first.State.Heap.HashSeed, second.State.Heap.HashSeed);
    }

    [Fact]
    public void CustomCapabilitiesOverrideProfileDefaults()
    {
        var console = new LuaBufferedConsole();
        var options = LuaHostOptions.Deterministic with
        {
            StandardLibrary = LuaHostCapabilityProfiles.Create(LuaHostProfile.Restricted) with
            {
                Console = console,
            },
        };
        var host = new LuaHost(options);

        var result = host.RunUtf8("print('custom')");

        Assert.True(result.Succeeded);
        Assert.Same(console, host.BufferedConsole);
        Assert.Equal("custom\n", Encoding.UTF8.GetString(console.GetStandardOutput()));
    }
}
