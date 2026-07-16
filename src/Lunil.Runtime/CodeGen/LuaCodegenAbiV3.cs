using System.ComponentModel;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Operations;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.CodeGen;

/// <summary>
/// Runtime ABI v3 adds non-numeric instruction helpers and guarded table/call inline caches.
/// The caches expose semantic operations only; LuaTable bucket storage remains private.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class LuaCodegenAbiV3
{
    public const int RuntimeAbiVersion = 3;
    internal const int CompiledBackedgeSafepointQuantum = 256;
    private const int CompiledSafepointObjectBudget =
        CompiledBackedgeSafepointQuantum * 8;

    public static void ExecuteNewTable(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        int destinationRegister,
        int hashCapacityBits,
        int arrayCapacity)
    {
        ArgumentNullException.ThrowIfNull(context);
        var allocationHint = frame.GetOrCreateTableAllocationHint(
            frame.ProgramCounter);
        WriteRegisterAndExtendTop(
            thread,
            frame,
            destinationRegister,
            LuaValue.FromTable(context.State.CreateTableForAllocationSite(
                arrayCapacity,
                hashCapacityBits == 0 ? 0 : 1 << (hashCapacityBits - 1),
                allocationHint)));
        frame.ProgramCounter++;
    }

    public static bool ExecuteGetTable(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        int destinationRegister,
        int targetRegister,
        int keyRegister)
    {
        var target = LuaCodegenAbiV2.ReadRegisterUnchecked(thread, frame, targetRegister);
        var key = LuaCodegenAbiV2.ReadRegisterUnchecked(thread, frame, keyRegister);
        if (target.TryGetTable() is { Metatable: null } table)
        {
            var value = key.IsInteger &&
                table.TryGetArrayValue(key.AsIntegerUnchecked(), out var arrayValue)
                    ? arrayValue
                    : table.Get(key);
            WriteRegisterAndExtendTop(
                thread,
                frame,
                destinationRegister,
                value);
            frame.ProgramCounter++;
            return true;
        }

        var engine = RequireEngine(context, thread);
        engine.ExecuteOperation(
            context.State,
            context.Scheduler ?? throw new InvalidOperationException(
                "The compiled scheduler is unavailable."),
            thread,
            frame,
            LuaRuntimeOperations.GetIndex(
                context.State,
                target,
                key),
            frame.Base + destinationRegister,
            expectedResults: 1);
        return ReferenceEquals(thread.CurrentFrame, frame);
    }

    public static bool ExecuteSetTable(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        int targetRegister,
        int keyRegister,
        int valueRegister)
    {
        var target = LuaCodegenAbiV2.ReadRegisterUnchecked(thread, frame, targetRegister);
        var key = LuaCodegenAbiV2.ReadRegisterUnchecked(thread, frame, keyRegister);
        var value = LuaCodegenAbiV2.ReadRegisterUnchecked(thread, frame, valueRegister);
        if (target.TryGetTable() is { Metatable: null } table)
        {
            if (!key.IsInteger ||
                !table.TrySetOrAppendArrayValue(key.AsIntegerUnchecked(), value))
            {
                table.Set(key, value);
            }

            frame.ProgramCounter++;
            return true;
        }

        var engine = RequireEngine(context, thread);
        engine.ExecuteOperation(
            context.State,
            context.Scheduler ?? throw new InvalidOperationException(
                "The compiled scheduler is unavailable."),
            thread,
            frame,
            LuaRuntimeOperations.SetIndex(
                context.State,
                target,
                key,
                value),
            frame.Top,
            expectedResults: 0);
        return ReferenceEquals(thread.CurrentFrame, frame);
    }

    public static void ExecuteSetList(
        LuaThread thread,
        LuaFrame frame,
        int tableRegister,
        int sourceRegister,
        int count,
        int offset)
    {
        LuaExecutionEngine.ExecuteSetList(
            thread,
            frame,
            new IR.Canonical.LuaIrInstruction(
                IR.Canonical.LuaIrOpcode.SetList,
                tableRegister,
                sourceRegister,
                count,
                offset));
        frame.ProgramCounter++;
    }

    public static void ExecuteClosure(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        int destinationRegister,
        int functionId)
    {
        ArgumentNullException.ThrowIfNull(context);
        WriteRegisterAndExtendTop(
            thread,
            frame,
            destinationRegister,
            LuaValue.FromFunction(LuaExecutionEngine.CreateClosure(thread, frame, functionId)));
        frame.ProgramCounter++;
    }

    public static void ExecuteVarArg(
        LuaThread thread,
        LuaFrame frame,
        int destinationRegister,
        int count)
    {
        LuaExecutionEngine.ExecuteVarArg(
            thread,
            frame,
            new IR.Canonical.LuaIrInstruction(
                IR.Canonical.LuaIrOpcode.VarArg,
                destinationRegister,
                count));
        frame.ProgramCounter++;
    }

    public static LuaCodegenPicExecutionResult TryExecuteTableGetPic(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        LuaCodegenTableSiteCache cache,
        int destinationRegister,
        int targetRegister,
        int keyRegister)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cache);
        var target = LuaCodegenAbiV2.ReadRegisterUnchecked(thread, frame, targetRegister);
        if (target.TryGetTable() is not { } table)
        {
            return LuaCodegenPicExecutionResult.GuardFailure;
        }

        var key = LuaCodegenAbiV2.ReadRegisterUnchecked(thread, frame, keyRegister);
        var exists = table.TryGetExistingEntry(key, out var value, out _);
        if (!exists && !cache.CanBypass(table, LuaMetamethod.Index))
        {
            return LuaCodegenPicExecutionResult.GuardFailure;
        }

        if (!context.TryReserveInstructions(1))
        {
            return LuaCodegenPicExecutionResult.InstructionBudget;
        }

        WriteRegisterAndExtendTop(
            thread,
            frame,
            destinationRegister,
            value);
        frame.ProgramCounter++;
        return LuaCodegenPicExecutionResult.Executed;
    }

    public static LuaCodegenPicExecutionResult TryExecuteTableSetPic(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        LuaCodegenTableSiteCache cache,
        int targetRegister,
        int keyRegister,
        int valueRegister)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cache);
        var target = LuaCodegenAbiV2.ReadRegisterUnchecked(thread, frame, targetRegister);
        if (target.TryGetTable() is not { } table)
        {
            return LuaCodegenPicExecutionResult.GuardFailure;
        }

        var key = LuaCodegenAbiV2.ReadRegisterUnchecked(thread, frame, keyRegister);
        var exists = table.TryGetExistingEntry(key, out _, out var entry);
        if (!exists && !cache.CanBypass(table, LuaMetamethod.NewIndex))
        {
            return LuaCodegenPicExecutionResult.GuardFailure;
        }

        if (!context.TryReserveInstructions(1))
        {
            return LuaCodegenPicExecutionResult.InstructionBudget;
        }

        var value = LuaCodegenAbiV2.ReadRegisterUnchecked(thread, frame, valueRegister);
        if (exists)
        {
            table.SetExistingEntry(entry, key, value);
        }
        else
        {
            if (!key.IsInteger ||
                !table.TrySetOrAppendArrayValue(key.AsIntegerUnchecked(), value))
            {
                table.Set(key, value);
            }
        }

        frame.ProgramCounter++;
        return LuaCodegenPicExecutionResult.Executed;
    }

    public static bool CanExecuteKnownClosureCall(
        LuaThread thread,
        LuaFrame frame,
        LuaCodegenCallSiteCache cache,
        int functionRegister,
        int expectedFunctionId)
    {
        ArgumentNullException.ThrowIfNull(cache);
        var function = LuaCodegenAbiV2.ReadRegisterUnchecked(thread, frame, functionRegister);
        var closure = function.TryGetClosure();
        return closure is not null && closure.Function.Id == expectedFunctionId &&
            cache.TryMatchOrAdd(closure);
    }

    public static int TryExecuteFramelessCall(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        int functionRegister,
        int argumentCount,
        int expectedResults)
    {
        ArgumentNullException.ThrowIfNull(context);
        var closure = LuaCodegenAbiV2.ReadRegisterUnchecked(
            thread,
            frame,
            functionRegister).TryGetClosure();
        if (closure is null)
        {
            return 0;
        }

        var argumentStart = checked(frame.Base + functionRegister + 1);
        var actualArgumentCount = argumentCount < 0
            ? Math.Max(0, frame.Top - argumentStart)
            : argumentCount;
        if (!RequireEngine(context, thread).TryExecuteFramelessCall(
                context.State,
                context,
                context.Scheduler,
                thread,
                frame,
                closure,
                argumentStart,
                actualArgumentCount,
                frame.Base + functionRegister,
                expectedResults))
        {
            return 0;
        }

        frame.ProgramCounter++;
        return closure.FramelessInstructionCount;
    }

    public static bool CanContinueAfterFramelessCall(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ReferenceEquals(thread.CurrentFrame, frame) &&
            context.State.Heap.PendingFinalizerCount == 0;
    }

    public static bool PollGcSafepoint(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(thread);
        ArgumentNullException.ThrowIfNull(frame);
        if (!ReferenceEquals(context.Thread, thread) ||
            !ReferenceEquals(thread.CurrentFrame, frame))
        {
            return false;
        }

        // One compiled quantum can allocate substantially more objects than one scheduler turn.
        // Advance enough bounded GC work to keep allocation debt from outpacing collection.
        context.State.Heap.SafePoint(CompiledSafepointObjectBudget);
        return ReferenceEquals(thread.CurrentFrame, frame) &&
            context.State.Heap.PendingFinalizerCount == 0;
    }

    public static void ExecuteKnownClosureCall(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        int functionRegister,
        int argumentCount,
        int expectedResults)
    {
        var closure = LuaCodegenAbiV2.ReadRegisterUnchecked(
            thread,
            frame,
            functionRegister).TryGetClosure() ?? throw new InvalidOperationException(
                "Known-closure call execution lost its guarded target.");
        RequireEngine(context, thread).ExecuteKnownClosureCall(
            context,
            thread,
            frame,
            closure,
            functionRegister,
            argumentCount,
            expectedResults);
    }

    public static void ExecuteKnownClosureTailCall(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        int functionRegister,
        int argumentCount)
    {
        var closure = LuaCodegenAbiV2.ReadRegisterUnchecked(
            thread,
            frame,
            functionRegister).TryGetClosure() ?? throw new InvalidOperationException(
                "Known-closure tail-call execution lost its guarded target.");
        RequireEngine(context, thread).ExecuteKnownClosureTailCall(
            context.State,
            thread,
            frame,
            closure,
            functionRegister,
            argumentCount);
    }

    private static void WriteRegisterAndExtendTop(
        LuaThread thread,
        LuaFrame frame,
        int register,
        LuaValue value)
    {
        var index = frame.Base + register;
        thread.Stack.WriteUnchecked(index, value);
        if (index >= frame.Top)
        {
            frame.Top = index + 1;
        }
    }

    private static LuaExecutionEngine RequireEngine(
        LuaExecutionContext context,
        LuaThread thread)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(thread);
        if (!ReferenceEquals(context.Thread, thread) || context.ExecutionEngine is not { } engine)
        {
            throw new InvalidOperationException(
                "The execution context does not belong to this execution engine and thread.");
        }

        return engine;
    }
}

/// <summary>Outcome of a fused guarded table PIC operation.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public enum LuaCodegenPicExecutionResult : byte
{
    GuardFailure,
    InstructionBudget,
    Executed,
}

/// <summary>Polymorphic cache for metatables proven not to define a requested metamethod.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class LuaCodegenTableSiteCache
{
    private const int MaximumEntries = 4;
    private readonly Lock _gate = new();
    private Entry[] _entries = [];

    internal bool CanBypass(LuaTable table, LuaMetamethod metamethod)
    {
        var metatable = table.Metatable;
        if (metatable is null)
        {
            return true;
        }

        var entries = Volatile.Read(ref _entries);
        foreach (var entry in entries)
        {
            if (entry.Metatable.TryGetTarget(out var cached) && cached.IsAlive &&
                entry.Metamethod == metamethod &&
                ReferenceEquals(cached, metatable) &&
                entry.ContentVersion == metatable.ContentVersion)
            {
                return true;
            }
        }

        if (!metatable.GetMetamethodField(metamethod).IsNil)
        {
            return false;
        }

        lock (_gate)
        {
            entries = Volatile.Read(ref _entries);
            var replacement = -1;
            for (var index = 0; index < entries.Length; index++)
            {
                if (!entries[index].Metatable.TryGetTarget(out var cached) || !cached.IsAlive)
                {
                    replacement = index;
                    continue;
                }

                if (entries[index].Metamethod == metamethod &&
                    ReferenceEquals(cached, metatable))
                {
                    if (entries[index].ContentVersion == metatable.ContentVersion)
                    {
                        return true;
                    }

                    replacement = index;
                    break;
                }
            }

            var newEntry = new Entry(
                new WeakReference<LuaTable>(metatable),
                metatable.ContentVersion,
                metamethod);
            if (replacement >= 0)
            {
                var updated = (Entry[])entries.Clone();
                updated[replacement] = newEntry;
                Volatile.Write(ref _entries, updated);
                return true;
            }

            if (entries.Length < MaximumEntries)
            {
                var updated = new Entry[entries.Length + 1];
                entries.CopyTo(updated, 0);
                updated[^1] = newEntry;
                Volatile.Write(ref _entries, updated);
                return true;
            }

            return false;
        }
    }

    private sealed record Entry(
        WeakReference<LuaTable> Metatable,
        ulong ContentVersion,
        LuaMetamethod Metamethod);
}

/// <summary>Weak polymorphic cache used by guarded direct Lua-closure dispatch.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class LuaCodegenCallSiteCache
{
    private const int MaximumEntries = 4;
    private readonly Lock _gate = new();
    private WeakReference<LuaClosure>[] _entries = [];

    internal bool TryMatchOrAdd(LuaClosure closure)
    {
        var entries = Volatile.Read(ref _entries);
        foreach (var entry in entries)
        {
            if (entry.TryGetTarget(out var cached) && cached.IsAlive &&
                ReferenceEquals(cached, closure))
            {
                return true;
            }
        }

        lock (_gate)
        {
            entries = Volatile.Read(ref _entries);
            var replacement = -1;
            for (var index = 0; index < entries.Length; index++)
            {
                if (!entries[index].TryGetTarget(out var cached) || !cached.IsAlive)
                {
                    replacement = index;
                }
                else if (ReferenceEquals(cached, closure))
                {
                    return true;
                }
            }

            var newEntry = new WeakReference<LuaClosure>(closure);
            if (replacement >= 0)
            {
                var updated = (WeakReference<LuaClosure>[])entries.Clone();
                updated[replacement] = newEntry;
                Volatile.Write(ref _entries, updated);
                return true;
            }

            if (entries.Length >= MaximumEntries)
            {
                return false;
            }

            var expanded = new WeakReference<LuaClosure>[entries.Length + 1];
            entries.CopyTo(expanded, 0);
            expanded[^1] = newEntry;
            Volatile.Write(ref _entries, expanded);
            return true;
        }
    }
}
