using System.Globalization;
using System.Numerics;
using System.Text;
using System.Buffers;
using Lunil.IR.Lua54;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Operations;
using Lunil.Runtime.Values;

namespace Lunil.StandardLibrary;

internal static class LuaStringLibrary
{
    private static readonly SearchValues<byte> PatternSpecials =
        SearchValues.Create("^$*+?.([%-"u8);
    private static readonly LuaNativeFunction GMatchIterator = new("string.gmatch", GMatchNext);

    public static LuaTable Install(LuaState state)
    {
        var module = state.CreateTable(hashCapacity: 24);
        LuaLibraryHelpers.SetFunction(state, module, "byte", Byte);
        LuaLibraryHelpers.SetFunction(state, module, "char", Character);
        LuaLibraryHelpers.SetFunction(state, module, "dump", Dump);
        LuaLibraryHelpers.SetFunction(state, module, "find", Find);
        LuaLibraryHelpers.SetFunction(state, module, "format", Format);
        LuaLibraryHelpers.SetFunction(state, module, "gmatch", GMatch);
        LuaLibraryHelpers.SetFunction(state, module, "gsub", GSub);
        LuaLibraryHelpers.SetFunction(state, module, "len", Length);
        LuaLibraryHelpers.SetFunction(state, module, "lower", Lower);
        LuaLibraryHelpers.SetFunction(state, module, "match", Match);
        LuaLibraryHelpers.SetFunction(state, module, "pack", Pack);
        LuaLibraryHelpers.SetFunction(state, module, "packsize", PackSize);
        LuaLibraryHelpers.SetFunction(state, module, "rep", Repeat);
        LuaLibraryHelpers.SetFunction(state, module, "reverse", Reverse);
        LuaLibraryHelpers.SetFunction(state, module, "sub", Substring);
        LuaLibraryHelpers.SetFunction(state, module, "unpack", Unpack);
        LuaLibraryHelpers.SetFunction(state, module, "upper", Upper);

        var metatable = state.GetTypeMetatable(LuaValueKind.String) ?? state.CreateTable(hashCapacity: 8);
        metatable.Set(LuaLibraryHelpers.String(state, "__index"), LuaValue.FromTable(module));
        state.SetTypeMetatable(LuaValueKind.String, metatable);
        state.SetGlobal("string", LuaValue.FromTable(module));
        return module;
    }

    private static LuaValue[] Length(LuaState _, ReadOnlySpan<LuaValue> arguments) =>
        [LuaValue.FromInteger(CheckBytes(arguments, 0, "len").Length)];

    private static LuaValue[] Substring(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var source = CheckBytes(arguments, 0, "sub");
        var start = RelativePosition(LuaLibraryHelpers.CheckInteger(arguments, 1, "sub"), source.Length);
        var end = RelativePosition(LuaLibraryHelpers.OptionalInteger(arguments, 2, -1, "sub"), source.Length);
        start = Math.Max(start, 1);
        end = Math.Min(end, source.Length);
        return [String(state, start <= end ? source.AsSpan((int)start - 1, (int)(end - start + 1)) : [])];
    }

    private static LuaValue[] Reverse(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var bytes = CheckBytes(arguments, 0, "reverse");
        Array.Reverse(bytes);
        return [String(state, bytes)];
    }

    private static LuaValue[] Lower(LuaState state, ReadOnlySpan<LuaValue> arguments) =>
        [String(state, ChangeAsciiCase(CheckBytes(arguments, 0, "lower"), upper: false))];

    private static LuaValue[] Upper(LuaState state, ReadOnlySpan<LuaValue> arguments) =>
        [String(state, ChangeAsciiCase(CheckBytes(arguments, 0, "upper"), upper: true))];

    private static LuaValue[] Repeat(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var source = CheckBytes(arguments, 0, "rep");
        var count = LuaLibraryHelpers.CheckInteger(arguments, 1, "rep");
        var separator = arguments.Length < 3
            ? []
            : LuaLibraryHelpers.CheckStringBytes(arguments, 2, "rep");
        if (count <= 0)
        {
            return [String(state, [])];
        }

        const long maximumLength = int.MaxValue - 1L;
        if (source.Length != 0 && count > maximumLength / source.Length)
        {
            throw new LuaRuntimeException("resulting string too large");
        }

        var length = (long)source.Length * count;
        var separators = count - 1;
        if (separator.Length != 0 && separators > (maximumLength - length) / separator.Length)
        {
            throw new LuaRuntimeException("resulting string too large");
        }

        length += (long)separator.Length * separators;

        var output = new byte[(int)length];
        var offset = 0;
        for (var index = 0L; index < count; index++)
        {
            if (index != 0)
            {
                separator.CopyTo(output, offset);
                offset += separator.Length;
            }

            source.CopyTo(output, offset);
            offset += source.Length;
        }

        return [String(state, output)];
    }

    private static LuaValue[] Byte(LuaState _, ReadOnlySpan<LuaValue> arguments)
    {
        var source = CheckBytes(arguments, 0, "byte");
        var start = RelativePosition(LuaLibraryHelpers.OptionalInteger(arguments, 1, 1, "byte"), source.Length);
        var end = RelativePosition(LuaLibraryHelpers.OptionalInteger(arguments, 2, start, "byte"), source.Length);
        start = Math.Max(start, 1);
        end = Math.Min(end, source.Length);
        if (start > end)
        {
            return [];
        }

        var length = end - start + 1;
        if (length >= int.MaxValue)
        {
            throw new LuaRuntimeException("string slice too long");
        }

        var result = new LuaValue[(int)length];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = LuaValue.FromInteger(source[(int)start - 1 + index]);
        }

        return result;
    }

    private static LuaValue[] Character(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var result = new byte[arguments.Length];
        for (var index = 0; index < result.Length; index++)
        {
            var value = LuaLibraryHelpers.CheckInteger(arguments, index, "char");
            if (unchecked((ulong)value) > byte.MaxValue)
            {
                throw LuaLibraryHelpers.BadArgument("char", index, "value out of range");
            }

            result[index] = (byte)value;
        }

        return [String(state, result)];
    }

    private static LuaValue[] Find(LuaState state, ReadOnlySpan<LuaValue> arguments) =>
        FindOrMatch(state, arguments, find: true);

    private static LuaValue[] Match(LuaState state, ReadOnlySpan<LuaValue> arguments) =>
        FindOrMatch(state, arguments, find: false);

    private static LuaValue[] FindOrMatch(
        LuaState state,
        ReadOnlySpan<LuaValue> arguments,
        bool find)
    {
        var function = find ? "find" : "match";
        var source = CheckBytes(arguments, 0, function);
        var pattern = CheckBytes(arguments, 1, function);
        var initial = RelativePosition(
            LuaLibraryHelpers.OptionalInteger(arguments, 2, 1, function),
            source.Length);
        if (initial < 1)
        {
            initial = 1;
        }

        if (initial > source.Length + 1L)
        {
            return [LuaValue.Nil];
        }

        if (find && (arguments.Length > 3 && arguments[3].IsTruthy || !HasPatternSpecials(pattern)))
        {
            var offset = source.AsSpan((int)initial - 1).IndexOf(pattern);
            return offset < 0
                ? [LuaValue.Nil]
                :
                [
                    LuaValue.FromInteger(initial + offset),
                    LuaValue.FromInteger(initial + offset + pattern.Length - 1),
                ];
        }

        var anchored = pattern.Length != 0 && pattern[0] == (byte)'^';
        var match = new LuaPatternMatcher(source, pattern).Find((int)initial - 1, anchored);
        if (match is null)
        {
            return [LuaValue.Nil];
        }

        var captures = CaptureValues(state, source, match);
        return find
            ? [LuaValue.FromInteger(match.Start + 1L), LuaValue.FromInteger(match.End), .. captures]
            : captures;
    }

    private static LuaValue[] GMatch(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var source = LuaValue.FromString(state.Strings.GetOrCreate(CheckBytes(arguments, 0, "gmatch")));
        var pattern = LuaValue.FromString(state.Strings.GetOrCreate(CheckBytes(arguments, 1, "gmatch")));
        var initial = RelativePosition(
            LuaLibraryHelpers.OptionalInteger(arguments, 2, 1, "gmatch"),
            source.AsString().Length);
        var offset = initial is < 1 or > int.MaxValue
            ? source.AsString().Length + 1L
            : initial - 1;
        var closure = state.CreateNativeClosure(
            GMatchIterator,
            [source, pattern, LuaValue.FromInteger(offset), LuaValue.FromInteger(-1)]);
        return [LuaValue.FromFunction(closure)];
    }

    private static LuaNativeStep GMatchNext(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values)
    {
        var closure = context.Closure ?? throw new LuaRuntimeException("invalid gmatch iterator");
        var sourceValue = closure.GetCapture(0);
        var patternValue = closure.GetCapture(1);
        var source = sourceValue.AsString().AsSpan();
        var pattern = patternValue.AsString().AsSpan();
        var offset = closure.GetCapture(2).AsInteger();
        var lastEnd = closure.GetCapture(3).AsInteger();
        if (offset > source.Length)
        {
            return LuaNativeStep.Completed();
        }

        var match = new LuaPatternMatcher(source, pattern).Find((int)offset, anchored: false);
        while (match is not null && match.End == lastEnd)
        {
            if (++offset > source.Length)
            {
                return LuaNativeStep.Completed();
            }

            match = new LuaPatternMatcher(source, pattern).Find((int)offset, anchored: false);
        }

        if (match is null)
        {
            closure.SetCapture(2, LuaValue.FromInteger(source.Length + 1L));
            return LuaNativeStep.Completed();
        }

        closure.SetCapture(2, LuaValue.FromInteger(match.End));
        closure.SetCapture(3, LuaValue.FromInteger(match.End));
        return LuaNativeStep.Completed(CaptureValues(context.State, source, match));
    }

    private static LuaNativeStep GSub(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values)
    {
        LuaValue sourceValue;
        LuaValue patternValue;
        LuaValue replacement;
        LuaNativeByteBuffer output;
        long maximum;
        long count;
        long scan;
        long copied;
        long lastMatch;
        var resumedReplacement = continuationId != 0;
        var changed = false;
        if (continuationId == 0)
        {
            var sourceArgument = LuaLibraryHelpers.Required(values, 0, "gsub");
            sourceValue = sourceArgument.Kind == LuaValueKind.String
                ? sourceArgument
                : LuaValue.FromString(context.State.Strings.GetOrCreate(CheckBytes(values, 0, "gsub")));
            patternValue = LuaValue.FromString(context.State.Strings.GetOrCreate(CheckBytes(values, 1, "gsub")));
            replacement = LuaLibraryHelpers.Required(values, 2, "gsub");
            if (replacement.Kind is not (LuaValueKind.String or LuaValueKind.Integer or
                LuaValueKind.Float or LuaValueKind.Function or LuaValueKind.Table))
            {
                throw LuaLibraryHelpers.BadArgument(
                    "gsub",
                    2,
                    $"string/function/table expected, got {LuaLibraryHelpers.TypeName(replacement)}");
            }

            maximum = LuaLibraryHelpers.OptionalInteger(
                values,
                3,
                sourceValue.AsString().Length + 1L,
                "gsub");
            output = context.CreateByteBuffer(sourceValue.AsString().Length);
            count = 0;
            scan = 0;
            copied = 0;
            lastMatch = -1;
        }
        else
        {
            var state = context.InvocationState;
            sourceValue = state[0];
            patternValue = state[1];
            replacement = state[2];
            maximum = state[3].AsInteger();
            count = state[4].AsInteger();
            scan = state[5].AsInteger();
            copied = state[6].AsInteger();
            lastMatch = state[7].AsInteger();
            changed = state[8].AsBoolean();
            var matchStart = state[9].AsInteger();
            var matchEnd = state[10].AsInteger();
            output = context.ByteBuffer ??
                throw new InvalidOperationException("string.gsub lost its byte buffer.");
            var replacementValue = values.Length == 0 ? LuaValue.Nil : values[0];
            output.Append(sourceValue.AsString().AsSpan().Slice(
                (int)copied,
                (int)(matchStart - copied)));
            changed |= AppendDynamicReplacement(
                output,
                replacementValue,
                sourceValue.AsString().AsSpan().Slice(
                    (int)matchStart,
                    (int)(matchEnd - matchStart)));
            copied = matchEnd;
            scan = matchEnd;
            lastMatch = matchEnd;
            count++;
        }

        var source = sourceValue.AsString().AsSpan();
        var pattern = patternValue.AsString().AsSpan();
        var anchored = pattern.Length != 0 && pattern[0] == (byte)'^';
        var matcher = new LuaPatternMatcher(source, pattern);
        while ((!resumedReplacement || !anchored) &&
            count < maximum && scan <= source.Length)
        {
            var match = matcher.Find((int)scan, anchored);
            if (match is null)
            {
                break;
            }

            if (match.End == lastMatch)
            {
                if (scan < source.Length)
                {
                    scan++;
                    continue;
                }

                break;
            }

            LuaValue replacementValue;
            if (replacement.Kind == LuaValueKind.Function)
            {
                return LuaNativeStep.CallLuaWithByteBuffer(
                    replacement,
                    CaptureValues(context.State, source, match),
                    1,
                    GSubState(sourceValue, patternValue, replacement, maximum,
                        count, scan, copied, lastMatch, changed, match.Start, match.End),
                    false,
                    byteBuffer: output);
            }

            if (replacement.Kind == LuaValueKind.Table)
            {
                var key = match.Captures[0].ToLuaValue(context.State, source);
                var get = LuaRuntimeOperations.GetIndex(context.State, replacement, key);
                if (get.RequiresCall)
                {
                    return LuaNativeStep.CallLuaWithByteBuffer(
                        get.Callable,
                        get.Arguments,
                        1,
                        GSubState(sourceValue, patternValue, replacement, maximum,
                            count, scan, copied, lastMatch, changed, match.Start, match.End),
                        false,
                        byteBuffer: output);
                }

                replacementValue = get.Value;
            }
            else
            {
                output.Append(source.Slice((int)copied, match.Start - (int)copied));
                AppendTemplateReplacement(output, replacement, source, match);
                changed = true;
                copied = match.End;
                scan = match.End;
                lastMatch = match.End;
                count++;
                if (anchored)
                {
                    break;
                }

                continue;
            }

            output.Append(source.Slice((int)copied, match.Start - (int)copied));
            changed |= AppendDynamicReplacement(
                output,
                replacementValue,
                source.Slice(match.Start, match.End - match.Start));
            copied = match.End;
            scan = match.End;
            lastMatch = match.End;
            count++;
            if (anchored)
            {
                break;
            }
        }

        if (!changed)
        {
            return LuaNativeStep.Completed(sourceValue, LuaValue.FromInteger(count));
        }

        output.Append(source[(int)copied..]);
        return LuaNativeStep.Completed(
            String(context.State, output.WrittenSpan),
            LuaValue.FromInteger(count));
    }

    private static LuaNativeStep Format(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values) =>
        LuaStringFormat.Format(context, continuationId, values);

    private static LuaValue[] Pack(LuaState state, ReadOnlySpan<LuaValue> arguments) =>
        [LuaStringPack.Pack(state, arguments)];

    private static LuaValue[] PackSize(LuaState _, ReadOnlySpan<LuaValue> arguments) =>
        [LuaValue.FromInteger(LuaStringPack.PackSize(arguments))];

    private static LuaValue[] Unpack(LuaState state, ReadOnlySpan<LuaValue> arguments) =>
        LuaStringPack.Unpack(state, arguments);

    private static LuaValue[] Dump(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var function = LuaLibraryHelpers.Required(arguments, 0, "dump");
        if (function.Kind != LuaValueKind.Function)
        {
            throw LuaLibraryHelpers.BadArgument(
                "dump",
                0,
                $"function expected, got {LuaLibraryHelpers.TypeName(function)}");
        }

        if (function.TryGetClosure() is not { } closure)
        {
            throw new LuaRuntimeException("unable to dump given function");
        }

        var strip = arguments.Length > 1 && arguments[1].IsTruthy;
        try
        {
            var bytes = Lua54CanonicalPrototypeWriter.Write(
                closure.Module,
                closure.Function.Id,
                strip);
            return [String(state, bytes)];
        }
        catch (Exception exception) when (
            exception is InvalidDataException or InvalidOperationException or
                ArgumentOutOfRangeException or OverflowException)
        {
            throw new LuaRuntimeException("unable to dump given function");
        }
    }

    private static LuaValue[] CaptureValues(
        LuaState state,
        ReadOnlySpan<byte> source,
        PatternMatch match)
    {
        var values = new LuaValue[match.Captures.Length];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = match.Captures[index].ToLuaValue(state, source);
        }

        return values;
    }

    private static LuaValue[] GSubState(
        LuaValue source,
        LuaValue pattern,
        LuaValue replacement,
        long maximum,
        long count,
        long scan,
        long copied,
        long lastMatch,
        bool changed,
        int matchStart,
        int matchEnd) =>
        [source, pattern, replacement, LuaValue.FromInteger(maximum),
            LuaValue.FromInteger(count), LuaValue.FromInteger(scan), LuaValue.FromInteger(copied),
            LuaValue.FromInteger(lastMatch), LuaValue.FromBoolean(changed),
            LuaValue.FromInteger(matchStart), LuaValue.FromInteger(matchEnd)];

    private static void AppendTemplateReplacement(
        LuaNativeByteBuffer output,
        LuaValue replacement,
        ReadOnlySpan<byte> source,
        PatternMatch match)
    {
        if (replacement.Kind == LuaValueKind.Integer)
        {
            output.Append(Encoding.ASCII.GetBytes(
                replacement.AsInteger().ToString(CultureInfo.InvariantCulture)));
            return;
        }

        if (replacement.Kind == LuaValueKind.Float)
        {
            output.Append(Encoding.ASCII.GetBytes(
                LuaValueOperations.FormatFloat(replacement.AsFloat())));
            return;
        }

        var template = replacement.AsString().AsSpan();
        var literalStart = 0;
        for (var index = 0; index < template.Length; index++)
        {
            if (template[index] != (byte)'%')
            {
                continue;
            }

            output.Append(template.Slice(literalStart, index - literalStart));

            if (++index == template.Length)
            {
                throw new LuaRuntimeException("invalid use of '%' in replacement string");
            }

            var code = template[index];
            if (code == (byte)'%')
            {
                output.Append(template.Slice(index, 1));
                literalStart = index + 1;
                continue;
            }

            if (code is < (byte)'0' or > (byte)'9')
            {
                throw new LuaRuntimeException("invalid use of '%' in replacement string");
            }

            var capture = code - (byte)'0';
            if (capture == 0)
            {
                output.Append(source.Slice(match.Start, match.End - match.Start));
            }
            else if (capture <= match.Captures.Length)
            {
                var captured = match.Captures[capture - 1];
                if (captured.IsPosition)
                {
                    output.Append(Encoding.ASCII.GetBytes(
                        (captured.Start + 1L).ToString(CultureInfo.InvariantCulture)));
                }
                else
                {
                    output.Append(source.Slice(captured.Start, captured.Length));
                }
            }
            else
            {
                throw new LuaRuntimeException($"invalid capture index %{capture}");
            }

            literalStart = index + 1;
        }

        output.Append(template[literalStart..]);
    }

    private static bool AppendDynamicReplacement(
        LuaNativeByteBuffer output,
        LuaValue value,
        ReadOnlySpan<byte> original)
    {
        if (!value.IsTruthy)
        {
            output.Append(original);
            return false;
        }

        switch (value.Kind)
        {
            case LuaValueKind.String:
                output.Append(value.AsString().AsSpan());
                break;
            case LuaValueKind.Integer:
                output.Append(Encoding.ASCII.GetBytes(
                    value.AsInteger().ToString(CultureInfo.InvariantCulture)));
                break;
            case LuaValueKind.Float:
                output.Append(Encoding.ASCII.GetBytes(
                    LuaValueOperations.FormatFloat(value.AsFloat())));
                break;
            default:
                throw new LuaRuntimeException(
                    $"invalid replacement value (a {LuaLibraryHelpers.TypeName(value)})");
        }

        return true;
    }

    private static byte[] CheckBytes(ReadOnlySpan<LuaValue> arguments, int index, string function) =>
        LuaLibraryHelpers.CheckStringBytes(arguments, index, function);

    private static LuaValue String(LuaState state, ReadOnlySpan<byte> bytes) =>
        LuaValue.FromString(state.Strings.GetOrCreate(bytes));

    private static long RelativePosition(long position, int length) => position >= 0
        ? position
        : position < -length ? 0 : length + position + 1L;

    private static byte[] ChangeAsciiCase(byte[] source, bool upper)
    {
        for (var index = 0; index < source.Length; index++)
        {
            if (upper && source[index] is >= (byte)'a' and <= (byte)'z')
            {
                source[index] -= 32;
            }
            else if (!upper && source[index] is >= (byte)'A' and <= (byte)'Z')
            {
                source[index] += 32;
            }
        }

        return source;
    }

    private static bool HasPatternSpecials(ReadOnlySpan<byte> pattern) =>
        pattern.IndexOfAny(PatternSpecials) >= 0;
}
