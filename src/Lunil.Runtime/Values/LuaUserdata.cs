using Lunil.Runtime.Memory;
using Lunil.Runtime.Operations;

namespace Lunil.Runtime.Values;

/// <summary>
/// Heap-owned full userdata with a host payload, per-object metatable, and Lua 5.4 user values.
/// Host payload references are not Lua values and must not retain untraced Lua objects.
/// </summary>
public sealed class LuaUserdata : LuaGcObject
{
    private readonly LuaValue[] _userValues;
    private object? _payload;
    private LuaTable? _metatable;

    internal LuaUserdata(
        LuaHeap owner,
        object? payload,
        int userValueCount,
        long payloadLogicalSize)
        : base(owner, CalculateLogicalSize(userValueCount, payloadLogicalSize))
    {
        _payload = payload;
        _userValues = new LuaValue[userValueCount];
    }

    public object? Payload => _payload;

    public int UserValueCount => _userValues.Length;

    public LuaTable? Metatable => _metatable;

    public T GetPayload<T>() where T : class =>
        _payload as T ?? throw new LuaRuntimeException(
            $"The userdata payload is not a {typeof(T).FullName}.");

    public LuaValue GetUserValue(int index) => _userValues[index];

    public void SetUserValue(int index, LuaValue value)
    {
        Owner.ValidateValue(value);
        Owner.WriteBarrierBack(this, value);
        _userValues[index] = value;
    }

    public void SetMetatable(LuaTable? metatable)
    {
        if (metatable is not null)
        {
            Owner.ValidateValue(LuaValue.FromTable(metatable));
            Owner.WriteBarrierBack(this, LuaValue.FromTable(metatable));
            if (!metatable.GetMetamethodField(LuaMetamethod.GarbageCollect).IsNil)
            {
                Owner.RegisterFinalizer(this);
            }
        }

        _metatable = metatable;
    }

    /// <summary>Releases an owned host resource once and clears the payload reference.</summary>
    public void DisposePayload()
    {
        var payload = _payload;
        _payload = null;
        if (payload is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    internal override void Traverse(LuaGcVisitor visitor)
    {
        visitor.Visit(_metatable);
        foreach (var value in _userValues)
        {
            visitor.Visit(value);
        }
    }

    internal override bool TryGetFinalizer(out LuaValue finalizer)
    {
        finalizer = _metatable?.GetMetamethodField(LuaMetamethod.GarbageCollect) ?? LuaValue.Nil;
        if (finalizer.Kind == LuaValueKind.Function)
        {
            Owner.RegisterFinalizer(this);
        }

        return finalizer.Kind == LuaValueKind.Function;
    }

    internal override void OnCollected() => DisposePayload();

    private static long CalculateLogicalSize(int userValueCount, long payloadLogicalSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(userValueCount);
        ArgumentOutOfRangeException.ThrowIfNegative(payloadLogicalSize);
        return checked(64 + userValueCount * 16L + payloadLogicalSize);
    }
}

/// <summary>Stable opaque identity used for Lua light userdata values.</summary>
public sealed class LuaLightUserdata
{
    public LuaLightUserdata(object identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        Identity = identity;
    }

    public object Identity { get; }
}
