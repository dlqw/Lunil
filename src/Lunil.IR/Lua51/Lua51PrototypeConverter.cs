using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Collections.Generic;
using Lunil.Core;
using Lunil.IR.Canonical;
using Lunil.IR.Lua53;

#pragma warning disable CA1720

namespace Lunil.IR.Lua51;

public static class Lua51PrototypeConverter
{
    public static LuaIrModule Convert(ReadOnlySpan<byte> bytes, Lua51ChunkReaderOptions? options = null) => Convert(Lua51ChunkReader.Read(bytes, options));
    public static LuaIrModule Convert(Lua51Chunk chunk)
    {
        LunilGuard.NotNull(chunk);
        var environmentRequirements = new Dictionary<Lua51Prototype, bool>(
            LunilReferenceEqualityComparer.Instance);
        AnalyzeEnvironmentRequirements(chunk.MainPrototype, environmentRequirements);
        var main = Translate(chunk.MainPrototype, default, environmentRequirements);
        return Lua53PrototypeConverter.Convert(new Lua53Chunk(
            new Lua53ChunkTarget(chunk.Target.ByteOrder == Lua51ByteOrder.LittleEndian ? Lua53ByteOrder.LittleEndian : Lua53ByteOrder.BigEndian,
                4, (byte)chunk.Target.SizeOfSizeT, 4, 8, (byte)chunk.Target.NumberSize),
            checked((byte)main.Upvalues.Length),
            main), LuaLanguageVersion.Lua51);
    }

    private static Lua53Prototype Translate(
        Lua51Prototype p,
        ImmutableArray<Lua53UpvalueDescriptor> upvalues,
        IReadOnlyDictionary<Lua51Prototype, bool> environmentRequirements)
    {
        var hasEnvironment = environmentRequirements[p];
        var upvalueOffset = hasEnvironment ? 1 : 0;
        if (upvalues.IsDefault)
        {
            var rootUpvalues = ImmutableArray.CreateBuilder<Lua53UpvalueDescriptor>(
                checked(p.UpvalueCount + upvalueOffset));
            if (hasEnvironment)
            {
                rootUpvalues.Add(new Lua53UpvalueDescriptor(1, 0));
            }

            rootUpvalues.AddRange(Enumerable.Range(0, p.UpvalueCount)
                .Select(_ => new Lua53UpvalueDescriptor(1, 0)));
            upvalues = rootUpvalues.MoveToImmutable();
        }
        var nestedUpvalues = new ImmutableArray<Lua53UpvalueDescriptor>[p.NestedPrototypes.Length];
        var skipped = new bool[p.Code.Length];
        for (var pc = 0; pc < p.Code.Length; pc++)
        {
            var instruction = p.Code[pc];
            if (instruction.Opcode != Lua51Opcode.Closure || instruction.Bx >= nestedUpvalues.Length)
                continue;
            var nested = p.NestedPrototypes[instruction.Bx];
            var count = nested.UpvalueCount;
            var nestedHasEnvironment = environmentRequirements[nested];
            var descriptors = ImmutableArray.CreateBuilder<Lua53UpvalueDescriptor>(
                checked(count + (nestedHasEnvironment ? 1 : 0)));
            if (nestedHasEnvironment)
            {
                // GETGLOBAL and SETGLOBAL refer to the function environment implicitly in
                // Lua 5.1. Keep it at canonical upvalue index zero for closures that need it.
                descriptors.Add(new Lua53UpvalueDescriptor(0, 0));
            }

            for (var index = 0; index < count; index++)
            {
                var bindingPc = pc + index + 1;
                if (bindingPc >= p.Code.Length)
                    throw new InvalidDataException("Lua 5.1 closure upvalue bindings are truncated.");
                var binding = p.Code[bindingPc];
                descriptors.Add(binding.Opcode switch
                {
                    Lua51Opcode.Move => new Lua53UpvalueDescriptor(1, checked((byte)binding.B)),
                    Lua51Opcode.GetUpvalue => new Lua53UpvalueDescriptor(
                        0,
                        checked((byte)(binding.B + upvalueOffset))),
                    _ => throw new InvalidDataException("Lua 5.1 closure has an invalid upvalue binding instruction."),
                });
                skipped[bindingPc] = true;
            }
            nestedUpvalues[instruction.Bx] = descriptors.MoveToImmutable();
        }

        for (var pc = 0; pc < p.Code.Length; pc++)
        {
            if (skipped[pc] || p.Code[pc].Opcode != Lua51Opcode.GenericForLoop)
            {
                continue;
            }

            var jumpPc = pc + 1;
            if (jumpPc >= p.Code.Length || p.Code[jumpPc].Opcode != Lua51Opcode.Jump)
            {
                throw new InvalidDataException(
                    "Lua 5.1 TFORLOOP must be followed by its control-flow jump.");
            }

            skipped[jumpPc] = true;
        }

        var pcMap = new int[p.Code.Length + 1];
        var translatedPc = 0;
        for (var pc = 0; pc < p.Code.Length; pc++)
        {
            pcMap[pc] = translatedPc;
            if (!skipped[pc])
            {
                translatedPc += p.Code[pc].Opcode == Lua51Opcode.GenericForLoop ? 2 : 1;
            }
        }
        pcMap[^1] = translatedPc;
        var code = ImmutableArray.CreateBuilder<Lua53Instruction>(translatedPc);
        for (var pc = 0; pc < p.Code.Length; pc++)
        {
            if (skipped[pc]) continue;
            var instruction = p.Code[pc];
            if (instruction.Opcode == Lua51Opcode.GenericForLoop)
            {
                if (pc + 1 >= p.Code.Length)
                {
                    throw new InvalidDataException(
                        "Lua 5.1 chunk ends before the generic-for companion jump.");
                }

                var jump = p.Code[pc + 1];
                var target = pc + 2 + jump.SignedBx;
                if ((uint)target > (uint)p.Code.Length)
                {
                    throw new InvalidDataException("Lua 5.1 generic-for target is outside the prototype.");
                }

                code.Add(Lua53Instruction.CreateAbc(
                    Lua53Opcode.GenericForCall,
                    instruction.A,
                    0,
                    instruction.C));
                code.Add(Lua53Instruction.CreateASignedBx(
                    Lua53Opcode.GenericForLoop,
                    checked(instruction.A + 2),
                    pcMap[target] - (pcMap[pc] + 2)));
            }
            else if (instruction.Opcode is Lua51Opcode.Jump or Lua51Opcode.NumericForLoop or Lua51Opcode.NumericForPrepare)
            {
                var target = pc + 1 + instruction.SignedBx;
                if ((uint)target > (uint)p.Code.Length)
                    throw new InvalidDataException("Lua 5.1 jump target is outside the prototype.");
                var mapped = pcMap[target] - (pcMap[pc] + 1);
                code.Add(Lua53Instruction.CreateASignedBx(
                    instruction.Opcode == Lua51Opcode.Jump ? Lua53Opcode.Jump :
                        instruction.Opcode == Lua51Opcode.NumericForLoop ? Lua53Opcode.NumericForLoop : Lua53Opcode.NumericForPrepare,
                    instruction.A, mapped));
            }
            else code.Add(Translate(instruction, upvalueOffset));
        }

        return new Lua53Prototype
        {
            Source = p.Source is { } s ? new Lua53String(s.ToArray()) : null,
            LineDefined = p.LineDefined,
            LastLineDefined = p.LastLineDefined,
            ParameterCount = p.ParameterCount,
            VarArgFlags = (byte)((p.VarArgFlags & 2) != 0 ? 1 : 0),
            MaximumStackSize = p.MaximumStackSize,
            Code = code.MoveToImmutable(),
            Constants = p.Constants.Select(Translate).ToImmutableArray(),
            NestedPrototypes = p.NestedPrototypes.Select((nested, index) => Translate(
                nested,
                nestedUpvalues[index],
                environmentRequirements)).ToImmutableArray(),
            LineInfo = TranslateLineInfo(p, skipped),
            LocalVariables = p.LocalVariables.Select(x => new Lua53LocalVariable(
                x.Name is { } n ? new Lua53String(n.ToArray()) : null,
                pcMap[x.StartProgramCounter],
                pcMap[x.EndProgramCounter])).ToImmutableArray(),
            UpvalueNames = TranslateUpvalueNames(
                p.UpvalueNames,
                upvalues.Length,
                hasEnvironment),
            Upvalues = upvalues,
        };
    }

    private static ImmutableArray<Lua53String?> TranslateUpvalueNames(
        ImmutableArray<Lua51String?> names,
        int upvalueCount,
        bool hasEnvironment)
    {
        var result = ImmutableArray.CreateBuilder<Lua53String?>(upvalueCount);
        if (hasEnvironment)
        {
            result.Add(new Lua53String("_ENV"u8.ToArray()));
        }
        foreach (var name in names)
        {
            result.Add(name is { } value ? new Lua53String(value.ToArray()) : null);
        }

        while (result.Count < upvalueCount)
        {
            result.Add(null);
        }

        return result.MoveToImmutable();
    }

    private static bool AnalyzeEnvironmentRequirements(
        Lua51Prototype prototype,
        IDictionary<Lua51Prototype, bool> requirements)
    {
        if (requirements.TryGetValue(prototype, out var existing))
        {
            return existing;
        }

        var required = prototype.Code.Any(static instruction =>
            instruction.Opcode is Lua51Opcode.GetGlobal or Lua51Opcode.SetGlobal);
        foreach (var nested in prototype.NestedPrototypes)
        {
            required |= AnalyzeEnvironmentRequirements(nested, requirements);
        }

        requirements[prototype] = required;
        return required;
    }

    private static ImmutableArray<int> TranslateLineInfo(Lua51Prototype prototype, bool[] skipped)
    {
        if (prototype.LineInfo.IsEmpty)
        {
            return [];
        }

        var lines = ImmutableArray.CreateBuilder<int>();
        for (var pc = 0; pc < prototype.Code.Length; pc++)
        {
            if (skipped[pc])
            {
                continue;
            }

            lines.Add(prototype.LineInfo[pc]);
            if (prototype.Code[pc].Opcode == Lua51Opcode.GenericForLoop)
            {
                lines.Add(prototype.LineInfo[pc]);
            }
        }

        return lines.ToImmutable();
    }

    private static Lua53Instruction Translate(Lua51Instruction i, int upvalueOffset)
    {
        if (i.Opcode == Lua51Opcode.Close)
        {
            return Lua53Instruction.CreateASignedBx(
                Lua53Opcode.Jump,
                checked(i.A + 1),
                0);
        }

        var op = i.Opcode switch
        {
            Lua51Opcode.Move => Lua53Opcode.Move,
            Lua51Opcode.LoadConstant => Lua53Opcode.LoadConstant,
            Lua51Opcode.LoadBoolean => Lua53Opcode.LoadBoolean,
            Lua51Opcode.LoadNil => Lua53Opcode.LoadNil,
            Lua51Opcode.GetUpvalue => Lua53Opcode.GetUpvalue,
            Lua51Opcode.GetGlobal => Lua53Opcode.GetGlobal,
            Lua51Opcode.GetTable => Lua53Opcode.GetTable,
            Lua51Opcode.SetGlobal => Lua53Opcode.SetTableUpvalue,
            Lua51Opcode.SetUpvalue => Lua53Opcode.SetUpvalue,
            Lua51Opcode.SetTable => Lua53Opcode.SetTable,
            Lua51Opcode.NewTable => Lua53Opcode.NewTable,
            Lua51Opcode.Self => Lua53Opcode.Self,
            Lua51Opcode.Add => Lua53Opcode.Add,
            Lua51Opcode.Subtract => Lua53Opcode.Subtract,
            Lua51Opcode.Multiply => Lua53Opcode.Multiply,
            Lua51Opcode.Divide => Lua53Opcode.Divide,
            Lua51Opcode.Modulo => Lua53Opcode.Modulo,
            Lua51Opcode.Power => Lua53Opcode.Power,
            Lua51Opcode.UnaryMinus => Lua53Opcode.UnaryMinus,
            Lua51Opcode.LogicalNot => Lua53Opcode.LogicalNot,
            Lua51Opcode.Length => Lua53Opcode.Length,
            Lua51Opcode.Concatenate => Lua53Opcode.Concatenate,
            Lua51Opcode.Jump => Lua53Opcode.Jump,
            Lua51Opcode.Equal => Lua53Opcode.Equal,
            Lua51Opcode.LessThan => Lua53Opcode.LessThan,
            Lua51Opcode.LessOrEqual => Lua53Opcode.LessOrEqual,
            Lua51Opcode.Test => Lua53Opcode.Test,
            Lua51Opcode.TestSet => Lua53Opcode.TestSet,
            Lua51Opcode.Call => Lua53Opcode.Call,
            Lua51Opcode.TailCall => Lua53Opcode.TailCall,
            Lua51Opcode.Return => Lua53Opcode.Return,
            Lua51Opcode.NumericForLoop => Lua53Opcode.NumericForLoop,
            Lua51Opcode.NumericForPrepare => Lua53Opcode.NumericForPrepare,
            Lua51Opcode.GenericForLoop => Lua53Opcode.GenericForLoop,
            Lua51Opcode.SetList => Lua53Opcode.SetList,
            Lua51Opcode.Closure => Lua53Opcode.Closure,
            Lua51Opcode.VarArg => Lua53Opcode.VarArg,
            _ => throw new InvalidDataException($"Unsupported Lua 5.1 opcode {i.Opcode}"),
        };
        if (i.Opcode is Lua51Opcode.GetGlobal) return Lua53Instruction.CreateAbc(op, i.A, 0, i.Bx | (1 << 8));
        if (i.Opcode is Lua51Opcode.SetGlobal) return Lua53Instruction.CreateAbc(op, 0, i.Bx | (1 << 8), i.A);
        if (i.Opcode is Lua51Opcode.GetUpvalue or Lua51Opcode.SetUpvalue)
            return Lua53Instruction.CreateAbc(op, i.A, checked(i.B + upvalueOffset), i.C);
        return i.Opcode is Lua51Opcode.LoadConstant or Lua51Opcode.Closure ? Lua53Instruction.CreateABx(op, i.A, i.Bx) :
            i.Opcode is Lua51Opcode.Jump or Lua51Opcode.NumericForLoop or Lua51Opcode.NumericForPrepare ? Lua53Instruction.CreateASignedBx(op, i.A, i.SignedBx) :
            Lua53Instruction.CreateAbc(op, i.A, i.B, i.C);
    }
    private static Lua53Constant Translate(Lua51Constant c) => c.Kind switch
    {
        Lua51ConstantKind.Nil => Lua53Constant.Nil,
        Lua51ConstantKind.False => Lua53Constant.False,
        Lua51ConstantKind.True => Lua53Constant.True,
        Lua51ConstantKind.Number => Lua53Constant.FromFloat(c.NumberValue),
        Lua51ConstantKind.String => Lua53Constant.FromString(new Lua53String(c.StringValue!.Value.ToArray()), c.StringValue.Value.Length <= 40),
        _ => throw new InvalidDataException("Unknown Lua 5.1 constant kind"),
    };
}
