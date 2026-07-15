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

        _bytes = initialCapacity == 0 ? [] : new byte[initialCapacity];
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

    internal void ValidateOwner(LuaHeap owner)
    {
        if (_ownerIdentity != owner.Identity)
        {
            throw new LuaRuntimeException(
                "Cannot move native continuation state between different LuaState instances.");
        }
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

        Array.Resize(ref _bytes, capacity);
    }

    private void ThrowQuotaExceeded(long byteLength)
    {
        var logicalSize = LuaString.CalculateLogicalSize(byteLength);
        throw new LuaRuntimeException(
            $"Lua heap quota exceeded while building a string " +
            $"({logicalSize} > {_maximumLogicalBytes} logical bytes).");
    }
}
