using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Lunil.Core.Numerics;
using Lunil.Core.Text;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Operations;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.StandardLibrary;

internal static class LuaBasicLibrary
{
    private static readonly LuaNativeFunction NextDescriptor = new("next", Next);
    private static readonly LuaNativeFunction IPairsIteratorDescriptor =
        new("ipairs iterator", IPairsIterator);

    public static LuaTable Install(LuaState state, LuaStandardLibraryOptions? options)
    {
        LuaStandardLibraryContext.Configure(state, options);
        SetFunction(state, "assert", Assert);
        SetFunction(state, "collectgarbage", CollectGarbage);
        SetStepFunction(state, "dofile", DoFile);
        SetFunction(state, "error", Error);
        SetFunction(state, "getmetatable", GetMetatable);
        SetFunction(state, "ipairs", IPairs);
        SetFunction(state, "loadfile", LoadFile);
        SetStepFunction(state, "load", Load);
        state.SetGlobal("next", LuaValue.FromFunction(NextDescriptor));
        SetStepFunction(state, "pairs", Pairs);
        state.InstallProtectedCallFunctions();
        SetStepFunction(state, "print", Print);
        SetFunction(state, "rawequal", RawEqual);
        SetFunction(state, "rawget", RawGet);
        SetFunction(state, "rawlen", RawLength);
        SetFunction(state, "rawset", RawSet);
        SetFunction(state, "select", Select);
        SetFunction(state, "setmetatable", SetMetatable);
        SetFunction(state, "tonumber", ToNumber);
        SetStepFunction(state, "tostring", ToStringStep);
        SetFunction(state, "type", Type);
        SetFunction(state, "warn", Warn);
        state.SetGlobal("_G", LuaValue.FromTable(state.Globals));
        state.SetGlobal("_VERSION", LuaLibraryHelpers.String(state, "Lua 5.4"));
        return state.Globals;
    }

    private static void SetFunction(LuaState state, string name, LuaNativeFunctionBody body) =>
        state.SetGlobal(name, LuaValue.FromFunction(new LuaNativeFunction(name, body)));

    private static void SetStepFunction(
        LuaState state,
        string name,
        LuaNativeFunctionStepBody body) =>
        state.SetGlobal(name, LuaValue.FromFunction(new LuaNativeFunction(name, body)));

    private static LuaValue[] Assert(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var condition = LuaLibraryHelpers.Required(arguments, 0, "assert");
        if (condition.IsTruthy)
        {
            return arguments.ToArray();
        }

        if (arguments.Length <= 1)
        {
            throw new LuaRuntimeException("assertion failed!");
        }

        throw new LuaRuntimeException(arguments[1]);
    }

    private static LuaValue[] Error(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var error = arguments.Length == 0 ? LuaValue.Nil : arguments[0];
        var level = LuaLibraryHelpers.OptionalInteger(arguments, 1, 1, "error");
        if (error.Kind == LuaValueKind.String && level > 0 && level <= int.MaxValue &&
            state.RunningThread is { } thread &&
            LuaDebugApi.GetFrame(state, thread, (int)level - 1) is { } frame)
        {
            var line = LuaDebugApi.GetCurrentLine(thread, frame);
            if (line > 0)
            {
                var prefix = Encoding.UTF8.GetBytes(
                    $"{LuaLibraryHelpers.ShortSource(frame.Closure.Function.SourceName)}:{line}: ");
                var message = error.AsString().AsSpan();
                var bytes = new byte[prefix.Length + message.Length];
                prefix.CopyTo(bytes, 0);
                message.CopyTo(bytes.AsSpan(prefix.Length));
                error = LuaValue.FromString(state.Strings.GetOrCreate(bytes));
            }
        }

        throw new LuaRuntimeException(error);
    }

    private static LuaValue[] GetMetatable(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var value = LuaLibraryHelpers.Required(arguments, 0, "getmetatable");
        var metatable = GetRawMetatable(state, value);
        if (metatable is null)
        {
            return [LuaValue.Nil];
        }

        var protection = GetField(state, metatable, "__metatable");
        return [protection.IsNil ? LuaValue.FromTable(metatable) : protection];
    }

    private static LuaValue[] SetMetatable(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var target = LuaLibraryHelpers.Required(arguments, 0, "setmetatable");
        if (target.Kind != LuaValueKind.Table)
        {
            throw LuaLibraryHelpers.BadArgument("setmetatable", 0, "table expected");
        }

        var metatableValue = LuaLibraryHelpers.Required(arguments, 1, "setmetatable");
        if (!metatableValue.IsNil && metatableValue.Kind != LuaValueKind.Table)
        {
            throw LuaLibraryHelpers.BadArgument("setmetatable", 1, "nil or table expected");
        }

        var table = target.AsTable();
        if (table.Metatable is { } current && !GetField(state, current, "__metatable").IsNil)
        {
            throw new LuaRuntimeException("cannot change a protected metatable");
        }

        table.SetMetatable(metatableValue.IsNil ? null : metatableValue.AsTable());
        return [target];
    }

    private static LuaValue[] RawEqual(LuaState _, ReadOnlySpan<LuaValue> arguments) =>
        [LuaValue.FromBoolean(
            LuaLibraryHelpers.Required(arguments, 0, "rawequal") ==
            LuaLibraryHelpers.Required(arguments, 1, "rawequal"))];

    private static LuaValue[] RawGet(LuaState _, ReadOnlySpan<LuaValue> arguments)
    {
        var table = CheckTable(arguments, 0, "rawget");
        var key = LuaLibraryHelpers.Required(arguments, 1, "rawget");
        return [table.Get(key)];
    }

    private static LuaValue[] RawSet(LuaState _, ReadOnlySpan<LuaValue> arguments)
    {
        var table = CheckTable(arguments, 0, "rawset");
        var key = LuaLibraryHelpers.Required(arguments, 1, "rawset");
        var value = LuaLibraryHelpers.Required(arguments, 2, "rawset");
        table.Set(key, value);
        return [arguments[0]];
    }

    private static LuaValue[] RawLength(LuaState _, ReadOnlySpan<LuaValue> arguments)
    {
        var value = LuaLibraryHelpers.Required(arguments, 0, "rawlen");
        return value.Kind switch
        {
            LuaValueKind.String => [LuaValue.FromInteger(value.AsString().AsSpan().Length)],
            LuaValueKind.Table => [LuaValue.FromInteger(value.AsTable().ArrayLength)],
            _ => throw LuaLibraryHelpers.BadArgument("rawlen", 0, "table or string expected"),
        };
    }

    private static LuaValue[] Next(LuaState _, ReadOnlySpan<LuaValue> arguments)
    {
        var table = CheckTable(arguments, 0, "next");
        var key = arguments.Length > 1 ? arguments[1] : LuaValue.Nil;
        return table.Next(key, out var nextKey, out var nextValue)
            ? [nextKey, nextValue]
            : [LuaValue.Nil];
    }

    private static LuaValue[] IPairs(LuaState _, ReadOnlySpan<LuaValue> arguments)
    {
        var value = LuaLibraryHelpers.Required(arguments, 0, "ipairs");
        return
        [
            LuaValue.FromFunction(IPairsIteratorDescriptor),
            value,
            LuaValue.FromInteger(0),
        ];
    }

    private static LuaNativeStep IPairsIterator(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values)
    {
        if (continuationId != 0)
        {
            var index = context.InvocationState[0];
            var value = values.Length == 0 ? LuaValue.Nil : values[0];
            return value.IsNil
                ? LuaNativeStep.Completed(LuaValue.Nil)
                : LuaNativeStep.Completed(index, value);
        }

        var target = LuaLibraryHelpers.Required(values, 0, "ipairs iterator");
        var current = LuaLibraryHelpers.CheckInteger(values, 1, "ipairs iterator");
        var indexValue = LuaValue.FromInteger(unchecked(current + 1));
        var resolution = LuaRuntimeOperations.GetIndex(context.State, target, indexValue);
        if (resolution.RequiresCall)
        {
            return LuaNativeStep.CallLua(
                resolution.Callable,
                resolution.Arguments,
                continuationId: 1,
                stateValues: [indexValue],
                callIsYieldable: false);
        }

        return resolution.Value.IsNil
            ? LuaNativeStep.Completed(LuaValue.Nil)
            : LuaNativeStep.Completed(indexValue, resolution.Value);
    }

    private static LuaNativeStep Pairs(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values)
    {
        if (continuationId != 0)
        {
            return LuaNativeStep.Completed(AdjustResults(values, 3));
        }

        var target = LuaLibraryHelpers.Required(values, 0, "pairs");
        var metatable = GetRawMetatable(context.State, target);
        var metamethod = metatable is null
            ? LuaValue.Nil
            : GetField(context.State, metatable, "__pairs");
        return metamethod.IsNil
            ? LuaNativeStep.Completed(
                LuaValue.FromFunction(NextDescriptor),
                target,
                LuaValue.Nil)
            : LuaNativeStep.CallLua(metamethod, [target], continuationId: 1);
    }

    private static LuaValue[] Select(LuaState _, ReadOnlySpan<LuaValue> arguments)
    {
        var selector = LuaLibraryHelpers.Required(arguments, 0, "select");
        if (selector.Kind == LuaValueKind.String &&
            selector.AsString().AsSpan() is var bytes && bytes.Length > 0 && bytes[0] == (byte)'#')
        {
            return [LuaValue.FromInteger(arguments.Length - 1)];
        }

        var index = LuaLibraryHelpers.CheckInteger(arguments, 0, "select");
        var count = arguments.Length;
        if (index < 0)
        {
            index = count + index;
        }
        else if (index > count)
        {
            index = count;
        }

        if (index < 1)
        {
            throw LuaLibraryHelpers.BadArgument("select", 0, "index out of range");
        }

        return arguments[checked((int)index)..].ToArray();
    }

    private static LuaValue[] ToNumber(LuaState _, ReadOnlySpan<LuaValue> arguments)
    {
        var value = LuaLibraryHelpers.Required(arguments, 0, "tonumber");
        if (arguments.Length < 2 || arguments[1].IsNil)
        {
            if (value.Kind is LuaValueKind.Integer or LuaValueKind.Float)
            {
                return [value];
            }

            return value.Kind == LuaValueKind.String &&
                LuaNumberParser.TryParseString(value.AsString().AsSpan(), out var number)
                    ? [FromNumber(number)]
                    : [LuaValue.Nil];
        }

        var @base = LuaLibraryHelpers.CheckInteger(arguments, 1, "tonumber");
        if (value.Kind != LuaValueKind.String)
        {
            throw LuaLibraryHelpers.BadArgument("tonumber", 0, "string expected");
        }

        if (@base is < 2 or > 36)
        {
            throw LuaLibraryHelpers.BadArgument("tonumber", 1, "base out of range");
        }

        return TryParseInteger(value.AsString().AsSpan(), (int)@base, out var integer)
            ? [LuaValue.FromInteger(integer)]
            : [LuaValue.Nil];
    }

    private static LuaValue[] Type(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var value = LuaLibraryHelpers.Required(arguments, 0, "type");
        return [LuaLibraryHelpers.String(state, LuaValueOperations.BasicTypeName(value))];
    }

    private static LuaNativeStep ToStringStep(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values)
    {
        if (continuationId != 0)
        {
            if (values.Length == 0 || values[0].Kind != LuaValueKind.String)
            {
                throw new LuaRuntimeException("'__tostring' must return a string");
            }

            return LuaNativeStep.Completed(values[0]);
        }

        var value = LuaLibraryHelpers.Required(values, 0, "tostring");
        var metamethod = GetMetafield(context.State, value, "__tostring");
        return metamethod.IsNil
            ? LuaNativeStep.Completed(DefaultToString(context.State, value))
            : LuaNativeStep.CallLua(
                metamethod,
                [value],
                continuationId: 1,
                callIsYieldable: false);
    }

    private static LuaNativeStep Print(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values)
    {
        LuaValue[] arguments;
        var index = 0;
        if (continuationId == 0)
        {
            arguments = values.ToArray();
        }
        else
        {
            index = checked((int)context.InvocationState[^1].AsInteger());
            arguments = context.InvocationState.Take(context.InvocationState.Count - 1).ToArray();
            if (values.Length == 0 || values[0].Kind != LuaValueKind.String)
            {
                throw new LuaRuntimeException("'__tostring' must return a string");
            }

            WritePrintValue(context.State, index, values[0].AsString().ToArray());
            index++;
        }

        while (index < arguments.Length)
        {
            var metamethod = GetMetafield(context.State, arguments[index], "__tostring");
            if (!metamethod.IsNil)
            {
                return LuaNativeStep.CallLua(
                    metamethod,
                    [arguments[index]],
                    continuationId: 1,
                    stateValues: [.. arguments, LuaValue.FromInteger(index)],
                    callIsYieldable: false);
            }

            WritePrintValue(
                context.State,
                index,
                DefaultToString(context.State, arguments[index]).AsString().ToArray());
            index++;
        }

        LuaStandardLibraryContext.Get(context.State).Options.Console.WriteLine();
        return LuaNativeStep.Completed();
    }

    private static void WritePrintValue(LuaState state, int index, byte[] bytes)
    {
        var console = LuaStandardLibraryContext.Get(state).Options.Console;
        if (index != 0)
        {
            console.Write("\t"u8.ToArray());
        }

        console.Write(bytes);
    }

    private static LuaValue[] Warn(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        _ = LuaLibraryHelpers.Required(arguments, 0, "warn");
        var context = LuaStandardLibraryContext.Get(state);
        if (arguments.Length == 1 && arguments[0].Kind == LuaValueKind.String)
        {
            var control = arguments[0].AsString().AsSpan();
            if (control.SequenceEqual("@on"u8))
            {
                context.WarningsEnabled = true;
                return [];
            }

            if (control.SequenceEqual("@off"u8))
            {
                context.WarningsEnabled = false;
                return [];
            }

            if (!control.IsEmpty && control[0] == (byte)'@')
            {
                return [];
            }
        }

        using var buffer = new MemoryStream();
        for (var index = 0; index < arguments.Length; index++)
        {
            var bytes = LuaLibraryHelpers.CheckStringBytes(arguments, index, "warn");
            buffer.Write(bytes);
        }

        if (context.WarningsEnabled)
        {
            state.RaiseWarning(LuaValue.FromString(state.Strings.GetOrCreate(buffer.ToArray())));
        }

        return [];
    }

    private static LuaValue[] CollectGarbage(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var option = arguments.Length == 0 || arguments[0].IsNil
            ? "collect"
            : Encoding.UTF8.GetString(LuaLibraryHelpers.CheckStringBytes(arguments, 0, "collectgarbage"));
        if (state.IsRunningFinalizer && option == "collect")
        {
            return [LuaValue.FromBoolean(false)];
        }

        return option switch
        {
            "stop" => StopGc(state),
            "restart" => RestartGc(state),
            "collect" => CollectGc(state),
            "count" => [LuaValue.FromFloat(state.Heap.LogicalBytes / 1024.0)],
            "step" => StepGc(state, arguments),
            "setpause" => [LuaValue.FromInteger(state.Heap.SetPause(
                checked((int)LuaLibraryHelpers.OptionalInteger(arguments, 1, 0, "collectgarbage"))))],
            "setstepmul" => [LuaValue.FromInteger(state.Heap.SetStepMultiplier(
                checked((int)LuaLibraryHelpers.OptionalInteger(arguments, 1, 0, "collectgarbage"))))],
            "isrunning" => [LuaValue.FromBoolean(state.Heap.IsRunning)],
            "generational" => ChangeGcMode(state, LuaGcMode.Generational),
            "incremental" => ChangeGcMode(state, LuaGcMode.Incremental),
            _ => throw LuaLibraryHelpers.BadArgument(
                "collectgarbage",
                0,
                $"invalid option '{option}'"),
        };
    }

    private static LuaValue[] StopGc(LuaState state)
    {
        state.Heap.Stop();
        return [LuaValue.FromInteger(0)];
    }

    private static LuaValue[] RestartGc(LuaState state)
    {
        state.Heap.Restart();
        return [LuaValue.FromInteger(0)];
    }

    private static LuaValue[] CollectGc(LuaState state)
    {
        state.Heap.CollectFull();
        return [LuaValue.FromInteger(0)];
    }

    private static LuaValue[] StepGc(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var size = LuaLibraryHelpers.OptionalInteger(arguments, 1, 0, "collectgarbage");
        // In generational mode Lua's zero-sized step performs a complete young
        // collection. Leaving it as a single object of incremental work makes the
        // observable result depend on unrelated allocations before the call.
        var budget = state.Heap.Mode == LuaGcMode.Generational && size == 0
            ? int.MaxValue / 4
            : size <= 0
                ? 1
                : checked((int)Math.Max(1, size * 16));
        state.Heap.Step(budget);
        return [LuaValue.FromBoolean(state.Heap.Phase == LuaGcPhase.Paused)];
    }

    private static LuaValue[] ChangeGcMode(LuaState state, LuaGcMode mode)
    {
        var previous = state.Heap.Mode;
        state.Heap.Mode = mode;
        return [LuaLibraryHelpers.String(
            state,
            previous == LuaGcMode.Incremental ? "incremental" : "generational")];
    }

    private static LuaNativeStep Load(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values)
    {
        if (continuationId == 0)
        {
            var chunk = LuaLibraryHelpers.Required(values, 0, "load");
            var explicitChunkName = values.Length > 1 && !values[1].IsNil;
            var chunkName = explicitChunkName
                ? LuaLibraryHelpers.CheckStringBytes(values, 1, "load")
                : chunk.Kind is LuaValueKind.String or LuaValueKind.Integer or LuaValueKind.Float
                    ? LuaLibraryHelpers.CheckStringBytes(values, 0, "load")
                    : "=(load)"u8.ToArray();
            if (explicitChunkName && chunkName.Length == 0)
            {
                chunkName = [0x01];
            }
            var mode = values.Length > 2 && !values[2].IsNil
                ? LuaLibraryHelpers.CheckStringBytes(values, 2, "load")
                : "bt"u8.ToArray();
            var hasEnvironment = values.Length > 3;
            var environment = hasEnvironment ? values[3] : LuaValue.Nil;
            if (chunk.Kind == LuaValueKind.Function)
            {
                return LuaNativeStep.CallLua(
                    chunk,
                    [],
                    continuationId: 1,
                    stateValues:
                    [
                        chunk,
                        LuaValue.FromString(context.State.Strings.GetOrCreate(chunkName)),
                        LuaValue.FromString(context.State.Strings.GetOrCreate(mode)),
                        LuaValue.FromBoolean(hasEnvironment),
                        environment,
                    ],
                    callIsYieldable: false,
                    callIsProtected: true);
            }

            var bytes = LuaLibraryHelpers.CheckStringBytes(values, 0, "load");
            return FinishLoad(context.State, bytes, chunkName, mode, hasEnvironment, environment);
        }

        var stateValues = context.InvocationState;
        if (values.Length == 0 || values[0].Kind != LuaValueKind.Boolean)
        {
            throw new InvalidOperationException(
                "A protected load reader callback did not report its status.");
        }

        if (!values[0].AsBoolean())
        {
            return LuaNativeStep.Completed(
                LuaValue.Nil,
                values.Length > 1
                    ? values[1]
                    : LuaLibraryHelpers.String(context.State, "load reader failed"));
        }

        var readerValues = values[1..];
        if (readerValues.Length == 0 || readerValues[0].IsNil ||
            readerValues[0].Kind == LuaValueKind.String &&
            readerValues[0].AsString().Length == 0)
        {
            var chunks = stateValues.Skip(5).SelectMany(static value => value.AsString().ToArray()).ToArray();
            return FinishLoad(
                context.State,
                chunks,
                stateValues[1].AsString().ToArray(),
                stateValues[2].AsString().ToArray(),
                stateValues[3].AsBoolean(),
                stateValues[4]);
        }

        byte[] nextChunk;
        try
        {
            nextChunk = LuaLibraryHelpers.CheckStringBytes(readerValues, 0, "load reader");
        }
        catch (LuaRuntimeException exception)
        {
            var error = exception.HasErrorValue
                ? exception.ErrorValue
                : LuaLibraryHelpers.String(context.State, exception.Message);
            return LuaNativeStep.Completed(LuaValue.Nil, error);
        }

        return LuaNativeStep.CallLua(
            stateValues[0],
            [],
            continuationId: 1,
            stateValues:
            [
                .. stateValues,
                LuaValue.FromString(context.State.Strings.GetOrCreate(nextChunk)),
            ],
            callIsYieldable: false,
            callIsProtected: true);
    }

    private static LuaValue[] LoadFile(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var path = arguments.Length == 0 || arguments[0].IsNil
            ? null
            : Encoding.UTF8.GetString(LuaLibraryHelpers.CheckStringBytes(arguments, 0, "loadfile"));
        var mode = arguments.Length > 1 && !arguments[1].IsNil
            ? LuaLibraryHelpers.CheckStringBytes(arguments, 1, "loadfile")
            : "bt"u8.ToArray();
        var hasEnvironment = arguments.Length > 2;
        var environment = hasEnvironment ? arguments[2] : LuaValue.Nil;
        try
        {
            var bytes = path is null
                ? LuaStandardLibraryContext.Get(state).Options.Console.ReadStandardInput()
                : LuaStandardLibraryContext.Get(state).Options.FileSystem.ReadAllBytes(path);
            bytes = PrepareFileChunk(bytes);
            var step = FinishLoad(
                state,
                bytes,
                Encoding.UTF8.GetBytes(path is null ? "=stdin" : "@" + path),
                mode,
                hasEnvironment,
                environment);
            return step.Values;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [LuaValue.Nil, LuaLibraryHelpers.String(state, exception.Message)];
        }
    }

    private static byte[] PrepareFileChunk(byte[] bytes)
    {
        var offset = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        var skippedComment = offset < bytes.Length && bytes[offset] == (byte)'#';
        if (skippedComment)
        {
            while (offset < bytes.Length && bytes[offset] != (byte)'\n')
            {
                offset++;
            }

            if (offset < bytes.Length)
            {
                offset++;
            }
        }

        var remaining = bytes.AsSpan(offset);
        if (remaining.StartsWith(new byte[] { 0x1b, (byte)'L', (byte)'u', (byte)'a' }))
        {
            return remaining.ToArray();
        }

        if (!skippedComment)
        {
            return offset == 0 ? bytes : remaining.ToArray();
        }

        var result = new byte[remaining.Length + 1];
        result[0] = (byte)'\n';
        remaining.CopyTo(result.AsSpan(1));
        return result;
    }

    private static LuaNativeStep DoFile(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values)
    {
        if (continuationId != 0)
        {
            return LuaNativeStep.Completed(values.ToArray());
        }

        var fileArguments = values.Length == 0 ? [] : new[] { values[0] };
        var loaded = LoadFile(context.State, fileArguments);
        if (loaded.Length != 1 || loaded[0].Kind != LuaValueKind.Function)
        {
            throw new LuaRuntimeException(loaded.Length > 1 ? loaded[1] : LuaValue.Nil);
        }

        return LuaNativeStep.CallLua(loaded[0], [], continuationId: 1);
    }

    internal static LuaNativeStep FinishLoad(
        LuaState state,
        byte[] bytes,
        byte[] chunkName,
        byte[] mode,
        bool hasEnvironment,
        LuaValue environment)
    {
        try
        {
            var binary = bytes.Length != 0 && bytes[0] == 0x1b;
            var modeText = Encoding.ASCII.GetString(mode);
            if (modeText.Any(static value => value is not ('b' or 't')) || modeText.Length == 0)
            {
                throw new LuaRuntimeException("invalid mode");
            }

            if (binary && !modeText.Contains('b') || !binary && !modeText.Contains('t'))
            {
                throw new LuaRuntimeException(
                    $"attempt to load a {(binary ? "binary" : "text")} chunk (mode is '{modeText}')");
            }

            LuaClosure closure;
            if (binary)
            {
                closure = state.LoadBinaryChunk(bytes);
            }
            else
            {
                var source = new SourceText(bytes);
                var lowering = LuaLowerer.Lower(LuaBinder.Bind(LuaParser.Parse(source)));
                if (lowering.Module is null || lowering.Diagnostics.Any(static diagnostic =>
                    diagnostic.Severity == Lunil.Core.Diagnostics.DiagnosticSeverity.Error))
                {
                    var message = lowering.Diagnostics.IsEmpty
                        ? "failed to compile chunk"
                        : FormatLoadDiagnostic(
                            source,
                            chunkName,
                            SelectLoadDiagnostic(lowering.Diagnostics));
                    return LuaNativeStep.Completed(
                        LuaValue.Nil,
                        LuaLibraryHelpers.String(state, message));
                }

                var sourceName = chunkName.ToImmutableArray();
                var module = lowering.Module with
                {
                    Functions =
                    [
                        .. lowering.Module.Functions.Select(function =>
                            function with { SourceName = sourceName }),
                    ],
                };
                closure = state.CreateMainClosure(module);
            }

            if (hasEnvironment && closure.Upvalues.Count > 0)
            {
                closure.Upvalues[0].Value = environment;
            }

            return LuaNativeStep.Completed(LuaValue.FromFunction(closure));
        }
        catch (Exception exception) when (
            exception is LuaRuntimeException or ArgumentException or InvalidOperationException or
                FormatException or OverflowException)
        {
            return LuaNativeStep.Completed(
                LuaValue.Nil,
                LuaLibraryHelpers.String(state, exception.Message));
        }
    }

    private static string FormatLoadDiagnostic(
        SourceText source,
        byte[] chunkName,
        Lunil.Core.Diagnostics.Diagnostic diagnostic)
    {
        var location = source.GetLocation(Math.Min(diagnostic.Span.Start, source.Length));
        var prefix = $"{LuaLibraryHelpers.ShortSource(chunkName)}:{location.Line + 1}: ";
        if (diagnostic.Code == "LUA2001" &&
            diagnostic.Span.Start >= source.Length &&
            source.Length > 512)
        {
            // PUC Lua's recursive production for an unfinished, very long list
            // exhausts its parser stack before it can report the missing token.
            return $"{prefix}C stack overflow";
        }

        var message = diagnostic.Code switch
        {
            "LUA1006" => "malformed number",
            "LUA2006" => "C stack overflow",
            "LUA2001" => "expected token",
            "LUA2002" => "unexpected symbol",
            "LUA2004" => "syntax error",
            _ => diagnostic.Message,
        };
        if (diagnostic.Code is "LUA1002" or "LUA1003" or "LUA1004" or "LUA1005" ||
            diagnostic.Span.Start >= source.Length)
        {
            return $"{prefix}{message} near <eof>";
        }

        var line = source.GetLineSpan(location.Line);
        var contextSpan = GetLoadDiagnosticContextSpan(source, line, diagnostic);
        const int maximumContextBytes = 96;
        var start = Math.Max(line.Start, contextSpan.Start);
        var end = Math.Min(
            line.End,
            Math.Max(contextSpan.End, contextSpan.Start + 1));
        if (end - start > maximumContextBytes)
        {
            start = end - maximumContextBytes;
        }

        var nearBytes = source.AsSpan()[start..end];
        var near = nearBytes.Length == 1 && (nearBytes[0] < 0x20 || nearBytes[0] >= 0x7f)
            ? $"<\\{nearBytes[0]}>"
            : Encoding.UTF8.GetString(nearBytes);
        return $"{prefix}{message} near '{near}'";
    }

    private static Lunil.Core.Text.TextSpan GetLoadDiagnosticContextSpan(
        SourceText source,
        Lunil.Core.Text.TextSpan line,
        Lunil.Core.Diagnostics.Diagnostic diagnostic)
    {
        var start = diagnostic.Span.Start;
        var end = diagnostic.Span.End;
        if (diagnostic.Code is "LUA1009" or "LUA1010" or "LUA1011" or
            "LUA1012" or "LUA1013" or "LUA1014")
        {
            start = FindQuotedTokenStart(source.AsSpan()[line.Start..start]) + line.Start;
            end = diagnostic.Code switch
            {
                // Lua reports the character that made an escape invalid. For an
                // otherwise complete escape that character is the closing quote.
                "LUA1010" or "LUA1011" or "LUA1012" or "LUA1013" => end + 1,
                // The UTF-8 overflow is known only after reading '}', but Lua's
                // near-token stops immediately before that delimiter.
                "LUA1014" => end - 1,
                _ => end,
            };
        }

        return Lunil.Core.Text.TextSpan.FromBounds(
            Math.Clamp(start, line.Start, line.End),
            Math.Clamp(end, Math.Clamp(start, line.Start, line.End), line.End));
    }

    private static int FindQuotedTokenStart(ReadOnlySpan<byte> prefix)
    {
        var quotedStart = -1;
        byte quote = 0;
        for (var position = 0; position < prefix.Length; position++)
        {
            var current = prefix[position];
            if (quote == 0)
            {
                if (current is (byte)'\'' or (byte)'"')
                {
                    quote = current;
                    quotedStart = position;
                }

                continue;
            }

            if (current == (byte)'\\')
            {
                position++;
            }
            else if (current == quote)
            {
                quote = 0;
                quotedStart = -1;
            }
        }

        return quotedStart >= 0 ? quotedStart : prefix.Length;
    }

    private static Lunil.Core.Diagnostics.Diagnostic SelectLoadDiagnostic(
        ImmutableArray<Lunil.Core.Diagnostics.Diagnostic> diagnostics)
    {
        var invalidStatements = diagnostics.Where(static diagnostic =>
            diagnostic.Severity == Lunil.Core.Diagnostics.DiagnosticSeverity.Error &&
            diagnostic.Code == "LUA2004").ToArray();
        if (invalidStatements.Length > 1)
        {
            // Recovery reports the leading identifier as an invalid statement and
            // then the token that made the statement impossible. Lua stops at that
            // second token (for example, "syntax error" reports near 'error').
            return invalidStatements[1];
        }

        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Severity == Lunil.Core.Diagnostics.DiagnosticSeverity.Error &&
                diagnostic.Code is not ("LUA1002" or "LUA1003" or "LUA1004" or "LUA1005"))
            {
                return diagnostic;
            }
        }

        return diagnostics[0];
    }

    private static LuaTable CheckTable(
        ReadOnlySpan<LuaValue> arguments,
        int index,
        string function)
    {
        var value = LuaLibraryHelpers.Required(arguments, index, function);
        return value.Kind == LuaValueKind.Table
            ? value.AsTable()
            : throw LuaLibraryHelpers.BadArgument(function, index, "table expected");
    }

    internal static LuaTable? GetRawMetatable(LuaState state, LuaValue value) => value.Kind switch
    {
        LuaValueKind.Table => value.AsTable().Metatable,
        LuaValueKind.Userdata => value.AsUserdata().Metatable,
        _ => state.GetTypeMetatable(value.Kind),
    };

    internal static LuaValue GetMetafield(LuaState state, LuaValue value, string name) =>
        GetRawMetatable(state, value) is { } metatable
            ? GetField(state, metatable, name)
            : LuaValue.Nil;

    private static LuaValue GetField(LuaState state, LuaTable table, string name) =>
        table.Get(LuaLibraryHelpers.String(state, name));

    internal static LuaValue DefaultToString(LuaState state, LuaValue value)
    {
        if (value.Kind == LuaValueKind.String)
        {
            return value;
        }

        var text = value.Kind switch
        {
            LuaValueKind.Nil => "nil",
            LuaValueKind.Boolean => value.AsBoolean() ? "true" : "false",
            LuaValueKind.Integer => value.AsInteger().ToString(CultureInfo.InvariantCulture),
            LuaValueKind.Float => LuaValueOperations.FormatFloat(value.AsFloat()),
            _ => FormatIdentity(state, value),
        };
        return LuaLibraryHelpers.String(state, text);
    }

    private static string FormatIdentity(LuaState state, LuaValue value)
    {
        var metatable = GetRawMetatable(state, value);
        var name = metatable is null ? LuaValue.Nil : GetField(state, metatable, "__name");
        var typeName = name.Kind == LuaValueKind.String
            ? name.AsString().ToString()
            : LuaLibraryHelpers.TypeName(value);
        var identity = value.TryGetGcObject()?.ObjectId ?? value.GetHashCode();
        return $"{typeName}: 0x{identity:x}";
    }

    private static LuaValue FromNumber(LuaNumber number) => number.Kind == LuaNumberKind.Integer
        ? LuaValue.FromInteger(number.Integer)
        : LuaValue.FromFloat(number.Float);

    private static bool TryParseInteger(ReadOnlySpan<byte> bytes, int @base, out long result)
    {
        bytes = TrimAsciiWhitespace(bytes);
        var negative = false;
        if (!bytes.IsEmpty && bytes[0] is (byte)'+' or (byte)'-')
        {
            negative = bytes[0] == (byte)'-';
            bytes = bytes[1..];
        }

        if (bytes.IsEmpty)
        {
            result = 0;
            return false;
        }

        ulong value = 0;
        foreach (var current in bytes)
        {
            var digit = current switch
            {
                >= (byte)'0' and <= (byte)'9' => current - (byte)'0',
                >= (byte)'A' and <= (byte)'Z' => current - (byte)'A' + 10,
                >= (byte)'a' and <= (byte)'z' => current - (byte)'a' + 10,
                _ => -1,
            };
            if (digit < 0 || digit >= @base)
            {
                result = 0;
                return false;
            }

            value = unchecked(value * (uint)@base + (uint)digit);
        }

        result = unchecked((long)(negative ? 0UL - value : value));
        return true;
    }

    private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> bytes)
    {
        var start = 0;
        var end = bytes.Length;
        while (start < end && IsAsciiWhitespace(bytes[start]))
        {
            start++;
        }

        while (end > start && IsAsciiWhitespace(bytes[end - 1]))
        {
            end--;
        }

        return bytes[start..end];
    }

    private static bool IsAsciiWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\f' or (byte)'\n' or (byte)'\r' or (byte)'\t' or (byte)'\v';

    private static LuaValue[] AdjustResults(ReadOnlySpan<LuaValue> values, int count)
    {
        var adjusted = new LuaValue[count];
        values[..Math.Min(values.Length, count)].CopyTo(adjusted);
        return adjusted;
    }
}
