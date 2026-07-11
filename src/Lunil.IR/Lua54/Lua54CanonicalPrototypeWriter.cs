using System.Collections.Immutable;
using System.Text;
using Lunil.IR.Canonical;

namespace Lunil.IR.Lua54;

/// <summary>Lowers executable canonical IR back into a portable PUC Lua 5.4 chunk.</summary>
public static class Lua54CanonicalPrototypeWriter
{
    private const int FieldsPerFlush = 50;
    private const int TemporaryRegisterCount = 2;

    public static Lua54Chunk CreateChunk(
        LuaIrModule module,
        int functionId,
        Lua54ChunkTarget? target = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        var errors = LuaIrVerifier.Verify(module);
        if (!errors.IsEmpty)
        {
            var first = errors[0];
            throw new InvalidDataException(
                $"Invalid canonical IR in function {first.FunctionId} at instruction " +
                $"{first.ProgramCounter}: {first.Message}");
        }

        if ((uint)functionId >= (uint)module.Functions.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(functionId));
        }

        var prototype = new ModuleConverter(module).Convert(functionId);
        return new Lua54Chunk(
            target ?? Lua54ChunkTarget.Host,
            checked((byte)prototype.Upvalues.Length),
            prototype);
    }

    public static byte[] Write(
        LuaIrModule module,
        int functionId,
        bool stripDebugInformation = false,
        Lua54ChunkTarget? target = null) =>
        Lua54ChunkWriter.Write(
            CreateChunk(module, functionId, target),
            stripDebugInformation);

    private sealed class ModuleConverter
    {
        private readonly LuaIrModule _module;
        private readonly Dictionary<int, ImmutableArray<int>> _children;

        public ModuleConverter(LuaIrModule module)
        {
            _module = module;
            _children = module.Functions
                .Where(static function => function.ParentFunctionId >= 0)
                .GroupBy(static function => function.ParentFunctionId)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.Select(static function => function.Id).ToImmutableArray());
        }

        public Lua54Prototype Convert(int functionId) =>
            new FunctionConverter(this, _module.Functions[functionId]).Convert();

        public ImmutableArray<int> GetChildren(int functionId) =>
            _children.TryGetValue(functionId, out var children) ? children : [];
    }

    private sealed class FunctionConverter
    {
        private readonly ModuleConverter _owner;
        private readonly LuaIrFunction _function;
        private readonly ImmutableArray<int> _children;
        private readonly Dictionary<int, int> _childIndexes;
        private readonly List<EmittedInstruction> _code = [];
        private readonly List<JumpPatch> _patches = [];
        private readonly Dictionary<int, int> _openSetListBases = [];
        private readonly int[] _programCounterMap;
        private readonly int _temporary0;
        private readonly int _temporary1;

        public FunctionConverter(ModuleConverter owner, LuaIrFunction function)
        {
            _owner = owner;
            _function = function;
            _children = owner.GetChildren(function.Id);
            _childIndexes = _children
                .Select(static (id, index) => (id, index))
                .ToDictionary(static pair => pair.id, static pair => pair.index);
            _programCounterMap = new int[function.Instructions.Length + 1];
            _temporary0 = function.RegisterCount;
            _temporary1 = checked(function.RegisterCount + 1);
        }

        public Lua54Prototype Convert()
        {
            if (_function.ParameterCount > byte.MaxValue)
            {
                throw new InvalidOperationException("A Lua 5.4 prototype cannot have more than 255 parameters.");
            }

            var maximumStackSize = Math.Max(2, checked(_function.RegisterCount + TemporaryRegisterCount));
            if (maximumStackSize > byte.MaxValue)
            {
                throw new InvalidOperationException("A Lua 5.4 prototype cannot use more than 255 registers.");
            }

            if (_function.Upvalues.Length > byte.MaxValue)
            {
                throw new InvalidOperationException("A Lua 5.4 prototype cannot have more than 255 upvalues.");
            }

            if (_function.IsVarArg)
            {
                Emit(
                    Lua54Instruction.CreateAbc(
                        Lua54Opcode.VarArgPrepare,
                        _function.ParameterCount,
                        0,
                        0),
                    _function.LineDefined);
            }

            for (var pc = 0; pc < _function.Instructions.Length; pc++)
            {
                _programCounterMap[pc] = _code.Count;
                PrepareOpenSetList(pc);
                ConvertInstruction(pc, _function.Instructions[pc]);
            }

            _programCounterMap[^1] = _code.Count;
            PatchJumps();
            var (lineInfo, absoluteLineInfo) = EncodeLines();

            return new Lua54Prototype
            {
                Source = _function.SourceName is [0x01]
                    ? new Lua54String(ReadOnlySpan<byte>.Empty)
                    : _function.SourceName.IsEmpty
                        ? null
                        : new Lua54String(_function.SourceName.AsSpan()),
                LineDefined = _function.LineDefined,
                LastLineDefined = _function.LastLineDefined,
                ParameterCount = checked((byte)_function.ParameterCount),
                VarArgFlags = _function.IsVarArg ? (byte)1 : (byte)0,
                MaximumStackSize = checked((byte)maximumStackSize),
                Code = _code.Select(static item => item.Instruction).ToImmutableArray(),
                Constants = _function.Constants.Select(ConvertConstant).ToImmutableArray(),
                Upvalues = _function.Upvalues.Select(ConvertUpvalue).ToImmutableArray(),
                NestedPrototypes = _children.Select(_owner.Convert).ToImmutableArray(),
                LineInfo = lineInfo,
                AbsoluteLineInfo = absoluteLineInfo,
                LocalVariables = ConvertLocalVariables(),
                UpvalueNames = _function.Upvalues.Select(ConvertUpvalueName).ToImmutableArray(),
            };
        }

        private void ConvertInstruction(int programCounter, LuaIrInstruction instruction)
        {
            var line = instruction.SourceLine;
            switch (instruction.Opcode)
            {
                case LuaIrOpcode.LoadConstant:
                    EmitLoadConstant(instruction.A, instruction.B, line);
                    break;
                case LuaIrOpcode.LoadNil:
                    if (instruction.B > 0)
                    {
                        Emit(
                            Lua54Instruction.CreateAbc(
                                Lua54Opcode.LoadNil,
                                instruction.A,
                                instruction.B - 1,
                                0),
                            line);
                    }

                    break;
                case LuaIrOpcode.Move:
                    EmitAbc(Lua54Opcode.Move, instruction.A, instruction.B, 0, line);
                    break;
                case LuaIrOpcode.SetTop:
                    // PUC bytecode tracks top only across open-result instructions. Canonical
                    // SetTop ends an open interval; the next open producer establishes top again.
                    break;
                case LuaIrOpcode.GetUpvalue:
                    EmitAbc(Lua54Opcode.GetUpvalue, instruction.A, instruction.B, 0, line);
                    break;
                case LuaIrOpcode.SetUpvalue:
                    EmitAbc(Lua54Opcode.SetUpvalue, instruction.B, instruction.A, 0, line);
                    break;
                case LuaIrOpcode.NewTable:
                    EmitNewTable(instruction, line);
                    break;
                case LuaIrOpcode.GetTable:
                    EmitAbc(
                        Lua54Opcode.GetTable,
                        instruction.A,
                        instruction.B,
                        instruction.C,
                        line);
                    break;
                case LuaIrOpcode.SetTable:
                    EmitAbc(
                        Lua54Opcode.SetTable,
                        instruction.A,
                        instruction.B,
                        instruction.C,
                        line);
                    break;
                case LuaIrOpcode.SetList:
                    EmitSetList(programCounter, instruction, line);
                    break;
                case LuaIrOpcode.Closure:
                    if (!_childIndexes.TryGetValue(instruction.B, out var childIndex))
                    {
                        throw new InvalidDataException(
                            $"Function {_function.Id} does not directly contain function {instruction.B}.");
                    }

                    Emit(
                        Lua54Instruction.CreateABx(Lua54Opcode.Closure, instruction.A, childIndex),
                        line);
                    break;
                case LuaIrOpcode.VarArg:
                    EmitAbc(
                        Lua54Opcode.VarArg,
                        instruction.A,
                        0,
                        instruction.B < 0 ? 0 : instruction.B + 1,
                        line);
                    break;
                case LuaIrOpcode.Unary:
                    EmitUnary(instruction, line);
                    break;
                case LuaIrOpcode.Binary:
                    EmitBinary(instruction, line);
                    break;
                case LuaIrOpcode.Jump:
                    if (instruction.C >= 0)
                    {
                        EmitAbc(Lua54Opcode.Close, instruction.C, 0, 0, line);
                    }

                    EmitJump(instruction.B, line);
                    break;
                case LuaIrOpcode.JumpIfFalse:
                    EmitConditionalJump(instruction.A, false, instruction.B, line);
                    break;
                case LuaIrOpcode.JumpIfTrue:
                    EmitConditionalJump(instruction.A, true, instruction.B, line);
                    break;
                case LuaIrOpcode.Call:
                    EmitAbc(
                        Lua54Opcode.Call,
                        instruction.A,
                        instruction.B < 0 ? 0 : instruction.B + 1,
                        instruction.C < 0 ? 0 : instruction.C + 1,
                        line);
                    break;
                case LuaIrOpcode.TailCall:
                    EmitAbc(
                        Lua54Opcode.TailCall,
                        instruction.A,
                        instruction.B < 0 ? 0 : instruction.B + 1,
                        VarArgReturnAdjustment,
                        line);
                    break;
                case LuaIrOpcode.Return:
                    EmitAbc(
                        Lua54Opcode.Return,
                        instruction.A,
                        instruction.B < 0 ? 0 : instruction.B + 1,
                        VarArgReturnAdjustment,
                        line);
                    break;
                case LuaIrOpcode.Close:
                    EmitAbc(Lua54Opcode.Close, instruction.A, 0, 0, line);
                    break;
                case LuaIrOpcode.MarkToBeClosed:
                    EmitAbc(Lua54Opcode.ToBeClosed, instruction.A, 0, 0, line);
                    break;
                case LuaIrOpcode.NumericForPrepare:
                    EmitNumericForPrepare(instruction.A, instruction.B, line);
                    break;
                case LuaIrOpcode.NumericForLoop:
                    EmitNumericForLoop(instruction.A, instruction.B, line);
                    break;
                default:
                    throw new InvalidDataException(
                        $"Unsupported canonical opcode {instruction.Opcode}.");
            }
        }

        private int VarArgReturnAdjustment =>
            _function.IsVarArg ? checked(_function.ParameterCount + 1) : 0;

        private void EmitLoadConstant(int register, int constant, int line)
        {
            if (constant <= Lua54Instruction.MaximumBx)
            {
                Emit(Lua54Instruction.CreateABx(Lua54Opcode.LoadConstant, register, constant), line);
                return;
            }

            if (constant > Lua54Instruction.MaximumAx)
            {
                throw new InvalidOperationException("A Lua 5.4 constant index exceeds the chunk format.");
            }

            Emit(Lua54Instruction.CreateABx(Lua54Opcode.LoadConstantExtra, register, 0), line);
            Emit(Lua54Instruction.CreateAx(Lua54Opcode.ExtraArgument, constant), line);
        }

        private void EmitNewTable(LuaIrInstruction instruction, int line)
        {
            var low = instruction.C & byte.MaxValue;
            var high = instruction.C >> 8;
            if (high > Lua54Instruction.MaximumAx)
            {
                throw new InvalidOperationException("A Lua 5.4 table array hint exceeds the chunk format.");
            }

            Emit(
                Lua54Instruction.CreateAbc(
                    Lua54Opcode.NewTable,
                    instruction.A,
                    instruction.B,
                    low,
                    high != 0),
                line);
            Emit(Lua54Instruction.CreateAx(Lua54Opcode.ExtraArgument, high), line);
        }

        private void EmitSetList(int programCounter, LuaIrInstruction instruction, int line)
        {
            if (instruction.D >= 0)
            {
                for (var index = 0; index < instruction.D; index++)
                {
                    var key = checked(instruction.B + index);
                    var value = checked(instruction.C + index);
                    if (key <= byte.MaxValue)
                    {
                        EmitAbc(Lua54Opcode.SetInteger, instruction.A, key, value, line);
                    }
                    else
                    {
                        EmitInteger(_temporary0, key, line);
                        EmitAbc(
                            Lua54Opcode.SetTable,
                            instruction.A,
                            _temporary0,
                            value,
                            line);
                    }
                }

                return;
            }

            if (!_openSetListBases.TryGetValue(programCounter, out var tableRegister))
            {
                throw new InvalidDataException(
                    "An open canonical SetList must immediately follow an open Call or VarArg.");
            }

            var block = (instruction.B - 1) / FieldsPerFlush;
            EmitExtendedSetList(tableRegister, count: 0, block, line);
        }

        private void PrepareOpenSetList(int programCounter)
        {
            var instruction = _function.Instructions[programCounter];
            var isOpenProducer = instruction.Opcode == LuaIrOpcode.Call
                ? instruction.C < 0
                : instruction.Opcode == LuaIrOpcode.VarArg && instruction.B < 0;
            if (!isOpenProducer || programCounter + 1 >= _function.Instructions.Length)
            {
                return;
            }

            var producerIndex = programCounter;
            var setListIndex = programCounter + 1;
            while (setListIndex < _function.Instructions.Length &&
                   _function.Instructions[setListIndex] is
                   {
                       Opcode: LuaIrOpcode.Call,
                       B: < 0,
                       C: < 0,
                   })
            {
                producerIndex = setListIndex++;
            }

            if (setListIndex >= _function.Instructions.Length)
            {
                return;
            }

            var producer = _function.Instructions[producerIndex];
            var setList = _function.Instructions[setListIndex];
            if (setList.Opcode != LuaIrOpcode.SetList || setList.D >= 0 ||
                setList.C != producer.A || _openSetListBases.ContainsKey(setListIndex))
            {
                return;
            }

            var block = (setList.B - 1) / FieldsPerFlush;
            var offset = setList.B - block * FieldsPerFlush;
            var tableRegister = producer.A - offset;
            if (tableRegister < 0)
            {
                throw new InvalidDataException("An open SetList has no representable register window.");
            }

            var line = producer.SourceLine;
            EmitAbc(Lua54Opcode.Move, tableRegister, setList.A, 0, line);
            for (var index = 1; index < offset; index++)
            {
                var key = checked(block * FieldsPerFlush + index);
                var destination = tableRegister + index;
                if (key <= byte.MaxValue)
                {
                    EmitAbc(
                        Lua54Opcode.GetInteger,
                        destination,
                        tableRegister,
                        key,
                        line);
                }
                else
                {
                    EmitInteger(destination, key, line);
                    EmitAbc(
                        Lua54Opcode.GetTable,
                        destination,
                        tableRegister,
                        destination,
                        line);
                }
            }

            _openSetListBases.Add(setListIndex, tableRegister);
        }

        private void EmitExtendedSetList(int tableRegister, int count, int block, int line)
        {
            var low = block & byte.MaxValue;
            var high = block >> 8;
            Emit(
                Lua54Instruction.CreateAbc(
                    Lua54Opcode.SetList,
                    tableRegister,
                    count,
                    low,
                    high != 0),
                line);
            if (high != 0)
            {
                Emit(Lua54Instruction.CreateAx(Lua54Opcode.ExtraArgument, high), line);
            }
        }

        private void EmitInteger(int register, int value, int line)
        {
            var signedLimit = Lua54Instruction.SignedBxOffset;
            if (value >= -signedLimit && value <= Lua54Instruction.MaximumBx - signedLimit)
            {
                Emit(
                    Lua54Instruction.CreateASignedBx(Lua54Opcode.LoadInteger, register, value),
                    line);
                return;
            }

            throw new InvalidOperationException(
                "A generated integer table index exceeds the Lua 5.4 immediate range.");
        }

        private void EmitUnary(LuaIrInstruction instruction, int line)
        {
            var opcode = (LuaIrUnaryOperator)instruction.C switch
            {
                LuaIrUnaryOperator.Negate => Lua54Opcode.UnaryMinus,
                LuaIrUnaryOperator.BitwiseNot => Lua54Opcode.BitwiseNot,
                LuaIrUnaryOperator.LogicalNot => Lua54Opcode.LogicalNot,
                LuaIrUnaryOperator.Length => Lua54Opcode.Length,
                _ => throw new InvalidDataException("Unknown canonical unary operator."),
            };
            EmitAbc(opcode, instruction.A, instruction.B, 0, line);
        }

        private void EmitBinary(LuaIrInstruction instruction, int line)
        {
            var op = (LuaIrBinaryOperator)instruction.D;
            if (op == LuaIrBinaryOperator.Concatenate)
            {
                EmitAbc(Lua54Opcode.Move, _temporary0, instruction.B, 0, line);
                EmitAbc(Lua54Opcode.Move, _temporary1, instruction.C, 0, line);
                EmitAbc(Lua54Opcode.Concatenate, _temporary0, 2, 0, line);
                EmitAbc(Lua54Opcode.Move, instruction.A, _temporary0, 0, line);
                return;
            }

            if (IsComparison(op))
            {
                EmitComparisonValue(instruction, op, line);
                return;
            }

            var (opcode, eventCode) = op switch
            {
                LuaIrBinaryOperator.Add => (Lua54Opcode.Add, 6),
                LuaIrBinaryOperator.Subtract => (Lua54Opcode.Subtract, 7),
                LuaIrBinaryOperator.Multiply => (Lua54Opcode.Multiply, 8),
                LuaIrBinaryOperator.Modulo => (Lua54Opcode.Modulo, 9),
                LuaIrBinaryOperator.Power => (Lua54Opcode.Power, 10),
                LuaIrBinaryOperator.Divide => (Lua54Opcode.Divide, 11),
                LuaIrBinaryOperator.FloorDivide => (Lua54Opcode.FloorDivide, 12),
                LuaIrBinaryOperator.BitwiseAnd => (Lua54Opcode.BitwiseAnd, 13),
                LuaIrBinaryOperator.BitwiseOr => (Lua54Opcode.BitwiseOr, 14),
                LuaIrBinaryOperator.BitwiseXor => (Lua54Opcode.BitwiseXor, 15),
                LuaIrBinaryOperator.ShiftLeft => (Lua54Opcode.ShiftLeft, 16),
                LuaIrBinaryOperator.ShiftRight => (Lua54Opcode.ShiftRight, 17),
                _ => throw new InvalidDataException("Unknown canonical binary operator."),
            };
            EmitAbc(opcode, instruction.A, instruction.B, instruction.C, line);
            EmitAbc(
                Lua54Opcode.MetamethodBinary,
                instruction.B,
                instruction.C,
                eventCode,
                line);
        }

        private void EmitComparisonValue(
            LuaIrInstruction instruction,
            LuaIrBinaryOperator op,
            int line)
        {
            var left = instruction.B;
            var right = instruction.C;
            var accepted = true;
            var opcode = op switch
            {
                LuaIrBinaryOperator.Equal => Lua54Opcode.Equal,
                LuaIrBinaryOperator.NotEqual => Lua54Opcode.Equal,
                LuaIrBinaryOperator.LessThan => Lua54Opcode.LessThan,
                LuaIrBinaryOperator.LessThanOrEqual => Lua54Opcode.LessOrEqual,
                LuaIrBinaryOperator.GreaterThan => Lua54Opcode.LessThan,
                LuaIrBinaryOperator.GreaterThanOrEqual => Lua54Opcode.LessOrEqual,
                _ => throw new InvalidDataException("Unknown canonical comparison operator."),
            };
            if (op == LuaIrBinaryOperator.NotEqual)
            {
                accepted = false;
            }
            else if (op is LuaIrBinaryOperator.GreaterThan or LuaIrBinaryOperator.GreaterThanOrEqual)
            {
                (left, right) = (right, left);
            }

            Emit(
                Lua54Instruction.CreateAbc(opcode, left, right, 0, accepted),
                line);
            var trueJump = _code.Count;
            Emit(Lua54Instruction.CreateSignedJump(Lua54Opcode.Jump, 0), line);
            EmitAbc(Lua54Opcode.LoadFalse, instruction.A, 0, 0, line);
            var endJump = _code.Count;
            Emit(Lua54Instruction.CreateSignedJump(Lua54Opcode.Jump, 0), line);
            var trueTarget = _code.Count;
            EmitAbc(Lua54Opcode.LoadTrue, instruction.A, 0, 0, line);
            _patches.Add(JumpPatch.ToRaw(trueJump, trueTarget));
            _patches.Add(JumpPatch.ToRaw(endJump, _code.Count));
        }

        private void EmitConditionalJump(int register, bool whenTrue, int target, int line)
        {
            Emit(
                Lua54Instruction.CreateAbc(
                    Lua54Opcode.Test,
                    register,
                    0,
                    0,
                    whenTrue),
                line);
            EmitJump(target, line);
        }

        private void EmitJump(int target, int line)
        {
            var index = _code.Count;
            Emit(Lua54Instruction.CreateSignedJump(Lua54Opcode.Jump, 0), line);
            _patches.Add(JumpPatch.ToCanonical(index, target));
        }

        private void EmitNumericForPrepare(int register, int target, int line)
        {
            var index = _code.Count;
            Emit(Lua54Instruction.CreateABx(Lua54Opcode.NumericForPrepare, register, 0), line);
            _patches.Add(JumpPatch.NumericPrepare(index, target));
        }

        private void EmitNumericForLoop(int register, int target, int line)
        {
            var index = _code.Count;
            Emit(Lua54Instruction.CreateABx(Lua54Opcode.NumericForLoop, register, 0), line);
            _patches.Add(JumpPatch.NumericLoop(index, target));
        }

        private void PatchJumps()
        {
            foreach (var patch in _patches)
            {
                var target = patch.RawTarget >= 0
                    ? patch.RawTarget
                    : _programCounterMap[patch.CanonicalTarget];
                var original = _code[patch.InstructionIndex];
                var patched = patch.Kind switch
                {
                    JumpPatchKind.Jump => Lua54Instruction.CreateSignedJump(
                        Lua54Opcode.Jump,
                        checked(target - patch.InstructionIndex - 1)),
                    JumpPatchKind.NumericPrepare => Lua54Instruction.CreateABx(
                        Lua54Opcode.NumericForPrepare,
                        original.Instruction.A,
                        checked(target - patch.InstructionIndex - 2)),
                    JumpPatchKind.NumericLoop => Lua54Instruction.CreateABx(
                        Lua54Opcode.NumericForLoop,
                        original.Instruction.A,
                        checked(patch.InstructionIndex + 1 - target)),
                    _ => throw new InvalidOperationException("Unknown jump patch kind."),
                };
                _code[patch.InstructionIndex] = original with { Instruction = patched };
            }
        }

        private (ImmutableArray<sbyte>, ImmutableArray<Lua54AbsoluteLineInfo>) EncodeLines()
        {
            var lines = ImmutableArray.CreateBuilder<sbyte>(_code.Count);
            var absolute = ImmutableArray.CreateBuilder<Lua54AbsoluteLineInfo>();
            var current = _function.LineDefined;
            for (var pc = 0; pc < _code.Count; pc++)
            {
                var line = _code[pc].SourceLine;
                var delta = (long)line - current;
                if (delta is >= sbyte.MinValue + 1 and <= sbyte.MaxValue)
                {
                    lines.Add((sbyte)delta);
                }
                else
                {
                    lines.Add(sbyte.MinValue);
                    absolute.Add(new Lua54AbsoluteLineInfo(pc, line));
                }

                current = line;
            }

            return (lines.MoveToImmutable(), absolute.ToImmutable());
        }

        private ImmutableArray<Lua54LocalVariable> ConvertLocalVariables()
        {
            var result = ImmutableArray.CreateBuilder<Lua54LocalVariable>(
                _function.LocalVariables.Length);
            foreach (var local in _function.LocalVariables)
            {
                result.Add(new Lua54LocalVariable(
                    new Lua54String(local.Name.AsSpan()),
                    _programCounterMap[local.StartProgramCounter],
                    _programCounterMap[local.EndProgramCounter]));
            }

            return result.MoveToImmutable();
        }

        private void EmitAbc(
            Lua54Opcode opcode,
            int a,
            int b,
            int c,
            int line,
            bool k = false) =>
            Emit(Lua54Instruction.CreateAbc(opcode, a, b, c, k), line);

        private void Emit(Lua54Instruction instruction, int line) =>
            _code.Add(new EmittedInstruction(instruction, line));

        private static bool IsComparison(LuaIrBinaryOperator op) => op is
            LuaIrBinaryOperator.Equal or LuaIrBinaryOperator.NotEqual or
            LuaIrBinaryOperator.LessThan or LuaIrBinaryOperator.LessThanOrEqual or
            LuaIrBinaryOperator.GreaterThan or LuaIrBinaryOperator.GreaterThanOrEqual;

        private static Lua54Constant ConvertConstant(LuaIrConstant constant) => constant.Kind switch
        {
            LuaIrConstantKind.Nil => Lua54Constant.Nil,
            LuaIrConstantKind.Boolean => constant.Boolean ? Lua54Constant.True : Lua54Constant.False,
            LuaIrConstantKind.Integer => Lua54Constant.FromInteger(constant.Integer),
            LuaIrConstantKind.Float => Lua54Constant.FromFloat(constant.Float),
            LuaIrConstantKind.String => Lua54Constant.FromString(
                new Lua54String(constant.Bytes.AsSpan()),
                constant.Bytes.Length <= 40),
            _ => throw new InvalidDataException("Unknown canonical constant kind."),
        };

        private static Lua54UpvalueDescriptor ConvertUpvalue(LuaIrUpvalue upvalue) =>
            upvalue.SourceKind switch
            {
                LuaIrUpvalueSourceKind.Register =>
                    new(1, checked((byte)upvalue.SourceIndex), upvalue.Kind),
                LuaIrUpvalueSourceKind.Upvalue =>
                    new(0, checked((byte)upvalue.SourceIndex), upvalue.Kind),
                LuaIrUpvalueSourceKind.Environment =>
                    new(1, checked((byte)upvalue.SourceIndex), upvalue.Kind),
                _ => throw new InvalidDataException("Unknown canonical upvalue source kind."),
            };

        private static Lua54String? ConvertUpvalueName(LuaIrUpvalue upvalue) =>
            new(upvalue.DebugName.IsEmpty
                ? Encoding.UTF8.GetBytes(upvalue.Name)
                : upvalue.DebugName.AsSpan());

        private readonly record struct EmittedInstruction(
            Lua54Instruction Instruction,
            int SourceLine);

        private enum JumpPatchKind : byte
        {
            Jump,
            NumericPrepare,
            NumericLoop,
        }

        private readonly record struct JumpPatch(
            int InstructionIndex,
            int CanonicalTarget,
            int RawTarget,
            JumpPatchKind Kind)
        {
            public static JumpPatch ToCanonical(int instructionIndex, int target) =>
                new(instructionIndex, target, -1, JumpPatchKind.Jump);

            public static JumpPatch ToRaw(int instructionIndex, int target) =>
                new(instructionIndex, -1, target, JumpPatchKind.Jump);

            public static JumpPatch NumericPrepare(int instructionIndex, int target) =>
                new(instructionIndex, target, -1, JumpPatchKind.NumericPrepare);

            public static JumpPatch NumericLoop(int instructionIndex, int target) =>
                new(instructionIndex, target, -1, JumpPatchKind.NumericLoop);
        }
    }
}
