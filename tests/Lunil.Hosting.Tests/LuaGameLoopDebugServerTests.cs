using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Lunil.Runtime.Execution;

namespace Lunil.Hosting.Tests;

/// <summary>
/// Cross-process attach transport: the game loop host serves the DAP protocol over a named pipe;
/// breakpoints, pause, stepping, stack, and variable requests are answered by the host-side
/// server, and resumes are queued into the next tick so the game loop stays the execution driver.
/// </summary>
public sealed class LuaGameLoopDebugServerTests
{
    [Fact]
    public void BreakpointOverPipeSuspendsOperationAndResumesOnContinue()
    {
        using var game = CreateGameLoop();
        using var server = game.StartDebugServer(UniquePipeName());
        using var client = TestDapClient.Connect(server);

        client.Initialize();
        client.SetBreakpoints([3]);
        client.ConfigurationDone();

        var compilation = game.Host.CompileUtf8(
            """
            local total = 0          -- 1
            for i = 1, 3 do          -- 2
              total = total + i      -- 3
              coroutine.yield(i)     -- 4
            end                      -- 5
            return total             -- 6
            """);
        var operation = game.Start(compilation);

        var paused = game.Tick();
        Assert.True(paused.Succeeded, string.Join("; ", paused.Failures.Select(f => f.Message)));
        Assert.Equal(LuaGameLoopOperationStatus.Suspended, operation.Status);

        var stopped = client.ReadEvent("stopped");
        Assert.Equal("breakpoint", stopped?["reason"]?.GetValue<string>());
        Assert.Equal(3, stopped?["line"]?.GetValue<int>());

        var frames = client.StackTrace((int)stopped!["threadId"]!.GetValue<int>());
        Assert.True(frames!.Count > 0);
        var locals = client.Variables((int)frames[0]!["id"]!.GetValue<int>());
        Assert.Contains(locals!, static item => item?["name"]?.GetValue<string>() == "total");

        // The loop body re-enters line 3 on the next iteration, so clear the breakpoint before
        // continuing to let the operation run to completion.
        client.SetBreakpoints([]);
        client.Continue();
        while (operation.Status != LuaGameLoopOperationStatus.Completed)
        {
            Assert.True(game.Tick().Succeeded);
        }

        Assert.Equal(6L, operation.Values[0].AsInteger());
    }

    [Fact]
    public void PauseRequestOverPipeSuspendsWithPauseReason()
    {
        using var game = CreateGameLoop();
        using var server = game.StartDebugServer(UniquePipeName());
        using var client = TestDapClient.Connect(server);

        client.Initialize();
        client.ConfigurationDone();
        var compilation = game.Host.CompileUtf8(
            """
            local total = 0
            for i = 1, 10000 do
              total = total + i
            end
            return total
            """);
        var operation = game.Start(compilation);

        client.Pause();
        var paused = game.Tick();
        Assert.True(paused.Succeeded, string.Join("; ", paused.Failures.Select(f => f.Message)));
        Assert.Equal(LuaGameLoopOperationStatus.Suspended, operation.Status);

        var stopped = client.ReadEvent("stopped");
        Assert.Equal("pause", stopped?["reason"]?.GetValue<string>());

        client.Continue();
        while (operation.Status != LuaGameLoopOperationStatus.Completed)
        {
            Assert.True(game.Tick().Succeeded);
        }

        Assert.True(operation.Values[0].AsInteger() > 0);
    }

    [Fact]
    public void ClientDisconnectDetachesAndResumesPausedOperation()
    {
        using var game = CreateGameLoop();
        using var server = game.StartDebugServer(UniquePipeName());
        using var client = TestDapClient.Connect(server);

        client.Initialize();
        client.SetBreakpoints([3]);
        client.ConfigurationDone();
        var compilation = game.Host.CompileUtf8(
            """
            local total = 0
            for i = 1, 3 do
              total = total + i
              coroutine.yield(i)
            end
            return total
            """);
        var operation = game.Start(compilation);

        var paused = game.Tick();
        Assert.True(paused.Succeeded, string.Join("; ", paused.Failures.Select(f => f.Message)));
        Assert.Equal(LuaGameLoopOperationStatus.Suspended, operation.Status);
        Assert.NotNull(client.ReadEvent("stopped"));

        // Dropping the pipe detaches the debugger and resumes the paused turn so the game loop
        // keeps executing without a client attached.
        client.Disconnect();
        Thread.Sleep(500);
        while (operation.Status != LuaGameLoopOperationStatus.Completed)
        {
            Assert.True(game.Tick().Succeeded);
        }

        Assert.Equal(6L, operation.Values[0].AsInteger());
        Assert.Null(server.PausedOperation);
    }

    [Fact]
    public void JitBackendRejectsDebugServerStart()
    {
        using var game = new LuaGameLoopHost(new LuaGameLoopHostOptions
        {
            HostOptions = new LuaHostOptions
            {
                ExecutionBackend = LuaHostExecutionBackend.Jit,
            },
        });
        Assert.Throws<InvalidOperationException>(() => game.StartDebugServer(UniquePipeName()));
    }

    private static LuaGameLoopHost CreateGameLoop() => new(new LuaGameLoopHostOptions
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

    private static string UniquePipeName() => "lunil-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>A minimal DAP client over a named pipe for driving the host-side server.</summary>
    private sealed class TestDapClient : IDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly byte[] _header = new byte[64];
        private int _sequence;

        private TestDapClient(NamedPipeClientStream pipe) => _pipe = pipe;

        public static TestDapClient Connect(LuaGameLoopDebugServer server)
        {
            var pipe = new NamedPipeClientStream(
                ".",
                server.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            pipe.Connect(10_000);
            return new TestDapClient(pipe);
        }

        public void Initialize() =>
            WriteRequest("initialize", new JsonObject { ["clientID"] = "test" });

        public void SetBreakpoints(int[] lines) =>
            Request(
                "setBreakpoints",
                new JsonObject
                {
                    ["source"] = new JsonObject { ["path"] = "test.lua" },
                    ["breakpoints"] = new JsonArray(lines.Select(static line => new JsonObject
                    {
                        ["line"] = line,
                    }).ToArray()),
                });

        public void ConfigurationDone() => Request("configurationDone", null);

        public void Pause() => Request("pause", new JsonObject { ["threadId"] = 1 });

        public void Continue() => Request("continue", new JsonObject { ["threadId"] = 1 });

        public JsonArray? StackTrace(int threadId) =>
            (JsonArray?)Request("stackTrace", new JsonObject { ["threadId"] = threadId })?["body"]?["stackFrames"];

        public JsonArray? Variables(int frameId) =>
            (JsonArray?)Request("variables", new JsonObject { ["variablesReference"] = frameId * 2 + 1000 })?["body"]?["variables"];

        public JsonNode? ReadEvent(string name)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                var message = ReadMessage();
                if (message is null)
                {
                    return null;
                }

                if (message["type"]?.GetValue<string>() == "event" &&
                    message["event"]?.GetValue<string>() == name)
                {
                    return message["body"];
                }

                if (message["type"]?.GetValue<string>() == "response")
                {
                    _pendingResponses.Enqueue(message);
                }
            }

            throw new TimeoutException($"Timed out waiting for DAP event '{name}'.");
        }

        public void Disconnect() => _pipe.Dispose();

        public void Dispose() => _pipe.Dispose();

        private JsonNode? Request(string command, JsonNode? arguments)
        {
            var sequence = ++_sequence;
            WriteRequest(command, arguments, sequence);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                foreach (var response in _pendingResponses.ToArray())
                {
                    if (response["request_seq"]?.GetValue<int>() == sequence)
                    {
                        _pendingResponses.Dequeue();
                        return response;
                    }
                }

                var message = ReadMessage();
                if (message is null)
                {
                    return null;
                }

                if (message["type"]?.GetValue<string>() == "response")
                {
                    if (message["request_seq"]?.GetValue<int>() == sequence)
                    {
                        return message;
                    }

                    _pendingResponses.Enqueue(message);
                }
            }

            throw new TimeoutException($"Timed out waiting for DAP response to '{command}'.");
        }

        private readonly Queue<JsonNode> _pendingResponses = new();

        private void WriteRequest(string command, JsonNode? arguments, int? sequence = null)
        {
            var body = new JsonObject
            {
                ["seq"] = sequence ?? ++_sequence,
                ["type"] = "request",
                ["command"] = command,
            };
            if (arguments is not null)
            {
                body["arguments"] = arguments;
            }

            WriteFrame(body);
        }

        private JsonNode? ReadMessage()
        {
            var length = ReadFrameLength();
            if (length is null)
            {
                return null;
            }

            var buffer = new byte[length.Value];
            var offset = 0;
            while (offset < length.Value)
            {
                var read = _pipe.Read(buffer, offset, length.Value - offset);
                if (read == 0)
                {
                    throw new EndOfStreamException("The debug pipe closed mid-message.");
                }

                offset += read;
            }

            return JsonNode.Parse(buffer);
        }

        private void WriteFrame(JsonNode body)
        {
            var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());
            var header = Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n");
            _pipe.Write(header);
            _pipe.Write(bytes);
            _pipe.Flush();
        }

        private int? ReadFrameLength()
        {
            var offset = 0;
            while (true)
            {
                var read = _pipe.Read(_header, offset, 1);
                if (read == 0)
                {
                    return null;
                }

                offset++;
                if (offset >= 4 &&
                    _header[offset - 4] == (byte)'\r' &&
                    _header[offset - 3] == (byte)'\n' &&
                    _header[offset - 2] == (byte)'\r' &&
                    _header[offset - 1] == (byte)'\n')
                {
                    var headerText = Encoding.ASCII.GetString(_header, 0, offset - 4);
                    foreach (var line in headerText.Split('\n'))
                    {
                        var trimmed = line.Trim();
                        if (trimmed.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                            int.TryParse(trimmed.AsSpan("Content-Length:".Length).Trim(), out var length))
                        {
                            return length;
                        }
                    }

                    throw new InvalidDataException("DAP frame is missing Content-Length.");
                }
            }
        }
    }
}
