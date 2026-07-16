using Lunil.IR.Canonical;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.Runtime;

[Flags]
public enum LuaDebugHookMask : byte
{
    None = 0,
    Call = 1,
    Return = 2,
    Line = 4,
    Count = 8,
}

public readonly record struct LuaDebugLocal(string Name, LuaValue Value);

/// <summary>Owner-checked stack, local, upvalue, and hook operations used by debug libraries.</summary>
public static class LuaDebugApi
{
    public static void SetHook(
        LuaState state,
        LuaThread thread,
        LuaValue hook,
        LuaDebugHookMask mask,
        int count)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(thread);
        state.Heap.ValidateValue(LuaValue.FromThread(thread));
        if (!hook.IsNil && hook.Kind != LuaValueKind.Function)
        {
            throw new LuaRuntimeException("hook must be a function or nil");
        }

        state.Heap.ValidateValue(hook);
        thread.DebugHook = hook;
        thread.DebugHookMask = hook.IsNil ? LuaDebugHookMask.None : mask;
        thread.DebugHookCount = Math.Max(count, 0);
        thread.DebugModeVersion = unchecked(thread.DebugModeVersion + 1);
        thread.DebugHookCounter = thread.DebugHookCount;
        foreach (var frame in thread.Frames)
        {
            var instructionPending =
                frame.DebugHookCheckedProgramCounter == frame.ProgramCounter;
            frame.LastDebugHookLine = mask.HasFlag(LuaDebugHookMask.Line)
                ? instructionPending ? -1 : GetCurrentLine(frame)
                : -1;
            frame.LastDebugHookProgramCounter = frame.ProgramCounter;
            frame.LastLineHookProgramCounter = instructionPending
                ? -1
                : frame.ProgramCounter;
            if (!instructionPending)
            {
                frame.DebugHookCheckedProgramCounter = -1;
            }
        }

        thread.Owner.WriteBarrier(thread, hook);
    }

    public static (LuaValue Hook, LuaDebugHookMask Mask, int Count) GetHook(
        LuaState state,
        LuaThread thread)
    {
        state.Heap.ValidateValue(LuaValue.FromThread(thread));
        return (thread.DebugHook, thread.DebugHookMask, thread.DebugHookCount);
    }

    public static bool TryGetHookSubject(
        LuaState state,
        LuaThread thread,
        out LuaValue function)
    {
        state.Heap.ValidateValue(LuaValue.FromThread(thread));
        function = thread.DebugHookSubjectFunction;
        return thread.IsRunningDebugHook && !function.IsNil;
    }

    public static bool TryGetHookTransferRange(
        LuaState state,
        LuaThread thread,
        out int start,
        out int count)
    {
        state.Heap.ValidateValue(LuaValue.FromThread(thread));
        start = thread.DebugHookTransferStart;
        count = thread.IsRunningDebugHook ? thread.DebugHookTransferValues.Count : 0;
        return count != 0;
    }

    public static LuaDebugLocal? GetHookTransfer(
        LuaState state,
        LuaThread thread,
        int index)
    {
        state.Heap.ValidateValue(LuaValue.FromThread(thread));
        if (!thread.IsRunningDebugHook)
        {
            return null;
        }

        var offset = index - thread.DebugHookTransferStart;
        if (offset < 0 || offset >= thread.DebugHookTransferValues.Count)
        {
            return null;
        }

        return new LuaDebugLocal(
            thread.DebugHookTransferIsNative ? "(C temporary)" : "(temporary)",
            thread.DebugHookTransferValues[offset]);
    }

    public static LuaFrame? GetFrame(LuaState state, LuaThread thread, int level)
    {
        state.Heap.ValidateValue(LuaValue.FromThread(thread));
        if (level < 0)
        {
            return null;
        }

        var remaining = level;
        for (var index = thread.Frames.Count - 1; index >= 0; index--)
        {
            var frame = thread.Frames[index];
            if (frame.IsHidden)
            {
                continue;
            }

            if (remaining-- == 0)
            {
                return frame;
            }
        }

        return null;
    }

    public static LuaValue GetFunction(LuaFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return frame.DebugFunctionOverride.IsNil
            ? LuaValue.FromFunction(frame.Closure)
            : frame.DebugFunctionOverride;
    }

    public static int GetCurrentLine(LuaFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!frame.DebugFunctionOverride.IsNil)
        {
            return -1;
        }

        var instructions = frame.Function.Instructions;
        if (instructions.IsEmpty)
        {
            return -1;
        }

        var pc = Math.Clamp(frame.ProgramCounter, 0, instructions.Length - 1);
        return instructions[pc].SourceLine == 0 ? -1 : instructions[pc].SourceLine;
    }

    public static int GetCurrentLine(LuaThread thread, LuaFrame frame)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(frame);
        if (!frame.DebugFunctionOverride.IsNil)
        {
            return -1;
        }

        var instructions = frame.Function.Instructions;
        if (instructions.IsEmpty)
        {
            return -1;
        }

        var effectiveProgramCounter = ReferenceEquals(thread.CurrentFrame, frame)
            ? EffectiveProgramCounter(thread, frame)
            : Math.Max(0, frame.ProgramCounter - 1);
        var pc = Math.Clamp(effectiveProgramCounter, 0, instructions.Length - 1);
        return instructions[pc].SourceLine == 0 ? -1 : instructions[pc].SourceLine;
    }

    public static LuaDebugLocal? GetLocal(LuaThread thread, LuaFrame frame, int index)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(frame);
        if (!frame.DebugFunctionOverride.IsNil)
        {
            return null;
        }

        if (index < 0)
        {
            var vararg = -index;
            return vararg <= frame.VarArgs.Count
                ? new LuaDebugLocal("(vararg)", frame.VarArgs[vararg - 1])
                : null;
        }

        if (index == 0)
        {
            return null;
        }

        return TryResolveLocalSlot(thread, frame, index, out var name, out var stackIndex)
            ? new LuaDebugLocal(name, thread.Stack[stackIndex])
            : null;
    }

    public static string? GetLocalName(LuaClosure closure, int index)
    {
        if (index <= 0)
        {
            return null;
        }

        var locals = closure.Function.LocalVariables;
        return index <= closure.Function.ParameterCount && index <= locals.Length
            ? Name(locals[index - 1].Name)
            : null;
    }

    public static string? SetLocal(LuaThread thread, LuaFrame frame, int index, LuaValue value)
    {
        thread.Owner.ValidateValue(value);
        if (!frame.DebugFunctionOverride.IsNil)
        {
            return null;
        }

        if (index < 0)
        {
            var vararg = -index;
            if (vararg > frame.VarArgStorage.Count)
            {
                return null;
            }

            frame.VarArgStorage[vararg - 1] = value;
            thread.Owner.WriteBarrier(thread, value);
            return "(vararg)";
        }

        if (!TryResolveLocalSlot(thread, frame, index, out var name, out var stackIndex))
        {
            return null;
        }

        thread.Stack[stackIndex] = value;
        return name;
    }

    public static IEnumerable<int> GetActiveLines(LuaClosure closure)
    {
        var function = closure.Function;
        var lines = function.Instructions.Select(static instruction => instruction.SourceLine)
            .Where(line => line > 0 &&
                (function.LineDefined == 0 || line > function.LineDefined ||
                    function.LineDefined == function.LastLineDefined &&
                    line == function.LineDefined) &&
                (function.LastLineDefined == 0 || line <= function.LastLineDefined))
            .Distinct()
            .ToList();
        if (lines.Count != 0 && function.LastLineDefined > 0)
        {
            lines.Add(function.LastLineDefined);
        }

        return lines.Distinct().Order();
    }

    private static IEnumerable<LuaIrLocalVariable> ActiveLocals(
        LuaThread thread,
        LuaFrame frame)
    {
        var pc = EffectiveProgramCounter(thread, frame);
        return frame.Function.LocalVariables.Where(local =>
            local.StartProgramCounter <= pc && pc < local.EndProgramCounter);
    }

    private static bool TryResolveLocalSlot(
        LuaThread thread,
        LuaFrame frame,
        int index,
        out string name,
        out int stackIndex)
    {
        var active = ActiveLocals(thread, frame).ToArray();
        if (index <= active.Length)
        {
            name = Name(active[index - 1].Name);
            stackIndex = frame.Base + index - 1;
            return true;
        }

        var temporary = index - active.Length;
        foreach (var register in ActiveTemporaryRegisters(thread, frame, active.Length))
        {
            if (--temporary != 0)
            {
                continue;
            }

            name = "(temporary)";
            stackIndex = frame.Base + register;
            return true;
        }

        name = string.Empty;
        stackIndex = -1;
        return false;
    }

    private static IEnumerable<int> ActiveTemporaryRegisters(
        LuaThread thread,
        LuaFrame frame,
        int localCount)
    {
        var function = frame.Function;
        var defined = new bool[function.RegisterCount];
        var limit = Math.Clamp(
            EffectiveProgramCounter(thread, frame),
            0,
            function.Instructions.Length);
        var activeCallStart = function.RegisterCount;
        if (thread.Status == LuaThreadStatus.Suspended && limit < function.Instructions.Length &&
            function.Instructions[limit] is
            {
                Opcode: LuaIrOpcode.Call or LuaIrOpcode.TailCall,
            } suspendedCall)
        {
            activeCallStart = suspendedCall.A;
        }
        else if (!ReferenceEquals(thread.CurrentFrame, frame) && limit > 0 &&
            function.Instructions[limit - 1] is
            {
                Opcode: LuaIrOpcode.Call or LuaIrOpcode.TailCall,
            } activeCall)
        {
            activeCallStart = activeCall.A;
            limit--;
        }

        for (var pc = 0; pc < limit; pc++)
        {
            var instruction = function.Instructions[pc];
            switch (instruction.Opcode)
            {
                case LuaIrOpcode.SetTop:
                    Array.Clear(defined, Math.Clamp(instruction.A, 0, defined.Length),
                        Math.Max(0, defined.Length - Math.Clamp(instruction.A, 0, defined.Length)));
                    break;
                case LuaIrOpcode.JumpIfFalse:
                case LuaIrOpcode.JumpIfTrue:
                    if (instruction.D != 0)
                    {
                        Array.Clear(defined, Math.Clamp(instruction.C, 0, defined.Length),
                            Math.Max(0, defined.Length - Math.Clamp(
                                instruction.C,
                                0,
                                defined.Length)));
                    }

                    break;
                case LuaIrOpcode.LoadNil:
                    MarkDefined(defined, instruction.A, Math.Max(instruction.B, 1));
                    break;
                case LuaIrOpcode.VarArg:
                    if (instruction.B > 0)
                    {
                        MarkDefined(defined, instruction.A, instruction.B);
                    }
                    break;
                case LuaIrOpcode.Call:
                    if (instruction.C > 0)
                    {
                        MarkDefined(defined, instruction.A, instruction.C);
                    }
                    break;
                case LuaIrOpcode.LoadConstant:
                case LuaIrOpcode.Move:
                case LuaIrOpcode.GetUpvalue:
                case LuaIrOpcode.NewTable:
                case LuaIrOpcode.GetTable:
                case LuaIrOpcode.Closure:
                case LuaIrOpcode.Unary:
                case LuaIrOpcode.Binary:
                    MarkDefined(defined, instruction.A, 1);
                    break;
            }
        }

        var pendingClosureEnd = 0;
        if (limit < function.Instructions.Length &&
            function.Instructions[limit].Opcode == LuaIrOpcode.Closure)
        {
            MarkDefined(defined, function.Instructions[limit].A, 1);
            pendingClosureEnd = function.Instructions[limit].A + 1;
        }

        var maximum = Math.Min(
            Math.Min(
                function.RegisterCount,
                Math.Max(frame.Top - frame.Base, pendingClosureEnd)),
            activeCallStart);
        for (var register = localCount; register < maximum; register++)
        {
            if (defined[register])
            {
                yield return register;
            }
        }
    }

    private static int EffectiveProgramCounter(LuaThread thread, LuaFrame frame)
    {
        var pc = frame.ProgramCounter;
        var instructions = frame.Function.Instructions;
        if (thread.Status != LuaThreadStatus.Suspended || pc <= 0 || pc > instructions.Length)
        {
            return pc;
        }

        var previous = instructions[pc - 1];
        return previous.Opcode is LuaIrOpcode.Call or LuaIrOpcode.TailCall &&
            thread.Stack[frame.Base + previous.A].TryGetNativeFunction() is not null
                ? pc - 1
                : pc;
    }

    private static void MarkDefined(bool[] defined, int start, int count)
    {
        var first = Math.Clamp(start, 0, defined.Length);
        var end = Math.Clamp(checked(start + count), first, defined.Length);
        for (var register = first; register < end; register++)
        {
            defined[register] = true;
        }
    }

    private static string Name(IEnumerable<byte> bytes) =>
        System.Text.Encoding.UTF8.GetString([.. bytes]);
}
