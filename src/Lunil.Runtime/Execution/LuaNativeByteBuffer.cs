using System.Buffers;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Execution;

/// <summary>
/// Mutable byte-only state that a resumable native activation can carry across Lua callbacks.
/// The buffer is tied to one logical heap and deliberately cannot retain Lua values.
/// </summary>
internal sealed class LuaNativeByteBuffer
{
    private readonly long _ownerIdentity;
    private readonly long _maximumLogicalBytes;
    private readonly int _maximumLength;
    private byte[] _bytes;
    private bool _bytesArePooled;

    internal LuaNativeByteBuffer(LuaHeap owner, int initialCapacity)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);

        _ownerIdentity = owner.Identity;
        _maximumLogicalBytes = owner.MaximumLogicalBytes;
        _maximumLength = checked((int)Math.Min(
            Array.MaxLength,
            Math.Max(0, owner.MaximumLogicalBytes - LuaString.LogicalOverhead)));
        if (initialCapacity > _maximumLength)
        {
            ThrowQuotaExceeded(initialCapacity);
        }

        _bytes = initialCapacity == 0
            ? []
            : ArrayPool<byte>.Shared.Rent(initialCapacity);
        _bytesArePooled = initialCapacity != 0;
    }

    /// <summary>The number of bytes written to the buffer.</summary>
    public int Length { get; private set; }

    /// <summary>A read-only view of the bytes written so far.</summary>
    public ReadOnlySpan<byte> WrittenSpan => _bytes.AsSpan(0, Length);

    /// <summary>Appends bytes, rejecting results that cannot fit in one quota-compliant Lua string.</summary>
    public void Append(ReadOnlySpan<byte> value)
    {
        var requiredLength = (long)Length + value.Length;
        if (requiredLength > _maximumLength)
        {
            ThrowQuotaExceeded(requiredLength);
        }

        EnsureCapacity((int)requiredLength);
        value.CopyTo(_bytes.AsSpan(Length));
        Length = (int)requiredLength;
    }

    /// <summary>
    /// Appends two adjacent spans with one capacity probe while preserving the quota failure
    /// point and requested length of two individual appends.
    /// </summary>
    internal void AppendPair(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var firstRequiredLength = (long)Length + first.Length;
        if (firstRequiredLength > _maximumLength)
        {
            ThrowQuotaExceeded(firstRequiredLength);
        }

        var requiredLength = firstRequiredLength + second.Length;
        if (requiredLength > _maximumLength)
        {
            ThrowQuotaExceeded(requiredLength);
        }

        EnsureCapacity((int)requiredLength);
        first.CopyTo(_bytes.AsSpan(Length));
        second.CopyTo(_bytes.AsSpan(Length + first.Length));
        Length = (int)requiredLength;
    }

    /// <summary>
    /// Reserves physical builder storage without committing logical string bytes. Hints are
    /// clamped to the largest string permitted by the owner, so a conservative estimate cannot
    /// change the error order of the append operation that follows.
    /// </summary>
    internal void ReserveCapacityHint(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        EnsureCapacity(Math.Min(capacity, _maximumLength));
    }

    internal void ValidateOwner(LuaHeap owner)
    {
        if (_ownerIdentity != owner.Identity)
        {
            throw new LuaRuntimeException(
                "Cannot move native continuation state between different LuaState instances.");
        }
    }

    /// <summary>
    /// Transfers the backing array into an immutable Lua string. Long strings take ownership
    /// without a second managed copy; short strings retain normal pool interning semantics.
    /// </summary>
    internal LuaString MoveToString(LuaStringPool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        if (pool.OwnerIdentity != _ownerIdentity)
        {
            throw new LuaRuntimeException(
                "Cannot move native continuation state between different LuaState instances.");
        }

        var bytes = _bytes;
        var length = Length;
        var bytesArePooled = _bytesArePooled;
        _bytes = [];
        _bytesArePooled = false;
        Length = 0;
        return pool.GetOrCreateOwned(bytes, length, bytesArePooled);
    }

    private void EnsureCapacity(int requiredLength)
    {
        if (_bytes.Length >= requiredLength)
        {
            return;
        }

        var capacity = Math.Min(_maximumLength, Math.Max(256, _bytes.Length));
        while (capacity < requiredLength)
        {
            var doubled = (long)capacity * 2;
            capacity = (int)Math.Min(_maximumLength, doubled);
            if (capacity < requiredLength && capacity == _maximumLength)
            {
                ThrowQuotaExceeded(requiredLength);
            }
        }

        var replacement = ArrayPool<byte>.Shared.Rent(capacity);
        _bytes.AsSpan(0, Length).CopyTo(replacement);
        if (_bytesArePooled)
        {
            ArrayPool<byte>.Shared.Return(_bytes);
        }

        _bytes = replacement;
        _bytesArePooled = true;
    }

    private void ThrowQuotaExceeded(long byteLength)
    {
        var logicalSize = LuaString.CalculateLogicalSize(byteLength);
        throw new LuaRuntimeException(
            $"Lua heap quota exceeded while building a string " +
            $"({logicalSize} > {_maximumLogicalBytes} logical bytes).");
    }
}
