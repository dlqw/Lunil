using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lunil.Runtime.Values;

namespace Lunil.Hosting;

public static class LuaPatchMigrationSchemaFormat
{
    public const int CurrentVersion = 1;

    public const string BundleEntryName = "migration/schema.json";
}

[JsonConverter(typeof(JsonStringEnumConverter<LuaPatchStateRuleKind>))]
public enum LuaPatchStateRuleKind : byte
{
    Preserve,
    Drop,
    HostAdapter,
    PatchTable,
}

[JsonConverter(typeof(JsonStringEnumConverter<LuaPatchResourceKind>))]
public enum LuaPatchResourceKind : byte
{
    Coroutine,
    Timer,
    EventSubscription,
    Task,
    HostResource,
}

[JsonConverter(typeof(JsonStringEnumConverter<LuaPatchResourceDisposition>))]
public enum LuaPatchResourceDisposition : byte
{
    Continue,
    Cancel,
    Restart,
    Drain,
    RejectIfActive,
}

public enum LuaPatchMigrationSchemaErrorCode : byte
{
    InvalidJson,
    NonCanonicalJson,
    UnsupportedFormatVersion,
    InvalidSchema,
    DuplicateModule,
    DuplicateRule,
    UnknownModule,
    AdapterRequired,
    AdapterNotRegistered,
    StatePathNotFound,
    StateKindMismatch,
    ResourceActive,
    AdapterFailed,
}

public sealed class LuaPatchMigrationSchemaException : Exception
{
    public LuaPatchMigrationSchemaException(
        LuaPatchMigrationSchemaErrorCode code,
        string message) : base(message)
    {
        Code = code;
    }

    public LuaPatchMigrationSchemaException(
        LuaPatchMigrationSchemaErrorCode code,
        string message,
        Exception innerException) : base(message, innerException)
    {
        Code = code;
    }

    public LuaPatchMigrationSchemaErrorCode Code { get; }
}

/// <summary>A signed, declarative state and asynchronous-resource migration schema.</summary>
public sealed record LuaPatchMigrationSchema
{
    public int FormatVersion { get; init; } = LuaPatchMigrationSchemaFormat.CurrentVersion;

    public required string SchemaId { get; init; }

    public required string BaseVersion { get; init; }

    public required string TargetVersion { get; init; }

    public ImmutableArray<LuaPatchModuleMigrationSchema> Modules { get; init; } = [];
}

public sealed record LuaPatchModuleMigrationSchema
{
    public required string ModuleName { get; init; }

    public ImmutableArray<LuaPatchStateRule> State { get; init; } = [];

    public ImmutableArray<LuaPatchResourceRule> Resources { get; init; } = [];
}

/// <summary>
/// Migrates one string-keyed path below a module cache. Paths use RFC 6901 JSON Pointer escaping;
/// an empty path identifies the cache root.
/// </summary>
public sealed record LuaPatchStateRule
{
    public required string TargetPath { get; init; }

    public string? SourcePath { get; init; }

    public required LuaPatchStateRuleKind Kind { get; init; }

    public string? AdapterId { get; init; }

    public bool Required { get; init; } = true;
}

public sealed record LuaPatchResourceRule
{
    public required string ResourceId { get; init; }

    public required LuaPatchResourceKind Kind { get; init; }

    public required LuaPatchResourceDisposition Disposition { get; init; }

    /// <summary>Module-cache path for a runtime-owned coroutine, CLR timer, or stable host resource.</summary>
    public string? StatePath { get; init; }

    /// <summary>Host adapter for external lifecycles or reversible coroutine actions.</summary>
    public string? AdapterId { get; init; }
}

public sealed record LuaPatchStateMigrationContext(
    string ModuleName,
    LuaPatchStateRule Rule,
    LuaValue PreviousValue,
    LuaValue CandidateValue);

/// <summary>Creates a reversible state mutation, including userdata payload migrations.</summary>
public interface ILuaPatchStateMigrationAdapter
{
    string AdapterId { get; }

    ILuaPatchStateMigrationOperation Prepare(LuaPatchStateMigrationContext context);
}

/// <summary>
/// A reversible state mutation. <see cref="ResultValue"/> is installed at the rule target after
/// <see cref="Apply"/> succeeds. Dispose releases journal resources and must not change live state.
/// </summary>
/// <summary>
/// Reversible state migration journal. <see cref="IDisposable.Dispose"/> must only release
/// operation-owned journal resources; it must not mutate committed or restored Lua state.
/// </summary>
public interface ILuaPatchStateMigrationOperation : IDisposable
{
    LuaValue ResultValue { get; }

    void Apply();

    void Rollback();
}

public sealed record LuaPatchResourceMigrationContext(
    string ModuleName,
    LuaPatchResourceRule Rule,
    LuaValue PreviousModuleState,
    LuaValue CandidateModuleState);

/// <summary>Resolves and journals host-owned timer, event, task, or coroutine lifecycle changes.</summary>
public interface ILuaPatchResourceMigrationAdapter
{
    string AdapterId { get; }

    ILuaPatchResourceMigrationOperation Prepare(LuaPatchResourceMigrationContext context);
}

/// <summary>
/// Reversible host-resource migration journal. <see cref="IDisposable.Dispose"/> must only release
/// operation-owned journal resources; it must not mutate committed or restored resource state.
/// </summary>
public interface ILuaPatchResourceMigrationOperation : IDisposable
{
    bool IsActive { get; }

    void Apply(LuaPatchResourceDisposition disposition);

    void Rollback();
}

public static class LuaPatchMigrationSchemaSerializer
{
    public static byte[] Serialize(
        LuaPatchMigrationSchema schema,
        LuaPatchResourceLimits? resourceLimits = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        resourceLimits ??= LuaPatchResourceLimits.Default;
        resourceLimits.Validate();
        Validate(schema, moduleNames: null, resourceLimits);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(
            schema,
            LuaPatchJsonContext.Default.LuaPatchMigrationSchema);
        LuaPatchResourceLimits.EnsureWithin(
            nameof(resourceLimits.MaximumMigrationSchemaBytes),
            serialized.Length,
            resourceLimits.MaximumMigrationSchemaBytes);
        return serialized;
    }

    public static LuaPatchMigrationSchema Deserialize(
        ReadOnlySpan<byte> utf8Json,
        LuaPatchResourceLimits? resourceLimits = null)
    {
        resourceLimits ??= LuaPatchResourceLimits.Default;
        resourceLimits.Validate();
        LuaPatchResourceLimits.EnsureWithin(
            nameof(resourceLimits.MaximumMigrationSchemaBytes),
            utf8Json.Length,
            resourceLimits.MaximumMigrationSchemaBytes);
        LuaPatchMigrationSchema schema;
        try
        {
            schema = JsonSerializer.Deserialize(
                utf8Json,
                LuaPatchJsonContext.Default.LuaPatchMigrationSchema) ??
                throw Error(
                    LuaPatchMigrationSchemaErrorCode.InvalidJson,
                    "The patch migration schema is empty.");
        }
        catch (JsonException exception)
        {
            throw new LuaPatchMigrationSchemaException(
                LuaPatchMigrationSchemaErrorCode.InvalidJson,
                "The patch migration schema is invalid JSON.",
                exception);
        }

        Validate(schema, moduleNames: null, resourceLimits);
        var canonical = JsonSerializer.SerializeToUtf8Bytes(
            schema,
            LuaPatchJsonContext.Default.LuaPatchMigrationSchema);
        if (!utf8Json.SequenceEqual(canonical))
        {
            throw Error(
                LuaPatchMigrationSchemaErrorCode.NonCanonicalJson,
                "The patch migration schema is not canonically encoded.");
        }

        return schema;
    }

    public static LuaPatchMigrationSchema? ReadFromBundle(
        LuaPatchBundle bundle,
        LuaPatchResourceLimits? resourceLimits = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        resourceLimits ??= LuaPatchResourceLimits.Default;
        resourceLimits.Validate();
        var entries = bundle.Entries.Where(static entry =>
            string.Equals(
                entry.Name,
                LuaPatchMigrationSchemaFormat.BundleEntryName,
                StringComparison.Ordinal)).ToArray();
        if (entries.Length == 0)
        {
            return null;
        }

        if (entries.Length != 1 || entries[0].Kind != LuaPatchEntryKind.CompanionData)
        {
            throw Error(
                LuaPatchMigrationSchemaErrorCode.InvalidSchema,
                "The migration schema entry must be unique companion data.");
        }

        var schema = Deserialize(entries[0].Content.Span, resourceLimits);
        Validate(schema, bundle.Entries
            .Where(static entry => entry.ModuleName is not null)
            .Select(static entry => entry.ModuleName!)
            .ToHashSet(StringComparer.Ordinal), resourceLimits);
        return schema;
    }

    internal static void Validate(
        LuaPatchMigrationSchema schema,
        IReadOnlySet<string>? moduleNames,
        LuaPatchResourceLimits resourceLimits)
    {
        if (schema.FormatVersion != LuaPatchMigrationSchemaFormat.CurrentVersion)
        {
            throw Error(
                LuaPatchMigrationSchemaErrorCode.UnsupportedFormatVersion,
                "The patch migration schema format version is unsupported.");
        }

        if (schema.Modules.IsDefault)
        {
            throw Error(
                LuaPatchMigrationSchemaErrorCode.InvalidSchema,
                "The migration module collection is invalid.");
        }

        LuaPatchResourceLimits.EnsureWithin(
            nameof(resourceLimits.MaximumMigrationModules),
            schema.Modules.Length,
            resourceLimits.MaximumMigrationModules);
        long stateRuleCount = 0;
        long resourceRuleCount = 0;
        foreach (var module in schema.Modules)
        {
            if (!module.State.IsDefault)
            {
                stateRuleCount += module.State.Length;
            }

            if (!module.Resources.IsDefault)
            {
                resourceRuleCount += module.Resources.Length;
            }
        }

        LuaPatchResourceLimits.EnsureWithin(
            nameof(resourceLimits.MaximumStateMigrationRules),
            stateRuleCount,
            resourceLimits.MaximumStateMigrationRules);
        LuaPatchResourceLimits.EnsureWithin(
            nameof(resourceLimits.MaximumResourceMigrationRules),
            resourceRuleCount,
            resourceLimits.MaximumResourceMigrationRules);

        if (string.IsNullOrWhiteSpace(schema.SchemaId) ||
            string.IsNullOrWhiteSpace(schema.BaseVersion) ||
            string.IsNullOrWhiteSpace(schema.TargetVersion) ||
            string.Equals(schema.BaseVersion, schema.TargetVersion, StringComparison.Ordinal))
        {
            throw Error(
                LuaPatchMigrationSchemaErrorCode.InvalidSchema,
                "Migration schema identity and distinct base/target versions are required.");
        }

        var modules = new HashSet<string>(StringComparer.Ordinal);
        string? previousModuleName = null;
        foreach (var module in schema.Modules)
        {
            if (string.IsNullOrWhiteSpace(module.ModuleName))
            {
                throw Error(
                    LuaPatchMigrationSchemaErrorCode.InvalidSchema,
                    "A migration module name is required.");
            }

            if (!modules.Add(module.ModuleName))
            {
                throw Error(
                    LuaPatchMigrationSchemaErrorCode.DuplicateModule,
                    $"Duplicate migration module '{module.ModuleName}'.");
            }

            if (previousModuleName is not null &&
                string.CompareOrdinal(previousModuleName, module.ModuleName) >= 0)
            {
                throw Error(
                    LuaPatchMigrationSchemaErrorCode.InvalidSchema,
                    "Migration modules must be sorted by ordinal module name.");
            }

            previousModuleName = module.ModuleName;

            if (moduleNames is not null && !moduleNames.Contains(module.ModuleName))
            {
                throw Error(
                    LuaPatchMigrationSchemaErrorCode.UnknownModule,
                    $"Migration schema refers to unknown module '{module.ModuleName}'.");
            }

            if (module.State.IsDefault || module.Resources.IsDefault)
            {
                throw Error(
                    LuaPatchMigrationSchemaErrorCode.InvalidSchema,
                    $"Module '{module.ModuleName}' has invalid migration collections.");
            }

            var targets = new HashSet<string>(StringComparer.Ordinal);
            var targetTrie = new StatePathTrie();
            string? previousTarget = null;
            foreach (var rule in module.State)
            {
                var targetPath = LuaPatchStatePath.Parse(rule.TargetPath);
                _ = LuaPatchStatePath.Parse(rule.SourcePath ?? rule.TargetPath);
                if (!Enum.IsDefined(rule.Kind) || !targets.Add(rule.TargetPath))
                {
                    throw Error(
                        LuaPatchMigrationSchemaErrorCode.DuplicateRule,
                        $"Module '{module.ModuleName}' has an invalid or duplicate state rule.");
                }

                if (previousTarget is not null &&
                    string.CompareOrdinal(previousTarget, rule.TargetPath) >= 0)
                {
                    throw Error(
                        LuaPatchMigrationSchemaErrorCode.InvalidSchema,
                        "State migration rules must be sorted by target path.");
                }

                previousTarget = rule.TargetPath;

                if (!targetTrie.TryAdd(targetPath.Segments))
                {
                    throw Error(
                        LuaPatchMigrationSchemaErrorCode.DuplicateRule,
                        $"Module '{module.ModuleName}' has overlapping state target paths.");
                }

                if (rule.Kind == LuaPatchStateRuleKind.HostAdapter !=
                    !string.IsNullOrWhiteSpace(rule.AdapterId))
                {
                    throw Error(
                        LuaPatchMigrationSchemaErrorCode.InvalidSchema,
                        "Only HostAdapter state rules declare an adapter id.");
                }
            }

            var resources = new HashSet<string>(StringComparer.Ordinal);
            string? previousResourceId = null;
            foreach (var resource in module.Resources)
            {
                if (string.IsNullOrWhiteSpace(resource.ResourceId) ||
                    !Enum.IsDefined(resource.Kind) ||
                    !Enum.IsDefined(resource.Disposition) ||
                    !resources.Add(resource.ResourceId))
                {
                    throw Error(
                        LuaPatchMigrationSchemaErrorCode.DuplicateRule,
                        $"Module '{module.ModuleName}' has an invalid or duplicate resource rule.");
                }

                if (previousResourceId is not null && string.CompareOrdinal(
                    previousResourceId,
                    resource.ResourceId) >= 0)
                {
                    throw Error(
                        LuaPatchMigrationSchemaErrorCode.InvalidSchema,
                        "Resource migration rules must be sorted by resource id.");
                }

                previousResourceId = resource.ResourceId;

                if (resource.StatePath is not null)
                {
                    _ = LuaPatchStatePath.Parse(resource.StatePath);
                }

                if (resource.Kind is (LuaPatchResourceKind.Coroutine or
                        LuaPatchResourceKind.Timer or LuaPatchResourceKind.HostResource) &&
                    string.IsNullOrWhiteSpace(resource.AdapterId) &&
                    resource.StatePath is null)
                {
                    throw Error(
                        LuaPatchMigrationSchemaErrorCode.InvalidSchema,
                        "A runtime-managed coroutine, timer, or host resource rule requires a state path.");
                }

                if (resource.Kind is (LuaPatchResourceKind.Coroutine or
                        LuaPatchResourceKind.Timer) &&
                    resource.Disposition is not (LuaPatchResourceDisposition.Continue or
                        LuaPatchResourceDisposition.RejectIfActive) &&
                    string.IsNullOrWhiteSpace(resource.AdapterId))
                {
                    throw Error(
                        LuaPatchMigrationSchemaErrorCode.AdapterRequired,
                        $"Resource '{resource.ResourceId}' requires a host adapter.");
                }

                if (resource.Kind == LuaPatchResourceKind.HostResource &&
                    resource.Disposition is not (LuaPatchResourceDisposition.Continue or
                        LuaPatchResourceDisposition.RejectIfActive) &&
                    string.IsNullOrWhiteSpace(resource.AdapterId))
                {
                    throw Error(
                        LuaPatchMigrationSchemaErrorCode.AdapterRequired,
                        $"Resource '{resource.ResourceId}' requires a host adapter.");
                }

                if (resource.Kind is not (LuaPatchResourceKind.Coroutine or
                        LuaPatchResourceKind.Timer or LuaPatchResourceKind.HostResource) &&
                    resource.Disposition != LuaPatchResourceDisposition.Continue &&
                    string.IsNullOrWhiteSpace(resource.AdapterId))
                {
                    throw Error(
                        LuaPatchMigrationSchemaErrorCode.AdapterRequired,
                        $"Resource '{resource.ResourceId}' requires a host adapter.");
                }
            }
        }
    }

    private static LuaPatchMigrationSchemaException Error(
        LuaPatchMigrationSchemaErrorCode code,
        string message) => new(code, message);

    private sealed class StatePathTrie
    {
        private readonly Dictionary<string, StatePathTrie> _children =
            new(StringComparer.Ordinal);
        private bool _terminal;

        public bool TryAdd(ImmutableArray<string> segments)
        {
            var node = this;
            foreach (var segment in segments)
            {
                if (node._terminal)
                {
                    return false;
                }

                if (!node._children.TryGetValue(segment, out var child))
                {
                    child = new StatePathTrie();
                    node._children.Add(segment, child);
                }

                node = child;
            }

            if (node._terminal || node._children.Count != 0)
            {
                return false;
            }

            node._terminal = true;
            return true;
        }
    }
}

internal readonly record struct LuaPatchStatePath(ImmutableArray<string> Segments)
{
    public static LuaPatchStatePath Parse(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
        {
            return new LuaPatchStatePath([]);
        }

        if (path[0] != '/')
        {
            throw new LuaPatchMigrationSchemaException(
                LuaPatchMigrationSchemaErrorCode.InvalidSchema,
                $"State path '{path}' is not an RFC 6901 JSON Pointer.");
        }

        var segments = path[1..].Split('/').Select(Unescape).ToImmutableArray();
        if (segments.Any(static segment => segment.Contains('\0', StringComparison.Ordinal)))
        {
            throw new LuaPatchMigrationSchemaException(
                LuaPatchMigrationSchemaErrorCode.InvalidSchema,
                "A state path contains a NUL character.");
        }

        return new LuaPatchStatePath(segments);
    }

    private static string Unescape(string segment)
    {
        var builder = new System.Text.StringBuilder(segment.Length);
        for (var index = 0; index < segment.Length; index++)
        {
            if (segment[index] != '~')
            {
                builder.Append(segment[index]);
                continue;
            }

            if (++index >= segment.Length || segment[index] is not ('0' or '1'))
            {
                throw new LuaPatchMigrationSchemaException(
                    LuaPatchMigrationSchemaErrorCode.InvalidSchema,
                    "A state path contains an invalid JSON Pointer escape.");
            }

            builder.Append(segment[index] == '0' ? '~' : '/');
        }

        return builder.ToString();
    }
}
