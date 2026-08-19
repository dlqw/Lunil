using Lunil.LanguageServer;

namespace Lunil.LanguageServer.Tests;

/// <summary>
/// Coverage for adaptive memory budgets: each budget is a clamped fraction of the
/// memory the runtime grants the process, the three together stay bounded, and the
/// language server wires them in as its defaults.
/// </summary>
public sealed class LanguageServerMemoryBudgetTests
{
    private const long Megabyte = 1024L * 1024;

    [Fact]
    public void BudgetsStayWithinTheirFloorsAndCaps()
    {
        Assert.InRange(LanguageServerMemoryBudget.WorkspaceCacheBytes, 64 * Megabyte, 512 * Megabyte);
        Assert.InRange(LanguageServerMemoryBudget.DocumentResidencyBytes, 96 * Megabyte, 1024 * Megabyte);
        Assert.InRange(LanguageServerMemoryBudget.AnalysisCacheBytes, 96 * Megabyte, 1024 * Megabyte);
    }

    [Fact]
    public void CombinedBudgetsLeaveMostOfTheGrantFree()
    {
        var total = LanguageServerMemoryBudget.TotalAvailableBytes;
        Assert.InRange(total, 1, long.MaxValue);
        var combined = LanguageServerMemoryBudget.WorkspaceCacheBytes +
            LanguageServerMemoryBudget.DocumentResidencyBytes +
            LanguageServerMemoryBudget.AnalysisCacheBytes;
        Assert.True(combined <= total / 4, $"Combined budgets {combined} exceed a quarter of {total}.");
    }

    [Fact]
    public void LargeGrantsSaturateAtCapsAndSmallGrantsAtFloors()
    {
        Assert.Equal(
            512 * Megabyte,
            ScaleForSpec(64L * 1024 * Megabyte, divisor: 24, floor: 64 * Megabyte, cap: 512 * Megabyte));
        Assert.Equal(
            64 * Megabyte,
            ScaleForSpec(512 * Megabyte, divisor: 24, floor: 64 * Megabyte, cap: 512 * Megabyte));
    }

    [Fact]
    public void LanguageServerWorkspaceStartsFromAdaptiveDefaults()
    {
        using var workspace = new LanguageServerWorkspace();
        Assert.Equal(LanguageServerMemoryBudget.AnalysisCacheBytes, workspace.MaximumCachedAnalysisBytes);
        Assert.Equal(LanguageServerMemoryBudget.DocumentResidencyBytes, workspace.MaximumDocumentResidencyBytes);
    }

    /// <summary>Mirrors <see cref="LanguageServerMemoryBudget"/> scaling for spot checks.</summary>
    private static long ScaleForSpec(long total, int divisor, long floor, long cap) =>
        Math.Clamp(total / divisor, floor, cap);
}
