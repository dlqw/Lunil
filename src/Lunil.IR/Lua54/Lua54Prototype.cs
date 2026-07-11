using System.Collections.Immutable;

namespace Lunil.IR.Lua54;

public readonly record struct Lua54UpvalueDescriptor(byte InStack, byte Index, byte Kind);

public readonly record struct Lua54AbsoluteLineInfo(int ProgramCounter, int Line);

public readonly record struct Lua54LocalVariable(
    Lua54String? Name,
    int StartProgramCounter,
    int EndProgramCounter);

/// <summary>An immutable logical representation of a PUC Lua 5.4 Proto.</summary>
public sealed record Lua54Prototype
{
    public Lua54String? Source { get; init; }

    public int LineDefined { get; init; }

    public int LastLineDefined { get; init; }

    public byte ParameterCount { get; init; }

    public byte VarArgFlags { get; init; }

    public byte MaximumStackSize { get; init; } = 2;

    public ImmutableArray<Lua54Instruction> Code { get; init; } = [];

    public ImmutableArray<Lua54Constant> Constants { get; init; } = [];

    public ImmutableArray<Lua54UpvalueDescriptor> Upvalues { get; init; } = [];

    public ImmutableArray<Lua54Prototype> NestedPrototypes { get; init; } = [];

    public ImmutableArray<sbyte> LineInfo { get; init; } = [];

    public ImmutableArray<Lua54AbsoluteLineInfo> AbsoluteLineInfo { get; init; } = [];

    public ImmutableArray<Lua54LocalVariable> LocalVariables { get; init; } = [];

    public ImmutableArray<Lua54String?> UpvalueNames { get; init; } = [];
}
