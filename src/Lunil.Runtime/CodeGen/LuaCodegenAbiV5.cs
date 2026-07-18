using System.ComponentModel;
using System.Runtime.CompilerServices;
using Lunil.Runtime.Operations;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.CodeGen;

/// <summary>
/// Runtime ABI v5 adds execution-local table fast paths for compiled numeric regions.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class LuaCodegenAbiV5
{
    public const int RuntimeAbiVersion = 5;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetCompilerProvenIntegerTableValue(
        ref LuaTable? cachedTable,
        LuaValue target,
        LuaCodegenTableSiteCache cache,
        long key,
        out LuaValue value)
    {
        if (!TryBindTable(ref cachedTable, target, out var table))
        {
            value = LuaValue.Nil;
            return false;
        }

        if (table.TryGetArrayValue(key, out value))
        {
            return !value.IsNil || table.Metatable is null ||
                CanBypassMissingInteger(cache, table, key);
        }

        return TryGetIntegerSlow(cache, table, key, out value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryGetIntegerSlow(
        LuaCodegenTableSiteCache cache,
        LuaTable table,
        long key,
        out LuaValue value)
    {
        var taggedKey = LuaValue.FromInteger(key);
        var exists = table.TryGetIntegerEntry(taggedKey, key, out value, out var entry);
        if (entry.IsArray)
        {
            cache.RecordIntegerFastPathHit(key);
        }
        else
        {
            cache.RecordIntegerFastPathMiss(key);
        }

        return exists || cache.CanBypass(table, LuaMetamethod.Index);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool CanBypassMissingInteger(
        LuaCodegenTableSiteCache cache,
        LuaTable table,
        long key)
    {
        cache.RecordIntegerFastPathMiss(key);
        return cache.CanBypass(table, LuaMetamethod.Index);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TrySetCompilerProvenIntegerTableValue(
        ref LuaTable? cachedTable,
        LuaValue target,
        LuaCodegenTableSiteCache cache,
        long key,
        LuaValue value)
    {
        if (!TryBindTable(ref cachedTable, target, out var table))
        {
            return false;
        }

        if (table.TrySetOrAppendArrayValue(key, value))
        {
            return true;
        }

        return TrySetIntegerSlow(cache, table, key, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TrySetCompilerProvenIntegerTableNonCollectableValue(
        ref LuaTable? cachedTable,
        LuaValue target,
        LuaCodegenTableSiteCache cache,
        long key,
        LuaValue value)
    {
        if (value.TryGetGcObject() is not null ||
            !TryBindTable(ref cachedTable, target, out var table))
        {
            return false;
        }

        if (table.TrySetOrAppendArrayNonCollectableValue(key, value))
        {
            return true;
        }

        return TrySetIntegerSlow(cache, table, key, value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TrySetIntegerSlow(
        LuaCodegenTableSiteCache cache,
        LuaTable table,
        long key,
        LuaValue value)
    {
        if (table.Metatable is not null)
        {
            return false;
        }

        var taggedKey = LuaValue.FromInteger(key);
        if (table.SetIntegerValueNoMetatable(taggedKey, key, value))
        {
            cache.RecordIntegerFastPathHit(key);
        }
        else
        {
            cache.RecordIntegerFastPathMiss(key);
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetCompilerProvenStringTableValue(
        ref LuaTable? cachedTable,
        LuaValue target,
        LuaCodegenTableSiteCache cache,
        ref LuaCodegenTableRegionSite regionSite,
        LuaValue key,
        out LuaValue value)
    {
        if (!TryBindTable(ref cachedTable, target, out var table) ||
            key.TryGetString() is not { } stringKey)
        {
            value = LuaValue.Nil;
            return false;
        }

        if (TryReadRegionEntry(table, stringKey, cache, ref regionSite, out value))
        {
            return true;
        }

        return TryGetStringSlow(table, stringKey, key, cache, ref regionSite, out value);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TryGetStringSlow(
        LuaTable table,
        LuaString stringKey,
        LuaValue key,
        LuaCodegenTableSiteCache cache,
        ref LuaCodegenTableRegionSite regionSite,
        out LuaValue value)
    {
        cache.RecordFastPathMiss();
        var exists = table.TryGetExistingEntry(key, out value, out var entry);
        if (exists)
        {
            regionSite.Observe(table, stringKey, entry);
            return true;
        }

        return cache.CanBypass(table, LuaMetamethod.Index);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TrySetCompilerProvenStringTableValue(
        ref LuaTable? cachedTable,
        LuaValue target,
        LuaCodegenTableSiteCache cache,
        ref LuaCodegenTableRegionSite regionSite,
        LuaValue key,
        LuaValue value)
    {
        if (!TryBindTable(ref cachedTable, target, out var table) ||
            key.TryGetString() is not { } stringKey)
        {
            return false;
        }

        if (TryReadRegionEntry(table, stringKey, cache, ref regionSite, out _))
        {
            table.SetValidatedExistingStringEntry(regionSite.Entry, value);
            return true;
        }

        return TrySetStringSlow(
            table,
            stringKey,
            key,
            value,
            cache,
            ref regionSite);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TrySetCompilerProvenStringTableNonCollectableValue(
        ref LuaTable? cachedTable,
        LuaValue target,
        LuaCodegenTableSiteCache cache,
        ref LuaCodegenTableRegionSite regionSite,
        LuaValue key,
        LuaValue value)
    {
        if (value.TryGetGcObject() is not null ||
            !TryBindTable(ref cachedTable, target, out var table) ||
            key.TryGetString() is not { } stringKey)
        {
            return false;
        }

        if (TryReadRegionEntry(table, stringKey, cache, ref regionSite, out _))
        {
            table.SetValidatedExistingStringNonCollectableEntry(regionSite.Entry, value);
            return true;
        }

        return TrySetStringSlow(
            table,
            stringKey,
            key,
            value,
            cache,
            ref regionSite);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool TrySetStringSlow(
        LuaTable table,
        LuaString stringKey,
        LuaValue key,
        LuaValue value,
        LuaCodegenTableSiteCache cache,
        ref LuaCodegenTableRegionSite regionSite)
    {
        cache.RecordFastPathMiss();
        var exists = table.TryGetExistingEntry(key, out _, out var entry);
        if (!exists && !cache.CanBypass(table, LuaMetamethod.NewIndex))
        {
            return false;
        }

        if (exists)
        {
            table.SetExistingStringEntry(entry, stringKey, value);
            if (!value.IsNil)
            {
                regionSite.Observe(table, stringKey, entry);
            }

            return true;
        }

        table.Set(key, value);
        if (!value.IsNil && table.TryGetExistingEntry(key, out _, out entry))
        {
            regionSite.Observe(table, stringKey, entry);
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryBindTable(
        ref LuaTable? cachedTable,
        LuaValue target,
        out LuaTable table)
    {
        table = cachedTable!;
        if (table is not null)
        {
            return true;
        }

        if (target.TryGetTable() is not { } resolved)
        {
            table = null!;
            return false;
        }

        cachedTable = resolved;
        table = resolved;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TryReadRegionEntry(
        LuaTable table,
        LuaString key,
        LuaCodegenTableSiteCache cache,
        ref LuaCodegenTableRegionSite regionSite,
        out LuaValue value)
    {
        if (!regionSite.HasEntry ||
            !ReferenceEquals(regionSite.Table, table) ||
            !ReferenceEquals(regionSite.Key, key))
        {
            value = LuaValue.Nil;
            return false;
        }

        if (regionSite.MetatableVersion == table.MetatableVersion &&
            table.TryReadExistingStringEntry(regionSite.Entry, key, out value))
        {
            return true;
        }

        cache.RecordInvalidation();
        regionSite = default;
        value = LuaValue.Nil;
        return false;
    }
}

/// <summary>
/// Opaque state for one table access site during a compiled numeric-region invocation.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public struct LuaCodegenTableRegionSite
{
    internal LuaTable? Table;
    internal LuaString? Key;
    internal LuaTableExistingEntry Entry;
    internal ulong MetatableVersion;
    internal bool HasEntry;

    internal void Observe(
        LuaTable table,
        LuaString key,
        LuaTableExistingEntry entry)
    {
        Table = table;
        Key = key;
        Entry = entry;
        MetatableVersion = table.MetatableVersion;
        HasEntry = true;
    }
}
