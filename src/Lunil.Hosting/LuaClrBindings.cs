using System.Collections.Immutable;
using System.Text;
using Lunil.Analysis;
using Lunil.Runtime.Values;

namespace Lunil.Hosting;

/// <summary>Controls how CLR enum values are represented in Lua.</summary>
public enum LuaClrEnumRepresentation : byte
{
    /// <summary>Represent an enum by its exact, case-sensitive symbolic name.</summary>
    Name,

    /// <summary>Represent an enum by its underlying integral value.</summary>
    UnderlyingValue,

    /// <summary>Represent an enum as a table containing <c>name</c> and <c>value</c>.</summary>
    NameAndInteger,
}

/// <summary>Controls how CLR <see cref="decimal"/> values cross the Lua boundary.</summary>
public enum LuaClrDecimalRepresentation : byte
{
    /// <summary>Use an invariant string and preserve all decimal digits.</summary>
    ExactString,

    /// <summary>Use a Lua integer when exact, otherwise reject the conversion.</summary>
    ExactInteger,

    /// <summary>Explicitly permit conversion to a Lua float with possible precision loss.</summary>
    LossyFloat,
}

/// <summary>Controls whether CLR collections are projected into Lua values.</summary>
public enum LuaClrCollectionProjection : byte
{
    /// <summary>Keep collections as ordinary CLR userdata.</summary>
    Userdata,

    /// <summary>Project lists and dictionaries to tables and other enumerables to bounded iterators.</summary>
    TablesAndIterators,
}

/// <summary>Controls whether reflection may be used when no static binding exists.</summary>
public enum LuaClrBindingMode : byte
{
    /// <summary>Require a registered binding for every construction, member, delegate, and event operation.</summary>
    RegistryOnly,

    /// <summary>Prefer registered bindings and retain the 0.12 exact-allowlist reflection fallback.</summary>
    RegistryThenReflection,
}

/// <summary>Controls how ref/out values are returned by the Lua <c>clr.call</c> function.</summary>
public enum LuaClrRefOutRepresentation : byte
{
    /// <summary>Return ref/out values positionally after the ordinary return value.</summary>
    Positional,

    /// <summary>Return one table keyed by stable CLR parameter names.</summary>
    NamedTable,

    /// <summary>Return positional values followed by the named table.</summary>
    PositionalAndNamedTable,
}

/// <summary>Stable conversion limits applied to nested CLR data projections.</summary>
public sealed record LuaClrConversionLimits
{
    /// <summary>Gets the default bounded conversion policy.</summary>
    public static LuaClrConversionLimits Default { get; } = new();

    /// <summary>Gets the maximum nested collection/tuple depth.</summary>
    public int MaximumDepth { get; init; } = 32;

    /// <summary>Gets the maximum total projected items in one conversion.</summary>
    public int MaximumItems { get; init; } = 65_536;

    /// <summary>Gets the maximum estimated payload bytes in one conversion.</summary>
    public long MaximumBytes { get; init; } = 16 * 1024 * 1024;
}

/// <summary>A named ref/out value returned by a CLR invocation.</summary>
public readonly record struct LuaClrRefOutValue(string Name, LuaValue Value);

/// <summary>Describes one statically registered CLR parameter.</summary>
public sealed record LuaClrParameterBinding(
    string Name,
    Type ParameterType,
    bool IsByRef = false,
    bool IsOut = false,
    bool HasDefaultValue = false,
    object? DefaultValue = null);

/// <summary>Invokes a statically generated CLR constructor.</summary>
/// <param name="arguments">Converted CLR arguments.</param>
public delegate object LuaClrConstructorInvoker(object?[] arguments);

/// <summary>Invokes a statically generated CLR member and mutates ref/out argument slots.</summary>
public delegate object? LuaClrMemberInvoker(object? target, object?[] arguments);

/// <summary>Adapts a bridge callback to a statically generated CLR delegate.</summary>
public delegate Delegate LuaClrDelegateFactory(Func<object?[], object?> callback);

/// <summary>Describes one reflection-free constructor binding.</summary>
public sealed record LuaClrConstructorBinding(
    ImmutableArray<LuaClrParameterBinding> Parameters,
    LuaClrConstructorInvoker Invoker)
{
    /// <summary>Creates a constructor binding from C# 9-compatible generated arrays.</summary>
    public LuaClrConstructorBinding(
        IEnumerable<LuaClrParameterBinding> parameters,
        LuaClrConstructorInvoker invoker)
        : this(parameters.ToImmutableArray(), invoker)
    {
    }
}

/// <summary>Describes one reflection-free CLR member binding.</summary>
public sealed record LuaClrMemberBinding(
    string Name,
    LuaClrMemberKind Kind,
    bool IsStatic,
    bool CanRead,
    bool CanWrite,
    ImmutableArray<LuaClrParameterBinding> Parameters,
    Type ReturnType,
    LuaClrMemberInvoker Invoker)
{
    /// <summary>Creates a member binding from C# 9-compatible generated arrays.</summary>
    public LuaClrMemberBinding(
        string name,
        LuaClrMemberKind kind,
        bool isStatic,
        bool canRead,
        bool canWrite,
        IEnumerable<LuaClrParameterBinding> parameters,
        Type returnType,
        LuaClrMemberInvoker invoker)
        : this(name, kind, isStatic, canRead, canWrite,
            parameters.ToImmutableArray(), returnType, invoker)
    {
    }
}

/// <summary>Contains reflection-free bindings for one exact CLR type.</summary>
public sealed class LuaClrTypeBinding
{
    /// <summary>Creates a binding from C# 9-compatible generated arrays.</summary>
    public LuaClrTypeBinding(
        Type clrType,
        IEnumerable<LuaClrConstructorBinding> constructors,
        IEnumerable<LuaClrMemberBinding> members,
        LuaClrDelegateFactory? delegateFactory = null,
        Type? delegateReturnType = null)
        : this(clrType, constructors.ToImmutableArray(), members.ToImmutableArray(),
            delegateFactory, delegateReturnType)
    {
    }

    /// <summary>Creates an immutable exact-type binding.</summary>
    public LuaClrTypeBinding(
        Type clrType,
        ImmutableArray<LuaClrConstructorBinding> constructors,
        ImmutableArray<LuaClrMemberBinding> members,
        LuaClrDelegateFactory? delegateFactory = null,
        Type? delegateReturnType = null)
    {
        LunilGuard.NotNull(clrType);
        if (clrType.ContainsGenericParameters)
        {
            throw new ArgumentException("A CLR binding must target an exact closed type.", nameof(clrType));
        }

        ClrType = clrType;
        Constructors = constructors.IsDefault ? [] : constructors;
        Members = members.IsDefault ? [] : members;
        DelegateFactory = delegateFactory;
        DelegateReturnType = delegateReturnType;
        if ((DelegateFactory is null) != (DelegateReturnType is null))
        {
            throw new ArgumentException("Delegate factory and return type must be supplied together.");
        }
        Validate();
    }

    /// <summary>Gets the exact CLR type.</summary>
    public Type ClrType { get; }

    /// <summary>Gets its exact full name.</summary>
    public string TypeName => ClrType.FullName ?? ClrType.Name;

    /// <summary>Gets its simple assembly name.</summary>
    public string AssemblyName => ClrType.Assembly.GetName().Name ?? string.Empty;

    /// <summary>Gets its registered constructors.</summary>
    public ImmutableArray<LuaClrConstructorBinding> Constructors { get; }

    /// <summary>Gets its registered members.</summary>
    public ImmutableArray<LuaClrMemberBinding> Members { get; }

    /// <summary>Gets the optional statically generated delegate factory.</summary>
    public LuaClrDelegateFactory? DelegateFactory { get; }

    /// <summary>Gets the generated delegate return type, or <see langword="null"/> for non-delegates.</summary>
    public Type? DelegateReturnType { get; }

    private void Validate()
    {
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var constructor in Constructors)
        {
            LunilGuard.NotNull(constructor);
            LunilGuard.NotNull(constructor.Invoker);
            ValidateParameters(constructor.Parameters);
            if (!signatures.Add(".ctor|" + Signature(constructor.Parameters)))
            {
                throw new ArgumentException($"Duplicate generated constructor binding for '{TypeName}'.");
            }
        }

        foreach (var member in Members)
        {
            LunilGuard.NotNull(member);
            LunilGuard.NotNull(member.Invoker);
            LunilGuard.NotNull(member.ReturnType);
            if (string.IsNullOrWhiteSpace(member.Name))
            {
                throw new ArgumentException("A generated member binding has an empty name.");
            }
            ValidateParameters(member.Parameters);
            var signature = $"{member.Name}|{member.Kind}|{member.IsStatic}|{Signature(member.Parameters)}";
            if (!signatures.Add(signature))
            {
                throw new ArgumentException($"Duplicate generated member binding '{TypeName}.{member.Name}'.");
            }
        }
    }

    private static void ValidateParameters(ImmutableArray<LuaClrParameterBinding> parameters)
    {
        if (parameters.IsDefault)
        {
            throw new ArgumentException("Generated binding parameter arrays must be initialized.");
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var parameter in parameters)
        {
            LunilGuard.NotNull(parameter);
            LunilGuard.NotNull(parameter.ParameterType);
            if (string.IsNullOrWhiteSpace(parameter.Name) || !names.Add(parameter.Name))
            {
                throw new ArgumentException("Generated binding parameter names must be non-empty and unique.");
            }
            if (parameter.IsOut && !parameter.IsByRef)
            {
                throw new ArgumentException("An out binding parameter must also be marked by-ref.");
            }
            if (parameter.ParameterType.IsByRef || parameter.ParameterType.IsPointer || parameter.ParameterType.IsByRefLike)
            {
                throw new ArgumentException("Binding parameter types use their element type and cannot be ref-like or pointer types.");
            }
        }
    }

    private static string Signature(ImmutableArray<LuaClrParameterBinding> parameters) =>
        string.Join("|", parameters.Select(static parameter =>
            $"{parameter.ParameterType.AssemblyQualifiedName}:{parameter.IsByRef}:{parameter.IsOut}"));
}

/// <summary>Registers exact, reflection-free CLR bindings and explicitly allowlisted closed generic types.</summary>
public sealed class LuaClrBindingRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LuaClrTypeBinding> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _closedGenerics = new(StringComparer.Ordinal);

    /// <summary>Registers one exact type. Conflicting registrations fail closed.</summary>
    public void Register(LuaClrTypeBinding binding)
    {
        LunilGuard.NotNull(binding);
        lock (_gate)
        {
            if (_types.TryGetValue(binding.TypeName, out var existing))
            {
                if (!ReferenceEquals(existing, binding))
                {
                    throw new LuaClrException(LuaClrErrorCode.BindingConflict,
                        $"CLR type '{binding.TypeName}' has conflicting static bindings.");
                }
                return;
            }
            _types.Add(binding.TypeName, binding);
        }
    }

    /// <summary>Registers a closed generic type under an exact generic definition and argument list.</summary>
    public void RegisterClosedGeneric(
        string genericTypeName,
        ImmutableArray<string> typeArgumentNames,
        LuaClrTypeBinding binding)
    {
        if (string.IsNullOrWhiteSpace(genericTypeName))
        {
            throw new ArgumentException("A generic type name is required.", nameof(genericTypeName));
        }
        if (typeArgumentNames.IsDefaultOrEmpty || typeArgumentNames.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one exact generic argument type name is required.", nameof(typeArgumentNames));
        }
        Register(binding);
        var key = ClosedGenericKey(genericTypeName, typeArgumentNames);
        lock (_gate)
        {
            if (_closedGenerics.TryGetValue(key, out var existing) &&
                !string.Equals(existing, binding.TypeName, StringComparison.Ordinal))
            {
                throw new LuaClrException(LuaClrErrorCode.BindingConflict,
                    $"Closed generic binding '{key}' has conflicting registrations.");
            }
            _closedGenerics[key] = binding.TypeName;
        }
    }

    /// <summary>Registers a closed generic using a C# 9-compatible generated string array.</summary>
    public void RegisterClosedGeneric(
        string genericTypeName,
        IEnumerable<string> typeArgumentNames,
        LuaClrTypeBinding binding) => RegisterClosedGeneric(
            genericTypeName, typeArgumentNames.ToImmutableArray(), binding);

    /// <summary>Attempts to retrieve an exact type binding.</summary>
    public bool TryGet(string typeName, out LuaClrTypeBinding? binding)
    {
        lock (_gate)
        {
            return _types.TryGetValue(typeName, out binding);
        }
    }

    /// <summary>Resolves only a pre-registered closed generic binding.</summary>
    public LuaClrTypeBinding ResolveClosedGeneric(
        string genericTypeName,
        ImmutableArray<string> typeArgumentNames)
    {
        var key = ClosedGenericKey(genericTypeName, typeArgumentNames);
        lock (_gate)
        {
            if (_closedGenerics.TryGetValue(key, out var typeName) &&
                _types.TryGetValue(typeName, out var binding))
            {
                return binding;
            }
        }
        throw new LuaClrException(LuaClrErrorCode.TypeNotAllowed,
            $"Closed generic binding '{key}' is not registered.");
    }

    /// <summary>Returns a deterministic snapshot of registered exact bindings.</summary>
    public ImmutableArray<LuaClrTypeBinding> GetBindings()
    {
        lock (_gate)
        {
            return [.. _types.Values.OrderBy(static binding => binding.TypeName, StringComparer.Ordinal)];
        }
    }

    /// <summary>Creates a Unity-compatible linker descriptor for the registered exact types.</summary>
    public string CreateUnityLinkXml()
    {
        var builder = new StringBuilder("<linker>\n");
        foreach (var assembly in GetBindings().GroupBy(static item => item.AssemblyName, StringComparer.Ordinal)
                     .OrderBy(static group => group.Key, StringComparer.Ordinal))
        {
            builder.Append("  <assembly fullname=\"").Append(XmlEscape(assembly.Key)).Append("\">\n");
            foreach (var binding in assembly.OrderBy(static item => item.TypeName, StringComparer.Ordinal))
            {
                builder.Append("    <type fullname=\"").Append(XmlEscape(binding.TypeName))
                    .Append("\" preserve=\"nothing\" />\n");
            }
            builder.Append("  </assembly>\n");
        }
        return builder.Append("</linker>\n").ToString();
    }

    /// <summary>
    /// Creates the same language-neutral analysis contract from the exact metadata used by the
    /// runtime registry. Generated C# bindings therefore cannot silently drift from editor types.
    /// </summary>
    public LuaHostAnalysisContract CreateAnalysisContract(
        string contractId,
        string globalName = "clr")
    {
        var builder = new LuaHostContractBuilder(contractId);
        foreach (var binding in GetBindings())
        {
            var typePath = globalName + "." + SanitizeContractName(binding.TypeName);
            foreach (var constructor in binding.Constructors)
            {
                builder.AddFunction(new LuaHostFunctionContract
                {
                    Path = typePath + ".new",
                    Parameters = [.. constructor.Parameters.Select(ToHostParameter)],
                    Returns = [ToHostType(binding.ClrType)],
                    Effects = LuaHostEffectKind.MayThrow,
                    Source = ContractSource(binding, ".ctor"),
                });
            }

            foreach (var member in binding.Members.Where(static item =>
                         item.Kind is LuaClrMemberKind.Method or LuaClrMemberKind.Operator or
                         LuaClrMemberKind.Indexer or LuaClrMemberKind.Event))
            {
                var callbackIndex = member.Kind == LuaClrMemberKind.Event
                    ? FindDelegateParameter(member.Parameters)
                    : -1;
                builder.AddFunction(new LuaHostFunctionContract
                {
                    Path = typePath + "." + member.Name,
                    Parameters = [.. member.Parameters.Select(ToHostParameter)],
                    Returns = member.ReturnType == typeof(void) ? [] : [ToHostType(member.ReturnType)],
                    Effects = LuaHostEffectKind.MayThrow |
                        (callbackIndex >= 0 ? LuaHostEffectKind.RegistersCallback : LuaHostEffectKind.None),
                    Callback = callbackIndex < 0 ? null : new LuaHostCallbackContract
                    {
                        ParameterIndex = callbackIndex,
                        Invocation = LuaHostCallbackInvocationKind.Deferred,
                        Cardinality = LuaHostCallbackCardinality.Many,
                        Retention = LuaHostCallbackRetentionKind.Stored,
                        MayThrow = true,
                    },
                    Source = ContractSource(binding, member.Name),
                });
            }
        }

        return builder.Build();
    }

    private static string ClosedGenericKey(string genericTypeName, ImmutableArray<string> arguments) =>
        genericTypeName + "[" + string.Join(",", arguments) + "]";

    private static LuaHostParameterContract ToHostParameter(LuaClrParameterBinding parameter) => new()
    {
        Name = parameter.Name,
        Type = ToHostType(parameter.ParameterType),
        IsOptional = parameter.HasDefaultValue || parameter.IsOut,
    };

    private static int FindDelegateParameter(ImmutableArray<LuaClrParameterBinding> parameters)
    {
        for (var index = 0; index < parameters.Length; index++)
        {
            if (typeof(Delegate).IsAssignableFrom(parameters[index].ParameterType))
            {
                return index;
            }
        }

        return -1;
    }

    private static LuaHostTypeDescriptor ToHostType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(void))
        {
            return new LuaHostTypeDescriptor { Kind = LuaHostTypeKind.Nil };
        }

        if (type == typeof(bool))
        {
            return new LuaHostTypeDescriptor { Kind = LuaHostTypeKind.Boolean };
        }

        if (type == typeof(string) || type == typeof(char))
        {
            return new LuaHostTypeDescriptor { Kind = LuaHostTypeKind.String };
        }

        if (type.IsEnum || type == typeof(byte) || type == typeof(sbyte) ||
            type == typeof(short) || type == typeof(ushort) || type == typeof(int) ||
            type == typeof(uint) || type == typeof(long) || type == typeof(ulong))
        {
            return new LuaHostTypeDescriptor { Kind = LuaHostTypeKind.Integer };
        }

        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
        {
            return new LuaHostTypeDescriptor { Kind = LuaHostTypeKind.Number };
        }

        if (typeof(Delegate).IsAssignableFrom(type))
        {
            return new LuaHostTypeDescriptor
            {
                Kind = LuaHostTypeKind.Function,
                Name = type.FullName,
                Parameters = [new LuaHostParameterContract
                {
                    Name = "...",
                    Type = new LuaHostTypeDescriptor { Kind = LuaHostTypeKind.Any },
                    IsOptional = true,
                }],
                HasVariadicParameters = true,
                HasVariadicReturns = true,
            };
        }

        if (type.IsArray)
        {
            return new LuaHostTypeDescriptor
            {
                Kind = LuaHostTypeKind.Table,
                Name = type.FullName,
            };
        }

        return new LuaHostTypeDescriptor
        {
            Kind = LuaHostTypeKind.Userdata,
            Name = type.FullName ?? type.Name,
            IsNullable = !type.IsValueType,
        };
    }

    private static LuaHostSourceLocation ContractSource(
        LuaClrTypeBinding binding,
        string member) => new()
        {
            Uri = $"dotnet://{binding.AssemblyName}/{binding.TypeName}#{member}",
            Line = 1,
            Column = 1,
            ImplementationUri = $"dotnet-implementation://{binding.AssemblyName}/{binding.TypeName}#{member}",
        };

    private static string SanitizeContractName(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            result.Append(char.IsLetterOrDigit(character) || character == '_'
                ? character
                : '_');
        }

        if (result.Length == 0 || !(result[0] == '_' || char.IsLetter(result[0])))
        {
            result.Insert(0, '_');
        }

        return result.ToString();
    }

    private static string XmlEscape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}

/// <summary>Implemented by generated binding providers.</summary>
public interface ILuaClrBindingProvider
{
    /// <summary>Adds all generated bindings to <paramref name="registry"/>.</summary>
    void RegisterBindings(LuaClrBindingRegistry registry);
}

/// <summary>Requests reflection-free bindings for a type from the Lunil binding generator.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class LuaClrGenerateBindingAttribute : Attribute
{
    /// <summary>Creates a request for all listed public members of an exact type.</summary>
    public LuaClrGenerateBindingAttribute(Type type, params string[] memberNames)
    {
        Type = type;
        MemberNames = memberNames ?? [];
    }

    /// <summary>Gets the exact type to bind.</summary>
    public Type Type { get; }

    /// <summary>Gets exact, case-sensitive member names. An empty list binds public constructors only.</summary>
    public IReadOnlyList<string> MemberNames { get; }
}
