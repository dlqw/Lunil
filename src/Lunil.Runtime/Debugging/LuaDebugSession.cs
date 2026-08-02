using Lunil.Runtime.Execution;

namespace Lunil.Runtime;

/// <summary>Stepping granularity requested by a debugger for the paused thread.</summary>
public enum LuaDebugStepMode : byte
{
    None,
    Into,
    Over,
    Out,
}

/// <summary>
/// Host-side debugger session attached to a Lua state: the breakpoint set, pause requests, and
/// the step state machine apply to every thread of the state (including coroutines). The
/// execution engine evaluates a debug checkpoint on every instruction; when the session requests
/// a pause it suspends the whole scheduling turn and reports <see cref="LuaVmSignal.Paused"/>.
/// Resume the root thread through <see cref="LuaInterpreter.Resume"/> to continue; coroutines
/// suspended by a debug pause are resumed by Lua-side <c>coroutine.resume</c>.
/// </summary>
public sealed class LuaDebugSession
{
    private readonly object _sync = new();
    private HashSet<int> _breakpointLines = new();
    private LuaDebugStepMode _stepMode;
    private int _stepTargetDepth;
    private int _skipLine;
    private int _lastPauseLine;
    private volatile bool _pauseRequested;
    private LuaState? _state;
    private LuaThread? _pausedThread;

    /// <summary>Gets whether the session is attached to a state.</summary>
    public bool IsAttached => _state is not null;

    /// <summary>The thread suspended by the most recent debug pause, or null.</summary>
    internal LuaThread? PausedThread => _pausedThread;

    /// <summary>The line of the most recent debug pause, or zero before the first pause.</summary>
    internal int PausedLine => _lastPauseLine;

    /// <summary>Attaches the session to a state. Detach any previous attachment first.</summary>
    public void Attach(LuaState state)
    {
        LunilGuard.NotNull(state);
        lock (_sync)
        {
            if (_state is not null)
            {
                throw new InvalidOperationException("The debug session is already attached.");
            }

            _state = state;
            state.DebugSession = this;
        }
    }

    /// <summary>Detaches the session, clears breakpoints and stepping, and releases the state.</summary>
    public void Detach()
    {
        lock (_sync)
        {
            if (_state is null)
            {
                return;
            }

            _state.DebugSession = null;
            _state = null;
            _pausedThread = null;
            _breakpointLines = new HashSet<int>();
            _stepMode = LuaDebugStepMode.None;
            _skipLine = 0;
            _lastPauseLine = 0;
            _pauseRequested = false;
        }
    }

    /// <summary>Replaces the breakpoint set. Lines equal to or below zero are ignored.</summary>
    public void SetBreakpoints(IEnumerable<int> lines)
    {
        LunilGuard.NotNull(lines);
        lock (_sync)
        {
            _breakpointLines = new HashSet<int>(lines.Where(static line => line > 0));
        }
    }

    /// <summary>Requests an asynchronous pause; the engine suspends at the next checkpoint.</summary>
    public void RequestPause() => _pauseRequested = true;

    /// <summary>Continues execution; the paused line is not reported again.</summary>
    public void Continue()
    {
        lock (_sync)
        {
            _stepMode = LuaDebugStepMode.None;
            _skipLine = _lastPauseLine;
        }
    }

    /// <summary>Steps into the next line of the suspended thread.</summary>
    public void StepInto()
    {
        lock (_sync)
        {
            _stepMode = LuaDebugStepMode.Into;
            _skipLine = _lastPauseLine;
        }
    }

    /// <summary>Steps over to the next line at or above the current call depth.</summary>
    public void StepOver()
    {
        lock (_sync)
        {
            _stepMode = LuaDebugStepMode.Over;
            _stepTargetDepth = _pausedThread?.FrameCount ?? 0;
            _skipLine = _lastPauseLine;
        }
    }

    /// <summary>Steps out of the current function; pauses on return to the caller frame.</summary>
    public void StepOut()
    {
        lock (_sync)
        {
            _stepMode = LuaDebugStepMode.Out;
            _stepTargetDepth = Math.Max(0, (_pausedThread?.FrameCount ?? 1) - 1);
            _skipLine = _lastPauseLine;
        }
    }

    /// <summary>
    /// Evaluated by the execution engine at every instruction of every thread. Returns true when
    /// the turn should be suspended for the debugger.
    /// </summary>
    internal bool EvaluatePause(LuaThread thread, int line)
    {
        var frameDepth = thread.FrameCount;
        if (_pauseRequested)
        {
            _pauseRequested = false;
            _stepMode = LuaDebugStepMode.None;
            _pausedThread = thread;
            _lastPauseLine = line;
            return true;
        }

        // Continue and step requests skip the remaining instructions of the paused line so the
        // same breakpoint line is not reported again immediately.
        if (line > 0 && line == _skipLine)
        {
            return false;
        }

        _skipLine = 0;
        if (line > 0 && _breakpointLines.Contains(line))
        {
            _stepMode = LuaDebugStepMode.None;
            _pausedThread = thread;
            _lastPauseLine = line;
            return true;
        }

        switch (_stepMode)
        {
            case LuaDebugStepMode.Into:
                if (line > 0)
                {
                    _stepMode = LuaDebugStepMode.None;
                    _pausedThread = thread;
                    _lastPauseLine = line;
                    return true;
                }

                break;
            case LuaDebugStepMode.Over:
            case LuaDebugStepMode.Out:
                if (line > 0 && frameDepth <= _stepTargetDepth)
                {
                    _stepMode = LuaDebugStepMode.None;
                    _pausedThread = thread;
                    _lastPauseLine = line;
                    return true;
                }

                break;
        }

        return false;
    }
}
