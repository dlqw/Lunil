using Lunil.Core;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.StandardLibrary;

/// <summary>
/// Lua 5.1 function/thread environment helpers. Maps getfenv/setfenv onto the shared _ENV
/// upvalue model without sharing mutated environment cells across distinct closures.
/// </summary>
internal static class LuaFunctionEnvironments
{
    public static bool SupportsFunctionEnvironments(LuaLanguageVersion version) =>
        version == LuaLanguageVersion.Lua51;

    public static LuaValue GetEnvironment(LuaState state, LuaValue target)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Heap.ValidateValue(target);
        return target.Kind switch
        {
            LuaValueKind.Function => GetFunctionEnvironment(state, target),
            LuaValueKind.Thread => GetThreadEnvironment(state, target.AsThread()),
            _ => throw LuaLibraryHelpers.BadArgument("getfenv", 0, "function or level expected"),
        };
    }

    public static void SetEnvironment(LuaState state, LuaValue target, LuaTable environment)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(environment);
        state.Heap.ValidateValue(target);
        state.Heap.ValidateValue(LuaValue.FromTable(environment));
        switch (target.Kind)
        {
            case LuaValueKind.Function:
                SetFunctionEnvironment(state, target, environment);
                break;
            case LuaValueKind.Thread:
                SetThreadEnvironment(state, target.AsThread(), environment);
                break;
            default:
                throw LuaLibraryHelpers.BadArgument("setfenv", 0, "function or level expected");
        }
    }

    public static LuaValue GetFunctionEnvironment(LuaState state, LuaValue function)
    {
        if (function.TryGetClosure() is { } closure)
        {
            if (TryFindEnvironmentUpvalueIndex(closure, out var index))
            {
                return closure.GetUpvalue(index).Value;
            }

            if (!closure.LegacyEnvironment.IsNil)
            {
                return closure.LegacyEnvironment;
            }

            return LuaValue.FromTable(state.Globals);
        }

        if (function.TryGetNativeClosure() is { } native)
        {
            return native.Environment.IsNil
                ? LuaValue.FromTable(state.Globals)
                : native.Environment;
        }

        if (function.TryGetNativeFunction() is not null)
        {
            // Descriptor-only native functions share the process-wide globals surface.
            return LuaValue.FromTable(state.Globals);
        }

        throw LuaLibraryHelpers.BadArgument("getfenv", 0, "function expected");
    }

    public static void SetFunctionEnvironment(LuaState state, LuaValue function, LuaTable environment)
    {
        var value = LuaValue.FromTable(environment);
        if (function.TryGetClosure() is { } closure)
        {
            if (TryFindEnvironmentUpvalueIndex(closure, out var index))
            {
                closure.ReplaceUpvalue(index, new LuaUpvalue(state.Heap, value));
                return;
            }

            closure.LegacyEnvironment = value;
            return;
        }

        if (function.TryGetNativeClosure() is { } native)
        {
            native.Environment = value;
            return;
        }

        if (function.TryGetNativeFunction() is not null)
        {
            throw new LuaRuntimeException("cannot change environment of given object");
        }

        throw LuaLibraryHelpers.BadArgument("setfenv", 0, "function expected");
    }

    public static LuaValue GetThreadEnvironment(LuaState state, LuaThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        state.Heap.ValidateValue(LuaValue.FromThread(thread));
        return thread.Environment.IsNil
            ? LuaValue.FromTable(state.Globals)
            : thread.Environment;
    }

    public static void SetThreadEnvironment(LuaState state, LuaThread thread, LuaTable environment)
    {
        ArgumentNullException.ThrowIfNull(thread);
        state.Heap.ValidateValue(LuaValue.FromThread(thread));
        thread.Environment = LuaValue.FromTable(environment);
    }

    public static bool TryResolveLevelTarget(
        LuaState state,
        LuaThread thread,
        long level,
        out LuaValue target)
    {
        target = default;
        if (level < 0 || level > int.MaxValue)
        {
            return false;
        }

        if (level == 0)
        {
            target = LuaValue.FromThread(thread);
            return true;
        }

        // Lua levels are 1-based from the caller of getfenv/setfenv. When the library function is
        // itself a native activation, level 1 is the nearest Lua frame (the Lua caller).
        var frame = LuaDebugApi.GetFrame(state, thread, checked((int)level - 1));
        if (frame is null)
        {
            return false;
        }

        target = LuaValue.FromFunction(frame.Closure);
        return true;
    }

    public static bool TryFindEnvironmentUpvalueIndex(LuaClosure closure, out int index)
    {
        ArgumentNullException.ThrowIfNull(closure);
        var upvalues = closure.Function.Upvalues;
        for (var i = 0; i < upvalues.Length; i++)
        {
            var descriptor = upvalues[i];
            if (descriptor.SourceKind == LuaIrUpvalueSourceKind.Environment ||
                string.Equals(descriptor.Name, "_ENV", StringComparison.Ordinal))
            {
                index = i;
                return true;
            }
        }

        // Main chunks install globals into upvalue 0 even when debug names are stripped.
        if (upvalues.Length > 0 &&
            ReferenceEquals(closure.Function, closure.Module.Functions[closure.Module.MainFunctionId]))
        {
            index = 0;
            return true;
        }

        index = -1;
        return false;
    }

    public static void CallModuleOption(LuaState state, LuaValue option, LuaTable module)
    {
        var argument = LuaValue.FromTable(module);
        if (option.TryGetNativeFunction() is { Body: not null } descriptor)
        {
            descriptor.Body(state, [argument]);
            return;
        }

        if (option.TryGetNativeClosure() is { } native)
        {
            if (native.Descriptor.Body is not null)
            {
                native.Descriptor.Body(state, [argument]);
                return;
            }

            throw new LuaRuntimeException("module option function is not callable from this host path");
        }

        if (option.TryGetClosure() is { } closure)
        {
            var result = new LuaInterpreter().Execute(state, closure, [argument]);
            if (result.Signal != LuaVmSignal.Completed)
            {
                throw new LuaRuntimeException(
                    $"module option did not complete (signal={result.Signal})");
            }

            return;
        }

        throw LuaLibraryHelpers.BadArgument("module", 1, "function expected");
    }
}
