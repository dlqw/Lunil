using Luac.Runtime.Values;

namespace Luac.Runtime.Execution;

public enum LuaThreadStatus : byte
{
    Suspended,
    Running,
    Dead,
    Error,
}

/// <summary>Owns the persistent Lua stack, logical frames, and identity map for open upvalues.</summary>
public sealed class LuaThread
{
    private readonly List<LuaFrame> _frames = [];
    private readonly SortedDictionary<int, LuaUpvalue> _openUpvalues = [];

    public LuaThread(int initialStackCapacity = 128)
    {
        Stack = new LuaStack(initialStackCapacity);
    }

    public LuaStack Stack { get; }

    public LuaThreadStatus Status { get; internal set; } = LuaThreadStatus.Suspended;

    public IReadOnlyList<LuaFrame> Frames => _frames;

    internal LuaFrame CurrentFrame => _frames[^1];

    internal int FrameCount => _frames.Count;

    internal void PushFrame(LuaFrame frame) => _frames.Add(frame);

    internal LuaFrame PopFrame()
    {
        var frame = _frames[^1];
        _frames.RemoveAt(_frames.Count - 1);
        return frame;
    }

    internal LuaUpvalue GetOrCreateOpenUpvalue(int stackIndex)
    {
        if (_openUpvalues.TryGetValue(stackIndex, out var upvalue))
        {
            return upvalue;
        }

        upvalue = new LuaUpvalue(this, stackIndex);
        _openUpvalues.Add(stackIndex, upvalue);
        return upvalue;
    }

    internal void CloseUpvalues(int fromStackIndex)
    {
        var keys = _openUpvalues.Keys.Where(key => key >= fromStackIndex).ToArray();
        foreach (var key in keys)
        {
            _openUpvalues[key].Close();
            _openUpvalues.Remove(key);
        }
    }

    internal void Reset()
    {
        CloseUpvalues(0);
        _frames.Clear();
        Status = LuaThreadStatus.Suspended;
    }
}
