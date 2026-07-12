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

            functions.Add(new LuaAotFunctionManifest(
                RequiredInt32(functionElement, "functionId"),
                shards.ToImmutable()));
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
        string sourceDocumentName)
    {
        var text = string.Join(
            "\n",
            options.EmitPortablePdb ? "pdb=1" : "pdb=0",
            $"shard={options.MaximumCanonicalInstructionsPerMethod}",
            $"body={options.MaximumMethodBodyBytes}",
            $"tokens={options.MaximumMetadataTokens}",
            $"branches={options.MaximumBranchInstructionsPerMethod}",
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
