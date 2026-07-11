using Luac.Runtime.Values;

namespace Luac.Runtime.Memory;

/// <summary>A registered host root. Dispose releases the logical Lua reference deterministically.</summary>
public sealed class LuaHandle : IDisposable
{
    private LuaHeap? _heap;
    private readonly long _id;

    internal LuaHandle(LuaHeap heap, long id)
    {
        _heap = heap;
        _id = id;
    }

    public LuaValue Value
    {
        get => GetHeap().GetHandleValue(_id);
        set => GetHeap().SetHandleValue(_id, value);
    }

    public void Dispose()
    {
        var heap = Interlocked.Exchange(ref _heap, null);
        heap?.RemoveHandle(_id);
    }

    private LuaHeap GetHeap() => _heap ?? throw new ObjectDisposedException(nameof(LuaHandle));
}
