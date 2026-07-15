// Target Frameworks: net10.0
#nullable enable

namespace Lunil.CodeGen.Cil
{
    public static class LuaAotCompiler
    {
        public static Lunil.CodeGen.Cil.Artifacts.LuaAotCompilationResult Compile(Lunil.IR.Canonical.LuaIrModule module, Lunil.CodeGen.Cil.Artifacts.LuaAotCompilationOptions? options = null) => throw null;
    }

    public static class LuaAotModuleIdentity
    {
        public static string ComputeContentId(Lunil.IR.Canonical.LuaIrModule module) => throw null;
        public static byte[] SerializeCanonicalModule(Lunil.IR.Canonical.LuaIrModule module) => throw null;
        public static Lunil.IR.Canonical.LuaIrModule DeserializeCanonicalModule(System.ReadOnlySpan<byte> content) => throw null;
    }

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

    public sealed class LuaPersistedAotExecutor
    {
        public Lunil.CodeGen.Cil.Loading.LuaAotLoadedModule LoadedModule { get => throw null; }
        public Lunil.Runtime.Execution.LuaInterpreterOptions InterpreterOptions { get => throw null; }
        public Lunil.CodeGen.Cil.LuaPersistedAotStatistics Statistics { get => throw null; }
        public LuaPersistedAotExecutor(Lunil.CodeGen.Cil.Loading.LuaAotLoadedModule loadedModule, Lunil.Runtime.Execution.LuaInterpreterOptions? interpreterOptions = null) { }
        public Lunil.Runtime.Execution.LuaExecutionResult Execute(Lunil.Runtime.LuaState state, Lunil.Runtime.Execution.LuaClosure closure, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments = null) => throw null;
        public Lunil.Runtime.Execution.LuaExecutionResult Start(Lunil.Runtime.LuaState state, Lunil.Runtime.Execution.LuaThread thread, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments = null) => throw null;
        public Lunil.Runtime.Execution.LuaExecutionResult Resume(Lunil.Runtime.LuaState state, Lunil.Runtime.Execution.LuaThread thread, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments = null) => throw null;
        public Lunil.Runtime.Execution.LuaExecutionResult Close(Lunil.Runtime.LuaState state, Lunil.Runtime.Execution.LuaThread thread) => throw null;
    }

    public sealed class LuaPersistedAotStatistics : System.IEquatable<Lunil.CodeGen.Cil.LuaPersistedAotStatistics>
    {
        public long CompiledInvocations { get => throw null; init { } }
        public long InterpreterFallbacks { get => throw null; init { } }
        public long Deoptimizations { get => throw null; init { } }
        public long DebugModeDeoptimizations { get => throw null; init { } }
        public long UnexpectedDeoptimizations { get => throw null; init { } }
        public LuaPersistedAotStatistics(long CompiledInvocations, long InterpreterFallbacks, long Deoptimizations, long DebugModeDeoptimizations, long UnexpectedDeoptimizations) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.LuaPersistedAotStatistics? left, Lunil.CodeGen.Cil.LuaPersistedAotStatistics? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.LuaPersistedAotStatistics? left, Lunil.CodeGen.Cil.LuaPersistedAotStatistics? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.LuaPersistedAotStatistics? other) => throw null;
        public void Deconstruct(out long CompiledInvocations, out long InterpreterFallbacks, out long Deoptimizations, out long DebugModeDeoptimizations, out long UnexpectedDeoptimizations) => throw null;
    }

    public sealed class LuaStaticAotExecutor
    {
        public Lunil.Runtime.Execution.LuaInterpreterOptions InterpreterOptions { get => throw null; }
        public LuaStaticAotExecutor(Lunil.Runtime.Execution.LuaInterpreterOptions? interpreterOptions = null) { }
        public Lunil.Runtime.Execution.LuaExecutionResult Execute(Lunil.Runtime.LuaState state, Lunil.Runtime.Execution.LuaClosure closure, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments = null) => throw null;
        public Lunil.Runtime.Execution.LuaExecutionResult Start(Lunil.Runtime.LuaState state, Lunil.Runtime.Execution.LuaThread thread, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments = null) => throw null;
        public Lunil.Runtime.Execution.LuaExecutionResult Resume(Lunil.Runtime.LuaState state, Lunil.Runtime.Execution.LuaThread thread, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments = null) => throw null;
        public Lunil.Runtime.Execution.LuaExecutionResult Close(Lunil.Runtime.LuaState state, Lunil.Runtime.Execution.LuaThread thread) => throw null;
    }

    public sealed class LuaStaticAotModule
    {
        public string ModuleName { get => throw null; }
        public string ModuleContentId { get => throw null; }
        public Lunil.IR.Canonical.LuaIrModule CanonicalModule { get => throw null; }
        public System.Collections.Generic.IReadOnlyDictionary<int, Lunil.CodeGen.Cil.Emission.LuaCompiledMethod> Functions { get => throw null; }
        public LuaStaticAotModule(string moduleName, string moduleContentId, Lunil.IR.Canonical.LuaIrModule canonicalModule, System.Collections.Generic.IReadOnlyDictionary<int, Lunil.CodeGen.Cil.Emission.LuaCompiledMethod> functions) { }
        public bool TryGetFunction(int functionId, out Lunil.CodeGen.Cil.Emission.LuaCompiledMethod? function) => throw null;
        public Lunil.Runtime.Execution.LuaClosure CreateMainClosure(Lunil.Runtime.LuaState state) => throw null;
    }

    public static class LuaStaticAotRegistry
    {
        public static void Register(Lunil.CodeGen.Cil.LuaStaticAotModule module) { }
        public static bool TryGetModule(string moduleName, out Lunil.CodeGen.Cil.LuaStaticAotModule? module) => throw null;
        public static bool TryGetModule(Lunil.IR.Canonical.LuaIrModule module, out Lunil.CodeGen.Cil.LuaStaticAotModule? compiledModule) => throw null;
        public static bool TryGetFunction(Lunil.IR.Canonical.LuaIrModule module, int functionId, out Lunil.CodeGen.Cil.Emission.LuaCompiledMethod? function) => throw null;
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
namespace Lunil.CodeGen.Cil.Artifacts
{
    public sealed class LuaAotArtifact : System.IEquatable<Lunil.CodeGen.Cil.Artifacts.LuaAotArtifact>
    {
        public Lunil.CodeGen.Cil.Artifacts.LuaAotArtifactManifest Manifest { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<byte> PeImage { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<byte> PortablePdbImage { get => throw null; init { } }
        public LuaAotArtifact(Lunil.CodeGen.Cil.Artifacts.LuaAotArtifactManifest Manifest, System.Collections.Immutable.ImmutableArray<byte> PeImage, System.Collections.Immutable.ImmutableArray<byte> PortablePdbImage) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Artifacts.LuaAotArtifact? left, Lunil.CodeGen.Cil.Artifacts.LuaAotArtifact? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Artifacts.LuaAotArtifact? left, Lunil.CodeGen.Cil.Artifacts.LuaAotArtifact? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Artifacts.LuaAotArtifact? other) => throw null;
        public void Deconstruct(out Lunil.CodeGen.Cil.Artifacts.LuaAotArtifactManifest Manifest, out System.Collections.Immutable.ImmutableArray<byte> PeImage, out System.Collections.Immutable.ImmutableArray<byte> PortablePdbImage) => throw null;
    }

    public sealed class LuaAotArtifactManifest : System.IEquatable<Lunil.CodeGen.Cil.Artifacts.LuaAotArtifactManifest>
    {
        public const string CurrentMagic = "LUNIL-CIL-AOT";
        public const int CurrentArtifactSchemaVersion = 1;
        public const int CurrentCodegenVersion = 1;
        public required string Magic { get => throw null; init { } }
        public required int ArtifactSchemaVersion { get => throw null; init { } }
        public required int IrFormatVersion { get => throw null; init { } }
        public required int RuntimeAbiVersion { get => throw null; init { } }
        public required int CodegenVersion { get => throw null; init { } }
        public required string ModuleContentId { get => throw null; init { } }
        public required string ModuleChecksum { get => throw null; init { } }
        public required string OptionsFingerprint { get => throw null; init { } }
        public required bool EmitPortablePdb { get => throw null; init { } }
        public required int MaximumCanonicalInstructionsPerMethod { get => throw null; init { } }
        public required int MaximumMethodBodyBytes { get => throw null; init { } }
        public required int MaximumMetadataTokens { get => throw null; init { } }
        public required int MaximumBranchInstructionsPerMethod { get => throw null; init { } }
        public required string AssemblyName { get => throw null; init { } }
        public required string TypeName { get => throw null; init { } }
        public required string PortablePdbName { get => throw null; init { } }
        public required string SourceDocumentName { get => throw null; init { } }
        public required string SourceDocumentChecksum { get => throw null; init { } }
        public required System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Artifacts.LuaAotFunctionManifest> Functions { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Artifacts.LuaAotArtifactManifest? left, Lunil.CodeGen.Cil.Artifacts.LuaAotArtifactManifest? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Artifacts.LuaAotArtifactManifest? left, Lunil.CodeGen.Cil.Artifacts.LuaAotArtifactManifest? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Artifacts.LuaAotArtifactManifest? other) => throw null;
    }

    public sealed class LuaAotCompilationOptions : System.IEquatable<Lunil.CodeGen.Cil.Artifacts.LuaAotCompilationOptions>
    {
        public static Lunil.CodeGen.Cil.Artifacts.LuaAotCompilationOptions Default { get => throw null; }
        public bool EmitPortablePdb { get => throw null; init { } }
        public int MaximumCanonicalInstructionsPerMethod { get => throw null; init { } }
        public int MaximumMethodBodyBytes { get => throw null; init { } }
        public int MaximumMetadataTokens { get => throw null; init { } }
        public int MaximumBranchInstructionsPerMethod { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Artifacts.LuaAotSourceDocument? SourceDocument { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Artifacts.LuaAotCompilationOptions? left, Lunil.CodeGen.Cil.Artifacts.LuaAotCompilationOptions? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Artifacts.LuaAotCompilationOptions? left, Lunil.CodeGen.Cil.Artifacts.LuaAotCompilationOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Artifacts.LuaAotCompilationOptions? other) => throw null;
    }

    public sealed class LuaAotCompilationResult : System.IEquatable<Lunil.CodeGen.Cil.Artifacts.LuaAotCompilationResult>
    {
        public Lunil.CodeGen.Cil.Artifacts.LuaAotArtifact? Artifact { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Artifacts.LuaAotDiagnostic> Diagnostics { get => throw null; init { } }
        public bool Succeeded { get => throw null; }
        public LuaAotCompilationResult(Lunil.CodeGen.Cil.Artifacts.LuaAotArtifact? Artifact, System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Artifacts.LuaAotDiagnostic> Diagnostics) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Artifacts.LuaAotCompilationResult? left, Lunil.CodeGen.Cil.Artifacts.LuaAotCompilationResult? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Artifacts.LuaAotCompilationResult? left, Lunil.CodeGen.Cil.Artifacts.LuaAotCompilationResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Artifacts.LuaAotCompilationResult? other) => throw null;
        public void Deconstruct(out Lunil.CodeGen.Cil.Artifacts.LuaAotArtifact? Artifact, out System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Artifacts.LuaAotDiagnostic> Diagnostics) => throw null;
    }

    public sealed class LuaAotDiagnostic : System.IEquatable<Lunil.CodeGen.Cil.Artifacts.LuaAotDiagnostic>
    {
        public string Code { get => throw null; init { } }
        public string Message { get => throw null; init { } }
        public int FunctionId { get => throw null; init { } }
        public int CanonicalProgramCounter { get => throw null; init { } }
        public LuaAotDiagnostic(string Code, string Message, int FunctionId = -1, int CanonicalProgramCounter = -1) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Artifacts.LuaAotDiagnostic? left, Lunil.CodeGen.Cil.Artifacts.LuaAotDiagnostic? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Artifacts.LuaAotDiagnostic? left, Lunil.CodeGen.Cil.Artifacts.LuaAotDiagnostic? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Artifacts.LuaAotDiagnostic? other) => throw null;
        public void Deconstruct(out string Code, out string Message, out int FunctionId, out int CanonicalProgramCounter) => throw null;
    }

    public sealed class LuaAotFunctionManifest : System.IEquatable<Lunil.CodeGen.Cil.Artifacts.LuaAotFunctionManifest>
    {
        public int FunctionId { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Artifacts.LuaAotMethodShardManifest> Shards { get => throw null; init { } }
        public LuaAotFunctionManifest(int FunctionId, System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Artifacts.LuaAotMethodShardManifest> Shards) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Artifacts.LuaAotFunctionManifest? left, Lunil.CodeGen.Cil.Artifacts.LuaAotFunctionManifest? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Artifacts.LuaAotFunctionManifest? left, Lunil.CodeGen.Cil.Artifacts.LuaAotFunctionManifest? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Artifacts.LuaAotFunctionManifest? other) => throw null;
        public void Deconstruct(out int FunctionId, out System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Artifacts.LuaAotMethodShardManifest> Shards) => throw null;
    }

    public sealed class LuaAotMethodShardManifest : System.IEquatable<Lunil.CodeGen.Cil.Artifacts.LuaAotMethodShardManifest>
    {
        public string MethodName { get => throw null; init { } }
        public int StartProgramCounter { get => throw null; init { } }
        public int InstructionCount { get => throw null; init { } }
        public LuaAotMethodShardManifest(string MethodName, int StartProgramCounter, int InstructionCount) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Artifacts.LuaAotMethodShardManifest? left, Lunil.CodeGen.Cil.Artifacts.LuaAotMethodShardManifest? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Artifacts.LuaAotMethodShardManifest? left, Lunil.CodeGen.Cil.Artifacts.LuaAotMethodShardManifest? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Artifacts.LuaAotMethodShardManifest? other) => throw null;
        public void Deconstruct(out string MethodName, out int StartProgramCounter, out int InstructionCount) => throw null;
    }

    public static class LuaAotPortablePdbMetadata
    {
        public static System.Guid ProgramCounterMapKind { get => throw null; }
        public static System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Artifacts.LuaAotProgramCounterMapEntry> DecodeProgramCounterMap(System.ReadOnlySpan<byte> content) => throw null;
    }

    public readonly struct LuaAotProgramCounterMapEntry : System.IEquatable<Lunil.CodeGen.Cil.Artifacts.LuaAotProgramCounterMapEntry>
    {
        public int IlOffset { get => throw null; init { } }
        public int CanonicalProgramCounter { get => throw null; init { } }
        public int LogicalProgramCounter { get => throw null; init { } }
        public LuaAotProgramCounterMapEntry(int IlOffset, int CanonicalProgramCounter, int LogicalProgramCounter) { }
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.CodeGen.Cil.Artifacts.LuaAotProgramCounterMapEntry left, Lunil.CodeGen.Cil.Artifacts.LuaAotProgramCounterMapEntry right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Artifacts.LuaAotProgramCounterMapEntry left, Lunil.CodeGen.Cil.Artifacts.LuaAotProgramCounterMapEntry right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.CodeGen.Cil.Artifacts.LuaAotProgramCounterMapEntry other) => throw null;
        public void Deconstruct(out int IlOffset, out int CanonicalProgramCounter, out int LogicalProgramCounter) => throw null;
    }

    public sealed class LuaAotSourceDocument : System.IEquatable<Lunil.CodeGen.Cil.Artifacts.LuaAotSourceDocument>
    {
        public required string LogicalName { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<byte> Content { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Artifacts.LuaAotSourceDocument? left, Lunil.CodeGen.Cil.Artifacts.LuaAotSourceDocument? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Artifacts.LuaAotSourceDocument? left, Lunil.CodeGen.Cil.Artifacts.LuaAotSourceDocument? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Artifacts.LuaAotSourceDocument? other) => throw null;
    }
}
namespace Lunil.CodeGen.Cil.Caching
{
    public enum LuaBackendCacheArtifactKind
    {
        CanonicalIr = 0,
        PersistedCil = 1,
        Profile = 2
    }

    public readonly struct LuaBackendCacheCompatibility : System.IEquatable<Lunil.CodeGen.Cil.Caching.LuaBackendCacheCompatibility>
    {
        public Lunil.CodeGen.Cil.Caching.LuaBackendCacheMismatch Mismatches { get => throw null; init { } }
        public bool IsCompatible { get => throw null; }
        public LuaBackendCacheCompatibility(Lunil.CodeGen.Cil.Caching.LuaBackendCacheMismatch Mismatches) { }
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.CodeGen.Cil.Caching.LuaBackendCacheCompatibility left, Lunil.CodeGen.Cil.Caching.LuaBackendCacheCompatibility right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Caching.LuaBackendCacheCompatibility left, Lunil.CodeGen.Cil.Caching.LuaBackendCacheCompatibility right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.CodeGen.Cil.Caching.LuaBackendCacheCompatibility other) => throw null;
        public void Deconstruct(out Lunil.CodeGen.Cil.Caching.LuaBackendCacheMismatch Mismatches) => throw null;
    }

    public static class LuaBackendCacheDiagnosticCodes
    {
        public const string Unavailable = "CACHE1001";
        public const string CorruptEntry = "CACHE1002";
        public const string IncompatibleEntry = "CACHE1003";
        public const string EntryTooLarge = "CACHE1004";
    }

    public sealed class LuaBackendCacheKey
    {
        public const int CurrentCacheKeySchemaVersion = 1;
        public const int CurrentProfileSchemaVersion = 1;
        public const string EmptyContentSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        public string CacheId { get => throw null; }
        public Lunil.CodeGen.Cil.Caching.LuaBackendCacheArtifactKind ArtifactKind { get => throw null; }
        public string SourceContentHash { get => throw null; }
        public string CanonicalModuleHash { get => throw null; }
        public string DependencyHash { get => throw null; }
        public string SourceBindingId { get => throw null; }
        public int CacheKeySchemaVersion { get => throw null; }
        public int IrFormatVersion { get => throw null; }
        public int RuntimeAbiVersion { get => throw null; }
        public int CodegenVersion { get => throw null; }
        public int ProfileSchemaVersion { get => throw null; }
        public int ArtifactSchemaVersion { get => throw null; }
        public string CompilerVersion { get => throw null; }
        public Lunil.CodeGen.Cil.Caching.LuaBackendOptimizationMode Optimization { get => throw null; }
        public bool DebugSymbols { get => throw null; }
        public Lunil.CodeGen.Cil.Caching.LuaBackendHookMode HookMode { get => throw null; }
        public Lunil.CodeGen.Cil.Caching.LuaBackendSandboxMode SandboxMode { get => throw null; }
        public string TargetFramework { get => throw null; }
        public string RuntimeIdentifier { get => throw null; }
        public Lunil.CodeGen.Cil.Caching.LuaBackendDeploymentMode DeploymentMode { get => throw null; }
        public Lunil.CodeGen.Cil.Caching.LuaBackendTrimmingMode TrimmingMode { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<string> FeatureSet { get => throw null; }
        public static Lunil.CodeGen.Cil.Caching.LuaBackendCacheKey Create(Lunil.CodeGen.Cil.Caching.LuaBackendCacheKeyParameters parameters) => throw null;
        public static string ComputeContentHash(System.ReadOnlySpan<byte> content) => throw null;
        public static string ComputeDependencyHash(System.Collections.Generic.IEnumerable<string> contentHashes) => throw null;
        public static Lunil.CodeGen.Cil.Caching.LuaBackendCacheKey ParseCanonicalDescriptor(System.ReadOnlySpan<byte> descriptor) => throw null;
        public byte[] SerializeCanonicalDescriptor() => throw null;
        public Lunil.CodeGen.Cil.Caching.LuaBackendCacheCompatibility GetCompatibility(Lunil.CodeGen.Cil.Caching.LuaBackendCacheKey candidate) => throw null;
    }

    public sealed class LuaBackendCacheKeyParameters : System.IEquatable<Lunil.CodeGen.Cil.Caching.LuaBackendCacheKeyParameters>
    {
        public required Lunil.CodeGen.Cil.Caching.LuaBackendCacheArtifactKind ArtifactKind { get => throw null; init { } }
        public required string SourceContentHash { get => throw null; init { } }
        public required string CanonicalModuleHash { get => throw null; init { } }
        public string DependencyHash { get => throw null; init { } }
        public required string SourceBindingId { get => throw null; init { } }
        public int CacheKeySchemaVersion { get => throw null; init { } }
        public int IrFormatVersion { get => throw null; init { } }
        public int RuntimeAbiVersion { get => throw null; init { } }
        public int CodegenVersion { get => throw null; init { } }
        public int ProfileSchemaVersion { get => throw null; init { } }
        public int ArtifactSchemaVersion { get => throw null; init { } }
        public required string CompilerVersion { get => throw null; init { } }
        public required Lunil.CodeGen.Cil.Caching.LuaBackendOptimizationMode Optimization { get => throw null; init { } }
        public required bool DebugSymbols { get => throw null; init { } }
        public required Lunil.CodeGen.Cil.Caching.LuaBackendHookMode HookMode { get => throw null; init { } }
        public required Lunil.CodeGen.Cil.Caching.LuaBackendSandboxMode SandboxMode { get => throw null; init { } }
        public required string TargetFramework { get => throw null; init { } }
        public required string RuntimeIdentifier { get => throw null; init { } }
        public required Lunil.CodeGen.Cil.Caching.LuaBackendDeploymentMode DeploymentMode { get => throw null; init { } }
        public required Lunil.CodeGen.Cil.Caching.LuaBackendTrimmingMode TrimmingMode { get => throw null; init { } }
        public System.Collections.Generic.IReadOnlyCollection<string> FeatureSet { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Caching.LuaBackendCacheKeyParameters? left, Lunil.CodeGen.Cil.Caching.LuaBackendCacheKeyParameters? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Caching.LuaBackendCacheKeyParameters? left, Lunil.CodeGen.Cil.Caching.LuaBackendCacheKeyParameters? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Caching.LuaBackendCacheKeyParameters? other) => throw null;
    }

    [System.Flags]
    public enum LuaBackendCacheMismatch
    {
        None = 0,
        CacheKeySchema = 1,
        ArtifactKind = 2,
        SourceContent = 4,
        CanonicalModule = 8,
        Dependencies = 16,
        SourceBinding = 32,
        IrFormat = 64,
        RuntimeAbi = 128,
        Codegen = 256,
        ProfileSchema = 512,
        ArtifactSchema = 1024,
        CompilerVersion = 2048,
        Optimization = 4096,
        DebugSymbols = 8192,
        HookMode = 16384,
        SandboxMode = 32768,
        TargetFramework = 65536,
        RuntimeIdentifier = 131072,
        DeploymentMode = 262144,
        TrimmingMode = 524288,
        FeatureSet = 1048576
    }

    public sealed class LuaBackendCacheReadResult : System.IEquatable<Lunil.CodeGen.Cil.Caching.LuaBackendCacheReadResult>
    {
        public Lunil.CodeGen.Cil.Caching.LuaBackendCacheReadStatus Status { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<byte> Payload { get => throw null; init { } }
        public string? DiagnosticCode { get => throw null; init { } }
        public bool IsHit { get => throw null; }
        public LuaBackendCacheReadResult(Lunil.CodeGen.Cil.Caching.LuaBackendCacheReadStatus Status, System.Collections.Immutable.ImmutableArray<byte> Payload, string? DiagnosticCode = null) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Caching.LuaBackendCacheReadResult? left, Lunil.CodeGen.Cil.Caching.LuaBackendCacheReadResult? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Caching.LuaBackendCacheReadResult? left, Lunil.CodeGen.Cil.Caching.LuaBackendCacheReadResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Caching.LuaBackendCacheReadResult? other) => throw null;
        public void Deconstruct(out Lunil.CodeGen.Cil.Caching.LuaBackendCacheReadStatus Status, out System.Collections.Immutable.ImmutableArray<byte> Payload, out string? DiagnosticCode) => throw null;
    }

    public enum LuaBackendCacheReadStatus
    {
        Miss = 0,
        Hit = 1,
        CorruptMiss = 2,
        Unavailable = 3
    }

    public sealed class LuaBackendCacheTrimResult : System.IEquatable<Lunil.CodeGen.Cil.Caching.LuaBackendCacheTrimResult>
    {
        public bool Succeeded { get => throw null; init { } }
        public int RemovedEntries { get => throw null; init { } }
        public long RemovedBytes { get => throw null; init { } }
        public long RemainingBytes { get => throw null; init { } }
        public string? DiagnosticCode { get => throw null; init { } }
        public LuaBackendCacheTrimResult(bool Succeeded, int RemovedEntries, long RemovedBytes, long RemainingBytes, string? DiagnosticCode = null) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Caching.LuaBackendCacheTrimResult? left, Lunil.CodeGen.Cil.Caching.LuaBackendCacheTrimResult? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Caching.LuaBackendCacheTrimResult? left, Lunil.CodeGen.Cil.Caching.LuaBackendCacheTrimResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Caching.LuaBackendCacheTrimResult? other) => throw null;
        public void Deconstruct(out bool Succeeded, out int RemovedEntries, out long RemovedBytes, out long RemainingBytes, out string? DiagnosticCode) => throw null;
    }

    public sealed class LuaBackendCacheWriteResult : System.IEquatable<Lunil.CodeGen.Cil.Caching.LuaBackendCacheWriteResult>
    {
        public Lunil.CodeGen.Cil.Caching.LuaBackendCacheWriteStatus Status { get => throw null; init { } }
        public int TrimmedEntries { get => throw null; init { } }
        public string? DiagnosticCode { get => throw null; init { } }
        public LuaBackendCacheWriteResult(Lunil.CodeGen.Cil.Caching.LuaBackendCacheWriteStatus Status, int TrimmedEntries = 0, string? DiagnosticCode = null) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Caching.LuaBackendCacheWriteResult? left, Lunil.CodeGen.Cil.Caching.LuaBackendCacheWriteResult? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Caching.LuaBackendCacheWriteResult? left, Lunil.CodeGen.Cil.Caching.LuaBackendCacheWriteResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Caching.LuaBackendCacheWriteResult? other) => throw null;
        public void Deconstruct(out Lunil.CodeGen.Cil.Caching.LuaBackendCacheWriteStatus Status, out int TrimmedEntries, out string? DiagnosticCode) => throw null;
    }

    public enum LuaBackendCacheWriteStatus
    {
        Created = 0,
        AlreadyPresent = 1,
        RejectedTooLarge = 2,
        Unavailable = 3
    }

    public enum LuaBackendDeploymentMode
    {
        Portable = 0,
        CoreClr = 1,
        ReadyToRun = 2,
        NativeAot = 3
    }

    public sealed class LuaBackendDiskCache
    {
        public LuaBackendDiskCache(Lunil.CodeGen.Cil.Caching.LuaBackendDiskCacheOptions options) { }
        public System.Threading.Tasks.ValueTask<Lunil.CodeGen.Cil.Caching.LuaBackendCacheReadResult> TryReadAsync(Lunil.CodeGen.Cil.Caching.LuaBackendCacheKey key, System.Threading.CancellationToken cancellationToken = null) => throw null;
        public System.Threading.Tasks.ValueTask<Lunil.CodeGen.Cil.Caching.LuaBackendCacheWriteResult> WriteAsync(Lunil.CodeGen.Cil.Caching.LuaBackendCacheKey key, System.ReadOnlyMemory<byte> payload, System.Threading.CancellationToken cancellationToken = null) => throw null;
        public System.Threading.Tasks.ValueTask<bool> QuarantineAsync(Lunil.CodeGen.Cil.Caching.LuaBackendCacheKey key, string reason, System.Threading.CancellationToken cancellationToken = null) => throw null;
        public System.Threading.Tasks.ValueTask<Lunil.CodeGen.Cil.Caching.LuaBackendCacheTrimResult> TrimAsync(System.Threading.CancellationToken cancellationToken = null) => throw null;
    }

    public sealed class LuaBackendDiskCacheOptions : System.IEquatable<Lunil.CodeGen.Cil.Caching.LuaBackendDiskCacheOptions>
    {
        public required string RootDirectory { get => throw null; init { } }
        public long MaximumBytes { get => throw null; init { } }
        public long MaximumEntryBytes { get => throw null; init { } }
        public long MaximumQuarantineBytes { get => throw null; init { } }
        public System.TimeSpan LockTimeout { get => throw null; init { } }
        public System.TimeSpan LockRetryDelay { get => throw null; init { } }
        public System.TimeSpan OrphanTemporaryEntryAge { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Caching.LuaBackendDiskCacheOptions? left, Lunil.CodeGen.Cil.Caching.LuaBackendDiskCacheOptions? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Caching.LuaBackendDiskCacheOptions? left, Lunil.CodeGen.Cil.Caching.LuaBackendDiskCacheOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Caching.LuaBackendDiskCacheOptions? other) => throw null;
    }

    public enum LuaBackendHookMode
    {
        Disabled = 0,
        Exact = 1
    }

    public enum LuaBackendOptimizationMode
    {
        Debug = 0,
        Release = 1
    }

    public enum LuaBackendSandboxMode
    {
        Default = 0,
        Trusted = 1,
        Restricted = 2
    }

    public enum LuaBackendTrimmingMode
    {
        Disabled = 0,
        Enabled = 1
    }
}
namespace Lunil.CodeGen.Cil.Emission
{
    public enum CilEmitterFlavor
    {
        ReflectionEmit = 0,
        Metadata = 1
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

    public sealed class MetadataCilPlanSink : Lunil.CodeGen.Cil.Emission.ICilInstructionSink
    {
        public Lunil.CodeGen.Cil.Emission.CilEmitterFlavor Flavor { get => throw null; }
        public Lunil.CodeGen.Cil.Emission.MetadataCilRecipe? Recipe { get => throw null; }
        public static System.ValueTuple<Lunil.CodeGen.Cil.Emission.MetadataCilRecipe?, Lunil.CodeGen.Cil.Planning.CilPlanVerificationResult> CreateRecipe(Lunil.CodeGen.Cil.Planning.CilMethodPlan plan, Lunil.CodeGen.Cil.Planning.CilPlanLimits? limits = null) => throw null;
        public void BeginMethod(Lunil.CodeGen.Cil.Planning.CilMethodPlan plan, int maximumEvaluationStack) { }
        public void DeclareLocal(Lunil.CodeGen.Cil.Planning.CilLocal local) { }
        public void Emit(Lunil.CodeGen.Cil.Planning.CilPlanInstruction instruction) { }
        public void EndMethod() { }
    }

    public sealed class MetadataCilRecipe : System.IEquatable<Lunil.CodeGen.Cil.Emission.MetadataCilRecipe>
    {
        public string MethodName { get => throw null; init { } }
        public int MaximumEvaluationStack { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilLocal> Locals { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilPlanInstruction> Instructions { get => throw null; init { } }
        public MetadataCilRecipe(string MethodName, int MaximumEvaluationStack, System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilLocal> Locals, System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilPlanInstruction> Instructions) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Emission.MetadataCilRecipe? left, Lunil.CodeGen.Cil.Emission.MetadataCilRecipe? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Emission.MetadataCilRecipe? left, Lunil.CodeGen.Cil.Emission.MetadataCilRecipe? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Emission.MetadataCilRecipe? other) => throw null;
        public void Deconstruct(out string MethodName, out int MaximumEvaluationStack, out System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilLocal> Locals, out System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilPlanInstruction> Instructions) => throw null;
    }

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
        ExactNumericSpecializedCil = 1
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
        UnsupportedInstruction = 5
    }

    public sealed class LuaJitTier2Plan : System.IEquatable<Lunil.CodeGen.Cil.Jit.LuaJitTier2Plan>
    {
        public int FunctionId { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Jit.LuaJitOptimization> Optimizations { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Jit.LuaJitDeoptMapEntry> DeoptMap { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Jit.LuaJitTier2CodeKind CodeKind { get => throw null; init { } }
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
namespace Lunil.CodeGen.Cil.Loading
{
    public static class LuaAotArtifactLoader
    {
        public static Lunil.CodeGen.Cil.Loading.LuaAotValidationResult Validate(Lunil.CodeGen.Cil.Artifacts.LuaAotArtifact artifact, Lunil.CodeGen.Cil.Loading.LuaAotLoadOptions? options = null) => throw null;
        public static Lunil.CodeGen.Cil.Loading.LuaAotValidationResult Validate(System.Collections.Immutable.ImmutableArray<byte> peImage, System.Collections.Immutable.ImmutableArray<byte> portablePdbImage = null, Lunil.CodeGen.Cil.Loading.LuaAotLoadOptions? options = null) => throw null;
        [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Loading a persisted CIL assembly requires dynamic code support.")]
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Dynamically loaded persisted CIL methods cannot be statically analyzed.")]
        public static Lunil.CodeGen.Cil.Loading.LuaAotLoadResult Load(Lunil.CodeGen.Cil.Artifacts.LuaAotArtifact artifact, Lunil.CodeGen.Cil.Loading.LuaAotLoadOptions? options = null) => throw null;
        [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("Loading a persisted CIL assembly requires dynamic code support.")]
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("Dynamically loaded persisted CIL methods cannot be statically analyzed.")]
        public static Lunil.CodeGen.Cil.Loading.LuaAotLoadResult Load(System.Collections.Immutable.ImmutableArray<byte> peImage, System.Collections.Immutable.ImmutableArray<byte> portablePdbImage = null, Lunil.CodeGen.Cil.Loading.LuaAotLoadOptions? options = null) => throw null;
    }

    public readonly struct LuaAotLoadMetrics : System.IEquatable<Lunil.CodeGen.Cil.Loading.LuaAotLoadMetrics>
    {
        public System.TimeSpan ValidationDuration { get => throw null; init { } }
        public System.TimeSpan AssemblyLoadDuration { get => throw null; init { } }
        public System.TimeSpan DelegateBindingDuration { get => throw null; init { } }
        public System.TimeSpan TotalDuration { get => throw null; init { } }
        public long AllocatedBytes { get => throw null; init { } }
        public LuaAotLoadMetrics(System.TimeSpan ValidationDuration, System.TimeSpan AssemblyLoadDuration, System.TimeSpan DelegateBindingDuration, System.TimeSpan TotalDuration, long AllocatedBytes) { }
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.CodeGen.Cil.Loading.LuaAotLoadMetrics left, Lunil.CodeGen.Cil.Loading.LuaAotLoadMetrics right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Loading.LuaAotLoadMetrics left, Lunil.CodeGen.Cil.Loading.LuaAotLoadMetrics right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.CodeGen.Cil.Loading.LuaAotLoadMetrics other) => throw null;
        public void Deconstruct(out System.TimeSpan ValidationDuration, out System.TimeSpan AssemblyLoadDuration, out System.TimeSpan DelegateBindingDuration, out System.TimeSpan TotalDuration, out long AllocatedBytes) => throw null;
    }

    public sealed class LuaAotLoadOptions : System.IEquatable<Lunil.CodeGen.Cil.Loading.LuaAotLoadOptions>
    {
        public static Lunil.CodeGen.Cil.Loading.LuaAotLoadOptions Default { get => throw null; }
        public string? ExpectedModuleContentId { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Loading.LuaAotLoadOptions? left, Lunil.CodeGen.Cil.Loading.LuaAotLoadOptions? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Loading.LuaAotLoadOptions? left, Lunil.CodeGen.Cil.Loading.LuaAotLoadOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Loading.LuaAotLoadOptions? other) => throw null;
    }

    public sealed class LuaAotLoadResult : System.IEquatable<Lunil.CodeGen.Cil.Loading.LuaAotLoadResult>
    {
        public Lunil.CodeGen.Cil.Loading.LuaAotLoadedModule? Module { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Artifacts.LuaAotDiagnostic> Diagnostics { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Loading.LuaAotLoadMetrics Metrics { get => throw null; init { } }
        public bool Succeeded { get => throw null; }
        public LuaAotLoadResult(Lunil.CodeGen.Cil.Loading.LuaAotLoadedModule? Module, System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Artifacts.LuaAotDiagnostic> Diagnostics, Lunil.CodeGen.Cil.Loading.LuaAotLoadMetrics Metrics = null) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Loading.LuaAotLoadResult? left, Lunil.CodeGen.Cil.Loading.LuaAotLoadResult? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Loading.LuaAotLoadResult? left, Lunil.CodeGen.Cil.Loading.LuaAotLoadResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Loading.LuaAotLoadResult? other) => throw null;
        public void Deconstruct(out Lunil.CodeGen.Cil.Loading.LuaAotLoadedModule? Module, out System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Artifacts.LuaAotDiagnostic> Diagnostics, out Lunil.CodeGen.Cil.Loading.LuaAotLoadMetrics Metrics) => throw null;
    }

    public sealed class LuaAotLoadedModule : System.IDisposable
    {
        public Lunil.CodeGen.Cil.Artifacts.LuaAotArtifactManifest Manifest { get => throw null; }
        public System.WeakReference LoadContextWeakReference { get => throw null; }
        public bool IsDisposed { get => throw null; }
        public Lunil.CodeGen.Cil.Emission.LuaCompiledMethod GetFunction(int functionId) => throw null;
        public bool TryGetFunction(int functionId, out Lunil.CodeGen.Cil.Emission.LuaCompiledMethod? function) => throw null;
        public void Dispose() { }
    }

    public sealed class LuaAotValidationResult : System.IEquatable<Lunil.CodeGen.Cil.Loading.LuaAotValidationResult>
    {
        public Lunil.CodeGen.Cil.Artifacts.LuaAotArtifactManifest? Manifest { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Artifacts.LuaAotDiagnostic> Diagnostics { get => throw null; init { } }
        public bool Succeeded { get => throw null; }
        public LuaAotValidationResult(Lunil.CodeGen.Cil.Artifacts.LuaAotArtifactManifest? Manifest, System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Artifacts.LuaAotDiagnostic> Diagnostics) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.CodeGen.Cil.Loading.LuaAotValidationResult? left, Lunil.CodeGen.Cil.Loading.LuaAotValidationResult? right) => throw null;
        public static bool operator ==(Lunil.CodeGen.Cil.Loading.LuaAotValidationResult? left, Lunil.CodeGen.Cil.Loading.LuaAotValidationResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.CodeGen.Cil.Loading.LuaAotValidationResult? other) => throw null;
        public void Deconstruct(out Lunil.CodeGen.Cil.Artifacts.LuaAotArtifactManifest? Manifest, out System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Artifacts.LuaAotDiagnostic> Diagnostics) => throw null;
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
        public Lunil.CodeGen.Cil.Planning.CilLabel Label { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<Lunil.CodeGen.Cil.Planning.CilLabel> Labels { get => throw null; }
        public Lunil.CodeGen.Cil.Planning.CilCallTarget? CallTarget { get => throw null; }
        public int CanonicalProgramCounter { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilPlanInstruction MarkLabel(Lunil.CodeGen.Cil.Planning.CilLabel label, int canonicalProgramCounter = -1) => throw null;
        public static Lunil.CodeGen.Cil.Planning.CilPlanInstruction Simple(Lunil.CodeGen.Cil.Planning.CilPlanOpCode opCode, int canonicalProgramCounter = -1) => throw null;
        public static Lunil.CodeGen.Cil.Planning.CilPlanInstruction WithInt32(Lunil.CodeGen.Cil.Planning.CilPlanOpCode opCode, int operand, int canonicalProgramCounter = -1) => throw null;
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
        Add = 6,
        Subtract = 7,
        Call = 8,
        Branch = 9,
        BranchTrue = 10,
        BranchFalse = 11,
        Switch = 12,
        Return = 13
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
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget ExitPoll { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget ExitContinue { get => throw null; }
        public static Lunil.CodeGen.Cil.Planning.CilCallTarget ExitReturn { get => throw null; }
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
