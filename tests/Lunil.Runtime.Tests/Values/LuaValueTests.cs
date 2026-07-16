using System.Runtime.CompilerServices;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Tests.Values;

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
        var first = LuaValue.FromString(new LuaState().Strings.GetOrCreate(bytes));
        var second = LuaValue.FromString(new LuaState().Strings.GetOrCreate(bytes));

        Assert.Equal(first, second);
        Assert.Equal(LuaValue.FromInteger(1), LuaValue.FromFloat(1.0));
        Assert.NotEqual(LuaValue.FromInteger(9_007_199_254_740_993),
            LuaValue.FromFloat(9_007_199_254_740_992d));
        Assert.Equal(bytes, first.AsString().ToArray());
    }

    [Fact]
    public void TableUsesArrayPartAndUnifiedIntegerFloatKeys()
    {
        var table = new LuaState().CreateTable();
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
        var pool = new LuaState().Strings;
        var shortBytes = new byte[] { 0x61, 0x62 };
        var longBytes = Enumerable.Repeat((byte)0x61, 41).ToArray();

        Assert.Same(pool.GetOrCreate(shortBytes), pool.GetOrCreate(shortBytes));
        Assert.NotSame(pool.GetOrCreate(longBytes), pool.GetOrCreate(longBytes));
    }

    [Fact]
    public void CachesBoundedShortStringIntegerConcatenationsInBothOrders()
    {
        var state = new LuaState();
        var text = state.Strings.GetOrCreate("item"u8);

        Assert.True(state.Strings.TryGetOrCreateIntegerConcat(
            text,
            -12,
            textFirst: true,
            out var suffix));
        Assert.True(state.Strings.TryGetOrCreateIntegerConcat(
            text,
            -12,
            textFirst: true,
            out var repeatedSuffix));
        Assert.True(state.Strings.TryGetOrCreateIntegerConcat(
            text,
            -12,
            textFirst: false,
            out var prefix));

        Assert.Same(suffix, repeatedSuffix);
        Assert.Equal("item-12", suffix.ToString());
        Assert.Equal("-12item", prefix.ToString());
        Assert.NotSame(suffix, prefix);
        Assert.False(state.Strings.TryGetOrCreateIntegerConcat(
            text,
            4097,
            textFirst: true,
            out _));
    }

    [Fact]
    public void IntegerConcatenationCacheRejectsForeignStringsAndNeverRevivesDeadEntries()
    {
        var state = new LuaState();
        var foreignState = new LuaState();
        var foreign = foreignState.Strings.GetOrCreate("item"u8);
        Assert.False(state.Strings.TryGetOrCreateIntegerConcat(
            foreign,
            7,
            textFirst: true,
            out _));

        var originalText = state.Strings.GetOrCreate("item"u8);
        Assert.True(state.Strings.TryGetOrCreateIntegerConcat(
            originalText,
            7,
            textFirst: true,
            out var originalResult));
        state.Heap.CollectFull();
        Assert.False(originalText.IsAlive);
        Assert.False(originalResult.IsAlive);

        var replacementText = state.Strings.GetOrCreate("item"u8);
        Assert.True(state.Strings.TryGetOrCreateIntegerConcat(
            replacementText,
            7,
            textFirst: true,
            out var replacementResult));
        Assert.True(replacementResult.IsAlive);
        Assert.NotSame(originalResult, replacementResult);
        Assert.Equal("item7", replacementResult.ToString());
    }
}
