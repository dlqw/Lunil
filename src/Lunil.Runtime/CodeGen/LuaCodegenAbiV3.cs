using System.ComponentModel;
using System.Runtime.CompilerServices;
using Lunil.IR.Canonical;
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
        LuaTable? cachedTable = null;
        return TryExecuteCompilerProvenTableGetPic(
            context,
            thread,
            frame,
            ref cachedTable,
            cache,
            destinationRegister,
            targetRegister,
            keyRegister);
    }

    internal static LuaCodegenPicExecutionResult TryExecuteCompilerProvenTableGetPic(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        ref LuaTable? cachedTable,
        LuaCodegenTableSiteCache cache,
        int destinationRegister,
        int targetRegister,
        int keyRegister)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cache);
        var table = cachedTable;
        if (table is null)
        {
            var target = LuaCodegenAbiV2.ReadRegisterUnchecked(thread, frame, targetRegister);
            if (target.TryGetTable() is not { } guardedTable)
            {
                return LuaCodegenPicExecutionResult.GuardFailure;
            }

            table = guardedTable;
            cachedTable = guardedTable;
        }

        var key = LuaCodegenAbiV2.ReadRegisterUnchecked(thread, frame, keyRegister);
        LuaValue value;
        bool exists;
        if (key.IsInteger)
        {
            exists = table.TryGetIntegerEntry(
                key,
                key.AsIntegerUnchecked(),
                out value,
                out var entry);
            if (entry.IsArray)
            {
                cache.RecordIntegerFastPathHit(key.AsIntegerUnchecked());
            }
            else
            {
                cache.RecordIntegerFastPathMiss(key.AsIntegerUnchecked());
            }
        }
        else if (key.Kind == LuaValueKind.String)
        {
            var stringKey = key.AsString();
            if (cache.TryGetStringEntry(
                    table,
                    stringKey,
                    out var cachedEntry,
                    out value))
            {
                exists = true;
            }
            else
            {
                exists = table.TryGetExistingEntry(key, out value, out var entry);
                if (exists)
                {
                    cache.ObserveStringEntry(table, stringKey, entry);
                }
            }
        }
        else
        {
            cache.RecordFastPathMiss();
            exists = table.TryGetExistingEntry(key, out value, out _);
        }

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

    internal static LuaCodegenPicExecutionResult TryExecuteCompilerProvenIntegerTableGetPic(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        ref LuaTable? cachedTable,
        LuaCodegenTableSiteCache cache,
        int destinationRegister,
        int targetRegister,
        int keyRegister)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cache);
        var table = cachedTable;
        if (table is null)
        {
            var target = LuaCodegenAbiV2.ReadRegisterUnchecked(thread, frame, targetRegister);
            if (target.TryGetTable() is not { } guardedTable)
            {
                return LuaCodegenPicExecutionResult.GuardFailure;
            }

            table = guardedTable;
            cachedTable = guardedTable;
        }

        var key = LuaCodegenAbiV2.ReadRegisterUnchecked(thread, frame, keyRegister);
        if (!key.IsInteger)
        {
            return LuaCodegenPicExecutionResult.GuardFailure;
        }

        var integerKey = key.AsIntegerUnchecked();
        var exists = table.TryGetIntegerEntry(key, integerKey, out var value, out var entry);
        if (entry.IsArray)
        {
            cache.RecordIntegerFastPathHit(integerKey);
        }
        else
        {
            cache.RecordIntegerFastPathMiss(integerKey);
        }

        if (!exists && !cache.CanBypass(table, LuaMetamethod.Index))
        {
            return LuaCodegenPicExecutionResult.GuardFailure;
        }

        if (!context.TryReserveInstructions(1))
        {
            return LuaCodegenPicExecutionResult.InstructionBudget;
        }

        WriteRegisterAndExtendTop(thread, frame, destinationRegister, value);
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
        LuaTable? cachedTable = null;
        return TryExecuteCompilerProvenTableSetPic(
            context,
            thread,
            frame,
            ref cachedTable,
            cache,
            targetRegister,
            keyRegister,
            valueRegister);
    }

    internal static LuaCodegenPicExecutionResult TryExecuteCompilerProvenTableSetPic(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        ref LuaTable? cachedTable,
        LuaCodegenTableSiteCache cache,
        int targetRegister,
        int keyRegister,
        int valueRegister)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cache);
        var table = cachedTable;
        if (table is null)
        {
            var target = LuaCodegenAbiV2.ReadRegisterUnchecked(thread, frame, targetRegister);
            if (target.TryGetTable() is not { } guardedTable)
            {
                return LuaCodegenPicExecutionResult.GuardFailure;
            }

            table = guardedTable;
            cachedTable = guardedTable;
        }

        var key = LuaCodegenAbiV2.ReadRegisterUnchecked(thread, frame, keyRegister);
        if (key.IsInteger && table.Metatable is null)
        {
            if (!context.TryReserveInstructions(1))
            {
                return LuaCodegenPicExecutionResult.InstructionBudget;
            }

            var denseValue = LuaCodegenAbiV2.ReadRegisterUnchecked(
                thread,
                frame,
                valueRegister);
            if (table.SetIntegerValueNoMetatable(
                    key,
                    key.AsIntegerUnchecked(),
                    denseValue))
            {
                cache.RecordIntegerFastPathHit(key.AsIntegerUnchecked());
            }
            else
            {
                cache.RecordIntegerFastPathMiss(key.AsIntegerUnchecked());
            }

            frame.ProgramCounter++;
            return LuaCodegenPicExecutionResult.Executed;
        }

        LuaTableExistingEntry entry;
        bool exists;
        var cachedStringEntry = false;
        if (key.IsInteger)
        {
            exists = table.TryGetIntegerEntry(
                key,
                key.AsIntegerUnchecked(),
                out _,
                out entry);
            if (entry.IsArray)
            {
                cache.RecordIntegerFastPathHit(key.AsIntegerUnchecked());
            }
            else
            {
                cache.RecordIntegerFastPathMiss(key.AsIntegerUnchecked());
            }
        }
        else if (key.Kind == LuaValueKind.String)
        {
            var stringKey = key.AsString();
            if (cache.TryGetStringEntry(table, stringKey, out entry, out _))
            {
                exists = true;
                cachedStringEntry = true;
            }
            else
            {
                exists = table.TryGetExistingEntry(key, out _, out entry);
                if (exists)
                {
                    cache.ObserveStringEntry(table, stringKey, entry);
                }
            }
        }
        else
        {
            cache.RecordFastPathMiss();
            exists = table.TryGetExistingEntry(key, out _, out entry);
        }

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
            if (cachedStringEntry)
            {
                table.SetExistingStringEntry(entry, key.AsString(), value);
            }
            else
            {
                table.SetExistingEntry(entry, key, value);
            }
        }
        else
        {
            table.Set(key, value);
        }

        frame.ProgramCounter++;
        return LuaCodegenPicExecutionResult.Executed;
    }

    internal static LuaCodegenPicExecutionResult TryExecuteCompilerProvenIntegerTableSetPic(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        ref LuaTable? cachedTable,
        LuaCodegenTableSiteCache cache,
        int targetRegister,
        int keyRegister,
        int valueRegister)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(cache);
        var table = cachedTable;
        if (table is null)
        {
            var target = LuaCodegenAbiV2.ReadRegisterUnchecked(thread, frame, targetRegister);
            if (target.TryGetTable() is not { } guardedTable)
            {
                return LuaCodegenPicExecutionResult.GuardFailure;
            }

            table = guardedTable;
            cachedTable = guardedTable;
        }

        var key = LuaCodegenAbiV2.ReadRegisterUnchecked(thread, frame, keyRegister);
        if (!key.IsInteger || table.Metatable is not null)
        {
            return LuaCodegenPicExecutionResult.GuardFailure;
        }

        if (!context.TryReserveInstructions(1))
        {
            return LuaCodegenPicExecutionResult.InstructionBudget;
        }

        var integerKey = key.AsIntegerUnchecked();
        var value = LuaCodegenAbiV2.ReadRegisterUnchecked(thread, frame, valueRegister);
        if (table.SetIntegerValueNoMetatable(key, integerKey, value))
        {
            cache.RecordIntegerFastPathHit(integerKey);
        }
        else
        {
            cache.RecordIntegerFastPathMiss(integerKey);
        }

        frame.ProgramCounter++;
        return LuaCodegenPicExecutionResult.Executed;
    }

    internal static bool TryGetCompilerProvenIntegerTableValue(
        ref LuaTable? cachedTable,
        LuaValue target,
        LuaCodegenTableSiteCache cache,
        long integerKey,
        out LuaValue value)
    {
        ArgumentNullException.ThrowIfNull(cache);
        var table = cachedTable;
        if (table is null)
        {
            if (target.TryGetTable() is not { } guardedTable)
            {
                value = LuaValue.Nil;
                return false;
            }

            table = guardedTable;
            cachedTable = guardedTable;
        }

        var key = LuaValue.FromInteger(integerKey);
        var exists = table.TryGetIntegerEntry(key, integerKey, out value, out var entry);
        if (entry.IsArray)
        {
            cache.RecordIntegerFastPathHit(integerKey);
        }
        else
        {
            cache.RecordIntegerFastPathMiss(integerKey);
        }

        return exists || cache.CanBypass(table, LuaMetamethod.Index);
    }

    internal static bool TrySetCompilerProvenIntegerTableValue(
        ref LuaTable? cachedTable,
        LuaValue target,
        LuaCodegenTableSiteCache cache,
        long integerKey,
        LuaValue value)
    {
        ArgumentNullException.ThrowIfNull(cache);
        var table = cachedTable;
        if (table is null)
        {
            if (target.TryGetTable() is not { } guardedTable)
            {
                return false;
            }

            table = guardedTable;
            cachedTable = guardedTable;
        }

        if (table.Metatable is not null)
        {
            return false;
        }

        var key = LuaValue.FromInteger(integerKey);
        if (table.SetIntegerValueNoMetatable(key, integerKey, value))
        {
            cache.RecordIntegerFastPathHit(integerKey);
        }
        else
        {
            cache.RecordIntegerFastPathMiss(integerKey);
        }

        return true;
    }

    internal static bool TryGetCompilerProvenStringTableValue(
        ref LuaTable? cachedTable,
        LuaValue target,
        LuaCodegenTableSiteCache cache,
        LuaValue key,
        out LuaValue value)
    {
        ArgumentNullException.ThrowIfNull(cache);
        var table = cachedTable;
        if (table is null)
        {
            if (target.TryGetTable() is not { } guardedTable)
            {
                value = LuaValue.Nil;
                return false;
            }

            table = guardedTable;
            cachedTable = guardedTable;
        }

        if (key.Kind != LuaValueKind.String)
        {
            value = LuaValue.Nil;
            return false;
        }

        var stringKey = key.AsString();
        bool exists;
        if (cache.TryGetStringEntry(table, stringKey, out var cachedEntry, out value))
        {
            exists = true;
        }
        else
        {
            exists = table.TryGetExistingEntry(key, out value, out var entry);
            if (exists)
            {
                cache.ObserveStringEntry(table, stringKey, entry);
            }
        }

        return exists || cache.CanBypass(table, LuaMetamethod.Index);
    }

    internal static bool TrySetCompilerProvenStringTableValue(
        ref LuaTable? cachedTable,
        LuaValue target,
        LuaCodegenTableSiteCache cache,
        LuaValue key,
        LuaValue value)
    {
        ArgumentNullException.ThrowIfNull(cache);
        var table = cachedTable;
        if (table is null)
        {
            if (target.TryGetTable() is not { } guardedTable)
            {
                return false;
            }

            table = guardedTable;
            cachedTable = guardedTable;
        }

        if (key.Kind != LuaValueKind.String)
        {
            return false;
        }

        var stringKey = key.AsString();
        LuaTableExistingEntry entry;
        bool exists;
        var cachedStringEntry = false;
        if (cache.TryGetStringEntry(table, stringKey, out entry, out _))
        {
            exists = true;
            cachedStringEntry = true;
        }
        else
        {
            exists = table.TryGetExistingEntry(key, out _, out entry);
            if (exists)
            {
                cache.ObserveStringEntry(table, stringKey, entry);
            }
        }

        if (!exists && !cache.CanBypass(table, LuaMetamethod.NewIndex))
        {
            return false;
        }

        if (exists)
        {
            if (cachedStringEntry)
            {
                table.SetExistingStringEntry(entry, stringKey, value);
            }
            else
            {
                table.SetExistingEntry(entry, key, value);
            }
        }
        else
        {
            table.Set(key, value);
        }

        return true;
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

/// <summary>
/// Bounded weak cache for table-entry identities and metatables proven not to define a requested
/// metamethod. Cached entry handles remain opaque and are guarded by table mutation versions.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class LuaCodegenTableSiteCache
{
    private const int MaximumEntries = 4;
    private readonly Lock _gate = new();
    private MetamethodEntry[] _metamethodEntries = [];
    private FieldEntry[] _fieldEntries = [];
    private ILuaCodegenTablePicCounterSink? _counters;
    private bool _sampleCounters;
    private bool _observedFastPathHit;
    private bool _observedFastPathMiss;
    private int _fastPathHitSampleCountdown = 256;
    private int _fastPathMissSampleCountdown = 256;

    public LuaCodegenTableSiteCache()
    {
    }

    internal LuaCodegenTableSiteCache(ILuaCodegenTablePicCounterSink counters)
    {
        ArgumentNullException.ThrowIfNull(counters);
        BindCounters(counters);
    }

    internal void BindCounters(ILuaCodegenTablePicCounterSink counters)
    {
        ArgumentNullException.ThrowIfNull(counters);
        _sampleCounters = counters.SupportsWeightedSampling;
        Volatile.Write(ref _counters, counters);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RecordFastPathHit()
    {
        if (!_sampleCounters)
        {
            _counters?.RecordHit();
            return;
        }

        if (!_observedFastPathHit)
        {
            _observedFastPathHit = true;
            _counters?.RecordHit();
            return;
        }

        if (--_fastPathHitSampleCountdown == 0)
        {
            _fastPathHitSampleCountdown = 256;
            _counters?.RecordHits(256);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RecordFastPathMiss()
    {
        if (!_sampleCounters)
        {
            _counters?.RecordMiss();
            return;
        }

        if (!_observedFastPathMiss)
        {
            _observedFastPathMiss = true;
            _counters?.RecordMiss();
            return;
        }

        if (--_fastPathMissSampleCountdown == 0)
        {
            _fastPathMissSampleCountdown = 256;
            _counters?.RecordMisses(256);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RecordIntegerFastPathHit(long key)
    {
        if (!_sampleCounters)
        {
            _counters?.RecordHit();
            return;
        }

        RecordFastPathHit();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RecordIntegerFastPathMiss(long key)
    {
        if (!_sampleCounters)
        {
            _counters?.RecordMiss();
            return;
        }

        RecordFastPathMiss();
    }

    internal void RecordInvalidation() => _counters?.RecordInvalidation();

    internal int FieldEntryCount => Volatile.Read(ref _fieldEntries).Length;

    internal bool TryGetStringEntry(
        LuaTable table,
        LuaString key,
        out LuaTableExistingEntry handle,
        out LuaValue value)
    {
        var entries = Volatile.Read(ref _fieldEntries);
        foreach (var entry in entries)
        {
            if (!entry.Table.TryGetTarget(out var cachedTable) ||
                !entry.Key.TryGetTarget(out var cachedKey) ||
                !ReferenceEquals(cachedTable, table) ||
                !ReferenceEquals(cachedKey, key))
            {
                continue;
            }

            if (cachedTable.IsAlive && cachedKey.IsAlive &&
                entry.MetatableVersion == table.MetatableVersion &&
                table.TryReadExistingStringEntry(entry.Handle, key, out value))
            {
                handle = entry.Handle;
                RecordFastPathHit();
                return true;
            }

            RecordInvalidation();
            break;
        }

        handle = default;
        value = LuaValue.Nil;
        RecordFastPathMiss();
        return false;
    }

    internal void ObserveStringEntry(
        LuaTable table,
        LuaString key,
        LuaTableExistingEntry handle)
    {
        lock (_gate)
        {
            var entries = Volatile.Read(ref _fieldEntries);
            var replacement = -1;
            for (var index = 0; index < entries.Length; index++)
            {
                if (!entries[index].Table.TryGetTarget(out var cachedTable) ||
                    !entries[index].Key.TryGetTarget(out var cachedKey) ||
                    !cachedTable.IsAlive || !cachedKey.IsAlive)
                {
                    replacement = index;
                    continue;
                }

                if (ReferenceEquals(cachedTable, table) && ReferenceEquals(cachedKey, key))
                {
                    if (entries[index].ShapeVersion == table.ShapeVersion &&
                        entries[index].StorageVersion == table.StorageVersion &&
                        entries[index].MetatableVersion == table.MetatableVersion)
                    {
                        return;
                    }

                    replacement = index;
                    break;
                }
            }

            var newEntry = new FieldEntry(
                new WeakReference<LuaTable>(table),
                new WeakReference<LuaString>(key),
                table.ShapeVersion,
                table.StorageVersion,
                table.MetatableVersion,
                handle);
            if (replacement >= 0)
            {
                var updated = (FieldEntry[])entries.Clone();
                updated[replacement] = newEntry;
                Volatile.Write(ref _fieldEntries, updated);
                return;
            }

            if (entries.Length < MaximumEntries)
            {
                var updated = new FieldEntry[entries.Length + 1];
                entries.CopyTo(updated, 0);
                updated[^1] = newEntry;
                Volatile.Write(ref _fieldEntries, updated);
            }
        }
    }

    internal bool CanBypass(LuaTable table, LuaMetamethod metamethod)
    {
        var metatable = table.Metatable;
        if (metatable is null)
        {
            return true;
        }

        var entries = Volatile.Read(ref _metamethodEntries);
        foreach (var entry in entries)
        {
            if (!entry.Metatable.TryGetTarget(out var cached) || !cached.IsAlive ||
                entry.Metamethod != metamethod || !ReferenceEquals(cached, metatable))
            {
                continue;
            }

            if (entry.ContentVersion == metatable.ContentVersion)
            {
                RecordFastPathHit();
                return true;
            }

            RecordInvalidation();
            break;
        }

        RecordFastPathMiss();
        if (!metatable.GetMetamethodField(metamethod).IsNil)
        {
            return false;
        }

        lock (_gate)
        {
            entries = Volatile.Read(ref _metamethodEntries);
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
                        RecordFastPathHit();
                        return true;
                    }

                    replacement = index;
                    break;
                }
            }

            var newEntry = new MetamethodEntry(
                new WeakReference<LuaTable>(metatable),
                metatable.ContentVersion,
                metamethod);
            if (replacement >= 0)
            {
                var updated = (MetamethodEntry[])entries.Clone();
                updated[replacement] = newEntry;
                Volatile.Write(ref _metamethodEntries, updated);
                return true;
            }

            if (entries.Length < MaximumEntries)
            {
                var updated = new MetamethodEntry[entries.Length + 1];
                entries.CopyTo(updated, 0);
                updated[^1] = newEntry;
                Volatile.Write(ref _metamethodEntries, updated);
                return true;
            }

            return false;
        }
    }

    private sealed record MetamethodEntry(
        WeakReference<LuaTable> Metatable,
        ulong ContentVersion,
        LuaMetamethod Metamethod);

    private sealed record FieldEntry(
        WeakReference<LuaTable> Table,
        WeakReference<LuaString> Key,
        ulong ShapeVersion,
        ulong StorageVersion,
        ulong MetatableVersion,
        LuaTableExistingEntry Handle);
}

internal interface ILuaCodegenTablePicCounterSink
{
    bool SupportsWeightedSampling => false;

    void RecordHit();

    void RecordMiss();

    void RecordInvalidation();

    void RecordHits(int count)
    {
        for (var index = 0; index < count; index++)
        {
            RecordHit();
        }
    }

    void RecordMisses(int count)
    {
        for (var index = 0; index < count; index++)
        {
            RecordMiss();
        }
    }
}

/// <summary>Weak module-identity cache used by guarded direct Lua-closure dispatch.</summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class LuaCodegenCallSiteCache
{
    private static readonly object MatchedModule = new();
    private readonly ConditionalWeakTable<LuaIrModule, object> _matchedModules = new();
    private readonly string? _expectedModuleContentId;
    private readonly Func<LuaIrModule, string>? _getModuleContentId;
    private WeakReference<LuaClosure>? _matchedClosure;
    private long _matchedFunctionGeneration;
    private WeakReference<object>? _directBackendEntry;
    private long _directFunctionGeneration;

    public LuaCodegenCallSiteCache()
    {
    }

    internal LuaCodegenCallSiteCache(
        string expectedModuleContentId,
        Func<LuaIrModule, string> getModuleContentId)
    {
        ArgumentException.ThrowIfNullOrEmpty(expectedModuleContentId);
        ArgumentNullException.ThrowIfNull(getModuleContentId);
        _expectedModuleContentId = expectedModuleContentId;
        _getModuleContentId = getModuleContentId;
    }

    internal bool TryMatchOrAdd(LuaClosure closure)
    {
        ArgumentNullException.ThrowIfNull(closure);
        var functionVersion = closure.FunctionVersion;
        var matchedClosure = Volatile.Read(ref _matchedClosure);
        if (matchedClosure is not null &&
            Interlocked.Read(ref _matchedFunctionGeneration) == functionVersion.Generation &&
            matchedClosure.TryGetTarget(out var cachedClosure) &&
            ReferenceEquals(cachedClosure, closure))
        {
            return true;
        }

        var module = closure.Module;
        if (_matchedModules.TryGetValue(module, out _))
        {
            CacheClosure(closure, functionVersion.Generation);
            return true;
        }

        if (_getModuleContentId is not null && !string.Equals(
                _getModuleContentId(module),
                _expectedModuleContentId,
                StringComparison.Ordinal))
        {
            return false;
        }

        _matchedModules.GetValue(module, static _ => MatchedModule);
        CacheClosure(closure, functionVersion.Generation);
        return true;
    }

    private void CacheClosure(LuaClosure closure, long functionGeneration)
    {
        var reference = Volatile.Read(ref _matchedClosure);
        if (reference is null)
        {
            reference = new WeakReference<LuaClosure>(closure);
            Volatile.Write(ref _matchedClosure, reference);
        }
        else
        {
            reference.SetTarget(closure);
        }

        Interlocked.Exchange(ref _matchedFunctionGeneration, functionGeneration);
    }

    internal bool TryGetDirectBackendEntry(
        long functionGeneration,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out object? entry)
    {
        var reference = Volatile.Read(ref _directBackendEntry);
        if (Interlocked.Read(ref _directFunctionGeneration) == functionGeneration &&
            reference is not null && reference.TryGetTarget(out entry))
        {
            return true;
        }

        entry = null;
        return false;
    }

    internal void SetDirectBackendEntry(object entry, long functionGeneration)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var reference = Volatile.Read(ref _directBackendEntry);
        if (reference is null)
        {
            reference = new WeakReference<object>(entry);
            Volatile.Write(ref _directBackendEntry, reference);
        }
        else
        {
            reference.SetTarget(entry);
        }

        Interlocked.Exchange(ref _directFunctionGeneration, functionGeneration);
    }
}
