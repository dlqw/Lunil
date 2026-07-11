using Luac.Runtime.Values;

namespace Luac.Runtime.Execution;

public sealed class LuaFrame
{
    internal LuaFrame(
        LuaClosure closure,
        int @base,
        int top,
        int returnBase,
        int expectedResults,
        LuaValue[] varArgs)
    {
        Closure = closure;
        Base = @base;
        Top = top;
        ReturnBase = returnBase;
        ExpectedResults = expectedResults;
        VarArgs = varArgs;
    }

    public LuaClosure Closure { get; }

    public int Base { get; }

    public int Top { get; internal set; }

    public int ProgramCounter { get; internal set; }

    public int ReturnBase { get; }

    public int ExpectedResults { get; }

    public IReadOnlyList<LuaValue> VarArgs { get; }

    internal LuaValue[] VarArgStorage => (LuaValue[])VarArgs;

    internal List<int> ToBeClosedSlots { get; } = [];
}
