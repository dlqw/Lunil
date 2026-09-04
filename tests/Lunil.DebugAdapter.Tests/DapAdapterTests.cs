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
    public void BreakpointsSentWithAbsoluteSourcePathsStillHit()
    {
        var script = CreateScript(
            """
            local a = 1       -- 2
            return a          -- 3
            """);
        Initialize();
        Launch(script);
        SetBreakpoints([2], script);
        ConfigurationDone();

        var stopped = WaitForEvent("stopped");
        Assert.Equal(2, (int?)stopped?["line"]);

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

    [Fact]
    public void StackAndVariableRequestsFailWhileRunning()
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
        var stack = Request("stackTrace", new JsonObject { ["threadId"] = 1 });
        Assert.False((bool?)stack?["success"]);
        Assert.NotNull((string?)stack?["message"]);

        var variables = Request("variables", new JsonObject { ["variablesReference"] = 1 });
        Assert.False((bool?)variables?["success"]);
        Assert.NotNull((string?)variables?["message"]);

        Continue();
        WaitForEvent("terminated");
    }

    [Fact]
    public void SetBreakpointsSnapToExecutableLinesAndRejectUnverifiableLines()
    {
        var script = CreateScript(
            """
            local value = 40
            -- comment only
            return value
            """);
        Initialize();
        Launch(script);
        var response = SetBreakpoints([1, 2, 30], script);
        var breakpoints = (JsonArray?)response?["body"]?["breakpoints"];
        Assert.NotNull(breakpoints);
        Assert.Equal(3, breakpoints!.Count);

        // An instruction line stays verified at its requested line.
        Assert.True((bool?)breakpoints[0]?["verified"]);
        Assert.Equal(1, (int?)breakpoints[0]?["line"]);

        // A comment line snaps forward to the next executable line.
        Assert.True((bool?)breakpoints[1]?["verified"]);
        Assert.Equal(3, (int?)breakpoints[1]?["line"]);

        // A line beyond the program cannot map to any executable line.
        Assert.False((bool?)breakpoints[2]?["verified"]);
        Assert.Equal(30, (int?)breakpoints[2]?["line"]);
        Assert.NotNull((string?)breakpoints[2]?["message"]);

        ConfigurationDone();
        var stopped = WaitForEvent("stopped");
        Assert.Equal(1, (int?)stopped?["line"]);

        // The snapped breakpoint on line 3 would stop the return; clear before resuming.
        SetBreakpoints([], script);
        Continue();
        WaitForEvent("terminated");
    }

    [Fact]
    public void TablesExpandAndReferencesExpireAfterResume()
    {
        var script = CreateScript(
            """
            local base = 5
            local items = { 10, 20, name = "lunil" }
            local function add(value)
              return value + base
            end
            local marker = add(items[1])
            return marker
            """);
        Initialize();
        Launch(script);
        SetBreakpoints([4]);
        ConfigurationDone();

        WaitForEvent("stopped");

        // Inside `add` the upvalue scope lists the closure upvalue by its real name; the
        // frame's locals must not leak into it.
        var upvalueScopes = Request("scopes", new JsonObject { ["frameId"] = 0 });
        var upvalueScopesArray = (JsonArray?)upvalueScopes?["body"]?["scopes"];
        Assert.NotNull(upvalueScopesArray);
        var upvalueReference = (int?)upvalueScopesArray![1]?["variablesReference"];
        Assert.True(upvalueReference > 0);
        var upvalues = Request("variables", new JsonObject { ["variablesReference"] = upvalueReference });
        var upvalueArray = (JsonArray?)upvalues?["body"]?["variables"];
        Assert.NotNull(upvalueArray);
        var baseUpvalue = upvalueArray!.FirstOrDefault(item =>
            item?["name"]?.GetValue<string>() == "base");
        Assert.NotNull(baseUpvalue);
        Assert.Equal("5", (string?)baseUpvalue?["value"]);
        Assert.DoesNotContain(upvalueArray, item => item?["name"]?.GetValue<string>() == "value");

        // The caller frame's `items` table expands: array part first, then the hash part.
        var callerScopes = Request("scopes", new JsonObject { ["frameId"] = 1 });
        var callerScopesArray = (JsonArray?)callerScopes?["body"]?["scopes"];
        Assert.NotNull(callerScopesArray);
        var localsReference = (int?)callerScopesArray![0]?["variablesReference"];
        Assert.True(localsReference > 0);
        var locals = Request("variables", new JsonObject { ["variablesReference"] = localsReference });
        var localsArray = (JsonArray?)locals?["body"]?["variables"];
        Assert.NotNull(localsArray);
        var tableVariable = localsArray!.FirstOrDefault(item =>
            item?["name"]?.GetValue<string>() == "items");
        Assert.NotNull(tableVariable);
        var tableReference = (int?)tableVariable?["variablesReference"];
        Assert.True(tableReference > 0);

        var entries = Request("variables", new JsonObject { ["variablesReference"] = tableReference });
        var entryArray = (JsonArray?)entries?["body"]?["variables"];
        Assert.NotNull(entryArray);
        Assert.Equal(3, entryArray!.Count);
        Assert.Equal("1", (string?)entryArray[0]?["name"]);
        Assert.Equal("10", (string?)entryArray[0]?["value"]);
        Assert.Equal("2", (string?)entryArray[1]?["name"]);
        Assert.Equal("[\"name\"]", (string?)entryArray[2]?["name"]);
        Assert.Equal("\"lunil\"", (string?)entryArray[2]?["value"]);

        // Pagination reads a window of the entries instead of restarting the listing.
        var page = Request("variables", new JsonObject
        {
            ["variablesReference"] = tableReference,
            ["start"] = 1,
            ["count"] = 1,
        });
        var pageArray = (JsonArray?)page?["body"]?["variables"];
        Assert.NotNull(pageArray);
        Assert.Single(pageArray!);
        Assert.Equal("2", (string?)pageArray[0]?["name"]);

        Continue();
        WaitForEvent("terminated");

        // The resume invalidated every reference handed out while paused.
        var stale = Request("variables", new JsonObject { ["variablesReference"] = tableReference });
        Assert.False((bool?)stale?["success"]);
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

    private JsonObject? SetBreakpoints(int[] lines, string? path = null)
    {
        var breakpoints = new JsonArray();
        foreach (var line in lines)
        {
            breakpoints.Add(new JsonObject { ["line"] = line });
        }

        var response = Request("setBreakpoints", new JsonObject
        {
            ["source"] = new JsonObject { ["path"] = path ?? _launchedFileName },
            ["breakpoints"] = breakpoints,
        });
        Assert.True((bool?)response?["success"]);
        return response;
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
