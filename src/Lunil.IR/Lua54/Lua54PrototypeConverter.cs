using System.Collections.Immutable;
using System.Text;
using Lunil.Core;
using Lunil.Core.Text;
using Lunil.IR.Canonical;

namespace Lunil.IR.Lua54;

/// <summary>Converts verified PUC Lua 5.4 prototypes into executable canonical IR.</summary>
public static class Lua54PrototypeConverter
{
    private const int ScratchRegisterCount = 3;

    public static LuaIrModule Convert(ReadOnlySpan<byte> binaryChunk, Lua54ChunkReaderOptions? options = null) =>
        Convert(Lua54ChunkReader.Read(binaryChunk, options));

    public static LuaIrModule Convert(Lua54Chunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        Lua54ChunkVerifier.ThrowIfInvalid(chunk);

        var prototypes = new List<PrototypeEntry>();
        var ids = new Dictionary<Lua54Prototype, int>(ReferenceEqualityComparer.Instance);
        AddPrototype(chunk.MainPrototype, parentId: -1, prototypes, ids);

        var functions = ImmutableArray.CreateBuilder<LuaIrFunction>(prototypes.Count);
        foreach (var entry in prototypes)
        {
            functions.Add(new FunctionConverter(entry, ids).Convert());
        }

        var module = new LuaIrModule
        {
            LanguageVersion = chunk.SourceFormat == LuaChunkFormat.Lua55
                ? LuaLanguageVersion.Lua55
                : LuaLanguageVersion.Lua54,
            MainFunctionId = 0,
            Functions = functions.MoveToImmutable(),
        };
        var errors = LuaIrVerifier.Verify(module);
        if (!errors.IsEmpty)
        {
            var first = errors[0];
            throw new InvalidDataException(
                $"Converted canonical IR is invalid in function {first.FunctionId} " +
                $"at instruction {first.ProgramCounter}: {first.Message}");
        }

        return module;
    }

    private static void AddPrototype(
        Lua54Prototype prototype,
        int parentId,
        List<PrototypeEntry> entries,
        Dictionary<Lua54Prototype, int> ids)
    {
        var id = entries.Count;
        ids.Add(prototype, id);
        entries.Add(new PrototypeEntry(prototype, id, parentId));
        foreach (var nested in prototype.NestedPrototypes)
        {
            AddPrototype(nested, id, entries, ids);
        }
    }

    private sealed record PrototypeEntry(Lua54Prototype Prototype, int Id, int ParentId);

    private sealed class FunctionConverter
    {
        private readonly PrototypeEntry _entry;
        private readonly Dictionary<Lua54Prototype, int> _ids;
        private readonly ImmutableArray<LuaIrConstant>.Builder _constants;
        private readonly List<LuaIrInstruction> _instructions = [];
        private readonly List<JumpPatch> _patches = [];
        private readonly int[] _programCounterMap;
        private readonly int[] _sourceLines;
        private int _sourceProgramCounter;
        private bool _usesScratchRegisters;
        private bool _scratchLifetimeClosed;

        public FunctionConverter(
            PrototypeEntry entry,
            Dictionary<Lua54Prototype, int> ids)
        {
            _entry = entry;
            _ids = ids;
            _constants = ImmutableArray.CreateBuilder<LuaIrConstant>(entry.Prototype.Constants.Length);
            foreach (var constant in entry.Prototype.Constants)
            {
                _constants.Add(ConvertConstant(constant));
            }

            _programCounterMap = new int[entry.Prototype.Code.Length + 1];
            _sourceLines = DecodeSourceLines(entry.Prototype);
        }

        private Lua54Prototype Prototype => _entry.Prototype;

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
                _usesScratchRegisters = false;
                _scratchLifetimeClosed = false;
                ConvertInstruction(Prototype.Code[_sourceProgramCounter]);
                if (_usesScratchRegisters && !_scratchLifetimeClosed)
                {
                    Emit(LuaIrOpcode.SetTop, Prototype.MaximumStackSize);
                }
            }

            _programCounterMap[Prototype.Code.Length] = _instructions.Count;
            PatchJumps();
            var instructions = _instructions.ToImmutableArray();

            return new LuaIrFunction
            {
                Id = _entry.Id,
                ParentFunctionId = _entry.ParentId,
                Span = default,
                SourceName = Prototype.Source is null
                    ? []
                    : [.. Prototype.Source.AsSpan()],
                LineDefined = Prototype.LineDefined,
                LastLineDefined = Prototype.LastLineDefined,
                ParameterCount = Prototype.ParameterCount,
                IsVarArg = Prototype.VarArgFlags != 0,
                RegisterCount = checked(Prototype.MaximumStackSize + ScratchRegisterCount),
                Constants = _constants.ToImmutable(),
                Upvalues = ConvertUpvalues(),
                Instructions = instructions,
                LocalVariables = ConvertLocalVariables(),
                BasicBlocks = LuaIrControlFlow.Build(instructions),
            };
        }

        private void ConvertInstruction(Lua54Instruction instruction)
        {
            switch (instruction.Opcode)
            {
                case Lua54Opcode.Move:
                    Emit(LuaIrOpcode.Move, instruction.A, instruction.B);
                    break;
                case Lua54Opcode.LoadInteger:
                    EmitLoadConstant(instruction.A, LuaIrConstant.FromInteger(instruction.SignedBx));
                    break;
                case Lua54Opcode.LoadFloat:
                    EmitLoadConstant(instruction.A, LuaIrConstant.FromFloat(instruction.SignedBx));
                    break;
                case Lua54Opcode.LoadConstant:
                    Emit(LuaIrOpcode.LoadConstant, instruction.A, instruction.Bx);
                    break;
                case Lua54Opcode.LoadConstantExtra:
                    Emit(
                        LuaIrOpcode.LoadConstant,
                        instruction.A,
                        Prototype.Code[_sourceProgramCounter + 1].Ax);
                    break;
                case Lua54Opcode.LoadFalse:
                    EmitLoadConstant(instruction.A, LuaIrConstant.FromBoolean(false));
                    break;
                case Lua54Opcode.LoadFalseAndSkip:
                    EmitLoadConstant(instruction.A, LuaIrConstant.FromBoolean(false));
                    EmitJump(_sourceProgramCounter + 2);
                    break;
                case Lua54Opcode.LoadTrue:
                    EmitLoadConstant(instruction.A, LuaIrConstant.FromBoolean(true));
                    break;
                case Lua54Opcode.LoadNil:
                    Emit(LuaIrOpcode.LoadNil, instruction.A, instruction.B + 1);
                    break;
                case Lua54Opcode.GetUpvalue:
                    Emit(LuaIrOpcode.GetUpvalue, instruction.A, instruction.B);
                    break;
                case Lua54Opcode.SetUpvalue:
                    Emit(LuaIrOpcode.SetUpvalue, instruction.B, instruction.A);
                    break;
                case Lua54Opcode.GetTableUpvalue:
                    Emit(LuaIrOpcode.GetUpvalue, Scratch0, instruction.B);
                    Emit(LuaIrOpcode.LoadConstant, Scratch1, instruction.C);
                    Emit(LuaIrOpcode.GetTable, instruction.A, Scratch0, Scratch1);
                    break;
                case Lua54Opcode.GetTable:
                    Emit(LuaIrOpcode.GetTable, instruction.A, instruction.B, instruction.C);
                    break;
                case Lua54Opcode.GetInteger:
                    EmitLoadConstant(Scratch0, LuaIrConstant.FromInteger(instruction.C));
                    Emit(LuaIrOpcode.GetTable, instruction.A, instruction.B, Scratch0);
                    break;
                case Lua54Opcode.GetField:
                    Emit(LuaIrOpcode.LoadConstant, Scratch0, instruction.C);
                    Emit(LuaIrOpcode.GetTable, instruction.A, instruction.B, Scratch0);
                    break;
                case Lua54Opcode.SetTableUpvalue:
                    Emit(LuaIrOpcode.GetUpvalue, Scratch0, instruction.A);
                    Emit(LuaIrOpcode.LoadConstant, Scratch1, instruction.B);
                    EmitRk(Scratch2, instruction.C, instruction.K);
                    Emit(LuaIrOpcode.SetTable, Scratch0, Scratch1, Scratch2);
                    break;
                case Lua54Opcode.SetTable:
                    EmitRk(Scratch0, instruction.C, instruction.K);
                    Emit(LuaIrOpcode.SetTable, instruction.A, instruction.B, Scratch0);
                    break;
                case Lua54Opcode.SetInteger:
                    EmitLoadConstant(Scratch0, LuaIrConstant.FromInteger(instruction.B));
                    EmitRk(Scratch1, instruction.C, instruction.K);
                    Emit(LuaIrOpcode.SetTable, instruction.A, Scratch0, Scratch1);
                    break;
                case Lua54Opcode.SetField:
                    Emit(LuaIrOpcode.LoadConstant, Scratch0, instruction.B);
                    EmitRk(Scratch1, instruction.C, instruction.K);
                    Emit(LuaIrOpcode.SetTable, instruction.A, Scratch0, Scratch1);
                    break;
                case Lua54Opcode.NewTable:
                    Emit(LuaIrOpcode.NewTable, instruction.A, instruction.B, GetExtendedC(instruction));
                    break;
                case Lua54Opcode.Self:
                    Emit(LuaIrOpcode.Move, instruction.A + 1, instruction.B);
                    EmitRk(Scratch0, instruction.C, instruction.K);
                    Emit(LuaIrOpcode.GetTable, instruction.A, instruction.B, Scratch0);
                    break;
                case Lua54Opcode.AddImmediate:
                    EmitImmediateBinary(instruction);
                    break;
                case Lua54Opcode.AddConstant:
                    EmitConstantBinary(instruction, LuaIrBinaryOperator.Add);
                    break;
                case Lua54Opcode.SubtractConstant:
                    EmitConstantBinary(instruction, LuaIrBinaryOperator.Subtract);
                    break;
                case Lua54Opcode.MultiplyConstant:
                    EmitConstantBinary(instruction, LuaIrBinaryOperator.Multiply);
                    break;
                case Lua54Opcode.ModuloConstant:
                    EmitConstantBinary(instruction, LuaIrBinaryOperator.Modulo);
                    break;
                case Lua54Opcode.PowerConstant:
                    EmitConstantBinary(instruction, LuaIrBinaryOperator.Power);
                    break;
                case Lua54Opcode.DivideConstant:
                    EmitConstantBinary(instruction, LuaIrBinaryOperator.Divide);
                    break;
                case Lua54Opcode.FloorDivideConstant:
                    EmitConstantBinary(instruction, LuaIrBinaryOperator.FloorDivide);
                    break;
                case Lua54Opcode.BitwiseAndConstant:
                    EmitConstantBinary(instruction, LuaIrBinaryOperator.BitwiseAnd);
                    break;
                case Lua54Opcode.BitwiseOrConstant:
                    EmitConstantBinary(instruction, LuaIrBinaryOperator.BitwiseOr);
                    break;
                case Lua54Opcode.BitwiseXorConstant:
                    EmitConstantBinary(instruction, LuaIrBinaryOperator.BitwiseXor);
                    break;
                case Lua54Opcode.ShiftRightImmediate:
                    EmitImmediateBinary(instruction);
                    break;
                case Lua54Opcode.ShiftLeftImmediate:
                    EmitImmediateBinary(instruction);
                    break;
                case Lua54Opcode.Add:
                    EmitRegisterBinary(instruction, LuaIrBinaryOperator.Add);
                    break;
                case Lua54Opcode.Subtract:
                    EmitRegisterBinary(instruction, LuaIrBinaryOperator.Subtract);
                    break;
                case Lua54Opcode.Multiply:
                    EmitRegisterBinary(instruction, LuaIrBinaryOperator.Multiply);
                    break;
                case Lua54Opcode.Modulo:
                    EmitRegisterBinary(instruction, LuaIrBinaryOperator.Modulo);
                    break;
                case Lua54Opcode.Power:
                    EmitRegisterBinary(instruction, LuaIrBinaryOperator.Power);
                    break;
                case Lua54Opcode.Divide:
                    EmitRegisterBinary(instruction, LuaIrBinaryOperator.Divide);
                    break;
                case Lua54Opcode.FloorDivide:
                    EmitRegisterBinary(instruction, LuaIrBinaryOperator.FloorDivide);
                    break;
                case Lua54Opcode.BitwiseAnd:
                    EmitRegisterBinary(instruction, LuaIrBinaryOperator.BitwiseAnd);
                    break;
                case Lua54Opcode.BitwiseOr:
                    EmitRegisterBinary(instruction, LuaIrBinaryOperator.BitwiseOr);
                    break;
                case Lua54Opcode.BitwiseXor:
                    EmitRegisterBinary(instruction, LuaIrBinaryOperator.BitwiseXor);
                    break;
                case Lua54Opcode.ShiftLeft:
                    EmitRegisterBinary(instruction, LuaIrBinaryOperator.ShiftLeft);
                    break;
                case Lua54Opcode.ShiftRight:
                    EmitRegisterBinary(instruction, LuaIrBinaryOperator.ShiftRight);
                    break;
                case Lua54Opcode.MetamethodBinary:
                case Lua54Opcode.MetamethodBinaryImmediate:
                case Lua54Opcode.MetamethodBinaryConstant:
                    // Canonical Binary performs the primitive operation and metamethod fallback atomically.
                    break;
                case Lua54Opcode.UnaryMinus:
                    EmitUnary(instruction, LuaIrUnaryOperator.Negate);
                    break;
                case Lua54Opcode.BitwiseNot:
                    EmitUnary(instruction, LuaIrUnaryOperator.BitwiseNot);
                    break;
                case Lua54Opcode.LogicalNot:
                    EmitUnary(instruction, LuaIrUnaryOperator.LogicalNot);
                    break;
                case Lua54Opcode.Length:
                    EmitUnary(instruction, LuaIrUnaryOperator.Length);
                    break;
                case Lua54Opcode.Concatenate:
                    EmitConcatenate(instruction);
                    break;
                case Lua54Opcode.Close:
                    Emit(LuaIrOpcode.Close, instruction.A);
                    break;
                case Lua54Opcode.ToBeClosed:
                    Emit(LuaIrOpcode.MarkToBeClosed, instruction.A);
                    break;
                case Lua54Opcode.Jump:
                    if (!IsCompanionJump(_sourceProgramCounter))
                    {
                        EmitJump(checked(_sourceProgramCounter + 1 + instruction.SignedJump));
                    }

                    break;
                case Lua54Opcode.Equal:
                    EmitComparison(instruction.A, instruction.B, LuaIrBinaryOperator.Equal, instruction.K);
                    break;
                case Lua54Opcode.LessThan:
                    EmitComparison(instruction.A, instruction.B, LuaIrBinaryOperator.LessThan, instruction.K);
                    break;
                case Lua54Opcode.LessOrEqual:
                    EmitComparison(
                        instruction.A,
                        instruction.B,
                        LuaIrBinaryOperator.LessThanOrEqual,
                        instruction.K);
                    break;
                case Lua54Opcode.EqualConstant:
                    Emit(LuaIrOpcode.LoadConstant, Scratch0, instruction.B);
                    EmitComparison(instruction.A, Scratch0, LuaIrBinaryOperator.Equal, instruction.K);
                    break;
                case Lua54Opcode.EqualImmediate:
                    EmitImmediateComparison(instruction, LuaIrBinaryOperator.Equal);
                    break;
                case Lua54Opcode.LessThanImmediate:
                    EmitImmediateComparison(instruction, LuaIrBinaryOperator.LessThan);
                    break;
                case Lua54Opcode.LessOrEqualImmediate:
                    EmitImmediateComparison(instruction, LuaIrBinaryOperator.LessThanOrEqual);
                    break;
                case Lua54Opcode.GreaterThanImmediate:
                    EmitImmediateComparison(instruction, LuaIrBinaryOperator.GreaterThan);
                    break;
                case Lua54Opcode.GreaterOrEqualImmediate:
                    EmitImmediateComparison(instruction, LuaIrBinaryOperator.GreaterThanOrEqual);
                    break;
                case Lua54Opcode.Test:
                    EmitConditionalJump(instruction.K, instruction.A, GetCompanionJumpTarget());
                    break;
                case Lua54Opcode.TestSet:
                    EmitConditionalJump(!instruction.K, instruction.B, _sourceProgramCounter + 2);
                    Emit(LuaIrOpcode.Move, instruction.A, instruction.B);
                    EmitJump(GetCompanionJumpTarget());
                    break;
                case Lua54Opcode.Call:
                    Emit(
                        LuaIrOpcode.Call,
                        instruction.A,
                        instruction.B == 0 ? -1 : instruction.B - 1,
                        instruction.C == 0 ? -1 : instruction.C - 1);
                    break;
                case Lua54Opcode.TailCall:
                    Emit(
                        LuaIrOpcode.TailCall,
                        instruction.A,
                        instruction.B == 0 ? -1 : instruction.B - 1);
                    break;
                case Lua54Opcode.Return:
                    Emit(
                        LuaIrOpcode.Return,
                        instruction.A,
                        instruction.B == 0 ? -1 : instruction.B - 1);
                    break;
                case Lua54Opcode.ReturnZero:
                    Emit(LuaIrOpcode.Return, 0, 0);
                    break;
                case Lua54Opcode.ReturnOne:
                    Emit(LuaIrOpcode.Return, instruction.A, 1);
                    break;
                case Lua54Opcode.NumericForLoop:
                    EmitTargeted(
                        LuaIrOpcode.NumericForLoop,
                        instruction.A,
                        checked(_sourceProgramCounter + 1 - instruction.Bx));
                    break;
                case Lua54Opcode.NumericForPrepare:
                    EmitTargeted(
                        LuaIrOpcode.NumericForPrepare,
                        instruction.A,
                        checked(_sourceProgramCounter + 2 + instruction.Bx));
                    break;
                case Lua54Opcode.GenericForPrepare:
                    Emit(LuaIrOpcode.MarkToBeClosed, instruction.A + 3);
                    EmitJump(checked(_sourceProgramCounter + 1 + instruction.Bx));
                    break;
                case Lua54Opcode.GenericForCall:
                    Emit(LuaIrOpcode.Move, instruction.A + 4, instruction.A);
                    Emit(LuaIrOpcode.Move, instruction.A + 5, instruction.A + 1);
                    Emit(LuaIrOpcode.Move, instruction.A + 6, instruction.A + 2);
                    Emit(
                        LuaIrOpcode.Call,
                        instruction.A + 4,
                        2,
                        instruction.C,
                        (int)LuaIrCallKind.ForIterator);
                    break;
                case Lua54Opcode.GenericForLoop:
                    EmitLoadConstant(Scratch0, LuaIrConstant.Nil);
                    Emit(
                        LuaIrOpcode.Binary,
                        Scratch1,
                        instruction.A + 4,
                        Scratch0,
                        (int)LuaIrBinaryOperator.NotEqual);
                    EmitConditionalJump(false, Scratch1, _sourceProgramCounter + 1);
                    Emit(LuaIrOpcode.Move, instruction.A + 2, instruction.A + 4);
                    EmitJump(checked(_sourceProgramCounter + 1 - instruction.Bx));
                    break;
                case Lua54Opcode.SetList:
                    Emit(
                        LuaIrOpcode.SetList,
                        instruction.A,
                        checked(GetExtendedC(instruction) * 50 + 1),
                        instruction.A + 1,
                        instruction.B == 0 ? -1 : instruction.B);
                    break;
                case Lua54Opcode.Closure:
                    Emit(
                        LuaIrOpcode.Closure,
                        instruction.A,
                        _ids[Prototype.NestedPrototypes[instruction.Bx]]);
                    break;
                case Lua54Opcode.VarArg:
                    Emit(
                        LuaIrOpcode.VarArg,
                        instruction.A,
                        instruction.C == 0 ? -1 : instruction.C - 1,
                        instruction.K ? instruction.B + 1 : 0);
                    break;
                case Lua54Opcode.VarArgPrepare:
                    if (Prototype.VarArgFlags == 2)
                    {
                        Emit(LuaIrOpcode.CreateVarArgTable, Prototype.ParameterCount);
                    }

                    break;
                case Lua54Opcode.Lua55GetVarArg:
                    Emit(LuaIrOpcode.GetVarArg, instruction.A, instruction.C);
                    break;
                case Lua54Opcode.Lua55ErrorIfNotNil:
                    Emit(
                        LuaIrOpcode.ErrorIfNotNil,
                        instruction.A,
                        instruction.Bx == 0 ? -1 : instruction.Bx - 1);
                    break;
                case Lua54Opcode.ExtraArgument:
                    // The canonical frame ABI already separates fixed arguments and varargs.
                    break;
                default:
                    throw new InvalidDataException($"Unsupported Lua 5.4 opcode {instruction.Opcode}.");
            }
        }

        private void EmitImmediateBinary(Lua54Instruction instruction)
        {
            var metamethod = Prototype.Code[_sourceProgramCounter + 1];
            var op = metamethod.C switch
            {
                6 => LuaIrBinaryOperator.Add,
                7 => LuaIrBinaryOperator.Subtract,
                16 => LuaIrBinaryOperator.ShiftLeft,
                17 => LuaIrBinaryOperator.ShiftRight,
                _ => throw new InvalidDataException(
                    $"Invalid immediate arithmetic metamethod event {metamethod.C}."),
            };
            EmitLoadConstant(Scratch0, LuaIrConstant.FromInteger(metamethod.SignedB));
            Emit(
                LuaIrOpcode.Binary,
                instruction.A,
                metamethod.K ? Scratch0 : instruction.B,
                metamethod.K ? instruction.B : Scratch0,
                (int)op);
        }

        private void EmitConstantBinary(Lua54Instruction instruction, LuaIrBinaryOperator op)
        {
            var metamethod = Prototype.Code[_sourceProgramCounter + 1];
            Emit(LuaIrOpcode.LoadConstant, Scratch0, instruction.C);
            Emit(
                LuaIrOpcode.Binary,
                instruction.A,
                metamethod.K ? Scratch0 : instruction.B,
                metamethod.K ? instruction.B : Scratch0,
                (int)op);
        }

        private void EmitRegisterBinary(Lua54Instruction instruction, LuaIrBinaryOperator op) =>
            Emit(LuaIrOpcode.Binary, instruction.A, instruction.B, instruction.C, (int)op);

        private void EmitUnary(Lua54Instruction instruction, LuaIrUnaryOperator op) =>
            Emit(LuaIrOpcode.Unary, instruction.A, instruction.B, (int)op);

        private void EmitConcatenate(Lua54Instruction instruction)
        {
            var last = checked(instruction.A + instruction.B - 1);
            for (var register = last - 1; register >= instruction.A; register--)
            {
                Emit(
                    LuaIrOpcode.Binary,
                    register,
                    register,
                    register + 1,
                    (int)LuaIrBinaryOperator.Concatenate);
            }
        }

        private void EmitImmediateComparison(
            Lua54Instruction instruction,
            LuaIrBinaryOperator op)
        {
            var constant = instruction.C == 0
                ? LuaIrConstant.FromInteger(instruction.SignedB)
                : LuaIrConstant.FromFloat(instruction.SignedB);
            EmitLoadConstant(Scratch0, constant);
            EmitComparison(instruction.A, Scratch0, op, instruction.K);
        }

        private void EmitComparison(int left, int right, LuaIrBinaryOperator op, bool accepted)
        {
            Emit(LuaIrOpcode.Binary, Scratch1, left, right, (int)op);
            EmitConditionalJump(accepted, Scratch1, GetCompanionJumpTarget());
        }

        private void EmitConditionalJump(bool whenTrue, int register, int rawTarget)
        {
            var opcode = whenTrue ? LuaIrOpcode.JumpIfTrue : LuaIrOpcode.JumpIfFalse;
            var index = _instructions.Count;
            Emit(
                opcode,
                register,
                c: register >= Scratch0 ? Prototype.MaximumStackSize : 0,
                d: register >= Scratch0 ? 1 : 0);
            _scratchLifetimeClosed |= register >= Scratch0;
            _patches.Add(new JumpPatch(index, rawTarget));
        }

        private void EmitJump(int rawTarget)
        {
            var index = _instructions.Count;
            Emit(LuaIrOpcode.Jump, c: -1);
            _patches.Add(new JumpPatch(index, rawTarget));
        }

        private void EmitTargeted(LuaIrOpcode opcode, int register, int rawTarget)
        {
            var index = _instructions.Count;
            Emit(opcode, register);
            _patches.Add(new JumpPatch(index, rawTarget));
        }

        private int GetCompanionJumpTarget()
        {
            var jumpProgramCounter = _sourceProgramCounter + 1;
            var jump = Prototype.Code[jumpProgramCounter];
            return checked(jumpProgramCounter + 1 + jump.SignedJump);
        }

        private bool IsCompanionJump(int programCounter) =>
            programCounter > 0 &&
            Lua54OpcodeInfo.Get(Prototype.Code[programCounter - 1].Opcode).IsTest;

        private int GetExtendedC(Lua54Instruction instruction) => instruction.K
            ? checked(instruction.C + Prototype.Code[_sourceProgramCounter + 1].Ax * 256)
            : instruction.C;

        private void EmitRk(int destination, int operand, bool isConstant)
        {
            if (isConstant)
            {
                Emit(LuaIrOpcode.LoadConstant, destination, operand);
            }
            else
            {
                Emit(LuaIrOpcode.Move, destination, operand);
            }
        }

        private void EmitLoadConstant(int register, LuaIrConstant constant)
        {
            var index = _constants.Count;
            _constants.Add(constant);
            Emit(LuaIrOpcode.LoadConstant, register, index);
        }

        private void Emit(
            LuaIrOpcode opcode,
            int a = 0,
            int b = 0,
            int c = 0,
            int d = 0)
        {
            var instruction = new LuaIrInstruction(
                opcode,
                a,
                b,
                c,
                d,
                sourceLine: _sourceLines[_sourceProgramCounter],
                logicalProgramCounter: _sourceProgramCounter);
            _usesScratchRegisters |= TouchesScratchRegister(instruction);
            _instructions.Add(instruction);
        }

        private bool TouchesScratchRegister(LuaIrInstruction instruction) =>
            instruction.Opcode switch
            {
                LuaIrOpcode.LoadConstant or LuaIrOpcode.NewTable or LuaIrOpcode.Closure =>
                    instruction.A >= Scratch0,
                LuaIrOpcode.LoadNil => instruction.A + instruction.B > Scratch0,
                LuaIrOpcode.Move => instruction.A >= Scratch0 || instruction.B >= Scratch0,
                LuaIrOpcode.GetUpvalue => instruction.A >= Scratch0,
                LuaIrOpcode.SetUpvalue => instruction.B >= Scratch0,
                LuaIrOpcode.GetTable or LuaIrOpcode.SetTable =>
                    instruction.A >= Scratch0 || instruction.B >= Scratch0 ||
                    instruction.C >= Scratch0,
                LuaIrOpcode.SetList => instruction.A >= Scratch0 || instruction.C >= Scratch0,
                LuaIrOpcode.VarArg or LuaIrOpcode.Unary =>
                    instruction.A >= Scratch0 || instruction.B >= Scratch0,
                LuaIrOpcode.Binary => instruction.A >= Scratch0 || instruction.B >= Scratch0 ||
                    instruction.C >= Scratch0,
                LuaIrOpcode.JumpIfFalse or LuaIrOpcode.JumpIfTrue => instruction.A >= Scratch0,
                LuaIrOpcode.Call or LuaIrOpcode.TailCall or LuaIrOpcode.Return =>
                    instruction.A >= Scratch0,
                LuaIrOpcode.Close or LuaIrOpcode.MarkToBeClosed or
                    LuaIrOpcode.NumericForPrepare or LuaIrOpcode.NumericForLoop =>
                    instruction.A >= Scratch0,
                _ => false,
            };

        private void PatchJumps()
        {
            foreach (var patch in _patches)
            {
                if ((uint)patch.RawTarget >= (uint)Prototype.Code.Length)
                {
                    throw new InvalidDataException(
                        $"Lua 5.4 jump target {patch.RawTarget} is outside the prototype.");
                }

                var target = _programCounterMap[patch.RawTarget];
                if ((uint)target >= (uint)_instructions.Count)
                {
                    throw new InvalidDataException(
                        $"Lua 5.4 jump target {patch.RawTarget} has no canonical instruction.");
                }

                _instructions[patch.InstructionIndex] =
                    _instructions[patch.InstructionIndex] with { B = target };
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
                    : ImmutableArray.Create(Prototype.UpvalueNames[index]?.ToArray() ?? []);
                var name = debugName.IsEmpty
                    ? $"(upvalue {index})"
                    : Encoding.UTF8.GetString(debugName.AsSpan());
                var sourceKind = _entry.ParentId < 0
                    ? LuaIrUpvalueSourceKind.Environment
                    : descriptor.InStack == 1
                        ? LuaIrUpvalueSourceKind.Register
                        : LuaIrUpvalueSourceKind.Upvalue;
                result.Add(new LuaIrUpvalue(name, index, sourceKind, descriptor.Index)
                {
                    Kind = descriptor.Kind,
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
                result.Add(new LuaIrLocalVariable(
                    local.Name is null ? [] : [.. local.Name.AsSpan()],
                    _programCounterMap[local.StartProgramCounter],
                    _programCounterMap[local.EndProgramCounter]));
            }

            return result.MoveToImmutable();
        }

        private static LuaIrConstant ConvertConstant(Lua54Constant constant) => constant.Kind switch
        {
            Lua54ConstantKind.Nil => LuaIrConstant.Nil,
            Lua54ConstantKind.False => LuaIrConstant.FromBoolean(false),
            Lua54ConstantKind.True => LuaIrConstant.FromBoolean(true),
            Lua54ConstantKind.Integer => LuaIrConstant.FromInteger(constant.IntegerValue),
            Lua54ConstantKind.Float => LuaIrConstant.FromFloat(constant.FloatValue),
            Lua54ConstantKind.ShortString or Lua54ConstantKind.LongString =>
                LuaIrConstant.FromString(constant.StringValue!.AsSpan()),
            _ => throw new InvalidDataException($"Unknown Lua 5.4 constant kind {constant.Kind}."),
        };

        private static int[] DecodeSourceLines(Lua54Prototype prototype)
        {
            var result = new int[prototype.Code.Length];
            if (prototype.LineInfo.IsEmpty)
            {
                return result;
            }

            var line = prototype.LineDefined;
            var absoluteIndex = 0;
            for (var pc = 0; pc < prototype.Code.Length; pc++)
            {
                if (prototype.LineInfo[pc] == sbyte.MinValue)
                {
                    var absolute = prototype.AbsoluteLineInfo[absoluteIndex++];
                    line = absolute.Line;
                }
                else
                {
                    line = checked(line + prototype.LineInfo[pc]);
                }

                result[pc] = line;
            }

            return result;
        }

        private readonly record struct JumpPatch(int InstructionIndex, int RawTarget);
    }
}
