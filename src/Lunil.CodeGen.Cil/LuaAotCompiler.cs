using System.Collections.Immutable;
using Lunil.CodeGen.Cil.Analysis;
using Lunil.CodeGen.Cil.Artifacts;
using Lunil.CodeGen.Cil.Emission;
using Lunil.CodeGen.Cil.Planning;
using Lunil.CodeGen.Cil.Verification;
using Lunil.IR.Canonical;
using Lunil.Runtime.CodeGen;

namespace Lunil.CodeGen.Cil;

public static class LuaAotCompiler
{
    public static LuaAotCompilationResult Compile(
        LuaIrModule module,
        LuaAotCompilationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        options ??= LuaAotCompilationOptions.Default;
        var optionError = ValidateOptions(options);
        if (optionError is not null)
        {
            return new LuaAotCompilationResult(null, [optionError]);
        }

        var irErrors = LuaIrVerifier.Verify(module);
        if (!irErrors.IsEmpty)
        {
            return new LuaAotCompilationResult(
                null,
                [.. irErrors.Select(error => new LuaAotDiagnostic(
                    "AOT1001",
                    error.Message,
                    error.FunctionId,
                    error.ProgramCounter))]);
        }

        var moduleBytes = LuaCanonicalModuleSerializer.Serialize(module);
        var moduleChecksum = LuaCanonicalModuleSerializer.Sha256Hex(moduleBytes);
        var moduleContentId = moduleChecksum;
        var sourceBytes = options.SourceDocument is null
            ? moduleBytes
            : options.SourceDocument.Content.IsDefault
                ? []
                : options.SourceDocument.Content.ToArray();
        var sourceChecksum = LuaCanonicalModuleSerializer.Sha256Hex(sourceBytes);
        var sourceName = options.SourceDocument?.LogicalName ??
            $"lunil://{moduleContentId}/module.lua";
        var optionsFingerprint = LuaAotManifestCodec.FingerprintOptions(
            options,
            sourceChecksum,
            sourceName);
        var assemblyName = $"Lunil.Aot.{moduleContentId[..16]}.{optionsFingerprint[..8]}";
        var typeName = $"Lunil.Generated.Module_{moduleContentId[..16]}";

        var methods = new List<PersistedCilMethod>();
        var functionManifests = ImmutableArray.CreateBuilder<LuaAotFunctionManifest>();
        var diagnostics = ImmutableArray.CreateBuilder<LuaAotDiagnostic>();
        foreach (var function in module.Functions)
        {
            var shards = ImmutableArray.CreateBuilder<LuaAotMethodShardManifest>();
            var blockLayout = CilBlockLayout.Build(function);
            var liveness = LuaRegisterLiveness.AnalyzeCached(module, function, out _);
            var ranges = PartitionFunction(
                function,
                options.MaximumCanonicalInstructionsPerMethod);
            for (var shardIndex = 0; shardIndex < ranges.Length; shardIndex++)
            {
                var range = ranges[shardIndex];
                var methodName = ranges.Length == 1
                    ? $"Function_{function.Id}"
                    : $"Function_{function.Id}_Shard_{shardIndex}";
                var plan = LuaCilMethodPlanner.Build(
                    function,
                    range.StartProgramCounter,
                    range.InstructionCount,
                    methodName,
                    blockLayout,
                    liveness);
                var verification = CilMethodPlanVerifier.Verify(
                    plan,
                    CilPlanLimits.Default with
                    {
                        MaximumBranchInstructions =
                            options.MaximumBranchInstructionsPerMethod,
                    });
                if (!verification.Succeeded)
                {
                    diagnostics.AddRange(verification.Diagnostics.Select(diagnostic =>
                        new LuaAotDiagnostic(
                            diagnostic.Code,
                            diagnostic.Message,
                            function.Id,
                            diagnostic.CanonicalProgramCounter)));
                    continue;
                }

                methods.Add(new PersistedCilMethod(methodName, plan));
                shards.Add(new LuaAotMethodShardManifest(
                    methodName,
                    range.StartProgramCounter,
                    range.InstructionCount));
            }

            functionManifests.Add(new LuaAotFunctionManifest(
                function.Id,
                shards.ToImmutable()));
        }

        if (diagnostics.Count != 0)
        {
            return new LuaAotCompilationResult(null, diagnostics.ToImmutable());
        }

        var manifest = new LuaAotArtifactManifest
        {
            Magic = LuaAotArtifactManifest.CurrentMagic,
            ArtifactSchemaVersion = LuaAotArtifactManifest.CurrentArtifactSchemaVersion,
            IrFormatVersion = module.FormatVersion,
            RuntimeAbiVersion = LuaCodegenAbiV3.RuntimeAbiVersion,
            CodegenVersion = LuaAotArtifactManifest.CurrentCodegenVersion,
            ModuleContentId = moduleContentId,
            ModuleChecksum = moduleChecksum,
            OptionsFingerprint = optionsFingerprint,
            EmitPortablePdb = options.EmitPortablePdb,
            MaximumCanonicalInstructionsPerMethod =
                options.MaximumCanonicalInstructionsPerMethod,
            MaximumMethodBodyBytes = options.MaximumMethodBodyBytes,
            MaximumMetadataTokens = options.MaximumMetadataTokens,
            MaximumBranchInstructionsPerMethod =
                options.MaximumBranchInstructionsPerMethod,
            AssemblyName = assemblyName,
            TypeName = typeName,
            PortablePdbName = assemblyName + ".pdb",
            SourceDocumentName = sourceName,
            SourceDocumentChecksum = sourceChecksum,
            Functions = functionManifests.ToImmutable(),
        };
        var manifestBytes = LuaAotManifestCodec.Serialize(manifest);
        var emission = ManagedPeCilEmitter.Emit(
            manifest,
            manifestBytes,
            moduleBytes,
            sourceBytes,
            methods,
            options);
        if (!emission.Diagnostics.IsEmpty)
        {
            return new LuaAotCompilationResult(
                null,
                [.. emission.Diagnostics.Select(diagnostic => new LuaAotDiagnostic(
                    diagnostic.Code,
                    diagnostic.Message,
                    CanonicalProgramCounter: diagnostic.CanonicalProgramCounter))]);
        }

        return new LuaAotCompilationResult(
            new LuaAotArtifact(manifest, emission.PeImage, emission.PortablePdbImage),
            []);
    }

    private static LuaAotDiagnostic? ValidateOptions(LuaAotCompilationOptions options)
    {
        if (options.MaximumCanonicalInstructionsPerMethod <= 0)
        {
            return new LuaAotDiagnostic(
                "AOT1002",
                "MaximumCanonicalInstructionsPerMethod must be positive.");
        }

        if (options.MaximumMethodBodyBytes <= 0 || options.MaximumMetadataTokens <= 0 ||
            options.MaximumBranchInstructionsPerMethod <= 0)
        {
            return new LuaAotDiagnostic(
                "AOT1004",
                "AOT method body, metadata token, and branch limits must be positive.");
        }

        if (options.SourceDocument is not null &&
            string.IsNullOrEmpty(options.SourceDocument.LogicalName))
        {
            return new LuaAotDiagnostic(
                "AOT1003",
                "The logical source document name cannot be empty.");
        }

        return null;
    }

    private static ImmutableArray<MethodShardRange> PartitionFunction(
        LuaIrFunction function,
        int maximumInstructions)
    {
        var ranges = ImmutableArray.CreateBuilder<MethodShardRange>();
        var pendingStart = 0;
        var pendingLength = 0;
        foreach (var block in function.BasicBlocks)
        {
            var blockStart = block.Start;
            var blockLength = block.Length;
            if (blockLength > maximumInstructions)
            {
                if (pendingLength != 0)
                {
                    ranges.Add(new MethodShardRange(pendingStart, pendingLength));
                    pendingLength = 0;
                }

                for (var offset = 0; offset < blockLength; offset += maximumInstructions)
                {
                    ranges.Add(new MethodShardRange(
                        checked(blockStart + offset),
                        Math.Min(maximumInstructions, blockLength - offset)));
                }

                pendingStart = checked(blockStart + blockLength);
                continue;
            }

            if (pendingLength != 0 && pendingLength > maximumInstructions - blockLength)
            {
                ranges.Add(new MethodShardRange(pendingStart, pendingLength));
                pendingStart = blockStart;
                pendingLength = 0;
            }

            if (pendingLength == 0)
            {
                pendingStart = blockStart;
            }

            pendingLength = checked(pendingLength + blockLength);
        }

        if (pendingLength != 0)
        {
            ranges.Add(new MethodShardRange(pendingStart, pendingLength));
        }

        return ranges.ToImmutable();
    }

    private readonly record struct MethodShardRange(
        int StartProgramCounter,
        int InstructionCount);
}
