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
            ? [userdata.GetUserValue((int)index - 1)]
            : [LuaValue.Nil];
    }

    private static LuaValue[] SetUserValue(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var userdata = CheckUserdata(arguments, 0, "setuservalue");
        var value = LuaLibraryHelpers.Required(arguments, 1, "setuservalue");
        var index = LuaLibraryHelpers.OptionalInteger(arguments, 2, 1, "setuservalue");
        if (index < 1 || index > userdata.UserValueCount)
        {
            throw LuaLibraryHelpers.BadArgument("setuservalue", 2, "invalid user value index");
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

            if (!explicitThread && level == 0 && !state.RunningNativeFunction.IsNil)
            {
                function = state.RunningNativeFunction;
                return [LuaValue.FromTable(CreateInfoTable(
                    state, function, frame: null, options, thread))];
            }

            frame = ResolveFrame(state, thread, level, explicitThread);
            if (frame is null)
            {
                return [LuaValue.Nil];
            }

            function = LuaValue.FromFunction(frame.Closure);
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
        return [];
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
        var skip = explicitThread ? level : Math.Max(0, level - 1);
        var frames = thread.Frames.Where(static frame => !frame.IsDebugHook && !frame.IsHidden)
            .Reverse().Skip((int)skip).ToArray();
        foreach (var frame in frames)
        {
            var source = ShortSource(frame.Closure.Function.SourceName);
            var line = LuaDebugApi.GetCurrentLine(frame);
            builder.Append("\n\t").Append(source);
            if (line >= 0)
            {
                builder.Append(':').Append(line);
            }

            builder.Append(": in function <").Append(source);
            if (frame.Closure.Function.LineDefined > 0)
            {
                builder.Append(':').Append(frame.Closure.Function.LineDefined);
            }

            builder.Append('>');
            if (frame.IsTailCall)
            {
                builder.Append("\n\t(...tail calls...)");
            }
        }

        return [LuaLibraryHelpers.String(state, builder.ToString())];
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
        if (options.Contains('S'))
        {
            if (closure is null)
            {
                SetString(state, table, "source", "=[C]");
                SetString(state, table, "short_src", "[C]");
                SetString(state, table, "what", "C");
                SetInteger(state, table, "linedefined", -1);
                SetInteger(state, table, "lastlinedefined", -1);
            }
            else
            {
                var source = Source(closure.Function.SourceName);
                SetString(state, table, "source", source);
                SetString(state, table, "short_src", ShortSource(closure.Function.SourceName));
                SetString(state, table, "what", closure.Function.LineDefined == 0 ? "main" : "Lua");
                SetInteger(state, table, "linedefined", closure.Function.LineDefined);
                SetInteger(state, table, "lastlinedefined", closure.Function.LastLineDefined);
            }
        }

        if (options.Contains('l'))
        {
            SetInteger(state, table, "currentline", frame is null ? -1 : LuaDebugApi.GetCurrentLine(frame));
        }

        if (options.Contains('u'))
        {
            var nups = closure?.Upvalues.Count ?? function.TryGetNativeClosure()?.CaptureCount ?? 0;
            SetInteger(state, table, "nups", nups);
            SetInteger(state, table, "nparams", closure?.Function.ParameterCount ?? 0);
            LuaLibraryHelpers.Set(state, table, "isvararg",
                LuaValue.FromBoolean(closure?.Function.IsVarArg ?? true));
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
            SetInteger(state, table, "ftransfer", 0);
            SetInteger(state, table, "ntransfer", 0);
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

        if (frameIndex <= 0)
        {
            return null;
        }

        var caller = thread.Frames[frameIndex - 1];
        var callPc = Math.Clamp(caller.ProgramCounter - 1, 0,
            caller.Closure.Function.Instructions.Length - 1);
        var call = caller.Closure.Function.Instructions[callPc];
        if (call.Opcode is not (LuaIrOpcode.Call or LuaIrOpcode.TailCall))
        {
            return null;
        }

        var local = LuaDebugApi.GetLocal(thread, caller, call.A + 1);
        if (local is { } named && !named.Name.StartsWith('('))
        {
            return (named.Name, "local");
        }

        for (var pc = callPc - 1; pc >= 0; pc--)
        {
            var instruction = caller.Closure.Function.Instructions[pc];
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

        var instructions = frame.Closure.Function.Instructions;
        for (var pc = beforeProgramCounter - 1; pc >= 0; pc--)
        {
            var instruction = instructions[pc];
            if (instruction.A != register)
            {
                continue;
            }

            if (instruction.Opcode == LuaIrOpcode.LoadConstant)
            {
                var constant = frame.Closure.Function.Constants[instruction.B];
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

        var adjusted = explicitThread ? (int)level : (int)level - 1;
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
        return upvalue.DebugName.IsEmpty
            ? upvalue.Name
            : Encoding.UTF8.GetString(upvalue.DebugName.AsSpan());
    }

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
                function, index, $"userdata expected, got {LuaLibraryHelpers.TypeName(value)}");
    }

    private static string Source(IEnumerable<byte> bytes)
    {
        var source = Encoding.UTF8.GetString([.. bytes]);
        if (source == "\u0001")
        {
            return string.Empty;
        }

        return source.Length == 0 ? "=?" : source;
    }

    private static string ShortSource(IEnumerable<byte> bytes)
    {
        const int maximumLength = 59;
        var source = Source(bytes);
        if (source.StartsWith('@'))
        {
            source = source[1..];
            return source.Length <= maximumLength
                ? source
                : "..." + source[^(maximumLength - 3)..];
        }

        if (source.StartsWith('='))
        {
            source = source[1..];
            return source.Length <= maximumLength ? source : source[..maximumLength];
        }

        const string prefix = "[string \"";
        const string suffix = "\"]";
        var available = maximumLength - prefix.Length - suffix.Length;
        var newLine = source.IndexOf('\n');
        var truncated = newLine >= 0 || source.Length > available;
        if (truncated)
        {
            var end = newLine < 0 ? source.Length : newLine;
            source = source[..Math.Min(end, available - 3)] + "...";
        }

        return prefix + source + suffix;
    }

    private static void SetString(LuaState state, LuaTable table, string name, string value) =>
        LuaLibraryHelpers.Set(state, table, name, LuaLibraryHelpers.String(state, value));

    private static void SetInteger(LuaState state, LuaTable table, string name, long value) =>
        LuaLibraryHelpers.Set(state, table, name, LuaValue.FromInteger(value));
}
