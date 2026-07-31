using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lunil.Analysis;

#pragma warning disable CA1720 // Lua contract type names intentionally match the Lua domain.
public enum LuaHostTypeKind : byte
{
    Any,
    Nil,
    Boolean,
    Integer,
    Number,
    String,
    Table,
    Function,
    Thread,
    Userdata,
}
#pragma warning restore CA1720

[Flags]
public enum LuaHostEffectKind : ushort
{
    None = 0,
    ReadsGlobal = 1,
    WritesGlobal = 2,
    ReadsTable = 4,
    WritesTable = 8,
    MayYield = 16,
    MayThrow = 32,
    RegistersCallback = 64,
    UnregistersCallback = 128,
    ReadsPersistence = 256,
    WritesPersistence = 512,
    DeletesPersistence = 1024,
    ClearsPersistence = 2048,
}

public enum LuaHostCallbackInvocationKind : byte
{
    Synchronous,
    Deferred,
    Asynchronous,
}

public enum LuaHostCallbackCardinality : byte
{
    Once,
    Many,
}

public enum LuaHostCallbackRetentionKind : byte
{
    Borrowed,
    Stored,
}

public enum LuaPersistenceOperationKind : byte
{
    Read,
    Write,
    Delete,
    Clear,
}

public sealed record LuaHostSourceLocation
{
    public string Uri { get; init; } = string.Empty;

    public int Line { get; init; } = 1;

    public int Column { get; init; } = 1;

    public string? ImplementationUri { get; init; }
}

public sealed record LuaHostTypeDescriptor
{
    public LuaHostTypeKind Kind { get; init; } = LuaHostTypeKind.Any;

    public string? Name { get; init; }

    public bool IsNullable { get; init; }

    public ImmutableDictionary<string, LuaHostTypeDescriptor> Fields { get; init; } =
        ImmutableDictionary<string, LuaHostTypeDescriptor>.Empty.WithComparers(StringComparer.Ordinal);

    public ImmutableArray<LuaHostParameterContract> Parameters { get; init; } = [];

    public ImmutableArray<LuaHostTypeDescriptor> Returns { get; init; } = [];

    public bool HasVariadicParameters { get; init; }

    public bool HasVariadicReturns { get; init; }
}

public sealed record LuaHostParameterContract
{
    public string Name { get; init; } = string.Empty;

    public LuaHostTypeDescriptor Type { get; init; } = new();

    public bool IsOptional { get; init; }
}

public sealed record LuaHostCallbackContract
{
    public int ParameterIndex { get; init; }

    public LuaHostCallbackInvocationKind Invocation { get; init; }

    public LuaHostCallbackCardinality Cardinality { get; init; } = LuaHostCallbackCardinality.Many;

    public LuaHostCallbackRetentionKind Retention { get; init; } = LuaHostCallbackRetentionKind.Stored;

    public string? UnsubscribeFunction { get; init; }

    public bool MayYield { get; init; }

    public bool MayThrow { get; init; }
}

public sealed record LuaHostPersistenceContract
{
    public LuaPersistenceOperationKind Operation { get; init; }

    public int? KeyParameterIndex { get; init; }

    public int? ValueParameterIndex { get; init; }

    public string SchemaId { get; init; } = string.Empty;

    public int SchemaVersion { get; init; } = 1;

    public LuaHostTypeDescriptor ValueType { get; init; } = new();

    public bool MissingReturnsNil { get; init; } = true;

    public string? MigrationFunction { get; init; }
}

public sealed record LuaHostFunctionOverloadContract
{
    public ImmutableArray<LuaHostParameterContract> Parameters { get; init; } = [];

    public ImmutableArray<LuaHostTypeDescriptor> Returns { get; init; } = [];

    public bool HasVariadicParameters { get; init; }

    public bool HasVariadicReturns { get; init; }
}

public sealed record LuaHostFunctionContract
{
    public string Path { get; init; } = string.Empty;

    public ImmutableArray<LuaHostParameterContract> Parameters { get; init; } = [];

    public ImmutableArray<LuaHostTypeDescriptor> Returns { get; init; } = [];

    public bool HasVariadicParameters { get; init; }

    public bool HasVariadicReturns { get; init; }

    public ImmutableArray<LuaHostFunctionOverloadContract> Overloads { get; init; } = [];

    public LuaHostEffectKind Effects { get; init; }

    public LuaHostCallbackContract? Callback { get; init; }

    public LuaHostPersistenceContract? Persistence { get; init; }

    public LuaHostSourceLocation? Source { get; init; }
}

/// <summary>Language-neutral, versioned description of Lua values injected by an external host.</summary>
public sealed record LuaHostAnalysisContract
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string ContractId { get; init; } = string.Empty;

    public ImmutableDictionary<string, LuaHostTypeDescriptor> Globals { get; init; } =
        ImmutableDictionary<string, LuaHostTypeDescriptor>.Empty.WithComparers(StringComparer.Ordinal);

    public ImmutableDictionary<string, LuaHostTypeDescriptor> Modules { get; init; } =
        ImmutableDictionary<string, LuaHostTypeDescriptor>.Empty.WithComparers(StringComparer.Ordinal);

    public ImmutableDictionary<string, LuaHostFunctionContract> Functions { get; init; } =
        ImmutableDictionary<string, LuaHostFunctionContract>.Empty.WithComparers(StringComparer.Ordinal);

    public string ToJson()
    {
        Validate();
        return JsonSerializer.Serialize(this, LuaHostContractJsonContext.Default.LuaHostAnalysisContract);
    }

    /// <summary>Creates a deterministic LuaLS-compatible declaration stub for this contract.</summary>
    public string ToLuaStub()
    {
        Validate();
        var output = new StringBuilder();
        output.AppendLine("---@meta");
        output.Append("-- Lunil host contract: ").AppendLine(ContractId);

        foreach (var global in Globals.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            AppendValueStub(output, global.Key, global.Value, isLocal: false);
        }

        foreach (var function in Functions.Values.OrderBy(static item => item.Path, StringComparer.Ordinal))
        {
            output.AppendLine();
            if (function.Source is { } source)
            {
                output.Append("-- source: ").AppendLine(source.Uri);
                if (!string.IsNullOrWhiteSpace(source.ImplementationUri))
                {
                    output.Append("-- implementation: ").AppendLine(source.ImplementationUri);
                }
            }

            foreach (var parameter in function.Parameters)
            {
                output.Append("---@param ").Append(parameter.Name);
                if (parameter.IsOptional)
                {
                    output.Append('?');
                }

                output.Append(' ').AppendLine(ToLuaStubType(parameter.Type));
            }

            foreach (var returnType in function.Returns)
            {
                output.Append("---@return ").AppendLine(ToLuaStubType(returnType));
            }

            foreach (var overload in function.Overloads)
            {
                output.Append("---@overload ").AppendLine(ToLuaStubFunctionType(
                    overload.Parameters,
                    overload.Returns));
            }

            output.Append("function ").Append(function.Path).Append('(');
            output.Append(string.Join(", ", function.Parameters.Select(static item => item.Name)));
            output.AppendLine(") end");
        }

        foreach (var module in Modules.OrderBy(static item => item.Key, StringComparer.Ordinal))
        {
            output.AppendLine();
            output.Append("---@module '").Append(EscapeLuaString(module.Key)).AppendLine("'");
            var localName = "module_" + SanitizeLuaIdentifier(module.Key);
            AppendValueStub(output, localName, module.Value, isLocal: true);
        }

        return output.ToString();
    }

    public static LuaHostAnalysisContract ParseJson(string json)
    {
        LunilGuard.NotNullOrWhiteSpace(json);
        var contract = JsonSerializer.Deserialize(
            json,
            LuaHostContractJsonContext.Default.LuaHostAnalysisContract) ??
            throw new JsonException("The host analysis contract is empty.");
        contract.Validate();
        return contract;
    }

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            throw new NotSupportedException($"Host contract schema {SchemaVersion} is not supported.");
        }

        LunilGuard.NotNullOrWhiteSpace(ContractId);
        ValidateMap(Globals, "global");
        ValidateMap(Modules, "module");
        LunilGuard.NotNull(Functions);
        foreach (var pair in Functions)
        {
            LunilGuard.NotNull(pair.Value);
            if (string.IsNullOrWhiteSpace(pair.Key) ||
                !string.Equals(pair.Key, pair.Value.Path, StringComparison.Ordinal) ||
                !IsCanonicalPath(pair.Key))
            {
                throw new InvalidOperationException("Host function keys must equal their canonical paths.");
            }

            ValidateParameters(pair.Value.Parameters, pair.Key);
            if (pair.Value.HasVariadicParameters && pair.Value.Parameters.IsEmpty)
            {
                throw new InvalidOperationException($"Variadic parameters are invalid for '{pair.Key}'.");
            }
            foreach (var type in pair.Value.Returns)
            {
                ValidateType(type, depth: 0);
            }

            foreach (var overload in pair.Value.Overloads)
            {
                ValidateParameters(overload.Parameters, pair.Key);
                if (overload.HasVariadicParameters && overload.Parameters.IsEmpty)
                {
                    throw new InvalidOperationException($"Variadic overload parameters are invalid for '{pair.Key}'.");
                }

                foreach (var type in overload.Returns)
                {
                    ValidateType(type, depth: 0);
                }
            }

            if (pair.Value.Callback is { } callback &&
                (callback.ParameterIndex < 0 || callback.ParameterIndex >= pair.Value.Parameters.Length))
            {
                throw new InvalidOperationException($"Callback parameter index is invalid for '{pair.Key}'.");
            }

            if (pair.Value.Callback is { } validCallback)
            {
                if (pair.Value.Parameters[validCallback.ParameterIndex].Type.Kind != LuaHostTypeKind.Function)
                {
                    throw new InvalidOperationException($"Callback parameter is not a function for '{pair.Key}'.");
                }

                if (!string.IsNullOrWhiteSpace(validCallback.UnsubscribeFunction) &&
                    !IsCanonicalPath(validCallback.UnsubscribeFunction))
                {
                    throw new InvalidOperationException($"Callback unsubscribe path is invalid for '{pair.Key}'.");
                }

                if (validCallback.Retention == LuaHostCallbackRetentionKind.Stored &&
                    !pair.Value.Effects.HasFlag(LuaHostEffectKind.RegistersCallback))
                {
                    throw new InvalidOperationException($"Stored callback effects are inconsistent for '{pair.Key}'.");
                }
            }

            if (pair.Value.Persistence is { } persistence)
            {
                if (persistence.SchemaVersion <= 0 || string.IsNullOrWhiteSpace(persistence.SchemaId))
                {
                    throw new InvalidOperationException($"Persistence schema is invalid for '{pair.Key}'.");
                }

                ValidateParameterIndex(persistence.KeyParameterIndex, pair.Value.Parameters.Length, pair.Key);
                ValidateParameterIndex(persistence.ValueParameterIndex, pair.Value.Parameters.Length, pair.Key);
                ValidateType(persistence.ValueType, depth: 0);
                ValidatePersistenceIndices(persistence, pair.Value.Parameters.Length, pair.Key);
                var expectedEffect = persistence.Operation switch
                {
                    LuaPersistenceOperationKind.Read => LuaHostEffectKind.ReadsPersistence,
                    LuaPersistenceOperationKind.Write => LuaHostEffectKind.WritesPersistence,
                    LuaPersistenceOperationKind.Delete => LuaHostEffectKind.DeletesPersistence,
                    _ => LuaHostEffectKind.ClearsPersistence,
                };
                if (!pair.Value.Effects.HasFlag(expectedEffect))
                {
                    throw new InvalidOperationException($"Persistence effects are inconsistent for '{pair.Key}'.");
                }
                if (!string.IsNullOrWhiteSpace(persistence.MigrationFunction) &&
                    !IsCanonicalPath(persistence.MigrationFunction))
                {
                    throw new InvalidOperationException($"Persistence migration path is invalid for '{pair.Key}'.");
                }
            }

            if (pair.Value.Source is { } source &&
                (string.IsNullOrWhiteSpace(source.Uri) || source.Line <= 0 || source.Column <= 0 ||
                 source.ImplementationUri is not null &&
                 string.IsNullOrWhiteSpace(source.ImplementationUri)))
            {
                throw new InvalidOperationException($"Source mapping is invalid for '{pair.Key}'.");
            }
        }
    }

    public ImmutableDictionary<string, LuaType> CreateGlobalTypes()
    {
        var descriptors = Globals.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        foreach (var function in Functions.Values)
        {
            var segments = function.Path.Split('.');
            var descriptor = new LuaHostTypeDescriptor
            {
                Kind = LuaHostTypeKind.Function,
                Name = function.Path,
                Parameters = function.Parameters,
                Returns = function.Returns,
                HasVariadicParameters = function.HasVariadicParameters,
                HasVariadicReturns = function.HasVariadicReturns,
            };
            if (segments.Length == 1)
            {
                descriptors[segments[0]] = descriptor;
                continue;
            }

            var root = descriptors.GetValueOrDefault(segments[0], new LuaHostTypeDescriptor
            {
                Kind = LuaHostTypeKind.Table,
                Name = segments[0],
            });
            descriptors[segments[0]] = AddDescriptorPath(root, segments, 1, descriptor);
        }

        return descriptors.ToImmutableDictionary(
            static pair => pair.Key,
            pair => ToLuaType(pair.Value),
            StringComparer.Ordinal);
    }

    public ImmutableDictionary<string, LuaType> CreateModuleTypes() =>
        Modules.ToImmutableDictionary(
            static pair => pair.Key,
            pair => ToLuaType(pair.Value),
            StringComparer.Ordinal);

    public static LuaType ToLuaType(LuaHostTypeDescriptor descriptor)
    {
        LunilGuard.NotNull(descriptor);
        LuaType type = descriptor.Kind switch
        {
            LuaHostTypeKind.Nil => LuaTypes.Nil,
            LuaHostTypeKind.Boolean => LuaTypes.Boolean,
            LuaHostTypeKind.Integer => LuaTypes.Integer,
            LuaHostTypeKind.Number => LuaTypes.Number,
            LuaHostTypeKind.String => LuaTypes.String,
            LuaHostTypeKind.Table => new LuaStructuralTableType([
                .. descriptor.Fields.Select(pair => new LuaTableField(
                    pair.Key,
                    null,
                    ToLuaType(pair.Value),
                    pair.Value.IsNullable)),
            ], IsOpen: descriptor.Fields.IsEmpty),
            LuaHostTypeKind.Function => new LuaFunctionType(
                [.. descriptor.Parameters.Select(parameter => new LuaFunctionParameter(
                    parameter.Name,
                    ToLuaType(parameter.Type),
                    parameter.IsOptional,
                    descriptor.HasVariadicParameters && parameter == descriptor.Parameters[^1]))],
                new LuaTypePack(
                    [.. descriptor.Returns.Select(ToLuaType)],
                    descriptor.HasVariadicReturns ? LuaTypes.Any : null),
                []),
            LuaHostTypeKind.Thread => LuaTypes.Thread,
            LuaHostTypeKind.Userdata => LuaTypes.Userdata,
            _ => LuaTypes.Any,
        };
        return descriptor.IsNullable && type.Kind != LuaTypeKind.Nil
            ? new LuaTypeRelations().Union(type, LuaTypes.Nil)
            : type;
    }

    private static void ValidateMap(
        ImmutableDictionary<string, LuaHostTypeDescriptor> values,
        string category)
    {
        LunilGuard.NotNull(values);
        foreach (var pair in values)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                throw new InvalidOperationException($"A host {category} has an empty name.");
            }

            ValidateType(pair.Value, depth: 0);
        }
    }

    private static void ValidateParameters(
        ImmutableArray<LuaHostParameterContract> parameters,
        string path)
    {
        if (parameters.IsDefault)
        {
            throw new InvalidOperationException($"Parameters are uninitialized for '{path}'.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            if (parameter is null || string.IsNullOrWhiteSpace(parameter.Name) || !names.Add(parameter.Name))
            {
                throw new InvalidOperationException($"Parameters are invalid for '{path}'.");
            }

            ValidateType(parameter.Type, depth: 0);
        }
    }

    private static void ValidateType(LuaHostTypeDescriptor type, int depth)
    {
        LunilGuard.NotNull(type);
        if (depth >= 32)
        {
            throw new InvalidOperationException("Host contract type nesting exceeds 32 levels.");
        }

        LunilGuard.NotNull(type.Fields);
        foreach (var field in type.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Key))
            {
                throw new InvalidOperationException("A host type contains an empty field name.");
            }

            ValidateType(field.Value, depth + 1);
        }

        ValidateParameters(type.Parameters, type.Name ?? "<type>");
        if (type.HasVariadicParameters && type.Parameters.IsEmpty)
        {
            throw new InvalidOperationException("A variadic host function type must declare its variadic parameter.");
        }
        foreach (var item in type.Returns)
        {
            ValidateType(item, depth + 1);
        }
    }

    private static void ValidateParameterIndex(int? index, int count, string path)
    {
        if (index is < 0 || index >= count)
        {
            throw new InvalidOperationException($"Persistence parameter index is invalid for '{path}'.");
        }
    }

    private static void ValidatePersistenceIndices(
        LuaHostPersistenceContract persistence,
        int parameterCount,
        string path)
    {
        var needsKey = persistence.Operation is LuaPersistenceOperationKind.Read or
            LuaPersistenceOperationKind.Write or LuaPersistenceOperationKind.Delete;
        var needsValue = persistence.Operation == LuaPersistenceOperationKind.Write;
        if (needsKey != persistence.KeyParameterIndex.HasValue ||
            needsValue != persistence.ValueParameterIndex.HasValue ||
            persistence.Operation == LuaPersistenceOperationKind.Clear &&
            (persistence.KeyParameterIndex.HasValue || persistence.ValueParameterIndex.HasValue))
        {
            throw new InvalidOperationException($"Persistence operation parameters are inconsistent for '{path}'.");
        }

        ValidateParameterIndex(persistence.KeyParameterIndex, parameterCount, path);
        ValidateParameterIndex(persistence.ValueParameterIndex, parameterCount, path);
    }

    private static bool IsCanonicalPath(string path)
    {
        var segments = path.Split('.');
        return segments.Length != 0 && segments.All(IsLuaIdentifier);
    }

    private static bool IsLuaIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value) || !(value[0] == '_' || char.IsLetter(value[0])))
        {
            return false;
        }

        return value.Skip(1).All(static character => character == '_' || char.IsLetterOrDigit(character));
    }

    private static void AppendValueStub(
        StringBuilder output,
        string name,
        LuaHostTypeDescriptor type,
        bool isLocal)
    {
        output.Append("---@type ").AppendLine(ToLuaStubType(type));
        if (isLocal)
        {
            output.Append("local ");
        }

        if (!isLocal && !IsLuaIdentifier(name))
        {
            output.Append("_G[\"").Append(EscapeLuaString(name)).AppendLine("\"] = nil");
        }
        else
        {
            output.Append(name).AppendLine(" = nil");
        }
    }

    private static string ToLuaStubType(LuaHostTypeDescriptor descriptor, int depth = 0)
    {
        if (depth >= 32)
        {
            return "any";
        }

        var type = descriptor.Kind switch
        {
            LuaHostTypeKind.Nil => "nil",
            LuaHostTypeKind.Boolean => "boolean",
            LuaHostTypeKind.Integer => "integer",
            LuaHostTypeKind.Number => "number",
            LuaHostTypeKind.String => "string",
            LuaHostTypeKind.Table when descriptor.Fields.IsEmpty => "table",
            LuaHostTypeKind.Table => "{" + string.Join(", ", descriptor.Fields
                .OrderBy(static item => item.Key, StringComparer.Ordinal)
                .Select(item => IsLuaIdentifier(item.Key)
                    ? item.Key + ": " + ToLuaStubType(item.Value, depth + 1)
                    : "[\"" + EscapeLuaString(item.Key) + "\"]: " +
                        ToLuaStubType(item.Value, depth + 1))) + "}",
            LuaHostTypeKind.Function => "fun(" + string.Join(", ", descriptor.Parameters.Select(parameter =>
                parameter.Name + (parameter.IsOptional ? "?" : string.Empty) + ": " +
                ToLuaStubType(parameter.Type, depth + 1))) + ")" +
                (descriptor.Returns.IsEmpty ? string.Empty : ": " + string.Join(", ",
                    descriptor.Returns.Select(item => ToLuaStubType(item, depth + 1)))),
            LuaHostTypeKind.Thread => "thread",
            LuaHostTypeKind.Userdata => descriptor.Name is { Length: > 0 } ? descriptor.Name : "userdata",
            _ => "any",
        };
        return descriptor.IsNullable && descriptor.Kind != LuaHostTypeKind.Nil
            ? type + "|nil"
            : type;
    }

    private static string ToLuaStubFunctionType(
        ImmutableArray<LuaHostParameterContract> parameters,
        ImmutableArray<LuaHostTypeDescriptor> returns) =>
        "fun(" + string.Join(", ", parameters.Select(parameter =>
            parameter.Name + (parameter.IsOptional ? "?" : string.Empty) + ": " +
            ToLuaStubType(parameter.Type, 1))) + ")" +
        (returns.IsEmpty ? string.Empty : ": " + string.Join(", ", returns.Select(item =>
            ToLuaStubType(item, 1))));

    private static string SanitizeLuaIdentifier(string value)
    {
        var result = new StringBuilder(value.Length + 1);
        foreach (var character in value)
        {
            result.Append(character == '_' || char.IsLetterOrDigit(character) ? character : '_');
        }

        if (result.Length == 0 || !(result[0] == '_' || char.IsLetter(result[0])))
        {
            result.Insert(0, '_');
        }

        return result.ToString();
    }

    private static string EscapeLuaString(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("'", "\\'", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);

    private static LuaHostTypeDescriptor AddDescriptorPath(
        LuaHostTypeDescriptor current,
        string[] segments,
        int index,
        LuaHostTypeDescriptor value)
    {
        var fields = current.Fields.ToBuilder();
        if (index == segments.Length - 1)
        {
            fields[segments[index]] = value;
        }
        else
        {
            var child = fields.GetValueOrDefault(segments[index], new LuaHostTypeDescriptor
            {
                Kind = LuaHostTypeKind.Table,
                Name = string.Join(".", segments.Take(index + 1)),
            });
            fields[segments[index]] = AddDescriptorPath(child, segments, index + 1, value);
        }

        return current with
        {
            Kind = LuaHostTypeKind.Table,
            Fields = fields.ToImmutable(),
        };
    }
}

/// <summary>Programmatic builder used by C#, C++, generated bindings, and tests.</summary>
public sealed class LuaHostContractBuilder(string contractId)
{
    private readonly Dictionary<string, LuaHostTypeDescriptor> _globals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LuaHostTypeDescriptor> _modules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LuaHostFunctionContract> _functions = new(StringComparer.Ordinal);

    public LuaHostContractBuilder AddGlobal(string name, LuaHostTypeDescriptor type)
    {
        Add(_globals, name, type);
        return this;
    }

    public LuaHostContractBuilder AddModule(string name, LuaHostTypeDescriptor type)
    {
        Add(_modules, name, type);
        return this;
    }

    public LuaHostContractBuilder AddFunction(LuaHostFunctionContract function)
    {
        LunilGuard.NotNull(function);
        if (_functions.TryGetValue(function.Path, out var existing))
        {
            if (existing.Callback != function.Callback || existing.Persistence != function.Persistence)
            {
                throw new InvalidOperationException(
                    $"Host function overload metadata is inconsistent for '{function.Path}'.");
            }

            _functions[function.Path] = existing with
            {
                Effects = existing.Effects | function.Effects,
                Overloads = [.. existing.Overloads, new LuaHostFunctionOverloadContract
                {
                    Parameters = function.Parameters,
                    Returns = function.Returns,
                    HasVariadicParameters = function.HasVariadicParameters,
                    HasVariadicReturns = function.HasVariadicReturns,
                }, .. function.Overloads],
            };
        }
        else
        {
            _functions.Add(function.Path, function);
        }

        return this;
    }

    public LuaHostAnalysisContract Build()
    {
        var contract = new LuaHostAnalysisContract
        {
            ContractId = contractId,
            Globals = _globals.ToImmutableDictionary(StringComparer.Ordinal),
            Modules = _modules.ToImmutableDictionary(StringComparer.Ordinal),
            Functions = _functions.ToImmutableDictionary(StringComparer.Ordinal),
        };
        contract.Validate();
        return contract;
    }

    private static void Add(
        Dictionary<string, LuaHostTypeDescriptor> target,
        string name,
        LuaHostTypeDescriptor type)
    {
        LunilGuard.NotNullOrWhiteSpace(name);
        LunilGuard.NotNull(type);
        if (!target.TryAdd(name, type))
        {
            throw new InvalidOperationException($"Host value '{name}' is already registered.");
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(LuaHostAnalysisContract))]
internal sealed partial class LuaHostContractJsonContext : JsonSerializerContext;
