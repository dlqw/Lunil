using System.Diagnostics.CodeAnalysis;
using Lunil.CodeGen.Cil;
using Lunil.CodeGen.Cil.Jit;
using Lunil.Compiler;
using Lunil.Hosting;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.Workspace;

namespace Lunil.NativeAot.Fixture;

public static class Program
{
    public static int Main()
    {
        var cases = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["precompiled"] = 55,
            ["closures"] = 42,
            ["control"] = 42,
            ["multireturn"] = 42,
            ["tables"] = 42,
        };

        var interpreter = new LuaInterpreter();
        foreach (var testCase in cases)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Modules", testCase.Key + ".lua");
            var value = Execute(interpreter, Compile(File.ReadAllText(path)));
            if (value != testCase.Value)
            {
                Console.Error.WriteLine(
                    $"Unexpected interpreter result for {testCase.Key}: expected={testCase.Value}, actual={value}.");
                return 2;
            }
        }

        var dynamicValue = Execute(
            interpreter,
            Compile("---@type integer\nreturn 21 * 2", requireAnalysis: true));
        if (dynamicValue != 42)
        {
            Console.Error.WriteLine($"Unexpected dynamic fallback result: {dynamicValue}.");
            return 2;
        }

        using (var workspace = new LuaWorkspace())
        {
            var workspaceResult = workspace.AnalyzeAsync([
                LuaWorkspaceDocument.FromUtf8(
                    "fixture.app",
                    "local dep = require('fixture.dep')\nreturn dep.value + 1"),
                LuaWorkspaceDocument.FromUtf8(
                    "fixture.dep",
                    "return { value = 41 }"),
            ]).GetAwaiter().GetResult();
            if (!workspaceResult.Succeeded ||
                workspaceResult.Graph.Nodes.Length != 2 ||
                workspaceResult.GetModule("fixture.app")?.ExportedType.DisplayName != "integer")
            {
                Console.Error.WriteLine("Incremental workspace analysis is invalid.");
                return 2;
            }
        }

        using var defaultJit = new LuaJitExecutor();
        if (defaultJit.Options.Policy != LuaJitPolicy.Auto ||
            !defaultJit.Options.EnableTier2 ||
            defaultJit.Options.EnableTier2ManagedFallback ||
            !defaultJit.Options.EnableLoopOsr ||
            defaultJit.Options.EnableLoopOsrManagedFallback)
        {
            Console.Error.WriteLine("The default JIT rollout policy is invalid.");
            return 3;
        }

        using var jit = new LuaJitExecutor(new LuaJitExecutorOptions
        {
            Policy = LuaJitPolicy.PreferJit,
            SynchronousCompilation = true,
        });
        if (System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
        {
            if (!jit.IsDynamicCodeAvailable)
            {
                Console.Error.WriteLine("CoreCLR JIT capability was not detected.");
                return 4;
            }
        }
        else
        {
            var fallbackModule = Compile("return 6 * 7");
            if (defaultJit.IsDynamicCodeAvailable ||
                Execute(defaultJit, fallbackModule) != 42 ||
                defaultJit.GetTier2State(fallbackModule, 0) != LuaJitTier2State.Disabled ||
                defaultJit.GetFunctionProfile(fallbackModule, 0).Samples != 0 ||
                jit.IsDynamicCodeAvailable ||
                Execute(jit, Compile("return 6 * 7")) != 42)
            {
                Console.Error.WriteLine("NativeAOT JIT fallback policy is invalid.");
                return 5;
            }
        }

        if (!VerifyClrInterop())
        {
            Console.Error.WriteLine("The preserved CLR interoperation contract is invalid.");
            return 6;
        }

        if (!VerifyReplayStore())
        {
            Console.Error.WriteLine("The NativeAOT replay-store contract is invalid.");
            return 7;
        }

        Console.WriteLine("LUNIL_NATIVEAOT_OK");
        return 0;
    }

    private static long Execute(LuaInterpreter executor, LuaIrModule module)
    {
        var state = new LuaState();
        var result = executor.Execute(state, state.CreateMainClosure(module));
        return result.Values[0].AsInteger();
    }

    private static long Execute(LuaJitExecutor executor, LuaIrModule module)
    {
        var state = new LuaState();
        var result = executor.Execute(state, state.CreateMainClosure(module));
        return result.Values[0].AsInteger();
    }

    private static LuaIrModule Compile(string source, bool requireAnalysis = false)
    {
        var compilation = new LuaCompiler().CompileUtf8(source, "=nativeaot-fixture");
        if (requireAnalysis && compilation.Analysis.Expressions.IsEmpty)
        {
            throw new InvalidOperationException("Static analysis was not published.");
        }

        return compilation.Module ?? throw new InvalidOperationException(
            string.Join(
                "; ",
                compilation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }

    [DynamicDependency(
        DynamicallyAccessedMemberTypes.PublicConstructors |
        DynamicallyAccessedMemberTypes.PublicMethods |
        DynamicallyAccessedMemberTypes.PublicProperties,
        typeof(ClrFixtureValue))]
    private static bool VerifyClrInterop()
    {
        var typeName = typeof(ClrFixtureValue).FullName!;
        using var host = new LuaHost(new LuaHostOptions
        {
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
            Clr = new LuaClrOptions
            {
                Capabilities = LuaClrCapabilities.TypeDiscovery | LuaClrCapabilities.Construction |
                    LuaClrCapabilities.MemberAccess,
                AllowedAssemblyNames = [typeof(ClrFixtureValue).Assembly.GetName().Name!],
                AllowedTypeNames = [typeName],
                AllowedMemberNames = ["Value", "Add"],
                InstallGlobalModule = true,
            },
        });

        var info = host.ClrBridge.ResolveType(typeName);
        var userdata = host.ClrBridge.CreateInstance(typeName, [LuaValue.FromInteger(42)]);
        var payload = userdata.GetPayload<LuaClrObject>();
        var luaResult = host.RunUtf8(
            $"local value=clr.new('{typeName}', 43); return type(value),value.Value,value:Add(1)");
        return info.IsConstructible &&
            payload.Instance is ClrFixtureValue { Value: 42 } &&
            luaResult.Succeeded &&
            luaResult.Execution!.Values[0].AsString().ToString() == "userdata" &&
            luaResult.Execution.Values[1].AsInteger() == 43 &&
            luaResult.Execution.Values[2].AsInteger() == 44;
    }

    private static bool VerifyReplayStore()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "lunil-nativeaot-replay",
            Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LuaPatchFileReplayStore(Path.Combine(directory, "replay.ndjson"));
            var at = new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);
            var reserved = store.TryReserve("state-a", "patch-1", "nonce-1", at);
            if (!reserved.Reserved)
            {
                return false;
            }

            using var lease = store.TryAcquireCommit(reserved.Reservation!, at);
            if (lease is null)
            {
                return false;
            }

            lease.Complete(at);
            return store.ReadAll().Select(static record => record.State).SequenceEqual([
                LuaPatchReplayRecordState.Reserved,
                LuaPatchReplayRecordState.Committed,
            ]);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    public sealed class ClrFixtureValue
    {
        public ClrFixtureValue(long value)
        {
            Value = value;
        }

        public long Value { get; }

        public long Add(long amount) => Value + amount;
    }
}
