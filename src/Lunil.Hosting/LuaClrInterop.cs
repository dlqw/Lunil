using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Operations;
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

    /// <summary>Allows allowlisted static and instance member access.</summary>
    MemberAccess = 4,

    /// <summary>Allows Lua functions to be adapted to allowlisted CLR delegates.</summary>
    DelegateConversion = 8,

    /// <summary>Allows event subscriptions that are explicitly allowlisted.</summary>
    EventSubscription = 16,

    /// <summary>Allows task/ValueTask results to be awaited through the bridge.</summary>
    Async = 32,

    /// <summary>Allows CLR disposal and explicit resource release.</summary>
    Disposal = 64,
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

    /// <summary>The requested member is outside the configured member allowlist.</summary>
    MemberNotAllowed,

    /// <summary>The requested member does not exist or is inaccessible.</summary>
    MemberNotFound,

    /// <summary>No overload accepts the supplied arguments.</summary>
    NoMatchingMember,

    /// <summary>A CLR member invocation failed.</summary>
    InvocationFailed,

    /// <summary>A Lua callback/delegate signature is unsupported.</summary>
    InvalidDelegate,

    /// <summary>A callback or event subscription is no longer valid.</summary>
    SubscriptionClosed,

    /// <summary>A task result cannot be represented by the bridge.</summary>
    AsyncFailed,

    /// <summary>The operation was attempted from an unsupported thread.</summary>
    ThreadDenied,

    /// <summary>The supplied ref/out arguments are invalid.</summary>
    InvalidRefOut,
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

    /// <summary>
    /// Gets exact member allowlist entries. Entries may be either a member name (applied to every
    /// allowlisted type) or <c>Full.Type.Name.Member</c>. Overloads are selected only after this
    /// allowlist check; reflection never becomes an unrestricted escape hatch.
    /// </summary>
    public ImmutableArray<string> AllowedMemberNames { get; init; } = [];

    /// <summary>Gets exact delegate type names that Lua functions may implement.</summary>
    public ImmutableArray<string> AllowedDelegateTypeNames { get; init; } = [];

    /// <summary>Gets exact event names that may be subscribed or unsubscribed.</summary>
    public ImmutableArray<string> AllowedEventNames { get; init; } = [];

    /// <summary>Gets whether <see cref="LuaHost"/> installs the global Lua <c>clr</c> table.</summary>
    public bool InstallGlobalModule { get; init; }

    /// <summary>Gets the accepted type-name length bound. The valid range is 1 through 4096.</summary>
    public int MaximumTypeNameLength { get; init; } = 256;

    /// <summary>Gets whether userdata owns instances constructed by the bridge.</summary>
    public bool OwnConstructedObjects { get; init; } = true;

    /// <summary>Gets the policy used when a CLR callback is invoked from a non-owner thread.</summary>
    public LuaClrThreadPolicy ThreadPolicy { get; init; } = LuaClrThreadPolicy.OwnerThreadOnly;

    /// <summary>Gets whether CLR exceptions are exposed to Lua with their messages.</summary>
    public bool IncludeExceptionMessages { get; init; }

    /// <summary>Gets the maximum number of members cached for one type.</summary>
    public int MaximumCachedMembers { get; init; } = 256;
}

/// <summary>Controls CLR callback thread admission.</summary>
public enum LuaClrThreadPolicy : byte
{
    /// <summary>Only the thread that created the bridge may enter Lua.</summary>
    OwnerThreadOnly,

    /// <summary>Allow callbacks from any thread when the host scheduler is idle.</summary>
    AnyThreadWhenIdle,
}

/// <summary>Describes a reflected member exposed through the bridge.</summary>
public enum LuaClrMemberKind : byte
{
    Method,
    Property,
    Field,
    Indexer,
    Operator,
    Event,
}

/// <summary>Stable, reflection-free member metadata returned by discovery APIs.</summary>
public sealed record LuaClrMemberInfo(
    string Name,
    LuaClrMemberKind Kind,
    bool IsStatic,
    bool CanRead,
    bool CanWrite,
    ImmutableArray<string> ParameterTypeNames,
    string ReturnTypeName);

/// <summary>Named CLR invocation argument used by host-side APIs.</summary>
public readonly record struct LuaClrNamedArgument(string Name, LuaValue Value);

/// <summary>Result of a CLR call, including ref/out values in declaration order.</summary>
public sealed record LuaClrInvocationResult(LuaValue ReturnValue, ImmutableArray<LuaValue> RefOutValues);

/// <summary>Opaque task wrapper returned by async CLR calls.</summary>
public sealed class LuaClrTask : IDisposable
{
    private readonly Task _task;
    private int _disposed;

    internal LuaClrTask(Task task, LuaClrBridge bridge)
    {
        _task = task;
        Bridge = bridge;
    }

    /// <summary>Gets the underlying task for host integration.</summary>
    public Task Task => _task;

    /// <summary>Gets the bridge that owns this task wrapper.</summary>
    public LuaClrBridge Bridge { get; }

    /// <summary>Gets whether the task completed.</summary>
    public bool IsCompleted => _task.IsCompleted;

    /// <summary>Gets whether the task faulted.</summary>
    public bool IsFaulted => _task.IsFaulted;

    /// <summary>Waits for completion and releases the wrapper.</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Task result metadata is preserved by the consuming application.")]
    internal object? GetResult()
    {
        _task.GetAwaiter().GetResult();
        return _task.GetType().GetProperty("Result")?.GetValue(_task);
    }
}

/// <summary>Bridge-owned cancellation source passed to allowlisted CLR calls.</summary>
public sealed class LuaClrCancellation : IDisposable
{
    private readonly CancellationTokenSource _source = new();
    private int _disposed;

    /// <summary>Gets the cancellation token supplied during conversion.</summary>
    public CancellationToken Token => _source.Token;

    /// <summary>Gets whether cancellation was requested.</summary>
    public bool IsCancellationRequested => _source.IsCancellationRequested;

    /// <summary>Requests cancellation.</summary>
    public void Cancel()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _source.Cancel();
    }

    /// <summary>Releases the underlying cancellation source once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _source.Dispose();
        }
    }
}

/// <summary>Disposable event/delegate subscription owned by one Lua state.</summary>
public sealed class LuaClrSubscription : IDisposable
{
    private readonly LuaClrCallbackRegistration _registration;
    private LuaHandle? _callbackHandle;
    private int _disposed;

    internal LuaClrSubscription(
        LuaClrCallbackRegistration registration,
        LuaValue callback,
        LuaHandle callbackHandle)
    {
        _registration = registration;
        Callback = callback;
        _callbackHandle = callbackHandle;
    }

    /// <summary>Gets the callback retained by this subscription.</summary>
    public LuaValue Callback { get; }

    /// <summary>Gets whether the subscription has been disposed.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Gets whether the callback is attached and admitted by the live patch generation.</summary>
    public bool IsActive => !IsDisposed && _registration.IsSubscriptionActive;

    /// <summary>Unsubscribes at most once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                _registration.Close();
            }
            finally
            {
                Interlocked.Exchange(ref _callbackHandle, null)?.Dispose();
            }
        }
    }
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
[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The host owns exact CLR interop metadata and preserves its allowlist.")]
[UnconditionalSuppressMessage("Trimming", "IL2067", Justification = "The host owns exact CLR interop metadata and preserves its allowlist.")]
[UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "The host owns exact CLR interop metadata and preserves its allowlist.")]
[UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "The host owns exact CLR interop metadata and preserves its allowlist.")]
[UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "The host owns exact CLR interop metadata and preserves its allowlist.")]
[UnconditionalSuppressMessage("Trimming", "IL2080", Justification = "The host owns exact CLR interop metadata and preserves its allowlist.")]
[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Exact allowlisted types are rooted by the host; delegate expressions use the interpreter on AOT runtimes.")]
public sealed partial class LuaClrBridge
{
    private readonly LuaState _state;
    private readonly LuaClrOptions _options;
    private readonly ImmutableHashSet<string> _allowedAssemblies;
    private readonly ImmutableHashSet<string> _allowedTypes;
    private readonly ImmutableHashSet<string> _allowedMembers;
    private readonly ImmutableHashSet<string> _allowedDelegates;
    private readonly ImmutableHashSet<string> _allowedEvents;
    private readonly ConcurrentDictionary<Type, ImmutableArray<MemberInfo>> _memberCache = [];
    private readonly object _callbackGate = new();
    private readonly int _ownerThreadId;

    /// <summary>Creates a bridge for one Lua state.</summary>
    public LuaClrBridge(LuaState state, LuaClrOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _options = options ?? LuaClrOptions.Disabled;
        const LuaClrCapabilities knownCapabilities =
            LuaClrCapabilities.TypeDiscovery | LuaClrCapabilities.Construction |
            LuaClrCapabilities.MemberAccess | LuaClrCapabilities.DelegateConversion |
            LuaClrCapabilities.EventSubscription | LuaClrCapabilities.Async |
            LuaClrCapabilities.Disposal;
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
        _allowedMembers = NormalizeNames(_options.AllowedMemberNames, nameof(options));
        _allowedDelegates = NormalizeNames(_options.AllowedDelegateTypeNames, nameof(options));
        _allowedEvents = NormalizeNames(_options.AllowedEventNames, nameof(options));
        if (_options.MaximumCachedMembers is < 1 or > 16_384)
        {
            throw new ArgumentOutOfRangeException(nameof(options), _options.MaximumCachedMembers,
                "The CLR member cache bound must be between 1 and 16384.");
        }
        if ((_options.Capabilities & (LuaClrCapabilities.TypeDiscovery |
                LuaClrCapabilities.Construction)) != LuaClrCapabilities.None &&
            _allowedAssemblies.Count == 0)
        {
            throw new ArgumentException(
                "CLR capabilities require at least one allowed assembly name.",
                nameof(options));
        }

        if ((_options.Capabilities & LuaClrCapabilities.MemberAccess) != LuaClrCapabilities.None &&
            _allowedMembers.Count == 0)
        {
            throw new ArgumentException(
                "Member access requires at least one allowed member name.", nameof(options));
        }

        if ((_options.Capabilities & LuaClrCapabilities.DelegateConversion) != LuaClrCapabilities.None &&
            _allowedDelegates.Count == 0)
        {
            throw new ArgumentException(
                "Delegate conversion requires at least one allowed delegate type name.", nameof(options));
        }

        _ownerThreadId = Environment.CurrentManagedThreadId;
    }

    /// <summary>Gets the Lua state that owns this bridge and its userdata.</summary>
    public LuaState State => _state;

    /// <summary>Gets the validated bridge configuration.</summary>
    public LuaClrOptions Options => _options;

    /// <summary>Gets whether this bridge grants at least one capability.</summary>
    public bool IsEnabled => _options.Capabilities != LuaClrCapabilities.None;

    /// <summary>Gets the managed thread that created this bridge.</summary>
    public int OwnerThreadId => _ownerThreadId;

    /// <summary>Returns a description of an allowed type in an already loaded assembly.</summary>
    public LuaClrTypeInfo ResolveType(string typeName)
    {
        RequireCapability(LuaClrCapabilities.TypeDiscovery);
        var type = ResolveAllowedType(typeName);
        return Describe(type);
    }

    internal Type ResolveAllowedTypeForHost(string typeName) => ResolveAllowedType(typeName);

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

        var userdata = _state.CreateUserdata(
            new LuaClrObject(instance, _options.OwnConstructedObjects),
            userValueCount: 1,
            payloadLogicalSize: 64);
        if ((_options.Capabilities & LuaClrCapabilities.MemberAccess) != LuaClrCapabilities.None)
        {
            AttachMetatable(userdata, type);
        }

        return userdata;
    }

    /// <summary>Returns allowlisted public members for an exact type.</summary>
    public ImmutableArray<LuaClrMemberInfo> ResolveMembers(string typeName)
    {
        RequireCapability(LuaClrCapabilities.MemberAccess);
        var type = ResolveAllowedType(typeName);
        return GetMembers(type)
            .Select(static member => DescribeMember(member))
            .OrderBy(static member => member.Name, StringComparer.Ordinal)
            .ThenBy(static member => member.Kind)
            .ToImmutableArray();
    }

    /// <summary>Reads an allowlisted instance or static member.</summary>
    public LuaValue GetMember(LuaValue target, string memberName, ReadOnlySpan<LuaValue> indexArguments = default)
    {
        RequireCapability(LuaClrCapabilities.MemberAccess);
        var (instance, type) = UnwrapTarget(target);
        EnsureMemberAllowed(type, memberName);
        var member = SelectMember(type, memberName, indexArguments, forWrite: false,
            requireStatic: instance is null);
        try
        {
            return ToLuaValue(member switch
            {
                PropertyInfo property => property.GetValue(instance, ConvertArguments(indexArguments, property.GetIndexParameters())),
                FieldInfo field => field.GetValue(instance),
                MethodInfo method => CreateBoundMethod(target, type, method.Name),
                _ => throw new LuaClrException(LuaClrErrorCode.MemberNotFound,
                    $"CLR member '{memberName}' is not readable."),
            });
        }
        catch (LuaClrException)
        {
            throw;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw InvocationFailure(memberName, exception.InnerException);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw InvocationFailure(memberName, exception);
        }
    }

    /// <summary>Writes an allowlisted instance or static property/field.</summary>
    public void SetMember(LuaValue target, string memberName, LuaValue value)
    {
        RequireCapability(LuaClrCapabilities.MemberAccess);
        var (instance, type) = UnwrapTarget(target);
        EnsureMemberAllowed(type, memberName);
        var member = SelectMember(type, memberName, [value], forWrite: true,
            requireStatic: instance is null);
        try
        {
            switch (member)
            {
                case PropertyInfo property when property.SetMethod is not null:
                    if (!TryConvert(value, property.PropertyType, out var propertyValue, out _))
                    {
                        throw NoMatchingMember(memberName);
                    }

                    property.SetValue(instance, propertyValue);
                    return;
                case FieldInfo field when !field.IsInitOnly:
                    if (!TryConvert(value, field.FieldType, out var fieldValue, out _))
                    {
                        throw NoMatchingMember(memberName);
                    }

                    field.SetValue(instance, fieldValue);
                    return;
                default:
                    throw new LuaClrException(LuaClrErrorCode.MemberNotFound,
                        $"CLR member '{memberName}' is not writable.");
            }
        }
        catch (LuaClrException)
        {
            throw;
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw InvocationFailure(memberName, exception.InnerException);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw InvocationFailure(memberName, exception);
        }
    }

    /// <summary>Invokes an allowlisted method, operator, or indexer.</summary>
    public LuaClrInvocationResult InvokeMember(
        LuaValue target,
        string memberName,
        ReadOnlySpan<LuaValue> arguments = default,
        ReadOnlySpan<LuaClrNamedArgument> namedArguments = default)
    {
        RequireCapability(LuaClrCapabilities.MemberAccess);
        var (instance, type) = UnwrapTarget(target);
        EnsureMemberAllowed(type, memberName);
        var methods = GetMembers(type).OfType<MethodInfo>()
            .Where(method => string.Equals(method.Name, memberName, StringComparison.Ordinal))
            .Where(method => instance is null ? method.IsStatic : !method.IsStatic)
            .ToArray();
        if (methods.Length == 0)
        {
            throw new LuaClrException(LuaClrErrorCode.MemberNotFound,
                $"CLR method '{memberName}' was not found.");
        }

        var selected = SelectMethod(methods, arguments, namedArguments);
        if (selected is null)
        {
            throw NoMatchingMember(memberName);
        }

        try
        {
            var result = selected.Value.Method.Invoke(instance, selected.Value.Arguments);
            var refOut = ImmutableArray.CreateBuilder<LuaValue>();
            var parameters = selected.Value.Method.GetParameters();
            for (var index = 0; index < parameters.Length; index++)
            {
                if (parameters[index].ParameterType.IsByRef)
                {
                    refOut.Add(ToLuaValue(selected.Value.Arguments[index]));
                }
            }

            return new LuaClrInvocationResult(ToLuaValue(result), refOut.ToImmutable());
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw InvocationFailure(memberName, exception.InnerException);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            throw InvocationFailure(memberName, exception);
        }
    }

    /// <summary>Invokes an allowlisted static method on an exact type.</summary>
    public LuaClrInvocationResult InvokeStatic(
        string typeName,
        string memberName,
        ReadOnlySpan<LuaValue> arguments = default,
        ReadOnlySpan<LuaClrNamedArgument> namedArguments = default)
    {
        var type = ResolveAllowedType(typeName);
        return InvokeMember(LuaValue.FromLightUserdata(new LuaLightUserdata(type)), memberName,
            arguments, namedArguments);
    }

    /// <summary>Creates a delegate of an allowlisted CLR delegate type backed by a Lua function.</summary>
    public Delegate CreateDelegate(LuaValue function, string delegateTypeName)
    {
        RequireCapability(LuaClrCapabilities.DelegateConversion);
        if (function.Kind != LuaValueKind.Function)
        {
            throw new LuaClrException(LuaClrErrorCode.InvalidDelegate, "A Lua function is required.");
        }

        var type = ResolveAllowedType(delegateTypeName);
        if (!typeof(Delegate).IsAssignableFrom(type) || type.GetMethod("Invoke") is null ||
            !_allowedDelegates.Contains(delegateTypeName))
        {
            throw new LuaClrException(LuaClrErrorCode.InvalidDelegate,
                $"CLR type '{delegateTypeName}' is not an allowed delegate type.");
        }

        ValidateDelegateSignature(type.GetMethod("Invoke")!);
        return BuildDelegate(type, CreateCallbackRegistration(function));
    }

    /// <summary>Subscribes a Lua function to an allowlisted CLR event.</summary>
    public LuaClrSubscription Subscribe(LuaValue target, string eventName, LuaValue callback)
    {
        RequireCapability(LuaClrCapabilities.EventSubscription);
        RequireCapability(LuaClrCapabilities.DelegateConversion);
        if (callback.Kind != LuaValueKind.Function)
        {
            throw new LuaClrException(LuaClrErrorCode.InvalidDelegate, "A Lua function is required.");
        }

        var (instance, type) = UnwrapTarget(target);
        EnsureEventAllowed(type, eventName);
        var eventInfo = type.GetEvent(eventName, BindingFlags.Public | BindingFlags.Instance |
            BindingFlags.Static) ?? throw new LuaClrException(LuaClrErrorCode.MemberNotFound,
                $"CLR event '{eventName}' was not found.");
        if (eventInfo.EventHandlerType is null)
        {
            throw new LuaClrException(LuaClrErrorCode.InvalidDelegate,
                $"CLR event '{eventName}' has no handler type.");
        }

        var handlerTypeName = eventInfo.EventHandlerType.FullName ?? eventInfo.EventHandlerType.Name;
        var handlerType = ResolveAllowedType(handlerTypeName);
        if (!_allowedDelegates.Contains(handlerTypeName))
        {
            throw new LuaClrException(
                LuaClrErrorCode.InvalidDelegate,
                $"CLR type '{handlerTypeName}' is not an allowed delegate type.");
        }

        ValidateDelegateSignature(handlerType.GetMethod("Invoke")!);
        var registration = CreateCallbackRegistration(callback);
        var handler = BuildDelegate(handlerType, registration);
        var handle = _state.CreateHandle(callback);
        try
        {
            eventInfo.AddEventHandler(instance, handler);
            registration.AttachSubscription(
                () => eventInfo.AddEventHandler(instance, handler),
                () => eventInfo.RemoveEventHandler(instance, handler));
            return new LuaClrSubscription(
                registration,
                callback,
                handle);
        }
        catch
        {
            registration.Close();
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Synchronously awaits a bridge task and converts its result to Lua.</summary>
    public LuaValue Await(LuaValue value)
    {
        RequireCapability(LuaClrCapabilities.Async);
        if (value.Kind != LuaValueKind.Userdata || value.AsUserdata().Payload is not LuaClrTask task)
        {
            throw new LuaClrException(LuaClrErrorCode.AsyncFailed, "A CLR task userdata is required.");
        }

        try
        {
            return ToLuaValue(task.GetResult());
        }
        catch (Exception exception)
        {
            throw new LuaClrException(LuaClrErrorCode.AsyncFailed, "CLR task failed.", exception);
        }
    }

    /// <summary>Creates a bridge-owned cancellation source userdata.</summary>
    public LuaUserdata CreateCancellation()
    {
        RequireCapability(LuaClrCapabilities.Async);
        return _state.CreateUserdata(new LuaClrCancellation(), 1, 32);
    }

    /// <summary>Requests cancellation through a bridge cancellation userdata.</summary>
    public void Cancel(LuaValue value)
    {
        RequireCapability(LuaClrCapabilities.Async);
        if (value.Kind != LuaValueKind.Userdata ||
            value.AsUserdata().Payload is not LuaClrCancellation cancellation)
        {
            throw new LuaClrException(LuaClrErrorCode.AsyncFailed,
                "A CLR cancellation userdata is required.");
        }
        cancellation.Cancel();
    }

    /// <summary>Disposes a bridge-owned CLR userdata or subscription.</summary>
    public void DisposeValue(LuaValue value)
    {
        RequireCapability(LuaClrCapabilities.Disposal);
        if (value.Kind == LuaValueKind.Userdata)
        {
            value.AsUserdata().DisposePayload();
            return;
        }

        if (value.Kind == LuaValueKind.LightUserdata && value.AsLightUserdata().Identity is IDisposable disposable)
        {
            disposable.Dispose();
            return;
        }

        throw new LuaClrException(LuaClrErrorCode.MemberNotFound, "Value is not disposable CLR userdata.");
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

        var module = _state.CreateTable(0, 12);
        var capture = LuaValue.FromLightUserdata(new LuaLightUserdata(this));
        AddModuleFunction(module, "type", new LuaNativeFunction("clr.type", TypeBody), capture);
        AddModuleFunction(module, "new", new LuaNativeFunction("clr.new", NewBody), capture);
        AddModuleFunction(module, "members", new LuaNativeFunction("clr.members", MembersBody), capture);
        AddModuleFunction(module, "call", new LuaNativeFunction("clr.call", CallBody), capture);
        AddModuleFunction(module, "get", new LuaNativeFunction("clr.get", GetBody), capture);
        AddModuleFunction(module, "set", new LuaNativeFunction("clr.set", SetBody), capture);
        AddModuleFunction(module, "on", new LuaNativeFunction("clr.on", SubscribeBody), capture);
        AddModuleFunction(module, "await", new LuaNativeFunction("clr.await", AwaitBody), capture);
        AddModuleFunction(module, "cancellation", new LuaNativeFunction("clr.cancellation", CancellationBody), capture);
        AddModuleFunction(module, "cancel", new LuaNativeFunction("clr.cancel", CancelBody), capture);
        AddModuleFunction(module, "dispose", new LuaNativeFunction("clr.dispose", DisposeBody), capture);
        _state.SetGlobal("clr", LuaValue.FromTable(module));
    }

    private void AddModuleFunction(
        LuaTable module,
        string name,
        LuaNativeFunction descriptor,
        LuaValue capture)
    {
        var functionName = LuaValue.FromString(_state.Strings.GetOrCreate(System.Text.Encoding.UTF8.GetBytes(name)));
        module.Set(
            LuaValue.FromString(_state.Strings.GetOrCreate(System.Text.Encoding.UTF8.GetBytes(name))),
            LuaValue.FromFunction(_state.CreateNativeClosure(
                new LuaNativeFunction($"clr.{name}", ModuleStep), [capture, functionName])));
    }

    private static LuaNativeStep ModuleStep(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values)
    {
        var name = context.Captures.Count > 1
            ? context.Captures[1].AsString().ToString()
            : string.Empty;
        var combined = new LuaValue[values.Length + 1];
        combined[0] = context.Captures[0];
        values.CopyTo(combined.AsSpan(1));
        var result = name switch
        {
            "type" => TypeBody(context.State, combined),
            "new" => NewBody(context.State, combined),
            "members" => MembersBody(context.State, combined),
            "call" => CallBody(context.State, combined),
            "get" => GetBody(context.State, combined),
            "set" => SetBody(context.State, combined),
            "on" => SubscribeBody(context.State, combined),
            "await" => AwaitBody(context.State, combined),
            "cancellation" => CancellationBody(context.State, combined),
            "cancel" => CancelBody(context.State, combined),
            "dispose" => DisposeBody(context.State, combined),
            _ => throw new LuaClrException(LuaClrErrorCode.MemberNotFound, "Unknown CLR module function."),
        };
        return LuaNativeStep.Completed(result);
    }

    private static LuaValue[] TypeBody(LuaState state, ReadOnlySpan<LuaValue> values)
    {
        try
        {
            return [ResolveTypeFromLua(GetBridge(state, values), state, values)];
        }
        catch (LuaClrException exception)
        {
            throw CreateLuaError(exception);
        }
    }

    private static LuaValue[] NewBody(LuaState state, ReadOnlySpan<LuaValue> values)
    {
        try
        {
            return [CreateFromLua(GetBridge(state, values), state, values)];
        }
        catch (LuaClrException exception)
        {
            throw CreateLuaError(exception);
        }
    }

    private static LuaValue[] MembersBody(LuaState state, ReadOnlySpan<LuaValue> values)
    {
        var bridge = GetBridge(state, values);
        try
        {
            var typeName = CheckString(state, values, 1, "clr.members");
            var result = state.CreateTable(0, 8);
            var members = bridge.ResolveMembers(typeName);
            for (var index = 0; index < members.Length; index++)
            {
                var member = state.CreateTable(0, 8);
                SetString(state, member, "name", members[index].Name);
                SetString(state, member, "kind", members[index].Kind.ToString());
                member.Set(LuaValue.FromString(state.Strings.GetOrCreate("static"u8)),
                    LuaValue.FromBoolean(members[index].IsStatic));
                member.Set(LuaValue.FromString(state.Strings.GetOrCreate("readable"u8)),
                    LuaValue.FromBoolean(members[index].CanRead));
                member.Set(LuaValue.FromString(state.Strings.GetOrCreate("writable"u8)),
                    LuaValue.FromBoolean(members[index].CanWrite));
                result.Set(LuaValue.FromInteger(index + 1), LuaValue.FromTable(member));
            }

            return [LuaValue.FromTable(result)];
        }
        catch (LuaClrException exception)
        {
            throw CreateLuaError(exception);
        }
    }

    private static LuaValue[] CallBody(LuaState state, ReadOnlySpan<LuaValue> values)
    {
        var bridge = GetBridge(state, values);
        try
        {
            LuaClrInvocationResult result;
            if (values.Length > 1 && values[1].Kind == LuaValueKind.String)
            {
                var typeName = values[1].AsString().ToString();
                var memberName = CheckString(state, values, 2, "clr.call");
                result = bridge.InvokeStatic(typeName, memberName, values[3..]);
            }
            else
            {
                var target = Required(values, 1, "clr.call");
                var memberName = CheckString(state, values, 2, "clr.call");
                result = bridge.InvokeMember(target, memberName, values[3..]);
            }
            return [result.ReturnValue, .. result.RefOutValues];
        }
        catch (LuaClrException exception)
        {
            throw CreateLuaError(exception);
        }
    }

    private static LuaValue[] GetBody(LuaState state, ReadOnlySpan<LuaValue> values)
    {
        var bridge = GetBridge(state, values);
        try
        {
            if (values.Length > 1 && values[1].Kind == LuaValueKind.String)
            {
                var typeName = values[1].AsString().ToString();
                return [bridge.GetMember(
                    LuaValue.FromLightUserdata(new LuaLightUserdata(
                        bridge.ResolveAllowedTypeForHost(typeName))),
                    CheckString(state, values, 2, "clr.get"), values[3..])];
            }
            var target = Required(values, 1, "clr.get");
            var memberName = CheckString(state, values, 2, "clr.get");
            return [bridge.GetMember(target, memberName, values[3..])];
        }
        catch (LuaClrException exception)
        {
            throw CreateLuaError(exception);
        }
    }

    private static LuaValue[] SetBody(LuaState state, ReadOnlySpan<LuaValue> values)
    {
        var bridge = GetBridge(state, values);
        try
        {
            LuaValue target;
            if (values.Length > 1 && values[1].Kind == LuaValueKind.String)
            {
                target = LuaValue.FromLightUserdata(new LuaLightUserdata(
                    bridge.ResolveAllowedTypeForHost(values[1].AsString().ToString())));
            }
            else
            {
                target = Required(values, 1, "clr.set");
            }
            var memberName = CheckString(state, values, 2, "clr.set");
            bridge.SetMember(target, memberName, Required(values, 3, "clr.set"));
            return [];
        }
        catch (LuaClrException exception)
        {
            throw CreateLuaError(exception);
        }
    }

    private static LuaValue[] SubscribeBody(LuaState state, ReadOnlySpan<LuaValue> values)
    {
        var bridge = GetBridge(state, values);
        try
        {
            var target = Required(values, 1, "clr.on");
            var eventName = CheckString(state, values, 2, "clr.on");
            var callback = Required(values, 3, "clr.on");
            var subscription = bridge.Subscribe(target, eventName, callback);
            var userdata = state.CreateUserdata(subscription, 1, 32);
            userdata.SetUserValue(0, callback);
            return [LuaValue.FromUserdata(userdata)];
        }
        catch (LuaClrException exception)
        {
            throw CreateLuaError(exception);
        }
    }

    private static LuaValue[] AwaitBody(LuaState state, ReadOnlySpan<LuaValue> values)
    {
        var bridge = GetBridge(state, values);
        try
        {
            return [bridge.Await(Required(values, 1, "clr.await"))];
        }
        catch (LuaClrException exception)
        {
            throw CreateLuaError(exception);
        }
    }

    private static LuaValue[] DisposeBody(LuaState state, ReadOnlySpan<LuaValue> values)
    {
        var bridge = GetBridge(state, values);
        try
        {
            bridge.DisposeValue(Required(values, 1, "clr.dispose"));
            return [];
        }
        catch (LuaClrException exception)
        {
            throw CreateLuaError(exception);
        }
    }

    private static LuaValue[] CancellationBody(LuaState state, ReadOnlySpan<LuaValue> values)
    {
        var bridge = GetBridge(state, values);
        try
        {
            return [LuaValue.FromUserdata(bridge.CreateCancellation())];
        }
        catch (LuaClrException exception)
        {
            throw CreateLuaError(exception);
        }
    }

    private static LuaValue[] CancelBody(LuaState state, ReadOnlySpan<LuaValue> values)
    {
        var bridge = GetBridge(state, values);
        try
        {
            bridge.Cancel(Required(values, 1, "clr.cancel"));
            return [];
        }
        catch (LuaClrException exception)
        {
            throw CreateLuaError(exception);
        }
    }

    private static LuaRuntimeException CreateLuaError(LuaClrException exception) =>
        new($"CLR {exception.Code}: {exception.Message}", exception);

    private static LuaValue ResolveTypeFromLua(
        LuaClrBridge bridge,
        LuaState state,
        ReadOnlySpan<LuaValue> values)
    {
        var typeName = CheckString(state, values, 1, "clr.type");
        var info = bridge.ResolveType(typeName);
        var table = state.CreateTable(0, 5);
        SetString(state, table, "name", info.FullName);
        SetString(state, table, "assembly", info.AssemblyName);
        table.Set(
            LuaValue.FromString(state.Strings.GetOrCreate("value_type"u8)),
            LuaValue.FromBoolean(info.IsValueType));
        table.Set(
            LuaValue.FromString(state.Strings.GetOrCreate("constructible"u8)),
            LuaValue.FromBoolean(info.IsConstructible));
        var constructors = state.CreateTable(0, info.Constructors.Length);
        for (var index = 0; index < info.Constructors.Length; index++)
        {
            var constructor = state.CreateTable(0, 1);
            var parameters = state.CreateTable(0, info.Constructors[index].ParameterTypeNames.Length);
            for (var parameterIndex = 0;
                 parameterIndex < info.Constructors[index].ParameterTypeNames.Length;
                 parameterIndex++)
            {
                parameters.Set(
                    LuaValue.FromInteger(parameterIndex + 1),
                    LuaValue.FromString(state.Strings.GetOrCreate(
                        System.Text.Encoding.UTF8.GetBytes(
                            info.Constructors[index].ParameterTypeNames[parameterIndex]))));
            }

            constructor.Set(
                LuaValue.FromString(state.Strings.GetOrCreate("parameters"u8)),
                LuaValue.FromTable(parameters));
            constructors.Set(
                LuaValue.FromInteger(index + 1),
                LuaValue.FromTable(constructor));
        }

        table.Set(
            LuaValue.FromString(state.Strings.GetOrCreate("constructors"u8)),
            LuaValue.FromTable(constructors));
        return LuaValue.FromTable(table);
    }

    private static LuaValue CreateFromLua(
        LuaClrBridge bridge,
        LuaState state,
        ReadOnlySpan<LuaValue> values)
    {
        var typeName = CheckString(state, values, 1, "clr.new");
        return LuaValue.FromUserdata(bridge.CreateInstance(typeName, values[2..]));
    }

    private static LuaClrBridge GetBridge(LuaState state, ReadOnlySpan<LuaValue> values) =>
        values.Length > 0 &&
        values[0].Kind == LuaValueKind.LightUserdata &&
        values[0].AsLightUserdata().Identity is LuaClrBridge bridge &&
        ReferenceEquals(bridge.State, state)
            ? bridge
            : throw new LuaClrException(
                LuaClrErrorCode.CapabilityDenied,
                "The CLR bridge capture is invalid.");

    private static string CheckString(
        LuaState state,
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

    private static LuaValue Required(ReadOnlySpan<LuaValue> values, int index, string function) =>
        (uint)index < (uint)values.Length
            ? values[index]
            : throw new LuaRuntimeException($"bad argument #{index} to '{function}' (value expected)");

    private static void SetString(LuaState state, LuaTable table, string name, string value) =>
        table.Set(
            LuaValue.FromString(state.Strings.GetOrCreate(System.Text.Encoding.UTF8.GetBytes(name))),
            LuaValue.FromString(state.Strings.GetOrCreate(System.Text.Encoding.UTF8.GetBytes(value))));

    private static (object? Instance, Type Type) UnwrapTarget(LuaValue target)
    {
        if (target.Kind == LuaValueKind.Userdata)
        {
            var payload = target.AsUserdata().Payload;
            if (payload is LuaClrObject clrObject)
            {
                return (clrObject.Instance, clrObject.ClrType);
            }

            if (payload is Type reflectedType)
            {
                return (null, reflectedType);
            }
        }

        if (target.Kind == LuaValueKind.LightUserdata && target.AsLightUserdata().Identity is Type type)
        {
            return (null, type);
        }

        throw new LuaClrException(LuaClrErrorCode.TypeNotAllowed,
            "A CLR userdata or allowlisted static type is required.");
    }

    private MemberInfo[] GetMembers(Type type)
    {
        return _memberCache.GetOrAdd(type, static (current, state) =>
        {
            var flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            var members = current.GetMembers(flags)
                .Where(member => member is MethodInfo or PropertyInfo or FieldInfo or EventInfo)
                .Where(member => member switch
                {
                    MethodInfo method => method.IsPublic,
                    PropertyInfo property => property.GetMethod?.IsPublic == true || property.SetMethod?.IsPublic == true,
                    FieldInfo field => field.IsPublic,
                    EventInfo @event => @event.AddMethod?.IsPublic == true,
                    _ => false,
                })
                .Where(member => state.IsMemberNameAllowed(current, member.Name))
                .OrderBy(member => member.Name, StringComparer.Ordinal)
                .ThenBy(static member => member switch
                {
                    MethodInfo method => string.Join('|', method.GetParameters().Select(parameter =>
                        parameter.ParameterType.FullName ?? parameter.ParameterType.Name)),
                    PropertyInfo property => property.PropertyType.FullName ?? property.PropertyType.Name,
                    FieldInfo field => field.FieldType.FullName ?? field.FieldType.Name,
                    EventInfo @event => @event.EventHandlerType?.FullName ?? string.Empty,
                    _ => string.Empty,
                }, StringComparer.Ordinal)
                .Take(state._options.MaximumCachedMembers)
                .ToArray();
            return members.ToImmutableArray();
        }, this).ToArray();
    }

    private bool IsMemberNameAllowed(Type type, string name) =>
        _allowedMembers.Contains(name) ||
        _allowedMembers.Contains($"{type.FullName}.{name}") ||
        _allowedMembers.Contains($"{type.Assembly.GetName().Name}:{type.FullName}.{name}");

    private void EnsureMemberAllowed(Type type, string name)
    {
        if (!IsMemberNameAllowed(type, name))
        {
            throw new LuaClrException(LuaClrErrorCode.MemberNotAllowed,
                $"CLR member '{type.FullName}.{name}' is not allowlisted.");
        }
    }

    private void EnsureEventAllowed(Type type, string name)
    {
        EnsureMemberAllowed(type, name);
        if (_allowedEvents.Count != 0 && !_allowedEvents.Contains(name) &&
            !_allowedEvents.Contains($"{type.FullName}.{name}"))
        {
            throw new LuaClrException(LuaClrErrorCode.MemberNotAllowed,
                $"CLR event '{type.FullName}.{name}' is not allowlisted.");
        }
    }

    private static LuaClrMemberInfo DescribeMember(MemberInfo member) => member switch
    {
        MethodInfo method => new LuaClrMemberInfo(method.Name,
            method.Name.StartsWith("op_", StringComparison.Ordinal) ? LuaClrMemberKind.Operator : LuaClrMemberKind.Method,
            method.IsStatic, false, false,
            [.. method.GetParameters().Select(parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name)],
            method.ReturnType.FullName ?? method.ReturnType.Name),
        PropertyInfo property => new LuaClrMemberInfo(property.Name, property.GetIndexParameters().Length > 0
                ? LuaClrMemberKind.Indexer : LuaClrMemberKind.Property,
            (property.GetMethod ?? property.SetMethod)?.IsStatic == true,
            property.GetMethod?.IsPublic == true, property.SetMethod?.IsPublic == true,
            [.. property.GetIndexParameters().Select(parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name)],
            property.PropertyType.FullName ?? property.PropertyType.Name),
        FieldInfo field => new LuaClrMemberInfo(field.Name, LuaClrMemberKind.Field, field.IsStatic,
            true, !field.IsInitOnly, [], field.FieldType.FullName ?? field.FieldType.Name),
        EventInfo @event => new LuaClrMemberInfo(@event.Name, LuaClrMemberKind.Event,
            (@event.AddMethod ?? @event.RemoveMethod)?.IsStatic == true, false, false, [],
            @event.EventHandlerType?.FullName ?? "System.Delegate"),
        _ => throw new InvalidOperationException("Unsupported CLR member."),
    };

    private MemberInfo SelectMember(
        Type type,
        string name,
        ReadOnlySpan<LuaValue> arguments,
        bool forWrite,
        bool requireStatic)
    {
        var members = GetMembers(type)
            .Where(member => string.Equals(member.Name, name, StringComparison.Ordinal))
            .Where(member => IsStatic(member) == requireStatic);
        if (forWrite)
        {
            members = members.Where(member => member switch
            {
                PropertyInfo property => property.SetMethod is not null,
                FieldInfo field => !field.IsInitOnly,
                _ => false,
            });
        }
        else if (arguments.Length > 0)
        {
            var argumentCount = arguments.Length;
            members = members.Where(member => member is PropertyInfo property &&
                property.GetIndexParameters().Length == argumentCount);
        }

        var selected = members.FirstOrDefault(member => member switch
        {
            PropertyInfo property => property.GetMethod is not null,
            FieldInfo field => true,
            MethodInfo => true,
            _ => false,
        });
        return selected ?? throw new LuaClrException(LuaClrErrorCode.MemberNotFound,
            $"CLR member '{name}' was not found.");
    }

    private static bool IsStatic(MemberInfo member) => member switch
    {
        MethodInfo method => method.IsStatic,
        PropertyInfo property => (property.GetMethod ?? property.SetMethod)?.IsStatic == true,
        FieldInfo field => field.IsStatic,
        EventInfo @event => (@event.AddMethod ?? @event.RemoveMethod)?.IsStatic == true,
        _ => false,
    };

    private static (MethodInfo Method, object?[] Arguments)? SelectMethod(
        IEnumerable<MethodInfo> methods,
        ReadOnlySpan<LuaValue> arguments,
        ReadOnlySpan<LuaClrNamedArgument> namedArguments)
    {
        var candidates = new List<(MethodInfo Method, object?[] Arguments, int Score, string Signature)>();
        foreach (var method in methods)
        {
            var parameters = method.GetParameters();
            if (arguments.Length > parameters.Length ||
                arguments.Length + namedArguments.Length > parameters.Length)
            {
                continue;
            }

            var values = new object?[parameters.Length];
            var assigned = new bool[parameters.Length];
            var score = 0;
            var valid = true;
            for (var index = 0; index < arguments.Length; index++)
            {
                var parameter = parameters[index];
                var targetType = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;
                if (!TryConvert(arguments[index], targetType, out values[index], out var cost))
                {
                    valid = false;
                    break;
                }
                assigned[index] = true;
                score += cost;
            }

            if (!valid)
            {
                continue;
            }

            foreach (var named in namedArguments)
            {
                var parameterIndex = Array.FindIndex(parameters, parameter =>
                    string.Equals(parameter.Name, named.Name, StringComparison.Ordinal));
                if (parameterIndex < 0 || assigned[parameterIndex])
                {
                    valid = false;
                    break;
                }

                var targetType = parameters[parameterIndex].ParameterType.IsByRef
                    ? parameters[parameterIndex].ParameterType.GetElementType()!
                    : parameters[parameterIndex].ParameterType;
                if (!TryConvert(named.Value, targetType, out values[parameterIndex], out var cost))
                {
                    valid = false;
                    break;
                }
                assigned[parameterIndex] = true;
                score += cost;
            }

            if (!valid)
            {
                continue;
            }

            for (var index = 0; index < parameters.Length; index++)
            {
                if (assigned[index])
                {
                    continue;
                }

                if (parameters[index].IsOut)
                {
                    values[index] = parameters[index].ParameterType.GetElementType()!.IsValueType
                        ? Activator.CreateInstance(parameters[index].ParameterType.GetElementType()!) : null;
                    assigned[index] = true;
                    continue;
                }

                if (parameters[index].HasDefaultValue)
                {
                    values[index] = parameters[index].DefaultValue;
                    assigned[index] = true;
                    score += 1;
                    continue;
                }

                valid = false;
                break;
            }

            if (valid)
            {
                candidates.Add((method, values, score,
                    string.Join('|', parameters.Select(parameter => parameter.ParameterType.FullName ?? parameter.ParameterType.Name))));
            }
        }

        var selected = candidates.OrderBy(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Signature, StringComparer.Ordinal).FirstOrDefault();
        return selected.Method is null ? null : (selected.Method, selected.Arguments);
    }

    private static object?[] ConvertArguments(ReadOnlySpan<LuaValue> values, ParameterInfo[] parameters)
    {
        var converted = new object?[parameters.Length];
        for (var index = 0; index < parameters.Length; index++)
        {
            if (!TryConvert(values[index], parameters[index].ParameterType, out converted[index], out _))
            {
                throw new LuaClrException(LuaClrErrorCode.NoMatchingMember, "Index argument conversion failed.");
            }
        }
        return converted;
    }

    private LuaValue CreateBoundMethod(LuaValue target, Type type, string memberName)
    {
        var capture = LuaValue.FromLightUserdata(new LuaLightUserdata(this));
        var name = LuaValue.FromString(_state.Strings.GetOrCreate(System.Text.Encoding.UTF8.GetBytes(memberName)));
        var closure = _state.CreateNativeClosure(
            new LuaNativeFunction($"clr.{memberName}", BoundMemberStep), [capture, target, name]);
        return LuaValue.FromFunction(closure);
    }

    private void AttachMetatable(LuaUserdata userdata, Type type)
    {
        var metatable = _state.CreateTable(0, 16);
        var capture = LuaValue.FromLightUserdata(new LuaLightUserdata(this));
        SetMetamethod(metatable, LuaMetamethod.Index, "clr.__index", IndexStep, capture);
        SetMetamethod(metatable, LuaMetamethod.NewIndex, "clr.__newindex", NewIndexStep, capture);
        foreach (var (metamethod, name) in OperatorMetamethods)
        {
            if (IsMemberNameAllowed(type, name))
            {
                SetMetamethod(metatable, metamethod, $"clr.{name}", OperatorStep, capture,
                    LuaValue.FromString(_state.Strings.GetOrCreate(System.Text.Encoding.UTF8.GetBytes(name))));
            }
        }
        userdata.SetMetatable(metatable);
    }

    private void SetMetamethod(
        LuaTable table,
        LuaMetamethod metamethod,
        string functionName,
        LuaNativeFunctionStepBody body,
        params LuaValue[] captures) => table.Set(
            LuaValue.FromString(_state.Strings.GetOrCreate(LuaMetamethodFacts.GetName(metamethod))),
            LuaValue.FromFunction(_state.CreateNativeClosure(new LuaNativeFunction(functionName, body), captures)));

    private static readonly (LuaMetamethod Metamethod, string Name)[] OperatorMetamethods =
    [
        (LuaMetamethod.Add, "op_Addition"), (LuaMetamethod.Subtract, "op_Subtraction"),
        (LuaMetamethod.Multiply, "op_Multiply"), (LuaMetamethod.Divide, "op_Division"),
        (LuaMetamethod.FloorDivide, "op_FloorDivision"), (LuaMetamethod.Modulo, "op_Modulus"),
        (LuaMetamethod.Power, "op_Exponent"), (LuaMetamethod.Equal, "op_Equality"),
        (LuaMetamethod.LessThan, "op_LessThan"), (LuaMetamethod.LessThanOrEqual, "op_LessThanOrEqual"),
        (LuaMetamethod.UnaryMinus, "op_UnaryNegation"),
    ];

    private static LuaNativeStep IndexStep(LuaNativeCallContext context, int continuationId, ReadOnlySpan<LuaValue> values)
    {
        var bridge = GetBridge(context);
        var target = Required(values, 0, "__index");
        var key = Required(values, 1, "__index");
        if (key.Kind == LuaValueKind.String)
        {
            return LuaNativeStep.Completed(bridge.GetMember(target, key.AsString().ToString()));
        }
        return LuaNativeStep.Completed(bridge.GetMember(target, "Item", [key]));
    }

    private static LuaNativeStep NewIndexStep(LuaNativeCallContext context, int continuationId, ReadOnlySpan<LuaValue> values)
    {
        var bridge = GetBridge(context);
        var target = Required(values, 0, "__newindex");
        var key = Required(values, 1, "__newindex");
        bridge.SetMember(target, key.Kind == LuaValueKind.String ? key.AsString().ToString() : "Item", Required(values, 2, "__newindex"));
        return LuaNativeStep.Completed();
    }

    private static LuaNativeStep BoundMemberStep(LuaNativeCallContext context, int continuationId, ReadOnlySpan<LuaValue> values)
    {
        var bridge = GetBridge(context);
        if (context.Captures.Count < 3)
        {
            throw new LuaClrException(LuaClrErrorCode.MemberNotFound, "CLR bound method capture is invalid.");
        }
        var arguments = values;
        if (values.Length > 0 && values[0] == context.Captures[1])
        {
            arguments = values[1..];
        }
        var result = bridge.InvokeMember(context.Captures[1], context.Captures[2].AsString().ToString(), arguments);
        return LuaNativeStep.Completed([result.ReturnValue, .. result.RefOutValues]);
    }

    private static LuaNativeStep OperatorStep(LuaNativeCallContext context, int continuationId, ReadOnlySpan<LuaValue> values)
    {
        var bridge = GetBridge(context);
        var name = context.Captures.Count > 1 ? context.Captures[1].AsString().ToString() : string.Empty;
        var result = bridge.InvokeMember(Required(values, 0, "operator"), name, values.Length > 1 ? values[1..] : []);
        return LuaNativeStep.Completed([result.ReturnValue, .. result.RefOutValues]);
    }

    private static LuaClrBridge GetBridge(LuaNativeCallContext context) =>
        context.Captures.Count > 0 && context.Captures[0].Kind == LuaValueKind.LightUserdata &&
        context.Captures[0].AsLightUserdata().Identity is LuaClrBridge bridge &&
        ReferenceEquals(bridge.State, context.State)
            ? bridge
            : throw new LuaClrException(LuaClrErrorCode.CapabilityDenied, "The CLR bridge capture is invalid.");

    private LuaClrException InvocationFailure(string memberName, Exception exception) =>
        new(LuaClrErrorCode.InvocationFailed,
            _options.IncludeExceptionMessages
                ? $"CLR member '{memberName}' failed: {exception.Message}"
                : $"CLR member '{memberName}' failed.", exception);

    private static LuaClrException NoMatchingMember(string memberName) =>
        new(LuaClrErrorCode.NoMatchingMember, $"No allowlisted CLR overload of '{memberName}' accepts the supplied values.");

    private static void ValidateDelegateSignature(MethodInfo invoke)
    {
        if (invoke.ReturnType != typeof(void) && !IsSupportedClrType(invoke.ReturnType))
        {
            throw new LuaClrException(LuaClrErrorCode.InvalidDelegate,
                $"Delegate return type '{invoke.ReturnType.FullName}' is unsupported.");
        }

        foreach (var parameter in invoke.GetParameters())
        {
            if (parameter.ParameterType.IsByRef || !IsSupportedClrType(parameter.ParameterType))
            {
                throw new LuaClrException(LuaClrErrorCode.InvalidDelegate,
                    $"Delegate parameter '{parameter.Name}' has an unsupported type.");
            }
        }
    }

    private Delegate BuildDelegate(
        Type delegateType,
        LuaClrCallbackRegistration registration)
    {
        var invoke = delegateType.GetMethod("Invoke")!;
        var parameters = invoke.GetParameters()
            .Select(parameter => Expression.Parameter(parameter.ParameterType, parameter.Name))
            .ToArray();
        var boxed = Expression.NewArrayInit(typeof(object), parameters.Select(parameter =>
            Expression.Convert(parameter, typeof(object))));
        var call = Expression.Call(
            Expression.Constant(this),
            nameof(InvokeDelegateCore),
            Type.EmptyTypes,
            Expression.Constant(registration),
            boxed,
            Expression.Constant(invoke.ReturnType, typeof(Type)));
        Expression body = invoke.ReturnType == typeof(void)
            ? Expression.Block(call, Expression.Empty())
            : Expression.Convert(call, invoke.ReturnType);
        return Expression.Lambda(delegateType, body, parameters).Compile(preferInterpretation: true);
    }

    private object? InvokeDelegateCore(
        LuaClrCallbackRegistration registration,
        object?[] arguments,
        Type returnType)
    {
        lock (_callbackGate)
        {
            EnsureThread();
            var function = registration.GetActiveCallback();
            var luaArguments = arguments.Select(ToLuaValue).ToArray();
            var closure = function.TryGetClosure() ??
                throw new LuaClrException(LuaClrErrorCode.InvalidDelegate, "Lua callback is not a closure.");
            var executor = new LuaExecutor(new LuaExecutorOptions
            {
                Interpreter = LuaInterpreterOptions.Default,
            });
            var callbackThread = _state.CreateThread(closure);
            var result = executor.Start(_state, callbackThread, luaArguments);
            if (result.Signal != LuaVmSignal.Completed)
            {
                throw new LuaClrException(LuaClrErrorCode.InvocationFailed,
                    "Lua callback did not complete.");
            }

            var value = result.Values.Length == 0 ? LuaValue.Nil : result.Values[0];
            if (returnType == typeof(void))
            {
                return null;
            }

            if (!TryConvert(value, returnType, out var converted, out _))
            {
                throw new LuaClrException(LuaClrErrorCode.InvocationFailed,
                    $"Lua callback result cannot convert to '{returnType.FullName}'.");
            }
            return converted;
        }
    }

    private LuaValue ToLuaValue(object? value)
    {
        if (value is null)
        {
            return LuaValue.Nil;
        }

        if (value is LuaValue luaValue)
        {
            _state.Heap.ValidateValue(luaValue);
            return luaValue;
        }

        switch (value)
        {
            case bool boolean:
                return LuaValue.FromBoolean(boolean);
            case string text:
                return LuaValue.FromString(_state.Strings.GetOrCreate(System.Text.Encoding.UTF8.GetBytes(text)));
            case char character:
                return LuaValue.FromString(_state.Strings.GetOrCreate(System.Text.Encoding.UTF8.GetBytes(character.ToString())));
            case byte number: return LuaValue.FromInteger(number);
            case sbyte number: return LuaValue.FromInteger(number);
            case short number: return LuaValue.FromInteger(number);
            case ushort number: return LuaValue.FromInteger(number);
            case int number: return LuaValue.FromInteger(number);
            case uint number: return LuaValue.FromInteger(number);
            case long number: return LuaValue.FromInteger(number);
            case ulong number when number <= long.MaxValue: return LuaValue.FromInteger((long)number);
            case float number: return LuaValue.FromFloat(number);
            case double number: return LuaValue.FromFloat(number);
            case decimal number: return LuaValue.FromFloat((double)number);
            case Enum enumeration:
                return LuaValue.FromString(_state.Strings.GetOrCreate(System.Text.Encoding.UTF8.GetBytes(enumeration.ToString())));
            case Task task:
                return LuaValue.FromUserdata(_state.CreateUserdata(new LuaClrTask(task, this), 1, 64));
            case Array array:
                {
                    var table = _state.CreateTable(array.Length, 1);
                    for (var index = 0; index < array.Length; index++)
                    {
                        table.Set(LuaValue.FromInteger(index + 1), ToLuaValue(array.GetValue(index)));
                    }
                    return LuaValue.FromTable(table);
                }
        }

        var valueType = value.GetType();
        if (valueType == typeof(ValueTask) ||
            valueType.IsGenericType && valueType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var asTask = valueType.GetMethod("AsTask", BindingFlags.Public | BindingFlags.Instance);
            if (asTask?.Invoke(value, null) is Task task)
            {
                return LuaValue.FromUserdata(_state.CreateUserdata(new LuaClrTask(task, this), 1, 64));
            }
        }

        var fields = valueType.GetFields(BindingFlags.Public | BindingFlags.Instance);
        if (valueType.FullName?.StartsWith("System.ValueTuple", StringComparison.Ordinal) == true)
        {
            var table = _state.CreateTable(0, fields.Length);
            for (var index = 0; index < fields.Length; index++)
            {
                table.Set(LuaValue.FromInteger(index + 1), ToLuaValue(fields[index].GetValue(value)));
            }
            return LuaValue.FromTable(table);
        }

        var userdata = _state.CreateUserdata(new LuaClrObject(value, ownsInstance: false), 1, 64);
        if ((_options.Capabilities & LuaClrCapabilities.MemberAccess) != LuaClrCapabilities.None)
        {
            AttachMetatable(userdata, value.GetType());
        }
        return LuaValue.FromUserdata(userdata);
    }

    private static bool IsSupportedClrType(Type type) =>
        type == typeof(void) || type == typeof(object) || type == typeof(string) ||
        type == typeof(bool) || type == typeof(char) || IsNumeric(type) || type.IsEnum ||
        Nullable.GetUnderlyingType(type) is not null || type.IsArray || type.IsClass;

    private void EnsureThread()
    {
        if (_options.ThreadPolicy == LuaClrThreadPolicy.OwnerThreadOnly &&
            Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new LuaClrException(LuaClrErrorCode.ThreadDenied,
                "CLR callbacks may only enter Lua from the bridge owner thread.");
        }

        if (_options.ThreadPolicy == LuaClrThreadPolicy.AnyThreadWhenIdle &&
            Environment.CurrentManagedThreadId != _ownerThreadId &&
            _state.RunningThread is not null)
        {
            throw new LuaClrException(LuaClrErrorCode.ThreadDenied,
                "CLR callbacks cannot enter a running Lua state from another thread.");
        }
    }

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
        score = 0;
        var nullable = Nullable.GetUnderlyingType(targetType);
        var nonNullable = nullable ?? targetType;
        if (nonNullable == typeof(CancellationToken))
        {
            if (value.IsNil)
            {
                converted = CancellationToken.None;
                score = 1;
                return true;
            }
            if (value.Kind == LuaValueKind.Userdata &&
                value.AsUserdata().Payload is LuaClrCancellation cancellation)
            {
                converted = cancellation.Token;
                return true;
            }
        }
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

        if (nonNullable.IsArray && value.Kind == LuaValueKind.Table)
        {
            var elementType = nonNullable.GetElementType()!;
            var table = value.AsTable();
            var length = table.ArrayLength;
            var array = Array.CreateInstance(elementType, length);
            for (var index = 0; index < length; index++)
            {
                if (!TryConvert(table.Get(LuaValue.FromInteger(index + 1)), elementType,
                    out var element, out var elementCost))
                {
                    converted = null;
                    score = 0;
                    return false;
                }
                array.SetValue(element, index);
                score += elementCost;
            }
            converted = array;
            score += 3;
            return true;
        }

        if (nonNullable.FullName?.StartsWith("System.ValueTuple", StringComparison.Ordinal) == true &&
            value.Kind == LuaValueKind.Table)
        {
            var fields = nonNullable.GetFields(BindingFlags.Public | BindingFlags.Instance);
            var table = value.AsTable();
            var args = new object?[fields.Length];
            for (var index = 0; index < fields.Length; index++)
            {
                if (!TryConvert(table.Get(LuaValue.FromInteger(index + 1)), fields[index].FieldType,
                    out args[index], out _))
                {
                    converted = null;
                    score = 0;
                    return false;
                }
            }
            converted = Activator.CreateInstance(nonNullable, args);
            score = 4;
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
