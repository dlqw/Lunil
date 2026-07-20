using System.Collections.Immutable;
using System.Text;
using Lunil.Core;
using Lunil.IR.Canonical;

namespace Lunil.IR.Lua53;

public static class Lua53PrototypeConverter
{
    private const int MinimumScratchRegisterCount = 3;

    public static LuaIrModule Convert(
        ReadOnlySpan<byte> binaryChunk,
        Lua53ChunkReaderOptions? options = null) =>
        Convert(Lua53ChunkReader.Read(binaryChunk, options));

    public static LuaIrModule Convert(Lua53Chunk chunk)
        => Convert(chunk, LuaLanguageVersion.Lua53);

    internal static LuaIrModule Convert(Lua53Chunk chunk, LuaLanguageVersion languageVersion)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var prototypes = new List<PrototypeEntry>();
        var ids = new Dictionary<Lua53Prototype, int>(ReferenceEqualityComparer.Instance);
        AddPrototype(chunk.MainPrototype, -1, prototypes, ids);

        var functions = ImmutableArray.CreateBuilder<LuaIrFunction>(prototypes.Count);
        foreach (var entry in prototypes)
        {
            functions.Add(new FunctionConverter(entry, ids).Convert());
        }

        var module = new LuaIrModule
        {
            LanguageVersion = languageVersion,
            MainFunctionId = 0,
            Functions = functions.MoveToImmutable(),
        };
        var errors = LuaIrVerifier.Verify(module);
        if (!errors.IsEmpty)
        {
            var first = errors[0];
            throw new InvalidDataException(
                $"Converted {LuaLanguageVersions.GetDisplayName(languageVersion)} canonical IR is invalid " +
                $"in function {first.FunctionId} " +
                $"at instruction {first.ProgramCounter}: {first.Message}");
        }

        return module;
    }

    private static void AddPrototype(
        Lua53Prototype prototype,
        int parentId,
        List<PrototypeEntry> entries,
        Dictionary<Lua53Prototype, int> ids)
    {
        var id = entries.Count;
        ids.Add(prototype, id);
        entries.Add(new PrototypeEntry(prototype, id, parentId));
        foreach (var nested in prototype.NestedPrototypes)
        {
            AddPrototype(nested, id, entries, ids);
        }
    }

    private sealed record PrototypeEntry(Lua53Prototype Prototype, int Id, int ParentId);

    private sealed class FunctionConverter
    {
        private readonly PrototypeEntry _entry;
        private readonly Dictionary<Lua53Prototype, int> _ids;
        private readonly ImmutableArray<LuaIrConstant>.Builder _constants;
        private readonly List<LuaIrInstruction> _instructions = [];
        private readonly List<JumpPatch> _patches = [];
        private readonly int[] _programCounterMap;
        private readonly int[] _sourceLines;
        private readonly int _scratchRegisterCount;
        private int _sourceProgramCounter;

        public FunctionConverter(
            PrototypeEntry entry,
            Dictionary<Lua53Prototype, int> ids)
        {
            _entry = entry;
            _ids = ids;
            _constants = ImmutableArray.CreateBuilder<LuaIrConstant>(entry.Prototype.Constants.Length);
            foreach (var constant in entry.Prototype.Constants)
            {
                _constants.Add(ConvertConstant(constant));
            }

            _programCounterMap = new int[entry.Prototype.Code.Length + 1];
            _scratchRegisterCount = Math.Max(
                MinimumScratchRegisterCount,
                entry.Prototype.Code
                    .Where(static instruction => instruction.Opcode == Lua53Opcode.GenericForCall)
                    .Select(static instruction => instruction.C)
                    .DefaultIfEmpty()
                    .Max());
            _sourceLines = entry.Prototype.LineInfo.IsEmpty
                ? new int[entry.Prototype.Code.Length]
                : entry.Prototype.LineInfo.ToArray();
            if (_sourceLines.Length != entry.Prototype.Code.Length)
            {
                throw new InvalidDataException("Lua 5.3 line info count must match code count.");
            }
        }

        private Lua53Prototype Prototype => _entry.Prototype;
        private int Scratch0 => Prototype.MaximumStackSize;
        private int Scratch1 => Prototype.MaximumStackSize + 1;
        private int Scratch2 => Prototype.MaximumStackSize + 2;

        public LuaIrFunction Convert()
        {
            for (_sourceProgramCounter = 0;
                 _sourceProgramCounter < Prototype.Code.Length;
                 _sourceProgramCounter++)
            {
                _programCounterMap[_sourceProgramCounter] = _instructions.Count;
                ConvertInstruction(Prototype.Code[_sourceProgramCounter]);
                if (IsConditional(Prototype.Code[_sourceProgramCounter]) &&
                    _sourceProgramCounter + 1 < Prototype.Code.Length &&
                    Prototype.Code[_sourceProgramCounter + 1].Opcode == Lua53Opcode.Jump)
                {
                    _programCounterMap[++_sourceProgramCounter] = _instructions.Count;
                }
            }

            _programCounterMap[^1] = _instructions.Count;
            PatchJumps();
            var instructions = _instructions.ToImmutableArray();
            return new LuaIrFunction
            {
                Id = _entry.Id,
                ParentFunctionId = _entry.ParentId,
                Span = default,
                SourceName = Prototype.Source is { } source ? [.. source.AsSpan()] : [],
                LineDefined = Prototype.LineDefined,
                LastLineDefined = Prototype.LastLineDefined,
                ParameterCount = Prototype.ParameterCount,
                IsVarArg = Prototype.VarArgFlags != 0,
                RegisterCount = checked(Prototype.MaximumStackSize + _scratchRegisterCount),
                Constants = _constants.ToImmutable(),
                Upvalues = ConvertUpvalues(),
                Instructions = instructions,
                LocalVariables = ConvertLocalVariables(),
                BasicBlocks = LuaIrControlFlow.Build(instructions),
            };
        }

        private void ConvertInstruction(Lua53Instruction instruction)
        {
            if (!Lua53GeneratedOpcodeTable.IsDefined(instruction.Opcode))
            {
                throw new InvalidDataException(
                    $"Unsupported Lua 5.3 opcode {Lua53GeneratedOpcodeTable.GetName(instruction.Opcode)}.");
            }

            switch (instruction.Opcode)
            {
                case Lua53Opcode.Move:
                    Emit(LuaIrOpcode.Move, instruction.A, instruction.B);
                    break;
                case Lua53Opcode.LoadConstant:
                    Emit(LuaIrOpcode.LoadConstant, instruction.A, instruction.Bx);
                    break;
                case Lua53Opcode.LoadConstantExtra:
                    Emit(LuaIrOpcode.LoadConstant, instruction.A,
                        Prototype.Code[_sourceProgramCounter + 1].Ax);
                    break;
                case Lua53Opcode.LoadBoolean:
                    EmitLoadConstant(instruction.A, LuaIrConstant.FromBoolean(instruction.B != 0));
                    if (instruction.C != 0)
                    {
                        EmitJump(_sourceProgramCounter + 2);
                    }

                    break;
                case Lua53Opcode.LoadNil:
                    Emit(LuaIrOpcode.LoadNil, instruction.A, instruction.B + 1);
                    break;
                case Lua53Opcode.GetUpvalue:
                    Emit(LuaIrOpcode.GetUpvalue, instruction.A, instruction.B);
                    break;
                case Lua53Opcode.GetGlobal:
                    Emit(LuaIrOpcode.GetUpvalue, Scratch0, instruction.B);
                    EmitRk(Scratch1, instruction.C, instruction.IsConstantC);
                    Emit(LuaIrOpcode.GetTable, instruction.A, Scratch0, Scratch1);
                    break;
                case Lua53Opcode.GetTable:
                    EmitRk(Scratch0, instruction.C, instruction.IsConstantC);
                    Emit(LuaIrOpcode.GetTable, instruction.A, instruction.B, Scratch0);
                    break;
                case Lua53Opcode.SetTableUpvalue:
                    Emit(LuaIrOpcode.GetUpvalue, Scratch0, instruction.A);
                    EmitRk(Scratch1, instruction.B, instruction.IsConstantB);
                    EmitRk(Scratch2, instruction.C, instruction.IsConstantC);
                    Emit(LuaIrOpcode.SetTable, Scratch0, Scratch1, Scratch2);
                    break;
                case Lua53Opcode.SetUpvalue:
                    Emit(LuaIrOpcode.SetUpvalue, instruction.B, instruction.A);
                    break;
                case Lua53Opcode.SetTable:
                    EmitRk(Scratch0, instruction.B, instruction.IsConstantB);
                    EmitRk(Scratch1, instruction.C, instruction.IsConstantC);
                    Emit(LuaIrOpcode.SetTable, instruction.A, Scratch0, Scratch1);
                    break;
                case Lua53Opcode.NewTable:
                    Emit(LuaIrOpcode.NewTable, instruction.A,
                        EncodeHashAllocationHint(DecodeFloatingByte(instruction.C)),
                        DecodeFloatingByte(instruction.B));
                    break;
                case Lua53Opcode.Self:
                    Emit(LuaIrOpcode.Move, instruction.A + 1, instruction.B);
                    EmitRk(Scratch0, instruction.C, instruction.IsConstantC);
                    Emit(LuaIrOpcode.GetTable, instruction.A, instruction.B, Scratch0);
                    break;
                case Lua53Opcode.Add:
                    EmitBinaryRk(instruction, LuaIrBinaryOperator.Add);
                    break;
                case Lua53Opcode.Subtract:
                    EmitBinaryRk(instruction, LuaIrBinaryOperator.Subtract);
                    break;
                case Lua53Opcode.Multiply:
                    EmitBinaryRk(instruction, LuaIrBinaryOperator.Multiply);
                    break;
                case Lua53Opcode.Modulo:
                    EmitBinaryRk(instruction, LuaIrBinaryOperator.Modulo);
                    break;
                case Lua53Opcode.Power:
                    EmitBinaryRk(instruction, LuaIrBinaryOperator.Power);
                    break;
                case Lua53Opcode.Divide:
                    EmitBinaryRk(instruction, LuaIrBinaryOperator.Divide);
                    break;
                case Lua53Opcode.FloorDivide:
                    EmitBinaryRk(instruction, LuaIrBinaryOperator.FloorDivide);
                    break;
                case Lua53Opcode.BitwiseAnd:
                    EmitBinaryRk(instruction, LuaIrBinaryOperator.BitwiseAnd);
                    break;
                case Lua53Opcode.BitwiseOr:
                    EmitBinaryRk(instruction, LuaIrBinaryOperator.BitwiseOr);
                    break;
                case Lua53Opcode.BitwiseXor:
                    EmitBinaryRk(instruction, LuaIrBinaryOperator.BitwiseXor);
                    break;
                case Lua53Opcode.ShiftLeft:
                    EmitBinaryRk(instruction, LuaIrBinaryOperator.ShiftLeft);
                    break;
                case Lua53Opcode.ShiftRight:
                    EmitBinaryRk(instruction, LuaIrBinaryOperator.ShiftRight);
                    break;
                case Lua53Opcode.UnaryMinus:
                    Emit(LuaIrOpcode.Unary, instruction.A, instruction.B,
                        (int)LuaIrUnaryOperator.Negate);
                    break;
                case Lua53Opcode.BitwiseNot:
                    Emit(LuaIrOpcode.Unary, instruction.A, instruction.B,
                        (int)LuaIrUnaryOperator.BitwiseNot);
                    break;
                case Lua53Opcode.LogicalNot:
                    Emit(LuaIrOpcode.Unary, instruction.A, instruction.B,
                        (int)LuaIrUnaryOperator.LogicalNot);
                    break;
                case Lua53Opcode.Length:
                    Emit(LuaIrOpcode.Unary, instruction.A, instruction.B,
                        (int)LuaIrUnaryOperator.Length);
                    break;
                case Lua53Opcode.Concatenate:
                    for (var register = instruction.C - 1; register >= instruction.B; register--)
                    {
                        Emit(LuaIrOpcode.Binary, register, register, register + 1,
                            (int)LuaIrBinaryOperator.Concatenate);
                    }

                    if (instruction.A != instruction.B)
                    {
                        Emit(LuaIrOpcode.Move, instruction.A, instruction.B);
                    }

                    break;
                case Lua53Opcode.Jump:
                    EmitJump(checked(_sourceProgramCounter + 1 + instruction.SignedBx));
                    break;
                case Lua53Opcode.Equal:
                    EmitComparison(instruction, LuaIrBinaryOperator.Equal);
                    break;
                case Lua53Opcode.LessThan:
                    EmitComparison(instruction, LuaIrBinaryOperator.LessThan);
                    break;
                case Lua53Opcode.LessOrEqual:
                    EmitComparison(instruction, LuaIrBinaryOperator.LessThanOrEqual);
                    break;
                case Lua53Opcode.Test:
                    EmitConditionalJump(instruction.C != 0, instruction.A, GetCompanionJumpTarget());
                    break;
                case Lua53Opcode.TestSet:
                    EmitConditionalJump(instruction.C == 0, instruction.B,
                        _sourceProgramCounter + 2);
                    Emit(LuaIrOpcode.Move, instruction.A, instruction.B);
                    EmitJump(GetCompanionJumpTarget());
                    break;
                case Lua53Opcode.Call:
                    Emit(LuaIrOpcode.Call, instruction.A,
                        instruction.B == 0 ? -1 : instruction.B - 1,
                        instruction.C == 0 ? -1 : instruction.C - 1);
                    break;
                case Lua53Opcode.TailCall:
                    Emit(LuaIrOpcode.TailCall, instruction.A,
                        instruction.B == 0 ? -1 : instruction.B - 1);
                    break;
                case Lua53Opcode.Return:
                    Emit(LuaIrOpcode.Return, instruction.A,
                        instruction.B == 0 ? -1 : instruction.B - 1);
                    break;
                case Lua53Opcode.NumericForLoop:
                    EmitTargeted(LuaIrOpcode.NumericForLoop, instruction.A,
                        checked(_sourceProgramCounter + 1 + instruction.SignedBx));
                    break;
                case Lua53Opcode.NumericForPrepare:
                    EmitTargeted(LuaIrOpcode.NumericForPrepare, instruction.A,
                        checked(_sourceProgramCounter + 1 + instruction.SignedBx));
                    break;
                case Lua53Opcode.GenericForCall:
                    Emit(LuaIrOpcode.Move, Scratch0, instruction.A);
                    Emit(LuaIrOpcode.Move, Scratch1, instruction.A + 1);
                    Emit(LuaIrOpcode.Move, Scratch2, instruction.A + 2);
                    Emit(LuaIrOpcode.Call, Scratch0, 2, instruction.C,
                        (int)LuaIrCallKind.ForIterator);
                    for (var result = 0; result < instruction.C; result++)
                    {
                        Emit(LuaIrOpcode.Move, instruction.A + 3 + result, Scratch0 + result);
                    }

                    break;
                case Lua53Opcode.GenericForLoop:
                    EmitConditionalJump(false, instruction.A + 1,
                        checked(_sourceProgramCounter + 1));
                    Emit(LuaIrOpcode.Move, instruction.A, instruction.A + 1);
                    EmitJump(checked(_sourceProgramCounter + 1 + instruction.SignedBx));
                    break;
                case Lua53Opcode.SetList:
                    var block = instruction.C;
                    if (block == 0)
                    {
                        block = Prototype.Code[_sourceProgramCounter + 1].Ax;
                    }

                    Emit(LuaIrOpcode.SetList, instruction.A,
                        checked((block - 1) * 50 + 1), instruction.A + 1,
                        instruction.B == 0 ? -1 : instruction.B);
                    break;
                case Lua53Opcode.Closure:
                    Emit(LuaIrOpcode.Closure, instruction.A,
                        _ids[Prototype.NestedPrototypes[instruction.Bx]]);
                    break;
                case Lua53Opcode.VarArg:
                    Emit(LuaIrOpcode.VarArg, instruction.A,
                        instruction.C == 0 ? -1 : instruction.C - 1);
                    break;
                case Lua53Opcode.ExtraArgument:
                    break;
                default:
                    throw new InvalidDataException($"Unsupported Lua 5.3 opcode {instruction.Opcode}.");
            }
        }

        private void EmitComparison(Lua53Instruction instruction, LuaIrBinaryOperator operation)
        {
            EmitRk(Scratch0, instruction.B, instruction.IsConstantB);
            EmitRk(Scratch1, instruction.C, instruction.IsConstantC);
            Emit(LuaIrOpcode.Binary, Scratch2, Scratch0, Scratch1, (int)operation);
            if (_sourceProgramCounter + 1 < Prototype.Code.Length &&
                Prototype.Code[_sourceProgramCounter + 1].Opcode == Lua53Opcode.Jump)
            {
                EmitConditionalJump(instruction.A != 0, Scratch2, GetCompanionJumpTarget());
                return;
            }

            var result = _sourceProgramCounter + 1 < Prototype.Code.Length
                ? Prototype.Code[_sourceProgramCounter + 1]
                : default;
            if (result.Opcode != Lua53Opcode.LoadBoolean || result.B != 1)
            {
                throw new InvalidDataException(
                    "Lua 5.3 comparison must be followed by a conditional jump or boolean materialization.");
            }

            var materializedOperation = instruction.A != 0
                ? operation
                : InvertComparison(operation);
            _instructions.RemoveAt(_instructions.Count - 1);
            Emit(LuaIrOpcode.Binary, Scratch2, Scratch0, Scratch1,
                (int)materializedOperation);
            EmitConditionalJump(false, Scratch2, _sourceProgramCounter + 2);
        }

        private static LuaIrBinaryOperator InvertComparison(LuaIrBinaryOperator operation) => operation switch
        {
            LuaIrBinaryOperator.Equal => LuaIrBinaryOperator.NotEqual,
            LuaIrBinaryOperator.LessThan => LuaIrBinaryOperator.GreaterThanOrEqual,
            LuaIrBinaryOperator.LessThanOrEqual => LuaIrBinaryOperator.GreaterThan,
            _ => throw new InvalidDataException("Unknown comparison operator."),
        };

        private static int DecodeFloatingByte(int value)
        {
            if ((uint)value > byte.MaxValue)
            {
                throw new InvalidDataException("Lua 5.3 table allocation hint exceeds the floating-byte field.");
            }

            if (value < 8)
            {
                return value;
            }

            var decoded = checked(((long)(value & 7) + 8) << ((value >> 3) - 1));
            if (decoded > int.MaxValue)
            {
                throw new InvalidDataException("Lua 5.3 table allocation hint exceeds the runtime limit.");
            }

            return (int)decoded;
        }

        private static int EncodeHashAllocationHint(int entryCount)
        {
            if (entryCount == 0)
            {
                return 0;
            }

            var size = 1L;
            var exponent = 1;
            while (size < entryCount)
            {
                size <<= 1;
                exponent++;
            }

            return exponent;
        }

        private void EmitBinaryRk(Lua53Instruction instruction, LuaIrBinaryOperator operation)
        {
            EmitRk(Scratch0, instruction.B, instruction.IsConstantB);
            EmitRk(Scratch1, instruction.C, instruction.IsConstantC);
            Emit(LuaIrOpcode.Binary, instruction.A, Scratch0, Scratch1, (int)operation);
        }

        private void EmitRk(int destination, int operand, bool isConstant)
        {
            if (isConstant)
            {
                Emit(LuaIrOpcode.LoadConstant, destination, operand & 0xff);
            }
            else
            {
                Emit(LuaIrOpcode.Move, destination, operand & 0xff);
            }
        }

        private void EmitLoadConstant(int register, LuaIrConstant value)
        {
            var index = _constants.Count;
            _constants.Add(value);
            Emit(LuaIrOpcode.LoadConstant, register, index);
        }

        private void EmitConditionalJump(bool whenTrue, int register, int rawTarget)
        {
            var index = _instructions.Count;
            Emit(whenTrue ? LuaIrOpcode.JumpIfTrue : LuaIrOpcode.JumpIfFalse, register);
            _patches.Add(new JumpPatch(index, rawTarget));
        }

        private void EmitJump(int rawTarget)
        {
            var index = _instructions.Count;
            Emit(LuaIrOpcode.Jump);
            _patches.Add(new JumpPatch(index, rawTarget));
        }

        private void EmitTargeted(LuaIrOpcode opcode, int register, int rawTarget)
        {
            var index = _instructions.Count;
            Emit(opcode, register);
            _patches.Add(new JumpPatch(index, rawTarget));
        }

        private void Emit(LuaIrOpcode opcode, int a = 0, int b = 0, int c = 0, int d = 0) =>
            _instructions.Add(new LuaIrInstruction(
                opcode,
                a,
                b,
                c,
                d,
                sourceLine: _sourceLines[_sourceProgramCounter],
                logicalProgramCounter: _sourceProgramCounter));

        private int GetCompanionJumpTarget()
        {
            var jump = Prototype.Code[_sourceProgramCounter + 1];
            if (jump.Opcode != Lua53Opcode.Jump)
            {
                throw new InvalidDataException("Lua 5.3 conditional instruction is not followed by JMP.");
            }

            return checked(_sourceProgramCounter + 2 + jump.SignedBx);
        }

        private static bool IsConditional(Lua53Instruction instruction) =>
            instruction.Opcode is Lua53Opcode.Equal or Lua53Opcode.LessThan or
                Lua53Opcode.LessOrEqual or Lua53Opcode.Test or Lua53Opcode.TestSet;

        private void PatchJumps()
        {
            foreach (var patch in _patches)
            {
                if ((uint)patch.RawTarget >= (uint)Prototype.Code.Length)
                {
                    throw new InvalidDataException(
                        $"Lua 5.3 jump target {patch.RawTarget} is outside the prototype.");
                }

                _instructions[patch.InstructionIndex] = _instructions[patch.InstructionIndex] with
                {
                    B = _programCounterMap[patch.RawTarget],
                };
            }
        }

        private ImmutableArray<LuaIrUpvalue> ConvertUpvalues()
        {
            var result = ImmutableArray.CreateBuilder<LuaIrUpvalue>(Prototype.Upvalues.Length);
            for (var index = 0; index < Prototype.Upvalues.Length; index++)
            {
                var descriptor = Prototype.Upvalues[index];
                var debugName = Prototype.UpvalueNames.IsEmpty
                    ? []
                    : ImmutableArray.Create(Prototype.UpvalueNames[index] is { } value
                        ? value.ToArray()
                        : []);
                var name = debugName.IsEmpty
                    ? $"(upvalue {index})"
                    : Encoding.UTF8.GetString(debugName.AsSpan());
                result.Add(new LuaIrUpvalue(
                    name,
                    index,
                    _entry.ParentId < 0
                        ? LuaIrUpvalueSourceKind.Environment
                        : descriptor.InStack != 0
                            ? LuaIrUpvalueSourceKind.Register
                            : LuaIrUpvalueSourceKind.Upvalue,
                    descriptor.Index)
                {
                    DebugName = debugName,
                });
            }

            return result.MoveToImmutable();
        }

        private ImmutableArray<LuaIrLocalVariable> ConvertLocalVariables()
        {
            var result = ImmutableArray.CreateBuilder<LuaIrLocalVariable>(
                Prototype.LocalVariables.Length);
            foreach (var local in Prototype.LocalVariables)
            {
                if ((uint)local.StartProgramCounter >= (uint)_programCounterMap.Length ||
                    (uint)local.EndProgramCounter >= (uint)_programCounterMap.Length)
                {
                    throw new InvalidDataException("Lua 5.3 local variable range is invalid.");
                }

                result.Add(new LuaIrLocalVariable(
                    local.Name is { } name ? [.. name.AsSpan()] : [],
                    _programCounterMap[local.StartProgramCounter],
                    _programCounterMap[local.EndProgramCounter]));
            }

            return result.MoveToImmutable();
        }

        private static LuaIrConstant ConvertConstant(Lua53Constant constant) => constant.Kind switch
        {
            Lua53ConstantKind.Nil => LuaIrConstant.Nil,
            Lua53ConstantKind.False => LuaIrConstant.FromBoolean(false),
            Lua53ConstantKind.True => LuaIrConstant.FromBoolean(true),
            Lua53ConstantKind.Integer => LuaIrConstant.FromInteger(constant.IntegerValue),
            Lua53ConstantKind.Float => LuaIrConstant.FromFloat(constant.FloatValue),
            Lua53ConstantKind.ShortString or Lua53ConstantKind.LongString =>
                LuaIrConstant.FromString(constant.StringValue is { } value
                    ? value.AsSpan()
                    : throw new InvalidDataException("String constant has no value.")),
            _ => throw new InvalidDataException($"Unknown Lua 5.3 constant kind {constant.Kind}."),
        };

        private readonly record struct JumpPatch(int InstructionIndex, int RawTarget);
    }
}
