using System.Text;
using Lunil.CodeGen.Cil.Jit;
using Lunil.IR.Lua54;
using Lunil.Runtime.Execution;
using Lunil.Workspace;

namespace Lunil.Hosting.Tests;

public sealed class LuaHostTests
{
    [Fact]
    public void RunCompilesAndExecutesThroughOnePublicBoundary()
    {
        using var host = new LuaHost();

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
        using var host = new LuaHost();

        var first = host.RunUtf8("answer=41");
        var second = host.RunUtf8("return answer+1");

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(42, Assert.Single(second.Execution!.Values).AsInteger());
    }

    [Fact]
    public void CompileFailureDoesNotStartExecution()
    {
        using var host = new LuaHost();

        var result = host.RunUtf8("local =");

        Assert.False(result.Succeeded);
        Assert.False(result.CompilationSucceeded);
        Assert.False(result.ExecutionStarted);
        Assert.Null(result.Execution);
    }

    [Fact]
    public void RestrictedProfileDeniesFileSystemAndCapturesConsole()
    {
        using var host = new LuaHost(LuaHostOptions.Restricted);

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
        using var first = new LuaHost(LuaHostOptions.Deterministic);
        using var second = new LuaHost(LuaHostOptions.Deterministic);

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
        using var host = new LuaHost(options);

        var result = host.RunUtf8("print('custom')");

        Assert.True(result.Succeeded);
        Assert.Same(console, host.BufferedConsole);
        Assert.Equal("custom\n", Encoding.UTF8.GetString(console.GetStandardOutput()));
    }

    [Fact]
    public void AnnotationsAreErasedFromRuntimeExecution()
    {
        using var host = new LuaHost();

        var result = host.RunUtf8("---@type string\nreturn 42");

        Assert.True(result.Succeeded);
        Assert.Equal(42, Assert.Single(result.Execution!.Values).AsInteger());
        Assert.Single(result.Compilation.Annotations.Annotations);
        Assert.Single(result.Compilation.Analysis.Functions);
        Assert.NotEmpty(result.Compilation.Analysis.Expressions);
    }

    [Fact]
    public async Task HostPublishesReusableWorkspaceAnalysisBoundary()
    {
        using var host = new LuaHost();

        var result = await host.AnalyzeWorkspaceAsync([
            LuaWorkspaceDocument.FromUtf8(
                "app",
                "local dep = require('dep')\nreturn dep.value + 1"),
            LuaWorkspaceDocument.FromUtf8("dep", "return { value = 41 }"),
        ]);

        Assert.True(result.Succeeded);
        Assert.Equal("integer", result.GetModule("app")!.ExportedType.DisplayName);
        Assert.Equal(2, result.Graph.Nodes.Length);
    }

    [Fact]
    public void InterpreterBackendRemainsAnExplicitDeterministicOptOut()
    {
        using var host = new LuaHost(LuaHostOptions.Default with
        {
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
        });

        var result = host.RunUtf8("return 6 * 7");

        Assert.True(result.Succeeded);
        Assert.Equal(LuaHostExecutionBackend.Interpreter, host.SelectedExecutionBackend);
        Assert.Null(host.JitStatistics);
        Assert.Equal(42, Assert.Single(result.Execution!.Values).AsInteger());
    }

    [Fact]
    public void QualifiedJitBackendCompilesEligibleCodeAndPreservesBudgets()
    {
        using var host = new LuaHost(LuaHostOptions.Default with
        {
            ExecutionBackend = LuaHostExecutionBackend.Jit,
            Execution = LuaInterpreterOptions.Default with
            {
                MaximumInstructionCount = 10_000,
            },
            Jit = LuaJitExecutorOptions.Default with
            {
                FunctionEntryThreshold = 1,
                BackedgeThreshold = 1,
                SynchronousCompilation = true,
                EnableTier2 = false,
                EnableLoopOsr = false,
            },
        });
        Assert.Null(host.JitStatistics);

        var result = host.RunUtf8(
            "local total=0; for i=1,100 do total=total+i end; return total");

        Assert.True(result.Succeeded);
        Assert.Equal(LuaHostExecutionBackend.Jit, host.SelectedExecutionBackend);
        Assert.True(host.IsDynamicCodeAvailable);
        Assert.NotNull(host.JitStatistics);
        Assert.True(host.JitStatistics!.CompilationCompleted > 0);
        Assert.True(host.JitStatistics.CompiledInvocations > 0);
        Assert.Equal(5_050, Assert.Single(result.Execution!.Values).AsInteger());
        Assert.Throws<Lunil.Runtime.LuaRuntimeException>(() => host.RunUtf8("while true do end"));
    }

    [Fact]
    public void DumpLoadPreservesConstantLeftMetamethodOperandOrder()
    {
        const string source = """
            local rhs
            local mt = {
                __add = function(left, right)
                    return left == 5.25 and right == rhs
                end,
            }
            rhs = setmetatable({}, mt)
            return 5.25 + rhs
            """;
        using var host = new LuaHost(LuaHostOptions.Default with
        {
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
        });
        var compilation = host.CompileUtf8(source, "@metamethod-order.lua");
        Assert.True(compilation.Succeeded);
        var module = Assert.IsType<Lunil.IR.Canonical.LuaIrModule>(compilation.Module);
        var binary = Lua54CanonicalPrototypeWriter.Write(module, module.MainFunctionId);

        var result = host.ExecuteBinaryChunk(binary);

        Assert.Equal(LuaVmSignal.Completed, result.Signal);
        Assert.True(Assert.Single(result.Values).AsBoolean());
    }
}
