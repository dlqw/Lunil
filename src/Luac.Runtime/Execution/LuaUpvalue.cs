using Luac.Runtime.Values;

namespace Luac.Runtime.Execution;

/// <summary>An identity-bearing cell that transitions exactly once from an open stack slot to closed storage.</summary>
public sealed class LuaUpvalue
{
    private LuaThread? _thread;
    private int _stackIndex;
    private LuaValue _closedValue;

    public LuaUpvalue(LuaValue closedValue)
    {
        _closedValue = closedValue;
        _stackIndex = -1;
    }

    internal LuaUpvalue(LuaThread thread, int stackIndex)
    {
        _thread = thread;
        _stackIndex = stackIndex;
    }

    public bool IsOpen => _thread is not null;

    public LuaValue Value
    {
        get => _thread is null ? _closedValue : _thread.Stack[_stackIndex];
        set
        {
            if (_thread is null)
            {
                _closedValue = value;
            }
            else
            {
                _thread.Stack[_stackIndex] = value;
            }
        }
    }

    internal void Close()
    {
        if (_thread is null)
        {
            return;
        }

        _closedValue = _thread.Stack[_stackIndex];
        _thread = null;
        _stackIndex = -1;
    }
}
