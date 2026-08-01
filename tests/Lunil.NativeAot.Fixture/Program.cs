using Lunil.CodeGen.Cil;
using Lunil.CodeGen.Cil.Jit;
using Lunil.Compiler;
using Lunil.Hosting;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.StandardLibrary;
using Lunil.Workspace;

[assembly: LuaClrGenerateBinding(
    typeof(Lunil.NativeAot.Fixture.Program.ClrFixtureValue),
    nameof(Lunil.NativeAot.Fixture.Program.ClrFixtureValue.Value),
    nameof(Lunil.NativeAot.Fixture.Program.ClrFixtureValue.Add),
    nameof(Lunil.NativeAot.Fixture.Program.ClrFixtureValue.Async))]
[assembly: LuaClrGenerateBinding(typeof(Func<int, int>))]

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

            using var autoHost = new LuaHost(new LuaHostOptions
            {
                ExecutionBackend = LuaHostExecutionBackend.Auto,
            });
            var hostResult = autoHost.RunUtf8("return 6 * 7", "=nativeaot-host");
            if (autoHost.IsDynamicCodeAvailable ||
                autoHost.SelectedExecutionBackend != LuaHostExecutionBackend.Interpreter ||
                autoHost.JitStatistics is not null ||
                !hostResult.Succeeded ||
                hostResult.Execution?.Values[0].AsInteger() != 42)
            {
                Console.Error.WriteLine("NativeAOT host fallback policy is invalid.");
                return 5;
            }

            try
            {
                using var _ = new LuaHost(new LuaHostOptions
                {
                    ExecutionBackend = LuaHostExecutionBackend.Jit,
                });
                Console.Error.WriteLine("NativeAOT host accepted a required dynamic-code backend.");
                return 5;
            }
            catch (PlatformNotSupportedException)
            {
            }
        }

        if (!VerifyClrInterop())
        {
            Console.Error.WriteLine("The preserved CLR interoperation contract is invalid.");
            return 6;
        }

        if (!VerifyFfi())
        {
            Console.Error.WriteLine("The NativeAOT registry-only FFI contract is invalid.");
            return 9;
        }

        if (!VerifyGameLoop())
        {
            Console.Error.WriteLine("The NativeAOT game-loop contract is invalid.");
            return 7;
        }

        if (!VerifyReplayStore())
        {
            Console.Error.WriteLine("The NativeAOT replay-store contract is invalid.");
            return 8;
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

    private static bool VerifyGameLoop()
    {
        using var game = new LuaGameLoopHost(new LuaGameLoopHostOptions
        {
            HostOptions = new LuaHostOptions
            {
                ExecutionBackend = LuaHostExecutionBackend.Interpreter,
            },
        });
        var operation = game.Start(game.Host.CompileUtf8(
            "local value=40; coroutine.yield(value); return value+2",
            "=nativeaot-game-loop"));
        var yielded = game.Tick();
        var completed = game.Tick();
        return yielded.Succeeded && completed.Succeeded &&
            yielded.ExecutedInstructionCount > 0 &&
            operation.Status == LuaGameLoopOperationStatus.Completed &&
            operation.Values[0].AsInteger() == 42;
    }

    private static bool VerifyFfi()
    {
        var registry = new LuaFfiBindingRegistry();
        registry.Register("fixture", "add", "i32(i32,i32)", AddNative);
        var options = new LuaStandardLibraryOptions
        {
            Ffi = new LuaFfiOptions
            {
                Enabled = true,
                AllowedLibraryNames = ["fixture"],
                AllowedSymbolNames = ["fixture!add"],
                BindingRegistry = registry,
                LibraryLoader = RegistryOnlyLoader.Instance,
            },
        };
        var state = new LuaState();
        LuaStandardLibrary.InstallBasic(state, options);
        LuaStandardLibrary.InstallFfi(state, options);
        var result = new LuaInterpreter().Execute(
            state,
            state.CreateMainClosure(Compile(
                "local lib=ffi.load('fixture'); " +
                "local add=ffi.bind(lib,'add','i32(i32,i32)'); " +
                "local value=add(20,22); ffi.close(lib); return value")));
        return result.Values[0].AsInteger() == 42;
    }

    private static object? AddNative(ReadOnlySpan<object?> arguments) =>
        checked((int)arguments[0]! + (int)arguments[1]!);

    private static bool VerifyClrInterop()
    {
        var typeName = typeof(ClrFixtureValue).FullName!;
        var delegateName = typeof(Func<int, int>).FullName!;
        var registry = new LuaClrBindingRegistry();
        new Lunil.Generated.LuaClrGeneratedBindings().RegisterBindings(registry);
        using var host = new LuaHost(new LuaHostOptions
        {
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
            Clr = new LuaClrOptions
            {
                Capabilities = LuaClrCapabilities.TypeDiscovery | LuaClrCapabilities.Construction |
                    LuaClrCapabilities.MemberAccess | LuaClrCapabilities.DelegateConversion |
                    LuaClrCapabilities.Async,
                AllowedAssemblyNames =
                [
                    typeof(ClrFixtureValue).Assembly.GetName().Name!,
                    typeof(Func<int, int>).Assembly.GetName().Name!,
                ],
                AllowedTypeNames = [typeName, delegateName],
                AllowedMemberNames =
                [
                    $"{typeName}.Value",
                    $"{typeName}.Add",
                    $"{typeName}.Async",
                ],
                AllowedDelegateTypeNames = [delegateName],
                BindingRegistry = registry,
                BindingMode = LuaClrBindingMode.RegistryOnly,
                InstallGlobalModule = true,
            },
        });

        var info = host.ClrBridge.ResolveType(typeName);
        var userdata = host.ClrBridge.CreateInstance(typeName, [LuaValue.FromInteger(42)]);
        var payload = userdata.GetPayload<LuaClrObject>();
        var luaResult = host.RunUtf8(
            $"local value=clr.new('{typeName}', 43); return type(value),value.Value,value:Add(1)");
        var function = host.RunUtf8("return function(value) return value+1 end").Execution!.Values[0];
        var callback = (Func<int, int>)host.ClrBridge.CreateDelegate(function, delegateName);
        var task = host.ClrBridge.InvokeStatic(
            typeName,
            nameof(ClrFixtureValue.Async),
            [LuaValue.FromInteger(41)]).ReturnValue;
        return info.IsConstructible &&
            payload.Instance is ClrFixtureValue { Value: 42 } &&
            luaResult.Succeeded &&
            luaResult.Execution!.Values[0].AsString().ToString() == "userdata" &&
            luaResult.Execution.Values[1].AsInteger() == 43 &&
            luaResult.Execution.Values[2].AsInteger() == 44 &&
            callback(41) == 42 &&
            host.ClrBridge.Await(task).AsInteger() == 41;
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

        public static Task<long> Async(long value) => Task.FromResult(value);
    }

    private sealed class RegistryOnlyLoader : ILuaFfiLibraryLoader
    {
        public static RegistryOnlyLoader Instance { get; } = new();

        private RegistryOnlyLoader()
        {
        }

        public IntPtr Load(string libraryName) => new(1);

        public IntPtr GetExport(IntPtr libraryHandle, string symbolName) =>
            throw new InvalidOperationException("The registry-only fixture must not resolve dynamic symbols.");

        public void Free(IntPtr libraryHandle)
        {
        }
    }
}
