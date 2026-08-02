using Lunil.Hosting;
using Lunil.Runtime.Execution;

var debugPipe = Array.IndexOf(args, "--debug-pipe") is var index && index >= 0 && index + 1 < args.Length
    ? args[index + 1]
    : null;

using var gameLoop = new LuaGameLoopHost(new LuaGameLoopHostOptions
{
    HostOptions = LuaHostOptions.Restricted with
    {
        ExecutionBackend = LuaHostExecutionBackend.Interpreter,
    },
});

// Optional cross-process debug endpoint: attach with
// lunil-debug-adapter --stdio --pipe <name> (VS Code: Lunil Lua attach configuration).
using var debugServer = debugPipe is null
    ? null
    : gameLoop.StartDebugServer(debugPipe);

var compilation = gameLoop.Host.CompileUtf8(
    "counter = 1; coroutine.yield(); counter = counter + 1; return counter",
    "@samples/portable/main.lua");
if (!compilation.Succeeded)
{
    throw new InvalidOperationException(string.Join(Environment.NewLine, compilation.Diagnostics));
}

var operation = gameLoop.Start(compilation);
var firstFrame = gameLoop.Tick();
var secondFrame = gameLoop.Tick();

if (!firstFrame.Succeeded || !secondFrame.Succeeded ||
    operation.Status != LuaGameLoopOperationStatus.Completed ||
    operation.Values.Length != 1 || operation.Values[0].AsInteger() != 2)
{
    throw new InvalidOperationException("The portable game-loop sample did not complete.");
}

Console.WriteLine(
    $"Lunil portable host completed in {gameLoop.FrameNumber} frames with " +
    $"{operation.Values[0].AsInteger()}.");
