using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Lunil.DebugAdapter;

namespace Lunil.DebugAdapter.Tests;

/// <summary>
/// Protocol-level DAP integration tests: start the adapter as a subprocess over stdio and drive
/// a launch session (breakpoints, stepping, pause, stack, and variables) end to end.
/// </summary>
public sealed class DapAdapterTests : IDisposable
{
    private readonly Process _process;
    private readonly DapConnection _connection;
    private readonly List<string> _events = [];
    private int _sequence;

    public DapAdapterTests()
    {
        var adapterAssembly = typeof(Program).Assembly.Location;
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{adapterAssembly}\" --stdio",
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

    private readonly List<string> _stderr = [];

    public void Dispose()
    {
        _connection.Dispose();
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }

        _process.Dispose();
    }

    [Fact]
    public void BreakpointHitStopsAndReportsStackAndVariables()
    {
        var script = CreateScript(
            """
            local value = 40          -- 2
            local total = value + 2   -- 3
            return total              -- 4
            """);
        Initialize();
        Launch(script);
        SetBreakpoints([3]);
        ConfigurationDone();

        var stopped = WaitForEvent("stopped");
        Assert.Equal("breakpoint", (string?)stopped?["reason"]);
        Assert.Equal(3, (int?)stopped?["line"]);

        var stack = Request("stackTrace", new JsonObject { ["threadId"] = 1 });
        var frames = (JsonArray?)stack?["body"]?["stackFrames"];
        Assert.NotNull(frames);
        Assert.True(frames!.Count >= 1);

        var scopes = Request("scopes", new JsonObject { ["frameId"] = 0 });
        var scopesArray = (JsonArray?)scopes?["body"]?["scopes"];
        Assert.NotNull(scopesArray);
        Assert.True(scopesArray!.Count >= 1);
        var localsReference = (int?)scopesArray[0]?["variablesReference"];
        Assert.NotNull(localsReference);

        var variables = Request("variables", new JsonObject { ["variablesReference"] = localsReference });
        var variablesArray = (JsonArray?)variables?["body"]?["variables"];
        Assert.NotNull(variablesArray);
        var names = variablesArray!
            .Select(item => (string?)item?["name"])
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("value", names);
        Assert.Contains("total", names);

        Continue();
        WaitForEvent("terminated");
    }

    [Fact]
    public void StepCommandsMoveThroughLines()
    {
        var script = CreateScript(
            """
            local a = 1       -- 2
            local b = a + 1   -- 3
            local c = b + 1   -- 4
            return c          -- 5
            """);
        Initialize();
        Launch(script);
        SetBreakpoints([2]);
        ConfigurationDone();

        var stopped = WaitForEvent("stopped");
        Assert.Equal(2, (int?)stopped?["line"]);

        Step("stepIn");
        var stepped = WaitForEvent("stopped");
        Assert.True((int?)stepped?["line"] > 2, "Step must move past the breakpoint line.");

        Step("next");
        var over = WaitForEvent("stopped");
        Assert.True((int?)over?["line"] > (int?)stepped?["line"]);

        Continue();
        WaitForEvent("terminated");
    }

    [Fact]
    public void PauseRequestStopsRunningScript()
    {
        var script = CreateScript(
            """
            local total = 0            -- 2
            for i = 1, 1000000 do      -- 3
              total = total + i        -- 4
            end                        -- 5
            return total               -- 6
            """);
        Initialize();
        Launch(script);
        ConfigurationDone();

        Thread.Sleep(300);
        var response = Request("pause", new JsonObject { ["threadId"] = 1 });
        Assert.True((bool?)response?["success"]);

        var stopped = WaitForEvent("stopped");
        Assert.Equal("pause", (string?)stopped?["reason"]);

        Continue();
        WaitForEvent("terminated");
    }

    [Fact]
    public void CompilationFailureReportsOutputAndTerminates()
    {
        var script = CreateScript("local = broken syntax");
        Initialize();
        Launch(script);
        ConfigurationDone();

        var output = WaitForEvent("output");
        Assert.Equal("stderr", (string?)output?["category"]);
        WaitForEvent("terminated");
    }

    [Fact]
    public void DisconnectEndsTheSession()
    {
        Initialize();
        Request("disconnect", null);
        Thread.Sleep(200);
        Assert.True(_process.HasExited || _process.WaitForExit(2000));
    }

    private void Initialize()
    {
        var response = Request("initialize", new JsonObject
        {
            ["clientID"] = "lunil-tests",
            ["adapterID"] = "lunil",
        });
        Assert.True((bool?)response?["success"]);
        WaitForEvent("initialized");
    }

    private void Launch(string path)
    {
        _launchedFileName = Path.GetFileName(path);
        var response = Request("launch", new JsonObject
        {
            ["program"] = path,
        });
        Assert.True((bool?)response?["success"]);
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
            ["source"] = new JsonObject { ["path"] = _launchedFileName },
            ["breakpoints"] = breakpoints,
        });
        Assert.True((bool?)response?["success"]);
    }

    private void ConfigurationDone()
    {
        var response = Request("configurationDone", null);
        Assert.True((bool?)response?["success"]);
    }

    private void Continue() => Request("continue", new JsonObject { ["threadId"] = 1 });

    private void Step(string command) => Request(command, new JsonObject { ["threadId"] = 1 });

    private JsonObject? Request(string command, JsonNode? arguments)
    {
        var id = ++_sequence;
        var request = new DapMessage(DapMessageKind.Request, command, arguments, id);
        _connection.WriteMessage(request);

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
                if (message.Method == "stopped" || message.Method == "terminated" ||
                    message.Method == "output" || message.Method == "initialized")
                {
                    // Events consumed by the tests are buffered for WaitForEvent; unsolicited
                    // events are retained so they are not lost before the caller waits.
                    _pendingEvents.Enqueue(message);
                    continue;
                }

                continue;
            }

            if (message.Kind == DapMessageKind.Response && message.Id == id)
            {
                return (JsonObject?)message.Body;
            }
        }
    }

    private readonly Queue<DapMessage> _pendingEvents = new();
    private string? _launchedFileName;

    private JsonObject? WaitForEvent(string name)
    {
        // First drain any event already buffered by Request.
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

    private static string CreateScript(string source)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lunil-dap-{Guid.NewGuid():N}.lua");
        File.WriteAllText(path, source);
        return path;
    }
}
