using Lunil.Runtime.Values;

namespace Lunil.Runtime.Memory;

internal sealed class LuaGcVisitor(LuaHeap heap)
{
    public void Visit(LuaValue value) => heap.MarkValue(value);

    public void Visit(LuaGcObject? value)
    {
        if (value is not null)
        {
            heap.MarkObject(value);
        }
    }

    public void VisitWeakTable(LuaTable table, LuaWeakMode mode) =>
        heap.RegisterWeakTable(table, mode);

    public bool IsUnreachable(LuaValue value) => heap.IsUnreachableInCurrentCycle(value);
}
