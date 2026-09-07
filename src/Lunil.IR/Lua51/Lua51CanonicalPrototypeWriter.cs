using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Collections.Generic;
using Lunil.Core;
using Lunil.IR.Canonical;
using Lunil.IR.Lua53;

#pragma warning disable CA1720

namespace Lunil.IR.Lua51;

public static class Lua51CanonicalPrototypeWriter
{
    public static byte[] Write(LuaIrModule module, int functionId, bool stripDebug = false)
    {
        var chunk = CreateChunk(module, functionId, stripDebug);
        var bytes = new List<byte>(4096); bytes.AddRange([0x1b, (byte)'L', (byte)'u', (byte)'a', 0x51, 0, 1, 4, 8, 4, 8, 0]);
        WritePrototype(bytes, chunk.MainPrototype, stripDebug); return [.. bytes];
    }
    public static Lua51Chunk CreateChunk(LuaIrModule module, int functionId, bool stripDebug = false)
    {
        LunilGuard.NotNull(module);
        if (module.LanguageVersion != LuaLanguageVersion.Lua51) throw new InvalidDataException("Lua 5.1 writer requires a Lua 5.1 module.");
        var source = Lua53CanonicalPrototypeWriter.CreateChunk(module with { LanguageVersion = LuaLanguageVersion.Lua53 }, functionId);
        var environmentIndexes = FindEnvironmentUpvalueIndexes(module);
        return new(Lua51ChunkTarget.Host, Translate(
            source.MainPrototype,
            module,
            functionId,
            environmentIndexes,
            stripDebug));
    }

    private static Lua51Prototype Translate(
        Lua53Prototype p,
        LuaIrModule module,
        int functionId,
        IReadOnlyDictionary<int, int?> environmentIndexes,
        bool stripDebug)
    {
        var function = module.Functions[functionId];
        if (p.Upvalues.Length != function.Upvalues.Length)
        {
            throw new InvalidDataException(
                $"Lua 5.1 prototype {functionId} does not match its canonical upvalues.");
        }

        var childIds = module.Functions
            .Where(candidate => candidate.ParentFunctionId == functionId)
            .Select(static candidate => candidate.Id)
            .ToImmutableArray();
        if (p.NestedPrototypes.Length != childIds.Length)
        {
            throw new InvalidDataException(
                $"Lua 5.1 prototype {functionId} does not match its canonical children.");
        }

        var environmentIndex = environmentIndexes[functionId];
        return new Lua51Prototype
        {
            Source = p.Source is { } s ? new Lua51String(s.ToArray()) : null,
            LineDefined = p.LineDefined,
            LastLineDefined = p.LastLineDefined,
            UpvalueCount = checked((byte)(p.Upvalues.Length - (environmentIndex.HasValue ? 1 : 0))),
            ParameterCount = p.ParameterCount,
            VarArgFlags = p.VarArgFlags == 0 ? (byte)0 : (byte)2,
            MaximumStackSize = p.MaximumStackSize,
            Code = TranslateCode(p, environmentIndex, childIds, environmentIndexes),
            Constants = p.Constants.Select(Translate).ToImmutableArray(),
            NestedPrototypes = p.NestedPrototypes.Select((nested, index) => Translate(
                nested,
                module,
                childIds[index],
                environmentIndexes,
                stripDebug)).ToImmutableArray(),
            LineInfo = TranslateLineInfo(p, childIds, environmentIndexes, stripDebug),
            LocalVariables = stripDebug ? [] : p.LocalVariables.Select(x => new Lua51LocalVariable(x.Name is { } n ? new Lua51String(n.ToArray()) : null, x.StartProgramCounter, x.EndProgramCounter)).ToImmutableArray(),
            UpvalueNames = TranslateUpvalueNames(p, environmentIndex, stripDebug),
        };
    }

    private static Dictionary<int, int?> FindEnvironmentUpvalueIndexes(LuaIrModule module)
    {
        var indexes = new Dictionary<int, int?>();
        int? Resolve(int functionId)
        {
            if (indexes.TryGetValue(functionId, out var existing))
            {
                return existing;
            }

            var function = module.Functions[functionId];
            int? parentEnvironmentIndex = null;
            if (function.ParentFunctionId >= 0)
            {
                parentEnvironmentIndex = Resolve(function.ParentFunctionId);
            }

            var candidates = function.Upvalues
                .Select(static (upvalue, index) => (upvalue, index))
                .Where(pair => function.ParentFunctionId < 0
                    ? pair.upvalue.SourceKind == LuaIrUpvalueSourceKind.Environment
                    : parentEnvironmentIndex.HasValue &&
                        pair.upvalue.SourceKind == LuaIrUpvalueSourceKind.Upvalue &&
                        pair.upvalue.SourceIndex == parentEnvironmentIndex.Value &&
                        string.Equals(pair.upvalue.Name, "_ENV", StringComparison.Ordinal))
                .Select(static pair => pair.index)
                .ToArray();
            if (candidates.Length > 1)
            {
                throw new InvalidDataException(
                    $"Lua 5.1 function {functionId} has multiple canonical environment upvalues.");
            }

            var result = candidates.Length == 0 ? (int?)null : candidates[0];
            indexes.Add(functionId, result);
            return result;
        }

        foreach (var function in module.Functions)
        {
            Resolve(function.Id);
        }

        return indexes;
    }

    private static ImmutableArray<Lua51String?> TranslateUpvalueNames(
        Lua53Prototype prototype,
        int? environmentIndex,
        bool stripDebug)
    {
        if (stripDebug || prototype.UpvalueNames.IsEmpty)
        {
            return [];
        }

        return prototype.UpvalueNames
            .Where((_, index) => index != environmentIndex)
            .Select(static name => name is { } value
                ? (Lua51String?)new Lua51String(value.ToArray())
                : null)
            .ToImmutableArray();
    }

    private static ImmutableArray<int> TranslateLineInfo(
        Lua53Prototype prototype,
        ImmutableArray<int> childIds,
        IReadOnlyDictionary<int, int?> environmentIndexes,
        bool stripDebug)
    {
        if (stripDebug || prototype.LineInfo.IsEmpty) return [];
        var lines = ImmutableArray.CreateBuilder<int>();
        for (var pc = 0; pc < prototype.Code.Length; pc++)
        {
            var line = prototype.LineInfo[pc];
            lines.Add(line);
            var instruction = prototype.Code[pc];
            if (instruction.Opcode == Lua53Opcode.Jump && instruction.A > 0)
            {
                lines.Add(line);
            }
            if (instruction.Opcode != Lua53Opcode.Closure) continue;
            var childEnvironmentIndex = environmentIndexes[childIds[instruction.Bx]];
            var bindingCount = prototype.NestedPrototypes[instruction.Bx].Upvalues.Length -
                (childEnvironmentIndex.HasValue ? 1 : 0);
            for (var index = 0; index < bindingCount; index++)
                lines.Add(line);
        }
        return lines.ToImmutable();
    }
    private static ImmutableArray<Lua51Instruction> TranslateCode(
        Lua53Prototype prototype,
        int? environmentIndex,
        ImmutableArray<int> childIds,
        IReadOnlyDictionary<int, int?> environmentIndexes)
    {
        var globalAccesses = AnalyzeGlobalAccesses(prototype, environmentIndex);
        var pcMap = BuildPcMap(prototype, childIds, environmentIndexes);
        var count = pcMap[^1];
        var code = ImmutableArray.CreateBuilder<Lua51Instruction>(count);
        for (var pc = 0; pc < prototype.Code.Length; pc++)
        {
            var instruction = prototype.Code[pc];
            if (instruction.Opcode == Lua53Opcode.Jump && instruction.A > 0)
            {
                var target = pc + 1 + instruction.SignedBx;
                code.Add(Lua51Instruction.CreateAbc(
                    Lua51Opcode.Close,
                    instruction.A - 1,
                    0,
                    0));
                code.Add(Lua51Instruction.CreateASignedBx(
                    Lua51Opcode.Jump,
                    0,
                    pcMap[target] - (pcMap[pc] + 2)));
            }
            else if (instruction.Opcode == Lua53Opcode.GenericForLoop)
            {
                var target = pc + 1 + instruction.SignedBx;
                code.Add(Lua51Instruction.CreateASignedBx(
                    Lua51Opcode.Jump,
                    0,
                    pcMap[target] - (pcMap[pc] + 1)));
            }
            else if (instruction.Opcode is Lua53Opcode.Jump or Lua53Opcode.NumericForLoop or Lua53Opcode.NumericForPrepare)
            {
                var target = pc + 1 + instruction.SignedBx;
                var mapped = pcMap[target] - (pcMap[pc] + 1);
                code.Add(Lua51Instruction.CreateASignedBx(Map(instruction.Opcode), instruction.A, mapped));
            }
            else if (instruction.Opcode == Lua53Opcode.Return &&
                pc > 0 &&
                prototype.Code[pc - 1].Opcode == Lua53Opcode.TailCall)
            {
                // Lua 5.1's verifier treats TAILCALL as an open call and requires the
                // following (unreachable) RETURN to consume the open result range.
                code.Add(Lua51Instruction.CreateAbc(
                    Lua51Opcode.Return,
                    instruction.A,
                    0,
                    0));
            }
            else if (globalAccesses.TryGetValue(pc, out var globalAccess))
            {
                code.Add(globalAccess.IsSet
                    ? Lua51Instruction.CreateABx(
                        Lua51Opcode.SetGlobal,
                        instruction.C,
                        globalAccess.ConstantIndex)
                    : Lua51Instruction.CreateABx(
                        Lua51Opcode.GetGlobal,
                        instruction.A,
                        globalAccess.ConstantIndex));
            }
            else code.Add(Translate(instruction, environmentIndex));
            if (instruction.Opcode != Lua53Opcode.Closure) continue;
            var childEnvironmentIndex = environmentIndexes[childIds[instruction.Bx]];
            foreach (var (upvalue, index) in prototype.NestedPrototypes[instruction.Bx].Upvalues
                .Select(static (upvalue, index) => (upvalue, index)))
            {
                if (index == childEnvironmentIndex)
                {
                    continue;
                }

                code.Add(Lua51Instruction.CreateAbc(
                    upvalue.InStack != 0 ? Lua51Opcode.Move : Lua51Opcode.GetUpvalue,
                    0,
                    upvalue.InStack != 0
                        ? upvalue.Index
                        : RemapUpvalueIndex(upvalue.Index, environmentIndex),
                    0));
            }
        }
        return code.MoveToImmutable();
    }

    private static int[] BuildPcMap(
        Lua53Prototype prototype,
        ImmutableArray<int> childIds,
        IReadOnlyDictionary<int, int?> environmentIndexes)
    {
        var pcMap = new int[prototype.Code.Length + 1];
        var count = 0;
        for (var pc = 0; pc < prototype.Code.Length; pc++)
        {
            pcMap[pc] = count++;
            var instruction = prototype.Code[pc];
            if (instruction.Opcode == Lua53Opcode.Closure)
            {
                count += prototype.NestedPrototypes[instruction.Bx].Upvalues.Length -
                    (environmentIndexes[childIds[instruction.Bx]].HasValue ? 1 : 0);
            }
            else if (instruction.Opcode == Lua53Opcode.Jump && instruction.A > 0)
            {
                count++;
            }
        }

        pcMap[^1] = count;
        return pcMap;
    }

    private static Dictionary<int, GlobalAccess> AnalyzeGlobalAccesses(
        Lua53Prototype prototype,
        int? environmentIndex)
    {
        var accesses = new Dictionary<int, GlobalAccess>();
        if (!environmentIndex.HasValue)
        {
            return accesses;
        }

        var consumedEnvironmentLoads = new HashSet<int>();
        for (var pc = 0; pc < prototype.Code.Length; pc++)
        {
            var instruction = prototype.Code[pc];
            var environmentRegister = instruction.Opcode switch
            {
                Lua53Opcode.GetTable => instruction.B,
                Lua53Opcode.SetTable => instruction.A,
                _ => -1,
            };
            if (environmentRegister < 0)
            {
                continue;
            }

            var environmentDefinition = FindRegisterDefinition(
                prototype,
                pc,
                environmentRegister);
            if (environmentDefinition < 0 ||
                prototype.Code[environmentDefinition] is not
                {
                    Opcode: Lua53Opcode.GetUpvalue,
                    B: var upvalueIndex,
                } ||
                upvalueIndex != environmentIndex.Value)
            {
                continue;
            }

            var keyRegister = instruction.Opcode == Lua53Opcode.GetTable
                ? instruction.C
                : instruction.B;
            var keyDefinition = FindRegisterDefinition(prototype, pc, keyRegister);
            if (keyDefinition < 0 ||
                prototype.Code[keyDefinition].Opcode != Lua53Opcode.LoadConstant)
            {
                throw new InvalidDataException(
                    "Lua 5.1 function-environment access requires a constant string key.");
            }

            var constantIndex = prototype.Code[keyDefinition].Bx;
            if ((uint)constantIndex >= (uint)prototype.Constants.Length ||
                prototype.Constants[constantIndex].Kind is not
                    (Lua53ConstantKind.ShortString or Lua53ConstantKind.LongString))
            {
                throw new InvalidDataException(
                    "Lua 5.1 function-environment access requires a constant string key.");
            }

            accesses.Add(pc, new GlobalAccess(
                instruction.Opcode == Lua53Opcode.SetTable,
                constantIndex));
            consumedEnvironmentLoads.Add(environmentDefinition);
        }

        for (var pc = 0; pc < prototype.Code.Length; pc++)
        {
            if (prototype.Code[pc] is
                {
                    Opcode: Lua53Opcode.GetUpvalue,
                    B: var upvalueIndex,
                } &&
                upvalueIndex == environmentIndex.Value &&
                !consumedEnvironmentLoads.Contains(pc))
            {
                throw new InvalidDataException(
                    "A canonical Lua 5.1 environment value cannot be materialized directly.");
            }
        }

        return accesses;
    }

    private static int FindRegisterDefinition(
        Lua53Prototype prototype,
        int programCounter,
        int register)
    {
        for (var pc = programCounter - 1; pc >= 0; pc--)
        {
            if (WritesRegister(prototype.Code[pc], register))
            {
                return pc;
            }
        }

        return -1;
    }

    private static bool WritesRegister(Lua53Instruction instruction, int register) =>
        instruction.Opcode switch
        {
            Lua53Opcode.LoadNil => register >= instruction.A &&
                register <= instruction.A + instruction.B,
            Lua53Opcode.Self => register == instruction.A || register == instruction.A + 1,
            Lua53Opcode.Call => register >= instruction.A &&
                (instruction.C == 0 || register < instruction.A + instruction.C - 1),
            Lua53Opcode.NumericForLoop => register == instruction.A ||
                register == instruction.A + 3,
            Lua53Opcode.NumericForPrepare => register == instruction.A,
            Lua53Opcode.GenericForCall => register >= instruction.A + 3 &&
                register < instruction.A + 3 + instruction.C,
            Lua53Opcode.GenericForLoop => register == instruction.A,
            Lua53Opcode.VarArg => register >= instruction.A &&
                (instruction.C == 0 || register < instruction.A + instruction.C - 1),
            Lua53Opcode.Move or Lua53Opcode.LoadConstant or Lua53Opcode.LoadConstantExtra or
                Lua53Opcode.LoadBoolean or Lua53Opcode.GetUpvalue or Lua53Opcode.GetGlobal or
                Lua53Opcode.GetTable or Lua53Opcode.NewTable or Lua53Opcode.Add or
                Lua53Opcode.Subtract or Lua53Opcode.Multiply or Lua53Opcode.Modulo or
                Lua53Opcode.Power or Lua53Opcode.Divide or Lua53Opcode.FloorDivide or
                Lua53Opcode.BitwiseAnd or Lua53Opcode.BitwiseOr or Lua53Opcode.BitwiseXor or
                Lua53Opcode.ShiftLeft or Lua53Opcode.ShiftRight or Lua53Opcode.UnaryMinus or
                Lua53Opcode.BitwiseNot or Lua53Opcode.LogicalNot or Lua53Opcode.Length or
                Lua53Opcode.Concatenate or Lua53Opcode.TestSet or Lua53Opcode.Closure =>
                register == instruction.A,
            _ => false,
        };

    private static int RemapUpvalueIndex(int upvalueIndex, int? environmentIndex)
    {
        if (!environmentIndex.HasValue)
        {
            return upvalueIndex;
        }

        if (upvalueIndex == environmentIndex.Value)
        {
            throw new InvalidDataException(
                "Lua 5.1 function-environment upvalues are implicit and cannot be bound directly.");
        }

        return upvalueIndex > environmentIndex.Value ? upvalueIndex - 1 : upvalueIndex;
    }

    private static Lua51Instruction Translate(
        Lua53Instruction i,
        int? environmentIndex)
    {
        if (i.Opcode == Lua53Opcode.GetUpvalue && i.B == environmentIndex)
        {
            // Keep the instruction count stable for jump/debug mappings. The register is dead
            // after the associated table access is restored to GETGLOBAL/SETGLOBAL.
            return Lua51Instruction.CreateAbc(Lua51Opcode.LoadNil, i.A, i.A, 0);
        }

        if (i.Opcode is Lua53Opcode.GetUpvalue or Lua53Opcode.SetUpvalue)
        {
            return Lua51Instruction.CreateAbc(
                Map(i.Opcode),
                i.A,
                RemapUpvalueIndex(i.B, environmentIndex),
                i.C);
        }

        return i.Opcode switch
        {
            Lua53Opcode.GetGlobal => Lua51Instruction.CreateABx(Lua51Opcode.GetGlobal, i.A, i.C & 0xff),
            Lua53Opcode.SetTableUpvalue => Lua51Instruction.CreateABx(Lua51Opcode.SetGlobal, i.C, i.B & 0xff),
            Lua53Opcode.LoadConstant => Lua51Instruction.CreateABx(Lua51Opcode.LoadConstant, i.A, i.Bx),
            Lua53Opcode.LoadNil => Lua51Instruction.CreateAbc(
                Lua51Opcode.LoadNil,
                i.A,
                checked(i.A + i.B),
                0),
            Lua53Opcode.Jump or Lua53Opcode.NumericForLoop or Lua53Opcode.NumericForPrepare => Lua51Instruction.CreateASignedBx(Map(i.Opcode), i.A, i.SignedBx),
            _ => Lua51Instruction.CreateAbc(Map(i.Opcode), i.A, i.B, i.C),
        };
    }

    private readonly record struct GlobalAccess(bool IsSet, int ConstantIndex);
    private static Lua51Opcode Map(Lua53Opcode op) => op switch
    {
        Lua53Opcode.Move => Lua51Opcode.Move,
        Lua53Opcode.LoadConstant => Lua51Opcode.LoadConstant,
        Lua53Opcode.LoadBoolean => Lua51Opcode.LoadBoolean,
        Lua53Opcode.LoadNil => Lua51Opcode.LoadNil,
        Lua53Opcode.GetUpvalue => Lua51Opcode.GetUpvalue,
        Lua53Opcode.GetTable => Lua51Opcode.GetTable,
        Lua53Opcode.SetUpvalue => Lua51Opcode.SetUpvalue,
        Lua53Opcode.SetTable => Lua51Opcode.SetTable,
        Lua53Opcode.NewTable => Lua51Opcode.NewTable,
        Lua53Opcode.Self => Lua51Opcode.Self,
        Lua53Opcode.Add => Lua51Opcode.Add,
        Lua53Opcode.Subtract => Lua51Opcode.Subtract,
        Lua53Opcode.Multiply => Lua51Opcode.Multiply,
        Lua53Opcode.Divide => Lua51Opcode.Divide,
        Lua53Opcode.Modulo => Lua51Opcode.Modulo,
        Lua53Opcode.Power => Lua51Opcode.Power,
        Lua53Opcode.UnaryMinus => Lua51Opcode.UnaryMinus,
        Lua53Opcode.LogicalNot => Lua51Opcode.LogicalNot,
        Lua53Opcode.Length => Lua51Opcode.Length,
        Lua53Opcode.Concatenate => Lua51Opcode.Concatenate,
        Lua53Opcode.Jump => Lua51Opcode.Jump,
        Lua53Opcode.Equal => Lua51Opcode.Equal,
        Lua53Opcode.LessThan => Lua51Opcode.LessThan,
        Lua53Opcode.LessOrEqual => Lua51Opcode.LessOrEqual,
        Lua53Opcode.Test => Lua51Opcode.Test,
        Lua53Opcode.TestSet => Lua51Opcode.TestSet,
        Lua53Opcode.Call => Lua51Opcode.Call,
        Lua53Opcode.TailCall => Lua51Opcode.TailCall,
        Lua53Opcode.Return => Lua51Opcode.Return,
        Lua53Opcode.NumericForLoop => Lua51Opcode.NumericForLoop,
        Lua53Opcode.NumericForPrepare => Lua51Opcode.NumericForPrepare,
        Lua53Opcode.GenericForCall => Lua51Opcode.GenericForLoop,
        Lua53Opcode.GenericForLoop => Lua51Opcode.Jump,
        Lua53Opcode.SetList => Lua51Opcode.SetList,
        Lua53Opcode.Closure => Lua51Opcode.Closure,
        Lua53Opcode.VarArg => Lua51Opcode.VarArg,
        _ => throw new InvalidDataException($"Opcode {op} is not representable in Lua 5.1"),
    };
    private static Lua51Constant Translate(Lua53Constant c) => c.Kind switch
    {
        Lua53ConstantKind.Nil => Lua51Constant.Nil,
        Lua53ConstantKind.False => Lua51Constant.False,
        Lua53ConstantKind.True => Lua51Constant.True,
        Lua53ConstantKind.Integer => Lua51Constant.FromNumber(c.IntegerValue),
        Lua53ConstantKind.Float => Lua51Constant.FromNumber(c.FloatValue),
        Lua53ConstantKind.ShortString or Lua53ConstantKind.LongString => Lua51Constant.FromString(new Lua51String(c.StringValue!.Value.ToArray())),
        _ => throw new InvalidDataException("Unknown constant kind"),
    };
    private static void WritePrototype(List<byte> b, Lua51Prototype p, bool strip)
    {
        WriteString(b, strip ? null : p.Source); WriteInt(b, p.LineDefined); WriteInt(b, p.LastLineDefined); b.Add(p.UpvalueCount); b.Add(p.ParameterCount); b.Add(p.VarArgFlags); b.Add(p.MaximumStackSize);
        WriteInt(b, p.Code.Length); foreach (var i in p.Code) WriteUInt(b, i.RawValue); WriteInt(b, p.Constants.Length); foreach (var c in p.Constants) WriteConstant(b, c);
        WriteInt(b, p.NestedPrototypes.Length); foreach (var n in p.NestedPrototypes) WritePrototype(b, n, strip); WriteInt(b, strip ? 0 : p.LineInfo.Length); if (!strip) foreach (var x in p.LineInfo) WriteInt(b, x);
        WriteInt(b, strip ? 0 : p.LocalVariables.Length); if (!strip) foreach (var l in p.LocalVariables) { WriteString(b, l.Name); WriteInt(b, l.StartProgramCounter); WriteInt(b, l.EndProgramCounter); }
        WriteInt(b, strip ? 0 : p.UpvalueNames.Length); if (!strip) foreach (var n in p.UpvalueNames) WriteString(b, n);
    }
    private static void WriteConstant(List<byte> b, Lua51Constant c) { b.Add(c.Kind switch { Lua51ConstantKind.Nil => (byte)0, Lua51ConstantKind.False or Lua51ConstantKind.True => (byte)1, Lua51ConstantKind.Number => (byte)3, Lua51ConstantKind.String => (byte)4, _ => throw new InvalidDataException() }); if (c.Kind is Lua51ConstantKind.False or Lua51ConstantKind.True) b.Add((byte)(c.Kind == Lua51ConstantKind.True ? 1 : 0)); else if (c.Kind == Lua51ConstantKind.Number) { Span<byte> x = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(x, BitConverter.DoubleToInt64Bits(c.NumberValue)); b.AddRange(x.ToArray()); } else if (c.Kind == Lua51ConstantKind.String) WriteString(b, c.StringValue); }
    private static void WriteString(List<byte> b, Lua51String? s) { if (s is not { } v) { WriteSizeT(b, 0); return; } WriteSizeT(b, checked((ulong)v.Length + 1)); b.AddRange(v.Bytes); b.Add(0); }
    private static void WriteInt(List<byte> b, int x) { Span<byte> v = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(v, x); b.AddRange(v.ToArray()); }
    private static void WriteUInt(List<byte> b, uint x) { Span<byte> v = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(v, x); b.AddRange(v.ToArray()); }
    private static void WriteSizeT(List<byte> b, ulong x) { Span<byte> v = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(v, x); b.AddRange(v.ToArray()); }
}
