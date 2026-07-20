using System.Collections.Immutable;
using System.Text;
using Lunil.Core;
using Lunil.IR.Canonical;

namespace Lunil.IR.Lua53;

/// <summary>Lowers executable canonical IR into a portable PUC Lua 5.3 chunk.</summary>
public static class Lua53CanonicalPrototypeWriter
{
    private const int FieldsPerFlush = 50;
    private const int ScratchRegisterCount = 3;

    public static Lua53Chunk CreateChunk(
        LuaIrModule module,
        int functionId,
        Lua53ChunkTarget? target = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (module.LanguageVersion != LuaLanguageVersion.Lua53)
        {
            throw new InvalidDataException(
                $"Cannot write {LuaLanguageVersions.GetDisplayName(module.LanguageVersion)} " +
                "semantics as a PUC Lua 5.3 chunk.");
        }

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
        return new Lua53Chunk(
            target ?? Lua53ChunkTarget.Host,
            checked((byte)prototype.Upvalues.Length),
            prototype);
    }

    public static byte[] Write(
        LuaIrModule module,
        int functionId,
        bool stripDebugInformation = false,
        Lua53ChunkTarget? target = null) =>
        Lua53ChunkWriter.Write(
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

        public Lua53Prototype Convert(int functionId) =>
            new FunctionConverter(this, _module.Functions[functionId]).Convert();

        public ImmutableArray<int> GetChildren(int functionId) =>
            _children.TryGetValue(functionId, out var children) ? children : [];

        public LuaIrFunction GetFunction(int functionId) => _module.Functions[functionId];
    }

    private sealed class FunctionConverter
    {
        private readonly ModuleConverter _owner;
        private readonly LuaIrFunction _function;
        private readonly ImmutableArray<int> _children;
        private readonly Dictionary<int, int> _childIndexes;
        private readonly List<EmittedInstruction> _code = [];
        private readonly List<Lua53Constant> _constants = [];
        private readonly List<JumpPatch> _patches = [];
        private readonly Dictionary<int, int> _openSetListTables = [];
        private readonly Dictionary<int, int> _openSetListSources = [];
        private readonly int[] _programCounterMap;
        private readonly int _scratch0;
        private readonly int _scratch1;
        private readonly int _scratch2;
        private int _highestRegister;

        public FunctionConverter(ModuleConverter owner, LuaIrFunction function)
        {
            _owner = owner;
            _function = function;
            _children = owner.GetChildren(function.Id);
            _childIndexes = _children
                .Select(static (id, index) => (id, index))
                .ToDictionary(static pair => pair.id, static pair => pair.index);
            _programCounterMap = new int[function.Instructions.Length + 1];
            _scratch0 = function.RegisterCount;
            _scratch1 = checked(function.RegisterCount + 1);
            _scratch2 = checked(function.RegisterCount + 2);
            _highestRegister = function.ParameterCount - 1;
            foreach (var constant in function.Constants)
            {
                _constants.Add(ConvertConstant(constant));
            }

            FindOpenSetListTables();
        }

        public Lua53Prototype Convert()
        {
            if (_function.ParameterCount > byte.MaxValue)
            {
                throw new InvalidOperationException("A Lua 5.3 prototype cannot have more than 255 parameters.");
            }

            if (_function.Upvalues.Length > byte.MaxValue)
            {
                throw new InvalidOperationException("A Lua 5.3 prototype cannot have more than 255 upvalues.");
            }

            for (var pc = 0; pc < _function.Instructions.Length; pc++)
            {
                _programCounterMap[pc] = _code.Count;
                if (_openSetListTables.TryGetValue(pc, out var tableRegister) &&
                    IsOpenProducer(_function.Instructions[pc]))
                {
                    var sourceTableRegister = _openSetListSources[pc];
                    if (sourceTableRegister != tableRegister)
                    {
                        EmitAbc(Lua53Opcode.Move, tableRegister, sourceTableRegister, 0,
                            _function.Instructions[pc].SourceLine);
                    }
                }

                ConvertInstruction(pc, _function.Instructions[pc]);
            }

            _programCounterMap[^1] = _code.Count;
            PatchJumps();
            var maximumStackSize = checked(Math.Max(2, _highestRegister + 1));
            if (maximumStackSize > byte.MaxValue)
            {
                throw new InvalidOperationException(
                    "A Lua 5.3 prototype cannot use more than 255 registers.");
            }

            var lineInfo = _code.Select(static item => item.SourceLine).ToImmutableArray();
            return new Lua53Prototype
            {
                Source = _function.SourceName is [0x01]
                    ? new Lua53String([])
                    : _function.SourceName.IsEmpty
                        ? null
                        : new Lua53String(_function.SourceName.ToArray()),
                LineDefined = _function.LineDefined,
                LastLineDefined = _function.LastLineDefined,
                ParameterCount = checked((byte)_function.ParameterCount),
                VarArgFlags = _function.IsVarArg ? (byte)1 : (byte)0,
                MaximumStackSize = checked((byte)maximumStackSize),
                Code = _code.Select(static item => item.Instruction).ToImmutableArray(),
                Constants = _constants.ToImmutableArray(),
                Upvalues = _function.Upvalues.Select(ConvertUpvalue).ToImmutableArray(),
                NestedPrototypes = _children.Select(_owner.Convert).ToImmutableArray(),
                LineInfo = lineInfo,
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
                    EmitABx(Lua53Opcode.LoadConstant, instruction.A, instruction.B, line);
                    break;
                case LuaIrOpcode.LoadNil:
                    if (instruction.B > 0)
                    {
                        EmitAbc(Lua53Opcode.LoadNil, instruction.A, instruction.B - 1, 0, line);
                    }

                    break;
                case LuaIrOpcode.Move:
                    EmitAbc(Lua53Opcode.Move, instruction.A, instruction.B, 0, line);
                    break;
                case LuaIrOpcode.SetTop:
                    break;
                case LuaIrOpcode.GetUpvalue:
                    EmitAbc(Lua53Opcode.GetUpvalue, instruction.A, instruction.B, 0, line);
                    break;
                case LuaIrOpcode.SetUpvalue:
                    EmitAbc(Lua53Opcode.SetUpvalue, instruction.B, instruction.A, 0, line);
                    break;
                case LuaIrOpcode.NewTable:
                    EmitAbc(
                        Lua53Opcode.NewTable,
                        instruction.A,
                        EncodeFloatingByte(instruction.C),
                        EncodeFloatingByte(instruction.B == 0 ? 0 : 1 << (instruction.B - 1)),
                        line);
                    break;
                case LuaIrOpcode.GetTable:
                    EmitAbc(Lua53Opcode.GetTable, instruction.A, instruction.B, instruction.C, line);
                    break;
                case LuaIrOpcode.SetTable:
                    EmitAbc(Lua53Opcode.SetTable, instruction.A, instruction.B, instruction.C, line);
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

                    EmitABx(Lua53Opcode.Closure, instruction.A, childIndex, line);
                    break;
                case LuaIrOpcode.VarArg:
                    EmitAbc(Lua53Opcode.VarArg, instruction.A, 0,
                        instruction.B < 0 ? 0 : instruction.B + 1, line);
                    break;
                case LuaIrOpcode.Unary:
                    EmitUnary(instruction, line);
                    break;
                case LuaIrOpcode.Binary:
                    EmitBinary(instruction, line);
                    break;
                case LuaIrOpcode.Jump:
                    EmitJump(instruction.B, instruction.C >= 0 ? instruction.C : 0, line);
                    break;
                case LuaIrOpcode.JumpIfFalse:
                    EmitConditionalJump(instruction.A, false, instruction.B, line);
                    break;
                case LuaIrOpcode.JumpIfTrue:
                    EmitConditionalJump(instruction.A, true, instruction.B, line);
                    break;
                case LuaIrOpcode.Call:
                    EmitAbc(Lua53Opcode.Call, instruction.A,
                        instruction.B < 0 ? 0 : instruction.B + 1,
                        instruction.C < 0 ? 0 : instruction.C + 1, line);
                    break;
                case LuaIrOpcode.TailCall:
                    EmitAbc(Lua53Opcode.TailCall, instruction.A,
                        instruction.B < 0 ? 0 : instruction.B + 1, 0, line);
                    break;
                case LuaIrOpcode.Return:
                    EmitAbc(Lua53Opcode.Return, instruction.A,
                        instruction.B < 0 ? 0 : instruction.B + 1, 0, line);
                    break;
                case LuaIrOpcode.Close:
                    EmitJumpToNext(instruction.A, line);
                    break;
                case LuaIrOpcode.MarkToBeClosed:
                    throw new InvalidDataException("Lua 5.3 does not support to-be-closed locals.");
                case LuaIrOpcode.NumericForPrepare:
                    EmitTargeted(
                        Lua53Opcode.NumericForPrepare,
                        instruction.A,
                        FindNumericForLoopProgramCounter(programCounter, instruction),
                        line);
                    break;
                case LuaIrOpcode.NumericForLoop:
                    EmitTargeted(Lua53Opcode.NumericForLoop, instruction.A, instruction.B, line);
                    break;
                default:
                    throw new InvalidDataException(
                        $"Unsupported canonical opcode {instruction.Opcode} for Lua 5.3.");
            }
        }

        private int FindNumericForLoopProgramCounter(
            int prepareProgramCounter,
            LuaIrInstruction prepare)
        {
            for (var pc = prepare.B - 1; pc > prepareProgramCounter; pc--)
            {
                if (_function.Instructions[pc] is
                    {
                        Opcode: LuaIrOpcode.NumericForLoop,
                        A: var register,
                    } && register == prepare.A)
                {
                    return pc;
                }
            }

            throw new InvalidDataException(
                "A Lua 5.3 numeric-for prepare instruction has no matching loop instruction.");
        }

        private void EmitUnary(LuaIrInstruction instruction, int line)
        {
            var opcode = (LuaIrUnaryOperator)instruction.C switch
            {
                LuaIrUnaryOperator.Negate => Lua53Opcode.UnaryMinus,
                LuaIrUnaryOperator.BitwiseNot => Lua53Opcode.BitwiseNot,
                LuaIrUnaryOperator.LogicalNot => Lua53Opcode.LogicalNot,
                LuaIrUnaryOperator.Length => Lua53Opcode.Length,
                _ => throw new InvalidDataException("Unknown canonical unary operator."),
            };
            EmitAbc(opcode, instruction.A, instruction.B, 0, line);
        }

        private void EmitBinary(LuaIrInstruction instruction, int line)
        {
            var operation = (LuaIrBinaryOperator)instruction.D;
            if (IsComparison(operation))
            {
                EmitComparison(instruction.A, instruction.B, instruction.C, operation, line);
                return;
            }

            var opcode = operation switch
            {
                LuaIrBinaryOperator.Add => Lua53Opcode.Add,
                LuaIrBinaryOperator.Subtract => Lua53Opcode.Subtract,
                LuaIrBinaryOperator.Multiply => Lua53Opcode.Multiply,
                LuaIrBinaryOperator.Modulo => Lua53Opcode.Modulo,
                LuaIrBinaryOperator.Power => Lua53Opcode.Power,
                LuaIrBinaryOperator.Divide => Lua53Opcode.Divide,
                LuaIrBinaryOperator.FloorDivide => Lua53Opcode.FloorDivide,
                LuaIrBinaryOperator.BitwiseAnd => Lua53Opcode.BitwiseAnd,
                LuaIrBinaryOperator.BitwiseOr => Lua53Opcode.BitwiseOr,
                LuaIrBinaryOperator.BitwiseXor => Lua53Opcode.BitwiseXor,
                LuaIrBinaryOperator.ShiftLeft => Lua53Opcode.ShiftLeft,
                LuaIrBinaryOperator.ShiftRight => Lua53Opcode.ShiftRight,
                LuaIrBinaryOperator.Concatenate => Lua53Opcode.Concatenate,
                _ => throw new InvalidDataException("Unknown canonical binary operator."),
            };
            if (operation == LuaIrBinaryOperator.Concatenate)
            {
                EmitAbc(Lua53Opcode.Move, _scratch0, instruction.B, 0, line);
                EmitAbc(Lua53Opcode.Move, _scratch1, instruction.C, 0, line);
                EmitAbc(Lua53Opcode.Concatenate, instruction.A, _scratch0, _scratch1, line);
            }
            else
            {
                EmitAbc(opcode, instruction.A, instruction.B, instruction.C, line);
            }
        }

        private void EmitComparison(
            int destination,
            int left,
            int right,
            LuaIrBinaryOperator operation,
            int line)
        {
            var opcode = operation switch
            {
                LuaIrBinaryOperator.Equal or LuaIrBinaryOperator.NotEqual => Lua53Opcode.Equal,
                LuaIrBinaryOperator.LessThan or LuaIrBinaryOperator.GreaterThan => Lua53Opcode.LessThan,
                LuaIrBinaryOperator.LessThanOrEqual or LuaIrBinaryOperator.GreaterThanOrEqual =>
                    Lua53Opcode.LessOrEqual,
                _ => throw new InvalidDataException("Unknown comparison operator."),
            };
            if (operation is LuaIrBinaryOperator.GreaterThan or LuaIrBinaryOperator.GreaterThanOrEqual)
            {
                (left, right) = (right, left);
            }

            var accepted = operation is not LuaIrBinaryOperator.NotEqual;
            EmitAbc(Lua53Opcode.Move, _scratch0, left, 0, line);
            EmitAbc(Lua53Opcode.Move, _scratch1, right, 0, line);
            EmitAbc(opcode, accepted ? 1 : 0, _scratch0, _scratch1, line);
            var jumpIndex = _code.Count;
            Emit(Lua53Instruction.CreateASignedBx(Lua53Opcode.Jump, 0, 0), line);
            EmitAbc(Lua53Opcode.LoadBoolean, destination, 0, 1, line);
            var trueProgramCounter = _code.Count;
            EmitAbc(Lua53Opcode.LoadBoolean, destination, 1, 0, line);
            _code[jumpIndex] = _code[jumpIndex] with
            {
                Instruction = Lua53Instruction.CreateASignedBx(
                    Lua53Opcode.Jump,
                    0,
                    trueProgramCounter - jumpIndex - 1),
            };
        }

        private void EmitSetList(int programCounter, LuaIrInstruction instruction, int line)
        {
            if (instruction.D >= 0)
            {
                EmitLoadGeneratedConstant(_scratch0, LuaIrConstant.FromInteger(instruction.B), line);
                EmitAbc(Lua53Opcode.SetTable, instruction.A, _scratch0, instruction.C, line);
                return;
            }

            if (!_openSetListTables.TryGetValue(programCounter, out var tableRegister))
            {
                throw new InvalidDataException(
                    "An open Lua 5.3 SetList must be associated with an open call or vararg producer.");
            }

            var block = ((instruction.B - 1) / FieldsPerFlush) + 1;
            if (block > Lua53Instruction.MaximumC)
            {
                throw new InvalidDataException("Lua 5.3 SetList block exceeds the opcode field width.");
            }

            EmitAbc(Lua53Opcode.SetList, tableRegister, 0, block, line);
        }

        private void FindOpenSetListTables()
        {
            for (var producer = 0; producer < _function.Instructions.Length; producer++)
            {
                var candidate = _function.Instructions[producer];
                var openProducer = candidate.Opcode == LuaIrOpcode.Call && candidate.C < 0 ||
                    candidate.Opcode == LuaIrOpcode.VarArg && candidate.B < 0;
                if (!openProducer)
                {
                    continue;
                }

                var setList = producer + 1;
                while (setList < _function.Instructions.Length &&
                       _function.Instructions[setList] is
                       { Opcode: LuaIrOpcode.Call, B: < 0, C: < 0 })
                {
                    setList++;
                }

                if (setList >= _function.Instructions.Length ||
                    _function.Instructions[setList] is not { Opcode: LuaIrOpcode.SetList, D: < 0 } list ||
                    list.C != candidate.A)
                {
                    continue;
                }

                var block = (list.B - 1) / FieldsPerFlush;
                var offset = list.B - block * FieldsPerFlush;
                var tableRegister = candidate.A - offset;
                if (tableRegister < 0)
                {
                    throw new InvalidDataException("An open SetList has no representable table register.");
                }

                _openSetListTables[setList] = tableRegister;
                _openSetListTables[producer] = tableRegister;
                _openSetListSources[producer] = list.A;
            }
        }

        private void EmitConditionalJump(int register, bool whenTrue, int target, int line)
        {
            EmitAbc(Lua53Opcode.Test, register, 0, whenTrue ? 1 : 0, line);
            EmitJump(target, 0, line);
        }

        private void EmitJumpToNext(int closeRegister, int line)
        {
            EmitJumpRaw(Lua53Opcode.Jump, closeRegister, 0, line, _function.Instructions.Length);
        }

        private void EmitJump(int target, int closeRegister, int line) =>
            EmitJumpRaw(Lua53Opcode.Jump, closeRegister, 0, line, target);

        private void EmitTargeted(Lua53Opcode opcode, int register, int target, int line)
        {
            var index = _code.Count;
            Emit(Lua53Instruction.CreateASignedBx(opcode, register, 0), line);
            _patches.Add(new JumpPatch(index, target));
        }

        private void EmitJumpRaw(
            Lua53Opcode opcode,
            int a,
            int signedBx,
            int line,
            int target)
        {
            var index = _code.Count;
            Emit(Lua53Instruction.CreateASignedBx(opcode, a, signedBx), line);
            _patches.Add(new JumpPatch(index, target));
        }

        private void PatchJumps()
        {
            foreach (var patch in _patches)
            {
                if ((uint)patch.RawTarget > (uint)_function.Instructions.Length)
                {
                    throw new InvalidDataException(
                        $"Lua 5.3 jump target {patch.RawTarget} is outside the canonical function.");
                }

                var target = _programCounterMap[patch.RawTarget];
                var signedBx = checked(target - patch.InstructionIndex - 1);
                var current = _code[patch.InstructionIndex].Instruction;
                _code[patch.InstructionIndex] = _code[patch.InstructionIndex] with
                {
                    Instruction = Lua53Instruction.CreateASignedBx(
                        current.Opcode,
                        current.A,
                        signedBx),
                };
            }
        }

        private void EmitLoadGeneratedConstant(int register, LuaIrConstant constant, int line)
        {
            var index = _constants.Count;
            _constants.Add(ConvertConstant(constant));
            EmitABx(Lua53Opcode.LoadConstant, register, index, line);
        }

        private void Emit(
            Lua53Instruction instruction,
            int line)
        {
            TrackRegisters(instruction);
            _code.Add(new EmittedInstruction(instruction, line));
        }

        private void EmitAbc(Lua53Opcode opcode, int a, int b, int c, int line) =>
            Emit(Lua53Instruction.CreateAbc(opcode, a, b, c), line);

        private void EmitABx(Lua53Opcode opcode, int a, int bx, int line) =>
            Emit(Lua53Instruction.CreateABx(opcode, a, bx), line);

        private void TrackRegisters(Lua53Instruction instruction)
        {
            var highest = instruction.A;
            switch (instruction.Opcode)
            {
                case Lua53Opcode.Move:
                case Lua53Opcode.GetTable:
                case Lua53Opcode.SetTable:
                case Lua53Opcode.Add:
                case Lua53Opcode.Subtract:
                case Lua53Opcode.Multiply:
                case Lua53Opcode.Modulo:
                case Lua53Opcode.Power:
                case Lua53Opcode.Divide:
                case Lua53Opcode.FloorDivide:
                case Lua53Opcode.BitwiseAnd:
                case Lua53Opcode.BitwiseOr:
                case Lua53Opcode.BitwiseXor:
                case Lua53Opcode.ShiftLeft:
                case Lua53Opcode.ShiftRight:
                case Lua53Opcode.Concatenate:
                case Lua53Opcode.Equal:
                case Lua53Opcode.LessThan:
                case Lua53Opcode.LessOrEqual:
                    highest = Math.Max(highest, Math.Max(instruction.B & 0xff, instruction.C & 0xff));
                    break;
                case Lua53Opcode.LoadNil:
                    highest = Math.Max(highest, instruction.A + instruction.B);
                    break;
                case Lua53Opcode.Call:
                case Lua53Opcode.TailCall:
                case Lua53Opcode.Return:
                case Lua53Opcode.VarArg:
                    if (instruction.B != 0)
                    {
                        highest = Math.Max(highest, instruction.A + instruction.B - 1);
                    }

                    if (instruction.C != 0)
                    {
                        highest = Math.Max(highest, instruction.A + instruction.C - 1);
                    }

                    break;
                case Lua53Opcode.SetList:
                    highest = Math.Max(highest, instruction.A + 1);
                    break;
            }

            _highestRegister = Math.Max(_highestRegister, highest);
        }

        private ImmutableArray<Lua53LocalVariable> ConvertLocalVariables()
        {
            var result = ImmutableArray.CreateBuilder<Lua53LocalVariable>(_function.LocalVariables.Length);
            foreach (var local in _function.LocalVariables)
            {
                if ((uint)local.StartProgramCounter >= (uint)_programCounterMap.Length ||
                    (uint)local.EndProgramCounter >= (uint)_programCounterMap.Length)
                {
                    throw new InvalidDataException("Lua 5.3 local variable range is invalid.");
                }

                result.Add(new Lua53LocalVariable(
                    new Lua53String(local.Name.ToArray()),
                    _programCounterMap[local.StartProgramCounter],
                    _programCounterMap[local.EndProgramCounter]));
            }

            return result.MoveToImmutable();
        }

        private static int EncodeFloatingByte(int value)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            if (value < 8)
            {
                return value;
            }

            var exponent = 0;
            while (value >= 8 << 4)
            {
                value = (value + 0xf) >> 4;
                exponent += 4;
            }

            while (value >= 8 << 1)
            {
                value = (value + 1) >> 1;
                exponent++;
            }

            return ((exponent + 1) << 3) | (value - 8);
        }

        private static Lua53Constant ConvertConstant(LuaIrConstant constant) => constant.Kind switch
        {
            LuaIrConstantKind.Nil => Lua53Constant.Nil,
            LuaIrConstantKind.Boolean => Lua53Constant.FromBoolean(constant.Boolean),
            LuaIrConstantKind.Integer => Lua53Constant.FromInteger(constant.Integer),
            LuaIrConstantKind.Float => Lua53Constant.FromFloat(constant.Float),
            LuaIrConstantKind.String => Lua53Constant.FromString(
                new Lua53String(constant.Bytes.ToArray()), constant.Bytes.Length <= 40),
            _ => throw new InvalidDataException("Unknown canonical constant kind."),
        };

        private static Lua53UpvalueDescriptor ConvertUpvalue(LuaIrUpvalue upvalue) =>
            upvalue.SourceKind switch
            {
                LuaIrUpvalueSourceKind.Register =>
                    new(1, checked((byte)upvalue.SourceIndex)),
                LuaIrUpvalueSourceKind.Upvalue =>
                    new(0, checked((byte)upvalue.SourceIndex)),
                LuaIrUpvalueSourceKind.Environment =>
                    new(1, checked((byte)upvalue.SourceIndex)),
                _ => throw new InvalidDataException("Unknown canonical upvalue source kind."),
            };

        private static Lua53String? ConvertUpvalueName(LuaIrUpvalue upvalue)
        {
            if (upvalue.DebugName.IsEmpty &&
                upvalue.Name.StartsWith("(upvalue ", StringComparison.Ordinal) &&
                upvalue.Name.EndsWith(')'))
            {
                return null;
            }

            return new Lua53String(upvalue.DebugName.IsEmpty
                ? Encoding.UTF8.GetBytes(upvalue.Name)
                : upvalue.DebugName.ToArray());
        }

        private static bool IsOpenProducer(LuaIrInstruction instruction) =>
            instruction.Opcode == LuaIrOpcode.Call && instruction.C < 0 ||
            instruction.Opcode == LuaIrOpcode.VarArg && instruction.B < 0;

        private static bool IsComparison(LuaIrBinaryOperator operation) => operation is
            LuaIrBinaryOperator.Equal or LuaIrBinaryOperator.NotEqual or
            LuaIrBinaryOperator.LessThan or LuaIrBinaryOperator.LessThanOrEqual or
            LuaIrBinaryOperator.GreaterThan or LuaIrBinaryOperator.GreaterThanOrEqual;

        private readonly record struct EmittedInstruction(Lua53Instruction Instruction, int SourceLine);
        private readonly record struct JumpPatch(int InstructionIndex, int RawTarget);
    }
}
