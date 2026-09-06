using System.IO;
using Lunil.Hosting;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.Hosting.Tests;

public sealed class LuaHostBackendResolutionTests
{
    [Theory]
    [InlineData(LuaHostExecutionBackend.Auto, LuaHostJitPolicy.InterpreterOnly, false, LuaHostExecutionBackend.Interpreter)]
    [InlineData(LuaHostExecutionBackend.Auto, LuaHostJitPolicy.Auto, true, LuaHostExecutionBackend.Jit)]
    [InlineData(LuaHostExecutionBackend.Auto, LuaHostJitPolicy.Auto, false, LuaHostExecutionBackend.Interpreter)]
    [InlineData(LuaHostExecutionBackend.Auto, LuaHostJitPolicy.PreferJit, true, LuaHostExecutionBackend.Jit)]
    [InlineData(LuaHostExecutionBackend.Auto, LuaHostJitPolicy.PreferJit, false, LuaHostExecutionBackend.Interpreter)]
    [InlineData(LuaHostExecutionBackend.Auto, LuaHostJitPolicy.RequireJit, true, LuaHostExecutionBackend.Jit)]
    [InlineData(LuaHostExecutionBackend.Interpreter, LuaHostJitPolicy.Auto, false, LuaHostExecutionBackend.Interpreter)]
    [InlineData(LuaHostExecutionBackend.Interpreter, LuaHostJitPolicy.RequireJit, false, LuaHostExecutionBackend.Interpreter)]
    [InlineData(LuaHostExecutionBackend.Jit, LuaHostJitPolicy.RequireJit, true, LuaHostExecutionBackend.Jit)]
    public void ResolvesTheExpectedBackend(
        LuaHostExecutionBackend requested,
        LuaHostJitPolicy policy,
        bool isDynamicCodeAvailable,
        LuaHostExecutionBackend expected)
    {
        var resolved = LuaHost.ResolveExecutionBackend(requested, isDynamicCodeAvailable, policy);

        Assert.Equal(expected, resolved);
    }

    [Theory]
    [InlineData(LuaHostExecutionBackend.Auto, LuaHostJitPolicy.RequireJit, false)]
    [InlineData(LuaHostExecutionBackend.Jit, LuaHostJitPolicy.Auto, false)]
    [InlineData(LuaHostExecutionBackend.Jit, LuaHostJitPolicy.RequireJit, false)]
    public void RejectsJitRequirementsWithoutDynamicCodeSupport(
        LuaHostExecutionBackend requested,
        LuaHostJitPolicy policy,
        bool isDynamicCodeAvailable)
    {
        Assert.Throws<PlatformNotSupportedException>(
            () => LuaHost.ResolveExecutionBackend(requested, isDynamicCodeAvailable, policy));
    }

    [Fact]
    public void RequireJitPolicyResolvesToTheJitBackendOnThisRuntime()
    {
        using var host = new LuaHost(LuaHostOptions.Default with
        {
            ExecutionBackend = LuaHostExecutionBackend.Auto,
            Jit = new LuaHostJitOptions { Policy = LuaHostJitPolicy.RequireJit },
        });

        Assert.Equal(LuaHostExecutionBackend.Jit, host.SelectedExecutionBackend);
    }

    [Fact]
    public void PcallContainsAClrExceptionThrownByAHostNativeFunction()
    {
        using var host = new LuaHost(LuaHostOptions.Default with
        {
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
        });

        host.State.SetGlobal(
            "host_boom",
            LuaValue.FromFunction(new LuaNativeFunction(
                "host_boom",
                static (_, _) => throw new IOException("host disk full"))));

        var result = host.RunUtf8("""
            local ok, err = pcall(host_boom)
            local ok2 = pcall(function() return 1 end)
            return ok, err, ok2
            """);

        Assert.True(result.Succeeded, result.Execution?.ToString());
        var values = result.Execution!.Values;
        Assert.Equal(LuaValue.FromBoolean(false), values[0]);
        Assert.Equal(
            "System.IO.IOException: host disk full",
            values[1].AsString().ToString());
        Assert.Equal(LuaValue.FromBoolean(true), values[2]);
    }
}
