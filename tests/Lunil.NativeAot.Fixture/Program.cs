using Lunil.CodeGen.Cil;
using Lunil.Core.Text;
using Lunil.IR.Canonical;
using Lunil.CodeGen.Cil.Jit;
using Lunil.Runtime;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

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

        var executor = new LuaStaticAotExecutor();
        foreach (var testCase in cases)
        {
            if (!LuaStaticAotRegistry.TryGetModule("fixture." + testCase.Key, out var registration) ||
                registration is null)
            {
                Console.Error.WriteLine($"Static AOT module '{testCase.Key}' was not registered.");
                return 1;
            }

            var value = Execute(executor, registration.CanonicalModule);
            if (value != testCase.Value)
            {
                Console.Error.WriteLine(
                    $"Unexpected static result for {testCase.Key}: expected={testCase.Value}, actual={value}.");
                return 2;
            }
        }

        var dynamicValue = Execute(executor, Compile("return 21 * 2"));
        if (dynamicValue != 42)
        {
            Console.Error.WriteLine($"Unexpected dynamic fallback result: {dynamicValue}.");
            return 2;
        }

        using var defaultJit = new LuaJitExecutor();
        if (defaultJit.Options.Policy != LuaJitPolicy.Auto ||
            !defaultJit.Options.EnableTier2 ||
            defaultJit.Options.EnableTier2ManagedFallback ||
            defaultJit.Options.EnableLoopOsr ||
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

        Console.WriteLine("LUNIL_NATIVEAOT_OK");
        return 0;
    }

    private static long Execute(LuaStaticAotExecutor executor, LuaIrModule module)
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

    private static LuaIrModule Compile(string source)
    {
        var parsing = LuaParser.Parse(SourceText.FromUtf8(source));
        var binding = LuaBinder.Bind(parsing);
        var lowering = LuaLowerer.Lower(binding);
        return lowering.Module ?? throw new InvalidOperationException(
            string.Join("; ", lowering.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }
}
