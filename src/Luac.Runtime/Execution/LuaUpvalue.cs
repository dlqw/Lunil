using Luac.Runtime.Memory;
using Luac.Runtime.Values;

namespace Luac.Runtime.Execution;

/// <summary>An identity-bearing cell that transitions exactly once from an open stack slot to closed storage.</summary>
public sealed class LuaUpvalue : LuaGcObject
{
    private LuaThread? _thread;
    private int _stackIndex;
    private LuaValue _closedValue;

    internal LuaUpvalue(LuaHeap owner, LuaValue closedValue)
        : base(owner, 48)
    {
        owner.ValidateValue(closedValue);
        _closedValue = closedValue;
        _stackIndex = -1;
    }

    internal LuaUpvalue(LuaThread thread, int stackIndex)
        : base(thread.Owner, 48)
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
            Owner.ValidateValue(value);
            Owner.WriteBarrier(this, value);
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
        Owner.WriteBarrier(this, _closedValue);
        _thread = null;
        _stackIndex = -1;
    }

    internal override void Traverse(LuaGcVisitor visitor)
    {
        if (_thread is null)
        {
            visitor.Visit(_closedValue);
        }
        else
        {
            visitor.Visit(_thread);
        }
    }
}
