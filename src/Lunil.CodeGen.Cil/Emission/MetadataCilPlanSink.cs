using System.Collections.Immutable;
using Lunil.CodeGen.Cil.Planning;
using Lunil.CodeGen.Cil.Verification;

namespace Lunil.CodeGen.Cil.Emission;

public sealed record MetadataCilRecipe(
    string MethodName,
    int MaximumEvaluationStack,
    ImmutableArray<CilLocal> Locals,
    ImmutableArray<CilPlanInstruction> Instructions);

/// <summary>
/// Deterministic metadata-emitter input. M3 resolves this recipe to ECMA-335 tokens, branch
/// widths, method bodies, and Portable PDB records without rerunning canonical lowering.
/// </summary>
public sealed class MetadataCilPlanSink : ICilInstructionSink
{
    private readonly ImmutableArray<CilLocal>.Builder _locals = ImmutableArray.CreateBuilder<CilLocal>();
    private readonly ImmutableArray<CilPlanInstruction>.Builder _instructions =
        ImmutableArray.CreateBuilder<CilPlanInstruction>();
    private string? _methodName;
    private int _maximumEvaluationStack;

    public CilEmitterFlavor Flavor => CilEmitterFlavor.Metadata;

    public MetadataCilRecipe? Recipe { get; private set; }

    public static (MetadataCilRecipe? Recipe, CilPlanVerificationResult Verification) CreateRecipe(
        CilMethodPlan plan,
        CilPlanLimits? limits = null)
    {
        var sink = new MetadataCilPlanSink();
        var verification = CilPlanEmitter.Emit(plan, sink, limits);
        return (verification.Succeeded ? sink.Recipe : null, verification);
    }

    public void BeginMethod(CilMethodPlan plan, int maximumEvaluationStack)
    {
        _methodName = plan.Name;
        _maximumEvaluationStack = maximumEvaluationStack;
    }

    public void DeclareLocal(CilLocal local) => _locals.Add(local);

    public void Emit(CilPlanInstruction instruction) => _instructions.Add(instruction);

    public void EndMethod()
    {
        Recipe = new MetadataCilRecipe(
            _methodName ?? throw new InvalidOperationException("CIL method was not initialized."),
            _maximumEvaluationStack,
            _locals.ToImmutable(),
            _instructions.ToImmutable());
    }
}
