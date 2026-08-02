using Lunil.Core.Text;
using Lunil.Runtime.Debugging;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.Runtime.Tests.Execution;

public sealed class LuaDebugSessionTests
{
    private const string Script =
        """
        local function add(a, b)      -- 1
          return a + b                -- 2
        end                           -- 3
        local x = add(20, 22)         -- 4
        local y = add(x, 1)           -- 5
        return y                      -- 6
        """;

    [Fact]
    public void BreakpointHitPausesAndResumeCompletes()
    {
        var state = new LuaState();
        var thread = state.CreateThread(Compile(state, Script));
        var session = new LuaDebugSession();
        session.Attach(state);
        session.SetBreakpoints([4]);
        var interpreter = new LuaInterpreter();

        var paused = interpreter.Start(state, thread);
        Assert.Equal(LuaVmSignal.Paused, paused.Signal);
        Assert.Equal(4, CurrentLine(state, thread));

        session.Continue();
        var completed = interpreter.Resume(state, thread);
        Assert.Equal(LuaVmSignal.Completed, completed.Signal);
        Assert.Equal(43, completed.Values[0].AsInteger());
        session.Detach();
    }

    [Fact]
    public void StepIntoStopsAtNextLine()
    {
        var state = new LuaState();
        var thread = state.CreateThread(Compile(state, Script));
        var session = new LuaDebugSession();
        session.Attach(state);
        session.SetBreakpoints([4]);
        var interpreter = new LuaInterpreter();

        var paused = interpreter.Start(state, thread);
        Assert.Equal(LuaVmSignal.Paused, paused.Signal);
        Assert.Equal(4, CurrentLine(state, thread));

        session.StepInto();
        var stepped = interpreter.Resume(state, thread);
        Assert.Equal(LuaVmSignal.Paused, stepped.Signal);
        Assert.Equal(2, CurrentLine(state, thread));
        session.Detach();
    }

    [Fact]
    public void StepOverSkipsCalleeBody()
    {
        var state = new LuaState();
        var thread = state.CreateThread(Compile(state, Script));
        var session = new LuaDebugSession();
        session.Attach(state);
        session.SetBreakpoints([4]);
        var interpreter = new LuaInterpreter();

        _ = interpreter.Start(state, thread);
        Assert.Equal(4, CurrentLine(state, thread));
        var depthAtBreakpoint = thread.FrameCount;

        session.StepOver();
        var paused = interpreter.Resume(state, thread);
        Assert.Equal(LuaVmSignal.Paused, paused.Signal);
        // Stepping over the add(20, 22) call pauses at the next line of the caller frame.
        Assert.Equal(5, CurrentLine(state, thread));
        Assert.Equal(depthAtBreakpoint, thread.FrameCount);
        session.Detach();
    }

    [Fact]
    public void StepOutReturnsToCaller()
    {
        var state = new LuaState();
        var thread = state.CreateThread(Compile(state, Script));
        var session = new LuaDebugSession();
        session.Attach(state);
        session.SetBreakpoints([2]);
        var interpreter = new LuaInterpreter();

        var paused = interpreter.Start(state, thread);
        Assert.Equal(LuaVmSignal.Paused, paused.Signal);
        Assert.Equal(2, CurrentLine(state, thread));
        var depthInsideCallee = thread.FrameCount;

        session.StepOut();
        var returned = interpreter.Resume(state, thread);
        Assert.Equal(LuaVmSignal.Paused, returned.Signal);
        Assert.True(thread.FrameCount < depthInsideCallee, "StepOut must return to a caller frame.");
        session.Detach();
    }

    [Fact]
    public void RequestPauseSuspendsAtNextCheckpoint()
    {
        var state = new LuaState();
        var thread = state.CreateThread(Compile(state, Script));
        var session = new LuaDebugSession();
        session.Attach(state);
        var interpreter = new LuaInterpreter();

        session.RequestPause();
        var paused = interpreter.Start(state, thread);
        Assert.Equal(LuaVmSignal.Paused, paused.Signal);

        session.Continue();
        var completed = interpreter.Resume(state, thread);
        Assert.Equal(LuaVmSignal.Completed, completed.Signal);
        session.Detach();
    }

    [Fact]
    public void BreakpointInsideCoroutinePausesTurnAndResumesThroughLua()
    {
        var state = new LuaState();
        state.InstallCoroutineModule();
        var source =
            """
            local co = coroutine.create(function()
              local total = 0               -- 2
              for i = 1, 3 do               -- 3
                total = total + i           -- 4
              end                           -- 5
              return total                  -- 6
            end)                            -- 7
            local ok, value = coroutine.resume(co)
            return ok, value
            """;
        var thread = state.CreateThread(Compile(state, source));
        var session = new LuaDebugSession();
        session.Attach(state);
        session.SetBreakpoints([2]);
        var interpreter = new LuaInterpreter();

        var paused = interpreter.Start(state, thread);
        Assert.Equal(LuaVmSignal.Paused, paused.Signal);
        // The breakpoint line belongs to the coroutine thread suspended by the pause.
        Assert.Equal(2, CurrentLine(state, session.PausedThread!));

        session.Continue();
        var completed = interpreter.ResumeDebugged(state, thread);
        Assert.Equal(LuaVmSignal.Completed, completed.Signal);
        Assert.Equal(LuaValue.FromBoolean(true), completed.Values[0]);
        Assert.Equal(LuaValue.FromInteger(6), completed.Values[1]);
        session.Detach();
    }

    [Fact]
    public void DetachStopsPausingAndCompletes()
    {
        var state = new LuaState();
        var thread = state.CreateThread(Compile(state, Script));
        var session = new LuaDebugSession();
        session.Attach(state);
        session.SetBreakpoints([4]);
        var interpreter = new LuaInterpreter();

        var paused = interpreter.Start(state, thread);
        Assert.Equal(LuaVmSignal.Paused, paused.Signal);

        session.Detach();
        var completed = interpreter.Resume(state, thread);
        Assert.Equal(LuaVmSignal.Completed, completed.Signal);
        Assert.Equal(43, completed.Values[0].AsInteger());
    }

    [Fact]
    public void NoBreakpointCompletesWithoutPausing()
    {
        var state = new LuaState();
        var thread = state.CreateThread(Compile(state, Script));
        var session = new LuaDebugSession();
        session.Attach(state);
        var interpreter = new LuaInterpreter();

        var completed = interpreter.Start(state, thread);
        Assert.Equal(LuaVmSignal.Completed, completed.Signal);
        Assert.Equal(43, completed.Values[0].AsInteger());
        session.Detach();
    }

    [Fact]
    public void AttachWhileAlreadyAttachedThrows()
    {
        var state = new LuaState();
        var session = new LuaDebugSession();
        session.Attach(state);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                session.Attach(new LuaState()));
        }
        finally
        {
            session.Detach();
        }
    }

    private static int CurrentLine(LuaState state, LuaThread thread) =>
        LuaDebugApi.GetCurrentLine(thread, LuaDebugApi.GetFrame(state, thread, 0)!);

    private static LuaClosure Compile(LuaState state, string source)
    {
        var lowering = LuaLowerer.Lower(
            LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source))));
        Assert.Empty(lowering.Diagnostics);
        return state.CreateMainClosure(lowering.Module!);
    }
}
