using Lunil.CodeGen.Cil.Planning;
using Lunil.CodeGen.Cil.Verification;

namespace Lunil.CodeGen.Cil.Emission;

public enum CilEmitterFlavor : byte
{
    ReflectionEmit,
}

/// <summary>
/// Common instruction sink used by the runtime Reflection.Emit encoder. Tokens and branch widths
/// are resolved by the sink; canonical lowering is performed only once.
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
        CilPlanLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sink);
        cancellationToken.ThrowIfCancellationRequested();
        var verification = CilMethodPlanVerifier.Verify(plan, limits, cancellationToken);
        if (!verification.Succeeded)
        {
            return verification;
        }

        EmitVerified(plan, sink, verification, cancellationToken);
        return verification;
    }

    public static void EmitVerified(
        CilMethodPlan plan,
        ICilInstructionSink sink,
        CilPlanVerificationResult verification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(verification);
        cancellationToken.ThrowIfCancellationRequested();
        if (!verification.Succeeded)
        {
            throw new ArgumentException(
                "A failed CIL plan verification cannot be emitted.",
                nameof(verification));
        }

        sink.BeginMethod(plan, verification.MaximumEvaluationStack);
        var localIndex = 0;
        foreach (var local in plan.Locals.OrderBy(static local => local.Index))
        {
            if ((localIndex++ & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            sink.DeclareLocal(local);
        }

        var instructionIndex = 0;
        foreach (var instruction in plan.Instructions)
        {
            if ((instructionIndex++ & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            sink.Emit(instruction);
        }

        cancellationToken.ThrowIfCancellationRequested();
        sink.EndMethod();
    }
}
