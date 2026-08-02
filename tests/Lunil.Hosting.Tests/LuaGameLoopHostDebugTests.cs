using Lunil.Runtime;
using Lunil.Runtime.Debugging;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.Hosting.Tests;

/// <summary>
/// Debugger attach semantics on the game loop host: a breakpoint suspends the operation turn
/// (LuaVmSignal.Paused becomes a suspended operation without breaking the loop contract), and
/// engine-level resume continues the coroutine to completion. Cross-process attach transport
/// and tick-driven debug resume are not part of the v1 attach surface.
/// </summary>
public sealed class LuaGameLoopHostDebugTests
{
    [Fact]
    public void BreakpointPausesGameLoopOperationAndResumesToCompletion()
    {
        using var game = CreateGameLoop();
        var compilation = game.Host.CompileUtf8(
            """
            local total = 0          -- 1
            for i = 1, 3 do          -- 2
              total = total + i      -- 3
              coroutine.yield(i)     -- 4
            end                      -- 5
            return total             -- 6
            """);
        var session = new LuaDebugSession();
        session.Attach(game.Host.State);
        session.SetBreakpoints([3]);

        var operation = game.Start(compilation);
        Assert.Equal(LuaGameLoopOperationStatus.Pending, operation.Status);

        // First tick hits the breakpoint inside the loop body and suspends the turn.
        var paused = game.Tick();
        Assert.True(paused.Succeeded, string.Join("; ", paused.Failures.Select(f => f.Message)));
        Assert.Equal(LuaGameLoopOperationStatus.Suspended, operation.Status);
        Assert.Equal(3, LuaDebugApi.GetCurrentLine(
            operation.Thread,
            LuaDebugApi.GetFrame(game.Host.State, operation.Thread, 0)!));

        // Clear the breakpoint, resume the debug pause, and drive the remaining yields to
        // completion at the engine level (v1 attach semantics).
        session.SetBreakpoints([]);
        session.Continue();
        var interpreter = new LuaInterpreter();
        var result = interpreter.ResumeDebugged(game.Host.State, operation.Thread);
        while (result.Signal == LuaVmSignal.Yielded)
        {
            result = interpreter.Resume(game.Host.State, operation.Thread);
        }

        Assert.Equal(LuaVmSignal.Completed, result.Signal);
        Assert.Equal(6L, result.Values[0].AsInteger());
        session.Detach();
    }

    [Fact]
    public void PauseRequestSuspendsAtNextCheckpointWithinGameLoop()
    {
        using var game = CreateGameLoop();
        var compilation = game.Host.CompileUtf8(
            """
            local total = 0
            for i = 1, 10000 do
              total = total + i
            end
            return total
            """);
        var session = new LuaDebugSession();
        session.Attach(game.Host.State);

        var operation = game.Start(compilation);
        session.RequestPause();
        var paused = game.Tick();

        Assert.True(paused.Succeeded, string.Join("; ", paused.Failures.Select(f => f.Message)));
        Assert.Equal(LuaGameLoopOperationStatus.Suspended, operation.Status);

        session.Continue();
        var interpreter = new LuaInterpreter();
        var result = interpreter.ResumeDebugged(game.Host.State, operation.Thread);
        while (result.Signal == LuaVmSignal.Yielded)
        {
            result = interpreter.Resume(game.Host.State, operation.Thread);
        }

        Assert.Equal(LuaVmSignal.Completed, result.Signal);
        Assert.True(result.Values[0].AsInteger() > 0);
        session.Detach();
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
}
