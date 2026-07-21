using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.Hosting;

/// <summary>Capabilities that an embedding host may grant to the CLR bridge.</summary>
[Flags]
public enum LuaClrCapabilities : byte
{
    /// <summary>Grants no CLR access.</summary>
    None = 0,

    /// <summary>Allows exact-allowlist type descriptions through <c>clr.type</c>.</summary>
    TypeDiscovery = 1,

    /// <summary>Allows exact-allowlist public constructor calls through <c>clr.new</c>.</summary>
    Construction = 2,
}

/// <summary>Stable error categories reported by CLR discovery and construction operations.</summary>
public enum LuaClrErrorCode : byte
{
    /// <summary>The requested bridge capability is disabled.</summary>
    CapabilityDenied,

    /// <summary>The supplied type name is empty or exceeds the configured bound.</summary>
    InvalidTypeName,

    /// <summary>The type name or its visibility is outside the configured boundary.</summary>
    TypeNotAllowed,

    /// <summary>No matching type exists in an already loaded, allowed assembly.</summary>
    TypeNotFound,

    /// <summary>More than one allowed loaded assembly defines the requested full name.</summary>
    AmbiguousType,

    /// <summary>The resolved type cannot be publicly constructed.</summary>
    TypeNotConstructible,

    /// <summary>No public constructor accepts the supplied Lua values.</summary>
    NoMatchingConstructor,

    /// <summary>The selected CLR constructor threw an exception.</summary>
    ConstructionFailed,
}

/// <summary>An exception with a stable CLR bridge error category.</summary>
public sealed class LuaClrException : Exception
{
    /// <summary>Creates a bridge exception with no inner exception.</summary>
    public LuaClrException(LuaClrErrorCode code, string message)
        : base(message)
    {
        Code = code;
    }

    /// <summary>Creates a bridge exception that retains the underlying CLR failure.</summary>
    public LuaClrException(LuaClrErrorCode code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    /// <summary>Gets the stable bridge error category.</summary>
    public LuaClrErrorCode Code { get; }
}

/// <summary>Capability, allowlist, module-installation, and ownership settings for a CLR bridge.</summary>
public sealed record LuaClrOptions
{
    /// <summary>Gets a configuration that grants no CLR capability.</summary>
    public static LuaClrOptions Disabled { get; } = new();

    /// <summary>Gets the capabilities granted to the bridge.</summary>
    public LuaClrCapabilities Capabilities { get; init; }

    /// <summary>Gets exact, case-sensitive simple names of assemblies that may be searched.</summary>
    public ImmutableArray<string> AllowedAssemblyNames { get; init; } = [];

    /// <summary>Gets exact, case-sensitive full names of public types that may be used.</summary>
    public ImmutableArray<string> AllowedTypeNames { get; init; } = [];

    /// <summary>Gets whether <see cref="LuaHost"/> installs the global Lua <c>clr</c> table.</summary>
    public bool InstallGlobalModule { get; init; }

    /// <summary>Gets the accepted type-name length bound. The valid range is 1 through 4096.</summary>
    public int MaximumTypeNameLength { get; init; } = 256;

    /// <summary>Gets whether userdata owns instances constructed by the bridge.</summary>
    public bool OwnConstructedObjects { get; init; } = true;
}

/// <summary>Describes one public CLR constructor by parameter type name.</summary>
public sealed record LuaClrConstructorInfo(ImmutableArray<string> ParameterTypeNames);

/// <summary>Describes an exact-allowlist CLR type without exposing a reflection object.</summary>
public sealed record LuaClrTypeInfo(
    string FullName,
    string AssemblyName,
    bool IsValueType,
    bool IsConstructible,
    ImmutableArray<LuaClrConstructorInfo> Constructors);

/// <summary>
/// Host-owned wrapper for a CLR object exposed through Lua userdata. Disposal is idempotent and
/// only forwards to the wrapped object when it implements <see cref="IDisposable"/>.
/// </summary>
public sealed class LuaClrObject : IDisposable
{
    private int _disposed;

    /// <summary>Creates a userdata payload wrapper for a CLR instance.</summary>
    public LuaClrObject(object instance, bool ownsInstance = true)
    {
        ArgumentNullException.ThrowIfNull(instance);
        Instance = instance;
        OwnsInstance = ownsInstance;
    }

    /// <summary>Gets the wrapped CLR instance.</summary>
    public object Instance { get; }

    /// <summary>Gets the runtime type of <see cref="Instance"/>.</summary>
    public Type ClrType => Instance.GetType();

    /// <summary>Gets whether disposing this wrapper also disposes the wrapped instance.</summary>
    public bool OwnsInstance { get; }

    /// <summary>Gets whether this wrapper has been disposed.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Disposes the owned instance at most once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 &&
            OwnsInstance &&
            Instance is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

/// <summary>
/// Capability-controlled type discovery and construction for a single Lua state. Only already
/// loaded assemblies and exact allowlisted type names are considered; the bridge never loads an
/// assembly by name.
/// </summary>
public sealed class LuaClrBridge
{
    private readonly LuaState _state;
    private readonly LuaClrOptions _options;
    private readonly ImmutableHashSet<string> _allowedAssemblies;
    private readonly ImmutableHashSet<string> _allowedTypes;

    /// <summary>Creates a bridge for one Lua state.</summary>
    public LuaClrBridge(LuaState state, LuaClrOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _options = options ?? LuaClrOptions.Disabled;
        const LuaClrCapabilities knownCapabilities =
            LuaClrCapabilities.TypeDiscovery | LuaClrCapabilities.Construction;
        if ((_options.Capabilities & ~knownCapabilities) != LuaClrCapabilities.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                _options.Capabilities,
                "The CLR capability set contains an unknown flag.");
        }

        if (_options.MaximumTypeNameLength is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                _options.MaximumTypeNameLength,
                "The CLR type-name length must be between 1 and 4096.");
        }

        _allowedAssemblies = NormalizeNames(_options.AllowedAssemblyNames, nameof(options));
        _allowedTypes = NormalizeNames(_options.AllowedTypeNames, nameof(options));
        if ((_options.Capabilities & (LuaClrCapabilities.TypeDiscovery |
                LuaClrCapabilities.Construction)) != LuaClrCapabilities.None &&
            _allowedAssemblies.Count == 0)
        {
            throw new ArgumentException(
                "CLR capabilities require at least one allowed assembly name.",
                nameof(options));
        }
    }

    /// <summary>Gets the Lua state that owns this bridge and its userdata.</summary>
    public LuaState State => _state;

    /// <summary>Gets the validated bridge configuration.</summary>
    public LuaClrOptions Options => _options;

    /// <summary>Gets whether this bridge grants at least one capability.</summary>
    public bool IsEnabled => _options.Capabilities != LuaClrCapabilities.None;

    /// <summary>Returns a description of an allowed type in an already loaded assembly.</summary>
    public LuaClrTypeInfo ResolveType(string typeName)
    {
        RequireCapability(LuaClrCapabilities.TypeDiscovery);
        var type = ResolveAllowedType(typeName);
        return Describe(type);
    }

    /// <summary>Constructs an allowed type and wraps the instance in state-owned userdata.</summary>
    public LuaUserdata CreateInstance(
        string typeName,
        ReadOnlySpan<LuaValue> arguments = default)
    {
        RequireCapability(LuaClrCapabilities.Construction);
        foreach (var argument in arguments)
        {
            _state.Heap.ValidateValue(argument);
        }

        var type = ResolveAllowedType(typeName);
        if (type.IsAbstract || type.IsInterface || type.IsByRefLike ||
            type.ContainsGenericParameters || !IsPubliclyVisible(type))
        {
            throw new LuaClrException(
                LuaClrErrorCode.TypeNotConstructible,
                $"CLR type '{type.FullName}' is not publicly constructible.");
        }

        var constructor = SelectConstructor(type, arguments, out var converted);
        if (constructor is null && !(type.IsValueType && arguments.Length == 0))
        {
            throw new LuaClrException(
                LuaClrErrorCode.NoMatchingConstructor,
                $"No public constructor of '{type.FullName}' accepts {arguments.Length} Lua value(s).");
        }

        object instance;
        try
        {
            instance = constructor is null
                ? CreateDefaultValueType(type)
                : constructor.Invoke(converted)!;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw new LuaClrException(
                LuaClrErrorCode.ConstructionFailed,
                $"CLR constructor for '{type.FullName}' failed.",
                exception.InnerException);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw new LuaClrException(
                LuaClrErrorCode.ConstructionFailed,
                $"CLR constructor for '{type.FullName}' failed.",
                exception);
        }

        return _state.CreateUserdata(
            new LuaClrObject(instance, _options.OwnConstructedObjects),
            userValueCount: 1,
            payloadLogicalSize: 64);
    }

    /// <summary>Installs the global Lua <c>clr</c> table for this bridge.</summary>
    public void InstallGlobalModule()
    {
        if (!IsEnabled)
        {
            throw new LuaClrException(
                LuaClrErrorCode.CapabilityDenied,
                "The CLR bridge is disabled for this host.");
        }

        var module = _state.CreateTable(0, 3);
        var capture = LuaValue.FromLightUserdata(new LuaLightUserdata(this));
        module.Set(
            LuaValue.FromString(_state.Strings.GetOrCreate("type"u8)),
            LuaValue.FromFunction(_state.CreateNativeClosure(
                new LuaNativeFunction("clr.type", TypeStep),
                [capture])));
        module.Set(
            LuaValue.FromString(_state.Strings.GetOrCreate("new"u8)),
            LuaValue.FromFunction(_state.CreateNativeClosure(
                new LuaNativeFunction("clr.new", NewStep),
                [capture])));
        _state.SetGlobal("clr", LuaValue.FromTable(module));
    }

    private static LuaNativeStep TypeStep(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values)
    {
        try
        {
            return LuaNativeStep.Completed(ResolveTypeFromLua(context, values));
        }
        catch (LuaClrException exception)
        {
            throw CreateLuaError(exception);
        }
    }

    private static LuaNativeStep NewStep(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values)
    {
        try
        {
            return LuaNativeStep.Completed(CreateFromLua(context, values));
        }
        catch (LuaClrException exception)
        {
            throw CreateLuaError(exception);
        }
    }

    private static LuaRuntimeException CreateLuaError(LuaClrException exception) =>
        new($"CLR {exception.Code}: {exception.Message}", exception);

    private static LuaValue ResolveTypeFromLua(
        LuaNativeCallContext context,
        ReadOnlySpan<LuaValue> values)
    {
        var bridge = GetBridge(context);
        var typeName = CheckString(context, values, 0, "clr.type");
        var info = bridge.ResolveType(typeName);
        var table = context.State.CreateTable(0, 5);
        SetString(context.State, table, "name", info.FullName);
        SetString(context.State, table, "assembly", info.AssemblyName);
        table.Set(
            LuaValue.FromString(context.State.Strings.GetOrCreate("value_type"u8)),
            LuaValue.FromBoolean(info.IsValueType));
        table.Set(
            LuaValue.FromString(context.State.Strings.GetOrCreate("constructible"u8)),
            LuaValue.FromBoolean(info.IsConstructible));
        var constructors = context.State.CreateTable(0, info.Constructors.Length);
        for (var index = 0; index < info.Constructors.Length; index++)
        {
            var constructor = context.State.CreateTable(0, 1);
            var parameters = context.State.CreateTable(0, info.Constructors[index].ParameterTypeNames.Length);
            for (var parameterIndex = 0;
                 parameterIndex < info.Constructors[index].ParameterTypeNames.Length;
                 parameterIndex++)
            {
                parameters.Set(
                    LuaValue.FromInteger(parameterIndex + 1),
                    LuaValue.FromString(context.State.Strings.GetOrCreate(
                        System.Text.Encoding.UTF8.GetBytes(
                            info.Constructors[index].ParameterTypeNames[parameterIndex]))));
            }

            constructor.Set(
                LuaValue.FromString(context.State.Strings.GetOrCreate("parameters"u8)),
                LuaValue.FromTable(parameters));
            constructors.Set(
                LuaValue.FromInteger(index + 1),
                LuaValue.FromTable(constructor));
        }

        table.Set(
            LuaValue.FromString(context.State.Strings.GetOrCreate("constructors"u8)),
            LuaValue.FromTable(constructors));
        return LuaValue.FromTable(table);
    }

    private static LuaValue CreateFromLua(
        LuaNativeCallContext context,
        ReadOnlySpan<LuaValue> values)
    {
        var bridge = GetBridge(context);
        var typeName = CheckString(context, values, 0, "clr.new");
        return LuaValue.FromUserdata(bridge.CreateInstance(typeName, values[1..]));
    }

    private static LuaClrBridge GetBridge(LuaNativeCallContext context) =>
        context.Captures.Count == 1 &&
        context.Captures[0].Kind == LuaValueKind.LightUserdata &&
        context.Captures[0].AsLightUserdata().Identity is LuaClrBridge bridge
            ? bridge
            : throw new LuaClrException(
                LuaClrErrorCode.CapabilityDenied,
                "The CLR bridge capture is invalid.");

    private static string CheckString(
        LuaNativeCallContext context,
        ReadOnlySpan<LuaValue> values,
        int index,
        string function)
    {
        if ((uint)index >= (uint)values.Length || values[index].Kind != LuaValueKind.String)
        {
            throw new LuaRuntimeException(
                $"bad argument #{index + 1} to '{function}' (string expected)");
        }

        return values[index].AsString().ToString();
    }

    private static void SetString(LuaState state, LuaTable table, string name, string value) =>
        table.Set(
            LuaValue.FromString(state.Strings.GetOrCreate(System.Text.Encoding.UTF8.GetBytes(name))),
            LuaValue.FromString(state.Strings.GetOrCreate(System.Text.Encoding.UTF8.GetBytes(value))));

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070",
        Justification = "The embedding application must preserve public constructors for each exact-allowlist type.")]
    private static LuaClrTypeInfo Describe(Type type)
    {
        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Select(static constructor => new LuaClrConstructorInfo(
                [.. constructor.GetParameters().Select(parameter =>
                    parameter.ParameterType.FullName ?? parameter.ParameterType.Name)]))
            .OrderBy(static constructor => string.Join('|', constructor.ParameterTypeNames), StringComparer.Ordinal)
            .ToImmutableArray();
        var constructible = !type.IsAbstract &&
            !type.IsInterface &&
            (constructors.Length > 0 || type.IsValueType);
        return new LuaClrTypeInfo(
            type.FullName ?? type.Name,
            type.Assembly.GetName().Name ?? string.Empty,
            type.IsValueType,
            constructible,
            constructors);
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026",
        Justification = "The bridge searches only already loaded assemblies; applications preserve exact-allowlist type metadata.")]
    private Type ResolveAllowedType(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName) || typeName.Length > _options.MaximumTypeNameLength)
        {
            throw new LuaClrException(
                LuaClrErrorCode.InvalidTypeName,
                "The CLR type name is empty or exceeds the configured length limit.");
        }

        if (!_allowedTypes.Contains(typeName))
        {
            throw new LuaClrException(
                LuaClrErrorCode.TypeNotAllowed,
                $"CLR type '{typeName}' is not allowlisted.");
        }

        var matches = AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => _allowedAssemblies.Contains(assembly.GetName().Name ?? string.Empty))
            .Select(assembly => assembly.GetType(typeName, throwOnError: false, ignoreCase: false))
            .Where(static type => type is not null)
            .Cast<Type>()
            .ToArray();
        var match = matches.Length switch
        {
            0 => throw new LuaClrException(
                LuaClrErrorCode.TypeNotFound,
                $"Allowlisted CLR type '{typeName}' is not loaded."),
            1 => matches[0],
            _ => throw new LuaClrException(
                LuaClrErrorCode.AmbiguousType,
                $"Allowlisted CLR type '{typeName}' resolves to multiple assemblies."),
        };
        if (!IsPubliclyVisible(match))
        {
            throw new LuaClrException(
                LuaClrErrorCode.TypeNotAllowed,
                $"CLR type '{typeName}' is not publicly visible.");
        }

        return match;
    }

    private static bool IsPubliclyVisible(Type type)
    {
        for (var current = type; current is not null; current = current.DeclaringType)
        {
            if (!current.IsPublic && !current.IsNestedPublic)
            {
                return false;
            }
        }

        return true;
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070",
        Justification = "The embedding application must preserve public constructors for each exact-allowlist type.")]
    private static ConstructorInfo? SelectConstructor(
        Type type,
        ReadOnlySpan<LuaValue> arguments,
        out object?[] converted)
    {
        converted = [];
        var candidates = new List<(ConstructorInfo Constructor, object?[] Arguments, int Score, string Signature)>();
        foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            var parameters = constructor.GetParameters();
            if (parameters.Length != arguments.Length)
            {
                continue;
            }

            var values = new object?[parameters.Length];
            var score = 0;
            var valid = true;
            for (var index = 0; index < parameters.Length; index++)
            {
                if (!TryConvert(arguments[index], parameters[index].ParameterType, out values[index], out var cost))
                {
                    valid = false;
                    break;
                }

                score += cost;
            }

            if (valid)
            {
                candidates.Add((
                    constructor,
                    values,
                    score,
                    string.Join('|', parameters.Select(parameter =>
                        parameter.ParameterType.FullName ?? parameter.ParameterType.Name))));
            }
        }

        var selected = candidates
            .OrderBy(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.Signature, StringComparer.Ordinal)
            .FirstOrDefault();
        if (selected.Constructor is null)
        {
            return null;
        }

        converted = selected.Arguments;
        return selected.Constructor;
    }

    private static bool TryConvert(
        LuaValue value,
        Type targetType,
        out object? converted,
        out int score)
    {
        var nullable = Nullable.GetUnderlyingType(targetType);
        var nonNullable = nullable ?? targetType;
        if (value.IsNil)
        {
            if (!nonNullable.IsValueType || nullable is not null)
            {
                converted = null;
                score = 1;
                return true;
            }

            converted = null;
            score = 0;
            return false;
        }

        if (value.Kind == LuaValueKind.Userdata)
        {
            var payload = value.AsUserdata().Payload;
            var instance = payload is LuaClrObject clrObject ? clrObject.Instance : payload;
            if (instance is not null && nonNullable.IsInstanceOfType(instance))
            {
                converted = instance;
                score = nonNullable == instance.GetType() ? 0 : 2;
                return true;
            }
        }

        if (nonNullable == typeof(LuaValue))
        {
            converted = value;
            score = 0;
            return true;
        }

        if (nonNullable == typeof(string) && value.Kind == LuaValueKind.String)
        {
            converted = value.AsString().ToString();
            score = 0;
            return true;
        }

        if (nonNullable == typeof(bool) && value.Kind == LuaValueKind.Boolean)
        {
            converted = value.AsBoolean();
            score = 0;
            return true;
        }

        if (nonNullable == typeof(char) && value.Kind == LuaValueKind.String)
        {
            var text = value.AsString().ToString();
            if (text.Length == 1)
            {
                converted = text[0];
                score = 1;
                return true;
            }
        }

        if (nonNullable.IsEnum)
        {
            if (value.Kind == LuaValueKind.String)
            {
                var enumName = value.AsString().ToString();
                if (Enum.GetNames(nonNullable).Contains(enumName, StringComparer.Ordinal))
                {
                    converted = Enum.Parse(nonNullable, enumName, ignoreCase: false);
                    score = 1;
                    return true;
                }
            }

            if (value.TryGetInteger(out var enumInteger))
            {
                try
                {
                    var underlying = Convert.ChangeType(
                        enumInteger,
                        Enum.GetUnderlyingType(nonNullable),
                        CultureInfo.InvariantCulture);
                    converted = Enum.ToObject(nonNullable, underlying!);
                    score = 2;
                    return true;
                }
                catch (Exception exception) when (exception is OverflowException or InvalidCastException)
                {
                }
            }
        }

        if (IsNumeric(nonNullable) && value.Kind is LuaValueKind.Integer or LuaValueKind.Float)
        {
            var number = value.Kind == LuaValueKind.Integer
                ? (object)value.AsInteger()
                : value.AsFloat();
            try
            {
                converted = Convert.ChangeType(number, nonNullable, CultureInfo.InvariantCulture);
                score = (nonNullable == typeof(long) && value.Kind == LuaValueKind.Integer) ||
                    (nonNullable == typeof(double) && value.Kind == LuaValueKind.Float)
                    ? 0
                    : 1;
                return value.Kind == LuaValueKind.Integer ||
                    double.IsFinite((double)number) ||
                    nonNullable == typeof(double) ||
                    nonNullable == typeof(float);
            }
            catch (Exception exception) when (exception is InvalidCastException or OverflowException or FormatException)
            {
            }
        }

        if (nonNullable == typeof(object))
        {
            converted = value.Kind switch
            {
                LuaValueKind.String => value.AsString().ToString(),
                LuaValueKind.Boolean => value.AsBoolean(),
                LuaValueKind.Integer => value.AsInteger(),
                LuaValueKind.Float => value.AsFloat(),
                _ => null,
            };
            if (converted is not null)
            {
                score = 10;
                return true;
            }
        }

        converted = null;
        score = 0;
        return false;
    }

    private static bool IsNumeric(Type type) => type == typeof(byte) ||
        type == typeof(sbyte) ||
        type == typeof(short) ||
        type == typeof(ushort) ||
        type == typeof(int) ||
        type == typeof(uint) ||
        type == typeof(long) ||
        type == typeof(ulong) ||
        type == typeof(float) ||
        type == typeof(double) ||
        type == typeof(decimal);

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "The embedding application must preserve public constructors for each exact-allowlist type.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2067",
        Justification = "The embedding application must preserve public constructors for each exact-allowlist type.")]
    private static object CreateDefaultValueType(Type type) => Activator.CreateInstance(type)!;

    private void RequireCapability(LuaClrCapabilities capability)
    {
        if ((_options.Capabilities & capability) != capability)
        {
            throw new LuaClrException(
                LuaClrErrorCode.CapabilityDenied,
                $"The CLR capability '{capability}' is not enabled for this host.");
        }
    }

    private static ImmutableHashSet<string> NormalizeNames(
        ImmutableArray<string> values,
        string parameterName)
    {
        var builder = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        foreach (var value in values.IsDefault ? [] : values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "CLR allowlist entries must not be empty.",
                    parameterName);
            }

            builder.Add(value);
        }

        return builder.ToImmutable();
    }
}
