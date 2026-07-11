using System.Collections.Immutable;
using System.Text;

namespace Luac.IR.Lua54;

/// <summary>An immutable binary Lua string used in a compiled prototype.</summary>
public sealed class Lua54String : IEquatable<Lua54String>
{
    private readonly ImmutableArray<byte> _bytes;

    public Lua54String(ReadOnlySpan<byte> bytes)
    {
        _bytes = ImmutableArray.Create(bytes.ToArray());
    }

    public int Length => _bytes.Length;

    public ReadOnlySpan<byte> AsSpan() => _bytes.AsSpan();

    public byte[] ToArray() => _bytes.ToArray();

    public static Lua54String FromUtf8(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new Lua54String(Encoding.UTF8.GetBytes(value));
    }

    public bool Equals(Lua54String? other) =>
        other is not null && AsSpan().SequenceEqual(other.AsSpan());

    public override bool Equals(object? obj) => Equals(obj as Lua54String);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(AsSpan());
        return hash.ToHashCode();
    }

    public override string ToString() => Encoding.UTF8.GetString(AsSpan());
}
