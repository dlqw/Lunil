using Lunil.CodeGen.Cil.Planning;
using Lunil.CodeGen.Cil.Verification;

namespace Lunil.CodeGen.Cil.Emission;

public enum CilEmitterFlavor : byte
{
    ReflectionEmit,
    Metadata,
}

/// <summary>
/// Common instruction sink implemented by the Reflection.Emit and metadata encoders. Tokens and
/// branch widths are resolved by the sink; canonical lowering is performed only once.
/// </summary>
public interface ICilInstructionSink
{
    CilEmitterFlavor Flavor { get; }

    void BeginMethod(CilMethodPlan plan, int maximumEvaluationStack);

    void DeclareLocal(CilLocal local);

    void Emit(CilPlanInstruction instruction);

    void EndMethod();
}

public static class CilPlanEmitter
{
    public static CilPlanVerificationResult Emit(
        CilMethodPlan plan,
        ICilInstructionSink sink,
        CilPlanLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sink);
        var verification = CilMethodPlanVerifier.Verify(plan, limits);
        if (!verification.Succeeded)
        {
            return verification;
        }

        sink.BeginMethod(plan, verification.MaximumEvaluationStack);
        foreach (var local in plan.Locals.OrderBy(static local => local.Index))
        {
            sink.DeclareLocal(local);
        }

        foreach (var instruction in plan.Instructions)
        {
            sink.Emit(instruction);
        }

        sink.EndMethod();
        return verification;
    }
}
