using System.Globalization;
using System.Text.Json.Nodes;
using Lunil.Compiler;
using Lunil.Runtime;
using Lunil.Runtime.Debugging;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.DebugAdapter;

/// <summary>
/// Drives one DAP session: executes a Lua script with an attached <see cref="LuaDebugSession"/>
/// and translates breakpoints, stepping, pause, stack, and variable requests to the runtime API.
/// The protocol loop reads on the calling thread; script execution runs on a dedicated thread
/// and waits for continue/step commands while paused.
/// </summary>
internal sealed class DapSession : IDisposable
{
    private const int MaximumVariablesPerPage = 100;

    private readonly DapConnection _connection;
    private readonly LuaDebugSession _debugSession = new();
    private readonly Dictionary<string, HashSet<int>> _breakpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> _breakableLines = new(StringComparer.Ordinal);
    private readonly Dictionary<int, VariablesTarget> _variablesTargets = new();
    private readonly SemaphoreSlim _resumeSignal = new(0);
    private readonly object _commandLock = new();
    private LuaState? _state;
    private LuaThread? _thread;
    private LuaInterpreter? _interpreter;
    private string? _sourcePath;
    private string? _scriptProgramPath;
    private string? _scriptSource;
    private LuaDebugStepMode _pendingStep;
    private string _lastStopReason = "breakpoint";
    private volatile bool _isPaused;
    private bool _disconnectRequested;
    private int _nextThreadId = 1;
    private int _nextVariablesReference;

    public DapSession(DapConnection connection)
    {
        _connection = connection;
    }

    public void Dispose() => _resumeSignal.Dispose();

    public void Run()
    {
        try
        {
            while (!_disconnectRequested)
            {
                var message = _connection.ReadMessage();
                if (message is null)
                {
                    break;
                }

                HandleMessage(message);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            Console.Error.WriteLine($"The debug adapter connection ended: {exception.Message}");
        }
        finally
        {
            _debugSession.Detach();
        }
    }

    private void HandleMessage(DapMessage message)
    {
        if (message.Kind == DapMessageKind.Request)
        {
            HandleRequest(message);
            return;
        }

        throw new InvalidDataException($"Unexpected DAP message kind: {message.Kind}");
    }

    private void HandleRequest(DapMessage message)
    {
        CurrentRequestId = message.Id ?? 0;
        try
        {
            switch (message.Method)
            {
                case "initialize":
                    Respond(message.Id, new JsonObject
                    {
                        ["success"] = true,
                        ["body"] = new JsonObject
                        {
                            ["supportsConfigurationDoneRequest"] = true,
                            ["supportsSteppingGranularity"] = false,
                            ["supportsTerminateRequest"] = false,
                        },
                    });
                    _connection.WriteMessage(DapMessage.Event("initialized"));
                    break;
                case "launch":
                    HandleLaunch(message.Body);
                    Respond(message.Id, new JsonObject { ["success"] = true });
                    break;
                case "setBreakpoints":
                    HandleSetBreakpoints(message.Body);
                    break;
                case "configurationDone":
                    // Start execution only after the client finished configuring breakpoints.
                    StartExecution();
                    Respond(message.Id, new JsonObject { ["success"] = true });
                    break;
                case "continue":
                    SetStep(LuaDebugStepMode.None);
                    Respond(message.Id, new JsonObject { ["success"] = true });
                    break;
                case "next":
                    SetStep(LuaDebugStepMode.Over);
                    Respond(message.Id, new JsonObject { ["success"] = true });
                    break;
                case "stepIn":
                    SetStep(LuaDebugStepMode.Into);
                    Respond(message.Id, new JsonObject { ["success"] = true });
                    break;
                case "stepOut":
                    SetStep(LuaDebugStepMode.Out);
                    Respond(message.Id, new JsonObject { ["success"] = true });
                    break;
                case "pause":
                    _debugSession.RequestPause();
                    lock (_commandLock)
                    {
                        _lastStopReason = "pause";
                    }
                    Respond(message.Id, new JsonObject { ["success"] = true });
                    break;
                case "stackTrace":
                    Respond(message.Id, RequirePaused(() => HandleStackTrace(message.Body)));
                    break;
                case "scopes":
                    Respond(message.Id, RequirePaused(() => HandleScopes(message.Body)));
                    break;
                case "variables":
                    Respond(message.Id, RequirePaused(() => HandleVariables(message.Body)));
                    break;
                case "threads":
                    Respond(message.Id, HandleThreads());
                    break;
                case "disconnect":
                    _disconnectRequested = true;
                    _resumeSignal.Release();
                    Respond(message.Id, new JsonObject { ["success"] = true });
                    break;
                default:
                    Respond(message.Id, new JsonObject
                    {
                        ["success"] = false,
                        ["message"] = $"Unsupported DAP command: {message.Method}",
                    });
                    break;
            }
        }
        catch (Exception exception)
        {
            Respond(message.Id, new JsonObject
            {
                ["success"] = false,
                ["message"] = exception.Message,
            });
        }
    }

    private void HandleLaunch(JsonNode? body)
    {
        var program = (string?)body?["program"];
        if (string.IsNullOrWhiteSpace(program))
        {
            throw new ArgumentException("launch requires a program path.");
        }

        var source = File.ReadAllText(program);
        _sourcePath = Path.GetFileName(program);
        _scriptProgramPath = program;
        _scriptSource = source;
    }

    private void StartExecution()
    {
        if (_scriptProgramPath is null)
        {
            return;
        }

        var programPath = _scriptProgramPath;
        var source = _scriptSource!;
        _scriptProgramPath = null;
        var execution = new Thread(() => RunExecutionLoop(programPath, source))
        {
            Name = "lunil-dap-execution",
            IsBackground = true,
        };
        execution.Start();
    }

    private void HandleSetBreakpoints(JsonNode? body)
    {
        var path = (string?)body?["source"]?["path"] ?? _sourcePath;
        var verified = new JsonArray();
        var resolved = new HashSet<int>();
        var breakable = ResolveBreakableLines(path);
        if (body?["breakpoints"] is JsonArray breakpoints)
        {
            foreach (var item in breakpoints)
            {
                var line = (int?)item?["line"] ?? 0;
                if (line <= 0)
                {
                    continue;
                }

                // Without a resolvable module the requested line is echoed unchanged rather
                // than pretending it was validated.
                if (breakable is null)
                {
                    verified.Add(new JsonObject { ["verified"] = true, ["line"] = line });
                    resolved.Add(line);
                    continue;
                }

                var index = breakable.BinarySearch(line);
                if (index < 0)
                {
                    index = ~index;
                }

                if (index >= breakable.Count)
                {
                    verified.Add(new JsonObject
                    {
                        ["verified"] = false,
                        ["line"] = line,
                        ["message"] = $"Line {line} does not map to an executable line.",
                    });
                    continue;
                }

                // Snap forward to the closest executable line so the runtime can actually
                // stop there; the response reports the line the breakpoint landed on.
                var mapped = breakable[index];
                verified.Add(new JsonObject { ["verified"] = true, ["line"] = mapped });
                resolved.Add(mapped);
            }
        }

        if (path is not null)
        {
            _breakpoints[path] = resolved;
            if (_sourcePath is not null && !string.Equals(path, _sourcePath, StringComparison.Ordinal))
            {
                // Standard DAP clients send the absolute source path while execution
                // resolves breakpoints against the launched file name; key both.
                _breakpoints[_sourcePath] = resolved;
            }

            if (_state is not null)
            {
                ApplyBreakpoints();
            }
        }

        Respond(CurrentRequestId, new JsonObject
        {
            ["success"] = true,
            ["body"] = new JsonObject { ["breakpoints"] = verified },
        });
    }

    /// <summary>
    /// The executable lines of the launched program (instruction source lines), or null while
    /// no compiled snapshot for the requested source is available.
    /// </summary>
    private List<int>? ResolveBreakableLines(string? path)
    {
        if (path is null || _scriptSource is null)
        {
            return null;
        }

        if (!_breakableLines.TryGetValue(path, out var lines))
        {
            // Only the launched program has a source snapshot to compile; other sources
            // (untouched modules) stay unverified.
            if (!string.Equals(path, _scriptProgramPath, StringComparison.Ordinal) &&
                !string.Equals(path, _sourcePath, StringComparison.Ordinal))
            {
                return null;
            }

            var sourceName = "@" + (_scriptProgramPath ?? "script");
            var compilation = new LuaCompiler().CompileUtf8(_scriptSource, sourceName);
            if (!compilation.Succeeded || compilation.Module is null)
            {
                return null;
            }

            lines = compilation.Module.Functions
                .SelectMany(static function => function.Instructions)
                .Select(static instruction => instruction.SourceLine)
                .Where(static line => line > 0)
                .Distinct()
                .OrderBy(static line => line)
                .ToList();
            _breakableLines[path] = lines;
            if (_sourcePath is not null && !string.Equals(path, _sourcePath, StringComparison.Ordinal))
            {
                _breakableLines[_sourcePath] = lines;
            }
        }

        return lines;
    }

    private int CurrentRequestId { get; set; }

    private JsonObject HandleStackTrace(JsonNode? body)
    {
        var threadId = (int?)body?["threadId"] ?? 0;
        var frames = new JsonArray();
        var level = 0;
        while (_thread is { } thread && LuaDebugApi.GetFrame(_state!, thread, level) is { } frame)
        {
            var line = LuaDebugApi.GetCurrentLine(thread, frame);
            frames.Add(new JsonObject
            {
                ["id"] = level,
                ["name"] = GetFrameName(frame),
                ["line"] = Math.Max(line, 1),
                ["column"] = 1,
                ["source"] = new JsonObject { ["path"] = _sourcePath },
            });
            level++;
        }

        return new JsonObject
        {
            ["success"] = true,
            ["body"] = new JsonObject { ["stackFrames"] = frames, ["totalFrames"] = level },
        };
    }

    private JsonObject HandleScopes(JsonNode? body)
    {
        var frameId = (int?)body?["frameId"] ?? 0;
        if (_thread is not { } thread || _state is not { } state ||
            LuaDebugApi.GetFrame(state, thread, frameId) is not { } frame)
        {
            return new JsonObject
            {
                ["success"] = false,
                ["message"] = "The requested stack frame is not available.",
            };
        }

        return new JsonObject
        {
            ["success"] = true,
            ["body"] = new JsonObject
            {
                ["scopes"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "Locals",
                        ["variablesReference"] = CreateVariablesTarget(new VariablesTarget(
                            VariablesKind.Locals, thread, frame, null)),
                        ["expensive"] = false,
                    },
                    new JsonObject
                    {
                        ["name"] = "Upvalues",
                        ["variablesReference"] = CreateVariablesTarget(new VariablesTarget(
                            VariablesKind.Upvalues, thread, frame, null)),
                        ["expensive"] = false,
                    },
                },
            },
        };
    }

    private JsonObject HandleVariables(JsonNode? body)
    {
        var reference = (int?)body?["variablesReference"] ?? 0;
        if (!_variablesTargets.TryGetValue(reference, out var target))
        {
            return new JsonObject
            {
                ["success"] = false,
                ["message"] = "The variables reference is unknown or no longer valid.",
            };
        }

        var start = Math.Max(0, (int?)body?["start"] ?? 0);
        var requestedCount = (int?)body?["count"] ?? 0;
        var count = requestedCount > 0
            ? Math.Min(requestedCount, MaximumVariablesPerPage)
            : MaximumVariablesPerPage;

        var entries = target.Kind switch
        {
            VariablesKind.Upvalues => EnumerateUpvalues(target.Frame!),
            VariablesKind.Table => EnumerateTableEntries(target.Table!),
            _ => EnumerateLocals(target),
        };

        var variables = new JsonArray();
        foreach (var (name, value) in entries.Skip(start).Take(count))
        {
            variables.Add(FormatValue(name, value));
        }

        return new JsonObject
        {
            ["success"] = true,
            ["body"] = new JsonObject { ["variables"] = variables },
        };
    }

    private static JsonObject HandleThreads() => new()
    {
        ["success"] = true,
        ["body"] = new JsonObject
        {
            ["threads"] = new JsonArray
            {
                new JsonObject { ["id"] = 1, ["name"] = "main" },
            },
        },
    };

    private void SetStep(LuaDebugStepMode step)
    {
        // Variables references handed out while paused would dangle across the resume.
        _variablesTargets.Clear();
        lock (_commandLock)
        {
            _pendingStep = step;
            // The next stop after a continue is a breakpoint hit; 'continue' is not
            // a valid DAP stopped reason.
            _lastStopReason = step == LuaDebugStepMode.None ? "breakpoint" : "step";
        }

        _resumeSignal.Release();
    }

    private void RunExecutionLoop(string programPath, string source)
    {
        try
        {
            var state = new LuaState();
            var compilation = new LuaCompiler().CompileUtf8(source, "@" + programPath);
            if (!compilation.Succeeded)
            {
                ReportCompilationFailure(compilation);
                return;
            }

            _state = state;
            _thread = state.CreateThread(state.CreateMainClosure(compilation.Module!));
            _interpreter = new LuaInterpreter();
            _debugSession.Attach(state);
            ApplyBreakpoints();
            _nextThreadId = 1;

            var result = _interpreter.Start(state, _thread);
            while (result.Signal == LuaVmSignal.Paused)
            {
                var reason = GetPauseReason();
                var line = Math.Max(1, LuaDebugApi.GetCurrentLine(
                    _thread,
                    LuaDebugApi.GetFrame(state, _thread, 0)!));
                _isPaused = true;
                _connection.WriteMessage(DapMessage.Event(
                    "stopped",
                    new JsonObject
                    {
                        ["reason"] = reason,
                        ["threadId"] = _nextThreadId,
                        ["line"] = line,
                        ["source"] = new JsonObject { ["path"] = programPath },
                    }));

                _resumeSignal.Wait();
                _isPaused = false;
                if (_disconnectRequested)
                {
                    return;
                }

                LuaDebugStepMode step;
                lock (_commandLock)
                {
                    step = _pendingStep;
                    _pendingStep = LuaDebugStepMode.None;
                }

                switch (step)
                {
                    case LuaDebugStepMode.Into:
                        _debugSession.StepInto();
                        break;
                    case LuaDebugStepMode.Over:
                        _debugSession.StepOver();
                        break;
                    case LuaDebugStepMode.Out:
                        _debugSession.StepOut();
                        break;
                    default:
                        _debugSession.Continue();
                        break;
                }

                result = _interpreter.ResumeDebugged(state, _thread);
            }

            if (result.Signal == LuaVmSignal.Error)
            {
                var message = result.Values.IsEmpty
                    ? "The script terminated with an error."
                    : result.Values[0].ToString();
                _connection.WriteMessage(DapMessage.Event(
                    "output",
                    new JsonObject
                    {
                        ["category"] = "stderr",
                        ["output"] = message + Environment.NewLine,
                    }));
            }

            _connection.WriteMessage(DapMessage.Event("terminated"));
        }
        catch (Exception exception)
        {
            _connection.WriteMessage(DapMessage.Event(
                "output",
                new JsonObject
                {
                    ["category"] = "stderr",
                    ["output"] = exception.ToString() + Environment.NewLine,
                }));
            _connection.WriteMessage(DapMessage.Event("terminated"));
        }
        finally
        {
            _debugSession.Detach();
        }
    }

    private void ReportCompilationFailure(LuaCompilationResult compilation)
    {
        foreach (var diagnostic in compilation.Diagnostics)
        {
            _connection.WriteMessage(DapMessage.Event(
                "output",
                new JsonObject
                {
                    ["category"] = "stderr",
                    ["output"] = diagnostic.ToString() + Environment.NewLine,
                }));
        }

        _connection.WriteMessage(DapMessage.Event("terminated"));
    }

    private void ApplyBreakpoints()
    {
        var lines = _breakpoints.TryGetValue(_sourcePath!, out var configured)
            ? configured
            : [];
        _debugSession.SetBreakpoints(lines);
    }

    private string GetPauseReason() => _lastStopReason;

    /// <summary>
    /// Stack inspection races with the running VM; while the thread is not paused the request
    /// fails instead of reading live frame data. The response callback only runs once the
    /// pause check passed, so handlers never touch the frames of a running VM.
    /// </summary>
    private JsonObject RequirePaused(Func<JsonObject> createResponse)
    {
        if (!_isPaused)
        {
            return new JsonObject
            {
                ["success"] = false,
                ["message"] = "The thread is not paused; stack data is unavailable.",
            };
        }

        return createResponse();
    }

    private static string GetFrameName(LuaFrame frame)
    {
        var function = LuaDebugApi.GetFunction(frame);
        if (function.TryGetClosure() is { } closure)
        {
            var sourceName = System.Text.Encoding.UTF8.GetString(closure.Function.SourceName.AsSpan());
            return string.IsNullOrWhiteSpace(sourceName) ? "script" : sourceName;
        }

        return "native";
    }

    private JsonObject FormatValue(string name, LuaValue value)
    {
        switch (value.Kind)
        {
            case LuaValueKind.Nil:
                return new JsonObject { ["name"] = name, ["value"] = "nil", ["variablesReference"] = 0 };
            case LuaValueKind.Boolean:
                return new JsonObject { ["name"] = name, ["value"] = value.AsBoolean().ToString().ToLowerInvariant(), ["variablesReference"] = 0 };
            case LuaValueKind.Integer:
                return new JsonObject { ["name"] = name, ["value"] = value.AsInteger().ToString(CultureInfo.InvariantCulture), ["variablesReference"] = 0 };
            case LuaValueKind.Float:
                return new JsonObject { ["name"] = name, ["value"] = value.AsFloat().ToString(CultureInfo.InvariantCulture), ["variablesReference"] = 0 };
            case LuaValueKind.String:
                return new JsonObject { ["name"] = name, ["value"] = $"\"{value.AsString().ToString()}\"", ["variablesReference"] = 0 };
            case LuaValueKind.Table:
                return FormatTable(name, value.AsTable());
            case LuaValueKind.Function:
                return new JsonObject { ["name"] = name, ["value"] = value.ToString(), ["variablesReference"] = 0 };
            default:
                return new JsonObject { ["name"] = name, ["value"] = value.ToString(), ["variablesReference"] = 0 };
        }
    }

    private JsonObject FormatTable(string name, LuaTable table)
    {
        var formatted = new JsonObject
        {
            ["name"] = name,
            ["value"] = table.ToString(),
            ["variablesReference"] = CreateVariablesTarget(
                new VariablesTarget(VariablesKind.Table, null, null, table)),
        };

        // Clients use the hint to page through the entries with start/count.
        var entryCount = table.ArrayLength + table.HashCount;
        if (entryCount > 0)
        {
            formatted["indexedVariables"] = entryCount;
        }

        return formatted;
    }

    private static IEnumerable<(string Name, LuaValue Value)> EnumerateLocals(VariablesTarget target)
    {
        var index = 1;
        while (LuaDebugApi.GetLocal(target.Thread!, target.Frame!, index) is { } local)
        {
            yield return (string.IsNullOrEmpty(local.Name) ? $"local{index}" : local.Name, local.Value);
            index++;
        }
    }

    private static IEnumerable<(string Name, LuaValue Value)> EnumerateUpvalues(LuaFrame frame)
    {
        var closure = frame.Closure;
        var names = closure.Function.Upvalues;
        for (var index = 0; index < closure.Upvalues.Count; index++)
        {
            var name = index < names.Length ? names[index].Name : string.Empty;
            yield return (string.IsNullOrWhiteSpace(name) ? $"upvalue{index}" : name, closure.Upvalues[index].Value);
        }
    }

    /// <summary>Enumerates entries array part first, then the hash part, via raw next.</summary>
    private static IEnumerable<(string Name, LuaValue Value)> EnumerateTableEntries(LuaTable table)
    {
        var key = LuaValue.Nil;
        while (table.Next(key, out key, out var value))
        {
            yield return (FormatKeyName(key), value);
        }
    }

    private static string FormatKeyName(LuaValue key) => key.Kind switch
    {
        LuaValueKind.String => $"[\"{key.AsString().ToString()}\"]",
        LuaValueKind.Integer => key.AsInteger().ToString(CultureInfo.InvariantCulture),
        _ => $"[{key}]",
    };

    private int CreateVariablesTarget(VariablesTarget target)
    {
        var reference = ++_nextVariablesReference;
        _variablesTargets[reference] = target;
        return reference;
    }

    private enum VariablesKind : byte
    {
        Locals,
        Upvalues,
        Table,
    }

    /// <summary>A variables scope or table bound to one paused stack; only valid until the resume.</summary>
    private sealed record VariablesTarget(
        VariablesKind Kind,
        LuaThread? Thread,
        LuaFrame? Frame,
        LuaTable? Table);

    private void Respond(int? id, JsonObject body)
    {
        if (id is not null)
        {
            _connection.WriteMessage(DapMessage.Response(id.Value, body));
        }
    }
}
