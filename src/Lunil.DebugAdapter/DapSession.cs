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
    private readonly DapConnection _connection;
    private readonly LuaDebugSession _debugSession = new();
    private readonly Dictionary<string, HashSet<int>> _breakpoints = new(StringComparer.Ordinal);
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
    private bool _disconnectRequested;
    private int _nextThreadId = 1;

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
                    _lastStopReason = "pause";
                    Respond(message.Id, new JsonObject { ["success"] = true });
                    break;
                case "stackTrace":
                    Respond(message.Id, HandleStackTrace(message.Body));
                    break;
                case "scopes":
                    Respond(message.Id, HandleScopes(message.Body));
                    break;
                case "variables":
                    Respond(message.Id, HandleVariables(message.Body));
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
        var lines = new HashSet<int>();
        if (body?["breakpoints"] is JsonArray breakpoints)
        {
            foreach (var item in breakpoints)
            {
                var line = (int?)item?["line"] ?? 0;
                if (line > 0)
                {
                    lines.Add(line);
                }
            }
        }

        if (path is not null)
        {
            _breakpoints[path] = lines;
            if (_sourcePath is not null && !string.Equals(path, _sourcePath, StringComparison.Ordinal))
            {
                // Standard DAP clients send the absolute source path while execution
                // resolves breakpoints against the launched file name; key both.
                _breakpoints[_sourcePath] = lines;
            }

            if (_state is not null)
            {
                ApplyBreakpoints();
            }
        }

        var verified = new JsonArray();
        foreach (var line in lines.Order())
        {
            verified.Add(new JsonObject
            {
                ["verified"] = true,
                ["line"] = line,
            });
        }

        Respond(CurrentRequestId, new JsonObject
        {
            ["success"] = true,
            ["body"] = new JsonObject { ["breakpoints"] = verified },
        });
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

    private static JsonObject HandleScopes(JsonNode? body)
    {
        var frameId = (int?)body?["frameId"] ?? 0;
        return new JsonObject
        {
            ["success"] = true,
            ["body"] = new JsonObject
            {
                ["scopes"] = new JsonArray
                {
                    new JsonObject { ["name"] = "Locals", ["variablesReference"] = EncodeVariablesReference(frameId, 0), ["expensive"] = false },
                    new JsonObject { ["name"] = "Upvalues", ["variablesReference"] = EncodeVariablesReference(frameId, 1), ["expensive"] = false },
                },
            },
        };
    }

    private JsonObject HandleVariables(JsonNode? body)
    {
        var reference = (int?)body?["variablesReference"] ?? 0;
        var (frameId, kind) = DecodeVariablesReference(reference);
        var variables = new JsonArray();
        if (_thread is { } thread && LuaDebugApi.GetFrame(_state!, thread, frameId) is { } frame)
        {
            var index = 1;
            while (LuaDebugApi.GetLocal(thread, frame, index) is { } local)
            {
                var name = string.IsNullOrEmpty(local.Name) ? $"local{index}" : local.Name;
                variables.Add(FormatValue(name, local.Value));
                index++;
            }

            if (kind == 1)
            {
                foreach (var upvalue in EnumerateUpvalues(frame.Closure))
                {
                    variables.Add(FormatValue(upvalue.Name, upvalue.Value));
                }
            }
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
        lock (_commandLock)
        {
            _pendingStep = step;
            _lastStopReason = step == LuaDebugStepMode.None ? "continue" : "step";
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

    private static JsonObject FormatValue(string name, LuaValue value)
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
                return new JsonObject { ["name"] = name, ["value"] = value.AsTable().ToString(), ["variablesReference"] = 0 };
            case LuaValueKind.Function:
                return new JsonObject { ["name"] = name, ["value"] = value.ToString(), ["variablesReference"] = 0 };
            default:
                return new JsonObject { ["name"] = name, ["value"] = value.ToString(), ["variablesReference"] = 0 };
        }
    }

    private static IEnumerable<(string Name, LuaValue Value)> EnumerateUpvalues(LuaClosure closure)
    {
        for (var index = 0; index < closure.Upvalues.Count; index++)
        {
            yield return ($"upvalue{index}", closure.Upvalues[index].Value);
        }
    }

    private static int EncodeVariablesReference(int frameId, int kind) => frameId * 2 + kind + 1000;

    private static (int FrameId, int Kind) DecodeVariablesReference(int reference)
    {
        var adjusted = reference - 1000;
        return (adjusted / 2, adjusted % 2);
    }

    private void Respond(int? id, JsonObject body)
    {
        if (id is not null)
        {
            _connection.WriteMessage(DapMessage.Response(id.Value, body));
        }
    }
}
