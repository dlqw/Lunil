using Lunil.IR.Canonical;

namespace Lunil.CodeGen.Cil.Jit;

public enum LuaJitEligibilityReason : byte
{
    Eligible,
    VerificationFailed,
    EstimatedCodeSizeTooLarge,
    NoRepeatedWork,
    DirectCoverageTooLow,
    SlowPathDensityTooHigh,
    SemanticBoundaryDensityTooHigh,
}

public enum LuaJitBreakEvenClass : byte
{
    Unfavorable,
    WithinCurrentInvocation,
    RepeatedInvocation,
    HighReuse,
}

public sealed record LuaJitFunctionEligibility(
    bool IsCompilable,
    bool IsAutoEligible,
    LuaJitEligibilityReason Reason,
    LuaJitBreakEvenClass BreakEvenClass,
    string? DiagnosticCode,
    int CanonicalInstructionCount,
    int BackedgeCount,
    int DirectCanonicalInstructionCount,
    int SlowPathCanonicalInstructionCount,
    int SchedulerBoundaryCount,
    int SemanticRiskCount,
    int PlanInstructionCount,
    long EstimatedCodeBytes)
{
    public double DirectCoverage => CanonicalInstructionCount == 0
        ? 0
        : (double)DirectCanonicalInstructionCount / CanonicalInstructionCount;

    public double SlowPathDensity => CanonicalInstructionCount == 0
        ? 1
        : (double)SlowPathCanonicalInstructionCount / CanonicalInstructionCount;

    public double SchedulerBoundaryDensity => CanonicalInstructionCount == 0
        ? 1
        : (double)SchedulerBoundaryCount / CanonicalInstructionCount;
}

internal static class LuaTier1EligibilityEvaluator
{
    private const double MinimumDirectCoverage = 0.80;
    private const double MaximumSlowPathDensity = 0.10;
    private const double MaximumSemanticBoundaryDensity = 0.15;
    private const int MinimumRepeatedInstructionCount = 24;
    private const long MaximumEstimatedCodeBytes = 512 * 1024;

    public static LuaJitFunctionEligibility Evaluate(
        LuaIrModule module,
        int functionId,
        bool includeInstructionObservation)
    {
        var planning = LuaCilCodeGenerator.PlanFunction(
            module,
            functionId,
            includeInstructionObservation: includeInstructionObservation);
        if (!planning.Succeeded)
        {
            return new LuaJitFunctionEligibility(
                IsCompilable: false,
                IsAutoEligible: false,
                LuaJitEligibilityReason.VerificationFailed,
                LuaJitBreakEvenClass.Unfavorable,
                DiagnosticCode: "JIT1005",
                CanonicalInstructionCount: 0,
                BackedgeCount: 0,
                DirectCanonicalInstructionCount: 0,
                SlowPathCanonicalInstructionCount: 0,
                SchedulerBoundaryCount: 0,
                SemanticRiskCount: 0,
                PlanInstructionCount: 0,
                EstimatedCodeBytes: 0);
        }

        var function = module.Functions[functionId];
        var plan = planning.Plan!;
        var instructionCount = plan.CanonicalInstructionCount;
        var estimatedCodeBytes = checked(plan.Instructions.Length * 8L);
        var backedgeCount = CountBackedges(function);
        var schedulerBoundaryCount = plan.SlowPathCanonicalInstructionCount;
        var semanticRiskCount = 0;
        foreach (var programCounter in plan.SlowPathCanonicalProgramCounters)
        {
            var effects = function.Instructions[programCounter].Effects;
            if ((effects & (LuaIrInstructionEffects.MayCall |
                    LuaIrInstructionEffects.MayYield)) != 0)
            {
                semanticRiskCount++;
            }
        }

        foreach (var instruction in function.Instructions)
        {
            if (instruction.Opcode == LuaIrOpcode.Return)
            {
                schedulerBoundaryCount++;
            }

            if (instruction.Opcode == LuaIrOpcode.Close)
            {
                semanticRiskCount++;
            }
        }

        if (estimatedCodeBytes > MaximumEstimatedCodeBytes)
        {
            return Result(
                isCompilable: false,
                LuaJitEligibilityReason.EstimatedCodeSizeTooLarge,
                "JIT1006");
        }

        if (backedgeCount == 0 && instructionCount < MinimumRepeatedInstructionCount)
        {
            return Result(
                isCompilable: true,
                LuaJitEligibilityReason.NoRepeatedWork,
                "JIT1101");
        }

        var directCoverage = Ratio(plan.DirectCanonicalInstructionCount, instructionCount);
        if (directCoverage < MinimumDirectCoverage)
        {
            return Result(
                isCompilable: true,
                LuaJitEligibilityReason.DirectCoverageTooLow,
                "JIT1102");
        }

        var slowPathDensity = Ratio(plan.SlowPathCanonicalInstructionCount, instructionCount);
        if (slowPathDensity > MaximumSlowPathDensity)
        {
            return Result(
                isCompilable: true,
                LuaJitEligibilityReason.SlowPathDensityTooHigh,
                "JIT1103");
        }

        var semanticBoundaryDensity = Ratio(semanticRiskCount, instructionCount);
        if (semanticBoundaryDensity > MaximumSemanticBoundaryDensity)
        {
            return Result(
                isCompilable: true,
                LuaJitEligibilityReason.SemanticBoundaryDensityTooHigh,
                "JIT1104");
        }

        return new LuaJitFunctionEligibility(
            IsCompilable: true,
            IsAutoEligible: true,
            LuaJitEligibilityReason.Eligible,
            backedgeCount > 0 && directCoverage >= 0.90 && slowPathDensity <= 0.05
                ? LuaJitBreakEvenClass.WithinCurrentInvocation
                : backedgeCount > 0
                    ? LuaJitBreakEvenClass.RepeatedInvocation
                    : LuaJitBreakEvenClass.HighReuse,
            DiagnosticCode: null,
            instructionCount,
            backedgeCount,
            plan.DirectCanonicalInstructionCount,
            plan.SlowPathCanonicalInstructionCount,
            schedulerBoundaryCount,
            semanticRiskCount,
            plan.Instructions.Length,
            estimatedCodeBytes);

        LuaJitFunctionEligibility Result(
            bool isCompilable,
            LuaJitEligibilityReason reason,
            string diagnosticCode) => new(
                isCompilable,
                IsAutoEligible: false,
                reason,
                LuaJitBreakEvenClass.Unfavorable,
                diagnosticCode,
                instructionCount,
                backedgeCount,
                plan.DirectCanonicalInstructionCount,
                plan.SlowPathCanonicalInstructionCount,
                schedulerBoundaryCount,
                semanticRiskCount,
                plan.Instructions.Length,
                estimatedCodeBytes);
    }

    private static int CountBackedges(LuaIrFunction function)
    {
        var count = 0;
        foreach (var block in function.BasicBlocks)
        {
            foreach (var successor in block.Successors)
            {
                if (successor <= block.Start)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static double Ratio(int numerator, int denominator) => denominator == 0
        ? 0
        : (double)numerator / denominator;
}
