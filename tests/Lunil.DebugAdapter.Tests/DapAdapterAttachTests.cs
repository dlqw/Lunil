using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Lunil.Hosting;
using Lunil.Runtime.Execution;

namespace Lunil.DebugAdapter.Tests;

/// <summary>
/// Cross-process attach end to end: the adapter process connects to a game-loop host's debug
/// pipe (LuaGameLoopDebugServer) and relays DAP requests, responses, and events verbatim; the
/// host serves breakpoints, stepping, pause, and stack while its tick stays the execution driver.
/// </summary>
public sealed class DapAdapterAttachTests : IDisposable
{
    private readonly LuaGameLoopHost _game;
    private readonly Process _process;
    private readonly DapConnection _connection;
    private readonly List<string> _stderr = [];
    private readonly List<string> _events = [];
    private readonly Queue<DapMessage> _pendingEvents = new();
    private int _sequence;

    public DapAdapterAttachTests()
    {
        _game = new LuaGameLoopHost(new LuaGameLoopHostOptions
        {
            MaximumCallbacksPerTick = 32,
            MaximumInstructionsPerTick = 100_000,
            HostOptions = new LuaHostOptions
            {
                ExecutionBackend = LuaHostExecutionBackend.Interpreter,
                Execution = LuaInterpreterOptions.Default with
                {
                    MaximumInstructionCount = 100_000,
                },
            },
        });
        Server = _game.StartDebugServer("lunil-" + Guid.NewGuid().ToString("N")[..8]);

        var adapterAssembly = typeof(Program).Assembly.Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{adapterAssembly}\" --stdio --pipe {Server.PipeName}",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        _process = Process.Start(startInfo)!;
        _process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null)
            {
                _stderr.Add(args.Data);
            }
        };
        _process.BeginErrorReadLine();
        _connection = new DapConnection(_process.StandardOutput.BaseStream, _process.StandardInput.BaseStream);
    }

    private LuaGameLoopDebugServer Server { get; }

    public void Dispose()
    {
        _connection.Dispose();
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        _process.Dispose();
        Server.Dispose();
        _game.Dispose();
    }

    [Fact]
    public void AttachRelaysBreakpointsAndResumesThroughHostTicks()
    {
        var compilation = _game.Host.CompileUtf8(
            """
            local total = 0
            for i = 1, 3 do
              total = total + i
              coroutine.yield(i)
            end
            return total
            """);
        var operation = _game.Start(compilation);

        Initialize();
        SetBreakpoints([3]);
        ConfigurationDone();

        var paused = _game.Tick();
        Assert.True(paused.Succeeded, string.Join("; ", paused.Failures.Select(f => f.Message)));
        Assert.Equal(LuaGameLoopOperationStatus.Suspended, operation.Status);

        var stopped = WaitForEvent("stopped");
        Assert.Equal("breakpoint", (string?)stopped?["reason"]);
        Assert.Equal(3, (int?)stopped?["line"]);

        var threadId = (int?)stopped?["threadId"] ?? 1;
        var stack = Request("stackTrace", new JsonObject { ["threadId"] = threadId });
        var frames = (JsonArray?)stack?["body"]?["stackFrames"];
        Assert.NotNull(frames);
        Assert.True(frames!.Count >= 1);
        var frameId = (int?)frames[0]?["id"] ?? 0;
        var variables = Request("variables", new JsonObject { ["variablesReference"] = frameId * 2 + 1000 });
        var variablesArray = (JsonArray?)variables?["body"]?["variables"];
        Assert.NotNull(variablesArray);
        Assert.Contains(variablesArray!, static item => item?["name"]?.GetValue<string>() == "total");

        // The loop body re-enters line 3, so clear the breakpoint before continuing.
        SetBreakpoints([]);
        Request("continue", new JsonObject { ["threadId"] = threadId });
        while (operation.Status != LuaGameLoopOperationStatus.Completed)
        {
            Assert.True(_game.Tick().Succeeded);
        }

        Assert.Equal(6L, operation.Values[0].AsInteger());
        Request("disconnect", null);
    }

    [Fact]
    public void AttachRelaysPauseRequestOverThePipe()
    {
        var compilation = _game.Host.CompileUtf8(
            """
            local total = 0
            for i = 1, 10000 do
              total = total + i
            end
            return total
            """);
        var operation = _game.Start(compilation);

        Initialize();
        ConfigurationDone();
        Request("pause", new JsonObject { ["threadId"] = 1 });

        var paused = _game.Tick();
        Assert.True(paused.Succeeded, string.Join("; ", paused.Failures.Select(f => f.Message)));
        Assert.Equal(LuaGameLoopOperationStatus.Suspended, operation.Status);

        var stopped = WaitForEvent("stopped");
        Assert.Equal("pause", (string?)stopped?["reason"]);

        Request("continue", new JsonObject { ["threadId"] = (int?)stopped?["threadId"] ?? 1 });
        while (operation.Status != LuaGameLoopOperationStatus.Completed)
        {
            Assert.True(_game.Tick().Succeeded);
        }

        Assert.True(operation.Values[0].AsInteger() > 0);
        Request("disconnect", null);
    }

    private void Initialize()
    {
        var response = Request("initialize", new JsonObject
        {
            ["clientID"] = "lunil-attach-tests",
            ["adapterID"] = "lunil",
        });
        Assert.True((bool?)response?["success"]);
        WaitForEvent("initialized");
    }

    private void SetBreakpoints(int[] lines)
    {
        var breakpoints = new JsonArray();
        foreach (var line in lines)
        {
            breakpoints.Add(new JsonObject { ["line"] = line });
        }

        var response = Request("setBreakpoints", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = "test.lua" },
            ["breakpoints"] = breakpoints,
        });
        Assert.True((bool?)response?["success"]);
    }

    private void ConfigurationDone()
    {
        var response = Request("configurationDone", null);
        Assert.True((bool?)response?["success"]);
    }

    private JsonObject? Request(string command, JsonNode? arguments)
    {
        var id = ++_sequence;
        _connection.WriteMessage(new DapMessage(DapMessageKind.Request, command, arguments, id));
        while (true)
        {
            var message = _connection.ReadMessage();
            if (message is null)
            {
                throw new InvalidOperationException(
                    "The debug adapter closed unexpectedly. stderr: " + string.Join(" | ", _stderr));
            }

            if (message.Kind == DapMessageKind.Event)
            {
                _events.Add(message.Method ?? "<null>");
                if (message.Method is "stopped" or "terminated" or "initialized")
                {
                    _pendingEvents.Enqueue(message);
                }

                continue;
            }

            if (message.Kind == DapMessageKind.Response && message.Id == id)
            {
                return (JsonObject?)message.Body;
            }
        }
    }

    private JsonObject? WaitForEvent(string name)
    {
        while (_pendingEvents.Count > 0)
        {
            var pending = _pendingEvents.Dequeue();
            if (pending.Method == name)
            {
                return pending.Body as JsonObject;
            }
        }

        while (true)
        {
            var message = _connection.ReadMessage();
            if (message is null)
            {
                throw new InvalidOperationException(
                    "The debug adapter closed unexpectedly. stderr: " + string.Join(" | ", _stderr));
            }

            if (message.Kind == DapMessageKind.Event && message.Method == name)
            {
                return (JsonObject?)message.Body;
            }

            if (message.Kind == DapMessageKind.Event)
            {
                _pendingEvents.Enqueue(message);
            }
        }
    }
}
