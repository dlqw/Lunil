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
    private bool TryInvokeDebugHook(
        LuaState state,
        LuaThread thread,
        LuaFrame frame,
        in LuaIrInstruction instruction)
    {
        if (!thread.HasDispatchableDebugHook || frame.IsDebugHook || frame.IsHidden)
        {
            return false;
        }

        var resumingHookedInstruction = false;
        if (frame.DebugHookCheckedProgramCounter == frame.ProgramCounter &&
            frame.PendingDebugHookEvent is null)
        {
            frame.DebugHookCheckedProgramCounter = -1;
            var dispatchedEvent = frame.DispatchedDebugHookEvent;
            frame.DispatchedDebugHookEvent = null;
            var hasNativeSubject = !thread.DebugHookSubjectFunction.IsNil;
            var completedLuaCallHook =
                dispatchedEvent is "call" or "tail call" &&
                !hasNativeSubject;
            var completedNativeReturnAtNewLine =
                dispatchedEvent == "return" && hasNativeSubject &&
                frame.NativeCallSourceLine > 0 && instruction.SourceLine > 0 &&
                frame.NativeCallSourceLine != instruction.SourceLine;
            if (!completedLuaCallHook && !completedNativeReturnAtNewLine &&
                (instruction.SourceLine > 0 || !frame.HasSourceLineInformation))
            {
                frame.LastDebugHookProgramCounter = frame.ProgramCounter;
            }

            if (dispatchedEvent == "return" && hasNativeSubject)
            {
                frame.NativeCallSourceLine = -1;
            }

            thread.DebugHookSubjectFunction = LuaValue.Nil;
            thread.ClearDebugHookTransfer();
            resumingHookedInstruction =
                !completedLuaCallHook && !completedNativeReturnAtNewLine;
        }

        string? hookEvent = null;
        var line = instruction.SourceLine > 0 ? instruction.SourceLine : -1;
        var countDue = HasDebugHook(thread, LuaDebugHookMask.Count) &&
            thread.DebugHookCount > 0 && !resumingHookedInstruction &&
            IsCountableDebugInstruction(instruction) &&
            --thread.DebugHookCounter <= 0;
        if (countDue)
        {
            thread.DebugHookCounter = thread.DebugHookCount;
        }

        if (frame.PendingDebugHookEvent is { } pending)
        {
            hookEvent = pending;
            frame.PendingDebugHookEvent = null;
        }
        else if (instruction.Opcode == LuaIrOpcode.Return &&
            HasDebugHook(thread, LuaDebugHookMask.Return) &&
            frame.ToBeClosedSlots.Count == 0 &&
            frame.ReturnHookProgramCounter != frame.ProgramCounter)
        {
            hookEvent = "return";
            frame.ReturnHookProgramCounter = frame.ProgramCounter;
        }
        else if (!resumingHookedInstruction &&
            HasDebugHook(thread, LuaDebugHookMask.Line) &&
            (!frame.HasSourceLineInformation && frame.LastLineHookProgramCounter < 0 ||
                line > 0 &&
                (line != frame.LastDebugHookLine ||
                    frame.LastDebugHookProgramCounter >= 0 &&
                    frame.ProgramCounter <= frame.LastDebugHookProgramCounter &&
                    !WasBackEdgeLineAlreadyReported(frame))))
        {
            hookEvent = "line";
            frame.LastDebugHookLine = line;
            frame.LastLineHookProgramCounter = frame.ProgramCounter;
            if (countDue)
            {
                frame.PendingDebugHookEvent = "count";
            }
        }
        else if (countDue)
        {
            hookEvent = "count";
            line = -1;
        }

        if (hookEvent is null)
        {
            if (line > 0 || !frame.HasSourceLineInformation)
            {
                frame.LastDebugHookProgramCounter = frame.ProgramCounter;
            }

            return false;
        }

        if (hookEvent != "line")
        {
            line = -1;
        }

        if (hookEvent == "return" && instruction.Opcode == LuaIrOpcode.Return &&
            thread.DebugHookSubjectFunction.IsNil)
        {
            if (frame.Continuation.Kind == LuaContinuationKind.ReturnAndClose)
            {
                thread.SetDebugHookTransfer(
                    frame.Continuation.Values,
                    isNative: false);
            }
            else
            {
                var start = frame.Base + instruction.A;
                var count = instruction.B < 0 ? Math.Max(0, frame.Top - start) : instruction.B;
                thread.SetDebugHookTransfer(
                    thread.Stack.AsReadOnlySpan(start, count),
                    isNative: false);
            }
        }
        else if (hookEvent is not ("call" or "tail call" or "return"))
        {
            thread.ClearDebugHookTransfer();
        }

        frame.DebugHookCheckedProgramCounter = frame.ProgramCounter;
        frame.DispatchedDebugHookEvent = hookEvent;
        var arguments = ArrayPool<LuaValue>.Shared.Rent(2);
        try
        {
            arguments[0] = LuaValue.FromString(state.GetDebugHookEventString(hookEvent));
            arguments[1] = line < 0 ? LuaValue.Nil : LuaValue.FromInteger(line);
            ReadOnlySpan<LuaValue> argumentSpan = arguments.AsSpan(0, 2);
            var hook = thread.DebugHook;
            thread.IsRunningDebugHook = true;
            if (hook.TryGetClosure() is { } closure)
            {
                var hookFrame = PushFrame(
                    thread,
                    closure,
                    argumentSpan,
                    Math.Max(frame.Top, frame.Base + frame.Function.RegisterCount),
                    expectedResults: 0,
                    isDebugHook: true);
                hookFrame.Continuation.IsYieldBarrier = true;
                return true;
            }

            try
            {
                var native = hook.TryGetNativeFunction() ??
                    throw new LuaRuntimeException("invalid hook function");
                if (native.StepBody is not null)
                {
                    throw new LuaRuntimeException("resumable native functions cannot be debug hooks");
                }

                _ = InvokeNativeBody(state, hook, argumentSpan);
                return false;
            }
            finally
            {
                thread.IsRunningDebugHook = false;
            }
        }
        finally
        {
            ArrayPool<LuaValue>.Shared.Return(arguments, clearArray: true);
        }
    }

    private static bool WasBackEdgeLineAlreadyReported(LuaFrame frame)
    {
        var instructions = frame.Function.Instructions;
        var previousPc = frame.LastDebugHookProgramCounter;
        var hookPc = frame.LastLineHookProgramCounter;
        if (previousPc < 0 || previousPc >= instructions.Length ||
            hookPc < 0 || hookPc >= instructions.Length)
        {
            return false;
        }

        var previous = instructions[previousPc];
        var reported = instructions[hookPc];
        return previous.SourceLine == reported.SourceLine &&
            previous.Span.Equals(reported.Span);
    }

    private static bool IsCountableDebugInstruction(LuaIrInstruction instruction) =>
        instruction.Opcode is not (
            LuaIrOpcode.Move or
            LuaIrOpcode.SetTop or
            LuaIrOpcode.GetUpvalue or
            LuaIrOpcode.Close);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool HasDebugHook(LuaThread thread, LuaDebugHookMask mask) =>
        (thread.DebugHookMask & mask) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int AddStackOffset(int start, int count)
    {
        Debug.Assert(start >= 0);
        Debug.Assert(count >= 0);
        var result = unchecked(start + count);
        Debug.Assert(result >= start);
        return result;
    }
}
