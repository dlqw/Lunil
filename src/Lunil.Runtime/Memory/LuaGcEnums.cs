namespace Lunil.Runtime.Memory;

public enum LuaGcColor : byte
{
    White,
    Gray,
    Black,
}

public enum LuaGcAge : byte
{
    New,
    Survival,
    Old0,
    Old1,
    Old,
}

public enum LuaGcMode : byte
{
    Incremental,
    Generational,
}

public enum LuaGcPhase : byte
{
    Paused,
    Propagate,
    Atomic,
    Sweep,
    Finalize,
}

public enum LuaGcCycleKind : byte
{
    Full,
    Minor,
}

public enum LuaGcFinalizationState : byte
{
    None,
    Pending,
    Finalized,
}

[Flags]
public enum LuaWeakMode : byte
{
    None = 0,
    Keys = 1 << 0,
    Values = 1 << 1,
}
