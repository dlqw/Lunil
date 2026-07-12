using System.Collections.Immutable;

namespace Lunil.CodeGen.Cil.Artifacts;

public sealed record LuaAotSourceDocument
{
    public required string LogicalName { get; init; }

    public ImmutableArray<byte> Content { get; init; } = [];
}

public sealed record LuaAotCompilationOptions
{
    public static LuaAotCompilationOptions Default { get; } = new();

    public bool EmitPortablePdb { get; init; } = true;

    public int MaximumCanonicalInstructionsPerMethod { get; init; } = 4_096;

    public int MaximumMethodBodyBytes { get; init; } = 4 * 1024 * 1024;

    public int MaximumMetadataTokens { get; init; } = 500_000;

    public int MaximumBranchInstructionsPerMethod { get; init; } = 250_000;

    public LuaAotSourceDocument? SourceDocument { get; init; }
}

public sealed record LuaAotMethodShardManifest(
    string MethodName,
    int StartProgramCounter,
    int InstructionCount);

public sealed record LuaAotFunctionManifest(
    int FunctionId,
    ImmutableArray<LuaAotMethodShardManifest> Shards);

public sealed record LuaAotArtifactManifest
{
    public const string CurrentMagic = "LUNIL-CIL-AOT";
    public const int CurrentArtifactSchemaVersion = 1;
    public const int CurrentCodegenVersion = 1;

    public required string Magic { get; init; }

    public required int ArtifactSchemaVersion { get; init; }

    public required int IrFormatVersion { get; init; }

    public required int RuntimeAbiVersion { get; init; }

    public required int CodegenVersion { get; init; }

    public required string ModuleContentId { get; init; }

    public required string ModuleChecksum { get; init; }

    public required string OptionsFingerprint { get; init; }

    public required bool EmitPortablePdb { get; init; }

    public required int MaximumCanonicalInstructionsPerMethod { get; init; }

    public required int MaximumMethodBodyBytes { get; init; }

    public required int MaximumMetadataTokens { get; init; }

    public required int MaximumBranchInstructionsPerMethod { get; init; }

    public required string AssemblyName { get; init; }

    public required string TypeName { get; init; }

    public required string PortablePdbName { get; init; }

    public required string SourceDocumentName { get; init; }

    public required string SourceDocumentChecksum { get; init; }

    public required ImmutableArray<LuaAotFunctionManifest> Functions { get; init; }
}

public sealed record LuaAotArtifact(
    LuaAotArtifactManifest Manifest,
    ImmutableArray<byte> PeImage,
    ImmutableArray<byte> PortablePdbImage);

public sealed record LuaAotDiagnostic(
    string Code,
    string Message,
    int FunctionId = -1,
    int CanonicalProgramCounter = -1);

public sealed record LuaAotCompilationResult(
    LuaAotArtifact? Artifact,
    ImmutableArray<LuaAotDiagnostic> Diagnostics)
{
    public bool Succeeded => Artifact is not null && Diagnostics.IsEmpty;
}
