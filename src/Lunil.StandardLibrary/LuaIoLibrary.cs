using System.Globalization;
using System.Text;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.StandardLibrary;

internal static class LuaIoLibrary
{
    private static readonly LuaNativeFunction LinesIteratorDescriptor =
        new("for iterator", static (context, _, _) => IterateLine(context));

    public static LuaTable Install(LuaState state, LuaStandardLibraryOptions? options)
    {
        if (options is not null)
        {
            LuaStandardLibraryContext.Configure(state, options);
        }

        var context = LuaStandardLibraryContext.Get(state);
        var module = state.CreateTable();
        var methods = state.CreateTable();
        var metatable = state.CreateTable();
        LuaLibraryHelpers.Set(state, metatable, "__index", LuaValue.FromTable(methods));
        LuaLibraryHelpers.Set(state, metatable, "__name", LuaLibraryHelpers.String(state, "FILE*"));
        LuaLibraryHelpers.SetFunction(state, metatable, "__gc", GarbageCollect);
        LuaLibraryHelpers.SetFunction(state, metatable, "__close", CloseMetamethod);
        LuaLibraryHelpers.SetFunction(state, metatable, "__tostring", ToStringValue);

        AddFileMethods(state, methods);
        AddModuleFunctions(state, module);

        var input = CreateFile(state, context.Options.Console.OpenStandardInput(), metatable,
            standard: true, readable: true, writable: false, append: false);
        var output = CreateFile(state, context.Options.Console.OpenStandardOutput(), metatable,
            standard: true, readable: false, writable: true, append: false);
        var error = CreateFile(state, context.Options.Console.OpenStandardError(), metatable,
            standard: true, readable: false, writable: true, append: false);
        LuaLibraryHelpers.Set(state, module, "stdin", input);
        LuaLibraryHelpers.Set(state, module, "stdout", output);
        LuaLibraryHelpers.Set(state, module, "stderr", error);
        SetDefault(state, "_IO_input", input);
        SetDefault(state, "_IO_output", output);
        LuaLibraryHelpers.Set(state, state.Globals, "io", LuaValue.FromTable(module));
        return module;
    }

    private static void AddModuleFunctions(LuaState state, LuaTable module)
    {
        LuaLibraryHelpers.SetFunction(state, module, "close", IoClose);
        LuaLibraryHelpers.SetFunction(state, module, "flush", IoFlush);
        LuaLibraryHelpers.SetFunction(state, module, "input", IoInput);
        LuaLibraryHelpers.SetFunction(state, module, "lines", IoLines);
        LuaLibraryHelpers.SetFunction(state, module, "open", IoOpen);
        LuaLibraryHelpers.SetFunction(state, module, "output", IoOutput);
        LuaLibraryHelpers.SetFunction(state, module, "popen", IoPopen);
        LuaLibraryHelpers.SetFunction(state, module, "read", IoRead);
        LuaLibraryHelpers.SetFunction(state, module, "tmpfile", IoTemporaryFile);
        LuaLibraryHelpers.SetFunction(state, module, "type", IoType);
        LuaLibraryHelpers.SetFunction(state, module, "write", IoWrite);
    }

    private static void AddFileMethods(LuaState state, LuaTable methods)
    {
        LuaLibraryHelpers.SetFunction(state, methods, "close", FileClose);
        LuaLibraryHelpers.SetFunction(state, methods, "flush", FileFlush);
        LuaLibraryHelpers.SetFunction(state, methods, "lines", FileLines);
        LuaLibraryHelpers.SetFunction(state, methods, "read", FileRead);
        LuaLibraryHelpers.SetFunction(state, methods, "seek", FileSeek);
        LuaLibraryHelpers.SetFunction(state, methods, "setvbuf", FileSetBuffer);
        LuaLibraryHelpers.SetFunction(state, methods, "write", FileWrite);
    }

    private static LuaValue[] IoOpen(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var path = Utf8(arguments, 0, "open");
        var modeText = arguments.Length < 2 || arguments[1].IsNil
            ? "r"
            : Encoding.UTF8.GetString(LuaLibraryHelpers.CheckStringBytes(arguments, 1, "open"));
        if (!TryParseMode(modeText, out var mode, out var readable, out var writable, out var append))
        {
            throw LuaLibraryHelpers.BadArgument("open", 1, "invalid mode");
        }

        try
        {
            var stream = LuaStandardLibraryContext.Get(state).Options.FileSystem.Open(path, mode);
            return [CreateFile(state, stream, GetFileMetatable(state), false, readable, writable, append)];
        }
        catch (Exception exception) when (IsIoException(exception))
        {
            return Failure(state, exception, path);
        }
    }

    private static LuaValue[] IoTemporaryFile(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        try
        {
            var stream = LuaStandardLibraryContext.Get(state).Options.FileSystem.OpenTemporary(out _);
            return [CreateFile(state, stream, GetFileMetatable(state), false, true, true, false)];
        }
        catch (Exception exception) when (IsIoException(exception))
        {
            return Failure(state, exception);
        }
    }

    private static LuaValue[] IoPopen(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var command = Utf8(arguments, 0, "popen");
        var mode = arguments.Length < 2 || arguments[1].IsNil
            ? "r"
            : Encoding.UTF8.GetString(LuaLibraryHelpers.CheckStringBytes(arguments, 1, "popen"));
        if (mode is not ("r" or "w"))
        {
            throw LuaLibraryHelpers.BadArgument("popen", 1, "invalid mode");
        }

        try
        {
            var stream = LuaStandardLibraryContext.Get(state).Options.OperatingSystem
                .OpenPipe(command, mode == "r", out var process);
            var file = new LuaFileHandle(stream, false, mode == "r", mode == "w", false, process);
            return [CreateFile(state, file, GetFileMetatable(state))];
        }
        catch (Exception exception) when (IsIoException(exception))
        {
            return Failure(state, exception, command);
        }
    }

    private static LuaValue[] IoInput(LuaState state, ReadOnlySpan<LuaValue> arguments) =>
        SetOrGetDefault(state, arguments, "input", "_IO_input", LuaFileMode.Read);

    private static LuaValue[] IoOutput(LuaState state, ReadOnlySpan<LuaValue> arguments) =>
        SetOrGetDefault(state, arguments, "output", "_IO_output", LuaFileMode.Write);

    private static LuaValue[] SetOrGetDefault(
        LuaState state,
        ReadOnlySpan<LuaValue> arguments,
        string function,
        string registryKey,
        LuaFileMode mode)
    {
        if (arguments.Length == 0 || arguments[0].IsNil)
        {
            return [GetDefault(state, registryKey)];
        }

        LuaValue file;
        if (arguments[0].Kind == LuaValueKind.String)
        {
            var path = arguments[0].AsString().ToString();
            try
            {
                var stream = LuaStandardLibraryContext.Get(state).Options.FileSystem.Open(path, mode);
                file = CreateFile(
                    state, stream, GetFileMetatable(state), false,
                    readable: mode == LuaFileMode.Read,
                    writable: mode == LuaFileMode.Write,
                    append: false);
            }
            catch (Exception exception) when (IsIoException(exception))
            {
                throw new LuaRuntimeException($"cannot open file '{path}' ({exception.Message})");
            }
        }
        else
        {
            _ = CheckOpenFile(arguments, 0, function);
            file = arguments[0];
        }

        SetDefault(state, registryKey, file);
        return [file];
    }

    private static LuaValue[] IoRead(LuaState state, ReadOnlySpan<LuaValue> arguments) =>
        Read(state, GetOpenDefault(state, "_IO_input", "input"), arguments, 0, "read");

    private static LuaValue[] IoWrite(LuaState state, ReadOnlySpan<LuaValue> arguments) =>
        Write(state, GetOpenDefault(state, "_IO_output", "output"), arguments, 0, "write");

    private static LuaValue[] IoFlush(LuaState state, ReadOnlySpan<LuaValue> arguments) =>
        Flush(state, GetOpenDefault(state, "_IO_output", "output"), "flush");

    private static LuaValue[] IoClose(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var file = arguments.Length == 0 || arguments[0].IsNil
            ? GetDefault(state, "_IO_output")
            : arguments[0];
        return Close(state, file, "close");
    }

    private static LuaValue[] IoLines(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        LuaValue file;
        var formatStart = 0;
        var autoClose = false;
        if (arguments.Length > 0 && !arguments[0].IsNil)
        {
            var path = Utf8(arguments, 0, "lines");
            try
            {
                var stream = LuaStandardLibraryContext.Get(state).Options.FileSystem
                    .Open(path, LuaFileMode.Read);
                file = CreateFile(state, stream, GetFileMetatable(state), false, true, false, false);
                autoClose = true;
                formatStart = 1;
            }
            catch (Exception exception) when (IsIoException(exception))
            {
                throw new LuaRuntimeException($"cannot open file '{path}' ({exception.Message})");
            }
        }
        else
        {
            file = GetDefault(state, "_IO_input");
            formatStart = arguments.Length == 0 ? 0 : 1;
        }

        return CreateLinesIterator(state, file, arguments[formatStart..], autoClose);
    }

    private static LuaValue[] IoType(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var value = LuaLibraryHelpers.Required(arguments, 0, "type");
        if (value.Kind != LuaValueKind.Userdata || value.AsUserdata().Payload is not LuaFileHandle file)
        {
            return [LuaValue.Nil];
        }

        return [LuaLibraryHelpers.String(state, file.IsClosed ? "closed file" : "file")];
    }

    private static LuaValue[] FileRead(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        _ = CheckOpenFile(arguments, 0, "read");
        return Read(state, arguments[0], arguments, 1, "read");
    }

    private static LuaValue[] FileWrite(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        _ = CheckOpenFile(arguments, 0, "write");
        return Write(state, arguments[0], arguments, 1, "write");
    }

    private static LuaValue[] FileFlush(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        _ = CheckOpenFile(arguments, 0, "flush");
        return Flush(state, arguments[0], "flush");
    }

    private static LuaValue[] FileClose(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        _ = CheckOpenFile(arguments, 0, "close");
        return Close(state, arguments[0], "close");
    }

    private static LuaValue[] FileLines(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        _ = CheckOpenFile(arguments, 0, "lines");
        var file = arguments[0];
        return CreateLinesIterator(state, file, arguments[1..], autoClose: false);
    }

    private static LuaValue[] FileSeek(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var file = CheckOpenFile(arguments, 0, "seek");
        var whence = arguments.Length < 2 || arguments[1].IsNil
            ? "cur"
            : Encoding.UTF8.GetString(LuaLibraryHelpers.CheckStringBytes(arguments, 1, "seek"));
        var origin = whence switch
        {
            "set" => SeekOrigin.Begin,
            "cur" => SeekOrigin.Current,
            "end" => SeekOrigin.End,
            _ => throw LuaLibraryHelpers.BadArgument("seek", 1, "invalid option"),
        };
        var offset = LuaLibraryHelpers.OptionalInteger(arguments, 2, 0, "seek");
        try
        {
            return [LuaValue.FromInteger(file.Seek(offset, origin))];
        }
        catch (Exception exception) when (IsIoException(exception))
        {
            return Failure(state, exception);
        }
    }

    private static LuaValue[] FileSetBuffer(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var file = CheckOpenFile(arguments, 0, "setvbuf");
        var mode = Encoding.UTF8.GetString(LuaLibraryHelpers.CheckStringBytes(arguments, 1, "setvbuf"));
        if (mode is not ("no" or "full" or "line"))
        {
            throw LuaLibraryHelpers.BadArgument("setvbuf", 1, "invalid option");
        }

        var size = LuaLibraryHelpers.OptionalInteger(arguments, 2, 8192, "setvbuf");
        if (size < 0 || size > int.MaxValue)
        {
            throw LuaLibraryHelpers.BadArgument("setvbuf", 2, "invalid buffer size");
        }

        file.BufferMode = mode;
        file.BufferSize = (int)size;
        return [LuaValue.FromBoolean(true)];
    }

    private static LuaValue[] GarbageCollect(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        if (arguments.Length > 0 && arguments[0].Kind == LuaValueKind.Userdata &&
            arguments[0].AsUserdata().Payload is LuaFileHandle file)
        {
            try
            {
                file.Dispose();
            }
            catch (Exception)
            {
            }
        }

        return [];
    }

    private static LuaValue[] CloseMetamethod(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        if (arguments.Length == 0 || arguments[0].Kind != LuaValueKind.Userdata ||
            arguments[0].AsUserdata().Payload is not LuaFileHandle file || file.IsClosed)
        {
            return [];
        }

        var result = Close(state, arguments[0], "close");
        if (result.Length > 0 && result[0].IsNil)
        {
            throw new LuaRuntimeException(result.Length > 1 ? result[1].AsString().ToString() : "file close failed");
        }

        return [];
    }

    private static LuaValue[] ToStringValue(LuaState state, ReadOnlySpan<LuaValue> arguments)
    {
        var value = LuaLibraryHelpers.Required(arguments, 0, "tostring");
        if (value.Kind != LuaValueKind.Userdata || value.AsUserdata().Payload is not LuaFileHandle file)
        {
            throw LuaLibraryHelpers.BadArgument("tostring", 0, "FILE* expected");
        }

        return [LuaLibraryHelpers.String(state,
            file.IsClosed ? "file (closed)" : $"file (0x{value.AsUserdata().GetHashCode():x})")];
    }

    private static LuaValue[] Read(
        LuaState state,
        LuaValue fileValue,
        ReadOnlySpan<LuaValue> arguments,
        int start,
        string function)
    {
        var file = CheckOpenFile(fileValue, start == 0 ? 0 : 0, function);
        if (!file.Readable)
        {
            return Failure(state, new IOException("file is not readable"));
        }

        var results = new List<LuaValue>();
        try
        {
            if (start >= arguments.Length)
            {
                return ReadOne(state, file, LuaLibraryHelpers.String(state, "l"), 0, out var value)
                    ? [value]
                    : [LuaValue.Nil];
            }

            for (var index = start; index < arguments.Length; index++)
            {
                if (!ReadOne(state, file, arguments[index], index - start, out var value))
                {
                    if (results.Count == 0)
                    {
                        results.Add(LuaValue.Nil);
                    }
                    break;
                }

                results.Add(value);
            }

            return [.. results];
        }
        catch (Exception exception) when (IsIoException(exception))
        {
            return Failure(state, exception);
        }
    }

    private static bool ReadOne(
        LuaState state,
        LuaFileHandle file,
        LuaValue format,
        int argumentIndex,
        out LuaValue value)
    {
        if (format.Kind is LuaValueKind.Integer or LuaValueKind.Float)
        {
            if (!format.TryGetInteger(out var count))
            {
                throw LuaLibraryHelpers.BadArgument(
                    "read", argumentIndex, "number has no integer representation");
            }

            if (count < 0 || count > int.MaxValue)
            {
                throw new LuaRuntimeException("not enough memory");
            }

            var bytes = file.ReadBytes((int)count);
            if (count != 0 && bytes.Length == 0)
            {
                value = LuaValue.Nil;
                return false;
            }

            if (count == 0 && file.IsEndOfFile)
            {
                value = LuaValue.Nil;
                return false;
            }

            value = LuaValue.FromString(state.Strings.GetOrCreate(bytes));
            return true;
        }

        if (format.Kind != LuaValueKind.String)
        {
            throw LuaLibraryHelpers.BadArgument(
                "read", argumentIndex,
                $"string expected, got {LuaLibraryHelpers.TypeName(format)}");
        }

        var option = format.AsString().ToString();
        if (option.StartsWith('*'))
        {
            option = option[1..];
        }

        switch (option.Length == 0 ? '\0' : option[0])
        {
            case 'n':
                return file.TryReadNumber(state, out value);
            case 'a':
                value = LuaValue.FromString(state.Strings.GetOrCreate(file.ReadAll()));
                return true;
            case 'l':
            case 'L':
                var line = file.ReadLine(option[0] == 'L');
                if (line is null)
                {
                    value = LuaValue.Nil;
                    return false;
                }

                value = LuaValue.FromString(state.Strings.GetOrCreate(line));
                return true;
            default:
                throw LuaLibraryHelpers.BadArgument("read", argumentIndex, "invalid format");
        }
    }

    private static LuaValue[] Write(
        LuaState state,
        LuaValue fileValue,
        ReadOnlySpan<LuaValue> arguments,
        int start,
        string function)
    {
        var file = CheckOpenFile(fileValue, 0, function);
        if (!file.Writable)
        {
            return Failure(state, new IOException("file is not writable"));
        }

        try
        {
            for (var index = start; index < arguments.Length; index++)
            {
                var value = arguments[index];
                var bytes = value.Kind switch
                {
                    LuaValueKind.String => value.AsString().ToArray(),
                    LuaValueKind.Integer => Encoding.ASCII.GetBytes(
                        value.AsInteger().ToString(CultureInfo.InvariantCulture)),
                    LuaValueKind.Float => Encoding.ASCII.GetBytes(
                        LuaValueOperations.FormatFloat(value.AsFloat())),
                    _ => throw LuaLibraryHelpers.BadArgument(
                        function, index - start, $"string expected, got {LuaLibraryHelpers.TypeName(value)}"),
                };
                file.Write(bytes);
            }

            return [fileValue];
        }
        catch (Exception exception) when (IsIoException(exception))
        {
            return Failure(state, exception);
        }
    }

    private static LuaValue[] Flush(LuaState state, LuaValue fileValue, string function)
    {
        var file = CheckOpenFile(fileValue, 0, function);
        try
        {
            file.Flush();
            return [LuaValue.FromBoolean(true)];
        }
        catch (Exception exception) when (IsIoException(exception))
        {
            return Failure(state, exception);
        }
    }

    private static LuaValue[] Close(LuaState state, LuaValue fileValue, string function)
    {
        var file = CheckOpenFile(fileValue, 0, function);
        if (file.IsStandard)
        {
            return [LuaValue.Nil, LuaLibraryHelpers.String(state, "cannot close standard file")];
        }

        try
        {
            var processResult = file.Close();
            return processResult is null
                ? [LuaValue.FromBoolean(true)]
                : ExecuteResult(state, processResult.Value);
        }
        catch (Exception exception) when (IsIoException(exception))
        {
            return Failure(state, exception);
        }
    }

    private static LuaValue[] CreateLinesIterator(
        LuaState state,
        LuaValue file,
        ReadOnlySpan<LuaValue> formats,
        bool autoClose)
    {
        if (formats.Length > 250)
        {
            throw new LuaRuntimeException("too many arguments");
        }

        var captures = new LuaValue[formats.Length + 2];
        captures[0] = file;
        captures[1] = LuaValue.FromBoolean(autoClose);
        formats.CopyTo(captures.AsSpan(2));
        var iterator = state.CreateNativeClosure(LinesIteratorDescriptor, captures);
        return autoClose
            ? [LuaValue.FromFunction(iterator), LuaValue.Nil, LuaValue.Nil, file]
            : [LuaValue.FromFunction(iterator)];
    }

    private static LuaNativeStep IterateLine(LuaNativeCallContext context)
    {
        var fileValue = context.Captures[0];
        var file = fileValue.AsUserdata().Payload as LuaFileHandle ??
            throw new LuaRuntimeException("file is already closed");
        if (file.IsClosed)
        {
            throw new LuaRuntimeException("file is already closed");
        }

        var formats = context.Captures.Count == 2
            ? new[] { LuaLibraryHelpers.String(context.State, "l") }
            : context.Captures.Skip(2).ToArray();
        var results = Read(context.State, fileValue, formats, 0, "read");
        if (results.Length == 0 || results[0].IsNil)
        {
            if (results.Length > 1)
            {
                if (context.Captures[1].AsBoolean())
                {
                    _ = Close(context.State, fileValue, "close");
                }

                throw new LuaRuntimeException(results[1].AsString().ToString());
            }

            if (context.Captures[1].AsBoolean() && !file.IsClosed)
            {
                _ = Close(context.State, fileValue, "close");
            }

            return LuaNativeStep.Completed();
        }

        return LuaNativeStep.Completed(results);
    }

    private static LuaValue CreateFile(
        LuaState state,
        Stream stream,
        LuaTable metatable,
        bool standard,
        bool readable,
        bool writable,
        bool append)
    {
        var file = new LuaFileHandle(stream, standard, readable, writable, append);
        return CreateFile(state, file, metatable);
    }

    private static LuaValue CreateFile(LuaState state, LuaFileHandle file, LuaTable metatable)
    {
        var userdata = state.CreateUserdata(file, userValueCount: 0, payloadLogicalSize: 128);
        userdata.SetMetatable(metatable);
        return LuaValue.FromUserdata(userdata);
    }

    private static LuaTable GetFileMetatable(LuaState state)
    {
        var output = GetDefault(state, "_IO_output");
        return output.AsUserdata().Metatable ?? throw new InvalidOperationException();
    }

    private static LuaFileHandle CheckOpenFile(
        ReadOnlySpan<LuaValue> arguments,
        int index,
        string function)
    {
        if ((uint)index >= (uint)arguments.Length)
        {
            throw LuaLibraryHelpers.BadArgument(function, index, "FILE* expected, got no value");
        }

        return CheckOpenFile(arguments[index], index, function);
    }

    private static LuaFileHandle CheckOpenFile(LuaValue value, int index, string function)
    {
        if (value.Kind != LuaValueKind.Userdata || value.AsUserdata().Payload is not LuaFileHandle file)
        {
            throw LuaLibraryHelpers.BadArgument(function, index, $"FILE* expected, got {LuaLibraryHelpers.TypeName(value)}");
        }

        if (file.IsClosed)
        {
            throw LuaLibraryHelpers.BadArgument(function, index, "attempt to use a closed file");
        }

        return file;
    }

    private static void SetDefault(LuaState state, string name, LuaValue value) =>
        state.Registry.Set(LuaLibraryHelpers.String(state, name), value);

    private static LuaValue GetDefault(LuaState state, string name) =>
        state.Registry.Get(LuaLibraryHelpers.String(state, name));

    private static LuaValue GetOpenDefault(LuaState state, string name, string role)
    {
        var value = GetDefault(state, name);
        if (value.Kind == LuaValueKind.Userdata &&
            value.AsUserdata().Payload is LuaFileHandle { IsClosed: true })
        {
            throw new LuaRuntimeException($"default {role} file is closed");
        }

        return value;
    }

    private static string Utf8(ReadOnlySpan<LuaValue> arguments, int index, string function) =>
        Encoding.UTF8.GetString(LuaLibraryHelpers.CheckStringBytes(arguments, index, function));

    private static LuaValue[] Failure(LuaState state, Exception exception, string? prefix = null)
    {
        var message = prefix is null ? exception.Message : $"{prefix}: {exception.Message}";
        return
        [
            LuaValue.Nil,
            LuaLibraryHelpers.String(state, message),
            LuaValue.FromInteger(exception.HResult & 0xffff),
        ];
    }

    private static LuaValue[] ExecuteResult(LuaState state, LuaExecuteResult result) =>
        result.Started && result.Status == 0
            ? [LuaValue.FromBoolean(true), LuaLibraryHelpers.String(state, result.Kind), LuaValue.FromInteger(0)]
            : [LuaValue.Nil, LuaLibraryHelpers.String(state, result.Kind), LuaValue.FromInteger(result.Status)];

    private static bool IsIoException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException or
            ObjectDisposedException or InvalidOperationException;

    private static bool TryParseMode(
        string text,
        out LuaFileMode mode,
        out bool readable,
        out bool writable,
        out bool append)
    {
        var normalized = text.EndsWith('b') ? text[..^1] : text;
        (mode, readable, writable, append) = normalized switch
        {
            "r" => (LuaFileMode.Read, true, false, false),
            "w" => (LuaFileMode.Write, false, true, false),
            "a" => (LuaFileMode.Append, false, true, true),
            "r+" => (LuaFileMode.ReadUpdate, true, true, false),
            "w+" => (LuaFileMode.WriteUpdate, true, true, false),
            "a+" => (LuaFileMode.AppendUpdate, true, true, true),
            _ => default,
        };
        return normalized is "r" or "w" or "a" or "r+" or "w+" or "a+";
    }
}

internal sealed class LuaFileHandle : IDisposable
{
    private readonly Stream _stream;
    private readonly IDisposable? _process;
    private readonly Stack<byte> _pushback = new();

    public LuaFileHandle(
        Stream stream,
        bool standard,
        bool readable,
        bool writable,
        bool append,
        IDisposable? process = null)
    {
        _stream = stream;
        _process = process;
        IsStandard = standard;
        Readable = readable;
        Writable = writable;
        Append = append;
    }

    public bool IsStandard { get; }
    public bool Readable { get; }
    public bool Writable { get; }
    public bool Append { get; }
    public bool IsClosed { get; private set; }
    public string BufferMode { get; set; } = "full";
    public int BufferSize { get; set; } = 8192;

    public bool IsEndOfFile
    {
        get
        {
            var value = ReadByte();
            if (value < 0)
            {
                return true;
            }

            Unread((byte)value);
            return false;
        }
    }

    public byte[] ReadBytes(int count)
    {
        var result = new byte[count];
        var length = 0;
        while (length < count)
        {
            var value = ReadByte();
            if (value < 0)
            {
                break;
            }

            result[length++] = (byte)value;
        }

        return length == count ? result : result[..length];
    }

    public byte[] ReadAll()
    {
        using var result = new MemoryStream();
        while (_pushback.Count > 0)
        {
            result.WriteByte(_pushback.Pop());
        }

        _stream.CopyTo(result);
        return result.ToArray();
    }

    public byte[]? ReadLine(bool keepNewLine)
    {
        using var result = new MemoryStream();
        while (true)
        {
            var value = ReadByte();
            if (value < 0)
            {
                return result.Length == 0 ? null : result.ToArray();
            }

            if (value == '\n')
            {
                if (keepNewLine)
                {
                    result.WriteByte((byte)value);
                }

                return result.ToArray();
            }

            result.WriteByte((byte)value);
        }
    }

    public bool TryReadNumber(LuaState state, out LuaValue value)
    {
        const int maximumNumeralLength = 200;
        int current;
        do
        {
            current = ReadByte();
        }
        while (current >= 0 && IsAsciiSpace((byte)current));
        if (current < 0)
        {
            value = LuaValue.Nil;
            return false;
        }

        var candidate = new List<byte>(maximumNumeralLength);
        var overflow = false;

        bool TakeCurrent()
        {
            if (candidate.Count >= maximumNumeralLength)
            {
                overflow = true;
                return false;
            }

            candidate.Add((byte)current);
            current = ReadByte();
            return true;
        }

        bool TakeIf(byte first, byte second) =>
            current == first || current == second ? TakeCurrent() : false;

        int ReadDigits(bool hexadecimal)
        {
            var count = 0;
            while (current >= 0 &&
                   (hexadecimal ? IsAsciiHexDigit((byte)current) : IsAsciiDigit((byte)current)) &&
                   TakeCurrent())
            {
                count++;
            }

            return count;
        }

        _ = TakeIf((byte)'-', (byte)'+');
        var hexadecimal = false;
        var digitCount = 0;
        if (TakeIf((byte)'0', (byte)'0'))
        {
            if (TakeIf((byte)'x', (byte)'X'))
            {
                hexadecimal = true;
            }
            else
            {
                digitCount = 1;
            }
        }

        digitCount += ReadDigits(hexadecimal);
        var decimalSeparator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        var localeDecimal = decimalSeparator.Length == 1 ? (byte)decimalSeparator[0] : (byte)'.';
        if (TakeIf(localeDecimal, (byte)'.'))
        {
            digitCount += ReadDigits(hexadecimal);
        }

        if (digitCount > 0 && TakeIf(
                hexadecimal ? (byte)'p' : (byte)'e',
                hexadecimal ? (byte)'P' : (byte)'E'))
        {
            _ = TakeIf((byte)'-', (byte)'+');
            _ = ReadDigits(hexadecimal: false);
        }

        if (current >= 0)
        {
            Unread((byte)current);
        }

        if (overflow)
        {
            value = LuaValue.Nil;
            return false;
        }

        if (localeDecimal != (byte)'.')
        {
            for (var index = 0; index < candidate.Count; index++)
            {
                if (candidate[index] == localeDecimal)
                {
                    candidate[index] = (byte)'.';
                }
            }
        }

        var stringValue = LuaValue.FromString(state.Strings.GetOrCreate(candidate.ToArray()));
        return LuaValueOperations.TryToNumber(stringValue, out value);
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (Append && _stream.CanSeek)
        {
            _stream.Seek(0, SeekOrigin.End);
        }

        _stream.Write(bytes);
        if (BufferMode == "no" || BufferMode == "line" && bytes.Contains((byte)'\n'))
        {
            _stream.Flush();
        }
    }

    public void Flush() => _stream.Flush();

    public long Seek(long offset, SeekOrigin origin)
    {
        if (!_stream.CanSeek)
        {
            throw new IOException("Illegal seek");
        }

        if (origin == SeekOrigin.Current)
        {
            offset -= _pushback.Count;
        }

        _pushback.Clear();
        return _stream.Seek(offset, origin);
    }

    public LuaExecuteResult? Close()
    {
        if (IsClosed)
        {
            return null;
        }

        IsClosed = true;
        _stream.Dispose();
        if (_process is ILuaPipeProcess pipe)
        {
            var result = pipe.Wait();
            pipe.Dispose();
            return result;
        }

        _process?.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (IsStandard || IsClosed)
        {
            return;
        }

        _ = Close();
    }

    private int ReadByte() => _pushback.Count > 0 ? _pushback.Pop() : _stream.ReadByte();

    private void Unread(byte value) => _pushback.Push(value);

    private static bool IsAsciiSpace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0x0b or 0x0c;

    private static bool IsAsciiDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';

    private static bool IsAsciiHexDigit(byte value) => IsAsciiDigit(value) ||
        value is >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';
}
