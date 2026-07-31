using System.Collections.Immutable;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.StandardLibrary;
using Lunil.Workspace;

namespace Lunil.Hosting;

/// <summary>Identifies the engine callback that advances queued Lua work.</summary>
public enum LuaGameLoopPhase : byte
{
    Update,
    FixedUpdate,
}

/// <summary>Controls how a yielded root coroutine is resumed.</summary>
public enum LuaGameLoopResumePolicy : byte
{
    NextTick,
    Manual,
}

/// <summary>Lifecycle state of work owned by a <see cref="LuaGameLoopHost"/>.</summary>
public enum LuaGameLoopOperationStatus : byte
{
    Pending,
    Running,
    Suspended,
    Completed,
    Cancelled,
    Faulted,
    Stale,
}

/// <summary>Stable game-loop scheduling error categories.</summary>
public enum LuaGameLoopErrorCode : byte
{
    WrongThread,
    ReentrantTick,
    QueueLimitExceeded,
    InvalidOperationState,
    StaleGeneration,
}

/// <summary>An invalid game-loop scheduling operation with a stable category.</summary>
public sealed class LuaGameLoopException : InvalidOperationException
{
    public LuaGameLoopException(LuaGameLoopErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    public LuaGameLoopException(
        LuaGameLoopErrorCode code,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public LuaGameLoopErrorCode Code { get; }
}

/// <summary>Marshals queue admission to an engine's main-thread dispatcher.</summary>
public interface ILuaGameLoopDispatcher
{
    bool CheckAccess();

    void Post(Action callback);
}

/// <summary>Resolves engine asset bytes without exposing engine types to portable Hosting.</summary>
public interface ILuaGameLoopAssetResolver
{
    ValueTask<LuaGameLoopReadResult> ResolveAsync(
        string assetId,
        CancellationToken cancellationToken = default);
}

/// <summary>Reads and atomically replaces game-owned persistent values.</summary>
public interface ILuaGameLoopPersistentStore
{
    ValueTask<LuaGameLoopReadResult> ReadAsync(
        string key,
        CancellationToken cancellationToken = default);

    ValueTask WriteAsync(
        string key,
        ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default);
}

/// <summary>Adds deletion and atomic namespace clearing without breaking V1 stores.</summary>
public interface ILuaGameLoopPersistentStoreV2 : ILuaGameLoopPersistentStore
{
    ValueTask<bool> DeleteAsync(
        string key,
        CancellationToken cancellationToken = default);

    ValueTask ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>Versioned serialization schema exposed by a persistent host implementation.</summary>
public sealed record LuaGameLoopPersistenceSchema(
    string SchemaId,
    int Version,
    string? MigrationFunction = null);

/// <summary>Allows tooling to keep runtime persistence schemas and analysis contracts consistent.</summary>
public interface ILuaGameLoopPersistenceSchemaProvider
{
    IReadOnlyCollection<LuaGameLoopPersistenceSchema> PersistenceSchemas { get; }
}

/// <summary>A binary asset or persistent value lookup that distinguishes missing from empty.</summary>
public readonly record struct LuaGameLoopReadResult(bool Found, ReadOnlyMemory<byte> Value)
{
    public static LuaGameLoopReadResult Missing { get; } = new(false, ReadOnlyMemory<byte>.Empty);

    public static LuaGameLoopReadResult FromValue(ReadOnlyMemory<byte> value) => new(true, value);
}

/// <summary>Per-operation scheduling configuration.</summary>
public sealed record LuaGameLoopStartOptions
{
    public static LuaGameLoopStartOptions Default { get; } = new();

    public LuaGameLoopPhase Phase { get; init; } = LuaGameLoopPhase.Update;

    public LuaGameLoopResumePolicy ResumePolicy { get; init; } =
        LuaGameLoopResumePolicy.NextTick;

    public CancellationToken CancellationToken { get; init; }
}

/// <summary>Portable game-loop composition, budgets, and engine service injection.</summary>
public sealed record LuaGameLoopHostOptions
{
    public static LuaGameLoopHostOptions Default { get; } = new();

    public LuaHostOptions HostOptions { get; init; } = LuaHostOptions.Default;

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public ILuaGameLoopDispatcher? Dispatcher { get; init; }

    public ILuaConsole? Console { get; init; }

    public ILuaModuleResolver? ModuleResolver { get; init; }

    public ILuaGameLoopAssetResolver? AssetResolver { get; init; }

    public ILuaGameLoopPersistentStore? PersistentStore { get; init; }

    public int MaximumCallbacksPerTick { get; init; } = 1_024;

    public long MaximumInstructionsPerTick { get; init; } = 1_000_000;

    public int MaximumQueuedWork { get; init; } = 65_536;
}

/// <summary>A callback or coroutine failure observed without aborting the remaining frame queue.</summary>
public sealed record LuaGameLoopFailure(
    LuaGameLoopOperation? Operation,
    LuaGameLoopErrorCode? Code,
    string Message,
    Exception? Exception);

/// <summary>Observable outcome of one Update or FixedUpdate boundary.</summary>
public sealed record LuaGameLoopTickResult(
    LuaGameLoopPhase Phase,
    long FrameNumber,
    int CallbackCount,
    long ExecutedInstructionCount,
    int CompletedOperationCount,
    int SuspendedOperationCount,
    int CancelledOperationCount,
    ImmutableArray<LuaGameLoopFailure> Failures,
    int RemainingQueuedWork)
{
    public bool Succeeded => Failures.IsEmpty;
}

/// <summary>Outcome of a bounded queue drain.</summary>
public sealed record LuaGameLoopDrainResult(
    bool Completed,
    int TickCount,
    int RemainingQueuedWork,
    int ActiveOperationCount);

/// <summary>A cancellable root coroutine or queued CLR continuation.</summary>
public sealed class LuaGameLoopOperation
{
    private int _status = (int)LuaGameLoopOperationStatus.Pending;
    private int _cancellationRequested;
    private int _executionQueued;
    private ImmutableArray<LuaValue> _values = [];

    internal LuaGameLoopOperation(
        long id,
        long generation,
        LuaGameLoopPhase phase,
        LuaGameLoopResumePolicy resumePolicy)
    {
        Id = id;
        Generation = generation;
        Phase = phase;
        ResumePolicy = resumePolicy;
    }

    public long Id { get; }

    public LuaGameLoopPhase Phase { get; }

    public LuaGameLoopResumePolicy ResumePolicy { get; }

    public LuaGameLoopOperationStatus Status =>
        (LuaGameLoopOperationStatus)Volatile.Read(ref _status);

    public bool IsCancellationRequested => Volatile.Read(ref _cancellationRequested) != 0;

    public bool IsTerminal => Status is LuaGameLoopOperationStatus.Completed or
        LuaGameLoopOperationStatus.Cancelled or LuaGameLoopOperationStatus.Faulted or
        LuaGameLoopOperationStatus.Stale;

    public ImmutableArray<LuaValue> Values => _values;

    public Exception? Exception { get; internal set; }

    internal long Generation { get; }

    internal LuaThread Thread { get; set; } = null!;

    internal Lunil.Runtime.Memory.LuaHandle ThreadHandle { get; set; } = null!;

    internal CancellationTokenRegistration CancellationRegistration { get; set; }

    internal LuaClrCancellation? LinkedCancellation { get; set; }

    internal bool HasThread => Thread is not null;

    internal bool TryQueueExecution() => Interlocked.CompareExchange(ref _executionQueued, 1, 0) == 0;

    internal void ClearQueuedExecution() => Volatile.Write(ref _executionQueued, 0);

    internal void RequestCancellation()
    {
        Interlocked.Exchange(ref _cancellationRequested, 1);
        if (LinkedCancellation is { IsCancellationRequested: false } cancellation)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    internal void SetStatus(LuaGameLoopOperationStatus status) =>
        Volatile.Write(ref _status, (int)status);

    internal void SetValues(ImmutableArray<LuaValue> values) => _values = values;
}
