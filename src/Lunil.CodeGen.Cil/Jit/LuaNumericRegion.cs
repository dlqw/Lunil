using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Lunil.CodeGen.Cil.Analysis;
using Lunil.IR.Canonical;

namespace Lunil.CodeGen.Cil.Jit;

internal enum LuaNumericRegionValueKind : byte
{
    Unknown,
    Integer,
    Float,
    Boolean,
    Tagged,
    Cleared,
    Conflict,
}

internal readonly record struct LuaNumericRegionTypeHint(
    int ProgramCounter,
    int Register,
    LuaNumericRegionValueKind Kind);

internal sealed record LuaNaturalLoopRegion(
    int FunctionId,
    int HeaderProgramCounter,
    int BackedgeProgramCounter,
    ImmutableArray<int> ProgramCounters,
    LuaRegisterLivenessResult Liveness);

internal sealed record LuaNumericRegionRegister(
    int Register,
    LuaNumericRegionValueKind Kind);

internal enum LuaNumericRegionTableOperation : byte
{
    Get,
    Set,
}

internal readonly record struct LuaNumericRegionTableSite(
    int ProgramCounter,
    int TableDefinitionProgramCounter,
    LuaNumericRegionTableOperation Operation);

/// <summary>
/// Static accounting for one canonical instruction in a numeric region. Hot execution charges
/// the containing basic block once; a failing instruction rolls back itself and the unexecuted
/// suffix. The cold slow tail restarts at the exact canonical PC and charges one instruction at a
/// time.
/// </summary>
internal readonly record struct LuaNumericRegionBudgetSite(
    int ProgramCounter,
    int BasicBlockEntryProgramCounter,
    int BasicBlockEndProgramCounter,
    int BasicBlockInstructionCost,
    int RemainingBasicBlockInstructionCost,
    int MaximumInstructionCostToSafepointOrExit,
    int DeoptimizationProgramCounter,
    int FailureInstructionRollbackCount,
    int ColdSlowTailProgramCounter);

internal sealed record LuaNumericRegionPlan(
    LuaNaturalLoopRegion Region,
    ImmutableArray<LuaNumericRegionRegister> Registers,
    ImmutableArray<int> BackedgeProgramCounters,
    int DirectNumericInstructionCount,
    ImmutableArray<LuaNumericRegionTableSite> TableSites,
    ImmutableArray<LuaNumericRegionBudgetSite> BudgetSites,
    int MaximumBackedgeSegmentInstructionCost,
    int HotInstructionBudgetCheckCount,
    ImmutableArray<ImmutableArray<LuaNumericRegionValueKind>> KindsBefore,
    ImmutableArray<ImmutableArray<LuaNumericRegionValueKind>> KindsAfter)
{
    public bool Contains(int programCounter) =>
        Region.ProgramCounters.BinarySearch(programCounter) >= 0;

    public LuaNumericRegionValueKind GetKindBefore(int programCounter, int register) =>
        KindAt(KindsBefore, programCounter, register);

    public LuaNumericRegionValueKind GetKindAfter(int programCounter, int register) =>
        KindAt(KindsAfter, programCounter, register);

    public bool TryGetTableSite(
        int programCounter,
        out LuaNumericRegionTableSite tableSite)
    {
        foreach (var candidate in TableSites)
        {
            if (candidate.ProgramCounter == programCounter)
            {
                tableSite = candidate;
                return true;
            }
        }

        tableSite = default;
        return false;
    }

    public LuaNumericRegionBudgetSite GetBudgetSite(int programCounter)
    {
        if ((uint)programCounter >= (uint)BudgetSites.Length ||
            BudgetSites[programCounter].ProgramCounter != programCounter)
        {
            throw new ArgumentOutOfRangeException(
                nameof(programCounter),
                $"PC {programCounter} is not part of the numeric region.");
        }

        return BudgetSites[programCounter];
    }

    private static LuaNumericRegionValueKind KindAt(
        ImmutableArray<ImmutableArray<LuaNumericRegionValueKind>> states,
        int programCounter,
        int register)
    {
        if ((uint)programCounter >= (uint)states.Length)
        {
            return LuaNumericRegionValueKind.Unknown;
        }

        var state = states[programCounter];
        return (uint)register < (uint)state.Length
            ? state[register]
            : LuaNumericRegionValueKind.Unknown;
    }
}

/// <summary>
/// Shared reducible-CFG discovery used by whole-function Tier 2 and loop OSR. A backedge is
/// admitted only when its target is a basic-block leader that dominates the source block.
/// </summary>
internal static class LuaNumericRegionAnalyzer
{
    private static readonly ConditionalWeakTable<LuaIrModule, ModuleCache> Caches = new();

    public static ImmutableArray<LuaNaturalLoopRegion> AnalyzeNaturalLoops(
        LuaIrModule module,
        int functionId,
        out bool livenessCacheHit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(module);
        if ((uint)functionId >= (uint)module.Functions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(functionId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Caches.GetValue(module, static _ => new ModuleCache()).GetOrAdd(
            module,
            functionId,
            out livenessCacheHit,
            cancellationToken);
    }

    private static ImmutableArray<LuaNaturalLoopRegion> AnalyzeNaturalLoopsCore(
        LuaIrModule module,
        int functionId,
        out bool livenessCacheHit,
        CancellationToken cancellationToken)
    {
        var function = module.Functions[functionId];
        var blocks = function.BasicBlocks.IsDefaultOrEmpty
            ? LuaIrControlFlow.Build(function.Instructions)
            : function.BasicBlocks;
        if (blocks.IsEmpty)
        {
            livenessCacheHit = false;
            return [];
        }

        var blockByStart = blocks.ToDictionary(static block => block.Start);
        var predecessors = blocks.ToDictionary(
            static block => block.Start,
            static _ => new HashSet<int>());
        foreach (var block in blocks)
        {
            foreach (var successor in block.Successors)
            {
                if (predecessors.TryGetValue(successor, out var targets))
                {
                    targets.Add(block.Start);
                }
            }
        }

        var dominators = ComputeDominators(blocks, predecessors, cancellationToken);
        var liveness = LuaRegisterLiveness.AnalyzeCached(
            module,
            function,
            out livenessCacheHit,
            cancellationToken);
        var discovered = new List<(int Header, int Backedge, HashSet<int> Blocks)>();
        foreach (var block in blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var backedgePc = block.End - 1;
            var instruction = function.Instructions[backedgePc];
            if (!IsBackedgeInstruction(instruction, backedgePc) ||
                !blockByStart.ContainsKey(instruction.B) ||
                !dominators[block.Start].Contains(instruction.B))
            {
                continue;
            }

            var loopBlocks = BuildNaturalLoop(
                instruction.B,
                block.Start,
                predecessors,
                dominators,
                cancellationToken);
            discovered.Add((instruction.B, backedgePc, loopBlocks));
        }

        var maximalBlocks = discovered
            .GroupBy(static loop => loop.Header)
            .ToDictionary(
                static group => group.Key,
                static group => group.SelectMany(static loop => loop.Blocks).ToHashSet());
        var regions = ImmutableArray.CreateBuilder<LuaNaturalLoopRegion>(discovered.Count);
        foreach (var loop in discovered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var programCounters = maximalBlocks[loop.Header]
                .SelectMany(start => Enumerable.Range(start, blockByStart[start].Length))
                .Order()
                .ToImmutableArray();
            regions.Add(new LuaNaturalLoopRegion(
                functionId,
                loop.Header,
                loop.Backedge,
                programCounters,
                liveness));
        }

        return regions
            .OrderBy(static region => region.HeaderProgramCounter)
            .ThenBy(static region => region.BackedgeProgramCounter)
            .ToImmutableArray();
    }

    private sealed class ModuleCache
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<int, ImmutableArray<LuaNaturalLoopRegion>> _regions = [];

        public ImmutableArray<LuaNaturalLoopRegion> GetOrAdd(
            LuaIrModule module,
            int functionId,
            out bool cacheHit,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_regions.TryGetValue(functionId, out var cached))
                {
                    cacheHit = true;
                    return cached;
                }

                var regions = AnalyzeNaturalLoopsCore(
                    module,
                    functionId,
                    out cacheHit,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                _regions.Add(functionId, regions);
                return regions;
            }
        }
    }

    public static bool IsBackedgeInstruction(
        LuaIrInstruction instruction,
        int programCounter) =>
        instruction.B <= programCounter && instruction.Opcode switch
        {
            LuaIrOpcode.Jump => true,
            LuaIrOpcode.JumpIfFalse or LuaIrOpcode.JumpIfTrue or
                LuaIrOpcode.NumericForLoop => true,
            _ => false,
        };

    private static Dictionary<int, HashSet<int>> ComputeDominators(
        ImmutableArray<LuaIrBasicBlock> blocks,
        Dictionary<int, HashSet<int>> predecessors,
        CancellationToken cancellationToken)
    {
        var starts = blocks.Select(static block => block.Start).ToHashSet();
        var entry = blocks[0].Start;
        var result = blocks.ToDictionary(
            static block => block.Start,
            block => block.Start == entry ? new HashSet<int> { entry } : new HashSet<int>(starts));
        var changed = true;
        while (changed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            changed = false;
            foreach (var block in blocks.Skip(1))
            {
                var incoming = predecessors[block.Start];
                HashSet<int> next;
                if (incoming.Count == 0)
                {
                    next = [block.Start];
                }
                else
                {
                    using var enumerator = incoming.GetEnumerator();
                    _ = enumerator.MoveNext();
                    next = new HashSet<int>(result[enumerator.Current]);
                    while (enumerator.MoveNext())
                    {
                        next.IntersectWith(result[enumerator.Current]);
                    }

                    next.Add(block.Start);
                }

                if (!result[block.Start].SetEquals(next))
                {
                    result[block.Start] = next;
                    changed = true;
                }
            }
        }

        return result;
    }

    private static HashSet<int> BuildNaturalLoop(
        int header,
        int source,
        Dictionary<int, HashSet<int>> predecessors,
        Dictionary<int, HashSet<int>> dominators,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<int> { header, source };
        var pending = new Stack<int>();
        if (source != header)
        {
            pending.Push(source);
        }

        while (pending.TryPop(out var block))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var predecessor in predecessors[block])
            {
                if (dominators[predecessor].Contains(header) && result.Add(predecessor))
                {
                    pending.Push(predecessor);
                }
            }
        }

        return result;
    }
}
