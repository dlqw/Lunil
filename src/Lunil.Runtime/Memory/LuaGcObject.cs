using Lunil.Runtime.Values;

namespace Lunil.Runtime.Memory;

/// <summary>Base for every object whose lifetime is controlled by the logical Lua heap.</summary>
public abstract class LuaGcObject
{
    protected LuaGcObject(LuaHeap owner, long logicalSize)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(logicalSize);
        Owner = owner;
        LogicalSize = logicalSize;
        ObjectId = owner.Register(this);
    }

    public LuaHeap Owner { get; }

    public long ObjectId { get; }

    public long LogicalSize { get; internal set; }

    public bool IsAlive { get; internal set; } = true;

    public LuaGcColor Color { get; internal set; } = LuaGcColor.White;

    public LuaGcAge Age { get; internal set; } = LuaGcAge.New;

    public LuaGcFinalizationState FinalizationState { get; internal set; }

    internal abstract void Traverse(LuaGcVisitor visitor);

    internal virtual bool TryGetFinalizer(out LuaValue finalizer)
    {
        finalizer = LuaValue.Nil;
        return false;
    }

    internal virtual void OnCollected()
    {
    }
}
