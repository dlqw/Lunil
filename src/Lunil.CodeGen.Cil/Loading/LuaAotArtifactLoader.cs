using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Lunil.CodeGen.Cil.Artifacts;
using Lunil.CodeGen.Cil.Emission;
using Lunil.IR.Canonical;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Execution;

namespace Lunil.CodeGen.Cil.Loading;

public sealed record LuaAotLoadOptions
{
    public static LuaAotLoadOptions Default { get; } = new();

    public string? ExpectedModuleContentId { get; init; }
}

public sealed record LuaAotLoadResult(
    LuaAotLoadedModule? Module,
    ImmutableArray<LuaAotDiagnostic> Diagnostics,
    LuaAotLoadMetrics Metrics = default)
{
    public bool Succeeded => Module is not null && Diagnostics.IsEmpty;
}

public readonly record struct LuaAotLoadMetrics(
    TimeSpan ValidationDuration,
    TimeSpan AssemblyLoadDuration,
    TimeSpan DelegateBindingDuration,
    TimeSpan TotalDuration,
    long AllocatedBytes);

public sealed record LuaAotValidationResult(
    LuaAotArtifactManifest? Manifest,
    ImmutableArray<LuaAotDiagnostic> Diagnostics)
{
    public bool Succeeded => Manifest is not null && Diagnostics.IsEmpty;
}

public sealed class LuaAotLoadedModule : IDisposable
{
    private LuaAotLoadContext? _loadContext;
    private Dictionary<int, LuaCompiledMethod>? _functions;

    internal LuaAotLoadedModule(
        LuaAotArtifactManifest manifest,
        LuaAotLoadContext loadContext,
        Dictionary<int, LuaCompiledMethod> functions)
    {
        Manifest = manifest;
        _loadContext = loadContext;
        _functions = functions;
        LoadContextWeakReference = new WeakReference(loadContext);
    }

    public LuaAotArtifactManifest Manifest { get; }

    public WeakReference LoadContextWeakReference { get; }

    public bool IsDisposed => _loadContext is null;

    public LuaCompiledMethod GetFunction(int functionId)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return _functions!.TryGetValue(functionId, out var function)
            ? function
            : throw new ArgumentOutOfRangeException(
                nameof(functionId),
                "The AOT artifact does not contain the requested function.");
    }

    public bool TryGetFunction(int functionId, out LuaCompiledMethod? function)
    {
        if (_functions is null)
        {
            function = null;
            return false;
        }

        return _functions.TryGetValue(functionId, out function);
    }

    public void Dispose()
    {
        var context = Interlocked.Exchange(ref _loadContext, null);
        Interlocked.Exchange(ref _functions, null)?.Clear();
        context?.Unload();
    }
}

public static class LuaAotArtifactLoader
{
    private static readonly string EmptyProfileFingerprint =
        LuaCanonicalModuleSerializer.Sha256Hex([]);

    public static LuaAotValidationResult Validate(
        LuaAotArtifact artifact,
        LuaAotLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return Validate(artifact.PeImage, artifact.PortablePdbImage, options);
    }

    public static LuaAotValidationResult Validate(
        ImmutableArray<byte> peImage,
        ImmutableArray<byte> portablePdbImage = default,
        LuaAotLoadOptions? options = null)
    {
        options ??= LuaAotLoadOptions.Default;
        if (peImage.IsDefaultOrEmpty)
        {
            return ValidationFailure("AOT2001", "The AOT PE image is empty.");
        }

        var envelope = ReadArtifactEnvelope(peImage);
        if (envelope is null)
        {
            return ValidationFailure(
                "AOT2009",
                "The AOT PE checksum footer is missing or malformed.");
        }

        try
        {
            var resources = ReadEmbeddedResources(envelope.CoreImage);
            if (!resources.TryGetValue(
                    ManagedPeCilEmitter.ManifestResourceName,
                    out var manifestBytes) ||
                !resources.TryGetValue(
                    ManagedPeCilEmitter.ModuleResourceName,
                    out var moduleBytes))
            {
                return ValidationFailure(
                    "AOT2002",
                    "The AOT artifact is missing required embedded resources.");
            }

            var manifest = LuaAotManifestCodec.Deserialize(manifestBytes.AsSpan());
            var manifestValidation = ValidateManifest(manifest, moduleBytes, options);
            if (manifestValidation is not null)
            {
                return ValidationFailure(
                    manifestValidation.Code,
                    manifestValidation.Message);
            }

            if (!envelope.ChecksumMatches)
            {
                return ValidationFailure(
                    "AOT2009",
                    "The AOT PE image checksum does not match its footer.");
            }

            var pdbValidation = ValidatePortablePdb(
                envelope.CoreImage,
                portablePdbImage,
                manifest);
            return pdbValidation is null
                ? new LuaAotValidationResult(manifest, [])
                : ValidationFailure(pdbValidation.Code, pdbValidation.Message);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not AccessViolationException)
        {
            return ValidationFailure(
                "AOT2003",
                $"The AOT artifact was rejected: {exception.GetType().Name}: {exception.Message}");
        }
    }

    [RequiresDynamicCode("Loading a persisted CIL assembly requires dynamic code support.")]
    [RequiresUnreferencedCode("Dynamically loaded persisted CIL methods cannot be statically analyzed.")]
    public static LuaAotLoadResult Load(
        LuaAotArtifact artifact,
        LuaAotLoadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return Load(artifact.PeImage, artifact.PortablePdbImage, options);
    }

    [RequiresDynamicCode("Loading a persisted CIL assembly requires dynamic code support.")]
    [RequiresUnreferencedCode("Dynamically loaded persisted CIL methods cannot be statically analyzed.")]
    public static LuaAotLoadResult Load(
        ImmutableArray<byte> peImage,
        ImmutableArray<byte> portablePdbImage = default,
        LuaAotLoadOptions? options = null)
    {
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var totalStopwatch = Stopwatch.StartNew();
        var validationDuration = TimeSpan.Zero;
        var assemblyLoadDuration = TimeSpan.Zero;
        var delegateBindingDuration = TimeSpan.Zero;

        LuaAotLoadMetrics CompleteMetrics()
        {
            totalStopwatch.Stop();
            return new LuaAotLoadMetrics(
                validationDuration,
                assemblyLoadDuration,
                delegateBindingDuration,
                totalStopwatch.Elapsed,
                Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore));
        }

        options ??= LuaAotLoadOptions.Default;
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return Failure(
                "AOT2010",
                "Dynamic PE loading is unavailable. Register build-time artifacts through LuaStaticAotRegistry.",
                CompleteMetrics());
        }

        var validationStopwatch = Stopwatch.StartNew();
        var validation = Validate(peImage, portablePdbImage, options);
        validationStopwatch.Stop();
        validationDuration = validationStopwatch.Elapsed;
        if (!validation.Succeeded)
        {
            return new LuaAotLoadResult(
                null,
                validation.Diagnostics,
                CompleteMetrics());
        }

        var envelope = ReadArtifactEnvelope(peImage)!;
        var manifest = validation.Manifest!;
        LuaAotLoadContext? loadContext = null;
        try
        {
            loadContext = new LuaAotLoadContext(manifest.AssemblyName);
            using var peStream = new MemoryStream(envelope.CoreImage.ToArray(), writable: false);
            Assembly assembly;
            var assemblyLoadStopwatch = Stopwatch.StartNew();
            try
            {
                if (portablePdbImage.IsDefaultOrEmpty)
                {
                    assembly = loadContext.LoadFromStream(peStream);
                }
                else
                {
                    using var pdbStream = new MemoryStream(
                        portablePdbImage.ToArray(),
                        writable: false);
                    assembly = loadContext.LoadFromStream(peStream, pdbStream);
                }
            }
            finally
            {
                assemblyLoadStopwatch.Stop();
                assemblyLoadDuration = assemblyLoadStopwatch.Elapsed;
            }

            if (!string.Equals(
                assembly.GetName().Name,
                manifest.AssemblyName,
                StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The loaded assembly identity does not match the AOT manifest.");
            }

            var generatedType = assembly.GetType(
                manifest.TypeName,
                throwOnError: true,
                ignoreCase: false)!;
            var delegateBindingStopwatch = Stopwatch.StartNew();
            Dictionary<int, LuaCompiledMethod> functions;
            try
            {
                functions = BindFunctions(generatedType, manifest);
            }
            finally
            {
                delegateBindingStopwatch.Stop();
                delegateBindingDuration = delegateBindingStopwatch.Elapsed;
            }
            var module = new LuaAotLoadedModule(manifest, loadContext, functions);
            loadContext = null;
            return new LuaAotLoadResult(module, [], CompleteMetrics());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not AccessViolationException)
        {
            return Failure(
                "AOT2003",
                $"The AOT artifact was rejected: {exception.GetType().Name}: {exception.Message}",
                CompleteMetrics());
        }
        finally
        {
            loadContext?.Unload();
        }
    }

    private static Dictionary<string, ImmutableArray<byte>> ReadEmbeddedResources(
        ImmutableArray<byte> peImage)
    {
        using var stream = new MemoryStream(peImage.ToArray(), writable: false);
        using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
        if (!peReader.HasMetadata || peReader.PEHeaders.CorHeader is null)
        {
            throw new BadImageFormatException("The image is not a managed PE artifact.");
        }

        var metadata = peReader.GetMetadataReader();
        var resourceRva = peReader.PEHeaders.CorHeader.ResourcesDirectory.RelativeVirtualAddress;
        if (resourceRva == 0)
        {
            throw new BadImageFormatException("The managed PE has no resource directory.");
        }

        var result = new Dictionary<string, ImmutableArray<byte>>(StringComparer.Ordinal);
        foreach (var handle in metadata.ManifestResources)
        {
            var resource = metadata.GetManifestResource(handle);
            if (!resource.Implementation.IsNil)
            {
                continue;
            }

            var block = peReader.GetSectionData(checked(resourceRva + (int)resource.Offset));
            var reader = block.GetReader();
            var length = reader.ReadInt32();
            if (length < 0 || length > reader.RemainingBytes)
            {
                throw new BadImageFormatException("An embedded resource has an invalid length.");
            }

            var name = metadata.GetString(resource.Name);
            if (!result.TryAdd(name, reader.ReadBytes(length).ToImmutableArray()))
            {
                throw new BadImageFormatException("The artifact contains duplicate resource names.");
            }
        }

        return result;
    }

    private static ArtifactEnvelope? ReadArtifactEnvelope(ImmutableArray<byte> peImage)
    {
        const int checksumLength = 32;
        var footerLength = checked(
            ManagedPeCilEmitter.ArtifactChecksumMagic.Length + checksumLength);
        if (peImage.Length <= footerLength)
        {
            return null;
        }

        var coreLength = peImage.Length - footerLength;
        var footer = peImage.AsSpan(coreLength, footerLength);
        if (!footer[..ManagedPeCilEmitter.ArtifactChecksumMagic.Length]
            .SequenceEqual(ManagedPeCilEmitter.ArtifactChecksumMagic))
        {
            return null;
        }

        var core = peImage.AsSpan(0, coreLength).ToImmutableArray();
        var expectedChecksum = footer[ManagedPeCilEmitter.ArtifactChecksumMagic.Length..];
        return new ArtifactEnvelope(
            core,
            expectedChecksum.SequenceEqual(SHA256.HashData(core.AsSpan())));
    }

    private static LuaAotDiagnostic? ValidateManifest(
        LuaAotArtifactManifest manifest,
        ImmutableArray<byte> moduleBytes,
        LuaAotLoadOptions options)
    {
        if (manifest.Magic != LuaAotArtifactManifest.CurrentMagic ||
            manifest.ArtifactSchemaVersion != LuaAotArtifactManifest.CurrentArtifactSchemaVersion ||
            manifest.IrFormatVersion != LuaIrModule.CurrentFormatVersion ||
            manifest.RuntimeAbiVersion != LuaCodegenAbiV4.RuntimeAbiVersion ||
            manifest.CodegenVersion != LuaAotArtifactManifest.CurrentCodegenVersion)
        {
            return new LuaAotDiagnostic(
                "AOT2004",
                "The AOT artifact schema, IR format, Runtime ABI, or codegen version is incompatible.");
        }

        var checksum = Convert.ToHexStringLower(SHA256.HashData(moduleBytes.AsSpan()));
        if (!string.Equals(checksum, manifest.ModuleChecksum, StringComparison.Ordinal) ||
            !string.Equals(checksum, manifest.ModuleContentId, StringComparison.Ordinal))
        {
            return new LuaAotDiagnostic(
                "AOT2005",
                "The embedded canonical module checksum does not match the manifest.");
        }

        if (!IsSha256Hex(manifest.ModuleChecksum) ||
            !IsSha256Hex(manifest.ModuleContentId) ||
            !IsSha256Hex(manifest.OptionsFingerprint) ||
            !IsSha256Hex(manifest.ProfileFingerprint) ||
            (!manifest.ProfileGuidedNumericRegions && !string.Equals(
                manifest.ProfileFingerprint,
                EmptyProfileFingerprint,
                StringComparison.Ordinal)) ||
            !IsSha256Hex(manifest.SourceDocumentChecksum) ||
            manifest.ProfilePolicyVersion !=
                LuaAotArtifactManifest.CurrentProfilePolicyVersion ||
            string.IsNullOrEmpty(manifest.AssemblyName) ||
            string.IsNullOrEmpty(manifest.TypeName) ||
            string.IsNullOrEmpty(manifest.PortablePdbName) ||
            string.IsNullOrEmpty(manifest.SourceDocumentName))
        {
            return new LuaAotDiagnostic(
                "AOT2007",
                "The artifact identity or source binding is malformed.");
        }

        if (manifest.MaximumCanonicalInstructionsPerMethod <= 0 ||
            manifest.MaximumMethodBodyBytes <= 0 ||
            manifest.MaximumMetadataTokens <= 0 ||
            manifest.MaximumBranchInstructionsPerMethod <= 0)
        {
            return new LuaAotDiagnostic(
                "AOT2007",
                "The artifact compilation limits are malformed.");
        }

        var manifestOptions = new LuaAotCompilationOptions
        {
            EmitPortablePdb = manifest.EmitPortablePdb,
            MaximumCanonicalInstructionsPerMethod =
                manifest.MaximumCanonicalInstructionsPerMethod,
            MaximumMethodBodyBytes = manifest.MaximumMethodBodyBytes,
            MaximumMetadataTokens = manifest.MaximumMetadataTokens,
            MaximumBranchInstructionsPerMethod =
                manifest.MaximumBranchInstructionsPerMethod,
            SourceDocument = new LuaAotSourceDocument
            {
                LogicalName = manifest.SourceDocumentName,
            },
        };
        var expectedOptionsFingerprint = LuaAotManifestCodec.FingerprintOptions(
            manifestOptions,
            manifest.SourceDocumentChecksum,
            manifest.SourceDocumentName,
            manifest.ProfileFingerprint,
            manifest.ProfileGuidedNumericRegions);
        var expectedAssemblyName =
            $"Lunil.Aot.{manifest.ModuleContentId[..16]}.{manifest.OptionsFingerprint[..8]}";
        var expectedTypeName =
            $"Lunil.Generated.Module_{manifest.ModuleContentId[..16]}";
        if (!string.Equals(
                expectedOptionsFingerprint,
                manifest.OptionsFingerprint,
                StringComparison.Ordinal) ||
            !string.Equals(expectedAssemblyName, manifest.AssemblyName, StringComparison.Ordinal) ||
            !string.Equals(expectedTypeName, manifest.TypeName, StringComparison.Ordinal) ||
            !string.Equals(
                manifest.AssemblyName + ".pdb",
                manifest.PortablePdbName,
                StringComparison.Ordinal))
        {
            return new LuaAotDiagnostic(
                "AOT2007",
                "The artifact identity does not match its module and compilation options.");
        }

        var moduleSummary = LuaCanonicalModuleSerializer.ReadSummary(moduleBytes.AsSpan());
        if (moduleSummary.FormatVersion != manifest.IrFormatVersion ||
            moduleSummary.Functions.Length != manifest.Functions.Length ||
            !moduleSummary.Functions.Select(static function => function.FunctionId)
                .SequenceEqual(manifest.Functions.Select(static function => function.FunctionId)) ||
            !moduleSummary.Functions.Any(function =>
                function.FunctionId == moduleSummary.MainFunctionId))
        {
            return new LuaAotDiagnostic(
                "AOT2007",
                "The artifact function map does not match the embedded canonical module.");
        }

        if (options.ExpectedModuleContentId is not null && !string.Equals(
            options.ExpectedModuleContentId,
            manifest.ModuleContentId,
            StringComparison.Ordinal))
        {
            return new LuaAotDiagnostic(
                "AOT2006",
                "The artifact module identity does not match the requested module.");
        }

        var functionIds = new HashSet<int>();
        var methodNames = new HashSet<string>(StringComparer.Ordinal);
        for (var functionIndex = 0; functionIndex < manifest.Functions.Length; functionIndex++)
        {
            var function = manifest.Functions[functionIndex];
            if (function.FunctionId < 0 || !functionIds.Add(function.FunctionId) ||
                function.Shards.IsDefaultOrEmpty)
            {
                return new LuaAotDiagnostic(
                    "AOT2007",
                    "The artifact function map is malformed.");
            }

            var expectedStart = 0;
            for (var shardIndex = 0; shardIndex < function.Shards.Length; shardIndex++)
            {
                var shard = function.Shards[shardIndex];
                var expectedMethodName = function.Shards.Length == 1
                    ? $"Function_{function.FunctionId}"
                    : $"Function_{function.FunctionId}_Shard_{shardIndex}";
                if (shard.StartProgramCounter != expectedStart || shard.InstructionCount <= 0 ||
                    shard.InstructionCount > manifest.MaximumCanonicalInstructionsPerMethod ||
                    !string.Equals(shard.MethodName, expectedMethodName, StringComparison.Ordinal) ||
                    !methodNames.Add(shard.MethodName))
                {
                    return new LuaAotDiagnostic(
                        "AOT2007",
                        "The artifact function map is malformed.");
                }

                expectedStart = checked(expectedStart + shard.InstructionCount);
            }

            if (expectedStart != moduleSummary.Functions[functionIndex].InstructionCount)
            {
                return new LuaAotDiagnostic(
                    "AOT2007",
                    "The artifact function map does not cover the canonical function.");
            }

            if (!manifest.ProfileGuidedNumericRegions && !function.NumericRegions.IsEmpty)
            {
                return new LuaAotDiagnostic(
                    "AOT2007",
                    "An unprofiled artifact contains profile-guided numeric regions.");
            }

            var occupiedProgramCounters = new HashSet<int>();
            for (var regionIndex = 0;
                regionIndex < function.NumericRegions.Length;
                regionIndex++)
            {
                var region = function.NumericRegions[regionIndex];
                var expectedMethodName =
                    $"Function_{function.FunctionId}_NumericRegion_{regionIndex}";
                if (!string.Equals(
                        region.MethodName,
                        expectedMethodName,
                        StringComparison.Ordinal) ||
                    !methodNames.Add(region.MethodName) ||
                    region.ProgramCounters.IsDefaultOrEmpty ||
                    !region.ProgramCounters.SequenceEqual(
                        region.ProgramCounters.Distinct().Order()) ||
                    region.ProgramCounters[0] < 0 ||
                    region.ProgramCounters[^1] >= expectedStart ||
                    !region.ProgramCounters.Contains(region.HeaderProgramCounter) ||
                    !region.ProgramCounters.Contains(region.BackedgeProgramCounter) ||
                    region.UnboxedNumericLocalCount <= 0 ||
                    region.DirectNumericInstructionCount <= 0 ||
                    region.SafepointCount <= 0 ||
                    region.ProgramCounters.Any(programCounter =>
                        !occupiedProgramCounters.Add(programCounter)))
                {
                    return new LuaAotDiagnostic(
                        "AOT2007",
                        "The artifact numeric-region map is malformed.");
                }
            }
        }

        return null;
    }

    private static bool IsSha256Hex(string value) =>
        value.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static LuaAotDiagnostic? ValidatePortablePdb(
        ImmutableArray<byte> peImage,
        ImmutableArray<byte> portablePdbImage,
        LuaAotArtifactManifest manifest)
    {
        if (portablePdbImage.IsDefaultOrEmpty)
        {
            return manifest.EmitPortablePdb
                ? new LuaAotDiagnostic(
                    "AOT2008",
                    "The Portable PDB required by the AOT manifest is missing.")
                : null;
        }

        if (!manifest.EmitPortablePdb)
        {
            return new LuaAotDiagnostic(
                "AOT2008",
                "The AOT artifact contains an unexpected Portable PDB.");
        }

        try
        {
            using var pdbStream = new MemoryStream(portablePdbImage.ToArray(), writable: false);
            using (var provider = MetadataReaderProvider.FromPortablePdbStream(
                pdbStream,
                MetadataStreamOptions.LeaveOpen))
            {
                var pdb = provider.GetMetadataReader();
                if (pdb.Documents.Count != 1)
                {
                    return new LuaAotDiagnostic(
                        "AOT2008",
                        "The Portable PDB source document map is malformed.");
                }

                var document = pdb.GetDocument(pdb.Documents.Single());
                if (!string.Equals(
                        pdb.GetString(document.Name),
                        manifest.SourceDocumentName,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        Convert.ToHexStringLower(pdb.GetBlobBytes(document.Hash)),
                        manifest.SourceDocumentChecksum,
                        StringComparison.Ordinal))
                {
                    return new LuaAotDiagnostic(
                        "AOT2008",
                        "The Portable PDB source binding does not match the AOT manifest.");
                }
            }

            using var peStream = new MemoryStream(peImage.ToArray(), writable: false);
            using var peReader = new PEReader(peStream);
            var checksums = peReader.ReadDebugDirectory()
                .Where(static entry => entry.Type == DebugDirectoryEntryType.PdbChecksum)
                .Select(peReader.ReadPdbChecksumDebugDirectoryData)
                .ToArray();
            if (checksums.Length == 1 &&
                string.Equals(checksums[0].AlgorithmName, "SHA256", StringComparison.Ordinal) &&
                checksums[0].Checksum.AsSpan().SequenceEqual(
                    SHA256.HashData(portablePdbImage.AsSpan())))
            {
                return null;
            }
        }
        catch (Exception exception) when (exception is BadImageFormatException or InvalidDataException)
        {
        }

        return new LuaAotDiagnostic(
            "AOT2008",
            "The Portable PDB checksum does not match the AOT PE debug directory.");
    }

    private static Dictionary<int, LuaCompiledMethod> BindFunctions(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type generatedType,
        LuaAotArtifactManifest manifest)
    {
        var result = new Dictionary<int, LuaCompiledMethod>();
        foreach (var function in manifest.Functions)
        {
            var numericRegions = function.NumericRegions.Select(region =>
            {
                var method = GetCompiledMethod(generatedType, region.MethodName);
                return new LoadedNumericRegion(
                    region.ProgramCounters,
                    method.CreateDelegate<LuaCompiledMethod>());
            }).ToImmutableArray();
            var shards = function.Shards.Select(shard =>
            {
                var method = GetCompiledMethod(generatedType, shard.MethodName);
                return new LoadedShard(
                    shard.StartProgramCounter,
                    checked(shard.StartProgramCounter + shard.InstructionCount),
                    method.CreateDelegate<LuaCompiledMethod>());
            }).ToImmutableArray();

            LuaCompiledMethod dispatcher = (context, thread, frame) =>
            {
                var programCounter = frame.ProgramCounter;
                foreach (var region in numericRegions)
                {
                    if (region.ProgramCounters.BinarySearch(programCounter) >= 0)
                    {
                        return region.Method(context, thread, frame);
                    }
                }

                foreach (var shard in shards)
                {
                    if (programCounter >= shard.StartProgramCounter &&
                        programCounter < shard.EndProgramCounter)
                    {
                        return shard.Method(context, thread, frame);
                    }
                }

                return LuaCompiledExit.Deopt(
                    Math.Max(programCounter, 0),
                    instructionsConsumed: 0,
                    LuaCompiledExitReason.BackendInvalidated);
            };
            result.Add(function.FunctionId, dispatcher);
        }

        return result;
    }

    private static MethodInfo GetCompiledMethod(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)]
        Type generatedType,
        string methodName) => generatedType.GetMethod(
            methodName,
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            [typeof(LuaExecutionContext), typeof(LuaThread), typeof(LuaFrame)],
            modifiers: null) ?? throw new MissingMethodException(
                generatedType.FullName,
                methodName);

    private static LuaAotLoadResult Failure(
        string code,
        string message,
        LuaAotLoadMetrics metrics = default) =>
        new(null, [new LuaAotDiagnostic(code, message)], metrics);

    private static LuaAotValidationResult ValidationFailure(string code, string message) =>
        new(null, [new LuaAotDiagnostic(code, message)]);

    private sealed record LoadedShard(
        int StartProgramCounter,
        int EndProgramCounter,
        LuaCompiledMethod Method);

    private sealed record LoadedNumericRegion(
        ImmutableArray<int> ProgramCounters,
        LuaCompiledMethod Method);

    private sealed record ArtifactEnvelope(
        ImmutableArray<byte> CoreImage,
        bool ChecksumMatches);
}

internal sealed class LuaAotLoadContext(string assemblyName) : AssemblyLoadContext(
    $"Lunil AOT {assemblyName}",
    isCollectible: true)
{
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(
            assemblyName.Name,
            typeof(LuaCodegenAbiV1).Assembly.GetName().Name,
            StringComparison.Ordinal))
        {
            return typeof(LuaCodegenAbiV1).Assembly;
        }

        return null;
    }
}
