using System.Text.Json.Serialization;

namespace Lunil.Hosting;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    GenerationMode = JsonSourceGenerationMode.Metadata | JsonSourceGenerationMode.Serialization)]
[JsonSerializable(typeof(LuaPatchManifest))]
[JsonSerializable(typeof(LuaPatchMigrationSchema))]
[JsonSerializable(typeof(LuaPatchJournalEntry))]
[JsonSerializable(typeof(LuaPatchJournalHashPayload))]
[JsonSerializable(typeof(LuaPatchReplayRecord))]
[JsonSerializable(typeof(LuaPatchReplayHashPayload))]
[JsonSerializable(typeof(LuaPatchDistributedBarrierFileState))]
internal sealed partial class LuaPatchJsonContext : JsonSerializerContext;
