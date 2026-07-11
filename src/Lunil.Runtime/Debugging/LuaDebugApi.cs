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
        thread.DebugHookCounter = thread.DebugHookCount;
        foreach (var frame in thread.Frames)
        {
            frame.LastDebugHookLine = mask.HasFlag(LuaDebugHookMask.Line)
                ? GetCurrentLine(frame)
                : -1;
            frame.DebugHookCheckedProgramCounter = -1;
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
            if (frame.IsDebugHook || frame.IsHidden)
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

    public static int GetCurrentLine(LuaFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var instructions = frame.Closure.Function.Instructions;
        if (instructions.IsEmpty)
        {
            return -1;
        }

        var pc = Math.Clamp(frame.ProgramCounter, 0, instructions.Length - 1);
        return instructions[pc].SourceLine == 0 ? -1 : instructions[pc].SourceLine;
    }

    public static LuaDebugLocal? GetLocal(LuaThread thread, LuaFrame frame, int index)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(frame);
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

        var active = ActiveLocals(frame).ToArray();
        if (index <= active.Length)
        {
            return new LuaDebugLocal(
                Name(active[index - 1].Name),
                thread.Stack[frame.Base + index - 1]);
        }

        var register = index - 1;
        return register < frame.Closure.Function.RegisterCount && frame.Base + register < frame.Top
            ? new LuaDebugLocal("(temporary)", thread.Stack[frame.Base + register])
            : null;
    }

    public static string? GetLocalName(LuaClosure closure, int index)
    {
        if (index <= 0)
        {
            return null;
        }

        var locals = closure.Function.LocalVariables;
        return index <= locals.Length ? Name(locals[index - 1].Name) : null;
    }

    public static string? SetLocal(LuaThread thread, LuaFrame frame, int index, LuaValue value)
    {
        thread.Owner.ValidateValue(value);
        if (index < 0)
        {
            return null;
        }

        var local = GetLocal(thread, frame, index);
        if (local is null)
        {
            return null;
        }

        thread.Stack[frame.Base + index - 1] = value;
        return local.Value.Name;
    }

    public static IEnumerable<int> GetActiveLines(LuaClosure closure)
    {
        var function = closure.Function;
        var lines = function.Instructions.Select(static instruction => instruction.SourceLine)
            .Where(line => line > 0 &&
                (function.LineDefined == 0 || line > function.LineDefined) &&
                (function.LastLineDefined == 0 || line <= function.LastLineDefined))
            .Distinct()
            .ToList();
        if (lines.Count != 0 && function.LastLineDefined > 0)
        {
            lines.Add(function.LastLineDefined);
        }

        return lines.Distinct().Order();
    }

    private static IEnumerable<LuaIrLocalVariable> ActiveLocals(LuaFrame frame)
    {
        var pc = frame.ProgramCounter;
        return frame.Closure.Function.LocalVariables.Where(local =>
            local.StartProgramCounter <= pc && pc < local.EndProgramCounter);
    }

    private static string Name(IEnumerable<byte> bytes) =>
        System.Text.Encoding.UTF8.GetString([.. bytes]);
}
