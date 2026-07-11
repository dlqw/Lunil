using System.Runtime.CompilerServices;
using Luac.Runtime.Values;

namespace Luac.Runtime.Tests.Values;

public sealed class LuaValueTests
{
    [Fact]
    public void UsesGcSafeSixteenByteRepresentation()
    {
        // Audited use: this observes type size only and performs no memory access.
        Assert.Equal(16, Unsafe.SizeOf<LuaValue>());
    }

    [Fact]
    public void PreservesBinaryStringsAndNormalizesNumericEquality()
    {
        var bytes = new byte[] { 0, 0xff, 0x61 };
        var first = LuaValue.FromString(new LuaString(bytes));
        var second = LuaValue.FromString(new LuaString(bytes));

        Assert.Equal(first, second);
        Assert.Equal(LuaValue.FromInteger(1), LuaValue.FromFloat(1.0));
        Assert.NotEqual(LuaValue.FromInteger(9_007_199_254_740_993),
            LuaValue.FromFloat(9_007_199_254_740_992d));
        Assert.Equal(bytes, first.AsString().ToArray());
    }

    [Fact]
    public void TableUsesArrayPartAndUnifiedIntegerFloatKeys()
    {
        var table = new LuaTable();
        table.Set(LuaValue.FromInteger(1), LuaValue.FromInteger(10));
        table.Set(LuaValue.FromFloat(2.0), LuaValue.FromInteger(20));

        Assert.Equal(2, table.ArrayLength);
        Assert.Equal(LuaValue.FromInteger(10), table.Get(LuaValue.FromFloat(1.0)));
        Assert.Equal(LuaValue.FromInteger(20), table.Get(LuaValue.FromInteger(2)));
        Assert.Throws<LuaRuntimeException>(() =>
            table.Set(LuaValue.FromFloat(double.NaN), LuaValue.Nil));
    }

    [Fact]
    public void InternsShortStringsButNotLongStrings()
    {
        var pool = new LuaStringPool();
        var shortBytes = new byte[] { 0x61, 0x62 };
        var longBytes = Enumerable.Repeat((byte)0x61, 41).ToArray();

        Assert.Same(pool.GetOrCreate(shortBytes), pool.GetOrCreate(shortBytes));
        Assert.NotSame(pool.GetOrCreate(longBytes), pool.GetOrCreate(longBytes));
    }
}
