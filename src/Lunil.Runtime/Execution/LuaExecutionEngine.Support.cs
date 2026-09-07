using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Lunil.Core;
using Lunil.IR.Canonical;
using Lunil.IR.Lua54;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Operations;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Execution;

internal sealed partial class LuaExecutionEngine
{

    private static LuaValue MaterializeError(LuaState state, LuaRuntimeException exception)
    {
        if (exception.HasErrorValue)
        {
            return exception.ErrorValue;
        }

        try
        {
            return LuaValue.FromString(state.Strings.GetOrCreate(
                System.Text.Encoding.UTF8.GetBytes(exception.Message)));
        }
        catch (LuaRuntimeException materializationFailure) when (
            materializationFailure.Message.StartsWith(
                "Lua heap quota exceeded",
                StringComparison.Ordinal))
        {
            // A Lua memory error must itself remain catchable when every quota byte is live.
            // Keep one canonical message permanently rooted per state, matching Lua's emergency
            // memory-error object instead of recursively trying to allocate another error value.
            return LuaValue.FromString(state.MemoryErrorString);
        }
    }

    private static LuaValue CreateErrorInErrorHandling(LuaState state)
    {
        try
        {
            return LuaValue.FromString(state.Strings.GetOrCreate("error in error handling"u8));
        }
        catch (LuaRuntimeException materializationFailure) when (
            materializationFailure.Message.StartsWith(
                "Lua heap quota exceeded",
                StringComparison.Ordinal))
        {
            return LuaValue.FromString(state.MemoryErrorString);
        }
    }

    internal static LuaValue Read(LuaThread thread, LuaFrame frame, int register) =>
        thread.Stack.ReadUnchecked(frame.Base + register);

    internal static void Write(LuaThread thread, LuaFrame frame, int register, LuaValue value)
    {
        var index = frame.Base + register;
        thread.Stack.WriteUnchecked(index, value);
        frame.Top = Math.Max(frame.Top, index + 1);
    }

    private static LuaValue[] InvokeNativeBody(
        LuaState state,
        LuaValue function,
        ReadOnlySpan<LuaValue> arguments)
    {
        var nativeFunction = function.TryGetNativeFunction() ??
            throw new InvalidOperationException("The callable is not a native function.");
        var previous = state.RunningNativeFunction;
        state.RunningNativeFunction = function;
        try
        {
            return nativeFunction.Body!(state, arguments);
        }
        catch (LuaRuntimeException)
        {
            throw;
        }
        catch (LuaHostException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw ConvertNativeClrException(exception);
        }
        finally
        {
            state.RunningNativeFunction = previous;
        }
    }

    private static LuaNativeStep InvokeNativeStep(
        LuaNativeFunction native,
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> arguments)
    {
        var stepBody = native.StepBody ??
            throw new InvalidOperationException("The native function has no step body.");
        try
        {
            return stepBody(context, continuationId, arguments);
        }
        catch (LuaRuntimeException)
        {
            throw;
        }
        catch (LuaHostException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw ConvertNativeClrException(exception);
        }
    }

    private static LuaRuntimeException ConvertNativeClrException(Exception exception)
    {
        // Native bodies are a host boundary: an arbitrary CLR exception must fail the
        // enclosing protected call as a Lua error instead of terminating the scheduler.
        // The original exception stays reachable as InnerException for programmatic hosts.
        return new LuaRuntimeException(
            $"{exception.GetType().FullName}: {exception.Message}",
            exception);
    }

    private static LuaClosure CreateNativeCallbackTrampoline(
        LuaState state,
        LuaValue callable)
    {
        var instructions = ImmutableArray.Create(
            new LuaIrInstruction(LuaIrOpcode.GetUpvalue, 0, 0),
            new LuaIrInstruction(LuaIrOpcode.VarArg, 1, -1),
            new LuaIrInstruction(LuaIrOpcode.TailCall, 0, -1, -1));
        var function = new LuaIrFunction
        {
            Id = 0,
            Span = default,
            ParameterCount = 0,
            IsVarArg = true,
            RegisterCount = 2,
            Upvalues =
            [
                new LuaIrUpvalue("(callback)", 0, LuaIrUpvalueSourceKind.Environment, 0),
            ],
            Instructions = instructions,
            BasicBlocks = LuaIrControlFlow.Build(instructions),
        };
        var module = new LuaIrModule
        {
            MainFunctionId = 0,
            Functions = [function],
        };
        return new LuaClosure(
            state.Heap,
            new LuaModuleRuntimeData(module),
            function,
            [new LuaUpvalue(state.Heap, callable)]);
    }

    private void ValidateInstructionLimit(long maximumInstructionCount)
    {
        if (maximumInstructionCount < 1 ||
            maximumInstructionCount > _options.MaximumInstructionCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumInstructionCount),
                maximumInstructionCount,
                $"The invocation instruction limit must be between 1 and " +
                $"{_options.MaximumInstructionCount}.");
        }
    }

    private static LuaExecutionResult RecordInstructionCount(
        LuaExecutionResult result,
        LuaScheduler scheduler) => result with
        {
            ExecutedInstructionCount = scheduler.TotalInstructionCount,
        };

}
