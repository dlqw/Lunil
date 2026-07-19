using System.Text;
using Lunil.Core;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Execution;

/// <summary>Runtime descriptors backing the Lua 5.4 coroutine module.</summary>
internal static class LuaCoroutineModule
{
    private static readonly LuaNativeFunction CreateDescriptor =
        new("coroutine.create", Create);

    private static readonly LuaNativeFunction ResumeDescriptor = new(
        "coroutine.resume",
        static (_, _) => throw new InvalidOperationException("coroutine.resume is a VM intrinsic."),
        LuaNativeFunctionKind.CoroutineResume);

    private static readonly LuaNativeFunction YieldDescriptor = new(
        "coroutine.yield",
        static (_, _) => throw new InvalidOperationException("coroutine.yield is a VM intrinsic."),
        LuaNativeFunctionKind.CoroutineYield);

    private static readonly LuaNativeFunction RunningDescriptor =
        new("coroutine.running", Running);

    private static readonly LuaNativeFunction StatusDescriptor =
        new("coroutine.status", Status);

    private static readonly LuaNativeFunction IsYieldableDescriptor =
        new("coroutine.isyieldable", IsYieldable);

    private static readonly LuaNativeFunction WrapFactoryDescriptor =
        new("coroutine.wrap", Wrap);

    private static readonly LuaNativeFunction WrappedCallDescriptor = new(
        "coroutine.wrap continuation",
        static (_, _) => throw new InvalidOperationException("coroutine.wrap is a VM intrinsic."),
        LuaNativeFunctionKind.CoroutineWrap);

    private static readonly LuaNativeFunction CloseDescriptor = new(
        "coroutine.close",
        static (_, _) => throw new InvalidOperationException("coroutine.close is a VM intrinsic."),
        LuaNativeFunctionKind.CoroutineClose);

    public static LuaTable CreateModule(LuaState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var module = state.CreateTable();
        Set(state, module, "create", LuaValue.FromFunction(CreateDescriptor));
        Set(state, module, "resume", LuaValue.FromFunction(ResumeDescriptor));
        Set(state, module, "yield", LuaValue.FromFunction(YieldDescriptor));
        Set(state, module, "running", LuaValue.FromFunction(RunningDescriptor));
        Set(state, module, "status", LuaValue.FromFunction(StatusDescriptor));
        Set(state, module, "isyieldable", LuaValue.FromFunction(IsYieldableDescriptor));
        Set(state, module, "wrap", LuaValue.FromFunction(WrapFactoryDescriptor));
        if (state.LanguageVersion == LuaLanguageVersion.Lua54)
        {
            Set(state, module, "close", LuaValue.FromFunction(CloseDescriptor));
        }

        return module;
    }

    private static LuaValue[] Create(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var function = RequireFunction(arguments, "create");
        return [LuaValue.FromThread(state.CreateThread(function))];
    }

    private static LuaValue[] Running(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        _ = arguments;
        var running = state.RunningThread ?? state.MainThread;
        return
        [
            LuaValue.FromThread(running),
            LuaValue.FromBoolean(ReferenceEquals(running, state.MainThread)),
        ];
    }

    private static LuaValue[] Status(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        if (arguments.Length == 0 || arguments[0].Kind != LuaValueKind.Thread)
        {
            throw new LuaRuntimeException("bad argument #1 to 'status' (thread expected)");
        }

        var thread = arguments[0].AsThread();
        state.Heap.ValidateValue(arguments[0]);
        var running = state.RunningThread;
        var status = ReferenceEquals(thread, running)
            ? "running"
            : thread.Status switch
            {
                LuaThreadStatus.New or LuaThreadStatus.Suspended => "suspended",
                LuaThreadStatus.Normal or LuaThreadStatus.Running => "normal",
                LuaThreadStatus.Dead or LuaThreadStatus.Error => "dead",
                _ => throw new InvalidOperationException("Unknown coroutine status."),
            };
        return [String(state, status)];
    }

    private static LuaValue[] IsYieldable(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        if (arguments.Length == 0 || arguments[0].IsNil)
        {
            return [LuaValue.FromBoolean(state.RunningThreadIsYieldable)];
        }

        if (arguments[0].Kind != LuaValueKind.Thread)
        {
            throw new LuaRuntimeException("bad argument #1 to 'isyieldable' (thread expected)");
        }

        var thread = arguments[0].AsThread();
        state.Heap.ValidateValue(arguments[0]);
        var running = state.RunningThread ?? state.MainThread;
        return
        [
            LuaValue.FromBoolean(ReferenceEquals(thread, running)
                ? state.RunningThreadIsYieldable
                : !ReferenceEquals(thread, state.MainThread)),
        ];
    }

    private static LuaValue[] Wrap(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var function = RequireFunction(arguments, "wrap");
        var thread = state.CreateThread(function);
        var wrapper = state.CreateNativeClosure(
            WrappedCallDescriptor,
            [LuaValue.FromThread(thread)]);
        return [LuaValue.FromFunction(wrapper)];
    }

    private static LuaValue RequireFunction(ReadOnlySpan<LuaValue> arguments, string name)
    {
        if (arguments.Length == 0 || arguments[0].Kind != LuaValueKind.Function)
        {
            var actual = arguments.Length == 0 ? LuaValueKind.Nil : arguments[0].Kind;
            throw new LuaRuntimeException(
                $"bad argument #1 to '{name}' (function expected, got {actual.ToString().ToLowerInvariant()})");
        }

        return arguments[0];
    }

    private static void Set(LuaState state, LuaTable module, string name, LuaValue value) =>
        module.Set(String(state, name), value);

    private static LuaValue String(LuaState state, string value) =>
        LuaValue.FromString(state.Strings.GetOrCreate(Encoding.UTF8.GetBytes(value)));
}
