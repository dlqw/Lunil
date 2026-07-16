using System.Reflection;
using System.Reflection.Emit;

namespace Lunil.CodeGen.Cil.Jit;

/// <summary>
/// Backend-neutral IL surface used by the exact numeric-region emitter. Reflection.Emit and the
/// persisted metadata writer deliberately consume the same instruction stream so guards,
/// materialization, budget accounting, and safepoints cannot drift between backends.
/// </summary>
internal abstract class LuaNumericRegionIlGenerator
{
    public abstract LuaNumericIlLabel DefineLabel();

    public abstract LuaNumericIlLocal DeclareLocal(Type type);

    public abstract void MarkLabel(LuaNumericIlLabel label);

    public abstract void Emit(OpCode opcode);

    public abstract void Emit(OpCode opcode, int value);

    public abstract void Emit(OpCode opcode, sbyte value);

    public abstract void Emit(OpCode opcode, long value);

    public abstract void Emit(OpCode opcode, double value);

    public abstract void Emit(OpCode opcode, string value);

    public abstract void Emit(OpCode opcode, LuaNumericIlLabel label);

    public abstract void Emit(OpCode opcode, LuaNumericIlLabel[] labels);

    public abstract void Emit(OpCode opcode, LuaNumericIlLocal local);

    public abstract void Emit(OpCode opcode, MethodInfo method);

    public abstract void Emit(OpCode opcode, ConstructorInfo constructor);
}

internal readonly record struct LuaNumericIlLabel(int Id, object? BackendHandle = null);

internal readonly record struct LuaNumericIlLocal(int Index, Type Type, object? BackendHandle = null);

internal sealed class ReflectionEmitLuaNumericRegionIlGenerator(ILGenerator generator) :
    LuaNumericRegionIlGenerator
{
    private int _nextLabelId;

    public override LuaNumericIlLabel DefineLabel() =>
        new(_nextLabelId++, generator.DefineLabel());

    public override LuaNumericIlLocal DeclareLocal(Type type)
    {
        var local = generator.DeclareLocal(type);
        return new LuaNumericIlLocal(local.LocalIndex, type, local);
    }

    public override void MarkLabel(LuaNumericIlLabel label) =>
        generator.MarkLabel((Label)label.BackendHandle!);

    public override void Emit(OpCode opcode) => generator.Emit(opcode);

    public override void Emit(OpCode opcode, int value) => generator.Emit(opcode, value);

    public override void Emit(OpCode opcode, sbyte value) => generator.Emit(opcode, value);

    public override void Emit(OpCode opcode, long value) => generator.Emit(opcode, value);

    public override void Emit(OpCode opcode, double value) => generator.Emit(opcode, value);

    public override void Emit(OpCode opcode, string value) => generator.Emit(opcode, value);

    public override void Emit(OpCode opcode, LuaNumericIlLabel label) =>
        generator.Emit(opcode, (Label)label.BackendHandle!);

    public override void Emit(OpCode opcode, LuaNumericIlLabel[] labels) =>
        generator.Emit(
            opcode,
            labels.Select(static label => (Label)label.BackendHandle!).ToArray());

    public override void Emit(OpCode opcode, LuaNumericIlLocal local) =>
        generator.Emit(opcode, (LocalBuilder)local.BackendHandle!);

    public override void Emit(OpCode opcode, MethodInfo method) => generator.Emit(opcode, method);

    public override void Emit(OpCode opcode, ConstructorInfo constructor) =>
        generator.Emit(opcode, constructor);
}
