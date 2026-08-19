namespace Lunil.LanguageServer;

/// <summary>
/// Sizes the language server's residency budgets from the memory the runtime
/// actually grants this process — physical memory capped by the GC heap hard
/// limit — instead of fixed constants. Small machines shrink toward the floors so
/// the server stays a good citizen next to the editor; large machines get
/// headroom for huge workspaces. Each budget is a fixed fraction of the total,
/// clamped between a floor and a ceiling, and the three fractions together stay
/// under a quarter of the available memory so indexing cannot crowd out the heap.
/// </summary>
internal static class LanguageServerMemoryBudget
{
    /// <summary>Fallback total when the runtime reports no usable limit.</summary>
    private const long FallbackTotalBytes = 8L * 1024 * 1024 * 1024;

    public static long TotalAvailableBytes { get; } = DetermineTotalAvailableBytes();

    /// <summary>
    /// Module-analysis cache: incremental rebuilds only need the changed-module
    /// working set, so this budget stays the smallest of the three.
    /// </summary>
    public static long WorkspaceCacheBytes { get; } =
        Scale(TotalAvailableBytes, divisor: 24, floor: 64L * 1024 * 1024, cap: 512L * 1024 * 1024);

    /// <summary>Closed-document source residency, the largest steady-state share.</summary>
    public static long DocumentResidencyBytes { get; } =
        Scale(TotalAvailableBytes, divisor: 10, floor: 96L * 1024 * 1024, cap: 1024L * 1024 * 1024);

    /// <summary>Cached single-document analyses backing hover, completion, and navigation.</summary>
    public static long AnalysisCacheBytes { get; } =
        Scale(TotalAvailableBytes, divisor: 12, floor: 96L * 1024 * 1024, cap: 1024L * 1024 * 1024);

    private static long DetermineTotalAvailableBytes()
    {
        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return available > 0 ? available : FallbackTotalBytes;
    }

    private static long Scale(long total, int divisor, long floor, long cap) =>
        Math.Clamp(total / divisor, floor, cap);
}
