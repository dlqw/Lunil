using System;
using System.Diagnostics;
using Lunil.Core;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.StandardLibrary;

/// <summary>Lua 5.2's unsigned 32-bit bit32 library.</summary>
internal static class LuaBit32Library
{
    public static LuaTable Install(LuaState state)
    {
        var module = state.CreateTable(hashCapacity: 12);
        Set(state, module, "band", static (_, values) => [LuaValue.FromInteger(Bitwise(values, '&'))]);
        Set(state, module, "bor", static (_, values) => [LuaValue.FromInteger(Bitwise(values, '|'))]);
        Set(state, module, "bxor", static (_, values) => [LuaValue.FromInteger(Bitwise(values, '^'))]);
        Set(state, module, "bnot", static (_, values) =>
            [LuaValue.FromInteger(unchecked((int)~ToUInt(values, 0)))]);
        Set(state, module, "lshift", static (_, values) =>
            [LuaValue.FromInteger(unchecked((int)ShiftLeft(ToUInt(values, 0), Shift(values, 1))))]);
        Set(state, module, "rshift", static (_, values) =>
            [LuaValue.FromInteger(unchecked((int)ShiftRight(ToUInt(values, 0), Shift(values, 1))))]);
        Set(state, module, "arshift", static (_, values) =>
            [LuaValue.FromInteger(unchecked((int)ArithmeticShift(ToUInt(values, 0), Shift(values, 1))))]);
        Set(state, module, "lrotate", static (_, values) =>
            [LuaValue.FromInteger(unchecked((int)Rotate(ToUInt(values, 0), Shift(values, 1), true)))]);
        Set(state, module, "rrotate", static (_, values) =>
            [LuaValue.FromInteger(unchecked((int)Rotate(ToUInt(values, 0), Shift(values, 1), false)))]);
        Set(state, module, "btest", static (_, values) =>
            [LuaValue.FromBoolean(Bitwise(values, '&') != 0)]);
        Set(state, module, "extract", static (_, values) =>
            [LuaValue.FromInteger(unchecked((int)((ToUInt(values, 0) >> Start(values, 1)) & Mask(values, 1, 2, values.Length > 2))))]);
        Set(state, module, "replace", static (_, values) =>
        {
            var value = ToUInt(values, 0);
            var field = ToUInt(values, 1);
            var start = Start(values, 2);
            var mask = Mask(values, 2, 3, values.Length > 3) << start;
            return [LuaValue.FromInteger(unchecked((int)((value & ~mask) | ((field << start) & mask))))];
        });
        state.SetGlobal("bit32", LuaValue.FromTable(module));
        return module;
    }

    private static void Set(
        LuaState state,
        LuaTable table,
        string name,
        LuaNativeFunctionBody function) =>
        LuaLibraryHelpers.SetFunction(state, table, name, function);

    private static uint Bitwise(ReadOnlySpan<LuaValue> values, char operation)
    {
        var result = operation == '&' ? uint.MaxValue : 0u;
        foreach (var value in values)
        {
            result = operation switch
            {
                '&' => result & ToUInt(value),
                '|' => result | ToUInt(value),
                '^' => result ^ ToUInt(value),
                _ => throw new UnreachableException(),
            };
        }

        return result;
    }

    private static uint ToUInt(ReadOnlySpan<LuaValue> values, int index) =>
        ToUInt(LuaLibraryHelpers.Required(values, index, "bit32"));

    private static uint ToUInt(LuaValue value)
    {
        var integer = LuaLibraryHelpers.CheckInteger([value], 0, "bit32");
        return unchecked((uint)integer);
    }

    private static int Shift(ReadOnlySpan<LuaValue> values, int index) =>
        checked((int)LuaLibraryHelpers.CheckInteger(values, index, "bit32"));

    private static int Start(ReadOnlySpan<LuaValue> values, int index)
    {
        var start = Shift(values, index);
        return start;
    }

    private static uint Mask(ReadOnlySpan<LuaValue> values, int startIndex, int widthIndex, bool hasWidth)
    {
        var width = hasWidth ? Shift(values, widthIndex) : 1;

        var start = Start(values, startIndex);
        if (start < 0 || width <= 0 || start + width > 32)
        {
            throw LuaLibraryHelpers.BadArgument("bit32", widthIndex, "field is out of range");
        }

        return width == 32 ? uint.MaxValue : (1u << width) - 1;
    }

    private static uint Rotate(uint value, int shift, bool left)
    {
        shift &= 31;
        return left ? (value << shift) | (value >> ((32 - shift) & 31))
            : (value >> shift) | (value << ((32 - shift) & 31));
    }

    private static uint ShiftLeft(uint value, int shift) => shift < 0 ? ShiftRight(value, -shift) :
        shift >= 32 ? 0 : value << shift;

    private static uint ShiftRight(uint value, int shift) => shift < 0 ? ShiftLeft(value, -shift) :
        shift >= 32 ? 0 : value >> shift;

    private static uint ArithmeticShift(uint value, int shift)
    {
        if (shift < 0) return ShiftLeft(value, -shift);
        if (shift >= 32) return (value & 0x80000000) != 0 ? uint.MaxValue : 0;
        return unchecked((uint)((int)value >> shift));
    }
}
