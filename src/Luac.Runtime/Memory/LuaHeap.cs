using Luac.Runtime.Values;
using System.Security.Cryptography;

namespace Luac.Runtime.Memory;

/// <summary>
/// Owns the logical Lua object graph independently of CLR reachability.
/// Collection algorithms are incremental and never use CLR finalizers for Lua semantics.
/// </summary>
public sealed class LuaHeap
{
    private readonly LuaHeapOptions _options;
    private readonly List<LuaGcObject> _objects = [];
    private readonly Dictionary<LuaGcObject, int> _permanentRoots =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<long, LuaValue> _handles = [];
    private readonly Queue<LuaGcObject> _gray = [];
    private readonly HashSet<LuaGcObject> _grayAgain =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<LuaGcObject> _remembered =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LuaTable, LuaWeakMode> _weakTables =
        new(ReferenceEqualityComparer.Instance);
    private readonly Queue<PendingFinalizer> _pendingFinalizers = [];
    private readonly LuaGcVisitor _visitor;
    private LuaGcObject[] _sweepCandidates = [];
    private int _sweepIndex;
    private long _nextObjectId;
    private long _nextHandleId;
    private long _allocationDebt;
    private bool _allocatedSinceSafePoint;
    private LuaGcCycleKind _cycleKind;
    private int _completedMinorCycles;
    private bool _finalizersSeparated;

    public LuaHeap(LuaHeapOptions? options = null)
    {
        _options = options ?? LuaHeapOptions.Default;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaximumLogicalBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.StepSizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.StepObjectBudget);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MinorCyclesBeforeMajor);
        Mode = _options.InitialMode;
        _visitor = new LuaGcVisitor(this);
        HashSeed = _options.HashSeed ?? RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue);
    }

    public long LogicalBytes { get; private set; }

    public long MaximumLogicalBytes => _options.MaximumLogicalBytes;

    public int HashSeed { get; }

    public int ObjectCount => _objects.Count;

    public int HandleCount => _handles.Count;

    public int RememberedObjectCount => _remembered.Count;

    public int PendingFinalizerCount => _pendingFinalizers.Count;

    public long CompletedCycleCount { get; private set; }

    public long CollectedObjectCount { get; private set; }

    public LuaGcMode Mode { get; set; }

    public LuaGcPhase Phase { get; internal set; } = LuaGcPhase.Paused;

    public bool StressEveryAllocation => _options.StressEveryAllocation;

    internal IReadOnlyList<LuaGcObject> Objects => _objects;

    internal IEnumerable<LuaGcObject> PermanentRoots => _permanentRoots.Keys;

    internal IEnumerable<LuaValue> HandleRoots => _handles.Values;

    internal long Register(LuaGcObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var nextBytes = checked(LogicalBytes + value.LogicalSize);
        if (nextBytes > _options.MaximumLogicalBytes)
        {
            throw new LuaRuntimeException(
                $"Lua heap quota exceeded ({nextBytes} > {_options.MaximumLogicalBytes} logical bytes).");
        }

        LogicalBytes = nextBytes;
        _allocationDebt = checked(_allocationDebt + value.LogicalSize);
        _allocatedSinceSafePoint = true;
        if (Phase != LuaGcPhase.Paused)
        {
            value.Color = LuaGcColor.Black;
        }

        _objects.Add(value);
        return checked(++_nextObjectId);
    }

    public LuaHandle CreateHandle(LuaValue value)
    {
        ValidateValue(value);
        var id = checked(++_nextHandleId);
        _handles.Add(id, value);
        if (Phase != LuaGcPhase.Paused)
        {
            PreserveDuringMutation(value);
        }

        return new LuaHandle(this, id);
    }

    public void ValidateValue(LuaValue value)
    {
        var gcObject = value.TryGetGcObject();
        if (gcObject is null)
        {
            return;
        }

        if (!ReferenceEquals(gcObject.Owner, this))
        {
            throw new LuaRuntimeException("Cannot move a collectable value between different LuaState instances.");
        }

        if (!gcObject.IsAlive)
        {
            throw new LuaRuntimeException("Cannot use a Lua object after it was collected by the logical heap.");
        }
    }

    public void SafePoint()
    {
        if (!_allocatedSinceSafePoint && Phase == LuaGcPhase.Paused &&
            _allocationDebt < _options.StepSizeBytes)
        {
            return;
        }

        if (_options.StressEveryAllocation && _allocatedSinceSafePoint)
        {
            CollectFull();
        }
        else if (Phase != LuaGcPhase.Paused || _allocationDebt >= _options.StepSizeBytes)
        {
            Step(_options.StepObjectBudget);
        }

        _allocatedSinceSafePoint = false;
    }

    public void Step(int objectBudget = -1)
    {
        if (objectBudget < 0)
        {
            objectBudget = _options.StepObjectBudget;
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(objectBudget);
        if (Phase == LuaGcPhase.Paused)
        {
            BeginCycle(Mode == LuaGcMode.Generational &&
                _completedMinorCycles < _options.MinorCyclesBeforeMajor
                    ? LuaGcCycleKind.Minor
                    : LuaGcCycleKind.Full);
        }

        var remaining = objectBudget;
        while (remaining > 0 && Phase != LuaGcPhase.Paused)
        {
            remaining -= Phase switch
            {
                LuaGcPhase.Propagate => PropagateOne(),
                LuaGcPhase.Atomic => RunAtomic(),
                LuaGcPhase.Sweep => SweepOne(),
                LuaGcPhase.Finalize => FinishCycle(),
                _ => throw new InvalidOperationException($"Unexpected GC phase {Phase}."),
            };
        }

        _allocationDebt = Math.Max(0, _allocationDebt - objectBudget * 64L);
    }

    public void CollectFull()
    {
        FinishActiveCycle();
        BeginCycle(LuaGcCycleKind.Full);
        FinishActiveCycle();
        _allocationDebt = 0;
        _allocatedSinceSafePoint = false;
    }

    public void CollectMinor()
    {
        FinishActiveCycle();
        BeginCycle(LuaGcCycleKind.Minor);
        FinishActiveCycle();
        _allocationDebt = 0;
        _allocatedSinceSafePoint = false;
    }

    public int RunPendingFinalizers(
        Action<LuaGcObject, LuaValue> callback,
        int maximumCount = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return RunPendingFinalizers(
            (target, finalizer) =>
            {
                callback(target, finalizer);
                return true;
            },
            maximumCount);
    }

    public int RunPendingFinalizers(
        Func<LuaGcObject, LuaValue, bool> callback,
        int maximumCount = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCount);
        var count = 0;
        while (count < maximumCount && _pendingFinalizers.TryDequeue(out var pending))
        {
            if (!pending.Target.IsAlive ||
                pending.Target.FinalizationState != LuaGcFinalizationState.Pending)
            {
                continue;
            }

            var completed = false;
            try
            {
                completed = callback(pending.Target, pending.Finalizer);
            }
            finally
            {
                if (completed)
                {
                    pending.Target.FinalizationState = LuaGcFinalizationState.Finalized;
                }
            }

            if (!completed)
            {
                _pendingFinalizers.Enqueue(pending);
                break;
            }

            count++;
        }

        return count;
    }

    public void WriteBarrier(LuaGcObject owner, LuaValue value)
    {
        ValidateObject(owner);
        ValidateValue(value);
        var target = value.TryGetGcObject();
        if (target is null)
        {
            return;
        }

        RememberOldToYoung(owner, target);
        if (Phase == LuaGcPhase.Sweep)
        {
            PreserveDuringMutation(target);
            return;
        }

        if (Phase is LuaGcPhase.Propagate or LuaGcPhase.Atomic &&
            owner.Color == LuaGcColor.Black && target.Color == LuaGcColor.White)
        {
            MarkObject(target);
        }
    }

    public void WriteBarrier(LuaGcObject owner, LuaGcObject target)
    {
        ValidateObject(owner);
        ValidateObject(target);
        RememberOldToYoung(owner, target);
        if (Phase == LuaGcPhase.Sweep)
        {
            PreserveDuringMutation(target);
        }
        else if (Phase is LuaGcPhase.Propagate or LuaGcPhase.Atomic &&
            owner.Color == LuaGcColor.Black && target.Color == LuaGcColor.White)
        {
            MarkObject(target);
        }
    }

    public void WriteBarrierBack(LuaGcObject owner, LuaValue value)
    {
        ValidateObject(owner);
        ValidateValue(value);
        var target = value.TryGetGcObject();
        if (target is not null)
        {
            RememberOldToYoung(owner, target);
        }

        if (Phase == LuaGcPhase.Sweep)
        {
            PreserveDuringMutation(owner);
            return;
        }

        if (Phase is LuaGcPhase.Propagate or LuaGcPhase.Atomic &&
            owner.Color == LuaGcColor.Black && _grayAgain.Add(owner))
        {
            owner.Color = LuaGcColor.Gray;
            _gray.Enqueue(owner);
        }
    }

    internal void AddPermanentRoot(LuaGcObject value)
    {
        ValidateObject(value);
        _permanentRoots[value] = _permanentRoots.GetValueOrDefault(value) + 1;
        if (Phase != LuaGcPhase.Paused)
        {
            PreserveDuringMutation(value);
        }
    }

    internal void RemovePermanentRoot(LuaGcObject value)
    {
        if (!_permanentRoots.TryGetValue(value, out var count))
        {
            return;
        }

        if (count == 1)
        {
            _permanentRoots.Remove(value);
        }
        else
        {
            _permanentRoots[value] = count - 1;
        }
    }

    internal void AdjustLogicalSize(LuaGcObject value, long delta)
    {
        ValidateObject(value);
        var next = checked(LogicalBytes + delta);
        if (next < 0 || next > _options.MaximumLogicalBytes)
        {
            throw new LuaRuntimeException("Lua heap quota exceeded while resizing an object.");
        }

        value.LogicalSize = checked(value.LogicalSize + delta);
        LogicalBytes = next;
        if (delta > 0)
        {
            _allocationDebt = checked(_allocationDebt + delta);
            _allocatedSinceSafePoint = true;
        }
    }

    internal LuaValue GetHandleValue(long id)
    {
        ObjectDisposedException.ThrowIf(
            !_handles.TryGetValue(id, out var value),
            typeof(LuaHandle));
        return value;
    }

    internal void SetHandleValue(long id, LuaValue value)
    {
        ValidateValue(value);
        ObjectDisposedException.ThrowIf(!_handles.ContainsKey(id), typeof(LuaHandle));

        _handles[id] = value;
        if (Phase != LuaGcPhase.Paused)
        {
            PreserveDuringMutation(value);
        }
    }

    internal void RemoveHandle(long id) => _handles.Remove(id);

    internal bool TryTakePendingFinalizer(
        out LuaGcObject target,
        out LuaValue finalizer)
    {
        while (_pendingFinalizers.TryDequeue(out var pending))
        {
            if (pending.Target.IsAlive &&
                pending.Target.FinalizationState == LuaGcFinalizationState.Pending)
            {
                target = pending.Target;
                finalizer = pending.Finalizer;
                return true;
            }
        }

        target = null!;
        finalizer = LuaValue.Nil;
        return false;
    }

    internal static void CompleteFinalizer(LuaGcObject target) =>
        target.FinalizationState = LuaGcFinalizationState.Finalized;

    internal void MarkValue(LuaValue value)
    {
        var gcObject = value.TryGetGcObject();
        if (gcObject is not null)
        {
            MarkObject(gcObject);
        }
    }

    internal void MarkObject(LuaGcObject value)
    {
        ValidateObject(value);
        if (value.Color == LuaGcColor.White)
        {
            value.Color = LuaGcColor.Gray;
            _gray.Enqueue(value);
        }
    }

    internal void RegisterWeakTable(LuaTable table, LuaWeakMode mode)
    {
        ValidateObject(table);
        if (_weakTables.TryGetValue(table, out var existing))
        {
            _weakTables[table] = existing | mode;
        }
        else
        {
            _weakTables.Add(table, mode);
        }
    }

    internal bool IsUnreachableInCurrentCycle(LuaValue value)
    {
        var gcObject = value.TryGetGcObject();
        return gcObject is not null && gcObject is not LuaString &&
            gcObject.Color == LuaGcColor.White &&
            (_cycleKind == LuaGcCycleKind.Full || !IsOld(gcObject));
    }

    private void BeginCycle(LuaGcCycleKind kind)
    {
        _cycleKind = kind;
        _gray.Clear();
        _grayAgain.Clear();
        _weakTables.Clear();
        _sweepCandidates = [];
        _sweepIndex = 0;
        _finalizersSeparated = false;
        foreach (var value in _objects)
        {
            value.Color = LuaGcColor.White;
        }

        foreach (var root in _permanentRoots.Keys)
        {
            MarkObject(root);
        }

        foreach (var root in _handles.Values)
        {
            MarkValue(root);
        }

        if (kind == LuaGcCycleKind.Minor)
        {
            foreach (var remembered in _remembered)
            {
                MarkObject(remembered);
            }
        }

        foreach (var pending in _pendingFinalizers)
        {
            MarkObject(pending.Target);
            MarkValue(pending.Finalizer);
        }

        Phase = LuaGcPhase.Propagate;
    }

    private int PropagateOne()
    {
        while (_gray.Count > 0)
        {
            var value = _gray.Dequeue();
            if (!value.IsAlive || value.Color != LuaGcColor.Gray)
            {
                continue;
            }

            _grayAgain.Remove(value);
            value.Traverse(_visitor);
            value.Color = LuaGcColor.Black;
            return 1;
        }

        Phase = LuaGcPhase.Atomic;
        return 1;
    }

    private int RunAtomic()
    {
        if (_gray.Count > 0)
        {
            return PropagateOne();
        }

        foreach (var root in _permanentRoots.Keys)
        {
            MarkObject(root);
        }

        foreach (var root in _handles.Values)
        {
            MarkValue(root);
        }

        if (_gray.Count > 0)
        {
            return 1;
        }

        if (!_finalizersSeparated)
        {
            _finalizersSeparated = true;
            foreach (var value in _objects)
            {
                if (!value.IsAlive || value.FinalizationState != LuaGcFinalizationState.None ||
                    value.Color != LuaGcColor.White ||
                    (_cycleKind == LuaGcCycleKind.Minor && IsOld(value)) ||
                    !value.TryGetFinalizer(out var finalizer))
                {
                    continue;
                }

                value.FinalizationState = LuaGcFinalizationState.Pending;
                MarkObject(value);
                MarkValue(finalizer);
                _pendingFinalizers.Enqueue(new PendingFinalizer(value, finalizer));
            }

            if (_gray.Count > 0)
            {
                return 1;
            }
        }

        var unlockedEphemeron = false;
        foreach (var pair in _weakTables)
        {
            if (pair.Value == LuaWeakMode.Keys && pair.Key.PropagateEphemerons(_visitor))
            {
                unlockedEphemeron = true;
            }
        }

        if (unlockedEphemeron || _gray.Count > 0)
        {
            return 1;
        }

        foreach (var pair in _weakTables)
        {
            pair.Key.ClearUnreachableWeakEntries(pair.Value, _visitor);
        }

        _grayAgain.Clear();
        _sweepCandidates = _objects.ToArray();
        _sweepIndex = 0;
        Phase = LuaGcPhase.Sweep;
        return 1;
    }

    private int SweepOne()
    {
        if (_sweepIndex >= _sweepCandidates.Length)
        {
            Phase = LuaGcPhase.Finalize;
            return 1;
        }

        var value = _sweepCandidates[_sweepIndex++];
        var collect = value.IsAlive && value.Color == LuaGcColor.White &&
            (_cycleKind == LuaGcCycleKind.Full || !IsOld(value));
        if (collect)
        {
            if (value.FinalizationState == LuaGcFinalizationState.None &&
                value.TryGetFinalizer(out var finalizer))
            {
                value.FinalizationState = LuaGcFinalizationState.Pending;
                PreserveDuringMutation(value);
                PreserveDuringMutation(finalizer);
                _pendingFinalizers.Enqueue(new PendingFinalizer(value, finalizer));
                return 1;
            }

            value.IsAlive = false;
            _objects.Remove(value);
            _permanentRoots.Remove(value);
            _remembered.Remove(value);
            LogicalBytes = checked(LogicalBytes - value.LogicalSize);
            CollectedObjectCount++;
            value.OnCollected();
        }
        else if (value.IsAlive)
        {
            value.Color = LuaGcColor.White;
            Promote(value);
        }

        return 1;
    }

    private int FinishCycle()
    {
        foreach (var value in _objects)
        {
            value.Color = LuaGcColor.White;
        }

        _remembered.RemoveWhere(static value => !value.IsAlive);
        _gray.Clear();
        _grayAgain.Clear();
        _weakTables.Clear();
        _sweepCandidates = [];
        _sweepIndex = 0;
        CompletedCycleCount++;
        if (_cycleKind == LuaGcCycleKind.Minor)
        {
            _completedMinorCycles++;
        }
        else
        {
            _completedMinorCycles = 0;
            _remembered.Clear();
        }

        Phase = LuaGcPhase.Paused;
        return 1;
    }

    private void FinishActiveCycle()
    {
        while (Phase != LuaGcPhase.Paused)
        {
            Step(int.MaxValue / 4);
        }
    }

    private void RememberOldToYoung(LuaGcObject owner, LuaGcObject target)
    {
        if (IsOld(owner) && !IsOld(target))
        {
            _remembered.Add(owner);
        }
    }

    private void PreserveDuringMutation(LuaValue value)
    {
        var target = value.TryGetGcObject();
        if (target is not null)
        {
            PreserveDuringMutation(target);
        }
    }

    private void PreserveDuringMutation(LuaGcObject target)
    {
        MarkObject(target);
        if (Phase != LuaGcPhase.Sweep)
        {
            return;
        }

        while (_gray.Count > 0)
        {
            var value = _gray.Dequeue();
            if (!value.IsAlive || value.Color != LuaGcColor.Gray)
            {
                continue;
            }

            value.Traverse(_visitor);
            value.Color = LuaGcColor.Black;
        }
    }

    private static bool IsOld(LuaGcObject value) => value.Age >= LuaGcAge.Old0;

    private static void Promote(LuaGcObject value)
    {
        value.Age = value.Age switch
        {
            LuaGcAge.New => LuaGcAge.Survival,
            LuaGcAge.Survival => LuaGcAge.Old0,
            LuaGcAge.Old0 => LuaGcAge.Old1,
            LuaGcAge.Old1 => LuaGcAge.Old,
            _ => LuaGcAge.Old,
        };
    }

    private readonly record struct PendingFinalizer(
        LuaGcObject Target,
        LuaValue Finalizer);

    private void ValidateObject(LuaGcObject value)
    {
        if (!ReferenceEquals(value.Owner, this))
        {
            throw new InvalidOperationException("The GC object belongs to a different heap.");
        }

        if (!value.IsAlive)
        {
            throw new InvalidOperationException("The GC object is no longer alive.");
        }
    }
}
