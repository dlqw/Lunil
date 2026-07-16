using System.Text;
using System.Collections.Immutable;
using Lunil.Core.Text;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.StandardLibrary;

internal static class LuaDebugLibrary
{
    public static LuaTable Install(LuaState state)
    {
        var module = state.CreateTable();
        var hooks = state.CreateTable();
        var hooksMetatable = state.CreateTable();
        LuaLibraryHelpers.Set(state, hooksMetatable, "__mode", LuaLibraryHelpers.String(state, "k"));
        hooks.SetMetatable(hooksMetatable);
        LuaLibraryHelpers.Set(state, state.Registry, "_HOOKKEY", LuaValue.FromTable(hooks));
        LuaLibraryHelpers.SetFunction(state, module, "debug", DebugConsole);
        LuaLibraryHelpers.SetFunction(state, module, "gethook", GetHook);
        LuaLibraryHelpers.SetFunction(state, module, "getinfo", GetInfo);
        LuaLibraryHelpers.SetFunction(state, module, "getlocal", GetLocal);
        LuaLibraryHelpers.SetFunction(state, module, "getmetatable", GetMetatable);
        LuaLibraryHelpers.SetFunction(state, module, "getregistry", GetRegistry);
        LuaLibraryHelpers.SetFunction(state, module, "getupvalue", GetUpvalue);
        LuaLibraryHelpers.SetFunction(state, module, "getuservalue", GetUserValue);
        LuaLibraryHelpers.SetFunction(state, module, "setcstacklimit", SetCStackLimit);
        LuaLibraryHelpers.SetFunction(state, module, "sethook", SetHook);
        LuaLibraryHelpers.SetFunction(state, module, "setlocal", SetLocal);
        LuaLibraryHelpers.SetFunction(state, module, "setmetatable", SetMetatable);
        LuaLibraryHelpers.SetFunction(state, module, "setupvalue", SetUpvalue);
        LuaLibraryHelpers.SetFunction(state, module, "setuservalue", SetUserValue);
        LuaLibraryHelpers.SetFunction(state, module, "traceback", Traceback);
        LuaLibraryHelpers.SetFunction(state, module, "upvalueid", UpvalueId);
        LuaLibraryHelpers.SetFunction(state, module, "upvaluejoin", UpvalueJoin);
        LuaLibraryHelpers.Set(state, state.Globals, "debug", LuaValue.FromTable(module));
        return module;
    }

    private static LuaNativeStep DebugConsole(
        LuaNativeCallContext context,
        int continuationId,
        ReadOnlySpan<LuaValue> values)
    {
        LuaValue[] lines;
        var index = 0;
        if (continuationId == 0)
        {
            var input = LuaStandardLibraryContext.Get(context.State).Options.Console
                .ReadStandardInput();
            lines = Encoding.UTF8.GetString(input)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => LuaLibraryHelpers.String(context.State, line))
                .ToArray();
        }
        else
        {
            index = checked((int)context.InvocationState[0].AsInteger());
            lines = context.InvocationState.Skip(1).ToArray();
            if (values.Length > 0 && !values[0].IsTruthy && values.Length > 1)
            {
                var console = LuaStandardLibraryContext.Get(context.State).Options.Console;
                console.WriteError(Encoding.UTF8.GetBytes($"{values[1]}\n"));
            }
        }

        while (index < lines.Length)
        {
            var line = lines[index++].AsString().ToString();
            if (line == "cont")
            {
                break;
            }

            var source = SourceText.FromUtf8($"return pcall(function() {line} end)");
            var lowering = LuaLowerer.Lower(LuaBinder.Bind(LuaParser.Parse(source)));
            if (lowering.Module is null || !lowering.Diagnostics.IsEmpty)
            {
                var message = lowering.Diagnostics.IsEmpty
                    ? "failed to compile debug command"
                    : lowering.Diagnostics[0].Message;
                LuaStandardLibraryContext.Get(context.State).Options.Console
                    .WriteError(Encoding.UTF8.GetBytes(message + "\n"));
                continue;
            }

            var sourceName = "=stdin"u8.ToArray().ToImmutableArray();
            var module = lowering.Module with
            {
                Functions =
                [
                    .. lowering.Module.Functions.Select(function =>
                        function with { SourceName = sourceName }),
                ],
            };
            var closure = context.State.CreateMainClosure(module);
            return LuaNativeStep.CallLua(
                LuaValue.FromFunction(closure),
                [],
                continuationId: 1,
                stateValues: [LuaValue.FromInteger(index), .. lines],
                callIsYieldable: false);
        }

        return LuaNativeStep.Completed();
    }

    private static LuaValue[] GetRegistry(LuaState state, ReadOnlySpan<LuaValue> arguments) =>
        [LuaValue.FromTable(state.Registry)];

    private static LuaValue[] GetMetatable(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var value = LuaLibraryHelpers.Required(arguments, 0, "getmetatable");
        var metatable = LuaBasicLibrary.GetRawMetatable(state, value);
        return [metatable is null ? LuaValue.Nil : LuaValue.FromTable(metatable)];
    }

    private static LuaValue[] SetMetatable(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var value = LuaLibraryHelpers.Required(arguments, 0, "setmetatable");
        var metatableValue = LuaLibraryHelpers.Required(arguments, 1, "setmetatable");
        var metatable = metatableValue.IsNil
            ? null
            : metatableValue.Kind == LuaValueKind.Table
                ? metatableValue.AsTable()
                : throw LuaLibraryHelpers.BadArgument("setmetatable", 1, "nil or table expected");
        switch (value.Kind)
        {
            case LuaValueKind.Table:
                value.AsTable().SetMetatable(metatable);
                break;
            case LuaValueKind.Userdata:
                value.AsUserdata().SetMetatable(metatable);
                break;
            default:
                state.SetTypeMetatable(value.Kind, metatable);
                break;
        }

        return [value];
    }

    private static LuaValue[] GetUserValue(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var userdata = CheckUserdata(arguments, 0, "getuservalue");
        var index = LuaLibraryHelpers.OptionalInteger(arguments, 1, 1, "getuservalue");
        return index >= 1 && index <= userdata.UserValueCount
            ? [userdata.GetUserValue((int)index - 1), LuaValue.FromBoolean(true)]
            : [LuaValue.Nil, LuaValue.FromBoolean(false)];
    }

    private static LuaValue[] SetUserValue(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var userdata = CheckUserdata(arguments, 0, "setuservalue");
        var value = LuaLibraryHelpers.Required(arguments, 1, "setuservalue");
        var index = LuaLibraryHelpers.OptionalInteger(arguments, 2, 1, "setuservalue");
        if (index < 1 || index > userdata.UserValueCount)
        {
            return [LuaValue.Nil];
        }

        userdata.SetUserValue((int)index - 1, value);
        return [arguments[0]];
    }

    private static LuaValue[] GetUpvalue(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var function = CheckFunction(arguments, 0, "getupvalue");
        var index = LuaLibraryHelpers.CheckInteger(arguments, 1, "getupvalue");
        return TryGetUpvalue(function, index, out var name, out var value)
            ? [LuaLibraryHelpers.String(state, name), value]
            : [];
    }

    private static LuaValue[] SetUpvalue(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var function = CheckFunction(arguments, 0, "setupvalue");
        var index = LuaLibraryHelpers.CheckInteger(arguments, 1, "setupvalue");
        var value = LuaLibraryHelpers.Required(arguments, 2, "setupvalue");
        if (function.TryGetClosure() is { } closure && index >= 1 && index <= closure.Upvalues.Count)
        {
            closure.GetUpvalue((int)index - 1).Value = value;
            return [LuaLibraryHelpers.String(state, UpvalueName(closure, (int)index - 1))];
        }

        if (function.TryGetNativeClosure() is { } native && index >= 1 && index <= native.CaptureCount)
        {
            native.SetCapture((int)index - 1, value);
            return [LuaLibraryHelpers.String(state, string.Empty)];
        }

        return [];
    }

    private static LuaValue[] UpvalueId(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var function = CheckFunction(arguments, 0, "upvalueid");
        var index = LuaLibraryHelpers.CheckInteger(arguments, 1, "upvalueid");
        object identity;
        if (function.TryGetClosure() is { } closure && index >= 1 && index <= closure.Upvalues.Count)
        {
            identity = closure.GetUpvalue((int)index - 1);
        }
        else if (function.TryGetNativeClosure() is { } native && index >= 1 && index <= native.CaptureCount)
        {
            identity = native.GetCaptureIdentity((int)index - 1);
        }
        else
        {
            return [LuaValue.Nil];
        }

        return [LuaValue.FromLightUserdata(new LuaLightUserdata(identity))];
    }

    private static LuaValue[] UpvalueJoin(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var first = CheckFunction(arguments, 0, "upvaluejoin").TryGetClosure() ??
            throw LuaLibraryHelpers.BadArgument("upvaluejoin", 0, "Lua function expected");
        var firstIndex = LuaLibraryHelpers.CheckInteger(arguments, 1, "upvaluejoin");
        var second = CheckFunction(arguments, 2, "upvaluejoin").TryGetClosure() ??
            throw LuaLibraryHelpers.BadArgument("upvaluejoin", 2, "Lua function expected");
        var secondIndex = LuaLibraryHelpers.CheckInteger(arguments, 3, "upvaluejoin");
        if (firstIndex < 1 || firstIndex > first.Upvalues.Count)
        {
            throw LuaLibraryHelpers.BadArgument("upvaluejoin", 1, "invalid upvalue index");
        }

        if (secondIndex < 1 || secondIndex > second.Upvalues.Count)
        {
            throw LuaLibraryHelpers.BadArgument("upvaluejoin", 3, "invalid upvalue index");
        }

        first.JoinUpvalue((int)firstIndex - 1, second, (int)secondIndex - 1);
        return [];
    }

    private static LuaValue[] GetLocal(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var offset = 0;
        var thread = ExtractThread(state, arguments, ref offset, out var explicitThread);
        var target = LuaLibraryHelpers.Required(arguments, offset, "getlocal");
        var index = checked((int)LuaLibraryHelpers.CheckInteger(arguments, offset + 1, "getlocal"));
        if (target.Kind == LuaValueKind.Function)
        {
            var closure = target.TryGetClosure();
            var name = closure is null ? null : LuaDebugApi.GetLocalName(closure, index);
            return name is null ? [] : [LuaLibraryHelpers.String(state, name)];
        }

        if (!target.TryGetInteger(out var level) || level < 0)
        {
            throw LuaLibraryHelpers.BadArgument("getlocal", offset, "level out of range");
        }

        if (explicitThread && level == 0 && TryGetActiveNativeCall(thread, out _))
        {
            return [];
        }

        if (!explicitThread && level == 2 &&
            LuaDebugApi.GetHookTransfer(state, thread, index) is { } transfer)
        {
            return [LuaLibraryHelpers.String(state, transfer.Name), transfer.Value];
        }

        if (!explicitThread && level == 0)
        {
            return index > 0 && index <= arguments.Length
                ? [LuaLibraryHelpers.String(state, "(C temporary)"), arguments[index - 1]]
                : [];
        }

        var frame = ResolveFrame(state, thread, level, explicitThread);
        if (frame is null)
        {
            throw LuaLibraryHelpers.BadArgument("getlocal", offset, "level out of range");
        }

        var local = LuaDebugApi.GetLocal(thread, frame, index);
        return local is null
            ? []
            : [LuaLibraryHelpers.String(state, local.Value.Name), local.Value.Value];
    }

    private static LuaValue[] SetLocal(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var offset = 0;
        var thread = ExtractThread(state, arguments, ref offset, out var explicitThread);
        var level = LuaLibraryHelpers.CheckInteger(arguments, offset, "setlocal");
        var index = checked((int)LuaLibraryHelpers.CheckInteger(arguments, offset + 1, "setlocal"));
        var value = LuaLibraryHelpers.Required(arguments, offset + 2, "setlocal");
        if (explicitThread && level == 0 && TryGetActiveNativeCall(thread, out _))
        {
            return [];
        }

        var frame = ResolveFrame(state, thread, level, explicitThread);
        if (frame is null)
        {
            throw LuaLibraryHelpers.BadArgument("setlocal", offset, "level out of range");
        }

        var name = LuaDebugApi.SetLocal(thread, frame, index, value);
        return name is null ? [] : [LuaLibraryHelpers.String(state, name)];
    }

    private static LuaValue[] GetInfo(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var offset = 0;
        var thread = ExtractThread(state, arguments, ref offset, out var explicitThread);
        var target = LuaLibraryHelpers.Required(arguments, offset, "getinfo");
        var options = arguments.Length <= offset + 1 || arguments[offset + 1].IsNil
            ? "flnSrtu"
            : Encoding.UTF8.GetString(
                LuaLibraryHelpers.CheckStringBytes(arguments, offset + 1, "getinfo"));
        if (options.Any(static option => !"flnSrtuL".Contains(option)))
        {
            throw LuaLibraryHelpers.BadArgument("getinfo", offset + 1, "invalid option");
        }

        LuaValue function;
        LuaFrame? frame = null;
        if (target.Kind == LuaValueKind.Function)
        {
            function = target;
        }
        else if (target.TryGetInteger(out var level))
        {
            if (level < 0)
            {
                return [LuaValue.Nil];
            }

            if (!explicitThread && level == 2 &&
                LuaDebugApi.TryGetHookSubject(state, thread, out var hookSubject))
            {
                function = hookSubject;
                return [LuaValue.FromTable(CreateInfoTable(
                    state, function, frame: null, options, thread))];
            }

            if (!explicitThread && level == 0 && !state.RunningNativeFunction.IsNil)
            {
                function = state.RunningNativeFunction;
                return [LuaValue.FromTable(CreateInfoTable(
                    state, function, frame: null, options, thread))];
            }

            if (explicitThread && level == 0 &&
                TryGetActiveNativeCall(thread, out var activeNative))
            {
                return [LuaValue.FromTable(CreateInfoTable(
                    state, activeNative, frame: null, options, thread))];
            }

            frame = ResolveFrame(state, thread, level, explicitThread);
            if (frame is null)
            {
                return [LuaValue.Nil];
            }

            function = LuaDebugApi.GetFunction(frame);
        }
        else
        {
            throw LuaLibraryHelpers.BadArgument("getinfo", offset, "function or level expected");
        }

        return [LuaValue.FromTable(CreateInfoTable(state, function, frame, options, thread))];
    }

    private static LuaValue[] SetHook(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var offset = 0;
        var thread = ExtractThread(state, arguments, ref offset, out _);
        var hook = arguments.Length > offset ? arguments[offset] : LuaValue.Nil;
        if (hook.IsNil)
        {
            LuaDebugApi.SetHook(state, thread, LuaValue.Nil, LuaDebugHookMask.None, 0);
            SetRegistryHook(state, thread, LuaValue.Nil);
            return [];
        }

        if (hook.Kind != LuaValueKind.Function)
        {
            throw LuaLibraryHelpers.BadArgument("sethook", offset, "function expected");
        }

        var maskText = Encoding.UTF8.GetString(
            LuaLibraryHelpers.CheckStringBytes(arguments, offset + 1, "sethook"));
        var count = checked((int)LuaLibraryHelpers.OptionalInteger(arguments, offset + 2, 0, "sethook"));
        var mask = LuaDebugHookMask.None;
        if (maskText.Contains('c')) mask |= LuaDebugHookMask.Call;
        if (maskText.Contains('r')) mask |= LuaDebugHookMask.Return;
        if (maskText.Contains('l')) mask |= LuaDebugHookMask.Line;
        if (count > 0) mask |= LuaDebugHookMask.Count;
        LuaDebugApi.SetHook(state, thread, hook, mask, count);
        SetRegistryHook(state, thread, hook);
        return [];
    }

    private static void SetRegistryHook(LuaState state, LuaThread thread, LuaValue hook)
    {
        var key = LuaLibraryHelpers.String(state, "_HOOKKEY");
        var table = state.Registry.Get(key).AsTable();
        table.Set(LuaValue.FromThread(thread), hook);
    }

    private static LuaValue[] GetHook(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var offset = 0;
        var thread = ExtractThread(state, arguments, ref offset, out _);
        var (hook, mask, count) = LuaDebugApi.GetHook(state, thread);
        if (hook.IsNil)
        {
            return [LuaValue.Nil];
        }

        var text = string.Concat(
            mask.HasFlag(LuaDebugHookMask.Call) ? "c" : string.Empty,
            mask.HasFlag(LuaDebugHookMask.Return) ? "r" : string.Empty,
            mask.HasFlag(LuaDebugHookMask.Line) ? "l" : string.Empty);
        return [hook, LuaLibraryHelpers.String(state, text), LuaValue.FromInteger(count)];
    }

    private static LuaValue[] SetCStackLimit(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        _ = LuaLibraryHelpers.CheckInteger(arguments, 0, "setcstacklimit");
        return [LuaValue.FromInteger(200)];
    }

    private static LuaValue[] Traceback(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var offset = 0;
        var thread = ExtractThread(state, arguments, ref offset, out var explicitThread);
        var message = arguments.Length > offset ? arguments[offset] : LuaValue.Nil;
        if (!message.IsNil && message.Kind is not (LuaValueKind.String or LuaValueKind.Integer or LuaValueKind.Float))
        {
            return [message];
        }

        var level = LuaLibraryHelpers.OptionalInteger(arguments, offset + 1, explicitThread ? 0 : 1, "traceback");
        var builder = new StringBuilder();
        if (!message.IsNil)
        {
            builder.Append(Encoding.UTF8.GetString(
                LuaLibraryHelpers.CheckStringBytes([message], 0, "traceback")));
            builder.Append('\n');
        }

        builder.Append("stack traceback:");
        var remainingSkip = level;
        var renderedNativeFrames = 0;
        if (!explicitThread)
        {
            if (remainingSkip == 0)
            {
                builder.Append("\n\t[C]: in function 'debug.traceback'");
                renderedNativeFrames++;
            }
            else
            {
                remainingSkip--;
            }
        }

        var activeNative = TryGetActiveNativeCallName(thread);
        if (activeNative is not null && activeNative != "traceback")
        {
            if (remainingSkip == 0)
            {
                builder.Append("\n\t[C]: in function '").Append(activeNative).Append('\'');
                renderedNativeFrames++;
            }
            else
            {
                remainingSkip--;
            }
        }

        var frames = thread.Frames.Where(static frame => !frame.IsHidden)
            .Reverse().Skip((int)Math.Min(remainingSkip, int.MaxValue)).ToArray();
        var frameIndexes = new Dictionary<LuaFrame, int>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < thread.Frames.Count; index++)
        {
            frameIndexes.Add(thread.Frames[index], index);
        }

        var totalFrames = renderedNativeFrames + frames.Length;
        if (totalFrames > 21)
        {
            var firstLuaFrames = Math.Max(0, 10 - renderedNativeFrames);
            for (var index = 0; index < firstLuaFrames; index++)
            {
                AppendTracebackFrame(
                    builder,
                    thread,
                    frames[index],
                    frameIndexes[frames[index]]);
            }

            builder.Append("\n\t...\t(skipping ")
                .Append(totalFrames - 21)
                .Append(" levels)");
            for (var index = frames.Length - 11; index < frames.Length; index++)
            {
                AppendTracebackFrame(
                    builder,
                    thread,
                    frames[index],
                    frameIndexes[frames[index]]);
            }
        }
        else
        {
            foreach (var frame in frames)
            {
                AppendTracebackFrame(builder, thread, frame, frameIndexes[frame]);
            }
        }

        return [LuaLibraryHelpers.String(state, builder.ToString())];
    }

    private static void AppendTracebackFrame(
        StringBuilder builder,
        LuaThread thread,
        LuaFrame frame,
        int frameIndex)
    {
        var source = LuaLibraryHelpers.ShortSource(frame.Function.SourceName);
        var line = LuaDebugApi.GetCurrentLine(thread, frame);
        builder.Append("\n\t").Append(source);
        if (line >= 0)
        {
            builder.Append(':').Append(line);
        }

        if (frame.IsDebugHook)
        {
            builder.Append(": in hook '?'");
            return;
        }

        var inferred = InferFunctionName(thread, frame, frameIndex);
        if (inferred is not null)
        {
            builder.Append(inferred.Value.Kind == "metamethod"
                    ? ": in metamethod '"
                    : ": in function '")
                .Append(inferred.Value.Name)
                .Append('\'');
            if (frame.IsTailCall)
            {
                builder.Append("\n\t(...tail calls...)");
            }

            return;
        }

        builder.Append(": in function <").Append(source);
        if (frame.Function.LineDefined > 0)
        {
            builder.Append(':').Append(frame.Function.LineDefined);
        }

        builder.Append('>');
        if (frame.IsTailCall)
        {
            builder.Append("\n\t(...tail calls...)");
        }
    }

    private static string? TryGetActiveNativeCallName(LuaThread thread)
    {
        return TryGetActiveNativeCall(thread, out var function)
            ? function.TryGetNativeFunction()!.Name
            : null;
    }

    private static bool TryGetActiveNativeCall(LuaThread thread, out LuaValue function)
    {
        var frame = thread.Frames.LastOrDefault(static frame => !frame.IsHidden);
        if (frame is null || frame.ProgramCounter < 0 ||
            frame.ProgramCounter >= frame.Function.Instructions.Length)
        {
            function = LuaValue.Nil;
            return false;
        }

        var instruction = frame.Function.Instructions[frame.ProgramCounter];
        if (instruction.Opcode is not (LuaIrOpcode.Call or LuaIrOpcode.TailCall) &&
            thread.Status == LuaThreadStatus.Suspended && frame.ProgramCounter > 0)
        {
            var previous = frame.Function.Instructions[frame.ProgramCounter - 1];
            if (previous.Opcode is LuaIrOpcode.Call or LuaIrOpcode.TailCall)
            {
                instruction = previous;
            }
        }

        if (instruction.Opcode is not (LuaIrOpcode.Call or LuaIrOpcode.TailCall))
        {
            function = LuaValue.Nil;
            return false;
        }

        function = thread.Stack[frame.Base + instruction.A];
        return function.TryGetNativeFunction() is not null;
    }

    private static LuaTable CreateInfoTable(
        LuaState state,
        LuaValue function,
        LuaFrame? frame,
        string options,
        LuaThread? thread)
    {
        var table = state.CreateTable();
        var closure = function.TryGetClosure();
        var native = function.TryGetNativeFunction();
        var luaFunction = frame?.FunctionVersion.Function ?? closure?.Function;
        if (options.Contains('S'))
        {
            if (luaFunction is null)
            {
                SetString(state, table, "source", "=[C]");
                SetString(state, table, "short_src", "[C]");
                SetString(state, table, "what", "C");
                SetInteger(state, table, "linedefined", -1);
                SetInteger(state, table, "lastlinedefined", -1);
            }
            else
            {
                var source = LuaLibraryHelpers.Source(luaFunction.SourceName);
                SetString(state, table, "source", source);
                SetString(
                    state,
                    table,
                    "short_src",
                    LuaLibraryHelpers.ShortSource(luaFunction.SourceName));
                SetString(state, table, "what", luaFunction.LineDefined == 0 ? "main" : "Lua");
                SetInteger(state, table, "linedefined", luaFunction.LineDefined);
                SetInteger(state, table, "lastlinedefined", luaFunction.LastLineDefined);
            }
        }

        if (options.Contains('l'))
        {
            SetInteger(
                state,
                table,
                "currentline",
                frame is null
                    ? -1
                    : thread is null
                        ? LuaDebugApi.GetCurrentLine(frame)
                        : LuaDebugApi.GetCurrentLine(thread, frame));
        }

        if (options.Contains('u'))
        {
            var nups = closure?.Upvalues.Count ?? function.TryGetNativeClosure()?.CaptureCount ?? 0;
            SetInteger(state, table, "nups", nups);
            SetInteger(state, table, "nparams", luaFunction?.ParameterCount ?? 0);
            LuaLibraryHelpers.Set(state, table, "isvararg",
                LuaValue.FromBoolean(luaFunction?.IsVarArg ?? true));
        }

        if (options.Contains('n'))
        {
            var inferred = frame is null || thread is null ? null : InferFunctionName(thread, frame);
            LuaLibraryHelpers.Set(state, table, "name",
                inferred is not null
                    ? LuaLibraryHelpers.String(state, inferred.Value.Name)
                    : native is null ? LuaValue.Nil : LuaLibraryHelpers.String(state, native.Name));
            SetString(state, table, "namewhat",
                inferred?.Kind ?? (native is null ? string.Empty : "global"));
        }

        if (options.Contains('t'))
        {
            LuaLibraryHelpers.Set(state, table, "istailcall", LuaValue.FromBoolean(frame?.IsTailCall ?? false));
        }

        if (options.Contains('r'))
        {
            if (thread is not null &&
                LuaDebugApi.TryGetHookTransferRange(state, thread, out var start, out var count))
            {
                SetInteger(state, table, "ftransfer", start);
                SetInteger(state, table, "ntransfer", count);
            }
            else
            {
                SetInteger(state, table, "ftransfer", 0);
                SetInteger(state, table, "ntransfer", 0);
            }
        }

        if (options.Contains('f'))
        {
            LuaLibraryHelpers.Set(state, table, "func", function);
        }

        if (options.Contains('L') && closure is not null)
        {
            var lines = state.CreateTable();
            foreach (var line in LuaDebugApi.GetActiveLines(closure))
            {
                lines.Set(LuaValue.FromInteger(line), LuaValue.FromBoolean(true));
            }

            LuaLibraryHelpers.Set(state, table, "activelines", LuaValue.FromTable(lines));
        }

        return table;
    }

    private static (string Name, string Kind)? InferFunctionName(LuaThread thread, LuaFrame frame)
    {
        var frameIndex = -1;
        for (var index = 0; index < thread.Frames.Count; index++)
        {
            if (ReferenceEquals(thread.Frames[index], frame))
            {
                frameIndex = index;
                break;
            }
        }

        return InferFunctionName(thread, frame, frameIndex);
    }

    private static (string Name, string Kind)? InferFunctionName(
        LuaThread thread,
        LuaFrame frame,
        int frameIndex)
    {
        if (frame.IsDebugHook)
        {
            return ("?", "hook");
        }

        if (frame.DebugFunctionName is not null && frame.DebugFunctionNameWhat is not null)
        {
            return (frame.DebugFunctionName, frame.DebugFunctionNameWhat);
        }

        if (frameIndex <= 0)
        {
            return null;
        }

        var caller = thread.Frames[frameIndex - 1];
        var callPc = Math.Clamp(caller.ProgramCounter - 1, 0,
            caller.Function.Instructions.Length - 1);
        var call = caller.Function.Instructions[callPc];
        if (call.Opcode is not (LuaIrOpcode.Call or LuaIrOpcode.TailCall))
        {
            return null;
        }

        if (call.Opcode == LuaIrOpcode.Call &&
            ((LuaIrCallKind)call.D == LuaIrCallKind.ForIterator ||
                IsGenericForIteratorCall(caller.Function, callPc, call)))
        {
            return ("for iterator", "for iterator");
        }

        var local = LuaDebugApi.GetLocal(thread, caller, call.A + 1);
        if (local is { } named && !named.Name.StartsWith('('))
        {
            return (named.Name, "local");
        }

        for (var pc = callPc - 1; pc >= 0; pc--)
        {
            var instruction = caller.Function.Instructions[pc];
            if (instruction.A != call.A)
            {
                continue;
            }

            if (instruction.Opcode == LuaIrOpcode.GetTable)
            {
                var key = ResolveStringRegister(caller, pc, instruction.C);
                if (key is not null)
                {
                    return (key, "field");
                }
            }

            if (instruction.Opcode == LuaIrOpcode.GetUpvalue &&
                instruction.B >= 0 && instruction.B < caller.Function.Upvalues.Length)
            {
                var upvalue = caller.Function.Upvalues[instruction.B];
                if (!IsSyntheticUpvalueName(upvalue))
                {
                    return (upvalue.Name, "upvalue");
                }
            }

            if (instruction.Opcode == LuaIrOpcode.Move)
            {
                var source = LuaDebugApi.GetLocal(thread, caller, instruction.B + 1);
                if (source is { } sourceLocal && !sourceLocal.Name.StartsWith('('))
                {
                    return (sourceLocal.Name, "local");
                }
            }

            break;
        }

        return null;
    }

    private static bool IsGenericForIteratorCall(
        LuaIrFunction function,
        int callPc,
        LuaIrInstruction call)
    {
        var instructions = function.Instructions;
        if (call.B != 2 || call.C <= 0 || callPc < 3 ||
            callPc + call.C + 2 >= instructions.Length)
        {
            return false;
        }

        var firstSetup = instructions[callPc - 3];
        var secondSetup = instructions[callPc - 2];
        var thirdSetup = instructions[callPc - 1];
        if (firstSetup.Opcode != LuaIrOpcode.Move || firstSetup.A != call.A ||
            secondSetup.Opcode != LuaIrOpcode.Move || secondSetup.A != call.A + 1 ||
            secondSetup.B != firstSetup.B + 1 ||
            thirdSetup.Opcode != LuaIrOpcode.Move || thirdSetup.A != call.A + 2 ||
            thirdSetup.B != firstSetup.B + 2)
        {
            return false;
        }

        var variableBase = instructions[callPc + 1].A;
        for (var index = 0; index < call.C; index++)
        {
            var resultMove = instructions[callPc + 1 + index];
            if (resultMove.Opcode != LuaIrOpcode.Move ||
                resultMove.A != variableBase + index || resultMove.B != call.A + index)
            {
                return false;
            }
        }

        var controlMove = instructions[callPc + 1 + call.C];
        var exit = instructions[callPc + 2 + call.C];
        return controlMove.Opcode == LuaIrOpcode.Move &&
            controlMove.A == firstSetup.B + 2 && controlMove.B == variableBase &&
            exit.Opcode == LuaIrOpcode.JumpIfFalse && exit.A == variableBase;
    }

    private static string? ResolveStringRegister(
        LuaFrame frame,
        int beforeProgramCounter,
        int register,
        int depth = 0)
    {
        if (depth >= 16)
        {
            return null;
        }

        var instructions = frame.Function.Instructions;
        for (var pc = beforeProgramCounter - 1; pc >= 0; pc--)
        {
            var instruction = instructions[pc];
            if (instruction.A != register)
            {
                continue;
            }

            if (instruction.Opcode == LuaIrOpcode.LoadConstant)
            {
                var constant = frame.Function.Constants[instruction.B];
                return constant.Kind == LuaIrConstantKind.String
                    ? Encoding.UTF8.GetString(constant.Bytes.AsSpan())
                    : null;
            }

            return instruction.Opcode == LuaIrOpcode.Move
                ? ResolveStringRegister(frame, pc, instruction.B, depth + 1)
                : null;
        }

        return null;
    }

    private static LuaFrame? ResolveFrame(
        LuaState state,
        LuaThread thread,
        long level,
        bool explicitThread)
    {
        if (level < 0 || level > int.MaxValue)
        {
            return null;
        }

        var adjusted = explicitThread
            ? (int)level - (TryGetActiveNativeCall(thread, out _) ? 1 : 0)
            : (int)level - 1;
        return adjusted < 0 ? null : LuaDebugApi.GetFrame(state, thread, adjusted);
    }

    private static LuaThread ExtractThread(
        LuaState state,
        ReadOnlySpan<LuaValue> arguments,
        ref int offset,
        out bool explicitThread)
    {
        if (arguments.Length > 0 && arguments[0].Kind == LuaValueKind.Thread)
        {
            offset = 1;
            explicitThread = true;
            return arguments[0].AsThread();
        }

        explicitThread = false;
        return state.RunningThread ?? state.MainThread;
    }

    private static bool TryGetUpvalue(
        LuaValue function,
        long index,
        out string name,
        out LuaValue value)
    {
        if (function.TryGetClosure() is { } closure && index >= 1 && index <= closure.Upvalues.Count)
        {
            name = UpvalueName(closure, (int)index - 1);
            value = closure.GetUpvalue((int)index - 1).Value;
            return true;
        }

        if (function.TryGetNativeClosure() is { } native && index >= 1 && index <= native.CaptureCount)
        {
            name = string.Empty;
            value = native.GetCapture((int)index - 1);
            return true;
        }

        name = string.Empty;
        value = LuaValue.Nil;
        return false;
    }

    private static string UpvalueName(LuaClosure closure, int index)
    {
        var upvalue = closure.Function.Upvalues[index];
        if (IsSyntheticUpvalueName(upvalue))
        {
            return "(no name)";
        }

        return upvalue.DebugName.IsEmpty
            ? upvalue.Name
            : Encoding.UTF8.GetString(upvalue.DebugName.AsSpan());
    }

    private static bool IsSyntheticUpvalueName(LuaIrUpvalue upvalue) =>
        upvalue.DebugName.IsEmpty &&
        upvalue.Name.StartsWith("(upvalue ", StringComparison.Ordinal) &&
        upvalue.Name.EndsWith(')');

    private static LuaValue CheckFunction(
        ReadOnlySpan<LuaValue> arguments,
        int index,
        string function)
    {
        var value = LuaLibraryHelpers.Required(arguments, index, function);
        return value.Kind == LuaValueKind.Function
            ? value
            : throw LuaLibraryHelpers.BadArgument(
                function, index, $"function expected, got {LuaLibraryHelpers.TypeName(value)}");
    }

    private static LuaUserdata CheckUserdata(
        ReadOnlySpan<LuaValue> arguments,
        int index,
        string function)
    {
        var value = LuaLibraryHelpers.Required(arguments, index, function);
        return value.Kind == LuaValueKind.Userdata
            ? value.AsUserdata()
            : throw LuaLibraryHelpers.BadArgument(
                function,
                index,
                $"userdata expected, got " +
                (value.Kind == LuaValueKind.LightUserdata
                    ? "light userdata"
                    : LuaLibraryHelpers.TypeName(value)));
    }

    private static void SetString(LuaState state, LuaTable table, string name, string value) =>
        LuaLibraryHelpers.Set(state, table, name, LuaLibraryHelpers.String(state, value));

    private static void SetInteger(LuaState state, LuaTable table, string name, long value) =>
        LuaLibraryHelpers.Set(state, table, name, LuaValue.FromInteger(value));
}
