using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Lunil.CodeGen.Cil.Artifacts;

internal static class LuaAotManifestCodec
{
    public static byte[] Serialize(LuaAotArtifactManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = false,
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("magic", manifest.Magic);
            writer.WriteNumber("artifactSchemaVersion", manifest.ArtifactSchemaVersion);
            writer.WriteNumber("irFormatVersion", manifest.IrFormatVersion);
            writer.WriteNumber("runtimeAbiVersion", manifest.RuntimeAbiVersion);
            writer.WriteNumber("codegenVersion", manifest.CodegenVersion);
            writer.WriteString("moduleContentId", manifest.ModuleContentId);
            writer.WriteString("moduleChecksum", manifest.ModuleChecksum);
            writer.WriteString("optionsFingerprint", manifest.OptionsFingerprint);
            writer.WriteBoolean(
                "profileGuidedNumericRegions",
                manifest.ProfileGuidedNumericRegions);
            writer.WriteNumber("profilePolicyVersion", manifest.ProfilePolicyVersion);
            writer.WriteString("profileFingerprint", manifest.ProfileFingerprint);
            writer.WriteBoolean("emitPortablePdb", manifest.EmitPortablePdb);
            writer.WriteNumber(
                "maximumCanonicalInstructionsPerMethod",
                manifest.MaximumCanonicalInstructionsPerMethod);
            writer.WriteNumber("maximumMethodBodyBytes", manifest.MaximumMethodBodyBytes);
            writer.WriteNumber("maximumMetadataTokens", manifest.MaximumMetadataTokens);
            writer.WriteNumber(
                "maximumBranchInstructionsPerMethod",
                manifest.MaximumBranchInstructionsPerMethod);
            writer.WriteString("assemblyName", manifest.AssemblyName);
            writer.WriteString("typeName", manifest.TypeName);
            writer.WriteString("portablePdbName", manifest.PortablePdbName);
            writer.WriteString("sourceDocumentName", manifest.SourceDocumentName);
            writer.WriteString("sourceDocumentChecksum", manifest.SourceDocumentChecksum);
            writer.WriteStartArray("functions");
            foreach (var function in manifest.Functions)
            {
                writer.WriteStartObject();
                writer.WriteNumber("functionId", function.FunctionId);
                writer.WriteStartArray("shards");
                foreach (var shard in function.Shards)
                {
                    writer.WriteStartObject();
                    writer.WriteString("methodName", shard.MethodName);
                    writer.WriteNumber("startProgramCounter", shard.StartProgramCounter);
                    writer.WriteNumber("instructionCount", shard.InstructionCount);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteStartArray("numericRegions");
                foreach (var region in function.NumericRegions)
                {
                    writer.WriteStartObject();
                    writer.WriteString("methodName", region.MethodName);
                    writer.WriteNumber("headerProgramCounter", region.HeaderProgramCounter);
                    writer.WriteNumber("backedgeProgramCounter", region.BackedgeProgramCounter);
                    writer.WriteStartArray("programCounters");
                    foreach (var programCounter in region.ProgramCounters)
                    {
                        writer.WriteNumberValue(programCounter);
                    }

                    writer.WriteEndArray();
                    writer.WriteNumber(
                        "unboxedNumericLocalCount",
                        region.UnboxedNumericLocalCount);
                    writer.WriteNumber(
                        "directNumericInstructionCount",
                        region.DirectNumericInstructionCount);
                    writer.WriteNumber("safepointCount", region.SafepointCount);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static LuaAotArtifactManifest Deserialize(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        var root = document.RootElement;
        var functions = ImmutableArray.CreateBuilder<LuaAotFunctionManifest>();
        foreach (var functionElement in Required(root, "functions").EnumerateArray())
        {
            var shards = ImmutableArray.CreateBuilder<LuaAotMethodShardManifest>();
            foreach (var shardElement in Required(functionElement, "shards").EnumerateArray())
            {
                shards.Add(new LuaAotMethodShardManifest(
                    RequiredString(shardElement, "methodName"),
                    RequiredInt32(shardElement, "startProgramCounter"),
                    RequiredInt32(shardElement, "instructionCount")));
            }

            var numericRegions = ImmutableArray.CreateBuilder<LuaAotNumericRegionManifest>();
            foreach (var regionElement in Required(functionElement, "numericRegions").EnumerateArray())
            {
                var programCounters = Required(regionElement, "programCounters")
                    .EnumerateArray()
                    .Select(static value => value.GetInt32())
                    .ToImmutableArray();
                numericRegions.Add(new LuaAotNumericRegionManifest(
                    RequiredString(regionElement, "methodName"),
                    RequiredInt32(regionElement, "headerProgramCounter"),
                    RequiredInt32(regionElement, "backedgeProgramCounter"),
                    programCounters,
                    RequiredInt32(regionElement, "unboxedNumericLocalCount"),
                    RequiredInt32(regionElement, "directNumericInstructionCount"),
                    RequiredInt32(regionElement, "safepointCount")));
            }

            functions.Add(new LuaAotFunctionManifest(
                RequiredInt32(functionElement, "functionId"),
                shards.ToImmutable())
            {
                NumericRegions = numericRegions.ToImmutable(),
            });
        }

        return new LuaAotArtifactManifest
        {
            Magic = RequiredString(root, "magic"),
            ArtifactSchemaVersion = RequiredInt32(root, "artifactSchemaVersion"),
            IrFormatVersion = RequiredInt32(root, "irFormatVersion"),
            RuntimeAbiVersion = RequiredInt32(root, "runtimeAbiVersion"),
            CodegenVersion = RequiredInt32(root, "codegenVersion"),
            ModuleContentId = RequiredString(root, "moduleContentId"),
            ModuleChecksum = RequiredString(root, "moduleChecksum"),
            OptionsFingerprint = RequiredString(root, "optionsFingerprint"),
            ProfileGuidedNumericRegions = Required(
                root,
                "profileGuidedNumericRegions").GetBoolean(),
            ProfilePolicyVersion = RequiredInt32(root, "profilePolicyVersion"),
            ProfileFingerprint = RequiredString(root, "profileFingerprint"),
            EmitPortablePdb = Required(root, "emitPortablePdb").GetBoolean(),
            MaximumCanonicalInstructionsPerMethod = RequiredInt32(
                root,
                "maximumCanonicalInstructionsPerMethod"),
            MaximumMethodBodyBytes = RequiredInt32(root, "maximumMethodBodyBytes"),
            MaximumMetadataTokens = RequiredInt32(root, "maximumMetadataTokens"),
            MaximumBranchInstructionsPerMethod = RequiredInt32(
                root,
                "maximumBranchInstructionsPerMethod"),
            AssemblyName = RequiredString(root, "assemblyName"),
            TypeName = RequiredString(root, "typeName"),
            PortablePdbName = RequiredString(root, "portablePdbName"),
            SourceDocumentName = RequiredString(root, "sourceDocumentName"),
            SourceDocumentChecksum = RequiredString(root, "sourceDocumentChecksum"),
            Functions = functions.ToImmutable(),
        };
    }

    public static string FingerprintOptions(
        LuaAotCompilationOptions options,
        string sourceChecksum,
        string sourceDocumentName,
        string profileFingerprint,
        bool profileGuidedNumericRegions)
    {
        var text = string.Join(
            "\n",
            options.EmitPortablePdb ? "pdb=1" : "pdb=0",
            $"shard={options.MaximumCanonicalInstructionsPerMethod}",
            $"body={options.MaximumMethodBodyBytes}",
            $"tokens={options.MaximumMetadataTokens}",
            $"branches={options.MaximumBranchInstructionsPerMethod}",
            $"profileGuided={(profileGuidedNumericRegions ? 1 : 0)}",
            $"profilePolicy={LuaAotArtifactManifest.CurrentProfilePolicyVersion}",
            $"profile={profileFingerprint}",
            $"source={sourceDocumentName}",
            $"sourceChecksum={sourceChecksum}");
        return LuaCanonicalModuleSerializer.Sha256Hex(Encoding.UTF8.GetBytes(text));
    }

    private static JsonElement Required(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
            ? value
            : throw new InvalidDataException($"AOT manifest property '{property}' is missing.");

    private static string RequiredString(JsonElement element, string property) =>
        Required(element, property).GetString() ??
        throw new InvalidDataException($"AOT manifest property '{property}' is null.");

    private static int RequiredInt32(JsonElement element, string property) =>
        Required(element, property).GetInt32();
}
