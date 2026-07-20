using System.Collections.Immutable;
using Lunil.IR.Lua54;

namespace Lunil.IR.Lua55;

/// <summary>
/// Normalizes the numeric-for register layout at the Lua 5.5 chunk boundary.
/// Lua 5.4's canonical IR reserves four registers for a numeric loop (the
/// control value is at A+3), while Lua 5.5 uses three (the control value is at
/// A+2).  The executable IR deliberately keeps the Lua 5.4 layout so source
/// compilation remains polymorphic; only binary adapter boundaries shift the
/// loop body registers.
/// </summary>
internal static class Lua55RegisterLayoutAdapter
{
    public static Lua54Chunk NormalizeToCanonical(Lua54Chunk chunk) =>
        chunk with { MainPrototype = NormalizePrototype(chunk.MainPrototype) };

    public static Lua54Chunk DenormalizeForLua55(Lua54Chunk chunk) =>
        chunk with { MainPrototype = DenormalizePrototype(chunk.MainPrototype) };

    private static Lua54Prototype NormalizePrototype(Lua54Prototype prototype)
    {
        var nested = prototype.NestedPrototypes.IsDefaultOrEmpty
            ? prototype.NestedPrototypes
            : prototype.NestedPrototypes.Select(NormalizePrototype).ToImmutableArray();
        var (code, addedSlots) = RemapLoops(prototype.Code, thresholdOffset: 2, delta: 1);
        code = NormalizeVarArgPrepare(code, prototype.ParameterCount);
        var maximumStackSize = checked(prototype.MaximumStackSize + addedSlots);
        if (maximumStackSize > byte.MaxValue)
        {
            throw new Lua55ChunkFormatException("numeric-for register normalization exceeds the stack limit");
        }

        return prototype with
        {
            MaximumStackSize = (byte)maximumStackSize,
            Code = code,
            NestedPrototypes = nested,
        };
    }

    private static Lua54Prototype DenormalizePrototype(Lua54Prototype prototype)
    {
        var nested = prototype.NestedPrototypes.IsDefaultOrEmpty
            ? prototype.NestedPrototypes
            : prototype.NestedPrototypes.Select(DenormalizePrototype).ToImmutableArray();
        var (code, _) = RemapLoops(prototype.Code, thresholdOffset: 3, delta: -1);
        code = NormalizeVarArgPrepare(code, 0);
        return prototype with
        {
            Code = code,
            NestedPrototypes = nested,
        };
    }

    private static ImmutableArray<Lua54Instruction> NormalizeVarArgPrepare(
        ImmutableArray<Lua54Instruction> code,
        int register)
    {
        if (code.IsDefaultOrEmpty)
        {
            return code;
        }

        var changed = false;
        var result = code.ToArray();
        for (var index = 0; index < result.Length; index++)
        {
            if (result[index].Opcode != Lua54Opcode.VarArgPrepare || result[index].A == register)
            {
                continue;
            }

            result[index] = new Lua54Instruction(
                (result[index].RawValue & ~(0xffU << 7)) | ((uint)register << 7));
            changed = true;
        }

        return changed ? result.ToImmutableArray() : code;
    }

    private static (ImmutableArray<Lua54Instruction> Code, int AddedSlots) RemapLoops(
        ImmutableArray<Lua54Instruction> source,
        int thresholdOffset,
        int delta)
    {
        if (source.IsDefaultOrEmpty)
        {
            return (source, 0);
        }

        var loops = FindLoops(source)
            .OrderByDescending(static loop => loop.Start)
            .ToArray();
        if (loops.Length == 0)
        {
            return (source, 0);
        }

        var code = source.ToArray();
        foreach (var loop in loops)
        {
            var loopOffset = loop.Generic ? thresholdOffset + 1 : thresholdOffset;
            var threshold = checked(loop.Register + loopOffset);
            for (var pc = loop.Start + 1; pc < loop.BodyEnd; pc++)
            {
                code[pc] = RemapInstruction(code[pc], threshold, delta);
            }
        }

        return (code.ToImmutableArray(), delta > 0 ? loops.Length : 0);
    }

    private static IEnumerable<(int Start, int BodyEnd, int Register, bool Generic)> FindLoops(
        ImmutableArray<Lua54Instruction> code)
    {
        for (var start = 0; start < code.Length; start++)
        {
            var prepare = code[start];
            if (prepare.Opcode is not
                (Lua54Opcode.NumericForPrepare or Lua54Opcode.GenericForPrepare))
            {
                continue;
            }

            for (var end = start + 1; end < code.Length; end++)
            {
                var loop = code[end];
                if (prepare.Opcode == Lua54Opcode.NumericForPrepare &&
                    (loop.Opcode != Lua54Opcode.NumericForLoop || loop.A != prepare.A))
                {
                    continue;
                }

                if (prepare.Opcode == Lua54Opcode.NumericForPrepare &&
                    end + 1 - loop.Bx == start + 1)
                {
                    yield return (start, end, prepare.A, false);
                    break;
                }

                if (prepare.Opcode != Lua54Opcode.GenericForPrepare ||
                    end != start + 1 + prepare.Bx ||
                    loop.Opcode != Lua54Opcode.GenericForCall ||
                    loop.A != prepare.A ||
                    end + 1 >= code.Length ||
                    code[end + 1].Opcode != Lua54Opcode.GenericForLoop ||
                    code[end + 1].A != prepare.A ||
                    end + 2 - code[end + 1].Bx != start + 1)
                {
                    continue;
                }

                yield return (start, end, prepare.A, true);
                break;
            }
        }
    }

    private static Lua54Instruction RemapInstruction(
        Lua54Instruction instruction,
        int threshold,
        int delta)
    {
        var raw = instruction.RawValue;
        if (UsesRegisterA(instruction.Opcode))
        {
            raw = RemapField(raw, 7, instruction.A, threshold, delta);
        }

        if (UsesRegisterB(instruction.Opcode))
        {
            raw = RemapField(raw, 16, instruction.B, threshold, delta);
        }

        if (UsesRegisterC(instruction))
        {
            raw = RemapField(raw, 24, instruction.C, threshold, delta);
        }

        return new Lua54Instruction(raw);
    }

    private static uint RemapField(uint raw, int shift, int value, int threshold, int delta)
    {
        if (value < threshold)
        {
            return raw;
        }

        var mapped = checked(value + delta);
        if ((uint)mapped > 0xff)
        {
            throw new Lua55ChunkFormatException("numeric-for register normalization exceeds the register limit");
        }

        const uint mask = 0xff;
        return (raw & ~(mask << shift)) | ((uint)mapped << shift);
    }

    private static bool UsesRegisterA(Lua54Opcode opcode) => opcode is not
        (Lua54Opcode.Jump or Lua54Opcode.SetTableUpvalue or Lua54Opcode.ExtraArgument);

    private static bool UsesRegisterB(Lua54Opcode opcode) => opcode is
        Lua54Opcode.Move or
        Lua54Opcode.GetTable or Lua54Opcode.GetInteger or Lua54Opcode.GetField or
        Lua54Opcode.SetTable or Lua54Opcode.Self or
        Lua54Opcode.AddImmediate or Lua54Opcode.AddConstant or
        Lua54Opcode.SubtractConstant or Lua54Opcode.MultiplyConstant or
        Lua54Opcode.ModuloConstant or Lua54Opcode.PowerConstant or
        Lua54Opcode.DivideConstant or Lua54Opcode.FloorDivideConstant or
        Lua54Opcode.BitwiseAndConstant or Lua54Opcode.BitwiseOrConstant or
        Lua54Opcode.BitwiseXorConstant or Lua54Opcode.ShiftRightImmediate or
        Lua54Opcode.ShiftLeftImmediate or Lua54Opcode.Add or Lua54Opcode.Subtract or
        Lua54Opcode.Multiply or Lua54Opcode.Modulo or Lua54Opcode.Power or
        Lua54Opcode.Divide or Lua54Opcode.FloorDivide or Lua54Opcode.BitwiseAnd or
        Lua54Opcode.BitwiseOr or Lua54Opcode.BitwiseXor or Lua54Opcode.ShiftLeft or
        Lua54Opcode.ShiftRight or Lua54Opcode.MetamethodBinary or
        Lua54Opcode.MetamethodBinaryImmediate or Lua54Opcode.MetamethodBinaryConstant or
        Lua54Opcode.UnaryMinus or Lua54Opcode.BitwiseNot or Lua54Opcode.LogicalNot or
        Lua54Opcode.Length or Lua54Opcode.Concatenate or Lua54Opcode.Equal or
        Lua54Opcode.LessThan or Lua54Opcode.LessOrEqual or Lua54Opcode.TestSet or
        Lua54Opcode.VarArg or Lua54Opcode.Lua55GetVarArg;

    private static bool UsesRegisterC(Lua54Instruction instruction) => instruction.Opcode switch
    {
        Lua54Opcode.GetTable or
        Lua54Opcode.Lua55GetVarArg or
        Lua54Opcode.Add or Lua54Opcode.Subtract or Lua54Opcode.Multiply or
        Lua54Opcode.Modulo or Lua54Opcode.Power or Lua54Opcode.Divide or
        Lua54Opcode.FloorDivide or Lua54Opcode.BitwiseAnd or Lua54Opcode.BitwiseOr or
        Lua54Opcode.BitwiseXor or Lua54Opcode.ShiftLeft or Lua54Opcode.ShiftRight => true,
        Lua54Opcode.SetTable or Lua54Opcode.Self => !instruction.K,
        _ => false,
    };
}
