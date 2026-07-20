using System.Collections.Immutable;
using System.Text;
using Lunil.Core;
using Lunil.IR.Canonical;

namespace Lunil.IR.Lua54;

/// <summary>Lowers executable canonical IR back into a portable PUC Lua 5.4 chunk.</summary>
public static class Lua54CanonicalPrototypeWriter
{
    private const int FieldsPerFlush = 50;

    public static Lua54Chunk CreateChunk(
        LuaIrModule module,
        int functionId,
        Lua54ChunkTarget? target = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (module.LanguageVersion != LuaLanguageVersion.Lua54)
        {
            throw new InvalidDataException(
                $"Cannot write {LuaLanguageVersions.GetDisplayName(module.LanguageVersion)} " +
                "semantics as a PUC Lua 5.4 chunk.");
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

        public LuaIrFunction GetFunction(int functionId) => _module.Functions[functionId];
    }

    private sealed class FunctionConverter
    {
        private readonly ModuleConverter _owner;
        private readonly LuaIrFunction _function;
        private readonly ImmutableArray<int> _children;
        private readonly Dictionary<int, int> _childIndexes;
        private readonly List<EmittedInstruction> _code = [];
        private readonly List<Lua54Constant> _constants = [];
        private readonly List<JumpPatch> _patches = [];
        private readonly Dictionary<int, int> _openSetListBases = [];
        private readonly HashSet<int> _canonicalJumpTargets;
        private readonly HashSet<int> _capturedRegisters;
        private readonly int[] _programCounterMap;
        private readonly bool[][] _liveAfter;
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
            _canonicalJumpTargets = CollectCanonicalJumpTargets(function);
            _capturedRegisters = CollectCapturedRegisters(owner, _children);
            _programCounterMap = new int[function.Instructions.Length + 1];
            _liveAfter = AnalyzeRegisterLiveness(owner, function, _capturedRegisters);
            _temporary0 = function.RegisterCount;
            _temporary1 = checked(function.RegisterCount + 1);
        }

        public Lua54Prototype Convert()
        {
            if (_function.ParameterCount > byte.MaxValue)
            {
                throw new InvalidOperationException("A Lua 5.4 prototype cannot have more than 255 parameters.");
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
                if (TryEmitOptimizedInstructionSequence(pc, out var finalProgramCounter))
                {
                    for (var consumed = pc + 1; consumed <= finalProgramCounter; consumed++)
                    {
                        _programCounterMap[consumed] = _programCounterMap[pc];
                    }

                    pc = finalProgramCounter;
                    continue;
                }

                if (TryEmitFusedComparisonBranch(pc))
                {
                    _programCounterMap[++pc] = _programCounterMap[pc - 1];
                    continue;
                }

                ConvertInstruction(pc, _function.Instructions[pc]);
            }

            _programCounterMap[^1] = _code.Count;
            PatchJumps();
            var (lineInfo, absoluteLineInfo) = EncodeLines();
            var maximumStackSize = GetMaximumStackSize();

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
                Constants = _constants.ToImmutableArray(),
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
                        line,
                        k: _capturedRegisters.Count != 0);
                    break;
                case LuaIrOpcode.Return:
                    EmitAbc(
                        Lua54Opcode.Return,
                        instruction.A,
                        instruction.B < 0 ? 0 : instruction.B + 1,
                        VarArgReturnAdjustment,
                        line,
                        k: _capturedRegisters.Count != 0);
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
            EmitConstantValue(register, _function.Constants[constant], line);
        }

        private void EmitConstantValue(int register, LuaIrConstant value, int line)
        {
            switch (value.Kind)
            {
                case LuaIrConstantKind.Nil:
                    EmitAbc(Lua54Opcode.LoadNil, register, 0, 0, line);
                    return;
                case LuaIrConstantKind.Boolean:
                    EmitAbc(
                        value.Boolean ? Lua54Opcode.LoadTrue : Lua54Opcode.LoadFalse,
                        register,
                        0,
                        0,
                        line);
                    return;
                case LuaIrConstantKind.Integer when CanEncodeSignedBx(value.Integer):
                    Emit(
                        Lua54Instruction.CreateASignedBx(
                            Lua54Opcode.LoadInteger,
                            register,
                            checked((int)value.Integer)),
                        line);
                    return;
                case LuaIrConstantKind.Float when TryGetLoadFloatImmediate(value.Float, out var immediate):
                    Emit(
                        Lua54Instruction.CreateASignedBx(
                            Lua54Opcode.LoadFloat,
                            register,
                            immediate),
                        line);
                    return;
            }

            var emittedConstant = GetOrAddConstant(value);
            if (emittedConstant <= Lua54Instruction.MaximumBx)
            {
                Emit(
                    Lua54Instruction.CreateABx(
                        Lua54Opcode.LoadConstant,
                        register,
                        emittedConstant),
                    line);
                return;
            }

            if (emittedConstant > Lua54Instruction.MaximumAx)
            {
                throw new InvalidOperationException("A Lua 5.4 constant index exceeds the chunk format.");
            }

            Emit(Lua54Instruction.CreateABx(Lua54Opcode.LoadConstantExtra, register, 0), line);
            Emit(Lua54Instruction.CreateAx(Lua54Opcode.ExtraArgument, emittedConstant), line);
        }

        private bool TryEmitOptimizedInstructionSequence(
            int programCounter,
            out int finalProgramCounter)
        {
            finalProgramCounter = programCounter;
            if (TryEmitOptimizedBinarySequence(programCounter, out finalProgramCounter))
            {
                return true;
            }

            if (programCounter + 1 >= _function.Instructions.Length ||
                _canonicalJumpTargets.Contains(programCounter + 1))
            {
                return false;
            }

            var first = _function.Instructions[programCounter];
            var second = _function.Instructions[programCounter + 1];
            if (first.Opcode != LuaIrOpcode.Move ||
                first.SourceLine != second.SourceLine ||
                _function.LocalVariables.Any(local =>
                    local.StartProgramCounter == programCounter + 1 ||
                    local.EndProgramCounter == programCounter + 1) ||
                !IsOptimizationTemporary(first.A, programCounter))
            {
                return false;
            }

            if (second is { Opcode: LuaIrOpcode.Move } && second.B == first.A &&
                IsTemporaryDiscardedAfter(programCounter + 1, first.A))
            {
                EmitAbc(Lua54Opcode.Move, second.A, first.B, 0, second.SourceLine);

                finalProgramCounter = programCounter + 1;
                return true;
            }

            if (second is { Opcode: LuaIrOpcode.Return, B: 1 } &&
                second.A == first.A &&
                IsOptimizationTemporary(first.B, programCounter))
            {
                EmitAbc(
                    Lua54Opcode.Return,
                    first.B,
                    2,
                    VarArgReturnAdjustment,
                    second.SourceLine,
                    k: _capturedRegisters.Count != 0);
                finalProgramCounter = programCounter + 1;
                return true;
            }

            return false;
        }

        private bool TryEmitOptimizedBinarySequence(
            int programCounter,
            out int finalProgramCounter)
        {
            finalProgramCounter = programCounter;
            var preparations = new List<PreparedRegister>(2);
            var cursor = programCounter;
            while (cursor < _function.Instructions.Length && preparations.Count < 2)
            {
                var candidate = _function.Instructions[cursor];
                if (!IsOptimizationTemporary(candidate.A, cursor))
                {
                    break;
                }

                if (candidate.Opcode == LuaIrOpcode.Move)
                {
                    preparations.Add(PreparedRegister.FromRegister(candidate.A, candidate.B));
                }
                else if (candidate.Opcode == LuaIrOpcode.LoadConstant)
                {
                    preparations.Add(PreparedRegister.FromConstant(
                        candidate.A,
                        _function.Constants[candidate.B]));
                }
                else
                {
                    break;
                }

                cursor++;
            }

            if (preparations.Count == 0 || cursor >= _function.Instructions.Length)
            {
                return false;
            }

            var binary = _function.Instructions[cursor];
            if (binary.Opcode != LuaIrOpcode.Binary ||
                (LuaIrBinaryOperator)binary.D == LuaIrBinaryOperator.Concatenate ||
                _capturedRegisters.Contains(binary.A) ||
                Enumerable.Range(programCounter, cursor - programCounter)
                    .Any(pc => _function.Instructions[pc].SourceLine != binary.SourceLine) ||
                preparations.Any(preparation =>
                    preparation.Destination != binary.B && preparation.Destination != binary.C))
            {
                return false;
            }

            if (Enumerable.Range(programCounter + 1, cursor - programCounter)
                .Any(HasCanonicalControlOrDebugBoundary))
            {
                return false;
            }

            var left = ResolvePreparedOperand(binary.B, preparations);
            var right = ResolvePreparedOperand(binary.C, preparations);
            var op = (LuaIrBinaryOperator)binary.D;
            var output = binary.A;
            var end = cursor;
            if (!IsComparison(op) && cursor + 1 < _function.Instructions.Length)
            {
                var resultMove = _function.Instructions[cursor + 1];
                if (!HasCanonicalControlOrDebugBoundary(cursor + 1) &&
                    resultMove is { Opcode: LuaIrOpcode.Move } && resultMove.B == binary.A &&
                    resultMove.SourceLine == binary.SourceLine &&
                    IsOptimizationTemporary(binary.A, cursor) &&
                    !_capturedRegisters.Contains(resultMove.A) &&
                    IsTemporaryDiscardedAfter(cursor + 1, binary.A))
                {
                    output = resultMove.A;
                    end++;
                }
            }

            if (IsComparison(op))
            {
                if (cursor + 1 >= _function.Instructions.Length ||
                    HasCanonicalControlOrDebugBoundary(cursor + 1))
                {
                    return false;
                }

                var branch = _function.Instructions[cursor + 1];
                var leftMaterializationRegister = left.PreparationRegister;
                var rightMaterializationRegister = right.PreparationRegister;
                if (left.IsConstant && !right.IsConstant && binary.A != right.Register)
                {
                    leftMaterializationRegister = binary.A;
                }
                else if (!left.IsConstant && right.IsConstant && binary.A != left.Register)
                {
                    rightMaterializationRegister = binary.A;
                }

                if (left.IsConstant && !right.IsConstant &&
                    leftMaterializationRegister == right.Register ||
                    !left.IsConstant && right.IsConstant &&
                    rightMaterializationRegister == left.Register ||
                    left.IsConstant && right.IsConstant &&
                    leftMaterializationRegister == rightMaterializationRegister &&
                    !left.Constant.Equals(right.Constant))
                {
                    return false;
                }

                if (branch.Opcode is not (LuaIrOpcode.JumpIfFalse or LuaIrOpcode.JumpIfTrue) ||
                    branch.A != binary.A ||
                    _liveAfter[cursor + 1][binary.A] ||
                    preparations.Any(preparation =>
                        preparation.Destination != binary.A &&
                        _liveAfter[cursor + 1][preparation.Destination]) ||
                    !IsOptimizationTemporary(binary.A, cursor) ||
                    !TryMaterializeOperand(
                        left,
                        leftMaterializationRegister,
                        binary.SourceLine,
                        out var leftRegister) ||
                    !TryMaterializeOperand(
                        right,
                        rightMaterializationRegister,
                        binary.SourceLine,
                        out var rightRegister))
                {
                    return false;
                }

                EmitDirectComparisonBranch(
                    leftRegister,
                    rightRegister,
                    op,
                    branch.Opcode == LuaIrOpcode.JumpIfTrue,
                    branch.B,
                    binary.SourceLine,
                    branch.SourceLine);
                finalProgramCounter = cursor + 1;
                return true;
            }

            if (preparations.Any(preparation =>
                    preparation.Destination != binary.A &&
                    preparation.Destination != output &&
                    _liveAfter[end][preparation.Destination]))
            {
                return false;
            }

            if (!TryEmitSelectedArithmetic(output, left, right, op, binary.SourceLine))
            {
                return false;
            }

            finalProgramCounter = end;
            return true;
        }

        private bool HasCanonicalControlOrDebugBoundary(int programCounter) =>
            _canonicalJumpTargets.Contains(programCounter) ||
            _function.LocalVariables.Any(local =>
                local.StartProgramCounter == programCounter ||
                local.EndProgramCounter == programCounter);

        private bool TryEmitSelectedArithmetic(
            int destination,
            PreparedOperand left,
            PreparedOperand right,
            LuaIrBinaryOperator op,
            int line)
        {
            var flipped = false;
            if (left.IsConstant && !right.IsConstant && IsCommutative(op))
            {
                (left, right) = (right, left);
                flipped = true;
            }

            if (left.IsConstant || !TryGetRegister(left, out var leftRegister))
            {
                return false;
            }

            var (registerOpcode, eventCode) = GetArithmeticOpcode(op);
            if (!right.IsConstant)
            {
                if (!TryGetRegister(right, out var rightRegister))
                {
                    return false;
                }

                EmitAbc(registerOpcode, destination, leftRegister, rightRegister, line);
                EmitAbc(
                    Lua54Opcode.MetamethodBinary,
                    leftRegister,
                    rightRegister,
                    eventCode,
                    line);
                return true;
            }

            if (op == LuaIrBinaryOperator.Add &&
                right.Constant.Kind == LuaIrConstantKind.Integer &&
                right.Constant.Integer is >= -Lua54Instruction.SignedCOffset and
                    <= Lua54Instruction.MaximumC - Lua54Instruction.SignedCOffset)
            {
                var encoded = checked((int)right.Constant.Integer) + Lua54Instruction.SignedCOffset;
                EmitAbc(Lua54Opcode.AddImmediate, destination, leftRegister, encoded, line);
                EmitAbc(
                    Lua54Opcode.MetamethodBinaryImmediate,
                    leftRegister,
                    encoded,
                    eventCode,
                    line,
                    k: flipped);
                return true;
            }

            var constantOpcode = GetConstantArithmeticOpcode(op);
            if (constantOpcode is null || right.Constant.Kind is not
                (LuaIrConstantKind.Integer or LuaIrConstantKind.Float) ||
                ((op is LuaIrBinaryOperator.BitwiseAnd or LuaIrBinaryOperator.BitwiseOr or
                    LuaIrBinaryOperator.BitwiseXor) &&
                 right.Constant.Kind != LuaIrConstantKind.Integer))
            {
                return false;
            }

            var constant = GetOrAddConstant(right.Constant);
            if (constant > byte.MaxValue)
            {
                return false;
            }

            EmitAbc(constantOpcode.Value, destination, leftRegister, constant, line);
            EmitAbc(
                Lua54Opcode.MetamethodBinaryConstant,
                leftRegister,
                constant,
                eventCode,
                line,
                k: flipped);
            return true;
        }

        private void EmitDirectComparisonBranch(
            int left,
            int right,
            LuaIrBinaryOperator op,
            bool branchWhenTrue,
            int target,
            int comparisonLine,
            int branchLine)
        {
            var acceptedWhenTrue = true;
            var opcode = op switch
            {
                LuaIrBinaryOperator.Equal => Lua54Opcode.Equal,
                LuaIrBinaryOperator.NotEqual => Lua54Opcode.Equal,
                LuaIrBinaryOperator.LessThan => Lua54Opcode.LessThan,
                LuaIrBinaryOperator.LessThanOrEqual => Lua54Opcode.LessOrEqual,
                LuaIrBinaryOperator.GreaterThan => Lua54Opcode.LessThan,
                LuaIrBinaryOperator.GreaterThanOrEqual => Lua54Opcode.LessOrEqual,
                _ => throw new InvalidOperationException("The fused operation is not a comparison."),
            };
            if (op == LuaIrBinaryOperator.NotEqual)
            {
                acceptedWhenTrue = false;
            }
            else if (op is LuaIrBinaryOperator.GreaterThan or LuaIrBinaryOperator.GreaterThanOrEqual)
            {
                (left, right) = (right, left);
            }

            Emit(
                Lua54Instruction.CreateAbc(
                    opcode,
                    left,
                    right,
                    0,
                    branchWhenTrue ? acceptedWhenTrue : !acceptedWhenTrue),
                comparisonLine);
            EmitJump(target, branchLine);
        }

        private bool TryMaterializeOperand(
            PreparedOperand operand,
            int preferredRegister,
            int line,
            out int register)
        {
            register = operand.Register;
            if (!operand.IsConstant)
            {
                return true;
            }

            register = preferredRegister;
            EmitConstantValue(register, operand.Constant, line);
            return true;
        }

        private int GetMaximumStackSize()
        {
            var highest = _function.ParameterCount - 1;
            highest = Math.Max(
                highest,
                _function.LocalVariables.Length == 0
                    ? -1
                    : Enumerable.Range(0, _function.Instructions.Length)
                        .Select(GetActiveLocalRegisterCount)
                        .DefaultIfEmpty(0)
                        .Max() - 1);
            foreach (var child in _children.Select(_owner.GetFunction))
            {
                foreach (var upvalue in child.Upvalues.Where(static upvalue =>
                             upvalue.SourceKind == LuaIrUpvalueSourceKind.Register))
                {
                    highest = Math.Max(highest, upvalue.SourceIndex);
                }
            }

            foreach (var emitted in _code)
            {
                highest = Math.Max(highest, GetHighestRegister(emitted.Instruction));
            }

            var size = Math.Max(2, checked(highest + 1));
            if (size > byte.MaxValue)
            {
                throw new InvalidOperationException(
                    "A Lua 5.4 prototype cannot use more than 255 registers.");
            }

            return size;
        }

        private int GetHighestRegister(Lua54Instruction instruction)
        {
            var a = instruction.A;
            return instruction.Opcode switch
            {
                Lua54Opcode.Move => Math.Max(a, instruction.B),
                Lua54Opcode.LoadInteger or Lua54Opcode.LoadFloat or Lua54Opcode.LoadConstant or
                    Lua54Opcode.LoadConstantExtra or Lua54Opcode.LoadFalse or
                    Lua54Opcode.LoadFalseAndSkip or Lua54Opcode.LoadTrue or
                    Lua54Opcode.GetUpvalue or Lua54Opcode.NewTable or Lua54Opcode.Closure or
                    Lua54Opcode.ReturnOne => a,
                Lua54Opcode.LoadNil => checked(a + instruction.B),
                Lua54Opcode.SetUpvalue or Lua54Opcode.Close or Lua54Opcode.ToBeClosed or
                    Lua54Opcode.Test => a,
                Lua54Opcode.GetTable => Math.Max(a, Math.Max(instruction.B, instruction.C)),
                Lua54Opcode.GetInteger or Lua54Opcode.GetField => Math.Max(a, instruction.B),
                Lua54Opcode.SetTable => Math.Max(
                    a,
                    Math.Max(instruction.B, instruction.K ? -1 : instruction.C)),
                Lua54Opcode.SetInteger or Lua54Opcode.SetField =>
                    Math.Max(a, instruction.K ? -1 : instruction.C),
                Lua54Opcode.Add or Lua54Opcode.Subtract or Lua54Opcode.Multiply or
                    Lua54Opcode.Modulo or Lua54Opcode.Power or Lua54Opcode.Divide or
                    Lua54Opcode.FloorDivide or Lua54Opcode.BitwiseAnd or Lua54Opcode.BitwiseOr or
                    Lua54Opcode.BitwiseXor or Lua54Opcode.ShiftLeft or Lua54Opcode.ShiftRight =>
                    Math.Max(a, Math.Max(instruction.B, instruction.C)),
                Lua54Opcode.AddImmediate or Lua54Opcode.AddConstant or
                    Lua54Opcode.SubtractConstant or Lua54Opcode.MultiplyConstant or
                    Lua54Opcode.ModuloConstant or Lua54Opcode.PowerConstant or
                    Lua54Opcode.DivideConstant or Lua54Opcode.FloorDivideConstant or
                    Lua54Opcode.BitwiseAndConstant or Lua54Opcode.BitwiseOrConstant or
                    Lua54Opcode.BitwiseXorConstant or Lua54Opcode.ShiftRightImmediate or
                    Lua54Opcode.ShiftLeftImmediate => Math.Max(a, instruction.B),
                Lua54Opcode.MetamethodBinary => Math.Max(a, instruction.B),
                Lua54Opcode.MetamethodBinaryImmediate or Lua54Opcode.MetamethodBinaryConstant => a,
                Lua54Opcode.UnaryMinus or Lua54Opcode.BitwiseNot or Lua54Opcode.LogicalNot or
                    Lua54Opcode.Length => Math.Max(a, instruction.B),
                Lua54Opcode.Concatenate => checked(a + instruction.B - 1),
                Lua54Opcode.Equal or Lua54Opcode.LessThan or Lua54Opcode.LessOrEqual =>
                    Math.Max(a, instruction.B),
                Lua54Opcode.EqualConstant or Lua54Opcode.EqualImmediate or
                    Lua54Opcode.LessThanImmediate or Lua54Opcode.LessOrEqualImmediate or
                    Lua54Opcode.GreaterThanImmediate or Lua54Opcode.GreaterOrEqualImmediate => a,
                Lua54Opcode.Call => instruction.B == 0 || instruction.C == 0
                    ? _function.RegisterCount - 1
                    : checked(a + Math.Max(instruction.B - 1, instruction.C - 2)),
                Lua54Opcode.TailCall => instruction.B == 0
                    ? _function.RegisterCount - 1
                    : checked(a + instruction.B - 1),
                Lua54Opcode.Return => instruction.B == 0
                    ? _function.RegisterCount - 1
                    : checked(a + Math.Max(0, instruction.B - 2)),
                Lua54Opcode.NumericForLoop or Lua54Opcode.NumericForPrepare => checked(a + 3),
                Lua54Opcode.GenericForPrepare or Lua54Opcode.GenericForLoop => checked(a + 3),
                Lua54Opcode.GenericForCall => checked(a + 3 + instruction.C),
                Lua54Opcode.SetList => instruction.B == 0
                    ? _function.RegisterCount - 1
                    : checked(a + instruction.B),
                Lua54Opcode.VarArg => instruction.C == 0
                    ? _function.RegisterCount - 1
                    : checked(a + Math.Max(0, instruction.C - 2)),
                Lua54Opcode.VarArgPrepare => Math.Max(-1, a - 1),
                Lua54Opcode.GetTableUpvalue or Lua54Opcode.SetTableUpvalue or Lua54Opcode.Self =>
                    Math.Max(a, instruction.B),
                Lua54Opcode.Jump or Lua54Opcode.ReturnZero or Lua54Opcode.ExtraArgument => -1,
                _ => throw new InvalidDataException(
                    $"Cannot determine register usage for Lua 5.4 opcode {instruction.Opcode}."),
            };
        }

        private static bool TryGetRegister(PreparedOperand operand, out int register)
        {
            register = operand.Register;
            return !operand.IsConstant;
        }

        private static PreparedOperand ResolvePreparedOperand(
            int register,
            IReadOnlyList<PreparedRegister> preparations)
        {
            for (var index = preparations.Count - 1; index >= 0; index--)
            {
                var preparation = preparations[index];
                if (preparation.Destination != register)
                {
                    continue;
                }

                if (preparation.IsConstant)
                {
                    return PreparedOperand.FromConstant(
                        preparation.Destination,
                        preparation.Constant);
                }

                register = preparation.SourceRegister;
            }

            return PreparedOperand.FromRegister(register);
        }

        private bool IsTemporaryDiscardedAfter(int programCounter, int register)
        {
            if (_capturedRegisters.Contains(register))
            {
                return false;
            }

            if (programCounter + 1 >= _function.Instructions.Length)
            {
                return true;
            }

            var next = _function.Instructions[programCounter + 1];
            return next.Opcode == LuaIrOpcode.SetTop && next.A <= register &&
                   !_canonicalJumpTargets.Contains(programCounter + 1);
        }

        private static bool IsCommutative(LuaIrBinaryOperator op) => op is
            LuaIrBinaryOperator.Add or LuaIrBinaryOperator.Multiply or
            LuaIrBinaryOperator.BitwiseAnd or LuaIrBinaryOperator.BitwiseOr or
            LuaIrBinaryOperator.BitwiseXor;

        private static (Lua54Opcode Opcode, int EventCode) GetArithmeticOpcode(
            LuaIrBinaryOperator op) => op switch
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
                _ => throw new InvalidDataException("Unknown canonical arithmetic operator."),
            };

        private static Lua54Opcode? GetConstantArithmeticOpcode(LuaIrBinaryOperator op) => op switch
        {
            LuaIrBinaryOperator.Add => Lua54Opcode.AddConstant,
            LuaIrBinaryOperator.Subtract => Lua54Opcode.SubtractConstant,
            LuaIrBinaryOperator.Multiply => Lua54Opcode.MultiplyConstant,
            LuaIrBinaryOperator.Modulo => Lua54Opcode.ModuloConstant,
            LuaIrBinaryOperator.Power => Lua54Opcode.PowerConstant,
            LuaIrBinaryOperator.Divide => Lua54Opcode.DivideConstant,
            LuaIrBinaryOperator.FloorDivide => Lua54Opcode.FloorDivideConstant,
            LuaIrBinaryOperator.BitwiseAnd => Lua54Opcode.BitwiseAndConstant,
            LuaIrBinaryOperator.BitwiseOr => Lua54Opcode.BitwiseOrConstant,
            LuaIrBinaryOperator.BitwiseXor => Lua54Opcode.BitwiseXorConstant,
            _ => null,
        };

        private bool TryEmitFusedComparisonBranch(int programCounter)
        {
            if (programCounter + 1 >= _function.Instructions.Length ||
                HasCanonicalControlOrDebugBoundary(programCounter + 1))
            {
                return false;
            }

            var comparison = _function.Instructions[programCounter];
            var branch = _function.Instructions[programCounter + 1];
            if (comparison.Opcode != LuaIrOpcode.Binary ||
                !IsComparison((LuaIrBinaryOperator)comparison.D) ||
                branch.Opcode is not (LuaIrOpcode.JumpIfFalse or LuaIrOpcode.JumpIfTrue) ||
                branch.A != comparison.A ||
                _liveAfter[programCounter + 1][comparison.A] ||
                !IsOptimizationTemporary(comparison.A, programCounter))
            {
                return false;
            }

            var op = (LuaIrBinaryOperator)comparison.D;
            var left = comparison.B;
            var right = comparison.C;
            var acceptedWhenTrue = true;
            var opcode = op switch
            {
                LuaIrBinaryOperator.Equal => Lua54Opcode.Equal,
                LuaIrBinaryOperator.NotEqual => Lua54Opcode.Equal,
                LuaIrBinaryOperator.LessThan => Lua54Opcode.LessThan,
                LuaIrBinaryOperator.LessThanOrEqual => Lua54Opcode.LessOrEqual,
                LuaIrBinaryOperator.GreaterThan => Lua54Opcode.LessThan,
                LuaIrBinaryOperator.GreaterThanOrEqual => Lua54Opcode.LessOrEqual,
                _ => throw new InvalidOperationException("The fused operation is not a comparison."),
            };
            if (op == LuaIrBinaryOperator.NotEqual)
            {
                acceptedWhenTrue = false;
            }
            else if (op is LuaIrBinaryOperator.GreaterThan or LuaIrBinaryOperator.GreaterThanOrEqual)
            {
                (left, right) = (right, left);
            }

            var branchWhenTrue = branch.Opcode == LuaIrOpcode.JumpIfTrue;
            Emit(
                Lua54Instruction.CreateAbc(
                    opcode,
                    left,
                    right,
                    0,
                    branchWhenTrue ? acceptedWhenTrue : !acceptedWhenTrue),
                comparison.SourceLine);
            EmitJump(branch.B, branch.SourceLine);
            return true;
        }

        private int GetActiveLocalRegisterCount(int programCounter) => Math.Max(
            _function.ParameterCount,
            _function.LocalVariables.Count(local =>
                local.StartProgramCounter <= programCounter &&
                programCounter < local.EndProgramCounter));

        private bool IsOptimizationTemporary(int register, int programCounter) =>
            register >= GetActiveLocalRegisterCount(programCounter) &&
            !_capturedRegisters.Contains(register);

        private static bool[][] AnalyzeRegisterLiveness(
            ModuleConverter owner,
            LuaIrFunction function,
            IReadOnlySet<int> capturedRegisters)
        {
            var instructionCount = function.Instructions.Length;
            var liveBefore = CreateLivenessMatrix(instructionCount, function.RegisterCount);
            var liveAfter = CreateLivenessMatrix(instructionCount, function.RegisterCount);
            var changed = true;
            while (changed)
            {
                changed = false;
                for (var pc = instructionCount - 1; pc >= 0; pc--)
                {
                    var nextAfter = new bool[function.RegisterCount];
                    foreach (var successor in LivenessSuccessors(function, pc))
                    {
                        UnionLiveness(nextAfter, liveBefore[successor]);
                    }

                    foreach (var register in capturedRegisters)
                    {
                        nextAfter[register] = true;
                    }

                    var nextBefore = (bool[])nextAfter.Clone();
                    ApplyLivenessDefinitions(function, function.Instructions[pc], nextBefore);
                    ApplyLivenessUses(owner, function, function.Instructions[pc], nextBefore);
                    if (!nextAfter.AsSpan().SequenceEqual(liveAfter[pc]) ||
                        !nextBefore.AsSpan().SequenceEqual(liveBefore[pc]))
                    {
                        liveAfter[pc] = nextAfter;
                        liveBefore[pc] = nextBefore;
                        changed = true;
                    }
                }
            }

            return liveAfter;
        }

        private static bool[][] CreateLivenessMatrix(int rows, int columns)
        {
            var matrix = new bool[rows][];
            for (var row = 0; row < rows; row++)
            {
                matrix[row] = new bool[columns];
            }

            return matrix;
        }

        private static IEnumerable<int> LivenessSuccessors(LuaIrFunction function, int pc)
        {
            var instruction = function.Instructions[pc];
            switch (instruction.Opcode)
            {
                case LuaIrOpcode.Jump:
                    yield return instruction.B;
                    break;
                case LuaIrOpcode.JumpIfFalse:
                case LuaIrOpcode.JumpIfTrue:
                case LuaIrOpcode.NumericForPrepare:
                case LuaIrOpcode.NumericForLoop:
                    yield return instruction.B;
                    if (pc + 1 < function.Instructions.Length)
                    {
                        yield return pc + 1;
                    }

                    break;
                case LuaIrOpcode.Return:
                case LuaIrOpcode.TailCall:
                    break;
                default:
                    if (pc + 1 < function.Instructions.Length)
                    {
                        yield return pc + 1;
                    }

                    break;
            }
        }

        private static void ApplyLivenessDefinitions(
            LuaIrFunction function,
            LuaIrInstruction instruction,
            bool[] live)
        {
            switch (instruction.Opcode)
            {
                case LuaIrOpcode.LoadConstant:
                case LuaIrOpcode.Move:
                case LuaIrOpcode.GetUpvalue:
                case LuaIrOpcode.NewTable:
                case LuaIrOpcode.GetTable:
                case LuaIrOpcode.Closure:
                case LuaIrOpcode.Unary:
                case LuaIrOpcode.Binary:
                    KillLiveness(live, instruction.A, 1);
                    break;
                case LuaIrOpcode.LoadNil:
                    KillLiveness(live, instruction.A, instruction.B);
                    break;
                case LuaIrOpcode.SetTop:
                    KillLiveness(live, instruction.A, function.RegisterCount - instruction.A);
                    break;
                case LuaIrOpcode.JumpIfFalse:
                case LuaIrOpcode.JumpIfTrue:
                    if (instruction.D != 0)
                    {
                        KillLiveness(live, instruction.C, function.RegisterCount - instruction.C);
                    }

                    break;
                case LuaIrOpcode.VarArg:
                    KillLiveness(
                        live,
                        instruction.A,
                        instruction.B < 0 ? function.RegisterCount - instruction.A : instruction.B);
                    break;
                case LuaIrOpcode.Call:
                    KillLiveness(
                        live,
                        instruction.A,
                        instruction.C < 0 ? function.RegisterCount - instruction.A : instruction.C);
                    break;
                case LuaIrOpcode.NumericForPrepare:
                case LuaIrOpcode.NumericForLoop:
                    KillLiveness(live, instruction.A, Math.Min(4, function.RegisterCount - instruction.A));
                    break;
            }
        }

        private static void ApplyLivenessUses(
            ModuleConverter owner,
            LuaIrFunction function,
            LuaIrInstruction instruction,
            bool[] live)
        {
            switch (instruction.Opcode)
            {
                case LuaIrOpcode.Move:
                case LuaIrOpcode.SetUpvalue:
                    UseLiveness(live, instruction.B, 1);
                    break;
                case LuaIrOpcode.GetTable:
                    UseLiveness(live, instruction.B, 1);
                    UseLiveness(live, instruction.C, 1);
                    break;
                case LuaIrOpcode.SetTable:
                    UseLiveness(live, instruction.A, 1);
                    UseLiveness(live, instruction.B, 1);
                    UseLiveness(live, instruction.C, 1);
                    break;
                case LuaIrOpcode.SetList:
                    UseLiveness(live, instruction.A, 1);
                    UseLiveness(
                        live,
                        instruction.C,
                        instruction.D < 0
                            ? function.RegisterCount - instruction.C
                            : instruction.D);
                    break;
                case LuaIrOpcode.Closure:
                    foreach (var upvalue in owner.GetFunction(instruction.B).Upvalues)
                    {
                        if (upvalue.SourceKind == LuaIrUpvalueSourceKind.Register)
                        {
                            UseLiveness(live, upvalue.SourceIndex, 1);
                        }
                    }

                    break;
                case LuaIrOpcode.Unary:
                    UseLiveness(live, instruction.B, 1);
                    break;
                case LuaIrOpcode.Binary:
                    UseLiveness(live, instruction.B, 1);
                    UseLiveness(live, instruction.C, 1);
                    break;
                case LuaIrOpcode.Jump:
                    if (instruction.C >= 0)
                    {
                        UseLiveness(live, instruction.C, function.RegisterCount - instruction.C);
                    }

                    break;
                case LuaIrOpcode.JumpIfFalse:
                case LuaIrOpcode.JumpIfTrue:
                case LuaIrOpcode.MarkToBeClosed:
                    UseLiveness(live, instruction.A, 1);
                    break;
                case LuaIrOpcode.Call:
                case LuaIrOpcode.TailCall:
                    UseLiveness(
                        live,
                        instruction.A,
                        instruction.B < 0
                            ? function.RegisterCount - instruction.A
                            : instruction.B + 1);
                    break;
                case LuaIrOpcode.Return:
                    UseLiveness(
                        live,
                        instruction.A,
                        instruction.B < 0
                            ? function.RegisterCount - instruction.A
                            : instruction.B);
                    break;
                case LuaIrOpcode.Close:
                    UseLiveness(live, instruction.A, function.RegisterCount - instruction.A);
                    break;
                case LuaIrOpcode.NumericForPrepare:
                case LuaIrOpcode.NumericForLoop:
                    UseLiveness(live, instruction.A, Math.Min(4, function.RegisterCount - instruction.A));
                    break;
            }
        }

        private static void KillLiveness(bool[] live, int start, int count) =>
            Array.Clear(live, start, count);

        private static void UseLiveness(bool[] live, int start, int count)
        {
            for (var index = 0; index < count; index++)
            {
                live[start + index] = true;
            }
        }

        private static void UnionLiveness(bool[] target, bool[] source)
        {
            for (var index = 0; index < target.Length; index++)
            {
                target[index] |= source[index];
            }
        }

        private int GetOrAddConstant(LuaIrConstant value)
        {
            var converted = ConvertConstant(value);
            for (var index = 0; index < _constants.Count; index++)
            {
                if (ConstantsEqual(_constants[index], converted))
                {
                    return index;
                }
            }

            _constants.Add(converted);
            return _constants.Count - 1;
        }

        private static bool ConstantsEqual(Lua54Constant left, Lua54Constant right) =>
            left.Kind == right.Kind && left.IntegerValue == right.IntegerValue &&
            BitConverter.DoubleToInt64Bits(left.FloatValue) ==
            BitConverter.DoubleToInt64Bits(right.FloatValue) &&
            ((left.StringValue is null && right.StringValue is null) ||
             (left.StringValue is not null && right.StringValue is not null &&
              left.StringValue.AsSpan().SequenceEqual(right.StringValue.AsSpan())));

        private static bool CanEncodeSignedBx(long value) =>
            value >= -Lua54Instruction.SignedBxOffset &&
            value <= Lua54Instruction.MaximumBx - Lua54Instruction.SignedBxOffset;

        private static bool TryGetLoadFloatImmediate(double value, out int immediate)
        {
            immediate = 0;
            if (!double.IsFinite(value) || value != Math.Truncate(value) ||
                value == 0 && BitConverter.DoubleToInt64Bits(value) < 0 ||
                value < -Lua54Instruction.SignedBxOffset ||
                value > Lua54Instruction.MaximumBx - Lua54Instruction.SignedBxOffset)
            {
                return false;
            }

            immediate = checked((int)value);
            return true;
        }

        private static HashSet<int> CollectCanonicalJumpTargets(LuaIrFunction function)
        {
            var targets = new HashSet<int>();
            foreach (var instruction in function.Instructions)
            {
                switch (instruction.Opcode)
                {
                    case LuaIrOpcode.Jump:
                    case LuaIrOpcode.JumpIfFalse:
                    case LuaIrOpcode.JumpIfTrue:
                    case LuaIrOpcode.NumericForPrepare:
                    case LuaIrOpcode.NumericForLoop:
                        targets.Add(instruction.B);
                        break;
                }
            }

            return targets;
        }

        private static HashSet<int> CollectCapturedRegisters(
            ModuleConverter owner,
            IEnumerable<int> children)
        {
            var registers = new HashSet<int>();
            foreach (var child in children.Select(owner.GetFunction))
            {
                foreach (var upvalue in child.Upvalues)
                {
                    if (upvalue.SourceKind == LuaIrUpvalueSourceKind.Register)
                    {
                        registers.Add(upvalue.SourceIndex);
                    }
                }
            }

            return registers;
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

        private static Lua54String? ConvertUpvalueName(LuaIrUpvalue upvalue)
        {
            if (IsSyntheticUpvalueName(upvalue))
            {
                return null;
            }

            return new Lua54String(upvalue.DebugName.IsEmpty
                ? Encoding.UTF8.GetBytes(upvalue.Name)
                : upvalue.DebugName.AsSpan());
        }

        private static bool IsSyntheticUpvalueName(LuaIrUpvalue upvalue) =>
            upvalue.DebugName.IsEmpty &&
            upvalue.Name.StartsWith("(upvalue ", StringComparison.Ordinal) &&
            upvalue.Name.EndsWith(')');

        private readonly record struct EmittedInstruction(
            Lua54Instruction Instruction,
            int SourceLine);

        private readonly record struct PreparedRegister(
            int Destination,
            int SourceRegister,
            LuaIrConstant Constant,
            bool IsConstant)
        {
            public static PreparedRegister FromRegister(int destination, int sourceRegister) =>
                new(destination, sourceRegister, default, false);

            public static PreparedRegister FromConstant(int destination, LuaIrConstant constant) =>
                new(destination, 0, constant, true);
        }

        private readonly record struct PreparedOperand(
            int Register,
            int PreparationRegister,
            LuaIrConstant Constant,
            bool IsConstant)
        {
            public static PreparedOperand FromRegister(int register) =>
                new(register, register, default, false);

            public static PreparedOperand FromConstant(
                int preparationRegister,
                LuaIrConstant constant) =>
                new(0, preparationRegister, constant, true);
        }

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
