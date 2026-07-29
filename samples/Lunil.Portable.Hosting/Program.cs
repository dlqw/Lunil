using Lunil.Hosting;
using Lunil.Runtime.Execution;

using var gameLoop = new LuaGameLoopHost(new LuaGameLoopHostOptions
{
    HostOptions = LuaHostOptions.Restricted with
    {
        ExecutionBackend = LuaHostExecutionBackend.Interpreter,
    },
});

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
