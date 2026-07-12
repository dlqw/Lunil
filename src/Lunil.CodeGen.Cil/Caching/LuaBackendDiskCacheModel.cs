using System.Collections.Immutable;

namespace Lunil.CodeGen.Cil.Caching;

public sealed record LuaBackendDiskCacheOptions
{
    public required string RootDirectory { get; init; }

    public long MaximumBytes { get; init; } = 1024L * 1024 * 1024;

    public long MaximumEntryBytes { get; init; } = 256L * 1024 * 1024;

    public long MaximumQuarantineBytes { get; init; } = 64L * 1024 * 1024;

    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan LockRetryDelay { get; init; } = TimeSpan.FromMilliseconds(25);

    public TimeSpan OrphanTemporaryEntryAge { get; init; } = TimeSpan.FromHours(1);
}

public enum LuaBackendCacheReadStatus : byte
{
    Miss,
    Hit,
    CorruptMiss,
    Unavailable,
}

public sealed record LuaBackendCacheReadResult(
    LuaBackendCacheReadStatus Status,
    ImmutableArray<byte> Payload,
    string? DiagnosticCode = null)
{
    public bool IsHit => Status == LuaBackendCacheReadStatus.Hit;
}

public enum LuaBackendCacheWriteStatus : byte
{
    Created,
    AlreadyPresent,
    RejectedTooLarge,
    Unavailable,
}

public sealed record LuaBackendCacheWriteResult(
    LuaBackendCacheWriteStatus Status,
    int TrimmedEntries = 0,
    string? DiagnosticCode = null);

public sealed record LuaBackendCacheTrimResult(
    bool Succeeded,
    int RemovedEntries,
    long RemovedBytes,
    long RemainingBytes,
    string? DiagnosticCode = null);

public static class LuaBackendCacheDiagnosticCodes
{
    public const string Unavailable = "CACHE1001";
    public const string CorruptEntry = "CACHE1002";
    public const string IncompatibleEntry = "CACHE1003";
    public const string EntryTooLarge = "CACHE1004";
}
