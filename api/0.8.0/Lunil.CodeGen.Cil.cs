// Target Frameworks: net10.0
#nullable enable

namespace Lunil.CodeGen.Cil
{
    public static class LuaCilCodeGenerator
    {
        public static Lunil.CodeGen.Cil.LuaCilPlanningResult PlanFunction(Lunil.IR.Canonical.LuaIrModule module, int functionId, Lunil.CodeGen.Cil.Planning.CilPlanLimits? limits = null, bool includeInstructionObservation = true, System.Threading.CancellationToken cancellationToken = null) => throw null;
    }

    public readonly struct LuaCilPlanningMetrics : System.IEquatable<Lunil.CodeGen.Cil.LuaCilPlanningMetrics>
    {
        public System.TimeSpan CanonicalVerificationDuration { get => throw null; init { } }
        public System.TimeSpan ControlFlowAnalysisDuration { get => throw null; init { } }
        public System.TimeSpan MethodPlanBuildDuration { get => throw null; init { } }
        public System.TimeSpan PlanVerificationDuration { get => throw null; init { } }
        public LuaCilPlanningMetrics(System.TimeSpan CanonicalVerificationDuration, System.TimeSpan ControlFlowAnalysisDuration, System.TimeSpan MethodPlanBuildDuration, System.TimeSpan PlanVerificationDuration) { }
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.CodeGen.Cil.LuaCilPlanningMetrics left, Lunil.CodeGen.Cil.LuaCilPlanningMetrics right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.LuaCilPlanningMetrics left, Lunil.CodeGen.Cil.LuaCilPlanningMetrics right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.CodeGen.Cil.LuaCilPlanningMetrics other) => throw null;
        public void Deconstruct(out System.TimeSpan CanonicalVerificationDuration, out System.TimeSpan ControlFlowAnalysisDuration, out System.TimeSpan MethodPlanBuildDuration, out System.TimeSpan PlanVerificationDuration) => throw null;
    }

    public sealed class LuaCilPlanningResult : System.IEquatable<Lunil.CodeGen.Cil.LuaCilPlanningResult>
    {
        public Lunil.CodeGen.Cil.Planning.CilMethodPlan? Plan { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Planning.CilPlanVerificationResult? Verification { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilPlanDiagnostic> Diagnostics { get => throw null; init { } }
        public bool Succeeded { get => throw null; }
        public Lunil.CodeGen.Cil.LuaCilPlanningMetrics Metrics { get => throw null; init { } }
        public LuaCilPlanningResult(Lunil.CodeGen.Cil.Planning.CilMethodPlan? Plan, Lunil.CodeGen.Cil.Planning.CilPlanVerificationResult? Verification, System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilPlanDiagnostic> Diagnostics) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.LuaCilPlanningResult? left, Lunil.CodeGen.Cil.LuaCilPlanningResult? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.LuaCilPlanningResult? left, Lunil.CodeGen.Cil.LuaCilPlanningResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.LuaCilPlanningResult? other) => throw null;
        public void Deconstruct(out Lunil.CodeGen.Cil.Planning.CilMethodPlan? Plan, out Lunil.CodeGen.Cil.Planning.CilPlanVerificationResult? Verification, out System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilPlanDiagnostic> Diagnostics) => throw null;
    }
}
namespace Lunil.CodeGen.Cil.Analysis
{
    public sealed class CilBlockLayout : System.IEquatable<Lunil.CodeGen.Cil.Analysis.CilBlockLayout>
    {
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilCanonicalBlock> Blocks { get => throw null; init { } }
        public CilBlockLayout(System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilCanonicalBlock> Blocks) { }
        public static Lunil.CodeGen.Cil.Analysis.CilBlockLayout Build(Lunil.IR.Canonical.LuaIrFunction function, System.Threading.CancellationToken cancellationToken = null) => throw null;
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Analysis.CilBlockLayout? left, Lunil.CodeGen.Cil.Analysis.CilBlockLayout? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Analysis.CilBlockLayout? left, Lunil.CodeGen.Cil.Analysis.CilBlockLayout? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Analysis.CilBlockLayout? other) => throw null;
        public void Deconstruct(out System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilCanonicalBlock> Blocks) => throw null;
    }

    public static class LuaRegisterLiveness
    {
        public static Lunil.CodeGen.Cil.Analysis.LuaRegisterLivenessResult Analyze(Lunil.IR.Canonical.LuaIrModule module, Lunil.IR.Canonical.LuaIrFunction function, System.Threading.CancellationToken cancellationToken = null) => throw null;
    }

    public sealed class LuaRegisterLivenessResult : System.IEquatable<Lunil.CodeGen.Cil.Analysis.LuaRegisterLivenessResult>
    {
        public System.Collections.Immutable.ImmutableArray<System.Collections.Immutable.ImmutableArray<int>> LiveBefore { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<System.Collections.Immutable.ImmutableArray<int>> LiveAfter { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilGcMap> GcMaps { get => throw null; init { } }
        public LuaRegisterLivenessResult(System.Collections.Immutable.ImmutableArray<System.Collections.Immutable.ImmutableArray<int>> LiveBefore, System.Collections.Immutable.ImmutableArray<System.Collections.Immutable.ImmutableArray<int>> LiveAfter, System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilGcMap> GcMaps) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Analysis.LuaRegisterLivenessResult? left, Lunil.CodeGen.Cil.Analysis.LuaRegisterLivenessResult? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Analysis.LuaRegisterLivenessResult? left, Lunil.CodeGen.Cil.Analysis.LuaRegisterLivenessResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Analysis.LuaRegisterLivenessResult? other) => throw null;
        public void Deconstruct(out System.Collections.Immutable.ImmutableArray<System.Collections.Immutable.ImmutableArray<int>> LiveBefore, out System.Collections.Immutable.ImmutableArray<System.Collections.Immutable.ImmutableArray<int>> LiveAfter, out System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilGcMap> GcMaps) => throw null;
    }
}
namespace Lunil.CodeGen.Cil.Emission
{
    public enum CilEmitterFlavor
    {
        ReflectionEmit = 0
    }

    public static class CilPlanEmitter
    {
        public static Lunil.CodeGen.Cil.Planning.CilPlanVerificationResult Emit(Lunil.CodeGen.Cil.Planning.CilMethodPlan plan, Lunil.CodeGen.Cil.Emission.ICilInstructionSink sink, Lunil.CodeGen.Cil.Planning.CilPlanLimits? limits = null, System.Threading.CancellationToken cancellationToken = null) => throw null;
        public static void EmitVerified(Lunil.CodeGen.Cil.Planning.CilMethodPlan plan, Lunil.CodeGen.Cil.Emission.ICilInstructionSink sink, Lunil.CodeGen.Cil.Planning.CilPlanVerificationResult verification, System.Threading.CancellationToken cancellationToken = null) { }
    }

    public interface ICilInstructionSink
    {
        Lunil.CodeGen.Cil.Emission.CilEmitterFlavor Flavor { get; }
        void BeginMethod(Lunil.CodeGen.Cil.Planning.CilMethodPlan plan, int maximumEvaluationStack);
        void DeclareLocal(Lunil.CodeGen.Cil.Planning.CilLocal local);
        void Emit(Lunil.CodeGen.Cil.Planning.CilPlanInstruction instruction);
        void EndMethod();
    }

    public delegate Lunil.Runtime.CodeGen.LuaCompiledExit LuaCompiledMethod(Lunil.Runtime.CodeGen.LuaExecutionContext context, Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame);

    public sealed class ReflectionEmitCilPlanSink : Lunil.CodeGen.Cil.Emission.ICilInstructionSink
    {
        public System.TimeSpan EmissionDuration { get => throw null; }
        public System.TimeSpan DelegateCreationDuration { get => throw null; }
        public Lunil.CodeGen.Cil.Emission.CilEmitterFlavor Flavor { get => throw null; }
        public Lunil.CodeGen.Cil.Emission.LuaCompiledMethod? CompiledMethod { get => throw null; }
        [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Reflection.Emit requires dynamic code support.")]
        public static Lunil.CodeGen.Cil.Emission.ReflectionEmitResult Compile(Lunil.CodeGen.Cil.Planning.CilMethodPlan plan, Lunil.CodeGen.Cil.Planning.CilPlanLimits? limits = null, System.Threading.CancellationToken cancellationToken = null) => throw null;
        [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Reflection.Emit requires dynamic code support.")]
        public static Lunil.CodeGen.Cil.Emission.ReflectionEmitResult Compile(Lunil.CodeGen.Cil.Planning.CilMethodPlan plan, Lunil.CodeGen.Cil.Planning.CilPlanVerificationResult verification, System.Threading.CancellationToken cancellationToken = null) => throw null;
        [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050", Justification = "ReflectionEmitCilPlanSink is only reached after the dynamic-code capability check.")]
        public void BeginMethod(Lunil.CodeGen.Cil.Planning.CilMethodPlan plan, int maximumEvaluationStack) { }
        public void DeclareLocal(Lunil.CodeGen.Cil.Planning.CilLocal local) { }
        public void Emit(Lunil.CodeGen.Cil.Planning.CilPlanInstruction instruction) { }
        public void EndMethod() { }
    }

    public readonly struct ReflectionEmitMetrics : System.IEquatable<Lunil.CodeGen.Cil.Emission.ReflectionEmitMetrics>
    {
        public System.TimeSpan PlanVerificationDuration { get => throw null; init { } }
        public System.TimeSpan EmissionDuration { get => throw null; init { } }
        public System.TimeSpan DelegateCreationDuration { get => throw null; init { } }
        public ReflectionEmitMetrics(System.TimeSpan PlanVerificationDuration, System.TimeSpan EmissionDuration, System.TimeSpan DelegateCreationDuration) { }
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.CodeGen.Cil.Emission.ReflectionEmitMetrics left, Lunil.CodeGen.Cil.Emission.ReflectionEmitMetrics right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Emission.ReflectionEmitMetrics left, Lunil.CodeGen.Cil.Emission.ReflectionEmitMetrics right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.CodeGen.Cil.Emission.ReflectionEmitMetrics other) => throw null;
        public void Deconstruct(out System.TimeSpan PlanVerificationDuration, out System.TimeSpan EmissionDuration, out System.TimeSpan DelegateCreationDuration) => throw null;
    }

    public sealed class ReflectionEmitResult : System.IEquatable<Lunil.CodeGen.Cil.Emission.ReflectionEmitResult>
    {
        public Lunil.CodeGen.Cil.Emission.LuaCompiledMethod? Method { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilPlanDiagnostic> Diagnostics { get => throw null; init { } }
        public int MaximumEvaluationStack { get => throw null; init { } }
        public bool Succeeded { get => throw null; }
        public Lunil.CodeGen.Cil.Emission.ReflectionEmitMetrics Metrics { get => throw null; init { } }
        public ReflectionEmitResult(Lunil.CodeGen.Cil.Emission.LuaCompiledMethod? Method, System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilPlanDiagnostic> Diagnostics, int MaximumEvaluationStack) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Emission.ReflectionEmitResult? left, Lunil.CodeGen.Cil.Emission.ReflectionEmitResult? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Emission.ReflectionEmitResult? left, Lunil.CodeGen.Cil.Emission.ReflectionEmitResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Emission.ReflectionEmitResult? other) => throw null;
        public void Deconstruct(out Lunil.CodeGen.Cil.Emission.LuaCompiledMethod? Method, out System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilPlanDiagnostic> Diagnostics, out int MaximumEvaluationStack) => throw null;
    }
}
namespace Lunil.CodeGen.Cil.Jit
{
    public enum LuaJitBreakEvenClass
    {
        Unfavorable = 0,
        WithinCurrentInvocation = 1,
        RepeatedInvocation = 2,
        HighReuse = 3
    }

    public enum LuaJitCallTargetKind
    {
        Lua = 0,
        Native = 1,
        Unknown = 2
    }

    public sealed class LuaJitCallTargetProfile : System.IEquatable<Lunil.CodeGen.Cil.Jit.LuaJitCallTargetProfile>
    {
        public Lunil.CodeGen.Cil.Jit.LuaJitCallTargetKind Kind { get => throw null; init { } }
        public string ModuleContentId { get => throw null; init { } }
        public int FunctionId { get => throw null; init { } }
        public string NativeName { get => throw null; init { } }
        public long Samples { get => throw null; init { } }
        public LuaJitCallTargetProfile(Lunil.CodeGen.Cil.Jit.LuaJitCallTargetKind Kind, string ModuleContentId, int FunctionId, string NativeName, long Samples) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Jit.LuaJitCallTargetProfile? left, Lunil.CodeGen.Cil.Jit.LuaJitCallTargetProfile? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Jit.LuaJitCallTargetProfile? left, Lunil.CodeGen.Cil.Jit.LuaJitCallTargetProfile? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Jit.LuaJitCallTargetProfile? other) => throw null;
        public void Deconstruct(out Lunil.CodeGen.Cil.Jit.LuaJitCallTargetKind Kind, out string ModuleContentId, out int FunctionId, out string NativeName, out long Samples) => throw null;
    }

    public readonly struct LuaJitCompilationMetrics : System.IEquatable<Lunil.CodeGen.Cil.Jit.LuaJitCompilationMetrics>
    {
        public System.TimeSpan CanonicalVerificationDuration { get => throw null; init { } }
        public System.TimeSpan ControlFlowAnalysisDuration { get => throw null; init { } }
        public System.TimeSpan MethodPlanBuildDuration { get => throw null; init { } }
        public System.TimeSpan PlanVerificationDuration { get => throw null; init { } }
        public System.TimeSpan ReflectionEmitDuration { get => throw null; init { } }
        public System.TimeSpan DelegateCreationDuration { get => throw null; init { } }
        public long AllocatedBytes { get => throw null; init { } }
        public int CanonicalInstructionCount { get => throw null; init { } }
        public int DirectCanonicalInstructionCount { get => throw null; init { } }
        public int SlowPathCanonicalInstructionCount { get => throw null; init { } }
        public int PlanInstructionCount { get => throw null; init { } }
        public long EstimatedCodeBytes { get => throw null; init { } }
        public LuaJitCompilationMetrics(System.TimeSpan CanonicalVerificationDuration, System.TimeSpan ControlFlowAnalysisDuration, System.TimeSpan MethodPlanBuildDuration, System.TimeSpan PlanVerificationDuration, System.TimeSpan ReflectionEmitDuration, System.TimeSpan DelegateCreationDuration, long AllocatedBytes, int CanonicalInstructionCount, int DirectCanonicalInstructionCount, int SlowPathCanonicalInstructionCount, int PlanInstructionCount, long EstimatedCodeBytes) { }
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.CodeGen.Cil.Jit.LuaJitCompilationMetrics left, Lunil.CodeGen.Cil.Jit.LuaJitCompilationMetrics right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Jit.LuaJitCompilationMetrics left, Lunil.CodeGen.Cil.Jit.LuaJitCompilationMetrics right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.CodeGen.Cil.Jit.LuaJitCompilationMetrics other) => throw null;
        public void Deconstruct(out System.TimeSpan CanonicalVerificationDuration, out System.TimeSpan ControlFlowAnalysisDuration, out System.TimeSpan MethodPlanBuildDuration, out System.TimeSpan PlanVerificationDuration, out System.TimeSpan ReflectionEmitDuration, out System.TimeSpan DelegateCreationDuration, out long AllocatedBytes, out int CanonicalInstructionCount, out int DirectCanonicalInstructionCount, out int SlowPathCanonicalInstructionCount, out int PlanInstructionCount, out long EstimatedCodeBytes) => throw null;
    }

    public enum LuaJitCompilationTier
    {
        Interpreter = 0,
        Tier1 = 1,
        Tier2 = 2,
        LoopOsr = 3
    }

    public sealed class LuaJitDeoptMapEntry : System.IEquatable<Lunil.CodeGen.Cil.Jit.LuaJitDeoptMapEntry>
    {
        public int ProgramCounter { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<int> MaterializedRegisters { get => throw null; init { } }
        public bool FrameTopMaterialized { get => throw null; init { } }
        public bool PendingTransformMaterialized { get => throw null; init { } }
        public LuaJitDeoptMapEntry(int ProgramCounter, System.Collections.Immutable.ImmutableArray<int> MaterializedRegisters, bool FrameTopMaterialized, bool PendingTransformMaterialized) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Jit.LuaJitDeoptMapEntry? left, Lunil.CodeGen.Cil.Jit.LuaJitDeoptMapEntry? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Jit.LuaJitDeoptMapEntry? left, Lunil.CodeGen.Cil.Jit.LuaJitDeoptMapEntry? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Jit.LuaJitDeoptMapEntry? other) => throw null;
        public void Deconstruct(out int ProgramCounter, out System.Collections.Immutable.ImmutableArray<int> MaterializedRegisters, out bool FrameTopMaterialized, out bool PendingTransformMaterialized) => throw null;
    }

    public enum LuaJitEligibilityReason
    {
        Eligible = 0,
        VerificationFailed = 1,
        EstimatedCodeSizeTooLarge = 2,
        NoRepeatedWork = 3,
        DirectCoverageTooLow = 4,
        SlowPathDensityTooHigh = 5,
        SemanticBoundaryDensityTooHigh = 6
    }

    public sealed class LuaJitEvent : System.IEquatable<Lunil.CodeGen.Cil.Jit.LuaJitEvent>
    {
        public Lunil.CodeGen.Cil.Jit.LuaJitEventKind Kind { get => throw null; init { } }
        public string ModuleContentId { get => throw null; init { } }
        public int FunctionId { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Jit.LuaJitFunctionState State { get => throw null; init { } }
        public long EstimatedCodeBytes { get => throw null; init { } }
        public System.TimeSpan Duration { get => throw null; init { } }
        public string? DiagnosticCode { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Jit.LuaJitCompilationTier Tier { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Jit.LuaJitCompilationMetrics? CompilationMetrics { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Jit.LuaJitFunctionEligibility? Eligibility { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Jit.LuaJitTier2CompilationMetrics? Tier2CompilationMetrics { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Jit.LuaJitTier2Eligibility? Tier2Eligibility { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrCompilationMetrics? LoopOsrCompilationMetrics { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrEligibility? LoopOsrEligibility { get => throw null; init { } }
        public LuaJitEvent(Lunil.CodeGen.Cil.Jit.LuaJitEventKind Kind, string ModuleContentId, int FunctionId, Lunil.CodeGen.Cil.Jit.LuaJitFunctionState State, long EstimatedCodeBytes = 0, System.TimeSpan Duration = null, string? DiagnosticCode = null, Lunil.CodeGen.Cil.Jit.LuaJitCompilationTier Tier = 1, Lunil.CodeGen.Cil.Jit.LuaJitCompilationMetrics? CompilationMetrics = null, Lunil.CodeGen.Cil.Jit.LuaJitFunctionEligibility? Eligibility = null, Lunil.CodeGen.Cil.Jit.LuaJitTier2CompilationMetrics? Tier2CompilationMetrics = null, Lunil.CodeGen.Cil.Jit.LuaJitTier2Eligibility? Tier2Eligibility = null, Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrCompilationMetrics? LoopOsrCompilationMetrics = null, Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrEligibility? LoopOsrEligibility = null) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Jit.LuaJitEvent? left, Lunil.CodeGen.Cil.Jit.LuaJitEvent? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Jit.LuaJitEvent? left, Lunil.CodeGen.Cil.Jit.LuaJitEvent? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Jit.LuaJitEvent? other) => throw null;
        public void Deconstruct(out Lunil.CodeGen.Cil.Jit.LuaJitEventKind Kind, out string ModuleContentId, out int FunctionId, out Lunil.CodeGen.Cil.Jit.LuaJitFunctionState State, out long EstimatedCodeBytes, out System.TimeSpan Duration, out string? DiagnosticCode, out Lunil.CodeGen.Cil.Jit.LuaJitCompilationTier Tier, out Lunil.CodeGen.Cil.Jit.LuaJitCompilationMetrics? CompilationMetrics, out Lunil.CodeGen.Cil.Jit.LuaJitFunctionEligibility? Eligibility, out Lunil.CodeGen.Cil.Jit.LuaJitTier2CompilationMetrics? Tier2CompilationMetrics, out Lunil.CodeGen.Cil.Jit.LuaJitTier2Eligibility? Tier2Eligibility, out Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrCompilationMetrics? LoopOsrCompilationMetrics, out Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrEligibility? LoopOsrEligibility) => throw null;
    }

    public enum LuaJitEventKind
    {
        Queued = 0,
        CompilationStarted = 1,
        CompilationCompleted = 2,
        CompilationFailed = 3,
        Fallback = 4,
        Deoptimized = 5,
        Evicted = 6,
        Invalidated = 7,
        Tier2Queued = 8,
        Tier2CompilationStarted = 9,
        Tier2CompilationCompleted = 10,
        Tier2CompilationFailed = 11,
        Tier2GuardFailed = 12,
        Tier2Invalidated = 13,
        LoopOsrQueued = 14,
        LoopOsrCompilationStarted = 15,
        LoopOsrCompilationCompleted = 16,
        LoopOsrCompilationFailed = 17,
        LoopOsrEntered = 18,
        LoopOsrExited = 19,
        LoopOsrGuardFailed = 20,
        LoopOsrInvalidated = 21,
        EligibilityAccepted = 22,
        EligibilityRejected = 23,
        Tier2EligibilityAccepted = 24,
        Tier2EligibilityRejected = 25,
        LoopOsrEligibilityAccepted = 26,
        LoopOsrEligibilityRejected = 27,
        LoopOsrCompilerPrepared = 28
    }

    public sealed class LuaJitException : System.Exception
    {
        public string DiagnosticCode { get => throw null; }
        public LuaJitException(string diagnosticCode, string message) { }
    }

    public sealed class LuaJitExecutor : System.IDisposable
    {
        public Lunil.CodeGen.Cil.Jit.LuaJitExecutorOptions Options { get => throw null; }
        public bool IsDynamicCodeAvailable { get => throw null; }
        public Lunil.CodeGen.Cil.Jit.LuaJitStatistics Statistics { get => throw null; }
        public event System.EventHandler<Lunil.CodeGen.Cil.Jit.LuaJitEvent>? EventOccurred;
        public LuaJitExecutor(Lunil.CodeGen.Cil.Jit.LuaJitExecutorOptions? options = null) { }
        public static Lunil.CodeGen.Cil.Jit.LuaJitFunctionEligibility EvaluateFunctionEligibility(Lunil.IR.Canonical.LuaIrModule module, int functionId, bool includeInstructionObservation = false) => throw null;
        public static Lunil.CodeGen.Cil.Jit.LuaJitTier2Eligibility EvaluateTier2PromotionEligibility(Lunil.IR.Canonical.LuaIrModule module, int functionId, Lunil.CodeGen.Cil.Jit.LuaJitFunctionProfile profile) => throw null;
        public static Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrEligibility EvaluateLoopOsrEligibility(Lunil.IR.Canonical.LuaIrModule module, Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrPlan plan) => throw null;
        public Lunil.Runtime.Execution.LuaExecutionResult Execute(Lunil.Runtime.LuaState state, Lunil.Runtime.Execution.LuaClosure closure, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments = null) => throw null;
        public Lunil.Runtime.Execution.LuaExecutionResult ExecuteBinaryChunk(Lunil.Runtime.LuaState state, System.ReadOnlySpan<byte> binaryChunk, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments = null, Lunil.IR.Lua54.Lua54ChunkReaderOptions? readerOptions = null) => throw null;
        public Lunil.Runtime.Execution.LuaExecutionResult Start(Lunil.Runtime.LuaState state, Lunil.Runtime.Execution.LuaThread thread, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments = null) => throw null;
        public Lunil.Runtime.Execution.LuaExecutionResult Resume(Lunil.Runtime.LuaState state, Lunil.Runtime.Execution.LuaThread thread, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments = null) => throw null;
        public Lunil.Runtime.Execution.LuaExecutionResult Close(Lunil.Runtime.LuaState state, Lunil.Runtime.Execution.LuaThread thread) => throw null;
        public Lunil.CodeGen.Cil.Jit.LuaJitFunctionState GetFunctionState(Lunil.IR.Canonical.LuaIrModule module, int functionId) => throw null;
        public Lunil.CodeGen.Cil.Jit.LuaJitFunctionEligibility GetFunctionEligibility(Lunil.IR.Canonical.LuaIrModule module, int functionId) => throw null;
        public Lunil.CodeGen.Cil.Jit.LuaJitFunctionProfile GetFunctionProfile(Lunil.IR.Canonical.LuaIrModule module, int functionId) => throw null;
        public Lunil.CodeGen.Cil.Jit.LuaJitTier2Eligibility GetTier2PromotionEligibility(Lunil.IR.Canonical.LuaIrModule module, int functionId) => throw null;
        public byte[] ExportProfile(Lunil.IR.Canonical.LuaIrModule module) => throw null;
        public Lunil.CodeGen.Cil.Jit.LuaJitProfileImportResult ImportProfile(Lunil.IR.Canonical.LuaIrModule module, System.ReadOnlySpan<byte> payload) => throw null;
        public Lunil.CodeGen.Cil.Jit.LuaJitCompilationTier GetFunctionTier(Lunil.IR.Canonical.LuaIrModule module, int functionId) => throw null;
        public Lunil.CodeGen.Cil.Jit.LuaJitTier2State GetTier2State(Lunil.IR.Canonical.LuaIrModule module, int functionId) => throw null;
        public Lunil.CodeGen.Cil.Jit.LuaJitTier2Plan? GetTier2Plan(Lunil.IR.Canonical.LuaIrModule module, int functionId) => throw null;
        public System.Collections.Generic.IReadOnlyList<Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrPlan> GetLoopOsrPlans(Lunil.IR.Canonical.LuaIrModule module, int functionId) => throw null;
        public Lunil.CodeGen.Cil.Jit.LuaJitOsrState GetLoopOsrState(Lunil.IR.Canonical.LuaIrModule module, int functionId, int headerProgramCounter, int backedgeProgramCounter) => throw null;
        public Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrEligibility GetLoopOsrEligibility(Lunil.IR.Canonical.LuaIrModule module, int functionId, int headerProgramCounter, int backedgeProgramCounter) => throw null;
        public void Invalidate(Lunil.IR.Canonical.LuaIrModule module) { }
        public void ClearCache() { }
        public System.Threading.Tasks.Task WaitForIdleAsync(System.Threading.CancellationToken cancellationToken = null) => throw null;
        public void Dispose() { }
    }

    public sealed class LuaJitExecutorOptions : System.IEquatable<Lunil.CodeGen.Cil.Jit.LuaJitExecutorOptions>
    {
        public static Lunil.CodeGen.Cil.Jit.LuaJitExecutorOptions Default { get => throw null; }
        public Lunil.CodeGen.Cil.Jit.LuaJitPolicy Policy { get => throw null; init { } }
        public int FunctionEntryThreshold { get => throw null; init { } }
        public int BackedgeThreshold { get => throw null; init { } }
        public bool SynchronousCompilation { get => throw null; init { } }
        public int CompilationQueueCapacity { get => throw null; init { } }
        public int MaximumConcurrentCompilations { get => throw null; init { } }
        public int MaximumCompilationAttempts { get => throw null; init { } }
        public int MaximumPolymorphicShapes { get => throw null; init { } }
        public bool EnableTier2 { get => throw null; init { } }
        public bool EnableTier2ManagedFallback { get => throw null; init { } }
        public int Tier2InvocationThreshold { get => throw null; init { } }
        public int Tier2BackedgeThreshold { get => throw null; init { } }
        public int MaximumTier2GuardFailures { get => throw null; init { } }
        public bool EnableLoopOsr { get => throw null; init { } }
        public bool EnableLoopOsrManagedFallback { get => throw null; init { } }
        public int LoopOsrBackedgeThreshold { get => throw null; init { } }
        public int MaximumLoopOsrGuardFailures { get => throw null; init { } }
        public System.TimeSpan CompilationRetryBackoff { get => throw null; init { } }
        public long MaximumCodeCacheBytes { get => throw null; init { } }
        public Lunil.Runtime.Execution.LuaInterpreterOptions Interpreter { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Jit.LuaJitExecutorOptions? left, Lunil.CodeGen.Cil.Jit.LuaJitExecutorOptions? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Jit.LuaJitExecutorOptions? left, Lunil.CodeGen.Cil.Jit.LuaJitExecutorOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Jit.LuaJitExecutorOptions? other) => throw null;
    }

    public sealed class LuaJitFunctionEligibility : System.IEquatable<Lunil.CodeGen.Cil.Jit.LuaJitFunctionEligibility>
    {
        public bool IsCompilable { get => throw null; init { } }
        public bool IsAutoEligible { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Jit.LuaJitEligibilityReason Reason { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Jit.LuaJitBreakEvenClass BreakEvenClass { get => throw null; init { } }
        public string? DiagnosticCode { get => throw null; init { } }
        public int CanonicalInstructionCount { get => throw null; init { } }
        public int BackedgeCount { get => throw null; init { } }
        public int DirectCanonicalInstructionCount { get => throw null; init { } }
        public int SlowPathCanonicalInstructionCount { get => throw null; init { } }
        public int SchedulerBoundaryCount { get => throw null; init { } }
        public int SemanticRiskCount { get => throw null; init { } }
        public int PlanInstructionCount { get => throw null; init { } }
        public long EstimatedCodeBytes { get => throw null; init { } }
        public double DirectCoverage { get => throw null; }
        public double SlowPathDensity { get => throw null; }
        public double SchedulerBoundaryDensity { get => throw null; }
        public LuaJitFunctionEligibility(bool IsCompilable, bool IsAutoEligible, Lunil.CodeGen.Cil.Jit.LuaJitEligibilityReason Reason, Lunil.CodeGen.Cil.Jit.LuaJitBreakEvenClass BreakEvenClass, string? DiagnosticCode, int CanonicalInstructionCount, int BackedgeCount, int DirectCanonicalInstructionCount, int SlowPathCanonicalInstructionCount, int SchedulerBoundaryCount, int SemanticRiskCount, int PlanInstructionCount, long EstimatedCodeBytes) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Jit.LuaJitFunctionEligibility? left, Lunil.CodeGen.Cil.Jit.LuaJitFunctionEligibility? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Jit.LuaJitFunctionEligibility? left, Lunil.CodeGen.Cil.Jit.LuaJitFunctionEligibility? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Jit.LuaJitFunctionEligibility? other) => throw null;
        public void Deconstruct(out bool IsCompilable, out bool IsAutoEligible, out Lunil.CodeGen.Cil.Jit.LuaJitEligibilityReason Reason, out Lunil.CodeGen.Cil.Jit.LuaJitBreakEvenClass BreakEvenClass, out string? DiagnosticCode, out int CanonicalInstructionCount, out int BackedgeCount, out int DirectCanonicalInstructionCount, out int SlowPathCanonicalInstructionCount, out int SchedulerBoundaryCount, out int SemanticRiskCount, out int PlanInstructionCount, out long EstimatedCodeBytes) => throw null;
    }

    public sealed class LuaJitFunctionProfile : System.IEquatable<Lunil.CodeGen.Cil.Jit.LuaJitFunctionProfile>
    {
        public long Samples { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Jit.LuaJitValueKinds> ArgumentKinds { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Jit.LuaJitSiteProfile> Sites { get => throw null; init { } }
        public LuaJitFunctionProfile(long Samples, System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Jit.LuaJitValueKinds> ArgumentKinds, System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Jit.LuaJitSiteProfile> Sites) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Jit.LuaJitFunctionProfile? left, Lunil.CodeGen.Cil.Jit.LuaJitFunctionProfile? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Jit.LuaJitFunctionProfile? left, Lunil.CodeGen.Cil.Jit.LuaJitFunctionProfile? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Jit.LuaJitFunctionProfile? other) => throw null;
        public void Deconstruct(out long Samples, out System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Jit.LuaJitValueKinds> ArgumentKinds, out System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Jit.LuaJitSiteProfile> Sites) => throw null;
    }

    public sealed class LuaJitFunctionProfileEntry : System.IEquatable<Lunil.CodeGen.Cil.Jit.LuaJitFunctionProfileEntry>
    {
        public int FunctionId { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Jit.LuaJitFunctionProfile Profile { get => throw null; init { } }
        public LuaJitFunctionProfileEntry(int FunctionId, Lunil.CodeGen.Cil.Jit.LuaJitFunctionProfile Profile) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Jit.LuaJitFunctionProfileEntry? left, Lunil.CodeGen.Cil.Jit.LuaJitFunctionProfileEntry? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Jit.LuaJitFunctionProfileEntry? left, Lunil.CodeGen.Cil.Jit.LuaJitFunctionProfileEntry? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Jit.LuaJitFunctionProfileEntry? other) => throw null;
        public void Deconstruct(out int FunctionId, out Lunil.CodeGen.Cil.Jit.LuaJitFunctionProfile Profile) => throw null;
    }

    public enum LuaJitFunctionState
    {
        Cold = 0,
        Queued = 1,
        Compiling = 2,
        Ready = 3,
        Failed = 4,
        Invalidated = 5
    }

    public enum LuaJitLoopOsrCodeKind
    {
        ManagedCanonicalProgram = 0,
        GuardedExactNumericCil = 1
    }

    public readonly struct LuaJitLoopOsrCompilationMetrics : System.IEquatable<Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrCompilationMetrics>
    {
        public System.TimeSpan CanonicalVerificationDuration { get => throw null; init { } }
        public System.TimeSpan LoopAnalysisDuration { get => throw null; init { } }
        public bool LivenessCacheHit { get => throw null; init { } }
        public System.TimeSpan SpecializationPlanningDuration { get => throw null; init { } }
        public System.TimeSpan CilEmissionDuration { get => throw null; init { } }
        public System.TimeSpan DelegateCreationDuration { get => throw null; init { } }
        public long AllocatedBytes { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrCodeKind CodeKind { get => throw null; init { } }
        public int LoopInstructionCount { get => throw null; init { } }
        public int SpecializedInstructionCount { get => throw null; init { } }
        public int GuardCount { get => throw null; init { } }
        public long EstimatedCodeBytes { get => throw null; init { } }
        public LuaJitLoopOsrCompilationMetrics(System.TimeSpan CanonicalVerificationDuration, System.TimeSpan LoopAnalysisDuration, bool LivenessCacheHit, System.TimeSpan SpecializationPlanningDuration, System.TimeSpan CilEmissionDuration, System.TimeSpan DelegateCreationDuration, long AllocatedBytes, Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrCodeKind CodeKind, int LoopInstructionCount, int SpecializedInstructionCount, int GuardCount, long EstimatedCodeBytes) { }
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrCompilationMetrics left, Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrCompilationMetrics right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrCompilationMetrics left, Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrCompilationMetrics right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrCompilationMetrics other) => throw null;
        public void Deconstruct(out System.TimeSpan CanonicalVerificationDuration, out System.TimeSpan LoopAnalysisDuration, out bool LivenessCacheHit, out System.TimeSpan SpecializationPlanningDuration, out System.TimeSpan CilEmissionDuration, out System.TimeSpan DelegateCreationDuration, out long AllocatedBytes, out Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrCodeKind CodeKind, out int LoopInstructionCount, out int SpecializedInstructionCount, out int GuardCount, out long EstimatedCodeBytes) => throw null;
    }

    public static class LuaJitLoopOsrDiagnosticCodes
    {
        public const string NoNumericHotspot = "JIT3101";
        public const string ManagedSemanticBoundary = "JIT3102";
        public const string UnsupportedInstruction = "JIT3103";
        public const string UnexpectedCodeKind = "JIT3104";
        public const string NonExactNumericProfile = "JIT3105";
    }

    public sealed class LuaJitLoopOsrEligibility : System.IEquatable<Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrEligibility>
    {
        public bool IsAutoEligible { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrEligibilityReason Reason { get => throw null; init { } }
        public string? DiagnosticCode { get => throw null; init { } }
        public int LoopInstructionCount { get => throw null; init { } }
        public int SpecializedInstructionCount { get => throw null; init { } }
        public int GuardCount { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrCodeKind ExpectedCodeKind { get => throw null; init { } }
        public LuaJitLoopOsrEligibility(bool IsAutoEligible, Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrEligibilityReason Reason, string? DiagnosticCode, int LoopInstructionCount, int SpecializedInstructionCount, int GuardCount, Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrCodeKind ExpectedCodeKind) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrEligibility? left, Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrEligibility? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrEligibility? left, Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrEligibility? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrEligibility? other) => throw null;
        public void Deconstruct(out bool IsAutoEligible, out Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrEligibilityReason Reason, out string? DiagnosticCode, out int LoopInstructionCount, out int SpecializedInstructionCount, out int GuardCount, out Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrCodeKind ExpectedCodeKind) => throw null;
    }

    public enum LuaJitLoopOsrEligibilityReason
    {
        Eligible = 0,
        NoNumericHotspot = 1,
        ManagedSemanticBoundary = 2,
        UnsupportedInstruction = 3,
        AwaitingExactNumericProfile = 4,
        NonExactNumericProfile = 5
    }

    public sealed class LuaJitLoopOsrPlan : System.IEquatable<Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrPlan>
    {
        public int FunctionId { get => throw null; init { } }
        public int HeaderProgramCounter { get => throw null; init { } }
        public int BackedgeProgramCounter { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<int> ProgramCounters { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Jit.LuaJitOsrEntryMap EntryMap { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrCodeKind CodeKind { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Jit.LuaJitOsrRegisterTypeProfile> NumericTypes { get => throw null; init { } }
        public int NumericRegionCount { get => throw null; init { } }
        public int UnboxedNumericLocalCount { get => throw null; init { } }
        public int DirectNumericInstructionCount { get => throw null; init { } }
        public int NumericRegionSafepointCount { get => throw null; init { } }
        public int NumericRegionHotInstructionBudgetCheckCount { get => throw null; init { } }
        public LuaJitLoopOsrPlan(int FunctionId, int HeaderProgramCounter, int BackedgeProgramCounter, System.Collections.Immutable.ImmutableArray<int> ProgramCounters, Lunil.CodeGen.Cil.Jit.LuaJitOsrEntryMap EntryMap) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrPlan? left, Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrPlan? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrPlan? left, Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrPlan? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Jit.LuaJitLoopOsrPlan? other) => throw null;
        public void Deconstruct(out int FunctionId, out int HeaderProgramCounter, out int BackedgeProgramCounter, out System.Collections.Immutable.ImmutableArray<int> ProgramCounters, out Lunil.CodeGen.Cil.Jit.LuaJitOsrEntryMap EntryMap) => throw null;
    }

    public sealed class LuaJitModuleProfile : System.IEquatable<Lunil.CodeGen.Cil.Jit.LuaJitModuleProfile>
    {
        public string ModuleContentId { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Jit.LuaJitFunctionProfileEntry> Functions { get => throw null; init { } }
        public LuaJitModuleProfile(string ModuleContentId, System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Jit.LuaJitFunctionProfileEntry> Functions) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Jit.LuaJitModuleProfile? left, Lunil.CodeGen.Cil.Jit.LuaJitModuleProfile? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Jit.LuaJitModuleProfile? left, Lunil.CodeGen.Cil.Jit.LuaJitModuleProfile? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Jit.LuaJitModuleProfile? other) => throw null;
        public void Deconstruct(out string ModuleContentId, out System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Jit.LuaJitFunctionProfileEntry> Functions) => throw null;
    }

    public sealed class LuaJitOptimization : System.IEquatable<Lunil.CodeGen.Cil.Jit.LuaJitOptimization>
    {
        public int ProgramCounter { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Jit.LuaJitOptimizationKind Kind { get => throw null; init { } }
        public int CanonicalInstructionCount { get => throw null; init { } }
        public string Guard { get => throw null; init { } }
        public LuaJitOptimization(int ProgramCounter, Lunil.CodeGen.Cil.Jit.LuaJitOptimizationKind Kind, int CanonicalInstructionCount, string Guard) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Jit.LuaJitOptimization? left, Lunil.CodeGen.Cil.Jit.LuaJitOptimization? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Jit.LuaJitOptimization? left, Lunil.CodeGen.Cil.Jit.LuaJitOptimization? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Jit.LuaJitOptimization? other) => throw null;
        public void Deconstruct(out int ProgramCounter, out Lunil.CodeGen.Cil.Jit.LuaJitOptimizationKind Kind, out int CanonicalInstructionCount, out string Guard) => throw null;
    }

    public enum LuaJitOptimizationKind
    {
        ConstantFold = 0,
        DeadMove = 1,
        NumericUnary = 2,
        NumericBinary = 3,
        BooleanBranch = 4,
        TableGetPic = 5,
        TableSetPic = 6,
        KnownClosureCall = 7,
        FixedResultWindowReuse = 8
    }

    public sealed class LuaJitOsrEntryMap : System.IEquatable<Lunil.CodeGen.Cil.Jit.LuaJitOsrEntryMap>
    {
        public int HeaderProgramCounter { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Jit.LuaJitOsrRegisterMap> Registers { get => throw null; init { } }
        public bool FrameTopMaterialized { get => throw null; init { } }
        public bool OpenUpvaluesMaterialized { get => throw null; init { } }
        public bool ToBeClosedStateMaterialized { get => throw null; init { } }
        public LuaJitOsrEntryMap(int HeaderProgramCounter, System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Jit.LuaJitOsrRegisterMap> Registers, bool FrameTopMaterialized, bool OpenUpvaluesMaterialized, bool ToBeClosedStateMaterialized) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Jit.LuaJitOsrEntryMap? left, Lunil.CodeGen.Cil.Jit.LuaJitOsrEntryMap? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Jit.LuaJitOsrEntryMap? left, Lunil.CodeGen.Cil.Jit.LuaJitOsrEntryMap? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Jit.LuaJitOsrEntryMap? other) => throw null;
        public void Deconstruct(out int HeaderProgramCounter, out System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Jit.LuaJitOsrRegisterMap> Registers, out bool FrameTopMaterialized, out bool OpenUpvaluesMaterialized, out bool ToBeClosedStateMaterialized) => throw null;
    }

    public sealed class LuaJitOsrRegisterMap : System.IEquatable<Lunil.CodeGen.Cil.Jit.LuaJitOsrRegisterMap>
    {
        public int CanonicalRegister { get => throw null; init { } }
        public int CompiledSlot { get => throw null; init { } }
        public LuaJitOsrRegisterMap(int CanonicalRegister, int CompiledSlot) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Jit.LuaJitOsrRegisterMap? left, Lunil.CodeGen.Cil.Jit.LuaJitOsrRegisterMap? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Jit.LuaJitOsrRegisterMap? left, Lunil.CodeGen.Cil.Jit.LuaJitOsrRegisterMap? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Jit.LuaJitOsrRegisterMap? other) => throw null;
        public void Deconstruct(out int CanonicalRegister, out int CompiledSlot) => throw null;
    }

    public sealed class LuaJitOsrRegisterTypeProfile : System.IEquatable<Lunil.CodeGen.Cil.Jit.LuaJitOsrRegisterTypeProfile>
    {
        public int ProgramCounter { get => throw null; init { } }
        public int Register { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Jit.LuaJitValueKinds Kinds { get => throw null; init { } }
        public LuaJitOsrRegisterTypeProfile(int ProgramCounter, int Register, Lunil.CodeGen.Cil.Jit.LuaJitValueKinds Kinds) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Jit.LuaJitOsrRegisterTypeProfile? left, Lunil.CodeGen.Cil.Jit.LuaJitOsrRegisterTypeProfile? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Jit.LuaJitOsrRegisterTypeProfile? left, Lunil.CodeGen.Cil.Jit.LuaJitOsrRegisterTypeProfile? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Jit.LuaJitOsrRegisterTypeProfile? other) => throw null;
        public void Deconstruct(out int ProgramCounter, out int Register, out Lunil.CodeGen.Cil.Jit.LuaJitValueKinds Kinds) => throw null;
    }

    public enum LuaJitOsrState
    {
        Disabled = 0,
        Ineligible = 1,
        Profiling = 2,
        Queued = 3,
        Compiling = 4,
        Ready = 5,
        Failed = 6,
        Invalidated = 7
    }

    public enum LuaJitPolicy
    {
        InterpreterOnly = 0,
        Auto = 1,
        PreferJit = 2,
        RequireJit = 3
    }

    public static class LuaJitProfileCodec
    {
        public const int CurrentSchemaVersion = 1;
        public const int CurrentCodegenVersion = 1;
        public static byte[] Serialize(Lunil.IR.Canonical.LuaIrModule module, System.Collections.Generic.IReadOnlyList<Lunil.CodeGen.Cil.Jit.LuaJitFunctionProfile> profiles) => throw null;
        public static Lunil.CodeGen.Cil.Jit.LuaJitModuleProfile Deserialize(Lunil.IR.Canonical.LuaIrModule module, System.ReadOnlySpan<byte> payload) => throw null;
    }

    public static class LuaJitProfileDiagnosticCodes
    {
        public const string Malformed = "JITP1001";
        public const string Incompatible = "JITP1002";
        public const string InvalidProfile = "JITP1003";
    }

    public sealed class LuaJitProfileImportResult : System.IEquatable<Lunil.CodeGen.Cil.Jit.LuaJitProfileImportResult>
    {
        public Lunil.CodeGen.Cil.Jit.LuaJitProfileImportStatus Status { get => throw null; init { } }
        public string? DiagnosticCode { get => throw null; init { } }
        public string? Message { get => throw null; init { } }
        public bool Succeeded { get => throw null; }
        public LuaJitProfileImportResult(Lunil.CodeGen.Cil.Jit.LuaJitProfileImportStatus Status, string? DiagnosticCode = null, string? Message = null) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Jit.LuaJitProfileImportResult? left, Lunil.CodeGen.Cil.Jit.LuaJitProfileImportResult? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Jit.LuaJitProfileImportResult? left, Lunil.CodeGen.Cil.Jit.LuaJitProfileImportResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Jit.LuaJitProfileImportResult? other) => throw null;
        public void Deconstruct(out Lunil.CodeGen.Cil.Jit.LuaJitProfileImportStatus Status, out string? DiagnosticCode, out string? Message) => throw null;
    }

    public enum LuaJitProfileImportStatus
    {
        Imported = 0,
        Rejected = 1,
        Incompatible = 2,
        Disabled = 3
    }

    public sealed class LuaJitProfileRemapResult : System.IEquatable<Lunil.CodeGen.Cil.Jit.LuaJitProfileRemapResult>
    {
        public Lunil.CodeGen.Cil.Jit.LuaJitProfileRemapStatus Status { get => throw null; init { } }
        public byte[]? Payload { get => throw null; init { } }
        public int RemappedFunctionCount { get => throw null; init { } }
        public int IncompatibleFunctionCount { get => throw null; init { } }
        public int AddedFunctionCount { get => throw null; init { } }
        public int RemovedFunctionCount { get => throw null; init { } }
        public string? DiagnosticCode { get => throw null; init { } }
        public string? Message { get => throw null; init { } }
        public bool Succeeded { get => throw null; }
        public LuaJitProfileRemapResult(Lunil.CodeGen.Cil.Jit.LuaJitProfileRemapStatus Status, byte[]? Payload, int RemappedFunctionCount, int IncompatibleFunctionCount, int AddedFunctionCount, int RemovedFunctionCount, string? DiagnosticCode = null, string? Message = null) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Jit.LuaJitProfileRemapResult? left, Lunil.CodeGen.Cil.Jit.LuaJitProfileRemapResult? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Jit.LuaJitProfileRemapResult? left, Lunil.CodeGen.Cil.Jit.LuaJitProfileRemapResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Jit.LuaJitProfileRemapResult? other) => throw null;
        public void Deconstruct(out Lunil.CodeGen.Cil.Jit.LuaJitProfileRemapStatus Status, out byte[]? Payload, out int RemappedFunctionCount, out int IncompatibleFunctionCount, out int AddedFunctionCount, out int RemovedFunctionCount, out string? DiagnosticCode, out string? Message) => throw null;
    }

    public enum LuaJitProfileRemapStatus
    {
        Remapped = 0,
        Rejected = 1
    }

    public static class LuaJitProfileRemapper
    {
        public static Lunil.CodeGen.Cil.Jit.LuaJitProfileRemapResult Remap(Lunil.IR.Canonical.LuaIrModule sourceModule, Lunil.IR.Canonical.LuaIrModule targetModule, System.ReadOnlySpan<byte> sourcePayload) => throw null;
    }

    public sealed class LuaJitSiteProfile : System.IEquatable<Lunil.CodeGen.Cil.Jit.LuaJitSiteProfile>
    {
        public int ProgramCounter { get => throw null; init { } }
        public Lunil.IR.Canonical.LuaIrOpcode Opcode { get => throw null; init { } }
        public long Samples { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Jit.LuaJitValueKinds FirstOperandKinds { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Jit.LuaJitValueKinds SecondOperandKinds { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Jit.LuaJitValueKinds ThirdOperandKinds { get => throw null; init { } }
        public long BranchTaken { get => throw null; init { } }
        public long BranchNotTaken { get => throw null; init { } }
        public bool IsMegamorphic { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Jit.LuaJitTableShapeProfile> TableShapes { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Jit.LuaJitCallTargetProfile> CallTargets { get => throw null; init { } }
        public LuaJitSiteProfile(int ProgramCounter, Lunil.IR.Canonical.LuaIrOpcode Opcode, long Samples, Lunil.CodeGen.Cil.Jit.LuaJitValueKinds FirstOperandKinds, Lunil.CodeGen.Cil.Jit.LuaJitValueKinds SecondOperandKinds, Lunil.CodeGen.Cil.Jit.LuaJitValueKinds ThirdOperandKinds, long BranchTaken, long BranchNotTaken, bool IsMegamorphic, System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Jit.LuaJitTableShapeProfile> TableShapes, System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Jit.LuaJitCallTargetProfile> CallTargets) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Jit.LuaJitSiteProfile? left, Lunil.CodeGen.Cil.Jit.LuaJitSiteProfile? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Jit.LuaJitSiteProfile? left, Lunil.CodeGen.Cil.Jit.LuaJitSiteProfile? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Jit.LuaJitSiteProfile? other) => throw null;
        public void Deconstruct(out int ProgramCounter, out Lunil.IR.Canonical.LuaIrOpcode Opcode, out long Samples, out Lunil.CodeGen.Cil.Jit.LuaJitValueKinds FirstOperandKinds, out Lunil.CodeGen.Cil.Jit.LuaJitValueKinds SecondOperandKinds, out Lunil.CodeGen.Cil.Jit.LuaJitValueKinds ThirdOperandKinds, out long BranchTaken, out long BranchNotTaken, out bool IsMegamorphic, out System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Jit.LuaJitTableShapeProfile> TableShapes, out System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Jit.LuaJitCallTargetProfile> CallTargets) => throw null;
    }

    public sealed class LuaJitStatistics : System.IEquatable<Lunil.CodeGen.Cil.Jit.LuaJitStatistics>
    {
        public long FunctionEntries { get => throw null; init { } }
        public long Backedges { get => throw null; init { } }
        public long CompilationQueued { get => throw null; init { } }
        public long CompilationStarted { get => throw null; init { } }
        public long CompilationCompleted { get => throw null; init { } }
        public long CompilationFailed { get => throw null; init { } }
        public long QueueRejected { get => throw null; init { } }
        public long CompiledInvocations { get => throw null; init { } }
        public long InterpreterFallbacks { get => throw null; init { } }
        public long Deoptimizations { get => throw null; init { } }
        public long CacheEvictions { get => throw null; init { } }
        public long Invalidations { get => throw null; init { } }
        public long EstimatedCodeBytes { get => throw null; init { } }
        public long TotalQueueLatencyTicks { get => throw null; init { } }
        public long TotalCompilationTicks { get => throw null; init { } }
        public long Tier2CompilationQueued { get => throw null; init { } }
        public long Tier2CompilationStarted { get => throw null; init { } }
        public long Tier2CompilationCompleted { get => throw null; init { } }
        public long Tier2CompilationFailed { get => throw null; init { } }
        public long Tier2Invocations { get => throw null; init { } }
        public long Tier2GuardFailures { get => throw null; init { } }
        public long Tier2Invalidations { get => throw null; init { } }
        public long LoopOsrRequests { get => throw null; init { } }
        public long LoopOsrCompilationQueued { get => throw null; init { } }
        public long LoopOsrCompilationStarted { get => throw null; init { } }
        public long LoopOsrCompilationCompleted { get => throw null; init { } }
        public long LoopOsrCompilationFailed { get => throw null; init { } }
        public long LoopOsrEntries { get => throw null; init { } }
        public long LoopOsrExits { get => throw null; init { } }
        public long LoopOsrGuardFailures { get => throw null; init { } }
        public long LoopOsrInvalidations { get => throw null; init { } }
        public long CompiledCanonicalInstructions { get => throw null; init { } }
        public long SchedulerExits { get => throw null; init { } }
        public long ContinueExits { get => throw null; init { } }
        public long PollExits { get => throw null; init { } }
        public long CallExits { get => throw null; init { } }
        public long TailCallExits { get => throw null; init { } }
        public long ReturnExits { get => throw null; init { } }
        public long InstructionBudgetPolls { get => throw null; init { } }
        public long GarbageCollectionPolls { get => throw null; init { } }
        public long DebugModeDeoptimizations { get => throw null; init { } }
        public long Tier1CompileAllocatedBytes { get => throw null; init { } }
        public long Tier1DirectCanonicalInstructions { get => throw null; init { } }
        public long Tier1SlowPathCanonicalInstructions { get => throw null; init { } }
        public long Tier1PlanInstructions { get => throw null; init { } }
        public long TotalCanonicalVerificationTicks { get => throw null; init { } }
        public long TotalControlFlowAnalysisTicks { get => throw null; init { } }
        public long TotalMethodPlanBuildTicks { get => throw null; init { } }
        public long TotalPlanVerificationTicks { get => throw null; init { } }
        public long TotalReflectionEmitTicks { get => throw null; init { } }
        public long TotalDelegateCreationTicks { get => throw null; init { } }
        public long EligibilityEvaluated { get => throw null; init { } }
        public long EligibilityAccepted { get => throw null; init { } }
        public long EligibilityRejected { get => throw null; init { } }
        public long Tier2EligibilityEvaluated { get => throw null; init { } }
        public long Tier2EligibilityAccepted { get => throw null; init { } }
        public long Tier2EligibilityRejected { get => throw null; init { } }
        public long LoopOsrEligibilityEvaluated { get => throw null; init { } }
        public long LoopOsrEligibilityAccepted { get => throw null; init { } }
        public long LoopOsrEligibilityRejected { get => throw null; init { } }
        public long Tier2MethodEntries { get => throw null; init { } }
        public long Tier2CompletedInvocations { get => throw null; init { } }
        public long Tier2UnsupportedExits { get => throw null; init { } }
        public LuaJitStatistics(long FunctionEntries, long Backedges, long CompilationQueued, long CompilationStarted, long CompilationCompleted, long CompilationFailed, long QueueRejected, long CompiledInvocations, long InterpreterFallbacks, long Deoptimizations, long CacheEvictions, long Invalidations, long EstimatedCodeBytes, long TotalQueueLatencyTicks, long TotalCompilationTicks, long Tier2CompilationQueued, long Tier2CompilationStarted, long Tier2CompilationCompleted, long Tier2CompilationFailed, long Tier2Invocations, long Tier2GuardFailures, long Tier2Invalidations, long LoopOsrRequests, long LoopOsrCompilationQueued, long LoopOsrCompilationStarted, long LoopOsrCompilationCompleted, long LoopOsrCompilationFailed, long LoopOsrEntries, long LoopOsrExits, long LoopOsrGuardFailures, long LoopOsrInvalidations, long CompiledCanonicalInstructions, long SchedulerExits, long ContinueExits, long PollExits, long CallExits, long TailCallExits, long ReturnExits, long InstructionBudgetPolls, long GarbageCollectionPolls, long DebugModeDeoptimizations, long Tier1CompileAllocatedBytes, long Tier1DirectCanonicalInstructions, long Tier1SlowPathCanonicalInstructions, long Tier1PlanInstructions, long TotalCanonicalVerificationTicks, long TotalControlFlowAnalysisTicks, long TotalMethodPlanBuildTicks, long TotalPlanVerificationTicks, long TotalReflectionEmitTicks, long TotalDelegateCreationTicks, long EligibilityEvaluated, long EligibilityAccepted, long EligibilityRejected, long Tier2EligibilityEvaluated, long Tier2EligibilityAccepted, long Tier2EligibilityRejected, long LoopOsrEligibilityEvaluated, long LoopOsrEligibilityAccepted, long LoopOsrEligibilityRejected) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Jit.LuaJitStatistics? left, Lunil.CodeGen.Cil.Jit.LuaJitStatistics? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Jit.LuaJitStatistics? left, Lunil.CodeGen.Cil.Jit.LuaJitStatistics? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Jit.LuaJitStatistics? other) => throw null;
        public void Deconstruct(out long FunctionEntries, out long Backedges, out long CompilationQueued, out long CompilationStarted, out long CompilationCompleted, out long CompilationFailed, out long QueueRejected, out long CompiledInvocations, out long InterpreterFallbacks, out long Deoptimizations, out long CacheEvictions, out long Invalidations, out long EstimatedCodeBytes, out long TotalQueueLatencyTicks, out long TotalCompilationTicks, out long Tier2CompilationQueued, out long Tier2CompilationStarted, out long Tier2CompilationCompleted, out long Tier2CompilationFailed, out long Tier2Invocations, out long Tier2GuardFailures, out long Tier2Invalidations, out long LoopOsrRequests, out long LoopOsrCompilationQueued, out long LoopOsrCompilationStarted, out long LoopOsrCompilationCompleted, out long LoopOsrCompilationFailed, out long LoopOsrEntries, out long LoopOsrExits, out long LoopOsrGuardFailures, out long LoopOsrInvalidations, out long CompiledCanonicalInstructions, out long SchedulerExits, out long ContinueExits, out long PollExits, out long CallExits, out long TailCallExits, out long ReturnExits, out long InstructionBudgetPolls, out long GarbageCollectionPolls, out long DebugModeDeoptimizations, out long Tier1CompileAllocatedBytes, out long Tier1DirectCanonicalInstructions, out long Tier1SlowPathCanonicalInstructions, out long Tier1PlanInstructions, out long TotalCanonicalVerificationTicks, out long TotalControlFlowAnalysisTicks, out long TotalMethodPlanBuildTicks, out long TotalPlanVerificationTicks, out long TotalReflectionEmitTicks, out long TotalDelegateCreationTicks, out long EligibilityEvaluated, out long EligibilityAccepted, out long EligibilityRejected, out long Tier2EligibilityEvaluated, out long Tier2EligibilityAccepted, out long Tier2EligibilityRejected, out long LoopOsrEligibilityEvaluated, out long LoopOsrEligibilityAccepted, out long LoopOsrEligibilityRejected) => throw null;
    }

    public sealed class LuaJitTableShapeProfile : System.IEquatable<Lunil.CodeGen.Cil.Jit.LuaJitTableShapeProfile>
    {
        public Lunil.CodeGen.Cil.Jit.LuaJitValueKinds KeyKinds { get => throw null; init { } }
        public int ArrayCapacity { get => throw null; init { } }
        public ulong ShapeVersion { get => throw null; init { } }
        public ulong MetatableVersion { get => throw null; init { } }
        public bool HasMetatable { get => throw null; init { } }
        public long Samples { get => throw null; init { } }
        public LuaJitTableShapeProfile(Lunil.CodeGen.Cil.Jit.LuaJitValueKinds KeyKinds, int ArrayCapacity, ulong ShapeVersion, ulong MetatableVersion, bool HasMetatable, long Samples) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Jit.LuaJitTableShapeProfile? left, Lunil.CodeGen.Cil.Jit.LuaJitTableShapeProfile? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Jit.LuaJitTableShapeProfile? left, Lunil.CodeGen.Cil.Jit.LuaJitTableShapeProfile? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Jit.LuaJitTableShapeProfile? other) => throw null;
        public void Deconstruct(out Lunil.CodeGen.Cil.Jit.LuaJitValueKinds KeyKinds, out int ArrayCapacity, out ulong ShapeVersion, out ulong MetatableVersion, out bool HasMetatable, out long Samples) => throw null;
    }

    public enum LuaJitTier2CodeKind
    {
        ManagedProfileProgram = 0,
        ExactNumericSpecializedCil = 1,
        GuardedSpecializedCil = 2
    }

    public readonly struct LuaJitTier2CompilationMetrics : System.IEquatable<Lunil.CodeGen.Cil.Jit.LuaJitTier2CompilationMetrics>
    {
        public System.TimeSpan CanonicalVerificationDuration { get => throw null; init { } }
        public System.TimeSpan LivenessAnalysisDuration { get => throw null; init { } }
        public bool LivenessCacheHit { get => throw null; init { } }
        public System.TimeSpan OptimizationPlanningDuration { get => throw null; init { } }
        public System.TimeSpan CilEmissionDuration { get => throw null; init { } }
        public System.TimeSpan DelegateCreationDuration { get => throw null; init { } }
        public long AllocatedBytes { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Jit.LuaJitTier2CodeKind CodeKind { get => throw null; init { } }
        public int OptimizationCount { get => throw null; init { } }
        public int SpecializedOptimizationCount { get => throw null; init { } }
        public int DeoptSiteCount { get => throw null; init { } }
        public long EstimatedCodeBytes { get => throw null; init { } }
        public LuaJitTier2CompilationMetrics(System.TimeSpan CanonicalVerificationDuration, System.TimeSpan LivenessAnalysisDuration, bool LivenessCacheHit, System.TimeSpan OptimizationPlanningDuration, System.TimeSpan CilEmissionDuration, System.TimeSpan DelegateCreationDuration, long AllocatedBytes, Lunil.CodeGen.Cil.Jit.LuaJitTier2CodeKind CodeKind, int OptimizationCount, int SpecializedOptimizationCount, int DeoptSiteCount, long EstimatedCodeBytes) { }
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.CodeGen.Cil.Jit.LuaJitTier2CompilationMetrics left, Lunil.CodeGen.Cil.Jit.LuaJitTier2CompilationMetrics right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Jit.LuaJitTier2CompilationMetrics left, Lunil.CodeGen.Cil.Jit.LuaJitTier2CompilationMetrics right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.CodeGen.Cil.Jit.LuaJitTier2CompilationMetrics other) => throw null;
        public void Deconstruct(out System.TimeSpan CanonicalVerificationDuration, out System.TimeSpan LivenessAnalysisDuration, out bool LivenessCacheHit, out System.TimeSpan OptimizationPlanningDuration, out System.TimeSpan CilEmissionDuration, out System.TimeSpan DelegateCreationDuration, out long AllocatedBytes, out Lunil.CodeGen.Cil.Jit.LuaJitTier2CodeKind CodeKind, out int OptimizationCount, out int SpecializedOptimizationCount, out int DeoptSiteCount, out long EstimatedCodeBytes) => throw null;
    }

    public static class LuaJitTier2DiagnosticCodes
    {
        public const string NoNumericHotspot = "JIT2101";
        public const string PolymorphicNumericProfile = "JIT2102";
        public const string ManagedOptimizationRequired = "JIT2103";
        public const string UnexpectedCodeKind = "JIT2104";
        public const string ManagedSemanticBoundary = "JIT2105";
        public const string UnsupportedInstruction = "JIT2106";
        public const string InsufficientTier2Work = "JIT2107";
        public const string HotLoopCallBoundary = "JIT2108";
    }

    public sealed class LuaJitTier2Eligibility : System.IEquatable<Lunil.CodeGen.Cil.Jit.LuaJitTier2Eligibility>
    {
        public bool IsAutoEligible { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Jit.LuaJitTier2EligibilityReason Reason { get => throw null; init { } }
        public string? DiagnosticCode { get => throw null; init { } }
        public long ProfileSamples { get => throw null; init { } }
        public int OptimizationCount { get => throw null; init { } }
        public int NumericOptimizationCount { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Jit.LuaJitTier2CodeKind ExpectedCodeKind { get => throw null; init { } }
        public LuaJitTier2Eligibility(bool IsAutoEligible, Lunil.CodeGen.Cil.Jit.LuaJitTier2EligibilityReason Reason, string? DiagnosticCode, long ProfileSamples, int OptimizationCount, int NumericOptimizationCount, Lunil.CodeGen.Cil.Jit.LuaJitTier2CodeKind ExpectedCodeKind) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Jit.LuaJitTier2Eligibility? left, Lunil.CodeGen.Cil.Jit.LuaJitTier2Eligibility? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Jit.LuaJitTier2Eligibility? left, Lunil.CodeGen.Cil.Jit.LuaJitTier2Eligibility? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Jit.LuaJitTier2Eligibility? other) => throw null;
        public void Deconstruct(out bool IsAutoEligible, out Lunil.CodeGen.Cil.Jit.LuaJitTier2EligibilityReason Reason, out string? DiagnosticCode, out long ProfileSamples, out int OptimizationCount, out int NumericOptimizationCount, out Lunil.CodeGen.Cil.Jit.LuaJitTier2CodeKind ExpectedCodeKind) => throw null;
    }

    public enum LuaJitTier2EligibilityReason
    {
        Eligible = 0,
        NoNumericHotspot = 1,
        PolymorphicNumericProfile = 2,
        ManagedOptimizationRequired = 3,
        ManagedSemanticBoundary = 4,
        UnsupportedInstruction = 5,
        InsufficientTier2Work = 6,
        HotLoopCallBoundary = 7
    }

    public sealed class LuaJitTier2Plan : System.IEquatable<Lunil.CodeGen.Cil.Jit.LuaJitTier2Plan>
    {
        public int FunctionId { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Jit.LuaJitOptimization> Optimizations { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Jit.LuaJitDeoptMapEntry> DeoptMap { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Jit.LuaJitTier2CodeKind CodeKind { get => throw null; init { } }
        public int NumericRegionCount { get => throw null; init { } }
        public int UnboxedNumericLocalCount { get => throw null; init { } }
        public int DirectNumericInstructionCount { get => throw null; init { } }
        public int NumericRegionSafepointCount { get => throw null; init { } }
        public int NumericRegionHotInstructionBudgetCheckCount { get => throw null; init { } }
        public LuaJitTier2Plan(int FunctionId, System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Jit.LuaJitOptimization> Optimizations, System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Jit.LuaJitDeoptMapEntry> DeoptMap) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Jit.LuaJitTier2Plan? left, Lunil.CodeGen.Cil.Jit.LuaJitTier2Plan? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Jit.LuaJitTier2Plan? left, Lunil.CodeGen.Cil.Jit.LuaJitTier2Plan? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Jit.LuaJitTier2Plan? other) => throw null;
        public void Deconstruct(out int FunctionId, out System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Jit.LuaJitOptimization> Optimizations, out System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Jit.LuaJitDeoptMapEntry> DeoptMap) => throw null;
    }

    public enum LuaJitTier2State
    {
        Disabled = 0,
        Profiling = 1,
        Queued = 2,
        Compiling = 3,
        Ready = 4,
        Failed = 5,
        Invalidated = 6,
        Ineligible = 7
    }

    [System.Flags]
    public enum LuaJitValueKinds
    {
        None = 0,
        Nil = 1,
        Boolean = 2,
        Integer = 4,
        Float = 8,
        String = 16,
        Table = 32,
        Function = 64,
        Thread = 128,
        Userdata = 256,
        LightUserdata = 512
    }
}
namespace Lunil.CodeGen.Cil.Planning
{
    public sealed class CilCallTarget : System.IEquatable<Lunil.CodeGen.Cil.Planning.CilCallTarget>
    {
        public string Id { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilStackValueKind> ParameterKinds { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Planning.CilStackValueKind ReturnKind { get => throw null; init { } }
        public bool IsGcSafePoint { get => throw null; init { } }
        public CilCallTarget(string Id, System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilStackValueKind> ParameterKinds, Lunil.CodeGen.Cil.Planning.CilStackValueKind ReturnKind, bool IsGcSafePoint = false) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Planning.CilCallTarget? left, Lunil.CodeGen.Cil.Planning.CilCallTarget? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Planning.CilCallTarget? left, Lunil.CodeGen.Cil.Planning.CilCallTarget? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Planning.CilCallTarget? other) => throw null;
        public void Deconstruct(out string Id, out System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilStackValueKind> ParameterKinds, out Lunil.CodeGen.Cil.Planning.CilStackValueKind ReturnKind, out bool IsGcSafePoint) => throw null;
    }

    public sealed class CilCanonicalBlock : System.IEquatable<Lunil.CodeGen.Cil.Planning.CilCanonicalBlock>
    {
        public int StartProgramCounter { get => throw null; init { } }
        public int Length { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<int> Successors { get => throw null; init { } }
        public CilCanonicalBlock(int StartProgramCounter, int Length, System.Collections.Immutable.ImmutableArray<int> Successors) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Planning.CilCanonicalBlock? left, Lunil.CodeGen.Cil.Planning.CilCanonicalBlock? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Planning.CilCanonicalBlock? left, Lunil.CodeGen.Cil.Planning.CilCanonicalBlock? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Planning.CilCanonicalBlock? other) => throw null;
        public void Deconstruct(out int StartProgramCounter, out int Length, out System.Collections.Immutable.ImmutableArray<int> Successors) => throw null;
    }

    public sealed class CilGcMap : System.IEquatable<Lunil.CodeGen.Cil.Planning.CilGcMap>
    {
        public int CanonicalProgramCounter { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<int> LiveRegisters { get => throw null; init { } }
        public CilGcMap(int CanonicalProgramCounter, System.Collections.Immutable.ImmutableArray<int> LiveRegisters) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Planning.CilGcMap? left, Lunil.CodeGen.Cil.Planning.CilGcMap? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Planning.CilGcMap? left, Lunil.CodeGen.Cil.Planning.CilGcMap? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Planning.CilGcMap? other) => throw null;
        public void Deconstruct(out int CanonicalProgramCounter, out System.Collections.Immutable.ImmutableArray<int> LiveRegisters) => throw null;
    }

    public readonly struct CilLabel : System.IEquatable<Lunil.CodeGen.Cil.Planning.CilLabel>
    {
        public int Id { get => throw null; init { } }
        public CilLabel(int Id) { }
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.CodeGen.Cil.Planning.CilLabel left, Lunil.CodeGen.Cil.Planning.CilLabel right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Planning.CilLabel left, Lunil.CodeGen.Cil.Planning.CilLabel right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.CodeGen.Cil.Planning.CilLabel other) => throw null;
        public void Deconstruct(out int Id) => throw null;
    }

    public sealed class CilLocal : System.IEquatable<Lunil.CodeGen.Cil.Planning.CilLocal>
    {
        public int Index { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Planning.CilStackValueKind Kind { get => throw null; init { } }
        public string Name { get => throw null; init { } }
        public CilLocal(int Index, Lunil.CodeGen.Cil.Planning.CilStackValueKind Kind, string Name) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Planning.CilLocal? left, Lunil.CodeGen.Cil.Planning.CilLocal? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Planning.CilLocal? left, Lunil.CodeGen.Cil.Planning.CilLocal? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Planning.CilLocal? other) => throw null;
        public void Deconstruct(out int Index, out Lunil.CodeGen.Cil.Planning.CilStackValueKind Kind, out string Name) => throw null;
    }

    public sealed class CilMethodPlan : System.IEquatable<Lunil.CodeGen.Cil.Planning.CilMethodPlan>
    {
        public required string Name { get => throw null; init { } }
        public required int FunctionId { get => throw null; init { } }
        public int CanonicalInstructionCount { get => throw null; init { } }
        public int StartProgramCounter { get => throw null; init { } }
        public int RegisterCount { get => throw null; init { } }
        public int DirectCanonicalInstructionCount { get => throw null; init { } }
        public int SlowPathCanonicalInstructionCount { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<int> DirectCanonicalProgramCounters { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<int> SlowPathCanonicalProgramCounters { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilStackValueKind> ParameterKinds { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Planning.CilStackValueKind ReturnKind { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilLocal> Locals { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilPlanInstruction> Instructions { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilGcMap> GcMaps { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilSequencePoint> SequencePoints { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilCanonicalBlock> Blocks { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Planning.CilMethodPlan? left, Lunil.CodeGen.Cil.Planning.CilMethodPlan? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Planning.CilMethodPlan? left, Lunil.CodeGen.Cil.Planning.CilMethodPlan? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Planning.CilMethodPlan? other) => throw null;
    }

    public sealed class CilPlanDiagnostic : System.IEquatable<Lunil.CodeGen.Cil.Planning.CilPlanDiagnostic>
    {
        public string Code { get => throw null; init { } }
        public string Message { get => throw null; init { } }
        public int InstructionIndex { get => throw null; init { } }
        public int CanonicalProgramCounter { get => throw null; init { } }
        public CilPlanDiagnostic(string Code, string Message, int InstructionIndex = -1, int CanonicalProgramCounter = -1) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Planning.CilPlanDiagnostic? left, Lunil.CodeGen.Cil.Planning.CilPlanDiagnostic? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Planning.CilPlanDiagnostic? left, Lunil.CodeGen.Cil.Planning.CilPlanDiagnostic? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Planning.CilPlanDiagnostic? other) => throw null;
        public void Deconstruct(out string Code, out string Message, out int InstructionIndex, out int CanonicalProgramCounter) => throw null;
    }

    public readonly struct CilPlanInstruction : System.IEquatable<Lunil.CodeGen.Cil.Planning.CilPlanInstruction>
    {
        public Lunil.CodeGen.Cil.Planning.CilPlanOpCode OpCode { get => throw null; }
        public int Int32Operand { get => throw null; }
        public long Int64Operand { get => throw null; }
        public Lunil.CodeGen.Cil.Planning.CilLabel Label { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilLabel> Labels { get => throw null; }
        public Lunil.CodeGen.Cil.Planning.CilCallTarget? CallTarget { get => throw null; }
        public int CanonicalProgramCounter { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilPlanInstruction MarkLabel(Lunil.CodeGen.Cil.Planning.CilLabel label, int canonicalProgramCounter = -1) => throw null;
        public static Lunil.CodeGen.Cil.Planning.CilPlanInstruction Simple(Lunil.CodeGen.Cil.Planning.CilPlanOpCode opCode, int canonicalProgramCounter = -1) => throw null;
        public static Lunil.CodeGen.Cil.Planning.CilPlanInstruction WithInt32(Lunil.CodeGen.Cil.Planning.CilPlanOpCode opCode, int operand, int canonicalProgramCounter = -1) => throw null;
        public static Lunil.CodeGen.Cil.Planning.CilPlanInstruction WithInt64(Lunil.CodeGen.Cil.Planning.CilPlanOpCode opCode, long operand, int canonicalProgramCounter = -1) => throw null;
        public static Lunil.CodeGen.Cil.Planning.CilPlanInstruction WithLabel(Lunil.CodeGen.Cil.Planning.CilPlanOpCode opCode, Lunil.CodeGen.Cil.Planning.CilLabel label, int canonicalProgramCounter = -1) => throw null;
        public static Lunil.CodeGen.Cil.Planning.CilPlanInstruction Switch(System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilLabel> labels, int canonicalProgramCounter = -1) => throw null;
        public static Lunil.CodeGen.Cil.Planning.CilPlanInstruction Call(Lunil.CodeGen.Cil.Planning.CilCallTarget target, int canonicalProgramCounter = -1) => throw null;
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.CodeGen.Cil.Planning.CilPlanInstruction left, Lunil.CodeGen.Cil.Planning.CilPlanInstruction right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Planning.CilPlanInstruction left, Lunil.CodeGen.Cil.Planning.CilPlanInstruction right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.CodeGen.Cil.Planning.CilPlanInstruction other) => throw null;
    }

    public sealed class CilPlanLimits : System.IEquatable<Lunil.CodeGen.Cil.Planning.CilPlanLimits>
    {
        public static Lunil.CodeGen.Cil.Planning.CilPlanLimits Default { get => throw null; }
        public int MaximumInstructions { get => throw null; init { } }
        public int MaximumLabels { get => throw null; init { } }
        public int MaximumEvaluationStack { get => throw null; init { } }
        public int MaximumBranchInstructions { get => throw null; init { } }
        public int MaximumMetadataReferences { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Planning.CilPlanLimits? left, Lunil.CodeGen.Cil.Planning.CilPlanLimits? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Planning.CilPlanLimits? left, Lunil.CodeGen.Cil.Planning.CilPlanLimits? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Planning.CilPlanLimits? other) => throw null;
    }

    public enum CilPlanOpCode
    {
        MarkLabel = 0,
        Nop = 1,
        LoadArgument = 2,
        LoadLocal = 3,
        StoreLocal = 4,
        LoadInt32 = 5,
        LoadInt64 = 6,
        ConvertInt64 = 7,
        Add = 8,
        Subtract = 9,
        Call = 10,
        Branch = 11,
        BranchTrue = 12,
        BranchFalse = 13,
        Switch = 14,
        Return = 15
    }

    public sealed class CilPlanVerificationResult : System.IEquatable<Lunil.CodeGen.Cil.Planning.CilPlanVerificationResult>
    {
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilPlanDiagnostic> Diagnostics { get => throw null; init { } }
        public int MaximumEvaluationStack { get => throw null; init { } }
        public bool Succeeded { get => throw null; }
        public CilPlanVerificationResult(System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilPlanDiagnostic> Diagnostics, int MaximumEvaluationStack) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Planning.CilPlanVerificationResult? left, Lunil.CodeGen.Cil.Planning.CilPlanVerificationResult? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Planning.CilPlanVerificationResult? left, Lunil.CodeGen.Cil.Planning.CilPlanVerificationResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Planning.CilPlanVerificationResult? other) => throw null;
        public void Deconstruct(out System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilPlanDiagnostic> Diagnostics, out int MaximumEvaluationStack) => throw null;
    }

    public sealed class CilSequencePoint : System.IEquatable<Lunil.CodeGen.Cil.Planning.CilSequencePoint>
    {
        public int PlanInstructionIndex { get => throw null; init { } }
        public int CanonicalProgramCounter { get => throw null; init { } }
        public int SourceLine { get => throw null; init { } }
        public int LogicalProgramCounter { get => throw null; init { } }
        public CilSequencePoint(int PlanInstructionIndex, int CanonicalProgramCounter, int SourceLine, int LogicalProgramCounter = -1) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Planning.CilSequencePoint? left, Lunil.CodeGen.Cil.Planning.CilSequencePoint? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Planning.CilSequencePoint? left, Lunil.CodeGen.Cil.Planning.CilSequencePoint? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Planning.CilSequencePoint? other) => throw null;
        public void Deconstruct(out int PlanInstructionIndex, out int CanonicalProgramCounter, out int SourceLine, out int LogicalProgramCounter) => throw null;
    }

    public enum CilStackValueKind
    {
        Void = 0,
        Int32 = 1,
        Int64 = 2,
        Float = 3,
        Object = 4,
        LuaValue = 5,
        ExecutionContext = 6,
        Thread = 7,
        Frame = 8,
        CompiledExit = 9
    }

    public static class CilWellKnownCalls
    {
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget ContextTryReserveInstructions { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget FrameGetProgramCounter { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget CommitProgramCounter { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget MaterializeConstant { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget ReadRegister { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget WriteRegister { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget ReadUpvalue { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget WriteUpvalue { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget ClearRegisters { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget SetFrameTop { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget LuaValueIsTruthy { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget CanExecuteCompiled { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget CanExecuteCompiledFrame { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget ReadRegisterUnchecked { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget WriteRegisterUnchecked { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget ClearRegistersUnchecked { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget SetFrameTopUnchecked { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget ReadTruthyAndSetFrameTopUnchecked { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget CanSkipClose { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget ObserveCanonicalInstruction { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget ExecuteCanonicalInstruction { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget CanExecuteUnaryPrimitive { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget ExecuteUnaryPrimitive { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget CanExecuteBinaryPrimitive { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget ExecuteBinaryPrimitive { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget ExecuteNumericForPrepare { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget ExecuteNumericForLoop { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget ExecuteNewTable { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget ExecuteGetTable { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget ExecuteSetTable { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget ExecuteSetList { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget ExecuteClosure { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget ExecuteVarArg { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget TryExecuteFramelessCall { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget CanContinueAfterFramelessCall { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget PollGcSafepoint { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget ExitPoll { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget ExitContinue { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget ExitReturn { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget ExitCall { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget ExitTailCall { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget ExitDeopt { get => throw null; }
        public static System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilCallTarget> All { get => throw null; }
        public static bool TryGet(string id, out Lunil.CodeGen.Cil.Planning.CilCallTarget target) => throw null;
    }
}
namespace Lunil.CodeGen.Cil.Verification
{
    public static class CilMethodPlanVerifier
    {
        public static Lunil.CodeGen.Cil.Planning.CilPlanVerificationResult Verify(Lunil.CodeGen.Cil.Planning.CilMethodPlan plan, Lunil.CodeGen.Cil.Planning.CilPlanLimits? limits = null, System.Threading.CancellationToken cancellationToken = null) => throw null;
    }
}
