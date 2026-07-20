using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Operations;
using Lunil.Runtime.Values;

namespace Lunil.StandardLibrary;

internal static class LuaTableLibrary
{
    public static LuaTable Install(LuaState state)
    {
        var module = state.CreateTable(hashCapacity: 8);
        LuaLibraryHelpers.SetFunction(state, module, "concat", Concat);
        LuaLibraryHelpers.SetFunction(state, module, "insert", Insert);
        LuaLibraryHelpers.SetFunction(state, module, "move", Move);
        LuaLibraryHelpers.SetFunction(state, module, "pack", Pack);
        LuaLibraryHelpers.SetFunction(state, module, "remove", Remove);
        LuaLibraryHelpers.SetFunction(state, module, "sort", Sort, "table.sort");
        LuaLibraryHelpers.SetFunction(state, module, "unpack", Unpack);
        state.SetGlobal("table", LuaValue.FromTable(module));
        return module;
    }

    private static LuaNativeStep Concat(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values)
    {
        LuaValue target;
        LuaValue separator;
        LuaNativeByteBuffer output;
        long index;
        long last;

        if (continuationId == 0)
        {
            target = LuaLibraryHelpers.Required(values, 0, "concat");
            if (target.Kind != LuaValueKind.Table &&
                (LuaBasicLibrary.GetMetafield(context.State, target, "__index").IsNil ||
                 LuaBasicLibrary.GetMetafield(context.State, target, "__len").IsNil))
            {
                throw LuaLibraryHelpers.BadArgument("concat", 0, "table expected");
            }

            separator = LuaValue.FromString(context.State.Strings.GetOrCreate(
                values.Length < 2 || values[1].IsNil
                    ? []
                    : LuaLibraryHelpers.CheckStringBytes(values, 1, "concat")));
            index = LuaLibraryHelpers.OptionalInteger(values, 2, 1, "concat");
            if (values.Length >= 4 && !values[3].IsNil)
            {
                last = LuaLibraryHelpers.CheckInteger(values, 3, "concat");
            }
            else
            {
                return ResolveLength(
                    context,
                    target,
                    1,
                    [target, separator, LuaValue.FromInteger(index)],
                    LengthContinuation.Concat);
            }

            output = context.CreateByteBuffer();
        }
        else if (continuationId == 1)
        {
            var state = context.InvocationState;
            target = state[0];
            separator = state[1];
            index = state[2].AsInteger();
            last = CallbackInteger(values, "object length is not an integer");
            output = context.CreateByteBuffer();
        }
        else
        {
            var state = context.InvocationState;
            target = state[0];
            separator = state[1];
            index = state[2].AsInteger();
            last = state[3].AsInteger();
            output = context.ByteBuffer ??
                throw new InvalidOperationException("table.concat lost its byte buffer.");
            var item = CallbackValue(values);
            AppendConcatValue(output, item, separator.AsString().AsSpan(), index, last);
            if (index == last)
            {
                return LuaNativeStep.Completed(LuaValue.FromString(
                    output.MoveToString(context.State.Strings)));
            }

            index++;
        }

        if (TryConcatRawTable(
                context,
                target,
                output,
                separator.AsString().AsSpan(),
                index,
                last,
                out var directResult))
        {
            return LuaNativeStep.Completed(directResult);
        }

        while (index <= last)
        {
            var resolution = LuaRuntimeOperations.GetIndex(
                context.State,
                target,
                LuaValue.FromInteger(index));
            if (resolution.RequiresCall)
            {
                return LuaNativeStep.CallLuaWithByteBuffer(
                    resolution.Callable,
                    resolution.Arguments,
                    2,
                    [target, separator, LuaValue.FromInteger(index), LuaValue.FromInteger(last)],
                    callIsYieldable: false,
                    byteBuffer: output);
            }

            AppendConcatValue(
                output,
                resolution.Value,
                separator.AsString().AsSpan(),
                index,
                last);
            if (index == last)
            {
                return LuaNativeStep.Completed(LuaValue.FromString(
                    output.MoveToString(context.State.Strings)));
            }

            index++;
        }

        return LuaNativeStep.Completed(LuaValue.FromString(
            output.MoveToString(context.State.Strings)));
    }

    private static LuaNativeStep Insert(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values)
    {
        if (continuationId == 0)
        {
            var target = LuaLibraryHelpers.Required(values, 0, "insert");
            if (values.Length is not (2 or 3))
            {
                throw new LuaRuntimeException("wrong number of arguments to 'insert'");
            }

            var position = values.Length == 3
                ? LuaLibraryHelpers.CheckInteger(values, 1, "insert")
                : long.MinValue;
            var item = values[^1];
            return ResolveLength(
                context,
                target,
                1,
                [target, item, LuaValue.FromInteger(position)],
                LengthContinuation.Insert);
        }

        if (continuationId == 4)
        {
            return LuaNativeStep.Completed();
        }

        var state = context.InvocationState;
        var table = state[0];
        var itemValue = state[1];
        var positionValue = state[2].AsInteger();
        long empty;
        long index;
        LuaValue moved = LuaValue.Nil;
        if (continuationId == 1)
        {
            empty = unchecked(CallbackInteger(values, "object length is not an integer") + 1);
            var position = positionValue == long.MinValue ? empty : positionValue;
            if (positionValue != long.MinValue &&
                unchecked((ulong)position - 1) >= unchecked((ulong)empty))
            {
                throw LuaLibraryHelpers.BadArgument("insert", 1, "position out of bounds");
            }

            positionValue = position;
            index = empty;
        }
        else
        {
            empty = state[3].AsInteger();
            index = state[4].AsInteger();
            if (continuationId == 2)
            {
                moved = CallbackValue(values);
            }
            else
            {
                index--;
            }
        }

        while (index > positionValue)
        {
            if (continuationId != 2)
            {
                var get = LuaRuntimeOperations.GetIndex(
                    context.State,
                    table,
                    LuaValue.FromInteger(index - 1));
                if (get.RequiresCall)
                {
                    return LuaNativeStep.CallLua(
                        get.Callable,
                        get.Arguments,
                        2,
                        [table, itemValue, LuaValue.FromInteger(positionValue), LuaValue.FromInteger(empty), LuaValue.FromInteger(index)],
                        callIsYieldable: false);
                }

                moved = get.Value;
            }

            var set = LuaRuntimeOperations.SetIndex(
                context.State,
                table,
                LuaValue.FromInteger(index),
                moved);
            if (set.RequiresCall)
            {
                return LuaNativeStep.CallLua(
                    set.Callable,
                    set.Arguments,
                    3,
                    [table, itemValue, LuaValue.FromInteger(positionValue), LuaValue.FromInteger(empty), LuaValue.FromInteger(index)],
                    callIsYieldable: false);
            }

            continuationId = 3;
            index--;
        }

        var finalSet = LuaRuntimeOperations.SetIndex(
            context.State,
            table,
            LuaValue.FromInteger(positionValue),
            itemValue);
        return finalSet.RequiresCall
            ? LuaNativeStep.CallLua(finalSet.Callable, finalSet.Arguments, 4, [], false)
            : LuaNativeStep.Completed();
    }

    internal static LuaValue[] Pack(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var result = state.CreateTable(arguments.Length, 1);
        for (var index = 0; index < arguments.Length; index++)
        {
            result.Set(LuaValue.FromInteger(index + 1L), arguments[index]);
        }

        result.Set(LuaLibraryHelpers.String(state, "n"), LuaValue.FromInteger(arguments.Length));
        return [LuaValue.FromTable(result)];
    }

    private static LuaNativeStep Remove(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values)
    {
        if (continuationId == 0)
        {
            var target = LuaLibraryHelpers.Required(values, 0, "remove");
            var requested = values.Length < 2 || values[1].IsNil
                ? long.MinValue
                : LuaLibraryHelpers.CheckInteger(values, 1, "remove");
            return ResolveLength(
                context,
                target,
                1,
                [target, LuaValue.FromInteger(requested)],
                LengthContinuation.Remove);
        }

        if (continuationId == 5)
        {
            return LuaNativeStep.Completed(context.InvocationState[0]);
        }

        var state = context.InvocationState;
        var table = state[0];
        var requestedPosition = state[1].AsInteger();
        long size;
        long position;
        LuaValue result;
        long index;
        LuaValue moved = LuaValue.Nil;
        if (continuationId == 1)
        {
            size = CallbackInteger(values, "object length is not an integer");
            position = requestedPosition == long.MinValue ? size : requestedPosition;
            if (position != size && unchecked((ulong)position - 1) > unchecked((ulong)size))
            {
                throw LuaLibraryHelpers.BadArgument("remove", 1, "position out of bounds");
            }

            var get = LuaRuntimeOperations.GetIndex(context.State, table, LuaValue.FromInteger(position));
            if (get.RequiresCall)
            {
                return LuaNativeStep.CallLua(
                    get.Callable,
                    get.Arguments,
                    2,
                    [table, LuaValue.FromInteger(position), LuaValue.FromInteger(size)],
                    false);
            }

            result = get.Value;
            index = position;
        }
        else
        {
            position = state[1].AsInteger();
            size = state[2].AsInteger();
            result = continuationId == 2 ? CallbackValue(values) : state[3];
            index = continuationId == 2 ? position : state[4].AsInteger();
            if (continuationId == 3)
            {
                moved = CallbackValue(values);
            }
            else if (continuationId == 4)
            {
                index++;
            }
        }

        while (index < size)
        {
            if (continuationId != 3)
            {
                var get = LuaRuntimeOperations.GetIndex(
                    context.State,
                    table,
                    LuaValue.FromInteger(index + 1));
                if (get.RequiresCall)
                {
                    return LuaNativeStep.CallLua(
                        get.Callable,
                        get.Arguments,
                        3,
                        [table, LuaValue.FromInteger(position), LuaValue.FromInteger(size), result, LuaValue.FromInteger(index)],
                        false);
                }

                moved = get.Value;
            }

            var set = LuaRuntimeOperations.SetIndex(
                context.State,
                table,
                LuaValue.FromInteger(index),
                moved);
            if (set.RequiresCall)
            {
                return LuaNativeStep.CallLua(
                    set.Callable,
                    set.Arguments,
                    4,
                    [table, LuaValue.FromInteger(position), LuaValue.FromInteger(size), result, LuaValue.FromInteger(index)],
                    false);
            }

            continuationId = 4;
            index++;
        }

        var clear = LuaRuntimeOperations.SetIndex(
            context.State,
            table,
            LuaValue.FromInteger(index),
            LuaValue.Nil);
        return clear.RequiresCall
            ? LuaNativeStep.CallLua(clear.Callable, clear.Arguments, 5, [result], false)
            : LuaNativeStep.Completed(result);
    }

    private static LuaNativeStep Move(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values)
    {
        LuaValue source;
        LuaValue destination;
        long first;
        long last;
        long target;
        long count;
        long offset;
        long delta;
        LuaValue moved = LuaValue.Nil;

        if (continuationId == 0)
        {
            source = LuaLibraryHelpers.Required(values, 0, "move");
            CheckTableAccess(context.State, source, 0, "move", "__index");
            first = LuaLibraryHelpers.CheckInteger(values, 1, "move");
            last = LuaLibraryHelpers.CheckInteger(values, 2, "move");
            target = LuaLibraryHelpers.CheckInteger(values, 3, "move");
            destination = values.Length >= 5 && !values[4].IsNil ? values[4] : source;
            CheckTableAccess(context.State, destination, values.Length >= 5 ? 4 : 0, "move", "__newindex");
            if (last < first)
            {
                return LuaNativeStep.Completed(destination);
            }

            if (first <= 0 && last >= unchecked(long.MaxValue + first))
            {
                throw LuaLibraryHelpers.BadArgument("move", 2, "too many elements to move");
            }

            count = unchecked(last - first + 1);
            if (target > unchecked(long.MaxValue - count + 1))
            {
                throw LuaLibraryHelpers.BadArgument("move", 3, "destination wrap around");
            }

            if (target > last || target <= first)
            {
                offset = 0;
                delta = 1;
            }
            else if (source == destination)
            {
                offset = count - 1;
                delta = -1;
            }
            else
            {
                var equality = LuaRuntimeOperations.Binary(
                    context.State,
                    LuaIrBinaryOperator.Equal,
                    source,
                    destination);
                if (equality.RequiresCall)
                {
                    return LuaNativeStep.CallLua(
                        equality.Callable,
                        equality.Arguments,
                        3,
                        [source, destination, LuaValue.FromInteger(first), LuaValue.FromInteger(last),
                            LuaValue.FromInteger(target), LuaValue.FromInteger(count)],
                        false);
                }

                var same = equality.Value.IsTruthy;
                offset = same ? count - 1 : 0;
                delta = same ? -1 : 1;
            }
        }
        else if (continuationId == 3)
        {
            var state = context.InvocationState;
            source = state[0];
            destination = state[1];
            first = state[2].AsInteger();
            last = state[3].AsInteger();
            target = state[4].AsInteger();
            count = state[5].AsInteger();
            var same = CallbackValue(values).IsTruthy;
            offset = same ? count - 1 : 0;
            delta = same ? -1 : 1;
        }
        else
        {
            var state = context.InvocationState;
            source = state[0];
            destination = state[1];
            first = state[2].AsInteger();
            target = state[3].AsInteger();
            count = state[4].AsInteger();
            offset = state[5].AsInteger();
            delta = state[6].AsInteger();
            last = unchecked(first + count - 1);
            if (continuationId == 1)
            {
                moved = CallbackValue(values);
            }
            else
            {
                offset += delta;
            }
        }

        while (offset >= 0 && offset < count)
        {
            if (continuationId != 1)
            {
                var get = LuaRuntimeOperations.GetIndex(
                    context.State,
                    source,
                    LuaValue.FromInteger(unchecked(first + offset)));
                if (get.RequiresCall)
                {
                    return LuaNativeStep.CallLua(
                        get.Callable,
                        get.Arguments,
                        1,
                        MoveState(source, destination, first, target, count, offset, delta),
                        false);
                }

                moved = get.Value;
            }

            var set = LuaRuntimeOperations.SetIndex(
                context.State,
                destination,
                LuaValue.FromInteger(unchecked(target + offset)),
                moved);
            if (set.RequiresCall)
            {
                return LuaNativeStep.CallLua(
                    set.Callable,
                    set.Arguments,
                    2,
                    MoveState(source, destination, first, target, count, offset, delta),
                    false);
            }

            continuationId = 2;
            offset += delta;
        }

        return LuaNativeStep.Completed(destination);
    }

    private static void CheckTableAccess(
        LuaState state,
        LuaValue value,
        int argumentIndex,
        string function,
        string metamethod)
    {
        if (value.Kind == LuaValueKind.Table ||
            !LuaBasicLibrary.GetMetafield(state, value, metamethod).IsNil)
        {
            return;
        }

        throw LuaLibraryHelpers.BadArgument(
            function,
            argumentIndex,
            $"table expected, got {LuaLibraryHelpers.TypeName(value)}");
    }

    internal static LuaNativeStep Unpack(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values)
    {
        LuaValue target;
        long index;
        long last;
        LuaValue[] state;
        var resultCount = 0;
        if (continuationId == 0)
        {
            target = LuaLibraryHelpers.Required(values, 0, "unpack");
            index = LuaLibraryHelpers.OptionalInteger(values, 1, 1, "unpack");
            if (values.Length >= 3 && !values[2].IsNil)
            {
                last = LuaLibraryHelpers.CheckInteger(values, 2, "unpack");
            }
            else
            {
                return ResolveLength(
                    context,
                    target,
                    1,
                    [target, LuaValue.FromInteger(index)],
                    LengthContinuation.Unpack);
            }

            state = CreateUnpackState(target, index, last);
        }
        else if (continuationId == 1)
        {
            target = context.InvocationState[0];
            index = context.InvocationState[1].AsInteger();
            last = CallbackInteger(values, "object length is not an integer");
            state = CreateUnpackState(target, index, last);
        }
        else
        {
            state = context.InvocationState as LuaValue[] ??
                throw new InvalidOperationException("table.unpack lost its reusable state.");
            target = state[0];
            index = state[1].AsInteger();
            last = state[2].AsInteger();
            resultCount = checked((int)state[3].AsInteger());
            state[4 + resultCount++] = CallbackValue(values);
            if (index == last)
            {
                return CompleteUnpack(state, resultCount);
            }

            index++;
            state[1] = LuaValue.FromInteger(index);
            state[3] = LuaValue.FromInteger(resultCount);
        }

        if (index > last)
        {
            return CompleteUnpack(state, resultCount);
        }

        while (index <= last)
        {
            var get = LuaRuntimeOperations.GetIndex(
                context.State,
                target,
                LuaValue.FromInteger(index));
            if (get.RequiresCall)
            {
                state[1] = LuaValue.FromInteger(index);
                state[3] = LuaValue.FromInteger(resultCount);
                return LuaNativeStep.CallLuaWithReusableState(
                    get.Callable,
                    get.Arguments,
                    2,
                    state,
                    false);
            }

            state[4 + resultCount++] = get.Value;
            if (index == last)
            {
                return CompleteUnpack(state, resultCount);
            }

            index++;
        }

        return CompleteUnpack(state, resultCount);
    }

    internal static LuaValue[] MaxN(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var table = LuaLibraryHelpers.Required(arguments, 0, "maxn");
        if (table.Kind != LuaValueKind.Table)
        {
            throw LuaLibraryHelpers.BadArgument("maxn", 0, "table expected");
        }

        var target = table.AsTable();
        long maximum = target.ArrayLength;
        var key = LuaValue.Nil;
        while (target.Next(key, out var nextKey, out var ignoredValue))
        {
            if (nextKey.Kind == LuaValueKind.Integer && nextKey.AsInteger() > maximum)
            {
                maximum = nextKey.AsInteger();
            }

            key = nextKey;
        }

        return [LuaValue.FromInteger(maximum)];
    }

    internal static LuaValue[] Create(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var size = LuaLibraryHelpers.OptionalInteger(arguments, 0, 0, "create");
        var hash = LuaLibraryHelpers.OptionalInteger(arguments, 1, 0, "create");
        if (size < 0 || hash < 0 || size > int.MaxValue || hash > int.MaxValue)
        {
            throw LuaLibraryHelpers.BadArgument("create", 0, "size out of range");
        }

        return [LuaValue.FromTable(state.CreateTable((int)size, (int)hash))];
    }

    internal static LuaNativeStep Foreach(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values)
    {
        LuaTable table;
        LuaValue callback;
        LuaValue key;
        if (continuationId == 0)
        {
            var target = LuaLibraryHelpers.Required(values, 0, "table.foreach");
            if (target.Kind != LuaValueKind.Table)
            {
                throw LuaLibraryHelpers.BadArgument("table.foreach", 0, "table expected");
            }

            table = target.AsTable();
            callback = LuaLibraryHelpers.Required(values, 1, "table.foreach");
            if (callback.Kind != LuaValueKind.Function)
            {
                throw LuaLibraryHelpers.BadArgument("table.foreach", 1, "function expected");
            }

            key = LuaValue.Nil;
        }
        else
        {
            var state = context.InvocationState;
            table = state[0].AsTable();
            callback = state[1];
            key = state[2];
            if (values.Length > 0 && !values[0].IsNil)
            {
                return LuaNativeStep.Completed(values[0]);
            }
        }

        if (!table.Next(key, out var nextKey, out var nextValue))
        {
            return LuaNativeStep.Completed(LuaValue.Nil);
        }

        return LuaNativeStep.CallLua(
            callback,
            [nextKey, nextValue],
            continuationId: 1,
            stateValues: [LuaValue.FromTable(table), callback, nextKey],
            callIsYieldable: false);
    }

    internal static LuaNativeStep ForeachI(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values)
    {
        LuaTable table;
        LuaValue callback;
        long index;
        if (continuationId == 0)
        {
            var target = LuaLibraryHelpers.Required(values, 0, "table.foreachi");
            if (target.Kind != LuaValueKind.Table)
            {
                throw LuaLibraryHelpers.BadArgument("table.foreachi", 0, "table expected");
            }

            table = target.AsTable();
            callback = LuaLibraryHelpers.Required(values, 1, "table.foreachi");
            if (callback.Kind != LuaValueKind.Function)
            {
                throw LuaLibraryHelpers.BadArgument("table.foreachi", 1, "function expected");
            }

            index = 1;
        }
        else
        {
            var state = context.InvocationState;
            table = state[0].AsTable();
            callback = state[1];
            index = state[2].AsInteger() + 1;
            if (values.Length > 0 && !values[0].IsNil)
            {
                return LuaNativeStep.Completed(values[0]);
            }
        }

        if (index > table.ArrayLength)
        {
            return LuaNativeStep.Completed(LuaValue.Nil);
        }

        var value = table.Get(LuaValue.FromInteger(index));
        return LuaNativeStep.CallLua(
            callback,
            [LuaValue.FromInteger(index), value],
            continuationId: 1,
            stateValues: [LuaValue.FromTable(table), callback, LuaValue.FromInteger(index)],
            callIsYieldable: false);
    }

    internal static LuaValue[] SetN(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var table = LuaLibraryHelpers.Required(arguments, 0, "table.setn");
        if (table.Kind != LuaValueKind.Table)
        {
            throw LuaLibraryHelpers.BadArgument("table.setn", 0, "table expected");
        }

        var _ = LuaLibraryHelpers.OptionalInteger(arguments, 1, table.AsTable().ArrayLength, "table.setn");
        return [table];
    }

    private static LuaValue[] CreateUnpackState(LuaValue target, long first, long last)
    {
        if (first > last)
        {
            return [target, LuaValue.FromInteger(first), LuaValue.FromInteger(last), LuaValue.FromInteger(0)];
        }

        if (UnpackResultCountIsTooLarge(first, last))
        {
            throw new LuaRuntimeException("too many results to unpack");
        }

        var resultCount = GetUnpackResultCount(first, last);
        var state = new LuaValue[checked(4 + resultCount)];
        state[0] = target;
        state[1] = LuaValue.FromInteger(first);
        state[2] = LuaValue.FromInteger(last);
        state[3] = LuaValue.FromInteger(0);
        return state;
    }

    private static LuaNativeStep CompleteUnpack(LuaValue[] state, int resultCount) =>
        LuaNativeStep.Completed(state.AsSpan(4, resultCount).ToArray());

    private static bool UnpackResultCountIsTooLarge(long first, long last)
    {
        var maximumResultCount = (ulong)Array.MaxLength - 4;
        if (first < 0 && last >= 0)
        {
            var negativeCount = (ulong)(-(first + 1)) + 1;
            var nonNegativeCount = (ulong)last + 1;
            return negativeCount > maximumResultCount ||
                nonNegativeCount > maximumResultCount ||
                negativeCount + nonNegativeCount > maximumResultCount;
        }

        return (ulong)(last - first) + 1 > maximumResultCount;
    }

    private static int GetUnpackResultCount(long first, long last)
    {
        if (first < 0 && last >= 0)
        {
            var negativeCount = (ulong)(-(first + 1)) + 1;
            var nonNegativeCount = (ulong)last + 1;
            return checked((int)(negativeCount + nonNegativeCount));
        }

        return checked((int)((ulong)(last - first) + 1));
    }

    private static LuaNativeStep Sort(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values)
    {
        if (continuationId == 0)
        {
            var target = LuaLibraryHelpers.Required(values, 0, "sort");
            if (target.Kind != LuaValueKind.Table &&
                (LuaBasicLibrary.GetMetafield(context.State, target, "__index").IsNil ||
                 LuaBasicLibrary.GetMetafield(context.State, target, "__newindex").IsNil ||
                 LuaBasicLibrary.GetMetafield(context.State, target, "__len").IsNil))
            {
                throw LuaLibraryHelpers.BadArgument(
                    "sort",
                    0,
                    $"table expected, got {LuaLibraryHelpers.TypeName(target)}");
            }

            var comparator = values.Length >= 2 ? values[1] : LuaValue.Nil;
            return ResolveLength(
                context,
                target,
                1,
                [target, comparator],
                LengthContinuation.Sort);
        }

        if (continuationId == 1)
        {
            var length = CallbackInteger(values, "object length is not an integer");
            if (length <= 1)
            {
                return LuaNativeStep.Completed();
            }

            if (length >= int.MaxValue)
            {
                throw LuaLibraryHelpers.BadArgument("sort", 0, "array too big");
            }

            var comparator = context.InvocationState[1];
            if (!comparator.IsNil && comparator.Kind != LuaValueKind.Function)
            {
                throw LuaLibraryHelpers.BadArgument(
                    "sort",
                    1,
                    $"function expected, got {LuaLibraryHelpers.TypeName(comparator)}");
            }

            var machine = SortMachine.Create(
                context.InvocationState[0],
                comparator,
                length);
            return RunSortMachine(context, machine);
        }

        var resumed = SortMachine.Decode(context.InvocationState);
        resumed.AcceptCallback(CallbackValue(values));
        return RunSortMachine(context, resumed);
    }

    private static LuaNativeStep RunSortMachine(
        LuaNativeCallContext context,
        SortMachine machine)
    {
        LuaNativeStep? pending;
        while (true)
        {
            switch (machine.ProgramCounter)
            {
                case SortProgramCounter.AuxStart:
                    if (machine.Lo >= machine.Up)
                    {
                        machine.ProgramCounter = SortProgramCounter.ReturnFromAux;
                        break;
                    }

                    machine.ProgramCounter = SortProgramCounter.GetLo;
                    break;
                case SortProgramCounter.GetLo:
                    pending = SortGet(context, machine, machine.Lo, SortProgramCounter.AfterGetLo);

                    if (pending.HasValue)

                    {

                        return pending.Value;

                    }

                    break;
                case SortProgramCounter.AfterGetLo:
                    machine.A = machine.CallbackValue;
                    machine.ProgramCounter = SortProgramCounter.GetUp;
                    break;
                case SortProgramCounter.GetUp:
                    pending = SortGet(context, machine, machine.Up, SortProgramCounter.AfterGetUp);

                    if (pending.HasValue)

                    {

                        return pending.Value;

                    }

                    break;
                case SortProgramCounter.AfterGetUp:
                    machine.B = machine.CallbackValue;
                    machine.ProgramCounter = SortProgramCounter.CompareUpToLo;
                    break;
                case SortProgramCounter.CompareUpToLo:
                    pending = SortCompare(
                        context,
                        machine,
                        machine.B,
                        machine.A,
                        SortProgramCounter.AfterCompareUpToLo);

                    if (pending.HasValue)

                    {

                        return pending.Value;

                    }

                    break;
                case SortProgramCounter.AfterCompareUpToLo:
                    machine.ProgramCounter = machine.CallbackValue.IsTruthy
                        ? SortProgramCounter.SetLoFromUp
                        : SortProgramCounter.EndpointsOrdered;
                    break;
                case SortProgramCounter.SetLoFromUp:
                    pending = SortSet(
                        context,
                        machine,
                        machine.Lo,
                        machine.B,
                        SortProgramCounter.SetUpFromLo);

                    if (pending.HasValue)

                    {

                        return pending.Value;

                    }

                    break;
                case SortProgramCounter.SetUpFromLo:
                    pending = SortSet(
                        context,
                        machine,
                        machine.Up,
                        machine.A,
                        SortProgramCounter.EndpointsOrdered);

                    if (pending.HasValue)

                    {

                        return pending.Value;

                    }

                    break;
                case SortProgramCounter.EndpointsOrdered:
                    if (machine.Up - machine.Lo == 1)
                    {
                        machine.ProgramCounter = SortProgramCounter.ReturnFromAux;
                        break;
                    }

                    machine.P = ChooseSortPivot(machine.Lo, machine.Up, machine.RandomSeed);
                    machine.ProgramCounter = SortProgramCounter.GetPivotForMedian;
                    break;
                case SortProgramCounter.GetPivotForMedian:
                    pending = SortGet(
                        context,
                        machine,
                        machine.P,
                        SortProgramCounter.AfterGetPivotForMedian);

                    if (pending.HasValue)

                    {

                        return pending.Value;

                    }

                    break;
                case SortProgramCounter.AfterGetPivotForMedian:
                    machine.A = machine.CallbackValue;
                    machine.ProgramCounter = SortProgramCounter.GetLoForMedian;
                    break;
                case SortProgramCounter.GetLoForMedian:
                    pending = SortGet(
                        context,
                        machine,
                        machine.Lo,
                        SortProgramCounter.AfterGetLoForMedian);

                    if (pending.HasValue)

                    {

                        return pending.Value;

                    }

                    break;
                case SortProgramCounter.AfterGetLoForMedian:
                    machine.B = machine.CallbackValue;
                    machine.ProgramCounter = SortProgramCounter.ComparePivotToLo;
                    break;
                case SortProgramCounter.ComparePivotToLo:
                    pending = SortCompare(
                        context,
                        machine,
                        machine.A,
                        machine.B,
                        SortProgramCounter.AfterComparePivotToLo);

                    if (pending.HasValue)

                    {

                        return pending.Value;

                    }

                    break;
                case SortProgramCounter.AfterComparePivotToLo:
                    machine.ProgramCounter = machine.CallbackValue.IsTruthy
                        ? SortProgramCounter.SetPivotFromLo
                        : SortProgramCounter.GetUpForMedian;
                    break;
                case SortProgramCounter.SetPivotFromLo:
                    pending = SortSet(
                        context,
                        machine,
                        machine.P,
                        machine.B,
                        SortProgramCounter.SetLoFromPivot);

                    if (pending.HasValue)

                    {

                        return pending.Value;

                    }

                    break;
                case SortProgramCounter.SetLoFromPivot:
                    pending = SortSet(
                        context,
                        machine,
                        machine.Lo,
                        machine.A,
                        SortProgramCounter.MedianOrdered);

                    if (pending.HasValue)

                    {

                        return pending.Value;

                    }

                    break;
                case SortProgramCounter.GetUpForMedian:
                    pending = SortGet(
                        context,
                        machine,
                        machine.Up,
                        SortProgramCounter.AfterGetUpForMedian);

                    if (pending.HasValue)

                    {

                        return pending.Value;

                    }

                    break;
                case SortProgramCounter.AfterGetUpForMedian:
                    machine.B = machine.CallbackValue;
                    machine.ProgramCounter = SortProgramCounter.CompareUpToPivot;
                    break;
                case SortProgramCounter.CompareUpToPivot:
                    pending = SortCompare(
                        context,
                        machine,
                        machine.B,
                        machine.A,
                        SortProgramCounter.AfterCompareUpToPivot);

                    if (pending.HasValue)

                    {

                        return pending.Value;

                    }

                    break;
                case SortProgramCounter.AfterCompareUpToPivot:
                    machine.ProgramCounter = machine.CallbackValue.IsTruthy
                        ? SortProgramCounter.SetPivotFromUp
                        : SortProgramCounter.MedianOrdered;
                    break;
                case SortProgramCounter.SetPivotFromUp:
                    pending = SortSet(
                        context,
                        machine,
                        machine.P,
                        machine.B,
                        SortProgramCounter.SetUpFromPivot);

                    if (pending.HasValue)

                    {

                        return pending.Value;

                    }

                    break;
                case SortProgramCounter.SetUpFromPivot:
                    pending = SortSet(
                        context,
                        machine,
                        machine.Up,
                        machine.A,
                        SortProgramCounter.MedianOrdered);

                    if (pending.HasValue)

                    {

                        return pending.Value;

                    }

                    break;
                case SortProgramCounter.MedianOrdered:
                    if (machine.Up - machine.Lo == 2)
                    {
                        machine.ProgramCounter = SortProgramCounter.ReturnFromAux;
                        break;
                    }

                    machine.ProgramCounter = SortProgramCounter.GetPartitionPivot;
                    break;
                case SortProgramCounter.GetPartitionPivot:
                    pending = SortGet(
                        context,
                        machine,
                        machine.P,
                        SortProgramCounter.AfterGetPartitionPivot);

                    if (pending.HasValue)

                    {

                        return pending.Value;

                    }

                    break;
                case SortProgramCounter.AfterGetPartitionPivot:
                    machine.Pivot = machine.CallbackValue;
                    machine.ProgramCounter = SortProgramCounter.GetBeforeUp;
                    break;
                case SortProgramCounter.GetBeforeUp:
                    pending = SortGet(
                        context,
                        machine,
                        machine.Up - 1,
                        SortProgramCounter.AfterGetBeforeUp);

                    if (pending.HasValue)

                    {

                        return pending.Value;

                    }

                    break;
                case SortProgramCounter.AfterGetBeforeUp:
                    machine.A = machine.CallbackValue;
                    machine.ProgramCounter = SortProgramCounter.SetPivotFromBeforeUp;
                    break;
                case SortProgramCounter.SetPivotFromBeforeUp:
                    pending = SortSet(
                        context,
                        machine,
                        machine.P,
                        machine.A,
                        SortProgramCounter.SetBeforeUpFromPivot);

                    if (pending.HasValue)

                    {

                        return pending.Value;

                    }

                    break;
                case SortProgramCounter.SetBeforeUpFromPivot:
                    pending = SortSet(
                        context,
                        machine,
                        machine.Up - 1,
                        machine.Pivot,
                        SortProgramCounter.PartitionStart);

                    if (pending.HasValue)

                    {

                        return pending.Value;

                    }

                    break;
                case SortProgramCounter.PartitionStart:
                    machine.I = machine.Lo;
                    machine.J = machine.Up - 1;
                    machine.ProgramCounter = SortProgramCounter.IncrementI;
                    break;
                case SortProgramCounter.IncrementI:
                    machine.I++;
                    machine.ProgramCounter = SortProgramCounter.GetI;
                    break;
                case SortProgramCounter.GetI:
                    pending = SortGet(
                        context,
                        machine,
                        machine.I,
                        SortProgramCounter.AfterGetI);

                    if (pending.HasValue)

                    {

                        return pending.Value;

                    }

                    break;
                case SortProgramCounter.AfterGetI:
                    machine.ItemI = machine.CallbackValue;
                    machine.ProgramCounter = SortProgramCounter.CompareIToPivot;
                    break;
                case SortProgramCounter.CompareIToPivot:
                    pending = SortCompare(
                        context,
                        machine,
                        machine.ItemI,
                        machine.Pivot,
                        SortProgramCounter.AfterCompareIToPivot);

                    if (pending.HasValue)

                    {

                        return pending.Value;

                    }

                    break;
                case SortProgramCounter.AfterCompareIToPivot:
                    if (machine.CallbackValue.IsTruthy)
                    {
                        if (machine.I == machine.Up - 1)
                        {
                            throw new LuaRuntimeException("invalid order function for sorting");
                        }

                        machine.ProgramCounter = SortProgramCounter.IncrementI;
                    }
                    else
                    {
                        machine.ProgramCounter = SortProgramCounter.DecrementJ;
                    }

                    break;
                case SortProgramCounter.DecrementJ:
                    machine.J--;
                    machine.ProgramCounter = SortProgramCounter.GetJ;
                    break;
                case SortProgramCounter.GetJ:
                    pending = SortGet(
                        context,
                        machine,
                        machine.J,
                        SortProgramCounter.AfterGetJ);

                    if (pending.HasValue)

                    {

                        return pending.Value;

                    }

                    break;
                case SortProgramCounter.AfterGetJ:
                    machine.ItemJ = machine.CallbackValue;
                    machine.ProgramCounter = SortProgramCounter.ComparePivotToJ;
                    break;
                case SortProgramCounter.ComparePivotToJ:
                    pending = SortCompare(
                        context,
                        machine,
                        machine.Pivot,
                        machine.ItemJ,
                        SortProgramCounter.AfterComparePivotToJ);

                    if (pending.HasValue)

                    {

                        return pending.Value;

                    }

                    break;
                case SortProgramCounter.AfterComparePivotToJ:
                    if (machine.CallbackValue.IsTruthy)
                    {
                        if (machine.J < machine.I)
                        {
                            throw new LuaRuntimeException("invalid order function for sorting");
                        }

                        machine.ProgramCounter = SortProgramCounter.DecrementJ;
                    }
                    else
                    {
                        machine.ProgramCounter = machine.J < machine.I
                            ? SortProgramCounter.SetBeforeUpFromI
                            : SortProgramCounter.SetIFromJ;
                    }

                    break;
                case SortProgramCounter.SetIFromJ:
                    pending = SortSet(
                        context,
                        machine,
                        machine.I,
                        machine.ItemJ,
                        SortProgramCounter.SetJFromI);

                    if (pending.HasValue)

                    {

                        return pending.Value;

                    }

                    break;
                case SortProgramCounter.SetJFromI:
                    pending = SortSet(
                        context,
                        machine,
                        machine.J,
                        machine.ItemI,
                        SortProgramCounter.IncrementI);

                    if (pending.HasValue)

                    {

                        return pending.Value;

                    }

                    break;
                case SortProgramCounter.SetBeforeUpFromI:
                    pending = SortSet(
                        context,
                        machine,
                        machine.Up - 1,
                        machine.ItemI,
                        SortProgramCounter.SetIFromPivot);

                    if (pending.HasValue)

                    {

                        return pending.Value;

                    }

                    break;
                case SortProgramCounter.SetIFromPivot:
                    pending = SortSet(
                        context,
                        machine,
                        machine.I,
                        machine.Pivot,
                        SortProgramCounter.PartitionComplete);

                    if (pending.HasValue)

                    {

                        return pending.Value;

                    }

                    break;
                case SortProgramCounter.PartitionComplete:
                    machine.P = machine.I;
                    if (machine.P - machine.Lo < machine.Up - machine.P)
                    {
                        machine.PushReturn(
                            machine.P + 1,
                            machine.Up,
                            machine.RandomSeed,
                            machine.P - machine.Lo);
                        machine.Up = machine.P - 1;
                    }
                    else
                    {
                        machine.PushReturn(
                            machine.Lo,
                            machine.P - 1,
                            machine.RandomSeed,
                            machine.Up - machine.P);
                        machine.Lo = machine.P + 1;
                    }

                    machine.ProgramCounter = SortProgramCounter.AuxStart;
                    break;
                case SortProgramCounter.ReturnFromAux:
                    if (!machine.TryPopReturn(out var frame))
                    {
                        return LuaNativeStep.Completed();
                    }

                    machine.Lo = frame.Lo;
                    machine.Up = frame.Up;
                    machine.RandomSeed = frame.RandomSeed;
                    if ((machine.Up - machine.Lo) / 128 > frame.SmallerSize)
                    {
                        machine.RandomSeed = RandomizeSortPivot();
                    }

                    machine.ProgramCounter = SortProgramCounter.AuxStart;
                    break;
                default:
                    throw new InvalidOperationException();
            }
        }
    }

    private static LuaNativeStep? SortGet(
        LuaNativeCallContext context,
        SortMachine machine,
        long index,
        SortProgramCounter continuation)
    {
        var get = LuaRuntimeOperations.GetIndex(
            context.State,
            machine.Table,
            LuaValue.FromInteger(index));
        machine.ProgramCounter = continuation;
        if (get.RequiresCall)
        {
            return LuaNativeStep.CallLuaWithReusableState(
                get.Callable,
                get.Arguments,
                2,
                machine.Encode(),
                callIsYieldable: false);
        }

        machine.CallbackValue = get.Value;
        return null;
    }

    private static LuaNativeStep? SortSet(
        LuaNativeCallContext context,
        SortMachine machine,
        long index,
        LuaValue value,
        SortProgramCounter continuation)
    {
        var set = LuaRuntimeOperations.SetIndex(
            context.State,
            machine.Table,
            LuaValue.FromInteger(index),
            value);
        machine.ProgramCounter = continuation;
        if (set.RequiresCall)
        {
            return LuaNativeStep.CallLuaWithReusableState(
                set.Callable,
                set.Arguments,
                2,
                machine.Encode(),
                callIsYieldable: false);
        }

        return null;
    }

    private static LuaNativeStep? SortCompare(
        LuaNativeCallContext context,
        SortMachine machine,
        LuaValue left,
        LuaValue right,
        SortProgramCounter continuation)
    {
        machine.ProgramCounter = continuation;
        if (!machine.Comparator.IsNil)
        {
            return LuaNativeStep.CallLuaWithReusableState(
                machine.Comparator,
                [left, right],
                2,
                machine.Encode(),
                callIsYieldable: false);
        }

        var comparison = LuaRuntimeOperations.Binary(
            context.State,
            LuaIrBinaryOperator.LessThan,
            left,
            right);
        if (comparison.RequiresCall)
        {
            return LuaNativeStep.CallLuaWithReusableState(
                comparison.Callable,
                comparison.Arguments,
                2,
                machine.Encode(),
                callIsYieldable: false);
        }

        machine.CallbackValue = comparison.Value;
        return null;
    }

    private static long ChooseSortPivot(long lo, long up, uint randomSeed)
    {
        if (up - lo < 100 || randomSeed == 0)
        {
            return (lo + up) / 2;
        }

        var quarter = (up - lo) / 4;
        return (randomSeed % (uint)(quarter * 2)) + lo + quarter;
    }

    private static uint RandomizeSortPivot()
    {
        var timestamp = unchecked((ulong)System.Diagnostics.Stopwatch.GetTimestamp());
        var ticks = unchecked((ulong)DateTime.UtcNow.Ticks);
        return unchecked((uint)(timestamp + (timestamp >> 32) + ticks + (ticks >> 32)));
    }

    private static LuaValue[] MoveState(
        LuaValue source,
        LuaValue destination,
        long first,
        long target,
        long count,
        long offset,
        long delta) =>
        [source, destination, LuaValue.FromInteger(first), LuaValue.FromInteger(target), LuaValue.FromInteger(count), LuaValue.FromInteger(offset), LuaValue.FromInteger(delta)];

    private static LuaNativeStep ResolveLength(
        LuaNativeCallContext context,
        LuaValue target,
        int continuationId,
        LuaValue[] state,
        LengthContinuation targetContinuation)
    {
        var resolution = LuaRuntimeOperations.Unary(
            context.State,
            LuaIrUnaryOperator.Length,
            target);
        return resolution.RequiresCall
            ? LuaNativeStep.CallLua(
                resolution.Callable,
                resolution.Arguments,
                continuationId,
                state,
                callIsYieldable: false)
            : ResumeWithImmediate(
                context,
                continuationId,
                resolution.Value,
                state,
                targetContinuation);
    }

    private static LuaNativeStep ResumeWithImmediate(
        LuaNativeCallContext context,
        int continuationId,
        LuaValue value,
        LuaValue[] invocationState,
        LengthContinuation targetContinuation)
    {
        var resumedContext = context.WithInvocationState(invocationState);
        return targetContinuation switch
        {
            LengthContinuation.Concat => Concat(resumedContext, continuationId, [value]),
            LengthContinuation.Insert => Insert(resumedContext, continuationId, [value]),
            LengthContinuation.Remove => Remove(resumedContext, continuationId, [value]),
            LengthContinuation.Sort => Sort(resumedContext, continuationId, [value]),
            LengthContinuation.Unpack => Unpack(resumedContext, continuationId, [value]),
            _ => throw new InvalidOperationException(),
        };
    }

    private static long CallbackInteger(ReadOnlySpan<LuaValue> values, string detail)
    {
        if (values.Length == 0 || !values[0].TryGetInteger(out var integer))
        {
            throw new LuaRuntimeException(detail);
        }

        return integer;
    }

    private static LuaValue CallbackValue(ReadOnlySpan<LuaValue> values) =>
        values.Length == 0 ? LuaValue.Nil : values[0];

    private static bool TryConcatRawTable(
        LuaNativeCallContext context,
        LuaValue target,
        LuaNativeByteBuffer output,
        ReadOnlySpan<byte> separator,
        long first,
        long last,
        out LuaValue result)
    {
        if (target.Kind != LuaValueKind.Table || target.AsTable().Metatable is not null)
        {
            result = LuaValue.Nil;
            return false;
        }

        var table = target.AsTable();
        const int maximumCapacityHint = 64 * 1024;
        if (first <= last)
        {
            var estimatedBytesPerItem = Math.Min(
                maximumCapacityHint,
                separator.Length + 8L);
            var maximumEstimatedItems = maximumCapacityHint / estimatedBytesPerItem;
            var itemDistance = unchecked((ulong)last - (ulong)first);
            var itemCount = itemDistance >= (ulong)maximumEstimatedItems
                ? maximumEstimatedItems
                : checked((long)itemDistance + 1);
            output.ReserveCapacityHint(checked((int)(itemCount * estimatedBytesPerItem)));
        }

        for (var index = first; index <= last; index++)
        {
            var item = table.TryGetArrayValue(index, out var arrayValue)
                ? arrayValue
                : table.Get(LuaValue.FromInteger(index));
            AppendConcatValue(
                output,
                item,
                separator,
                index,
                last);
            if (index == long.MaxValue)
            {
                break;
            }
        }

        result = LuaValue.FromString(output.MoveToString(context.State.Strings));
        return true;
    }

    private static void AppendConcatValue(
        LuaNativeByteBuffer output,
        LuaValue value,
        ReadOnlySpan<byte> separator,
        long index,
        long last)
    {
        var appendSeparator = index < last;
        switch (value.Kind)
        {
            case LuaValueKind.String:
                if (appendSeparator)
                {
                    output.AppendPair(value.AsString().AsSpan(), separator);
                }
                else
                {
                    output.Append(value.AsString().AsSpan());
                }

                break;
            case LuaValueKind.Integer:
            case LuaValueKind.Float:
                Span<byte> number = stackalloc byte[
                    LuaValueOperations.MaximumFormattedNumberByteCount];
                var length = LuaValueOperations.FormatNumberUtf8(value, number);
                if (appendSeparator)
                {
                    output.AppendPair(number[..length], separator);
                }
                else
                {
                    output.Append(number[..length]);
                }

                break;
            default:
                throw new LuaRuntimeException(
                    $"invalid value ({LuaLibraryHelpers.TypeName(value)}) at index {index} in table for 'concat'");
        }
    }

    private enum LengthContinuation : byte
    {
        Concat,
        Insert,
        Remove,
        Sort,
        Unpack,
    }

    private enum SortProgramCounter : byte
    {
        AuxStart,
        GetLo,
        AfterGetLo,
        GetUp,
        AfterGetUp,
        CompareUpToLo,
        AfterCompareUpToLo,
        SetLoFromUp,
        SetUpFromLo,
        EndpointsOrdered,
        GetPivotForMedian,
        AfterGetPivotForMedian,
        GetLoForMedian,
        AfterGetLoForMedian,
        ComparePivotToLo,
        AfterComparePivotToLo,
        SetPivotFromLo,
        SetLoFromPivot,
        GetUpForMedian,
        AfterGetUpForMedian,
        CompareUpToPivot,
        AfterCompareUpToPivot,
        SetPivotFromUp,
        SetUpFromPivot,
        MedianOrdered,
        GetPartitionPivot,
        AfterGetPartitionPivot,
        GetBeforeUp,
        AfterGetBeforeUp,
        SetPivotFromBeforeUp,
        SetBeforeUpFromPivot,
        PartitionStart,
        IncrementI,
        GetI,
        AfterGetI,
        CompareIToPivot,
        AfterCompareIToPivot,
        DecrementJ,
        GetJ,
        AfterGetJ,
        ComparePivotToJ,
        AfterComparePivotToJ,
        SetIFromJ,
        SetJFromI,
        SetBeforeUpFromI,
        SetIFromPivot,
        PartitionComplete,
        ReturnFromAux,
    }

    private readonly record struct SortReturnFrame(
        long Lo,
        long Up,
        uint RandomSeed,
        long SmallerSize);

    private sealed class SortMachine
    {
        private const int FixedStateSize = 17;
        private const int ReturnFrameSize = 4;
        private readonly List<SortReturnFrame> _returnFrames = [];
        private LuaValue[]? _encodedState;

        public LuaValue Table { get; private init; }

        public LuaValue Comparator { get; private init; }

        public SortProgramCounter ProgramCounter { get; set; }

        public long Lo { get; set; }

        public long Up { get; set; }

        public uint RandomSeed { get; set; }

        public long P { get; set; }

        public long I { get; set; }

        public long J { get; set; }

        public LuaValue A { get; set; }

        public LuaValue B { get; set; }

        public LuaValue Pivot { get; set; }

        public LuaValue ItemI { get; set; }

        public LuaValue ItemJ { get; set; }

        public LuaValue CallbackValue { get; set; }

        public static SortMachine Create(LuaValue table, LuaValue comparator, long length) =>
            new()
            {
                Table = table,
                Comparator = comparator,
                ProgramCounter = SortProgramCounter.AuxStart,
                Lo = 1,
                Up = length,
                A = LuaValue.Nil,
                B = LuaValue.Nil,
                Pivot = LuaValue.Nil,
                ItemI = LuaValue.Nil,
                ItemJ = LuaValue.Nil,
                CallbackValue = LuaValue.Nil,
            };

        public static SortMachine Decode(IReadOnlyList<LuaValue> values)
        {
            if (values is not LuaValue[] state || state.Length < FixedStateSize ||
                state[0].Kind != LuaValueKind.LightUserdata ||
                state[0].AsLightUserdata().Identity is not SortMachine machine ||
                !ReferenceEquals(machine._encodedState, state))
            {
                throw new InvalidOperationException("Invalid table.sort continuation state.");
            }

            var frameCount = checked((int)state[16].AsInteger());
            if (frameCount < 0 || state.Length < FixedStateSize + (frameCount * ReturnFrameSize))
            {
                throw new InvalidOperationException("Invalid table.sort continuation stack.");
            }

            return machine;
        }

        public LuaValue[] Encode()
        {
            var requiredLength = checked(FixedStateSize + (_returnFrames.Count * ReturnFrameSize));
            if (_encodedState is null)
            {
                _encodedState = new LuaValue[requiredLength];
                _encodedState[0] = LuaValue.FromLightUserdata(new LuaLightUserdata(this));
            }
            else if (_encodedState.Length < requiredLength)
            {
                var state = new LuaValue[requiredLength];
                Array.Copy(_encodedState, state, _encodedState.Length);
                _encodedState = state;
            }

            var values = _encodedState;
            values[1] = Table;
            values[2] = Comparator;
            values[3] = LuaValue.FromInteger((long)ProgramCounter);
            values[4] = LuaValue.FromInteger(Lo);
            values[5] = LuaValue.FromInteger(Up);
            values[6] = LuaValue.FromInteger(RandomSeed);
            values[7] = LuaValue.FromInteger(P);
            values[8] = LuaValue.FromInteger(I);
            values[9] = LuaValue.FromInteger(J);
            values[10] = A;
            values[11] = B;
            values[12] = Pivot;
            values[13] = ItemI;
            values[14] = ItemJ;
            values[15] = CallbackValue;
            values[16] = LuaValue.FromInteger(_returnFrames.Count);
            for (var i = 0; i < _returnFrames.Count; i++)
            {
                var frame = _returnFrames[i];
                var offset = FixedStateSize + (i * ReturnFrameSize);
                values[offset] = LuaValue.FromInteger(frame.Lo);
                values[offset + 1] = LuaValue.FromInteger(frame.Up);
                values[offset + 2] = LuaValue.FromInteger(frame.RandomSeed);
                values[offset + 3] = LuaValue.FromInteger(frame.SmallerSize);
            }

            return values;
        }

        public void AcceptCallback(LuaValue value) => CallbackValue = value;

        public void PushReturn(long lo, long up, uint randomSeed, long smallerSize) =>
            _returnFrames.Add(new SortReturnFrame(lo, up, randomSeed, smallerSize));

        public bool TryPopReturn(out SortReturnFrame frame)
        {
            if (_returnFrames.Count == 0)
            {
                frame = default;
                return false;
            }

            var index = _returnFrames.Count - 1;
            frame = _returnFrames[index];
            _returnFrames.RemoveAt(index);
            return true;
        }
    }

}
