using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using Lunil.CodeGen.Cil.Artifacts;
using Lunil.CodeGen.Cil.Planning;
using Lunil.CodeGen.Cil.Verification;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.CodeGen.Cil.Emission;

internal sealed record PersistedCilMethod(
    string Name,
    CilMethodPlan Plan);

internal sealed record ManagedPeEmissionResult(
    ImmutableArray<byte> PeImage,
    ImmutableArray<byte> PortablePdbImage,
    ImmutableArray<CilPlanDiagnostic> Diagnostics);

internal static class ManagedPeCilEmitter
{
    public const string ManifestResourceName = "lunil.aot.manifest.json";
    public const string ModuleResourceName = "lunil.canonical.module";

    public static ReadOnlySpan<byte> ArtifactChecksumMagic => "LUNILPECHK1\0"u8;

    private static readonly Guid Sha256DocumentHashAlgorithm =
        new("8829d00f-11b8-4213-878b-770e8597ac16");
    private static readonly Guid LuaDocumentLanguage =
        new("25f1518f-4f6f-4489-9d74-03f61f6d9d6b");

    public static ManagedPeEmissionResult Emit(
        LuaAotArtifactManifest manifest,
        ReadOnlySpan<byte> manifestBytes,
        ReadOnlySpan<byte> moduleBytes,
        ReadOnlySpan<byte> sourceBytes,
        IReadOnlyList<PersistedCilMethod> methods,
        LuaAotCompilationOptions options)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(methods);
        var diagnostics = ImmutableArray.CreateBuilder<CilPlanDiagnostic>();
        var verifiedMethods = new List<(PersistedCilMethod Method, CilPlanVerificationResult Verification)>();
        foreach (var method in methods)
        {
            var verification = CilMethodPlanVerifier.Verify(method.Plan);
            if (!verification.Succeeded)
            {
                diagnostics.AddRange(verification.Diagnostics);
            }

            verifiedMethods.Add((method, verification));
        }

        if (diagnostics.Count != 0)
        {
            return new ManagedPeEmissionResult([], [], diagnostics.ToImmutable());
        }

        var metadata = new MetadataBuilder();
        var moduleVersionId = DeterministicGuid(manifestBytes, moduleBytes);
        metadata.AddModule(
            generation: 0,
            metadata.GetOrAddString(manifest.AssemblyName + ".dll"),
            metadata.GetOrAddGuid(moduleVersionId),
            default,
            default);
        metadata.AddAssembly(
            metadata.GetOrAddString(manifest.AssemblyName),
            new Version(1, 0, 0, 0),
            default,
            default,
            (AssemblyFlags)0,
            AssemblyHashAlgorithm.Sha256);

        var systemRuntime = AddAssemblyReference(metadata, ResolveSystemRuntimeAssemblyName());
        var runtime = AddAssemblyReference(metadata, typeof(LuaCodegenAbiV1).Assembly.GetName());
        var objectType = metadata.AddTypeReference(
            systemRuntime,
            metadata.GetOrAddString("System"),
            metadata.GetOrAddString("Object"));
        var types = AddRuntimeTypeReferences(metadata, runtime);
        var calls = AddCallReferences(metadata, types);
        var methodSignature = AddCompiledMethodSignature(metadata, types);

        metadata.AddTypeDefinition(
            TypeAttributes.NotPublic,
            default,
            metadata.GetOrAddString("<Module>"),
            default,
            MetadataTokens.FieldDefinitionHandle(1),
            MetadataTokens.MethodDefinitionHandle(1));

        var firstMethod = MetadataTokens.MethodDefinitionHandle(1);
        var separator = manifest.TypeName.LastIndexOf('.');
        var typeNamespace = separator < 0 ? string.Empty : manifest.TypeName[..separator];
        var typeName = separator < 0 ? manifest.TypeName : manifest.TypeName[(separator + 1)..];
        metadata.AddTypeDefinition(
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed |
            TypeAttributes.BeforeFieldInit,
            metadata.GetOrAddString(typeNamespace),
            metadata.GetOrAddString(typeName),
            objectType,
            MetadataTokens.FieldDefinitionHandle(1),
            firstMethod);

        var ilStream = new BlobBuilder();
        var methodBodies = new MethodBodyStreamEncoder(ilStream);
        var emittedMethods = new List<EmittedMethod>(methods.Count);
        for (var index = 0; index < verifiedMethods.Count; index++)
        {
            var item = verifiedMethods[index];
            var emitted = EmitMethodBody(
                metadata,
                methodBodies,
                item.Method,
                item.Verification.MaximumEvaluationStack,
                types,
                calls);
            if (emitted.CodeSize > options.MaximumMethodBodyBytes)
            {
                diagnostics.Add(new CilPlanDiagnostic(
                    "CIL0028",
                    $"Method body '{item.Method.Name}' contains {emitted.CodeSize} bytes; " +
                    $"limit is {options.MaximumMethodBodyBytes}."));
            }

            var methodHandle = metadata.AddMethodDefinition(
                MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig,
                MethodImplAttributes.IL | MethodImplAttributes.Managed,
                metadata.GetOrAddString(item.Method.Name),
                methodSignature,
                emitted.BodyOffset,
                MetadataTokens.ParameterHandle(1));
            emittedMethods.Add(emitted with { Handle = methodHandle });
        }

        if (diagnostics.Count != 0)
        {
            return new ManagedPeEmissionResult([], [], diagnostics.ToImmutable());
        }

        var resources = new BlobBuilder();
        AddManagedResource(metadata, resources, ManifestResourceName, manifestBytes);
        AddManagedResource(metadata, resources, ModuleResourceName, moduleBytes);

        var pdbImage = ImmutableArray<byte>.Empty;
        BlobContentId pdbId = default;
        var metadataTokenCount = metadata.GetRowCounts().Aggregate(
            0L,
            static (total, count) => checked(total + count));
        if (metadataTokenCount > options.MaximumMetadataTokens)
        {
            return new ManagedPeEmissionResult(
                [],
                [],
                [new CilPlanDiagnostic(
                    "CIL0029",
                    $"Artifact metadata contains {metadataTokenCount} rows; limit is " +
                    $"{options.MaximumMetadataTokens}.")]);
        }

        if (options.EmitPortablePdb)
        {
            (pdbImage, pdbId) = EmitPortablePdb(
                metadata,
                manifest,
                sourceBytes,
                emittedMethods);
        }

        var debugDirectory = new DebugDirectoryBuilder();
        debugDirectory.AddReproducibleEntry();
        if (options.EmitPortablePdb)
        {
            debugDirectory.AddCodeViewEntry(manifest.PortablePdbName, pdbId, 0x0100);
            debugDirectory.AddPdbChecksumEntry(
                "SHA256",
                SHA256.HashData(pdbImage.AsSpan()).ToImmutableArray());
        }

        var peBuilder = new ManagedPEBuilder(
            PEHeaderBuilder.CreateLibraryHeader(),
            new MetadataRootBuilder(metadata),
            ilStream,
            managedResources: resources,
            debugDirectoryBuilder: debugDirectory,
            strongNameSignatureSize: 0,
            deterministicIdProvider: ComputeContentId);
        var peBlob = new BlobBuilder();
        peBuilder.Serialize(peBlob);
        return new ManagedPeEmissionResult(
            AppendArtifactChecksum(peBlob.ToImmutableArray()),
            pdbImage,
            diagnostics.ToImmutable());
    }

    private static ImmutableArray<byte> AppendArtifactChecksum(
        ImmutableArray<byte> peImage)
    {
        var checksum = SHA256.HashData(peImage.AsSpan());
        var result = new byte[
            peImage.Length + ArtifactChecksumMagic.Length + checksum.Length];
        peImage.AsSpan().CopyTo(result);
        ArtifactChecksumMagic.CopyTo(result.AsSpan(peImage.Length));
        checksum.CopyTo(result.AsSpan(peImage.Length + ArtifactChecksumMagic.Length));
        return result.ToImmutableArray();
    }

    private static EmittedMethod EmitMethodBody(
        MetadataBuilder metadata,
        MethodBodyStreamEncoder methodBodies,
        PersistedCilMethod method,
        int maximumStack,
        RuntimeTypes types,
        Dictionary<string, MemberReferenceHandle> calls)
    {
        var code = new BlobBuilder();
        var controlFlow = new ControlFlowBuilder();
        var encoder = new InstructionEncoder(code, controlFlow);
        var labels = method.Plan.Instructions
            .Where(static instruction => instruction.OpCode == CilPlanOpCode.MarkLabel)
            .ToDictionary(static instruction => instruction.Label.Id, _ => encoder.DefineLabel());
        var instructionOffsets = ImmutableArray.CreateBuilder<int>(method.Plan.Instructions.Length);
        foreach (var instruction in method.Plan.Instructions)
        {
            instructionOffsets.Add(encoder.Offset);
            EmitInstruction(encoder, instruction, labels, calls);
        }

        var localSignature = AddLocalSignature(metadata, method.Plan.Locals, types);
        var bodyOffset = methodBodies.AddMethodBody(
            encoder,
            maximumStack,
            localSignature,
            MethodBodyAttributes.InitLocals);
        return new EmittedMethod(
            default,
            method.Plan,
            bodyOffset,
            code.Count,
            instructionOffsets.ToImmutable(),
            localSignature);
    }

    private static void EmitInstruction(
        InstructionEncoder encoder,
        CilPlanInstruction instruction,
        Dictionary<int, LabelHandle> labels,
        Dictionary<string, MemberReferenceHandle> calls)
    {
        switch (instruction.OpCode)
        {
            case CilPlanOpCode.MarkLabel:
                encoder.MarkLabel(labels[instruction.Label.Id]);
                break;
            case CilPlanOpCode.Nop:
                encoder.OpCode(ILOpCode.Nop);
                break;
            case CilPlanOpCode.LoadArgument:
                encoder.LoadArgument(instruction.Int32Operand);
                break;
            case CilPlanOpCode.LoadLocal:
                encoder.LoadLocal(instruction.Int32Operand);
                break;
            case CilPlanOpCode.StoreLocal:
                encoder.StoreLocal(instruction.Int32Operand);
                break;
            case CilPlanOpCode.LoadInt32:
                encoder.LoadConstantI4(instruction.Int32Operand);
                break;
            case CilPlanOpCode.Add:
                encoder.OpCode(ILOpCode.Add);
                break;
            case CilPlanOpCode.Subtract:
                encoder.OpCode(ILOpCode.Sub);
                break;
            case CilPlanOpCode.Call:
                encoder.Call(calls[instruction.CallTarget!.Id]);
                break;
            case CilPlanOpCode.Branch:
                encoder.Branch(ILOpCode.Br, labels[instruction.Label.Id]);
                break;
            case CilPlanOpCode.BranchTrue:
                encoder.Branch(ILOpCode.Brtrue, labels[instruction.Label.Id]);
                break;
            case CilPlanOpCode.BranchFalse:
                encoder.Branch(ILOpCode.Brfalse, labels[instruction.Label.Id]);
                break;
            case CilPlanOpCode.Switch:
                var switchEncoder = encoder.Switch(instruction.Labels.Length);
                foreach (var label in instruction.Labels)
                {
                    switchEncoder.Branch(labels[label.Id]);
                }

                break;
            case CilPlanOpCode.Return:
                encoder.OpCode(ILOpCode.Ret);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported persisted CIL plan opcode {instruction.OpCode}.");
        }
    }

    private static StandaloneSignatureHandle AddLocalSignature(
        MetadataBuilder metadata,
        ImmutableArray<CilLocal> locals,
        RuntimeTypes types)
    {
        if (locals.IsEmpty)
        {
            return default;
        }

        var blob = new BlobBuilder();
        var encoder = new BlobEncoder(blob).LocalVariableSignature(locals.Length);
        foreach (var local in locals.OrderBy(static local => local.Index))
        {
            EncodeType(encoder.AddVariable().Type(), local.Kind, types);
        }

        return metadata.AddStandaloneSignature(metadata.GetOrAddBlob(blob));
    }

    private static BlobHandle AddCompiledMethodSignature(
        MetadataBuilder metadata,
        RuntimeTypes types)
    {
        var blob = new BlobBuilder();
        var signature = new BlobEncoder(blob).MethodSignature();
        signature.Parameters(
            3,
            returnType => EncodeType(returnType.Type(), CilStackValueKind.CompiledExit, types),
            parameters =>
            {
                EncodeType(parameters.AddParameter().Type(), CilStackValueKind.ExecutionContext, types);
                EncodeType(parameters.AddParameter().Type(), CilStackValueKind.Thread, types);
                EncodeType(parameters.AddParameter().Type(), CilStackValueKind.Frame, types);
            });
        return metadata.GetOrAddBlob(blob);
    }

    private static Dictionary<string, MemberReferenceHandle> AddCallReferences(
        MetadataBuilder metadata,
        RuntimeTypes types)
    {
        var result = new Dictionary<string, MemberReferenceHandle>(StringComparer.Ordinal);
        foreach (var target in CilWellKnownCalls.All)
        {
            var isInstance = target.Id is
                "LuaExecutionContext.TryReserveInstructions" or
                "LuaFrame.get_ProgramCounter";
            var declaringType = target.Id switch
            {
                "LuaExecutionContext.TryReserveInstructions" => types.ExecutionContext,
                "LuaFrame.get_ProgramCounter" => types.Frame,
                _ when target.Id.StartsWith("LuaCodegenAbiV1.", StringComparison.Ordinal) =>
                    types.CodegenAbiV1,
                _ when target.Id.StartsWith("LuaCodegenAbiV2.", StringComparison.Ordinal) =>
                    types.CodegenAbiV2,
                _ when target.Id.StartsWith("LuaCompiledExit.", StringComparison.Ordinal) =>
                    types.CompiledExit,
                _ => throw new InvalidOperationException(
                    $"Unknown Runtime ABI call target {target.Id}."),
            };
            var separator = target.Id.LastIndexOf('.');
            var memberName = target.Id[(separator + 1)..];
            var parameterKinds = isInstance
                ? target.ParameterKinds.RemoveAt(0)
                : target.ParameterKinds;
            var signatureBlob = new BlobBuilder();
            var signature = new BlobEncoder(signatureBlob).MethodSignature(
                SignatureCallingConvention.Default,
                genericParameterCount: 0,
                isInstanceMethod: isInstance);
            signature.Parameters(
                parameterKinds.Length,
                returnType => EncodeCallReturnType(returnType, target, types),
                parameters =>
                {
                    for (var parameterIndex = 0; parameterIndex < parameterKinds.Length; parameterIndex++)
                    {
                        var parameter = parameters.AddParameter().Type();
                        if (target.Id is "LuaCompiledExit.Poll" or "LuaCompiledExit.Deopt" &&
                            parameterIndex == 2)
                        {
                            parameter.Type(types.CompiledExitReason, isValueType: true);
                        }
                        else
                        {
                            EncodeType(parameter, parameterKinds[parameterIndex], types);
                        }
                    }
                });
            result.Add(
                target.Id,
                metadata.AddMemberReference(
                    declaringType,
                    metadata.GetOrAddString(memberName),
                    metadata.GetOrAddBlob(signatureBlob)));
        }

        return result;
    }

    private static void EncodeCallReturnType(
        ReturnTypeEncoder encoder,
        CilCallTarget target,
        RuntimeTypes types)
    {
        if (target.Id is "LuaExecutionContext.TryReserveInstructions" or
            "LuaCodegenAbiV1.IsTruthy" or "LuaCodegenAbiV1.CanExecuteCompiled" or
            "LuaCodegenAbiV2.CanExecuteCompiledFrame" or
            "LuaCodegenAbiV2.CanSkipClose" or
            "LuaCodegenAbiV2.CanExecuteUnaryPrimitive" or
            "LuaCodegenAbiV2.CanExecuteBinaryPrimitive")
        {
            encoder.Type().Boolean();
            return;
        }

        EncodeReturnType(encoder, target.ReturnKind, types);
    }

    private static void EncodeReturnType(
        ReturnTypeEncoder encoder,
        CilStackValueKind kind,
        RuntimeTypes types)
    {
        if (kind == CilStackValueKind.Void)
        {
            encoder.Void();
            return;
        }

        EncodeType(encoder.Type(), kind, types);
    }

    private static void EncodeType(
        SignatureTypeEncoder encoder,
        CilStackValueKind kind,
        RuntimeTypes types)
    {
        switch (kind)
        {
            case CilStackValueKind.Int32:
                encoder.Int32();
                break;
            case CilStackValueKind.Int64:
                encoder.Int64();
                break;
            case CilStackValueKind.Float:
                encoder.Double();
                break;
            case CilStackValueKind.Object:
                encoder.Object();
                break;
            case CilStackValueKind.LuaValue:
                encoder.Type(types.LuaValue, isValueType: true);
                break;
            case CilStackValueKind.ExecutionContext:
                encoder.Type(types.ExecutionContext, isValueType: false);
                break;
            case CilStackValueKind.Thread:
                encoder.Type(types.Thread, isValueType: false);
                break;
            case CilStackValueKind.Frame:
                encoder.Type(types.Frame, isValueType: false);
                break;
            case CilStackValueKind.CompiledExit:
                encoder.Type(types.CompiledExit, isValueType: true);
                break;
            default:
                throw new InvalidOperationException($"No persisted signature type exists for {kind}.");
        }
    }

    private static RuntimeTypes AddRuntimeTypeReferences(
        MetadataBuilder metadata,
        AssemblyReferenceHandle runtime)
    {
        TypeReferenceHandle Add(Type type) => metadata.AddTypeReference(
            runtime,
            metadata.GetOrAddString(type.Namespace ?? string.Empty),
            metadata.GetOrAddString(type.Name));

        return new RuntimeTypes(
            Add(typeof(LuaExecutionContext)),
            Add(typeof(LuaThread)),
            Add(typeof(LuaFrame)),
            Add(typeof(LuaValue)),
            Add(typeof(LuaCompiledExit)),
            Add(typeof(LuaCompiledExitReason)),
            Add(typeof(LuaCodegenAbiV1)),
            Add(typeof(LuaCodegenAbiV2)));
    }

    private static AssemblyReferenceHandle AddAssemblyReference(
        MetadataBuilder metadata,
        AssemblyName assemblyName)
    {
        var token = assemblyName.GetPublicKeyToken() ?? [];
        return metadata.AddAssemblyReference(
            metadata.GetOrAddString(assemblyName.Name ??
                throw new InvalidOperationException("Assembly reference has no name.")),
            assemblyName.Version ?? new Version(0, 0, 0, 0),
            string.IsNullOrEmpty(assemblyName.CultureName)
                ? default
                : metadata.GetOrAddString(assemblyName.CultureName),
            metadata.GetOrAddBlob(token),
            (AssemblyFlags)0,
            default);
    }

    private static AssemblyName ResolveSystemRuntimeAssemblyName()
    {
        try
        {
            return Assembly.Load(new AssemblyName("System.Runtime")).GetName();
        }
        catch (FileNotFoundException)
        {
            return typeof(object).Assembly.GetName();
        }
    }

    private static void AddManagedResource(
        MetadataBuilder metadata,
        BlobBuilder resources,
        string name,
        ReadOnlySpan<byte> content)
    {
        var offset = checked((uint)resources.Count);
        resources.WriteInt32(content.Length);
        resources.WriteBytes(content.ToArray());
        metadata.AddManifestResource(
            ManifestResourceAttributes.Public,
            metadata.GetOrAddString(name),
            default,
            offset);
    }

    private static (ImmutableArray<byte> Image, BlobContentId Id) EmitPortablePdb(
        MetadataBuilder metadata,
        LuaAotArtifactManifest manifest,
        ReadOnlySpan<byte> sourceBytes,
        IReadOnlyList<EmittedMethod> methods)
    {
        var pdbMetadata = new MetadataBuilder();
        var document = pdbMetadata.AddDocument(
            pdbMetadata.GetOrAddDocumentName(manifest.SourceDocumentName),
            pdbMetadata.GetOrAddGuid(Sha256DocumentHashAlgorithm),
            pdbMetadata.GetOrAddBlob(SHA256.HashData(sourceBytes)),
            pdbMetadata.GetOrAddGuid(LuaDocumentLanguage));
        foreach (var method in methods)
        {
            var sequencePoints = EncodeSequencePoints(pdbMetadata, method);
            pdbMetadata.AddMethodDebugInformation(
                sequencePoints.IsNil ? default : document,
                sequencePoints);
            var programCounterMap = LuaAotPortablePdbMetadata.EncodeProgramCounterMap(
                method.Plan.SequencePoints.Select(point =>
                    new LuaAotProgramCounterMapEntry(
                        method.InstructionOffsets[point.PlanInstructionIndex],
                        point.CanonicalProgramCounter,
                        point.LogicalProgramCounter)));
            pdbMetadata.AddCustomDebugInformation(
                method.Handle,
                pdbMetadata.GetOrAddGuid(
                    LuaAotPortablePdbMetadata.ProgramCounterMapKind),
                pdbMetadata.GetOrAddBlob(programCounterMap));
        }

        var builder = new PortablePdbBuilder(
            pdbMetadata,
            metadata.GetRowCounts(),
            entryPoint: default,
            idProvider: ComputeContentId);
        var pdbBlob = new BlobBuilder();
        var id = builder.Serialize(pdbBlob);
        return (pdbBlob.ToImmutableArray(), id);
    }

    private static BlobHandle EncodeSequencePoints(
        MetadataBuilder metadata,
        EmittedMethod method)
    {
        var points = method.Plan.SequencePoints
            .Where(static point => point.SourceLine > 0)
            .Select(point => (
                Offset: method.InstructionOffsets[point.PlanInstructionIndex],
                point.SourceLine))
            .Distinct()
            .OrderBy(static point => point.Offset)
            .ToArray();
        if (points.Length == 0)
        {
            return default;
        }

        var blob = new BlobBuilder();
        blob.WriteCompressedInteger(method.LocalSignature.IsNil
            ? 0
            : MetadataTokens.GetRowNumber(method.LocalSignature));
        var previousOffset = 0;
        var previousLine = 0;
        var first = true;
        foreach (var point in points)
        {
            blob.WriteCompressedInteger(first ? point.Offset : point.Offset - previousOffset);
            blob.WriteCompressedInteger(0);
            blob.WriteCompressedInteger(1);
            if (first)
            {
                blob.WriteCompressedInteger(point.SourceLine);
                blob.WriteCompressedInteger(1);
                first = false;
            }
            else
            {
                blob.WriteCompressedSignedInteger(point.SourceLine - previousLine);
                blob.WriteCompressedSignedInteger(0);
            }

            previousOffset = point.Offset;
            previousLine = point.SourceLine;
        }

        return metadata.GetOrAddBlob(blob);
    }

    private static BlobContentId ComputeContentId(IEnumerable<Blob> blobs)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var blob in blobs)
        {
            var bytes = blob.GetBytes();
            hash.AppendData(bytes.AsSpan());
        }

        return BlobContentId.FromHash(hash.GetHashAndReset());
    }

    private static Guid DeterministicGuid(
        ReadOnlySpan<byte> manifest,
        ReadOnlySpan<byte> module)
    {
        Span<byte> guidBytes = stackalloc byte[16];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(manifest);
        hash.AppendData(module);
        hash.GetHashAndReset()[..16].CopyTo(guidBytes);
        return new Guid(guidBytes);
    }

    private sealed record RuntimeTypes(
        TypeReferenceHandle ExecutionContext,
        TypeReferenceHandle Thread,
        TypeReferenceHandle Frame,
        TypeReferenceHandle LuaValue,
        TypeReferenceHandle CompiledExit,
        TypeReferenceHandle CompiledExitReason,
        TypeReferenceHandle CodegenAbiV1,
        TypeReferenceHandle CodegenAbiV2);

    private sealed record EmittedMethod(
        MethodDefinitionHandle Handle,
        CilMethodPlan Plan,
        int BodyOffset,
        int CodeSize,
        ImmutableArray<int> InstructionOffsets,
        StandaloneSignatureHandle LocalSignature);
}
