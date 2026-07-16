using System.Collections.Immutable;
using Lunil.CodeGen.Cil.Analysis;
using Lunil.CodeGen.Cil.Artifacts;
using Lunil.CodeGen.Cil.Emission;
using Lunil.CodeGen.Cil.Jit;
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
        byte[] profileBytes;
        Dictionary<int, LuaJitFunctionProfileEntry> profileByFunction;
        if (options.Profile is null)
        {
            profileBytes = [];
            profileByFunction = new Dictionary<int, LuaJitFunctionProfileEntry>();
        }
        else
        {
            if (!string.Equals(
                options.Profile.ModuleContentId,
                moduleContentId,
                StringComparison.Ordinal))
            {
                return new LuaAotCompilationResult(
                    null,
                    [new LuaAotDiagnostic(
                        "AOT1005",
                        "The persisted AOT profile belongs to a different canonical module.")]);
            }

            if (options.Profile.Functions.IsDefault ||
                options.Profile.Functions.Any(static entry =>
                    entry is null || entry.Profile is null))
            {
                return new LuaAotCompilationResult(
                    null,
                    [new LuaAotDiagnostic(
                        "AOT1006",
                        "The persisted AOT profile is malformed or incomplete.")]);
            }

            try
            {
                profileByFunction = options.Profile.Functions.ToDictionary(
                    static entry => entry.FunctionId);
                var orderedProfiles = module.Functions
                    .Select(function => profileByFunction[function.Id].Profile)
                    .ToArray();
                profileBytes = LuaJitProfileCodec.Serialize(module, orderedProfiles);
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or
                KeyNotFoundException)
            {
                return new LuaAotCompilationResult(
                    null,
                    [new LuaAotDiagnostic(
                        "AOT1006",
                        $"The persisted AOT profile is malformed or incompatible: " +
                        $"{exception.Message}")]);
            }
        }

        var profileFingerprint = LuaCanonicalModuleSerializer.Sha256Hex(profileBytes);
        var optionsFingerprint = LuaAotManifestCodec.FingerprintOptions(
            options,
            sourceChecksum,
            sourceName,
            profileFingerprint,
            options.Profile is not null);
        var assemblyName = $"Lunil.Aot.{moduleContentId[..16]}.{optionsFingerprint[..8]}";
        var typeName = $"Lunil.Generated.Module_{moduleContentId[..16]}";

        var methods = new List<PersistedCilMethod>();
        var numericMethods = new List<PersistedNumericCilMethod>();
        var functionManifests = ImmutableArray.CreateBuilder<LuaAotFunctionManifest>();
        var diagnostics = ImmutableArray.CreateBuilder<LuaAotDiagnostic>();
        foreach (var function in module.Functions)
        {
            var numericRegions = profileByFunction.TryGetValue(
                function.Id,
                out var functionProfile)
                ? ProfileGuidedLuaTier2Compiler.BuildNumericRegionPlans(
                    module,
                    function,
                    functionProfile.Profile,
                    CancellationToken.None)
                : [];
            var shards = ImmutableArray.CreateBuilder<LuaAotMethodShardManifest>();
            var blockLayout = CilBlockLayout.Build(function);
            var liveness = LuaRegisterLiveness.AnalyzeCached(module, function, out _);
            var ranges = PartitionFunction(
                function,
                options.MaximumCanonicalInstructionsPerMethod,
                numericRegions);
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

            var numericRegionManifests = ImmutableArray.CreateBuilder<
                LuaAotNumericRegionManifest>();
            for (var regionIndex = 0; regionIndex < numericRegions.Length; regionIndex++)
            {
                var numericRegion = numericRegions[regionIndex];
                var methodName = $"Function_{function.Id}_NumericRegion_{regionIndex}";
                numericMethods.Add(new PersistedNumericCilMethod(
                    methodName,
                    function,
                    numericRegion));
                numericRegionManifests.Add(new LuaAotNumericRegionManifest(
                    methodName,
                    numericRegion.Region.HeaderProgramCounter,
                    numericRegion.Region.BackedgeProgramCounter,
                    numericRegion.Region.ProgramCounters,
                    numericRegion.Registers.Count(static register => register.Kind is
                        LuaNumericRegionValueKind.Integer or
                        LuaNumericRegionValueKind.Float or
                        LuaNumericRegionValueKind.Boolean),
                    numericRegion.DirectNumericInstructionCount,
                    numericRegion.BackedgeProgramCounters.Length));
            }

            functionManifests.Add(new LuaAotFunctionManifest(
                function.Id,
                shards.ToImmutable())
            {
                NumericRegions = numericRegionManifests.ToImmutable(),
            });
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
            RuntimeAbiVersion = LuaCodegenAbiV4.RuntimeAbiVersion,
            CodegenVersion = LuaAotArtifactManifest.CurrentCodegenVersion,
            ModuleContentId = moduleContentId,
            ModuleChecksum = moduleChecksum,
            OptionsFingerprint = optionsFingerprint,
            ProfileGuidedNumericRegions = options.Profile is not null,
            ProfilePolicyVersion = LuaAotArtifactManifest.CurrentProfilePolicyVersion,
            ProfileFingerprint = profileFingerprint,
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
            numericMethods,
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
        int maximumInstructions,
        ImmutableArray<LuaNumericRegionPlan> numericRegions)
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

        if (numericRegions.IsEmpty || ranges.Count == 0)
        {
            return ranges.ToImmutable();
        }

        // A canonical shard can execute several instructions before returning to the shared
        // dispatcher. Split it whenever execution enters or leaves a profiled numeric region so
        // the dispatcher gets an opportunity to select the persisted specialized method. Keep
        // canonical coverage for every PC: a guard deoptimization still resumes precisely through
        // the interpreter, and the manifest remains a complete compatibility fallback map.
        var isNumericProgramCounter = new bool[function.Instructions.Length];
        foreach (var region in numericRegions)
        {
            foreach (var programCounter in region.Region.ProgramCounters)
            {
                isNumericProgramCounter[programCounter] = true;
            }
        }

        var boundaries = new SortedSet<int>();
        for (var programCounter = 1;
             programCounter < isNumericProgramCounter.Length;
             programCounter++)
        {
            if (isNumericProgramCounter[programCounter] !=
                isNumericProgramCounter[programCounter - 1])
            {
                boundaries.Add(programCounter);
            }
        }

        if (boundaries.Count == 0)
        {
            return ranges.ToImmutable();
        }

        var splitRanges = ImmutableArray.CreateBuilder<MethodShardRange>();
        foreach (var range in ranges)
        {
            var start = range.StartProgramCounter;
            var end = checked(start + range.InstructionCount);
            if (range.InstructionCount > 1)
            {
                foreach (var boundary in boundaries.GetViewBetween(start + 1, end - 1))
                {
                    splitRanges.Add(new MethodShardRange(start, boundary - start));
                    start = boundary;
                }
            }

            splitRanges.Add(new MethodShardRange(start, end - start));
        }

        return splitRanges.ToImmutable();
    }

    private readonly record struct MethodShardRange(
        int StartProgramCounter,
        int InstructionCount);
}
