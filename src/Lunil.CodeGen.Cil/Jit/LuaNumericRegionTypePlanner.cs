using System.Collections.Immutable;
using Lunil.IR.Canonical;

namespace Lunil.CodeGen.Cil.Jit;

/// <summary>
/// Builds an exact, versioned type proof for a natural loop. Canonical registers are reusable
/// slots, so different definitions of the same register may legitimately carry different exact
/// types. The proof therefore follows reaching definitions instead of assigning one type to the
/// physical register for the entire region. A join is accepted only when every reaching version
/// has the same exact representation.
/// </summary>
internal static class LuaNumericRegionPlanner
{
    public static LuaNumericRegionPlan? TryCreate(
        LuaIrFunction function,
        LuaNaturalLoopRegion region,
        IEnumerable<LuaNumericRegionTypeHint> hints,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(hints);
        cancellationToken.ThrowIfCancellationRequested();
        if (region.ProgramCounters.IsEmpty || function.RegisterCount <= 0)
        {
            return null;
        }

        var inside = region.ProgramCounters.ToHashSet();
        if (!inside.Contains(region.HeaderProgramCounter) ||
            !TryBuildReachingDefinitions(
                function,
                region,
                inside,
                cancellationToken,
                out var before,
                out var after))
        {
            return null;
        }

        var solver = new ExactTypeSolver();
        foreach (var state in before.Values.Concat(after.Values))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var definitions in state)
            {
                foreach (var definition in definitions)
                {
                    _ = solver.GetOrAdd(definition);
                }
            }
        }

        foreach (var hint in hints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!inside.Contains(hint.ProgramCounter) ||
                (uint)hint.Register >= (uint)function.RegisterCount ||
                hint.Kind is LuaNumericRegionValueKind.Unknown or
                    LuaNumericRegionValueKind.Conflict ||
                !TryAssignUse(
                    solver,
                    before[hint.ProgramCounter][hint.Register],
                    hint.Kind))
            {
                return null;
            }
        }

        foreach (var pc in region.ProgramCounters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ApplyStructuralConstraints(function, pc, before[pc], solver))
            {
                return null;
            }
        }

        var changed = true;
        while (changed)
        {
            var version = solver.Version;
            foreach (var pc in region.ProgramCounters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var instruction = function.Instructions[pc];
                if (instruction.Opcode != LuaIrOpcode.Binary ||
                    !ApplyBinaryResultConstraint(instruction, pc, before[pc], solver))
                {
                    if (instruction.Opcode == LuaIrOpcode.Binary)
                    {
                        return null;
                    }
                }
            }

            changed = solver.Version != version;
        }

        var kindsBefore = CreateKindMaps(function, before, solver, cancellationToken);
        var kindsAfter = CreateKindMaps(function, after, solver, cancellationToken);
        var directNumericInstructionCount = 0;
        foreach (var pc in region.ProgramCounters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!VerifyInstruction(
                    function,
                    pc,
                    kindsBefore[pc],
                    kindsAfter[pc],
                    ref directNumericInstructionCount))
            {
                return null;
            }
        }

        var registerSet = new HashSet<LuaNumericRegionRegister>();
        foreach (var pc in region.ProgramCounters)
        {
            for (var register = 0; register < function.RegisterCount; register++)
            {
                var beforeKind = kindsBefore[pc][register];
                if (IsPromoted(beforeKind))
                {
                    registerSet.Add(new LuaNumericRegionRegister(register, beforeKind));
                }

                var afterKind = kindsAfter[pc][register];
                if (IsPromoted(afterKind))
                {
                    registerSet.Add(new LuaNumericRegionRegister(register, afterKind));
                }
            }
        }

        var registers = registerSet
            .OrderBy(static register => register.Register)
            .ThenBy(static register => register.Kind)
            .ToImmutableArray();
        if (directNumericInstructionCount == 0 || registers.IsEmpty)
        {
            return null;
        }

        var backedges = region.ProgramCounters
            .Where(pc => IsTakenBackedge(function.Instructions[pc], pc, inside))
            .ToImmutableArray();
        if (backedges.IsEmpty)
        {
            return null;
        }

        if (!TryBuildBudgetPlan(
                function,
                region,
                inside,
                backedges,
                cancellationToken,
                out var budgetSites,
                out var maximumBackedgeSegmentInstructionCost))
        {
            return null;
        }

        return new LuaNumericRegionPlan(
            region,
            registers,
            backedges,
            directNumericInstructionCount,
            budgetSites,
            maximumBackedgeSegmentInstructionCost,
            HotInstructionBudgetCheckCount: 0,
            kindsBefore,
            kindsAfter);
    }

    private static bool TryBuildBudgetPlan(
        LuaIrFunction function,
        LuaNaturalLoopRegion region,
        HashSet<int> inside,
        ImmutableArray<int> backedges,
        CancellationToken cancellationToken,
        out ImmutableArray<LuaNumericRegionBudgetSite> sites,
        out int maximumBackedgeSegmentInstructionCost)
    {
        sites = [];
        maximumBackedgeSegmentInstructionCost = 0;
        var blocks = function.BasicBlocks.IsDefaultOrEmpty
            ? LuaIrControlFlow.Build(function.Instructions)
            : function.BasicBlocks;
        var blockIndexByProgramCounter = new int[function.Instructions.Length];
        Array.Fill(blockIndexByProgramCounter, -1);
        var mappedProgramCounterCount = 0;
        for (var blockIndex = 0; blockIndex < blocks.Length; blockIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var block = blocks[blockIndex];
            if (!inside.Contains(block.Start))
            {
                continue;
            }

            for (var pc = block.Start; pc < block.End; pc++)
            {
                if (!inside.Contains(pc) || blockIndexByProgramCounter[pc] >= 0)
                {
                    return false;
                }

                blockIndexByProgramCounter[pc] = blockIndex;
                mappedProgramCounterCount++;
            }
        }

        if (mappedProgramCounterCount != region.ProgramCounters.Length)
        {
            return false;
        }

        var maximumCosts = new int[function.Instructions.Length];
        Array.Fill(maximumCosts, -1);
        var visiting = new bool[function.Instructions.Length];
        bool TryComputeMaximumCost(int programCounter, out int cost)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!inside.Contains(programCounter))
            {
                cost = 0;
                return true;
            }

            cost = maximumCosts[programCounter];
            if (cost >= 0)
            {
                return true;
            }

            if (visiting[programCounter])
            {
                cost = 0;
                return false;
            }

            visiting[programCounter] = true;
            var instruction = function.Instructions[programCounter];
            var successorMaximum = 0;
            foreach (var successor in Successors(instruction, programCounter))
            {
                if (!inside.Contains(successor) ||
                    IsCutBackedge(instruction, programCounter, successor))
                {
                    continue;
                }

                if (!TryComputeMaximumCost(successor, out var successorCost))
                {
                    visiting[programCounter] = false;
                    cost = 0;
                    return false;
                }

                successorMaximum = Math.Max(successorMaximum, successorCost);
            }

            visiting[programCounter] = false;
            cost = checked(1 + successorMaximum);
            maximumCosts[programCounter] = cost;
            return true;
        }

        foreach (var pc in region.ProgramCounters)
        {
            if (!TryComputeMaximumCost(pc, out _))
            {
                return false;
            }
        }

        var byProgramCounter = Enumerable.Repeat(
                new LuaNumericRegionBudgetSite(ProgramCounter: -1, 0, 0, 0, 0, 0, 0, 0, 0),
                function.Instructions.Length)
            .ToArray();
        foreach (var pc in region.ProgramCounters)
        {
            var block = blocks[blockIndexByProgramCounter[pc]];
            var remainingBlockCost = block.End - pc;
            byProgramCounter[pc] = new LuaNumericRegionBudgetSite(
                pc,
                block.Start,
                block.End,
                block.Length,
                remainingBlockCost,
                maximumCosts[pc],
                DeoptimizationProgramCounter: pc,
                FailureInstructionRollbackCount: remainingBlockCost,
                ColdSlowTailProgramCounter: pc);
        }

        foreach (var backedge in backedges)
        {
            var target = function.Instructions[backedge].B;
            var segmentCost = maximumCosts[target];
            if (segmentCost < 0)
            {
                return false;
            }

            maximumBackedgeSegmentInstructionCost = Math.Max(
                maximumBackedgeSegmentInstructionCost,
                segmentCost);
        }

        if (maximumBackedgeSegmentInstructionCost <= 0)
        {
            return false;
        }

        sites = byProgramCounter.ToImmutableArray();
        return true;
    }

    private static bool IsCutBackedge(
        LuaIrInstruction instruction,
        int programCounter,
        int successor) => successor <= programCounter &&
        instruction.B == successor &&
        LuaNumericRegionAnalyzer.IsBackedgeInstruction(instruction, programCounter);

    private static bool TryBuildReachingDefinitions(
        LuaIrFunction function,
        LuaNaturalLoopRegion region,
        HashSet<int> inside,
        CancellationToken cancellationToken,
        out Dictionary<int, ImmutableArray<ValueDefinition>[]> before,
        out Dictionary<int, ImmutableArray<ValueDefinition>[]> after)
    {
        before = region.ProgramCounters.ToDictionary(
            static pc => pc,
            _ => CreateDefinitionState(function.RegisterCount));
        after = region.ProgramCounters.ToDictionary(
            static pc => pc,
            _ => CreateDefinitionState(function.RegisterCount));
        for (var register = 0; register < function.RegisterCount; register++)
        {
            before[region.HeaderProgramCounter][register] =
                [ValueDefinition.Entry(register)];
        }

        var pending = new Queue<int>();
        var scheduled = new HashSet<int>();
        pending.Enqueue(region.HeaderProgramCounter);
        scheduled.Add(region.HeaderProgramCounter);
        while (pending.TryDequeue(out var pc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            scheduled.Remove(pc);
            var outgoing = after[pc];
            Array.Copy(before[pc], outgoing, outgoing.Length);
            foreach (var register in WrittenRegisters(
                         function.Instructions[pc],
                         function.RegisterCount))
            {
                if ((uint)register >= (uint)function.RegisterCount)
                {
                    return false;
                }

                outgoing[register] = [new ValueDefinition(pc, register)];
            }

            foreach (var successor in Successors(function.Instructions[pc], pc))
            {
                if (!inside.Contains(successor) ||
                    !MergeDefinitionState(before[successor], outgoing) ||
                    !scheduled.Add(successor))
                {
                    continue;
                }

                pending.Enqueue(successor);
            }
        }

        foreach (var pc in region.ProgramCounters)
        {
            if (pc != region.HeaderProgramCounter &&
                !before[pc].Any(static set => !set.IsDefaultOrEmpty))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ApplyStructuralConstraints(
        LuaIrFunction function,
        int pc,
        ImmutableArray<ValueDefinition>[] before,
        ExactTypeSolver solver)
    {
        var instruction = function.Instructions[pc];
        switch (instruction.Opcode)
        {
            case LuaIrOpcode.LoadConstant:
                return TryAssignDefinition(
                    solver,
                    new ValueDefinition(pc, instruction.A),
                    ConstantKind(function.Constants[instruction.B]));
            case LuaIrOpcode.Move:
                return TryUnifyDefinitionWithUse(
                    solver,
                    new ValueDefinition(pc, instruction.A),
                    before[instruction.B]);
            case LuaIrOpcode.SetTop:
                if (instruction.A < 0 || instruction.A > function.RegisterCount)
                {
                    return false;
                }

                for (var register = instruction.A;
                     register < function.RegisterCount;
                     register++)
                {
                    if (!solver.TryGetId(new ValueDefinition(pc, register), out var cleared) ||
                        !solver.TryAssign(cleared, LuaNumericRegionValueKind.Cleared))
                    {
                        return false;
                    }
                }

                return true;
            case LuaIrOpcode.Unary:
                return ApplyUnaryConstraints(instruction, pc, before, solver);
            case LuaIrOpcode.Binary:
                return ApplyBinaryStructuralConstraints(instruction, pc, before, solver);
            case LuaIrOpcode.Jump:
                return instruction.C < 0;
            case LuaIrOpcode.JumpIfFalse:
            case LuaIrOpcode.JumpIfTrue:
                return instruction.D == 0 &&
                    TryGetUseId(solver, before[instruction.A], out _);
            case LuaIrOpcode.NumericForLoop:
                return ApplyNumericForConstraints(instruction, pc, before, solver);
            default:
                return false;
        }
    }

    private static bool ApplyUnaryConstraints(
        LuaIrInstruction instruction,
        int pc,
        ImmutableArray<ValueDefinition>[] before,
        ExactTypeSolver solver)
    {
        if (!TryGetUseId(solver, before[instruction.B], out var operand) ||
            !solver.TryGetId(new ValueDefinition(pc, instruction.A), out var destination))
        {
            return false;
        }

        return (LuaIrUnaryOperator)instruction.C switch
        {
            LuaIrUnaryOperator.Negate => solver.TryUnion(operand, destination),
            LuaIrUnaryOperator.BitwiseNot =>
                solver.TryAssign(operand, LuaNumericRegionValueKind.Integer) &&
                solver.TryAssign(destination, LuaNumericRegionValueKind.Integer),
            LuaIrUnaryOperator.LogicalNot =>
                solver.TryAssign(destination, LuaNumericRegionValueKind.Boolean),
            _ => false,
        };
    }

    private static bool ApplyBinaryStructuralConstraints(
        LuaIrInstruction instruction,
        int pc,
        ImmutableArray<ValueDefinition>[] before,
        ExactTypeSolver solver)
    {
        if (!TryGetUseId(solver, before[instruction.B], out var left) ||
            !TryGetUseId(solver, before[instruction.C], out var right) ||
            !solver.TryGetId(new ValueDefinition(pc, instruction.A), out var destination))
        {
            return false;
        }

        var operation = (LuaIrBinaryOperator)instruction.D;
        if (operation == LuaIrBinaryOperator.Concatenate)
        {
            return false;
        }

        if (operation is LuaIrBinaryOperator.BitwiseAnd or LuaIrBinaryOperator.BitwiseOr or
            LuaIrBinaryOperator.BitwiseXor or LuaIrBinaryOperator.ShiftLeft or
            LuaIrBinaryOperator.ShiftRight)
        {
            return solver.TryAssign(left, LuaNumericRegionValueKind.Integer) &&
                solver.TryAssign(right, LuaNumericRegionValueKind.Integer) &&
                solver.TryAssign(destination, LuaNumericRegionValueKind.Integer);
        }

        if (IsComparison(operation))
        {
            return solver.TryAssign(destination, LuaNumericRegionValueKind.Boolean);
        }

        return operation is LuaIrBinaryOperator.Divide or LuaIrBinaryOperator.Power
            ? solver.TryAssign(destination, LuaNumericRegionValueKind.Float)
            : true;
    }

    private static bool ApplyBinaryResultConstraint(
        LuaIrInstruction instruction,
        int pc,
        ImmutableArray<ValueDefinition>[] before,
        ExactTypeSolver solver)
    {
        var operation = (LuaIrBinaryOperator)instruction.D;
        if (operation == LuaIrBinaryOperator.Concatenate ||
            !TryGetUseId(solver, before[instruction.B], out var left) ||
            !TryGetUseId(solver, before[instruction.C], out var right) ||
            !solver.TryGetId(new ValueDefinition(pc, instruction.A), out var destination))
        {
            return false;
        }

        if (operation is LuaIrBinaryOperator.BitwiseAnd or LuaIrBinaryOperator.BitwiseOr or
            LuaIrBinaryOperator.BitwiseXor or LuaIrBinaryOperator.ShiftLeft or
            LuaIrBinaryOperator.ShiftRight || IsComparison(operation) ||
            operation is LuaIrBinaryOperator.Divide or LuaIrBinaryOperator.Power)
        {
            return true;
        }

        var leftKind = solver.GetKind(left);
        var rightKind = solver.GetKind(right);
        if (!IsNumeric(leftKind) || !IsNumeric(rightKind))
        {
            return true;
        }

        var resultKind = leftKind == LuaNumericRegionValueKind.Integer &&
            rightKind == LuaNumericRegionValueKind.Integer
                ? LuaNumericRegionValueKind.Integer
                : LuaNumericRegionValueKind.Float;
        return solver.TryAssign(destination, resultKind);
    }

    private static bool ApplyNumericForConstraints(
        LuaIrInstruction instruction,
        int pc,
        ImmutableArray<ValueDefinition>[] before,
        ExactTypeSolver solver)
    {
        var ids = new List<int>(7);
        for (var register = instruction.A; register < instruction.A + 4; register++)
        {
            if ((uint)register >= (uint)before.Length ||
                !TryGetUseId(solver, before[register], out var id))
            {
                return false;
            }

            ids.Add(id);
        }

        foreach (var register in new[]
                 {
                     instruction.A,
                     instruction.A + 1,
                     instruction.A + 3,
                 })
        {
            if (!solver.TryGetId(new ValueDefinition(pc, register), out var id))
            {
                return false;
            }

            ids.Add(id);
        }

        for (var index = 1; index < ids.Count; index++)
        {
            if (!solver.TryUnion(ids[0], ids[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static ImmutableArray<ImmutableArray<LuaNumericRegionValueKind>>
        CreateKindMaps(
            LuaIrFunction function,
            Dictionary<int, ImmutableArray<ValueDefinition>[]> states,
            ExactTypeSolver solver,
            CancellationToken cancellationToken)
    {
        var result = ImmutableArray.CreateBuilder<
            ImmutableArray<LuaNumericRegionValueKind>>(function.Instructions.Length);
        for (var pc = 0; pc < function.Instructions.Length; pc++)
        {
            if ((pc & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (!states.TryGetValue(pc, out var state))
            {
                result.Add([]);
                continue;
            }

            var kinds = ImmutableArray.CreateBuilder<LuaNumericRegionValueKind>(state.Length);
            for (var register = 0; register < state.Length; register++)
            {
                kinds.Add(ResolveKind(state[register], solver));
            }

            result.Add(kinds.MoveToImmutable());
        }

        return result.MoveToImmutable();
    }

    private static bool VerifyInstruction(
        LuaIrFunction function,
        int pc,
        ImmutableArray<LuaNumericRegionValueKind> before,
        ImmutableArray<LuaNumericRegionValueKind> after,
        ref int directNumericInstructionCount)
    {
        var instruction = function.Instructions[pc];
        switch (instruction.Opcode)
        {
            case LuaIrOpcode.LoadConstant:
                return ConstantKind(function.Constants[instruction.B]) ==
                    Kind(after, instruction.A);
            case LuaIrOpcode.Move:
                return IsPromoted(Kind(before, instruction.B)) &&
                    Kind(before, instruction.B) == Kind(after, instruction.A);
            case LuaIrOpcode.SetTop:
                return instruction.A >= 0 && instruction.A <= function.RegisterCount;
            case LuaIrOpcode.Unary:
                {
                    var operand = Kind(before, instruction.B);
                    var destination = Kind(after, instruction.A);
                    var valid = (LuaIrUnaryOperator)instruction.C switch
                    {
                        LuaIrUnaryOperator.Negate => IsNumeric(operand) &&
                            destination == operand,
                        LuaIrUnaryOperator.BitwiseNot =>
                            operand == LuaNumericRegionValueKind.Integer &&
                            destination == LuaNumericRegionValueKind.Integer,
                        LuaIrUnaryOperator.LogicalNot => IsPromoted(operand) &&
                            destination == LuaNumericRegionValueKind.Boolean,
                        _ => false,
                    };
                    directNumericInstructionCount += valid ? 1 : 0;
                    return valid;
                }
            case LuaIrOpcode.Binary:
                {
                    var left = Kind(before, instruction.B);
                    var right = Kind(before, instruction.C);
                    var destination = Kind(after, instruction.A);
                    var operation = (LuaIrBinaryOperator)instruction.D;
                    var valid = IsNumeric(left) && IsNumeric(right) &&
                        destination == BinaryResultKind(operation, left, right);
                    directNumericInstructionCount += valid ? 1 : 0;
                    return valid;
                }
            case LuaIrOpcode.Jump:
                return instruction.C < 0;
            case LuaIrOpcode.JumpIfFalse:
            case LuaIrOpcode.JumpIfTrue:
                return instruction.D == 0 && IsPromoted(Kind(before, instruction.A));
            case LuaIrOpcode.NumericForLoop:
                {
                    var kind = Kind(before, instruction.A);
                    var valid = IsNumeric(kind) &&
                        Enumerable.Range(instruction.A + 1, 3).All(register =>
                            Kind(before, register) == kind) &&
                        Kind(after, instruction.A) == kind &&
                        Kind(after, instruction.A + 1) == kind &&
                        Kind(after, instruction.A + 3) == kind;
                    directNumericInstructionCount += valid ? 1 : 0;
                    return valid;
                }
            default:
                return false;
        }
    }

    private static LuaNumericRegionValueKind BinaryResultKind(
        LuaIrBinaryOperator operation,
        LuaNumericRegionValueKind left,
        LuaNumericRegionValueKind right) => operation switch
        {
            LuaIrBinaryOperator.Concatenate => LuaNumericRegionValueKind.Unknown,
            LuaIrBinaryOperator.Equal or LuaIrBinaryOperator.NotEqual or
                LuaIrBinaryOperator.LessThan or LuaIrBinaryOperator.LessThanOrEqual or
                LuaIrBinaryOperator.GreaterThan or LuaIrBinaryOperator.GreaterThanOrEqual =>
                LuaNumericRegionValueKind.Boolean,
            LuaIrBinaryOperator.BitwiseAnd or LuaIrBinaryOperator.BitwiseOr or
                LuaIrBinaryOperator.BitwiseXor or LuaIrBinaryOperator.ShiftLeft or
                LuaIrBinaryOperator.ShiftRight =>
                left == LuaNumericRegionValueKind.Integer &&
                right == LuaNumericRegionValueKind.Integer
                    ? LuaNumericRegionValueKind.Integer
                    : LuaNumericRegionValueKind.Unknown,
            LuaIrBinaryOperator.Divide or LuaIrBinaryOperator.Power =>
                LuaNumericRegionValueKind.Float,
            _ => left == LuaNumericRegionValueKind.Integer &&
                right == LuaNumericRegionValueKind.Integer
                    ? LuaNumericRegionValueKind.Integer
                    : LuaNumericRegionValueKind.Float,
        };

    private static bool TryAssignUse(
        ExactTypeSolver solver,
        ImmutableArray<ValueDefinition> definitions,
        LuaNumericRegionValueKind kind)
    {
        if (!TryGetUseId(solver, definitions, out var id))
        {
            return false;
        }

        return solver.TryAssign(id, kind);
    }

    private static bool TryAssignDefinition(
        ExactTypeSolver solver,
        ValueDefinition definition,
        LuaNumericRegionValueKind kind) =>
        IsPromoted(kind) && solver.TryGetId(definition, out var id) &&
        solver.TryAssign(id, kind);

    private static bool TryUnifyDefinitionWithUse(
        ExactTypeSolver solver,
        ValueDefinition definition,
        ImmutableArray<ValueDefinition> uses) =>
        solver.TryGetId(definition, out var destination) &&
        TryGetUseId(solver, uses, out var source) &&
        solver.TryUnion(destination, source);

    private static bool TryGetUseId(
        ExactTypeSolver solver,
        ImmutableArray<ValueDefinition> definitions,
        out int id)
    {
        id = -1;
        foreach (var definition in definitions)
        {
            if (!solver.TryGetId(definition, out var candidate))
            {
                return false;
            }

            if (id < 0)
            {
                id = candidate;
            }
            else if (!solver.TryUnion(id, candidate))
            {
                return false;
            }
        }

        return id >= 0;
    }

    private static LuaNumericRegionValueKind ResolveKind(
        ImmutableArray<ValueDefinition> definitions,
        ExactTypeSolver solver)
    {
        var result = LuaNumericRegionValueKind.Unknown;
        foreach (var definition in definitions)
        {
            if (!solver.TryGetId(definition, out var id))
            {
                return LuaNumericRegionValueKind.Unknown;
            }

            var kind = solver.GetKind(id);
            if (!IsPromoted(kind))
            {
                return LuaNumericRegionValueKind.Unknown;
            }

            if (result == LuaNumericRegionValueKind.Unknown)
            {
                result = kind;
            }
            else if (result != kind)
            {
                return LuaNumericRegionValueKind.Conflict;
            }
        }

        return result;
    }

    private static ImmutableArray<ValueDefinition>[] CreateDefinitionState(
        int registerCount) => new ImmutableArray<ValueDefinition>[registerCount];

    private static bool MergeDefinitionState(
        ImmutableArray<ValueDefinition>[] destination,
        ImmutableArray<ValueDefinition>[] source)
    {
        var changed = false;
        for (var register = 0; register < destination.Length; register++)
        {
            var incoming = source[register];
            if (incoming.IsDefaultOrEmpty)
            {
                continue;
            }

            var current = destination[register];
            if (current.IsDefaultOrEmpty)
            {
                destination[register] = incoming;
                changed = true;
                continue;
            }

            ImmutableArray<ValueDefinition>.Builder? merged = null;
            foreach (var definition in incoming)
            {
                if (current.Contains(definition) ||
                    merged is not null && merged.Contains(definition))
                {
                    continue;
                }

                merged ??= current.ToBuilder();
                merged.Add(definition);
            }

            if (merged is not null)
            {
                destination[register] = merged.ToImmutable();
                changed = true;
            }
        }

        return changed;
    }

    private static IEnumerable<int> WrittenRegisters(
        LuaIrInstruction instruction,
        int registerCount) =>
        instruction.Opcode switch
        {
            LuaIrOpcode.LoadConstant or LuaIrOpcode.Move or LuaIrOpcode.Unary or
                LuaIrOpcode.Binary => [instruction.A],
            LuaIrOpcode.SetTop when instruction.A >= 0 && instruction.A <= registerCount =>
                Enumerable.Range(instruction.A, registerCount - instruction.A),
            LuaIrOpcode.NumericForLoop =>
                [instruction.A, instruction.A + 1, instruction.A + 3],
            _ => [],
        };

    private static IEnumerable<int> Successors(
        LuaIrInstruction instruction,
        int programCounter) => instruction.Opcode switch
        {
            LuaIrOpcode.Jump => [instruction.B],
            LuaIrOpcode.JumpIfFalse or LuaIrOpcode.JumpIfTrue or
                LuaIrOpcode.NumericForLoop => [instruction.B, programCounter + 1],
            _ => [programCounter + 1],
        };

    private static LuaNumericRegionValueKind ConstantKind(LuaIrConstant constant) =>
        constant.Kind switch
        {
            LuaIrConstantKind.Boolean => LuaNumericRegionValueKind.Boolean,
            LuaIrConstantKind.Integer => LuaNumericRegionValueKind.Integer,
            LuaIrConstantKind.Float => LuaNumericRegionValueKind.Float,
            _ => LuaNumericRegionValueKind.Unknown,
        };

    private static LuaNumericRegionValueKind Kind(
        ImmutableArray<LuaNumericRegionValueKind> kinds,
        int register) => (uint)register < (uint)kinds.Length
            ? kinds[register]
            : LuaNumericRegionValueKind.Unknown;

    private static bool IsPromoted(LuaNumericRegionValueKind kind) =>
        kind is LuaNumericRegionValueKind.Integer or LuaNumericRegionValueKind.Float or
            LuaNumericRegionValueKind.Boolean;

    private static bool IsNumeric(LuaNumericRegionValueKind kind) =>
        kind is LuaNumericRegionValueKind.Integer or LuaNumericRegionValueKind.Float;

    private static bool IsComparison(LuaIrBinaryOperator operation) => operation is
        LuaIrBinaryOperator.Equal or LuaIrBinaryOperator.NotEqual or
        LuaIrBinaryOperator.LessThan or LuaIrBinaryOperator.LessThanOrEqual or
        LuaIrBinaryOperator.GreaterThan or LuaIrBinaryOperator.GreaterThanOrEqual;

    private static bool IsTakenBackedge(
        LuaIrInstruction instruction,
        int programCounter,
        HashSet<int> inside) =>
        inside.Contains(instruction.B) && LuaNumericRegionAnalyzer.IsBackedgeInstruction(
            instruction,
            programCounter);

    private readonly record struct ValueDefinition(int ProgramCounter, int Register)
    {
        public static ValueDefinition Entry(int register) => new(-1, register);
    }

    private sealed class ExactTypeSolver
    {
        private readonly Dictionary<ValueDefinition, int> _ids = [];
        private readonly List<int> _parents = [];
        private readonly List<byte> _ranks = [];
        private readonly List<LuaNumericRegionValueKind> _kinds = [];

        public int Version { get; private set; }

        public int GetOrAdd(ValueDefinition definition)
        {
            if (_ids.TryGetValue(definition, out var existing))
            {
                return existing;
            }

            var id = _parents.Count;
            _ids.Add(definition, id);
            _parents.Add(id);
            _ranks.Add(0);
            _kinds.Add(LuaNumericRegionValueKind.Unknown);
            return id;
        }

        public bool TryGetId(ValueDefinition definition, out int id) =>
            _ids.TryGetValue(definition, out id);

        public LuaNumericRegionValueKind GetKind(int id) => _kinds[Find(id)];

        public bool TryAssign(int id, LuaNumericRegionValueKind kind)
        {
            if (!IsConstraintKind(kind))
            {
                return false;
            }

            var root = Find(id);
            if (_kinds[root] == LuaNumericRegionValueKind.Unknown)
            {
                _kinds[root] = kind;
                Version++;
                return true;
            }

            return _kinds[root] == kind;
        }

        public bool TryUnion(int left, int right)
        {
            var leftRoot = Find(left);
            var rightRoot = Find(right);
            if (leftRoot == rightRoot)
            {
                return true;
            }

            var leftKind = _kinds[leftRoot];
            var rightKind = _kinds[rightRoot];
            if (IsConstraintKind(leftKind) && IsConstraintKind(rightKind) &&
                leftKind != rightKind)
            {
                return false;
            }

            if (_ranks[leftRoot] < _ranks[rightRoot])
            {
                (leftRoot, rightRoot) = (rightRoot, leftRoot);
                (leftKind, rightKind) = (rightKind, leftKind);
            }

            _parents[rightRoot] = leftRoot;
            if (_ranks[leftRoot] == _ranks[rightRoot])
            {
                _ranks[leftRoot]++;
            }

            _kinds[leftRoot] = IsConstraintKind(leftKind) ? leftKind : rightKind;
            Version++;
            return true;
        }

        private int Find(int id)
        {
            var root = id;
            while (_parents[root] != root)
            {
                root = _parents[root];
            }

            while (_parents[id] != id)
            {
                var parent = _parents[id];
                _parents[id] = root;
                id = parent;
            }

            return root;
        }

        private static bool IsConstraintKind(LuaNumericRegionValueKind kind) =>
            IsPromoted(kind) || kind == LuaNumericRegionValueKind.Cleared;
    }
}
