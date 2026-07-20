using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Lunil.Core;
using Lunil.IR.Canonical;
using Lunil.IR.Lua53;

namespace Lunil.IR.Lua52;

/// <summary>Writes canonical modules to the official Lua 5.2 chunk layout.</summary>
public static class Lua52CanonicalPrototypeWriter
{
    public static byte[] Write(
        LuaIrModule module,
        int functionId,
        bool stripDebug = false)
    {
        var chunk = CreateChunk(module, functionId, stripDebug);
        var bytes = new List<byte>(4096);
        WriteHeader(bytes);
        WritePrototype(bytes, chunk.MainPrototype, stripDebug);
        return [.. bytes];
    }

    public static Lua52Chunk CreateChunk(
        LuaIrModule module,
        int functionId,
        bool stripDebug = false)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (module.LanguageVersion != LuaLanguageVersion.Lua52)
        {
            throw new InvalidDataException("Lua 5.2 writer requires a Lua 5.2 canonical module.");
        }

        // The Lua 5.3 lowering already emits the shared register/RK instruction shape. Reuse it
        // only as an internal lowering step, then translate every opcode and serialize with the
        // independent Lua 5.2 header/prototype order below.
        var loweringModule = module with { LanguageVersion = LuaLanguageVersion.Lua53 };
        var lua53Chunk = Lua53CanonicalPrototypeWriter.CreateChunk(
            loweringModule,
            functionId);
        return new Lua52Chunk(
            Lua52ChunkTarget.Host,
            TranslatePrototype(lua53Chunk.MainPrototype));
    }

    private static Lua52Prototype TranslatePrototype(Lua53Prototype prototype) => new()
    {
        Source = prototype.Source is { } source ? new Lua52String(source.ToArray()) : null,
        LineDefined = prototype.LineDefined,
        LastLineDefined = prototype.LastLineDefined,
        ParameterCount = prototype.ParameterCount,
        VarArgFlags = prototype.VarArgFlags,
        MaximumStackSize = prototype.MaximumStackSize,
        Code = prototype.Code.Select(static instruction => TranslateInstruction(instruction)).ToImmutableArray(),
        Constants = prototype.Constants.Select(TranslateConstant).ToImmutableArray(),
        Upvalues = prototype.Upvalues.Select(static value =>
            new Lua52UpvalueDescriptor(value.InStack, value.Index)).ToImmutableArray(),
        NestedPrototypes = prototype.NestedPrototypes.Select(TranslatePrototype).ToImmutableArray(),
        LineInfo = prototype.LineInfo,
        LocalVariables = prototype.LocalVariables.Select(static value => new Lua52LocalVariable(
            value.Name is { } name ? new Lua52String(name.ToArray()) : null,
            value.StartProgramCounter,
            value.EndProgramCounter)).ToImmutableArray(),
        UpvalueNames = prototype.UpvalueNames
            .Select(static (Lua53String? value) => value is { } name
                ? (Lua52String?)new Lua52String(name.ToArray())
                : null)
            .ToImmutableArray(),
    };

    private static Lua52Instruction TranslateInstruction(Lua53Instruction instruction)
    {
        var opcode = instruction.Opcode switch
        {
            Lua53Opcode.Move => Lua52Opcode.Move,
            Lua53Opcode.LoadConstant => Lua52Opcode.LoadConstant,
            Lua53Opcode.LoadConstantExtra => Lua52Opcode.LoadConstantExtra,
            Lua53Opcode.LoadBoolean => Lua52Opcode.LoadBoolean,
            Lua53Opcode.LoadNil => Lua52Opcode.LoadNil,
            Lua53Opcode.GetUpvalue => Lua52Opcode.GetUpvalue,
            Lua53Opcode.GetGlobal => Lua52Opcode.GetTableUpvalue,
            Lua53Opcode.GetTable => Lua52Opcode.GetTable,
            Lua53Opcode.SetTableUpvalue => Lua52Opcode.SetTableUpvalue,
            Lua53Opcode.SetUpvalue => Lua52Opcode.SetUpvalue,
            Lua53Opcode.SetTable => Lua52Opcode.SetTable,
            Lua53Opcode.NewTable => Lua52Opcode.NewTable,
            Lua53Opcode.Self => Lua52Opcode.Self,
            Lua53Opcode.Add => Lua52Opcode.Add,
            Lua53Opcode.Subtract => Lua52Opcode.Subtract,
            Lua53Opcode.Multiply => Lua52Opcode.Multiply,
            Lua53Opcode.Divide => Lua52Opcode.Divide,
            Lua53Opcode.Modulo => Lua52Opcode.Modulo,
            Lua53Opcode.Power => Lua52Opcode.Power,
            Lua53Opcode.UnaryMinus => Lua52Opcode.UnaryMinus,
            Lua53Opcode.LogicalNot => Lua52Opcode.LogicalNot,
            Lua53Opcode.Length => Lua52Opcode.Length,
            Lua53Opcode.Concatenate => Lua52Opcode.Concatenate,
            Lua53Opcode.Jump => Lua52Opcode.Jump,
            Lua53Opcode.Equal => Lua52Opcode.Equal,
            Lua53Opcode.LessThan => Lua52Opcode.LessThan,
            Lua53Opcode.LessOrEqual => Lua52Opcode.LessOrEqual,
            Lua53Opcode.Test => Lua52Opcode.Test,
            Lua53Opcode.TestSet => Lua52Opcode.TestSet,
            Lua53Opcode.Call => Lua52Opcode.Call,
            Lua53Opcode.TailCall => Lua52Opcode.TailCall,
            Lua53Opcode.Return => Lua52Opcode.Return,
            Lua53Opcode.NumericForLoop => Lua52Opcode.NumericForLoop,
            Lua53Opcode.NumericForPrepare => Lua52Opcode.NumericForPrepare,
            Lua53Opcode.GenericForCall => Lua52Opcode.GenericForCall,
            Lua53Opcode.GenericForLoop => Lua52Opcode.GenericForLoop,
            Lua53Opcode.SetList => Lua52Opcode.SetList,
            Lua53Opcode.Closure => Lua52Opcode.Closure,
            Lua53Opcode.VarArg => Lua52Opcode.VarArg,
            Lua53Opcode.ExtraArgument => Lua52Opcode.ExtraArgument,
            _ => throw new InvalidDataException($"Unsupported Lua 5.2 opcode {instruction.Opcode}.")
        };

        return instruction.Opcode is Lua53Opcode.LoadConstant or Lua53Opcode.Closure
            ? Lua52Instruction.CreateABx(opcode, instruction.A, instruction.Bx)
            : instruction.Opcode is Lua53Opcode.Jump or Lua53Opcode.NumericForLoop or
                Lua53Opcode.NumericForPrepare or Lua53Opcode.GenericForLoop
                ? Lua52Instruction.CreateASignedBx(opcode, instruction.A, instruction.SignedBx)
                : instruction.Opcode == Lua53Opcode.ExtraArgument
                    ? Lua52Instruction.CreateAx(opcode, instruction.Ax)
                    : Lua52Instruction.CreateAbc(opcode, instruction.A, instruction.B, instruction.C);
    }

    private static Lua52Constant TranslateConstant(Lua53Constant constant) => constant.Kind switch
    {
        Lua53ConstantKind.Nil => Lua52Constant.Nil,
        Lua53ConstantKind.False => Lua52Constant.False,
        Lua53ConstantKind.True => Lua52Constant.True,
        Lua53ConstantKind.Integer => Lua52Constant.FromNumber(constant.IntegerValue),
        Lua53ConstantKind.Float => Lua52Constant.FromNumber(constant.FloatValue),
        Lua53ConstantKind.ShortString or Lua53ConstantKind.LongString =>
            TranslateString(constant.StringValue),
        _ => throw new InvalidDataException("Unknown canonical constant kind."),
    };

    private static Lua52Constant TranslateString(Lua53String? value)
    {
        if (value is not { } actual)
        {
            throw new InvalidDataException("A Lua 5.2 string constant is missing its payload.");
        }

        return Lua52Constant.FromString(new Lua52String(actual.ToArray()));
    }

    private static void WriteHeader(List<byte> bytes)
    {
        bytes.AddRange([0x1b, (byte)'L', (byte)'u', (byte)'a', 0x52, 0, 1, 4, 8, 4, 8, 0,
            0x19, 0x93, (byte)'\r', (byte)'\n', 0x1a, (byte)'\n']);
    }

    private static void WritePrototype(List<byte> bytes, Lua52Prototype prototype, bool stripDebug)
    {
        WriteInt(bytes, prototype.LineDefined);
        WriteInt(bytes, prototype.LastLineDefined);
        bytes.Add(prototype.ParameterCount);
        bytes.Add(prototype.VarArgFlags);
        bytes.Add(prototype.MaximumStackSize);
        WriteInt(bytes, prototype.Code.Length);
        foreach (var instruction in prototype.Code)
        {
            WriteUInt(bytes, instruction.RawValue);
        }

        WriteInt(bytes, prototype.Constants.Length);
        foreach (var constant in prototype.Constants)
        {
            WriteConstant(bytes, constant);
        }

        WriteInt(bytes, prototype.NestedPrototypes.Length);
        foreach (var nested in prototype.NestedPrototypes)
        {
            WritePrototype(bytes, nested, stripDebug);
        }

        WriteInt(bytes, prototype.Upvalues.Length);
        foreach (var upvalue in prototype.Upvalues)
        {
            bytes.Add(upvalue.InStack);
            bytes.Add(upvalue.Index);
        }

        WriteString(bytes, stripDebug ? null : prototype.Source);
        WriteInt(bytes, stripDebug ? 0 : prototype.LineInfo.Length);
        if (!stripDebug)
        {
            foreach (var line in prototype.LineInfo)
            {
                WriteInt(bytes, line);
            }
        }

        WriteInt(bytes, stripDebug ? 0 : prototype.LocalVariables.Length);
        if (!stripDebug)
        {
            foreach (var local in prototype.LocalVariables)
            {
                WriteString(bytes, local.Name);
                WriteInt(bytes, local.StartProgramCounter);
                WriteInt(bytes, local.EndProgramCounter);
            }
        }

        WriteInt(bytes, stripDebug ? 0 : prototype.UpvalueNames.Length);
        if (!stripDebug)
        {
            foreach (var name in prototype.UpvalueNames)
            {
                WriteString(bytes, name);
            }
        }
    }

    private static void WriteConstant(List<byte> bytes, Lua52Constant constant)
    {
        switch (constant.Kind)
        {
            case Lua52ConstantKind.Nil:
                bytes.Add(0);
                break;
            case Lua52ConstantKind.False:
            case Lua52ConstantKind.True:
                bytes.Add(1);
                bytes.Add((byte)(constant.Kind == Lua52ConstantKind.True ? 1 : 0));
                break;
            case Lua52ConstantKind.Number:
                bytes.Add(3);
                Span<byte> number = stackalloc byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(number, BitConverter.DoubleToInt64Bits(constant.NumberValue));
                bytes.AddRange(number.ToArray());
                break;
            case Lua52ConstantKind.String:
                bytes.Add(4);
                WriteString(bytes, constant.StringValue);
                break;
            default:
                throw new InvalidDataException("Unknown Lua 5.2 constant kind.");
        }
    }

    private static void WriteString(List<byte> bytes, Lua52String? value)
    {
        if (value is not { } actual)
        {
            WriteSizeT(bytes, 0);
            return;
        }

        WriteSizeT(bytes, checked((ulong)actual.Length + 1));
        bytes.AddRange(actual.Bytes);
        bytes.Add(0);
    }

    private static void WriteInt(List<byte> bytes, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        bytes.AddRange(buffer.ToArray());
    }

    private static void WriteUInt(List<byte> bytes, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        bytes.AddRange(buffer.ToArray());
    }

    private static void WriteSizeT(List<byte> bytes, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        bytes.AddRange(buffer.ToArray());
    }
}
