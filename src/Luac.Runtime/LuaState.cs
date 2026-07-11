using System.Text;
using Luac.IR.Canonical;
using Luac.Runtime.Execution;
using Luac.Runtime.Values;

namespace Luac.Runtime;

public sealed class LuaState
{
    public LuaState()
    {
        Strings = new LuaStringPool();
        Globals = new LuaTable();
        MainThread = new LuaThread();
    }

    public LuaStringPool Strings { get; }

    public LuaTable Globals { get; }

    public LuaThread MainThread { get; }

    public void SetGlobal(string name, LuaValue value)
    {
        ArgumentNullException.ThrowIfNull(name);
        Globals.Set(
            LuaValue.FromString(Strings.GetOrCreate(Encoding.UTF8.GetBytes(name))),
            value);
    }

    public LuaValue GetGlobal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Globals.Get(LuaValue.FromString(
            Strings.GetOrCreate(Encoding.UTF8.GetBytes(name))));
    }

    public LuaClosure CreateMainClosure(LuaIrModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        var function = module.Functions[module.MainFunctionId];
        return new LuaClosure(
            module,
            function,
            [new LuaUpvalue(LuaValue.FromTable(Globals))]);
    }
}
