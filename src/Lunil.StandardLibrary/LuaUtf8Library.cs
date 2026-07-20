using Lunil.Core;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.StandardLibrary;

internal static class LuaUtf8Library
{
    private const uint MaximumUnicode = 0x10ffff;
    private const uint MaximumUtf8 = 0x7fffffff;
    private static readonly uint[] MinimumValues =
        [uint.MaxValue, 0x80, 0x800, 0x10000, 0x200000, 0x4000000];

    private static readonly LuaNativeFunction StrictIterator =
        new("utf8.codes", static (_, arguments) => Iterate(arguments, strict: true));
    private static readonly LuaNativeFunction LaxIterator =
        new("utf8.codes", static (_, arguments) => Iterate(arguments, strict: false));

    public static LuaTable Install(LuaState state)
    {
        var module = state.CreateTable(hashCapacity: 8);
        LuaLibraryHelpers.SetFunction(state, module, "char", Character);
        LuaLibraryHelpers.SetFunction(state, module, "codepoint", CodePoint);
        LuaLibraryHelpers.SetFunction(state, module, "codes", Codes);
        LuaLibraryHelpers.SetFunction(state, module, "len", Length);
        LuaLibraryHelpers.SetFunction(state, module, "offset", Offset);
        var maximumLeadingByte = state.LanguageVersion == LuaLanguageVersion.Lua53
            ? (byte)0xf4
            : (byte)0xfd;
        LuaLibraryHelpers.Set(
            state,
            module,
            "charpattern",
            LuaValue.FromString(state.Strings.GetOrCreate(
                [
                    (byte)'[', 0, (byte)'-', 0x7f, 0xc2, (byte)'-', maximumLeadingByte, (byte)']',
                    (byte)'[', 0x80, (byte)'-', 0xbf, (byte)']', (byte)'*',
                ])));
        state.SetGlobal("utf8", LuaValue.FromTable(module));
        return module;
    }

    private static LuaValue[] Character(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var result = new List<byte>(arguments.Length * 4);
        for (var index = 0; index < arguments.Length; index++)
        {
            var integer = LuaLibraryHelpers.CheckInteger(arguments, index, "char");
            var code = unchecked((ulong)integer);
            if (code > (state.LanguageVersion == LuaLanguageVersion.Lua53
                ? 0x10_ffffu
                : MaximumUtf8))
            {
                throw LuaLibraryHelpers.BadArgument("char", index, "value out of range");
            }

            Encode(result, (uint)code);
        }

        return [LuaValue.FromString(state.Strings.GetOrCreate(result.ToArray()))];
    }

    private static LuaValue[] Length(LuaState _, ReadOnlySpan<LuaValue> arguments)
    {
        var bytes = CheckString(arguments, 0, "len");
        var start = RelativePosition(
            LuaLibraryHelpers.OptionalInteger(arguments, 1, 1, "len"),
            bytes.Length);
        var end = RelativePosition(
            LuaLibraryHelpers.OptionalInteger(arguments, 2, -1, "len"),
            bytes.Length);
        var lax = arguments.Length > 3 && arguments[3].IsTruthy;
        if (start < 1 || --start > bytes.Length)
        {
            throw LuaLibraryHelpers.BadArgument("len", 1, "initial position out of bounds");
        }

        if (--end >= bytes.Length)
        {
            throw LuaLibraryHelpers.BadArgument("len", 2, "final position out of bounds");
        }

        long count = 0;
        while (start <= end)
        {
            if (!TryDecode(bytes, checked((int)start), !lax, out var unusedCode, out var next))
            {
                return [LuaValue.Nil, LuaValue.FromInteger(start + 1)];
            }

            start = next;
            count++;
        }

        return [LuaValue.FromInteger(count)];
    }

    private static LuaValue[] CodePoint(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var bytes = CheckString(arguments, 0, "codepoint");
        var start = RelativePosition(
            LuaLibraryHelpers.OptionalInteger(arguments, 1, 1, "codepoint"),
            bytes.Length);
        var end = RelativePosition(
            LuaLibraryHelpers.OptionalInteger(arguments, 2, start, "codepoint"),
            bytes.Length);
        var lax = arguments.Length > 3 && arguments[3].IsTruthy;
        var boundsMessage = state.LanguageVersion == LuaLanguageVersion.Lua53
            ? "out of range"
            : "out of bounds";
        if (start < 1)
        {
            throw LuaLibraryHelpers.BadArgument("codepoint", 1, boundsMessage);
        }

        if (end > bytes.Length)
        {
            throw LuaLibraryHelpers.BadArgument("codepoint", 2, boundsMessage);
        }

        if (start > end)
        {
            return [];
        }

        var values = new List<LuaValue>();
        var position = checked((int)(start - 1));
        var exclusiveEnd = checked((int)end);
        while (position < exclusiveEnd)
        {
            if (!TryDecode(bytes, position, !lax, out var code, out position))
            {
                throw new LuaRuntimeException("invalid UTF-8 code");
            }

            values.Add(LuaValue.FromInteger(code));
        }

        return values.ToArray();
    }

    private static LuaValue[] Codes(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var bytes = CheckString(arguments, 0, "codes");
        if (bytes.Length > 0 && IsContinuation(bytes[0]))
        {
            throw LuaLibraryHelpers.BadArgument("codes", 0, "invalid UTF-8 code");
        }

        var lax = arguments.Length > 1 && arguments[1].IsTruthy;
        return
        [
            LuaValue.FromFunction(lax ? LaxIterator : StrictIterator),
            LuaValue.FromString(state.Strings.GetOrCreate(bytes)),
            LuaValue.FromInteger(0),
        ];
    }

    private static LuaValue[] Iterate(ReadOnlySpan<LuaValue> arguments, bool strict)
    {
        var bytes = CheckString(arguments, 0, "codes");
        var control = unchecked((ulong)LuaLibraryHelpers.CheckInteger(arguments, 1, "codes"));
        while (control < (ulong)bytes.Length && IsContinuation(bytes[(int)control]))
        {
            control++;
        }

        if (control >= (ulong)bytes.Length)
        {
            return [];
        }

        if (!TryDecode(bytes, (int)control, strict, out var code, out var next) ||
            next < bytes.Length && IsContinuation(bytes[next]))
        {
            throw new LuaRuntimeException("invalid UTF-8 code");
        }

        return [LuaValue.FromInteger((long)control + 1), LuaValue.FromInteger(code)];
    }

    private static LuaValue[] Offset(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var bytes = CheckString(arguments, 0, "offset");
        var count = LuaLibraryHelpers.CheckInteger(arguments, 1, "offset");
        var defaultPosition = count >= 0 ? 1 : bytes.Length + 1L;
        var position = RelativePosition(
            LuaLibraryHelpers.OptionalInteger(arguments, 2, defaultPosition, "offset"),
            bytes.Length);
        if (position < 1 || --position > bytes.Length)
        {
            throw LuaLibraryHelpers.BadArgument(
                "offset",
                2,
                state.LanguageVersion == LuaLanguageVersion.Lua53
                    ? "position out of range"
                    : "position out of bounds");
        }

        if (count == 0)
        {
            while (position > 0 && IsContinuation(bytes[(int)position]))
            {
                position--;
            }
        }
        else
        {
            if (position < bytes.Length && IsContinuation(bytes[(int)position]))
            {
                throw new LuaRuntimeException("initial position is a continuation byte");
            }

            if (count < 0)
            {
                while (count < 0 && position > 0)
                {
                    do
                    {
                        position--;
                    }
                    while (position > 0 && IsContinuation(bytes[(int)position]));
                    count++;
                }
            }
            else
            {
                count--;
                while (count > 0 && position < bytes.Length)
                {
                    do
                    {
                        position++;
                    }
                    while (position < bytes.Length && IsContinuation(bytes[(int)position]));
                    count--;
                }
            }
        }

        return count == 0 ? [LuaValue.FromInteger(position + 1)] : [LuaValue.Nil];
    }

    private static byte[] CheckString(
        ReadOnlySpan<LuaValue> arguments,
        int index,
        string function)
    {
        return LuaLibraryHelpers.CheckStringBytes(arguments, index, function);
    }

    private static long RelativePosition(long position, int length)
    {
        if (position >= 0)
        {
            return position;
        }

        var magnitude = unchecked(0UL - (ulong)position);
        return magnitude > (ulong)length ? 0 : length + position + 1;
    }

    private static bool TryDecode(
        ReadOnlySpan<byte> bytes,
        int position,
        bool strict,
        out uint code,
        out int next)
    {
        code = 0;
        next = position;
        if ((uint)position >= (uint)bytes.Length)
        {
            return false;
        }

        uint first = bytes[position];
        if (first < 0x80)
        {
            code = first;
            next = position + 1;
            return true;
        }

        uint result = 0;
        var count = 0;
        var shifted = first;
        while ((shifted & 0x40) != 0)
        {
            count++;
            if (count > 5 || position + count >= bytes.Length)
            {
                return false;
            }

            var continuation = bytes[position + count];
            if (!IsContinuation(continuation))
            {
                return false;
            }

            result = (result << 6) | (uint)(continuation & 0x3f);
            shifted <<= 1;
        }

        result |= (shifted & 0x7f) << (count * 5);
        if (result > MaximumUtf8 || result < MinimumValues[count] ||
            strict && (result > MaximumUnicode || result is >= 0xd800 and <= 0xdfff))
        {
            return false;
        }

        code = result;
        next = position + count + 1;
        return true;
    }

    private static bool IsContinuation(byte value) => (value & 0xc0) == 0x80;

    private static void Encode(List<byte> result, uint code)
    {
        if (code < 0x80)
        {
            result.Add((byte)code);
            return;
        }

        Span<byte> continuation = stackalloc byte[5];
        var count = 0;
        var maximumFirstBytePayload = 0x3fu;
        do
        {
            continuation[count++] = (byte)(0x80 | (code & 0x3f));
            code >>= 6;
            maximumFirstBytePayload >>= 1;
        }
        while (code > maximumFirstBytePayload);

        result.Add((byte)((~maximumFirstBytePayload << 1) | code));
        while (count > 0)
        {
            result.Add(continuation[--count]);
        }
    }
}
