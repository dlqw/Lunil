using System.Collections.Immutable;
using Lunil.Core;
using Lunil.IR.Canonical;
using Lunil.IR.Lua53;

namespace Lunil.IR.Lua52;

/// <summary>Reads Lua 5.2 chunks and lowers their instruction identity to canonical IR.</summary>
public static class Lua52PrototypeConverter
{
    public static LuaIrModule Convert(
        ReadOnlySpan<byte> binaryChunk,
        Lua52ChunkReaderOptions? options = null) =>
        Lua53PrototypeConverter.Convert(Translate(Lua52ChunkReader.Read(binaryChunk, options)),
            LuaLanguageVersion.Lua52);

    public static LuaIrModule Convert(Lua52Chunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        return Lua53PrototypeConverter.Convert(Translate(chunk), LuaLanguageVersion.Lua52);
    }

    private static Lua53Chunk Translate(Lua52Chunk chunk) => new(
        new Lua53ChunkTarget(
            chunk.Target.ByteOrder == Lua52ByteOrder.LittleEndian
                ? Lua53ByteOrder.LittleEndian
                : Lua53ByteOrder.BigEndian,
            checked((byte)chunk.Target.SizeOfInt),
            checked((byte)chunk.Target.SizeOfSizeT),
            checked((byte)chunk.Target.InstructionSize),
            integerSize: 8,
            checked((byte)chunk.Target.NumberSize)),
        checked((byte)chunk.MainPrototype.Upvalues.Length),
        Translate(chunk.MainPrototype));

    private static Lua53Prototype Translate(Lua52Prototype prototype) => new()
    {
        Source = prototype.Source is { } source ? new Lua53String(source.ToArray()) : null,
        LineDefined = prototype.LineDefined,
        LastLineDefined = prototype.LastLineDefined,
        ParameterCount = prototype.ParameterCount,
        VarArgFlags = prototype.VarArgFlags,
        MaximumStackSize = prototype.MaximumStackSize,
        Code = prototype.Code.Select(static instruction => Translate(instruction)).ToImmutableArray(),
        Constants = prototype.Constants.Select(static constant => Translate(constant)).ToImmutableArray(),
        Upvalues = prototype.Upvalues.Select(static upvalue =>
            new Lua53UpvalueDescriptor(upvalue.InStack, upvalue.Index)).ToImmutableArray(),
        NestedPrototypes = prototype.NestedPrototypes.Select(Translate).ToImmutableArray(),
        LineInfo = prototype.LineInfo,
        LocalVariables = prototype.LocalVariables.Select(static local => new Lua53LocalVariable(
            local.Name is { } name ? new Lua53String(name.ToArray()) : null,
            local.StartProgramCounter,
            local.EndProgramCounter)).ToImmutableArray(),
        UpvalueNames = prototype.UpvalueNames
            .Select(static (Lua52String? name) => name is { } value
                ? (Lua53String?)new Lua53String(value.ToArray())
                : null)
            .ToImmutableArray(),
    };

    private static Lua53Instruction Translate(Lua52Instruction instruction)
    {
        var opcode = instruction.Opcode switch
        {
            Lua52Opcode.Move => Lua53Opcode.Move,
            Lua52Opcode.LoadConstant => Lua53Opcode.LoadConstant,
            Lua52Opcode.LoadConstantExtra => Lua53Opcode.LoadConstantExtra,
            Lua52Opcode.LoadBoolean => Lua53Opcode.LoadBoolean,
            Lua52Opcode.LoadNil => Lua53Opcode.LoadNil,
            Lua52Opcode.GetUpvalue => Lua53Opcode.GetUpvalue,
            Lua52Opcode.GetTableUpvalue => Lua53Opcode.GetGlobal,
            Lua52Opcode.GetTable => Lua53Opcode.GetTable,
            Lua52Opcode.SetTableUpvalue => Lua53Opcode.SetTableUpvalue,
            Lua52Opcode.SetUpvalue => Lua53Opcode.SetUpvalue,
            Lua52Opcode.SetTable => Lua53Opcode.SetTable,
            Lua52Opcode.NewTable => Lua53Opcode.NewTable,
            Lua52Opcode.Self => Lua53Opcode.Self,
            Lua52Opcode.Add => Lua53Opcode.Add,
            Lua52Opcode.Subtract => Lua53Opcode.Subtract,
            Lua52Opcode.Multiply => Lua53Opcode.Multiply,
            Lua52Opcode.Divide => Lua53Opcode.Divide,
            Lua52Opcode.Modulo => Lua53Opcode.Modulo,
            Lua52Opcode.Power => Lua53Opcode.Power,
            Lua52Opcode.UnaryMinus => Lua53Opcode.UnaryMinus,
            Lua52Opcode.LogicalNot => Lua53Opcode.LogicalNot,
            Lua52Opcode.Length => Lua53Opcode.Length,
            Lua52Opcode.Concatenate => Lua53Opcode.Concatenate,
            Lua52Opcode.Jump => Lua53Opcode.Jump,
            Lua52Opcode.Equal => Lua53Opcode.Equal,
            Lua52Opcode.LessThan => Lua53Opcode.LessThan,
            Lua52Opcode.LessOrEqual => Lua53Opcode.LessOrEqual,
            Lua52Opcode.Test => Lua53Opcode.Test,
            Lua52Opcode.TestSet => Lua53Opcode.TestSet,
            Lua52Opcode.Call => Lua53Opcode.Call,
            Lua52Opcode.TailCall => Lua53Opcode.TailCall,
            Lua52Opcode.Return => Lua53Opcode.Return,
            Lua52Opcode.NumericForLoop => Lua53Opcode.NumericForLoop,
            Lua52Opcode.NumericForPrepare => Lua53Opcode.NumericForPrepare,
            Lua52Opcode.GenericForCall => Lua53Opcode.GenericForCall,
            Lua52Opcode.GenericForLoop => Lua53Opcode.GenericForLoop,
            Lua52Opcode.SetList => Lua53Opcode.SetList,
            Lua52Opcode.Closure => Lua53Opcode.Closure,
            Lua52Opcode.VarArg => Lua53Opcode.VarArg,
            Lua52Opcode.ExtraArgument => Lua53Opcode.ExtraArgument,
            _ => throw new InvalidDataException($"Unsupported Lua 5.2 opcode {instruction.Opcode}.")
        };

        return Lua53Instruction.CreateAbc(opcode, instruction.A, instruction.B, instruction.C)
            with
        {
            RawValue = instruction.Opcode is Lua52Opcode.LoadConstant or
                    Lua52Opcode.LoadConstantExtra or Lua52Opcode.Closure
                    ? Lua53Instruction.CreateABx(opcode, instruction.A, instruction.Bx).RawValue
                    : instruction.Opcode is Lua52Opcode.Jump or Lua52Opcode.NumericForLoop or
                        Lua52Opcode.NumericForPrepare or Lua52Opcode.GenericForLoop
                        ? Lua53Instruction.CreateASignedBx(opcode, instruction.A, instruction.SignedBx).RawValue
                        : instruction.Opcode == Lua52Opcode.ExtraArgument
                            ? Lua53Instruction.CreateAx(opcode, instruction.Ax).RawValue
                            : Lua53Instruction.CreateAbc(opcode, instruction.A, instruction.B, instruction.C).RawValue,
        };
    }

    private static Lua53Constant Translate(Lua52Constant constant) => constant.Kind switch
    {
        Lua52ConstantKind.Nil => Lua53Constant.Nil,
        Lua52ConstantKind.False => Lua53Constant.False,
        Lua52ConstantKind.True => Lua53Constant.True,
        Lua52ConstantKind.Number => Lua53Constant.FromFloat(constant.NumberValue),
        Lua52ConstantKind.String => TranslateString(constant.StringValue),
        _ => throw new InvalidDataException("Unknown Lua 5.2 constant kind."),
    };

    private static Lua53Constant TranslateString(Lua52String? value)
    {
        if (value is not { } actual)
        {
            throw new InvalidDataException("A Lua 5.2 string constant is missing its payload.");
        }

        return Lua53Constant.FromString(
            new Lua53String(actual.ToArray()),
            actual.Length <= 40);
    }
}
