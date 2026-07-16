using Lunil.Runtime.Values;
using System.Security.Cryptography;

namespace Lunil.Runtime.Memory;

/// <summary>
/// Owns the logical Lua object graph independently of CLR reachability.
/// Collection algorithms are incremental and never use CLR finalizers for Lua semantics.
/// </summary>
public sealed class LuaHeap
{
    private static long s_nextHeapIdentity;

    private readonly LuaHeapOptions _options;
    private readonly List<LuaGcObject> _objects = [];
    private readonly HashSet<LuaGcObject> _youngObjects =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LuaGcObject, int> _permanentRoots =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<long, LuaValue> _handles = [];
    private readonly Queue<LuaGcObject> _gray = [];
    private readonly HashSet<LuaGcObject> _grayAgain =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<LuaGcObject> _remembered =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<LuaGcObject> _rememberedDuringFullCycle =
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
        Identity = Interlocked.Increment(ref s_nextHeapIdentity);
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

    public bool IsRunning { get; private set; } = true;

    public int Pause { get; private set; } = 200;

    public int StepMultiplier { get; private set; } = 100;

    internal IReadOnlyList<LuaGcObject> Objects => _objects;

    internal int YoungObjectCount => _youngObjects.Count;

    internal int LastSweepCandidateCount { get; private set; }

    internal long Identity { get; }

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

        value.HeapIndex = _objects.Count;
        _objects.Add(value);
        _youngObjects.Add(value);
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

    public void SafePoint() => SafePoint(_options.StepObjectBudget);

    internal void SafePoint(int stepObjectBudget)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(stepObjectBudget);
        if (!IsRunning)
        {
            return;
        }

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
            Step(stepObjectBudget);
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

    public void Stop() => IsRunning = false;

    public void Restart() => IsRunning = true;

    public int SetPause(int value)
    {
        var previous = Pause;
        if (value != 0)
        {
            Pause = value;
        }

        return previous;
    }

    public int SetStepMultiplier(int value)
    {
        var previous = StepMultiplier;
        if (value != 0)
        {
            StepMultiplier = value;
        }

        return previous;
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

            if (!pending.Target.TryGetFinalizer(out var finalizer))
            {
                pending.Target.FinalizationState = LuaGcFinalizationState.Finalized;
                continue;
            }

            var completed = false;
            try
            {
                completed = callback(pending.Target, finalizer);
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
            if (target is not null)
            {
                PreserveDuringMutation(target);
            }

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
                if (!pending.Target.TryGetFinalizer(out finalizer))
                {
                    pending.Target.FinalizationState = LuaGcFinalizationState.Finalized;
                    continue;
                }

                target = pending.Target;
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
        if (_cycleKind == LuaGcCycleKind.Minor && IsOld(value))
        {
            return;
        }

        MarkObjectCore(value);
    }

    private void MarkRememberedObject(LuaGcObject value)
    {
        ValidateObject(value);
        MarkObjectCore(value);
    }

    private void MarkObjectCore(LuaGcObject value)
    {
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
        if (kind == LuaGcCycleKind.Full)
        {
            _rememberedDuringFullCycle.Clear();
        }

        _sweepCandidates = [];
        _sweepIndex = 0;
        LastSweepCandidateCount = 0;
        _finalizersSeparated = false;
        foreach (var value in GetCycleObjects())
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
                MarkRememberedObject(remembered);
            }
        }

        foreach (var pending in _pendingFinalizers)
        {
            MarkObject(pending.Target);
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
            var unlockedBeforeFinalizers = false;
            foreach (var pair in _weakTables)
            {
                if (pair.Value == LuaWeakMode.Keys && pair.Key.PropagateEphemerons(_visitor))
                {
                    unlockedBeforeFinalizers = true;
                }
            }

            if (unlockedBeforeFinalizers || _gray.Count > 0)
            {
                return 1;
            }

            // Lua clears weak values before resurrecting objects that are waiting for
            // finalization. References reachable only through a finalizable object must
            // therefore disappear from weak-value tables, while weak keys can still be
            // retained after that object's graph is marked for the finalizer.
            foreach (var pair in _weakTables)
            {
                pair.Key.ClearUnreachableWeakEntries(
                    pair.Value & LuaWeakMode.Values,
                    _visitor);
            }

            _finalizersSeparated = true;
            foreach (var value in GetCycleObjects())
            {
                if (!value.IsAlive || value.FinalizationState != LuaGcFinalizationState.None ||
                    value.Color != LuaGcColor.White ||
                    (_cycleKind == LuaGcCycleKind.Minor && IsOld(value)) ||
                    !value.TryGetFinalizer(out _))
                {
                    continue;
                }

                value.FinalizationState = LuaGcFinalizationState.Pending;
                MarkObject(value);
                _pendingFinalizers.Enqueue(new PendingFinalizer(value));
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
        _sweepCandidates = GetCycleObjects().ToArray();
        LastSweepCandidateCount = _sweepCandidates.Length;
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
                value.TryGetFinalizer(out _))
            {
                value.FinalizationState = LuaGcFinalizationState.Pending;
                PreserveDuringMutation(value);
                _pendingFinalizers.Enqueue(new PendingFinalizer(value));
                return 1;
            }

            value.IsAlive = false;
            RemoveObjectByReference(value);
            _youngObjects.Remove(value);
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
        foreach (var value in GetCycleObjects())
        {
            value.Color = LuaGcColor.White;
        }

        if (_cycleKind == LuaGcCycleKind.Minor)
        {
            foreach (var remembered in _remembered)
            {
                if (remembered.IsAlive)
                {
                    remembered.Color = LuaGcColor.White;
                }
            }
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
            // A major sweep promotes every snapshotted young survivor. Keep only owners
            // that acquired an old-to-young edge while this full cycle was active, since
            // allocations made after the sweep snapshot can remain young.
            _remembered.IntersectWith(_rememberedDuringFullCycle);
            _rememberedDuringFullCycle.Clear();
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
            if (_cycleKind == LuaGcCycleKind.Full && Phase != LuaGcPhase.Paused)
            {
                _rememberedDuringFullCycle.Add(owner);
            }

            if (_cycleKind == LuaGcCycleKind.Minor &&
                Phase is LuaGcPhase.Propagate or LuaGcPhase.Atomic)
            {
                // Old objects are outside the ordinary minor mark domain. Force the owner
                // through its normal traversal instead of marking the target directly so
                // weak tables still apply their key/value and ephemeron semantics.
                MarkRememberedObject(owner);
            }
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

    private void Promote(LuaGcObject value)
    {
        var wasOld = IsOld(value);
        value.Age = _cycleKind == LuaGcCycleKind.Full && !IsOld(value)
            ? LuaGcAge.Old0
            : value.Age switch
            {
                LuaGcAge.New => LuaGcAge.Survival,
                LuaGcAge.Survival => LuaGcAge.Old0,
                LuaGcAge.Old0 => LuaGcAge.Old1,
                LuaGcAge.Old1 => LuaGcAge.Old,
                _ => LuaGcAge.Old,
            };

        if (IsOld(value))
        {
            _youngObjects.Remove(value);
            if (!wasOld && _cycleKind == LuaGcCycleKind.Minor)
            {
                // The promoted object may already reference objects born after it. Those
                // edges were created while both sides were young, so no write barrier could
                // have remembered them at mutation time. Conservatively scan this newly old
                // owner in later minors until the next full cycle.
                _remembered.Add(value);
            }
            else if (!wasOld && _cycleKind == LuaGcCycleKind.Full)
            {
                // A young owner can acquire a post-snapshot young child while a major cycle
                // is active and then be promoted by that cycle. At mutation time the edge
                // was young-to-young, so preserve every newly old owner for the next minor.
                _remembered.Add(value);
                _rememberedDuringFullCycle.Add(value);
            }
        }
    }

    private void RemoveObjectByReference(LuaGcObject value)
    {
        var index = value.HeapIndex;
        if ((uint)index >= (uint)_objects.Count || !ReferenceEquals(_objects[index], value))
        {
            throw new InvalidOperationException("The collected object has an invalid heap slot.");
        }

        var lastIndex = _objects.Count - 1;
        if (index != lastIndex)
        {
            var replacement = _objects[lastIndex];
            _objects[index] = replacement;
            replacement.HeapIndex = index;
        }

        _objects.RemoveAt(lastIndex);
        value.HeapIndex = -1;
    }

    private IEnumerable<LuaGcObject> GetCycleObjects() =>
        _cycleKind == LuaGcCycleKind.Minor ? _youngObjects : _objects;

    private readonly record struct PendingFinalizer(LuaGcObject Target);

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
