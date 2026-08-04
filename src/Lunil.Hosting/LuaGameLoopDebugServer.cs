using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Lunil.Runtime;
using Lunil.Runtime.Debugging;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.Hosting;

/// <summary>
/// Cross-process debug endpoint for a <see cref="LuaGameLoopHost"/>: serves the Debug Adapter
/// Protocol over a named pipe so an external debugger (through <c>lunil-debug-adapter --pipe</c>)
/// can set breakpoints, step, pause, and inspect stack frames of game-loop operations. Pause
/// events are raised by the game loop on its owner thread; resume commands are queued back into
/// the game loop so the tick stays the single execution driver. One client is served at a time;
/// after a disconnect the server accepts the next connection until disposed.
/// </summary>
public sealed class LuaGameLoopDebugServer : IDisposable
{
    private readonly LuaGameLoopHost _host;
    private readonly string _pipeName;
    private readonly LuaDebugSession _session = new();
    private readonly object _sync = new();
    private readonly Dictionary<string, HashSet<int>> _breakpoints = new(StringComparer.Ordinal);
    private Thread? _listener;
    private NamedPipeServerStream? _pipe;
    private LuaDapPipeConnection? _connection;
    private LuaGameLoopOperation? _pausedOperation;
    private string _stopReason = "breakpoint";
    private int _disposed;

    internal LuaGameLoopDebugServer(LuaGameLoopHost host, string pipeName)
    {
        _host = host;
        _pipeName = pipeName;
    }

    /// <summary>Gets whether the server has an active client connection.</summary>
    public bool IsConnected => _connection is not null;

    /// <summary>Gets the operation suspended by the most recent debug pause, if any.</summary>
    public LuaGameLoopOperation? PausedOperation
    {
        get
        {
            lock (_sync)
            {
                return _pausedOperation;
            }
        }
    }

    /// <summary>Gets the named pipe the server listens on.</summary>
    public string PipeName => _pipeName;

    /// <summary>Starts the pipe listener on a background thread.</summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_listener is not null)
            {
                throw new InvalidOperationException("The debug server is already started.");
            }

            _listener = new Thread(AcceptLoop)
            {
                Name = "lunil-debug-pipe",
                IsBackground = true,
            };
            _listener.Start();
        }
    }

    /// <summary>
    /// Stops the server, closes any client connection, and detaches the debug session from the
    /// host state. A paused operation stays suspended; detach does not resume it.
    /// </summary>
    public void Dispose()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _disposed, 1);
        lock (_sync)
        {
            _connection?.Dispose();
            _connection = null;
            _pipe?.Dispose();
            _pipe = null;
            _session.Detach();
            _pausedOperation = null;
        }
    }

    /// <summary>
    /// Raised by the game loop host on its owner thread when an operation turn suspends for the
    /// debugger. Publishes a DAP <c>stopped</c> event over the active connection; a missing or
    /// slow client never breaks the game loop.
    /// </summary>
    internal void OnDebugPause(LuaGameLoopOperation operation, int line)
    {
        lock (_sync)
        {
            _pausedOperation = operation;
            var connection = _connection;
            var reason = _stopReason == "pause" ? "pause" : _stopReason == "step" ? "step" : "breakpoint";
            _stopReason = "breakpoint";
            if (connection is null)
            {
                return;
            }

            try
            {
                connection.WriteMessage(LuaDapMessage.Event(
                    "stopped",
                    new JsonObject
                    {
                        ["reason"] = reason,
                        ["threadId"] = (int)operation.Id,
                        ["line"] = line,
                        ["source"] = new JsonObject { ["path"] = SourcePath(operation) },
                    }));
            }
            catch (Exception exception) when (exception is IOException or
                TimeoutException or InvalidOperationException or ObjectDisposedException or
                SocketException)
            {
                // The debugger is not a game-loop failure: drop the event and keep ticking.
            }
        }
    }

    private void AcceptLoop()
    {
        while (Volatile.Read(ref _disposed) == 0)
        {
            NamedPipeServerStream? pipe = null;
            LuaDapPipeConnection? connection = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    64 * 1024,
                    64 * 1024);
                lock (_sync)
                {
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        pipe.Dispose();
                        return;
                    }

                    _pipe = pipe;
                }

                pipe.WaitForConnection();
                lock (_sync)
                {
                    if (Volatile.Read(ref _disposed) != 0)
                    {
                        return;
                    }

                    _session.Attach(_host.Host.State);
                    connection = new LuaDapPipeConnection(pipe, pipe);
                    _connection = connection;
                }

                ServeConnection(connection);
            }
            catch (Exception exception) when (exception is IOException or
                InvalidOperationException or UnauthorizedAccessException or ObjectDisposedException or
                SocketException)
            {
                // Client dropped or the pipe was closed while waiting (including dispose; Unix
                // named pipes surface this as a canceled socket operation); back off briefly so
                // the released pipe name is available before the next accept.
                _ = exception;
                Thread.Sleep(100);
            }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_connection, connection))
                    {
                        _session.Detach();
                        _connection = null;
                        if (_pausedOperation is { } paused)
                        {
                            // Detach leaves the game loop runnable: resume the paused turn so the
                            // host keeps executing without a debugger attached.
                            _pausedOperation = null;
                            _host.QueueDebugResume(paused);
                        }
                    }

                    _pipe = null;
                }

                connection?.Dispose();
                pipe?.Dispose();
            }
        }
    }

    private void ServeConnection(LuaDapPipeConnection connection)
    {
        try
        {
            while (true)
            {
                var message = connection.ReadMessage();
                if (message is null)
                {
                    return;
                }

                if (message.Kind != LuaDapMessageKind.Request)
                {
                    continue;
                }

                HandleRequest(connection, message);
            }
        }
        catch (Exception exception) when (exception is IOException or
            InvalidDataException or SocketException)
        {
            // The client connection ended; the accept loop starts over.
        }
    }

    private void HandleRequest(LuaDapPipeConnection connection, LuaDapMessage message)
    {
        try
        {
            switch (message.Method)
            {
                case "initialize":
                    Respond(connection, message.Id, new JsonObject
                    {
                        ["success"] = true,
                        ["body"] = new JsonObject
                        {
                            ["supportsConfigurationDoneRequest"] = true,
                            ["supportsSteppingGranularity"] = false,
                            ["supportsTerminateRequest"] = false,
                        },
                    });
                    connection.WriteMessage(LuaDapMessage.Event("initialized"));
                    break;
                case "attach":
                    // The server is already attached to the host state; the request is a formality.
                    Respond(connection, message.Id, new JsonObject { ["success"] = true });
                    break;
                case "setBreakpoints":
                    HandleSetBreakpoints(connection, message);
                    break;
                case "configurationDone":
                    Respond(connection, message.Id, new JsonObject { ["success"] = true });
                    break;
                case "continue":
                    Resume(LuaDebugStepMode.None);
                    Respond(connection, message.Id, new JsonObject { ["success"] = true });
                    break;
                case "next":
                    Resume(LuaDebugStepMode.Over);
                    Respond(connection, message.Id, new JsonObject { ["success"] = true });
                    break;
                case "stepIn":
                    Resume(LuaDebugStepMode.Into);
                    Respond(connection, message.Id, new JsonObject { ["success"] = true });
                    break;
                case "stepOut":
                    Resume(LuaDebugStepMode.Out);
                    Respond(connection, message.Id, new JsonObject { ["success"] = true });
                    break;
                case "pause":
                    _session.RequestPause();
                    _stopReason = "pause";
                    Respond(connection, message.Id, new JsonObject { ["success"] = true });
                    break;
                case "stackTrace":
                    Respond(connection, message.Id, HandleStackTrace(message.Body));
                    break;
                case "scopes":
                    Respond(connection, message.Id, HandleScopes(message.Body));
                    break;
                case "variables":
                    Respond(connection, message.Id, HandleVariables(message.Body));
                    break;
                case "threads":
                    Respond(connection, message.Id, HandleThreads());
                    break;
                case "disconnect":
                    Respond(connection, message.Id, new JsonObject { ["success"] = true });
                    break;
                default:
                    Respond(connection, message.Id, new JsonObject
                    {
                        ["success"] = false,
                        ["message"] = $"Unsupported DAP command: {message.Method}",
                    });
                    break;
            }
        }
        catch (Exception exception)
        {
            Respond(connection, message.Id, new JsonObject
            {
                ["success"] = false,
                ["message"] = exception.Message,
            });
        }
    }

    private void Resume(LuaDebugStepMode step)
    {
        lock (_sync)
        {
            _stopReason = step == LuaDebugStepMode.None ? "breakpoint" : "step";
            var paused = _pausedOperation;
            _pausedOperation = null;
            switch (step)
            {
                case LuaDebugStepMode.Into:
                    _session.StepInto();
                    break;
                case LuaDebugStepMode.Over:
                    _session.StepOver();
                    break;
                case LuaDebugStepMode.Out:
                    _session.StepOut();
                    break;
                default:
                    _session.Continue();
                    break;
            }

            if (paused is not null)
            {
                _host.QueueDebugResume(paused);
            }
        }
    }

    private void HandleSetBreakpoints(LuaDapPipeConnection connection, LuaDapMessage message)
    {
        var path = (string?)message.Body?["source"]?["path"];
        var lines = new HashSet<int>();
        if (message.Body?["breakpoints"] is JsonArray breakpoints)
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

        lock (_sync)
        {
            if (path is not null)
            {
                _breakpoints[path] = lines;
            }

            _session.SetBreakpoints(_breakpoints.Values.SelectMany(static set => set));
        }

        var verified = new JsonArray();
        foreach (var line in lines.OrderBy(static item => item))
        {
            verified.Add(new JsonObject
            {
                ["verified"] = true,
                ["line"] = line,
            });
        }

        Respond(connection, message.Id, new JsonObject
        {
            ["success"] = true,
            ["body"] = new JsonObject { ["breakpoints"] = verified },
        });
    }

    private JsonObject HandleStackTrace(JsonNode? body)
    {
        _ = (int?)body?["threadId"] ?? 0;
        var frames = new JsonArray();
        var level = 0;
        if (PausedOperation is { } paused)
        {
            while (LuaDebugApi.GetFrame(_host.Host.State, paused.Thread, level) is { } frame)
            {
                frames.Add(new JsonObject
                {
                    ["id"] = level,
                    ["name"] = GetFrameName(frame),
                    ["line"] = Math.Max(LuaDebugApi.GetCurrentLine(paused.Thread, frame), 1),
                    ["column"] = 1,
                    ["source"] = new JsonObject { ["path"] = SourcePath(paused) },
                });
                level++;
            }
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
        if (PausedOperation is { } paused &&
            LuaDebugApi.GetFrame(_host.Host.State, paused.Thread, frameId) is { } frame)
        {
            var index = 1;
            while (LuaDebugApi.GetLocal(paused.Thread, frame, index) is { } local)
            {
                var name = string.IsNullOrEmpty(local.Name) ? $"local{index}" : local.Name;
                variables.Add(FormatValue(name, local.Value));
                index++;
            }

            if (kind == 1)
            {
                for (var upvalue = 0; upvalue < frame.Closure.Upvalues.Count; upvalue++)
                {
                    variables.Add(FormatValue($"upvalue{upvalue}", frame.Closure.Upvalues[upvalue].Value));
                }
            }
        }

        return new JsonObject
        {
            ["success"] = true,
            ["body"] = new JsonObject { ["variables"] = variables },
        };
    }

    private JsonObject HandleThreads()
    {
        var threads = new JsonArray();
        foreach (var operation in _host.SnapshotOperations())
        {
            threads.Add(new JsonObject
            {
                ["id"] = (int)operation.Id,
                ["name"] = operation.Status == LuaGameLoopOperationStatus.Suspended
                    ? $"operation {operation.Id} (suspended)"
                    : $"operation {operation.Id}",
            });
        }

        return new JsonObject
        {
            ["success"] = true,
            ["body"] = new JsonObject { ["threads"] = threads },
        };
    }

    private string SourcePath(LuaGameLoopOperation operation) =>
        LuaDebugApi.GetFrame(_host.Host.State, operation.Thread, 0) is { } frame &&
            LuaDebugApi.GetFunction(frame).TryGetClosure() is { } closure
            ? System.Text.Encoding.UTF8.GetString(closure.Function.SourceName.AsSpan())
            : _pipeName;

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

    private static int EncodeVariablesReference(int frameId, int kind) => frameId * 2 + kind + 1000;

    private static (int FrameId, int Kind) DecodeVariablesReference(int reference)
    {
        var adjusted = reference - 1000;
        return (adjusted / 2, adjusted % 2);
    }

    private static void Respond(LuaDapPipeConnection connection, int? id, JsonObject body)
    {
        if (id is not null)
        {
            connection.WriteMessage(LuaDapMessage.Response(id.Value, body));
        }
    }
}
