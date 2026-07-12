using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Lunil.CodeGen.Cil.Planning;

namespace Lunil.CodeGen.Cil.Verification;

public static class CilMethodPlanVerifier
{
    public static CilPlanVerificationResult Verify(
        CilMethodPlan plan,
        CilPlanLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        limits ??= CilPlanLimits.Default;
        var diagnostics = ImmutableArray.CreateBuilder<CilPlanDiagnostic>();
        if (plan.Instructions.Length > limits.MaximumInstructions)
        {
            diagnostics.Add(new CilPlanDiagnostic(
                "CIL0001",
                $"Method plan contains {plan.Instructions.Length} instructions; limit is " +
                $"{limits.MaximumInstructions}."));
            return new CilPlanVerificationResult(diagnostics.ToImmutable(), 0);
        }

        if (plan.Instructions.IsEmpty)
        {
            diagnostics.Add(new CilPlanDiagnostic("CIL0002", "Method plan is empty."));
            return new CilPlanVerificationResult(diagnostics.ToImmutable(), 0);
        }

        var branchInstructionCount = plan.Instructions.Sum(static instruction =>
            instruction.OpCode == CilPlanOpCode.Switch
                ? instruction.Labels.Length
                : instruction.OpCode is CilPlanOpCode.Branch or CilPlanOpCode.BranchTrue or
                    CilPlanOpCode.BranchFalse ? 1 : 0);
        cancellationToken.ThrowIfCancellationRequested();
        if (branchInstructionCount > limits.MaximumBranchInstructions)
        {
            diagnostics.Add(new CilPlanDiagnostic(
                "CIL0026",
                $"Method plan contains {branchInstructionCount} branch targets; limit is " +
                $"{limits.MaximumBranchInstructions}."));
            return new CilPlanVerificationResult(diagnostics.ToImmutable(), 0);
        }

        var metadataReferenceCount = plan.Instructions
            .Where(static instruction => instruction.CallTarget is not null)
            .Select(static instruction => instruction.CallTarget!.Id)
            .Distinct(StringComparer.Ordinal)
            .Count();
        cancellationToken.ThrowIfCancellationRequested();
        if (metadataReferenceCount > limits.MaximumMetadataReferences)
        {
            diagnostics.Add(new CilPlanDiagnostic(
                "CIL0027",
                $"Method plan references {metadataReferenceCount} metadata members; limit is " +
                $"{limits.MaximumMetadataReferences}."));
            return new CilPlanVerificationResult(diagnostics.ToImmutable(), 0);
        }

        var labels = BuildLabelMap(plan, limits, diagnostics, cancellationToken);
        ValidateMetadata(plan, diagnostics, cancellationToken);
        var incoming = new EvaluationStack?[plan.Instructions.Length];
        var queue = new Queue<int>();
        incoming[0] = default(EvaluationStack);
        queue.Enqueue(0);
        var maximumStack = 0;
        while (queue.TryDequeue(out var index))
        {
            if ((index & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var stack = incoming[index]!.Value.Clone();
            maximumStack = Math.Max(maximumStack, stack.Count);
            var instruction = plan.Instructions[index];
            var valid = ApplyInstruction(plan, instruction, index, ref stack, diagnostics);
            maximumStack = Math.Max(maximumStack, stack.Count);
            if (stack.Count > limits.MaximumEvaluationStack)
            {
                diagnostics.Add(Error(
                    "CIL0003",
                    $"Evaluation stack depth {stack.Count} exceeds limit " +
                    $"{limits.MaximumEvaluationStack}.",
                    index,
                    instruction));
                valid = false;
            }

            if (!valid)
            {
                continue;
            }

            var outgoing = stack;
            foreach (var successor in Successors(plan, labels, index, instruction, diagnostics))
            {
                if (successor < 0 || successor >= plan.Instructions.Length)
                {
                    continue;
                }

                if (incoming[successor] is null)
                {
                    incoming[successor] = outgoing;
                    queue.Enqueue(successor);
                }
                else if (!incoming[successor]!.Value.SequenceEqual(outgoing))
                {
                    diagnostics.Add(Error(
                        "CIL0004",
                        "Control-flow merge has incompatible evaluation-stack shapes.",
                        successor,
                        plan.Instructions[successor]));
                }
            }
        }

        for (var index = 0; index < incoming.Length; index++)
        {
            if ((index & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (incoming[index] is null)
            {
                diagnostics.Add(Error(
                    "CIL0025",
                    "Method plan contains unreachable instructions.",
                    index,
                    plan.Instructions[index]));
            }
        }

        return new CilPlanVerificationResult(diagnostics.ToImmutable(), maximumStack);
    }

    private static Dictionary<int, int> BuildLabelMap(
        CilMethodPlan plan,
        CilPlanLimits limits,
        ImmutableArray<CilPlanDiagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var labels = new Dictionary<int, int>();
        for (var index = 0; index < plan.Instructions.Length; index++)
        {
            if ((index & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var instruction = plan.Instructions[index];
            if (instruction.OpCode != CilPlanOpCode.MarkLabel)
            {
                continue;
            }

            if (instruction.Label.Id < 0)
            {
                diagnostics.Add(Error("CIL0005", "Label id cannot be negative.", index, instruction));
            }
            else if (!labels.TryAdd(instruction.Label.Id, index))
            {
                diagnostics.Add(Error("CIL0006", "Label id is defined more than once.", index, instruction));
            }
        }

        if (labels.Count > limits.MaximumLabels)
        {
            diagnostics.Add(new CilPlanDiagnostic(
                "CIL0007",
                $"Method plan defines {labels.Count} labels; limit is {limits.MaximumLabels}."));
        }

        return labels;
    }

    private static void ValidateMetadata(
        CilMethodPlan plan,
        ImmutableArray<CilPlanDiagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var localIndexes = new HashSet<int>();
        foreach (var local in plan.Locals)
        {
            if (local.Index < 0 || !localIndexes.Add(local.Index))
            {
                diagnostics.Add(new CilPlanDiagnostic(
                    "CIL0008",
                    "Local indexes must be non-negative and unique."));
            }
        }

        var gcProgramCounters = new HashSet<int>();
        foreach (var map in plan.GcMaps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (map.CanonicalProgramCounter < 0 || !gcProgramCounters.Add(map.CanonicalProgramCounter) ||
                map.LiveRegisters.Any(register => register < 0 || register >= plan.RegisterCount) ||
                !map.LiveRegisters.SequenceEqual(map.LiveRegisters.Distinct().Order()))
            {
                diagnostics.Add(new CilPlanDiagnostic(
                    "CIL0009",
                    "GC maps require a unique non-negative PC and sorted unique registers.",
                    CanonicalProgramCounter: map.CanonicalProgramCounter));
            }
        }

        foreach (var point in plan.SequencePoints)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (point.PlanInstructionIndex < 0 || point.PlanInstructionIndex >= plan.Instructions.Length ||
                point.CanonicalProgramCounter < 0 || point.SourceLine < 0)
            {
                diagnostics.Add(new CilPlanDiagnostic(
                    "CIL0010",
                    "Sequence point is outside the method plan or canonical source range.",
                    point.PlanInstructionIndex,
                    point.CanonicalProgramCounter));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        ValidateBlocks(plan, diagnostics);
    }

    private static void ValidateBlocks(
        CilMethodPlan plan,
        ImmutableArray<CilPlanDiagnostic>.Builder diagnostics)
    {
        if (plan.Blocks.IsEmpty)
        {
            return;
        }

        var starts = plan.Blocks.Select(static block => block.StartProgramCounter).ToHashSet();
        var expectedStart = plan.StartProgramCounter;
        foreach (var block in plan.Blocks.OrderBy(static block => block.StartProgramCounter))
        {
            if (block.StartProgramCounter != expectedStart || block.Length <= 0 ||
                block.Successors.Any(successor => !starts.Contains(successor)))
            {
                diagnostics.Add(new CilPlanDiagnostic(
                    "CIL0022",
                    "Canonical block layout must be dense, non-empty, and target block starts.",
                    CanonicalProgramCounter: block.StartProgramCounter));
                return;
            }

            expectedStart = checked(expectedStart + block.Length);
        }

        if (expectedStart != checked(
            plan.StartProgramCounter + plan.CanonicalInstructionCount))
        {
            diagnostics.Add(new CilPlanDiagnostic(
                "CIL0022",
                "Canonical block layout does not cover the complete function."));
        }
    }

    private static bool ApplyInstruction(
        CilMethodPlan plan,
        CilPlanInstruction instruction,
        int index,
        ref EvaluationStack stack,
        ImmutableArray<CilPlanDiagnostic>.Builder diagnostics)
    {
        switch (instruction.OpCode)
        {
            case CilPlanOpCode.MarkLabel:
            case CilPlanOpCode.Nop:
            case CilPlanOpCode.Branch:
                return true;
            case CilPlanOpCode.LoadArgument:
                if (instruction.Int32Operand < 0 ||
                    instruction.Int32Operand >= plan.ParameterKinds.Length)
                {
                    diagnostics.Add(Error("CIL0011", "Argument index is invalid.", index, instruction));
                    return false;
                }

                stack.Push(plan.ParameterKinds[instruction.Int32Operand]);
                return true;
            case CilPlanOpCode.LoadLocal:
                return TryLoadLocal(plan, instruction, index, ref stack, diagnostics);
            case CilPlanOpCode.StoreLocal:
                return TryStoreLocal(plan, instruction, index, ref stack, diagnostics);
            case CilPlanOpCode.LoadInt32:
                stack.Push(CilStackValueKind.Int32);
                return true;
            case CilPlanOpCode.Add:
            case CilPlanOpCode.Subtract:
                return Pop(ref stack, CilStackValueKind.Int32, index, instruction, diagnostics) &&
                    Pop(ref stack, CilStackValueKind.Int32, index, instruction, diagnostics) &&
                    Push(ref stack, CilStackValueKind.Int32);
            case CilPlanOpCode.Call:
                return ApplyCall(plan, instruction, index, ref stack, diagnostics);
            case CilPlanOpCode.BranchTrue:
            case CilPlanOpCode.BranchFalse:
            case CilPlanOpCode.Switch:
                return Pop(ref stack, CilStackValueKind.Int32, index, instruction, diagnostics);
            case CilPlanOpCode.Return:
                if (!Pop(ref stack, plan.ReturnKind, index, instruction, diagnostics))
                {
                    return false;
                }

                if (stack.Count != 0)
                {
                    diagnostics.Add(Error(
                        "CIL0012",
                        "Return leaves values on the evaluation stack.",
                        index,
                        instruction));
                    return false;
                }

                return true;
            default:
                diagnostics.Add(Error("CIL0013", "Unknown plan opcode.", index, instruction));
                return false;
        }
    }

    private static bool ApplyCall(
        CilMethodPlan plan,
        CilPlanInstruction instruction,
        int index,
        ref EvaluationStack stack,
        ImmutableArray<CilPlanDiagnostic>.Builder diagnostics)
    {
        if (instruction.CallTarget is not { } target)
        {
            diagnostics.Add(Error("CIL0014", "Call has no target signature.", index, instruction));
            return false;
        }

        if (target.Id.StartsWith("Lua", StringComparison.Ordinal))
        {
            if (!CilWellKnownCalls.TryGet(target.Id, out var expected))
            {
                diagnostics.Add(Error(
                    "CIL0023",
                    "Runtime ABI call target is not registered.",
                    index,
                    instruction));
                return false;
            }

            if (target.ReturnKind != expected.ReturnKind ||
                target.IsGcSafePoint != expected.IsGcSafePoint ||
                !target.ParameterKinds.SequenceEqual(expected.ParameterKinds))
            {
                diagnostics.Add(Error(
                    "CIL0024",
                    "Runtime ABI call signature does not match the registered contract.",
                    index,
                    instruction));
                return false;
            }
        }

        for (var parameter = target.ParameterKinds.Length - 1; parameter >= 0; parameter--)
        {
            if (!Pop(ref stack, target.ParameterKinds[parameter], index, instruction, diagnostics))
            {
                return false;
            }
        }

        if (target.IsGcSafePoint)
        {
            if (stack.Contains(CilStackValueKind.LuaValue))
            {
                diagnostics.Add(Error(
                    "CIL0015",
                    "A logical-GC safe point retains an unspilled LuaValue on the CIL stack.",
                    index,
                    instruction));
                return false;
            }

            if (instruction.CanonicalProgramCounter < 0 || !plan.GcMaps.Any(map =>
                map.CanonicalProgramCounter == instruction.CanonicalProgramCounter))
            {
                diagnostics.Add(Error(
                    "CIL0016",
                    "A logical-GC safe point has no canonical GC map.",
                    index,
                    instruction));
                return false;
            }
        }

        if (target.ReturnKind != CilStackValueKind.Void)
        {
            stack.Push(target.ReturnKind);
        }

        return true;
    }

    private static bool TryLoadLocal(
        CilMethodPlan plan,
        CilPlanInstruction instruction,
        int index,
        ref EvaluationStack stack,
        ImmutableArray<CilPlanDiagnostic>.Builder diagnostics)
    {
        var local = plan.Locals.FirstOrDefault(local => local.Index == instruction.Int32Operand);
        if (local is null)
        {
            diagnostics.Add(Error("CIL0017", "Local index is invalid.", index, instruction));
            return false;
        }

        stack.Push(local.Kind);
        return true;
    }

    private static bool TryStoreLocal(
        CilMethodPlan plan,
        CilPlanInstruction instruction,
        int index,
        ref EvaluationStack stack,
        ImmutableArray<CilPlanDiagnostic>.Builder diagnostics)
    {
        var local = plan.Locals.FirstOrDefault(local => local.Index == instruction.Int32Operand);
        return local is not null
            ? Pop(ref stack, local.Kind, index, instruction, diagnostics)
            : AddMissingLocal(index, instruction, diagnostics);
    }

    private static bool AddMissingLocal(
        int index,
        CilPlanInstruction instruction,
        ImmutableArray<CilPlanDiagnostic>.Builder diagnostics)
    {
        diagnostics.Add(Error("CIL0017", "Local index is invalid.", index, instruction));
        return false;
    }

    private static IEnumerable<int> Successors(
        CilMethodPlan plan,
        IReadOnlyDictionary<int, int> labels,
        int index,
        CilPlanInstruction instruction,
        ImmutableArray<CilPlanDiagnostic>.Builder diagnostics)
    {
        switch (instruction.OpCode)
        {
            case CilPlanOpCode.Return:
                yield break;
            case CilPlanOpCode.Branch:
                yield return ResolveLabel(labels, instruction.Label, index, instruction, diagnostics);
                yield break;
            case CilPlanOpCode.BranchTrue:
            case CilPlanOpCode.BranchFalse:
                yield return ResolveLabel(labels, instruction.Label, index, instruction, diagnostics);
                break;
            case CilPlanOpCode.Switch:
                foreach (var label in instruction.Labels)
                {
                    yield return ResolveLabel(labels, label, index, instruction, diagnostics);
                }

                break;
        }

        if (index + 1 < plan.Instructions.Length)
        {
            yield return index + 1;
        }
        else
        {
            diagnostics.Add(Error(
                "CIL0018",
                "Reachable control flow falls off the end of the method.",
                index,
                instruction));
        }
    }

    private static int ResolveLabel(
        IReadOnlyDictionary<int, int> labels,
        CilLabel label,
        int index,
        CilPlanInstruction instruction,
        ImmutableArray<CilPlanDiagnostic>.Builder diagnostics)
    {
        if (labels.TryGetValue(label.Id, out var target))
        {
            return target;
        }

        diagnostics.Add(Error("CIL0019", "Branch target label is undefined.", index, instruction));
        return -1;
    }

    private static bool Pop(
        ref EvaluationStack stack,
        CilStackValueKind expected,
        int index,
        CilPlanInstruction instruction,
        ImmutableArray<CilPlanDiagnostic>.Builder diagnostics)
    {
        if (stack.Count == 0)
        {
            diagnostics.Add(Error("CIL0020", "Evaluation stack underflow.", index, instruction));
            return false;
        }

        var actual = stack.Pop();
        if (actual == expected)
        {
            return true;
        }

        diagnostics.Add(Error(
            "CIL0021",
            $"Evaluation stack type mismatch: expected {expected}, found {actual}.",
            index,
            instruction));
        return false;
    }

    private static bool Push(
        ref EvaluationStack stack,
        CilStackValueKind value)
    {
        stack.Push(value);
        return true;
    }

    [InlineArray(16)]
    private struct InlineStackStorage
    {
        private CilStackValueKind _element0;
    }

    private struct EvaluationStack
    {
        private InlineStackStorage _inline;
        private CilStackValueKind[]? _overflow;

        public int Count { get; private set; }

        public void Push(CilStackValueKind value)
        {
            if (Count < 16)
            {
                _inline[Count++] = value;
                return;
            }

            var overflowIndex = Count - 16;
            if (_overflow is null)
            {
                _overflow = new CilStackValueKind[16];
            }
            else if (overflowIndex == _overflow.Length)
            {
                Array.Resize(ref _overflow, checked(_overflow.Length * 2));
            }

            _overflow[overflowIndex] = value;
            Count++;
        }

        public CilStackValueKind Pop()
        {
            var index = --Count;
            return index < 16 ? _inline[index] : _overflow![index - 16];
        }

        public bool Contains(CilStackValueKind value)
        {
            for (var index = 0; index < Count; index++)
            {
                if (Get(index) == value)
                {
                    return true;
                }
            }

            return false;
        }

        public bool SequenceEqual(EvaluationStack other)
        {
            if (Count != other.Count)
            {
                return false;
            }

            for (var index = 0; index < Count; index++)
            {
                if (Get(index) != other.Get(index))
                {
                    return false;
                }
            }

            return true;
        }

        public EvaluationStack Clone()
        {
            var clone = this;
            if (_overflow is not null)
            {
                clone._overflow = (CilStackValueKind[])_overflow.Clone();
            }

            return clone;
        }

        private readonly CilStackValueKind Get(int index) =>
            index < 16 ? _inline[index] : _overflow![index - 16];
    }

    private static CilPlanDiagnostic Error(
        string code,
        string message,
        int index,
        CilPlanInstruction instruction) =>
        new(code, message, index, instruction.CanonicalProgramCounter);
}
