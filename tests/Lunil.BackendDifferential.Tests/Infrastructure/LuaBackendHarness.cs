using System.Collections.Immutable;
using System.Globalization;
using Lunil.CodeGen.Cil;
using Lunil.CodeGen.Cil.Jit;
using Lunil.Core.Text;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.StandardLibrary;
using Lunil.Syntax.Parsing;

namespace Lunil.BackendDifferential.Tests.Infrastructure;

internal interface ILuaBackendHarness
{
    string Name { get; }

    LuaExecutionResult Execute(
        LuaState state,
        LuaClosure closure,
        ReadOnlySpan<LuaValue> arguments = default);

    LuaExecutionResult Start(
        LuaState state,
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments = default);

    LuaExecutionResult Resume(
        LuaState state,
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments = default);
}

internal sealed class InterpreterBackendHarness : ILuaBackendHarness
{
    private readonly LuaInterpreter _interpreter;

    public InterpreterBackendHarness(LuaInterpreterOptions? options = null)
    {
        _interpreter = new LuaInterpreter(options);
    }

    public string Name => "interpreter";

    public LuaExecutionResult Execute(
        LuaState state,
        LuaClosure closure,
        ReadOnlySpan<LuaValue> arguments = default) =>
        _interpreter.Execute(state, closure, arguments);

    public LuaExecutionResult Start(
        LuaState state,
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments = default) =>
        _interpreter.Start(state, thread, arguments);

    public LuaExecutionResult Resume(
        LuaState state,
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments = default) =>
        _interpreter.Resume(state, thread, arguments);
}

internal sealed class ExecutorBackendHarness : ILuaBackendHarness
{
    private readonly LuaExecutor _executor;

    public ExecutorBackendHarness(LuaInterpreterOptions options)
    {
        _executor = new LuaExecutor(new LuaExecutorOptions { Interpreter = options });
    }

    public string Name => "executor-auto";

    public LuaExecutionResult Execute(
        LuaState state,
        LuaClosure closure,
        ReadOnlySpan<LuaValue> arguments = default) =>
        _executor.Execute(state, closure, arguments);

    public LuaExecutionResult Start(
        LuaState state,
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments = default) =>
        _executor.Start(state, thread, arguments);

    public LuaExecutionResult Resume(
        LuaState state,
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments = default) =>
        _executor.Resume(state, thread, arguments);
}

internal sealed class Tier1JitBackendHarness : ILuaBackendHarness, IDisposable
{
    private readonly LuaJitExecutor _executor;

    public Tier1JitBackendHarness(LuaInterpreterOptions options)
    {
        _executor = new LuaJitExecutor(new LuaJitExecutorOptions
        {
            Policy = LuaJitPolicy.PreferJit,
            SynchronousCompilation = true,
            EnableTier2 = false,
            Interpreter = options,
        });
    }

    public string Name => "coreclr-tier1-jit";

    public LuaExecutionResult Execute(
        LuaState state,
        LuaClosure closure,
        ReadOnlySpan<LuaValue> arguments = default) =>
        _executor.Execute(state, closure, arguments);

    public LuaExecutionResult Start(
        LuaState state,
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments = default) =>
        _executor.Start(state, thread, arguments);

    public LuaExecutionResult Resume(
        LuaState state,
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments = default) =>
        _executor.Resume(state, thread, arguments);

    public void Dispose() => _executor.Dispose();
}

internal sealed class Tier2JitBackendHarness : ILuaBackendHarness, IDisposable
{
    private readonly LuaJitExecutor _executor;

    public Tier2JitBackendHarness(LuaInterpreterOptions options)
    {
        _executor = new LuaJitExecutor(new LuaJitExecutorOptions
        {
            Policy = LuaJitPolicy.PreferJit,
            SynchronousCompilation = true,
            EnableTier2 = true,
            EnableTier2ManagedFallback = true,
            Tier2InvocationThreshold = 1,
            Tier2BackedgeThreshold = 1,
            Interpreter = options,
        });
    }

    public string Name => "coreclr-tier2-jit";

    public LuaExecutionResult Execute(
        LuaState state,
        LuaClosure closure,
        ReadOnlySpan<LuaValue> arguments = default) =>
        _executor.Execute(state, closure, arguments);

    public LuaExecutionResult Start(
        LuaState state,
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments = default) =>
        _executor.Start(state, thread, arguments);

    public LuaExecutionResult Resume(
        LuaState state,
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments = default) =>
        _executor.Resume(state, thread, arguments);

    public void Dispose() => _executor.Dispose();
}

internal sealed class LoopOsrBackendHarness : ILuaBackendHarness, IDisposable
{
    private readonly LuaJitExecutor _executor;

    public LoopOsrBackendHarness(LuaInterpreterOptions options)
    {
        _executor = new LuaJitExecutor(new LuaJitExecutorOptions
        {
            Policy = LuaJitPolicy.Auto,
            FunctionEntryThreshold = int.MaxValue,
            BackedgeThreshold = int.MaxValue,
            SynchronousCompilation = true,
            EnableTier2 = false,
            EnableLoopOsr = true,
            EnableLoopOsrManagedFallback = true,
            LoopOsrBackedgeThreshold = 1,
            Interpreter = options,
        });
    }

    public string Name => "experimental-loop-osr";

    public LuaExecutionResult Execute(
        LuaState state,
        LuaClosure closure,
        ReadOnlySpan<LuaValue> arguments = default) =>
        _executor.Execute(state, closure, arguments);

    public LuaExecutionResult Start(
        LuaState state,
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments = default) =>
        _executor.Start(state, thread, arguments);

    public LuaExecutionResult Resume(
        LuaState state,
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments = default) =>
        _executor.Resume(state, thread, arguments);

    public void Dispose() => _executor.Dispose();
}

internal sealed record LuaBackendTestOptions
{
    public static LuaBackendTestOptions Default { get; } = new();

    public long MaximumInstructionCount { get; init; } = 100_000_000;

    public int MaximumStackSlots { get; init; } = 1_000_000;

    public int MaximumCallDepth { get; init; } = 20_000;
}

internal static class LuaBackendCatalog
{
    public static IReadOnlyList<ILuaBackendHarness> All { get; } = CreateAll();

    public static IReadOnlyList<ILuaBackendHarness> CreateAll(
        LuaBackendTestOptions? options = null)
    {
        options ??= LuaBackendTestOptions.Default;
        var interpreterOptions = new LuaInterpreterOptions
        {
            MaximumInstructionCount = options.MaximumInstructionCount,
            MaximumStackSlots = options.MaximumStackSlots,
            MaximumCallDepth = options.MaximumCallDepth,
        };
        return
        [
            new InterpreterBackendHarness(interpreterOptions),
            new ExecutorBackendHarness(interpreterOptions),
            new Tier1JitBackendHarness(interpreterOptions),
            new Tier2JitBackendHarness(interpreterOptions),
            new LoopOsrBackendHarness(interpreterOptions),
        ];
    }

    public static IEnumerable<object[]> TheoryData() =>
        All.Select(static backend => new object[] { backend });
}

internal sealed class LuaBackendSession
{
    private LuaBackendSession(
        ILuaBackendHarness backend,
        LuaState state,
        LuaClosure closure)
    {
        Backend = backend;
        State = state;
        Closure = closure;
    }

    public ILuaBackendHarness Backend { get; }

    public LuaState State { get; }

    public LuaClosure Closure { get; }

    public static LuaBackendSession Create(
        ILuaBackendHarness backend,
        string source,
        LuaStateOptions? stateOptions = null,
        bool installStandardLibrary = false)
    {
        ArgumentNullException.ThrowIfNull(backend);
        var state = new LuaState(stateOptions);
        if (installStandardLibrary)
        {
            LuaStandardLibrary.InstallAll(state);
        }

        return new LuaBackendSession(
            backend,
            state,
            state.CreateMainClosure(Compile(source)));
    }

    internal static LuaBackendSession Create(
        ILuaBackendHarness backend,
        LuaIrModule module,
        LuaStateOptions? stateOptions = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(module);
        var state = new LuaState(stateOptions);
        return new LuaBackendSession(backend, state, state.CreateMainClosure(module));
    }

    public LuaBackendObservation Execute(ReadOnlySpan<LuaValue> arguments = default)
    {
        var copiedArguments = arguments.ToArray();
        return Observe(() => Backend.Execute(State, Closure, copiedArguments));
    }

    public LuaThread CreateThread() => State.CreateThread(Closure);

    public LuaBackendObservation Start(
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments = default)
    {
        var copiedArguments = arguments.ToArray();
        return Observe(() => Backend.Start(State, thread, copiedArguments));
    }

    public LuaBackendObservation Resume(
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments = default)
    {
        var copiedArguments = arguments.ToArray();
        return Observe(() => Backend.Resume(State, thread, copiedArguments));
    }

    internal static LuaIrModule Compile(string source)
    {
        var parsing = LuaParser.Parse(SourceText.FromUtf8(source));
        var binding = LuaBinder.Bind(parsing);
        var lowering = LuaLowerer.Lower(binding);
        if (!lowering.Succeeded || lowering.Module is null)
        {
            var diagnostics = parsing.Diagnostics
                .Concat(binding.Diagnostics)
                .Concat(lowering.Diagnostics)
                .Select(static diagnostic => diagnostic.Message);
            throw new InvalidOperationException(
                $"Backend differential source did not compile: {string.Join("; ", diagnostics)}");
        }

        return lowering.Module;
    }

    private static LuaBackendObservation Observe(Func<LuaExecutionResult> execute)
    {
        try
        {
            var result = execute();
            return new LuaBackendObservation(
                result.Signal,
                [.. result.Values.Select(LuaObservedValue.Create)],
                null,
                null);
        }
        catch (LuaRuntimeException exception)
        {
            LuaObservedValue? error = exception.HasErrorValue
                ? LuaObservedValue.Create(exception.ErrorValue)
                : null;
            return new LuaBackendObservation(
                LuaVmSignal.Error,
                [],
                error,
                exception.HasErrorValue ? null : exception.Message);
        }
    }
}

internal sealed record LuaBackendObservation(
    LuaVmSignal Signal,
    ImmutableArray<LuaObservedValue> Values,
    LuaObservedValue? ErrorValue,
    string? RuntimeError)
{
    public string? ErrorText => RuntimeError ??
        (ErrorValue is { Kind: LuaValueKind.String } error
            ? System.Text.Encoding.UTF8.GetString(Convert.FromHexString(error.Representation))
            : ErrorValue?.ToString());

    public override string ToString()
    {
        var values = string.Join(", ", Values);
        var error = ErrorValue?.ToString() ?? RuntimeError ?? string.Empty;
        return $"{Signal}: [{values}] {error}".TrimEnd();
    }
}

internal readonly record struct LuaObservedValue(LuaValueKind Kind, string Representation)
{
    public static LuaObservedValue Create(LuaValue value) => value.Kind switch
    {
        LuaValueKind.Nil => new(value.Kind, "nil"),
        LuaValueKind.Boolean => new(value.Kind, value.AsBoolean() ? "true" : "false"),
        LuaValueKind.Integer => new(
            value.Kind,
            value.AsInteger().ToString(CultureInfo.InvariantCulture)),
        LuaValueKind.Float => new(
            value.Kind,
            BitConverter.DoubleToInt64Bits(value.AsFloat()).ToString("x16", CultureInfo.InvariantCulture)),
        LuaValueKind.String => new(value.Kind, Convert.ToHexString(value.AsString().AsSpan())),
        _ => new(value.Kind, value.Kind.ToString()),
    };

    public override string ToString() => $"{Kind}:{Representation}";
}

internal static class LuaBackendAssert
{
    public static void AllAgree(Func<ILuaBackendHarness, LuaBackendObservation> execute)
    {
        var results = LuaBackendCatalog.All
            .Select(backend => (backend.Name, Observation: execute(backend)))
            .ToArray();
        Assert.NotEmpty(results);
        var expected = results[0].Observation;
        foreach (var result in results.Skip(1))
        {
            Assert.True(
                Equivalent(expected, result.Observation),
                $"Backend {result.Name} returned {result.Observation}; expected {expected}.");
        }
    }

    private static bool Equivalent(
        LuaBackendObservation left,
        LuaBackendObservation right) =>
        left.Signal == right.Signal &&
        left.Values.SequenceEqual(right.Values) &&
        left.ErrorValue == right.ErrorValue &&
        string.Equals(left.RuntimeError, right.RuntimeError, StringComparison.Ordinal);
}
