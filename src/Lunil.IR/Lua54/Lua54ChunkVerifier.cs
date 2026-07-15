using System.Collections.Immutable;

namespace Lunil.IR.Lua54;

/// <summary>
/// Performs structural checks required before a chunk can reach an execution backend.
/// The verifier will be extended alongside the interpreter's executable invariants.
/// </summary>
public static class Lua54ChunkVerifier
{
    public static ImmutableArray<Lua54VerificationError> Verify(Lua54Chunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(chunk.MainPrototype);

        var errors = ImmutableArray.CreateBuilder<Lua54VerificationError>();
        if (chunk.MainUpvalueCount != chunk.MainPrototype.Upvalues.Length)
        {
            errors.Add(new Lua54VerificationError(
                "main",
                $"Header declares {chunk.MainUpvalueCount} upvalues but the main prototype has " +
                $"{chunk.MainPrototype.Upvalues.Length}."));
        }

        VerifyPrototype(chunk.MainPrototype, parent: null, "main", errors);
        return errors.ToImmutable();
    }

    public static void ThrowIfInvalid(Lua54Chunk chunk)
    {
        var errors = Verify(chunk);
        if (!errors.IsEmpty)
        {
            var first = errors[0];
            var location = first.ProgramCounter is int pc ? $" at instruction {pc}" : string.Empty;
            throw new InvalidDataException(
                $"Invalid Lua 5.4 prototype {first.PrototypePath}{location}: {first.Message}");
        }
    }

    private static void VerifyPrototype(
        Lua54Prototype prototype,
        Lua54Prototype? parent,
        string path,
        ImmutableArray<Lua54VerificationError>.Builder errors)
    {
        if (prototype.LineDefined < 0 || prototype.LastLineDefined < prototype.LineDefined)
        {
            Add(errors, path, "The source line range is invalid.");
        }

        if (prototype.MaximumStackSize < 2)
        {
            Add(errors, path, "MaximumStackSize must be at least 2.");
        }

        if (prototype.ParameterCount > prototype.MaximumStackSize)
        {
            Add(errors, path, "ParameterCount exceeds MaximumStackSize.");
        }

        if (prototype.Code.IsDefault || prototype.Constants.IsDefault ||
            prototype.Upvalues.IsDefault || prototype.NestedPrototypes.IsDefault ||
            prototype.LineInfo.IsDefault || prototype.AbsoluteLineInfo.IsDefault ||
            prototype.LocalVariables.IsDefault || prototype.UpvalueNames.IsDefault)
        {
            Add(errors, path, "Prototype collections must not be default ImmutableArray values.");
            return;
        }

        if (prototype.Code.IsEmpty)
        {
            Add(errors, path, "A prototype must contain at least one instruction.");
        }

        if (!prototype.LineInfo.IsEmpty && prototype.LineInfo.Length != prototype.Code.Length)
        {
            Add(errors, path, "LineInfo must be empty or contain one entry per instruction.");
        }

        if (!prototype.UpvalueNames.IsEmpty &&
            prototype.UpvalueNames.Length != prototype.Upvalues.Length)
        {
            Add(errors, path, "UpvalueNames must be empty or match the upvalue count.");
        }

        VerifyConstants(prototype, path, errors);
        VerifyUpvalues(prototype, parent, path, errors);
        VerifyDebugInformation(prototype, path, errors);
        VerifyInstructions(prototype, path, errors);

        for (var index = 0; index < prototype.NestedPrototypes.Length; index++)
        {
            var nested = prototype.NestedPrototypes[index];
            if (nested is null)
            {
                Add(errors, path, $"Nested prototype {index} is null.");
                continue;
            }

            VerifyPrototype(nested, prototype, $"{path}.p[{index}]", errors);
        }
    }

    private static void VerifyConstants(
        Lua54Prototype prototype,
        string path,
        ImmutableArray<Lua54VerificationError>.Builder errors)
    {
        for (var index = 0; index < prototype.Constants.Length; index++)
        {
            var constant = prototype.Constants[index];
            if (!Enum.IsDefined(constant.Kind))
            {
                Add(errors, path, $"Constant {index} has unknown kind {constant.Kind}.");
            }
            else if (constant.Kind is Lua54ConstantKind.ShortString or Lua54ConstantKind.LongString &&
                     constant.StringValue is null)
            {
                Add(errors, path, $"String constant {index} has a null value.");
            }
        }
    }

    private static void VerifyUpvalues(
        Lua54Prototype prototype,
        Lua54Prototype? parent,
        string path,
        ImmutableArray<Lua54VerificationError>.Builder errors)
    {
        for (var index = 0; index < prototype.Upvalues.Length; index++)
        {
            var upvalue = prototype.Upvalues[index];
            if (upvalue.InStack > 1)
            {
                Add(errors, path, $"Upvalue {index} has invalid InStack value {upvalue.InStack}.");
            }

            if (upvalue.Kind > 3)
            {
                Add(errors, path, $"Upvalue {index} has unknown kind {upvalue.Kind}.");
            }

            if (parent is null)
            {
                continue;
            }

            var limit = upvalue.InStack == 1
                ? parent.MaximumStackSize
                : parent.Upvalues.Length;
            if (upvalue.Index >= limit)
            {
                Add(
                    errors,
                    path,
                    $"Upvalue {index} index {upvalue.Index} exceeds its parent source limit {limit}.");
            }
        }
    }

    private static void VerifyDebugInformation(
        Lua54Prototype prototype,
        string path,
        ImmutableArray<Lua54VerificationError>.Builder errors)
    {
        var expectedAbsoluteIndex = 0;
        var currentLine = prototype.LineDefined;
        for (var pc = 0; pc < prototype.LineInfo.Length; pc++)
        {
            if (prototype.LineInfo[pc] == sbyte.MinValue)
            {
                if (expectedAbsoluteIndex >= prototype.AbsoluteLineInfo.Length ||
                    prototype.AbsoluteLineInfo[expectedAbsoluteIndex].ProgramCounter != pc)
                {
                    Add(errors, path, $"LineInfo absolute marker at PC {pc} has no matching entry.");
                }
                else
                {
                    currentLine = prototype.AbsoluteLineInfo[expectedAbsoluteIndex].Line;
                    expectedAbsoluteIndex++;
                }
            }
            else
            {
                var nextLine = (long)currentLine + prototype.LineInfo[pc];
                if (nextLine < 0 || nextLine > int.MaxValue)
                {
                    Add(errors, path, $"LineInfo overflows at PC {pc}.");
                }
                else
                {
                    currentLine = (int)nextLine;
                }
            }
        }

        if (expectedAbsoluteIndex != prototype.AbsoluteLineInfo.Length)
        {
            Add(errors, path, "AbsoluteLineInfo contains entries without LineInfo markers.");
        }

        var previousPc = -1;
        foreach (var line in prototype.AbsoluteLineInfo)
        {
            if (line.ProgramCounter < 0 || line.ProgramCounter >= prototype.Code.Length)
            {
                Add(errors, path, $"Absolute line PC {line.ProgramCounter} is outside the code range.");
            }

            if (line.ProgramCounter <= previousPc)
            {
                Add(errors, path, "Absolute line PCs must be strictly increasing.");
            }

            if (line.Line < 0)
            {
                Add(errors, path, "Absolute line numbers must not be negative.");
            }

            previousPc = line.ProgramCounter;
        }

        foreach (var local in prototype.LocalVariables)
        {
            if (local.StartProgramCounter < 0 ||
                local.EndProgramCounter < local.StartProgramCounter ||
                local.EndProgramCounter > prototype.Code.Length)
            {
                Add(errors, path, "A local variable has an invalid program-counter range.");
            }

            if (local.Name is null)
            {
                Add(errors, path, "A local variable has a null debug name.");
            }
        }
    }

    private static void VerifyInstructions(
        Lua54Prototype prototype,
        string path,
        ImmutableArray<Lua54VerificationError>.Builder errors)
    {
        bool Register(int register) => (uint)register < prototype.MaximumStackSize;
        bool RegisterRange(int start, int count) =>
            count >= 0 && start >= 0 && (long)start + count <= prototype.MaximumStackSize;
        bool Constant(int index) => (uint)index < (uint)prototype.Constants.Length;
        void Require(bool condition, string message, int pc)
        {
            if (!condition)
            {
                Add(errors, path, message, pc);
            }
        }

        for (var pc = 0; pc < prototype.Code.Length; pc++)
        {
            var instruction = prototype.Code[pc];
            if (!Lua54OpcodeInfo.IsDefined(instruction.Opcode))
            {
                Add(errors, path, $"Unknown opcode value {(byte)instruction.Opcode}.", pc);
                continue;
            }

            var opcode = instruction.Opcode;
            Lua54Instruction? next =
                pc + 1 < prototype.Code.Length ? prototype.Code[pc + 1] : null;

            if (Lua54OpcodeInfo.Get(opcode).SetsRegisterA &&
                instruction.A >= prototype.MaximumStackSize)
            {
                Add(errors, path, $"Register A ({instruction.A}) exceeds MaximumStackSize.", pc);
            }

            switch (opcode)
            {
                case Lua54Opcode.Move:
                    Require(Register(instruction.A) && Register(instruction.B),
                        "MOVE register is out of range.", pc);
                    break;
                case Lua54Opcode.LoadInteger:
                case Lua54Opcode.LoadFloat:
                case Lua54Opcode.LoadFalse:
                case Lua54Opcode.LoadFalseAndSkip:
                case Lua54Opcode.LoadTrue:
                    Require(Register(instruction.A), "Destination register is out of range.", pc);
                    if (opcode == Lua54Opcode.LoadFalseAndSkip)
                    {
                        Require(next?.Opcode == Lua54Opcode.LoadTrue,
                            "LFALSESKIP must be followed by LOADTRUE.", pc);
                        Require(pc + 2 < prototype.Code.Length,
                            "LFALSESKIP skips outside the code range.", pc);
                    }

                    break;
                case Lua54Opcode.LoadConstant when instruction.Bx >= prototype.Constants.Length:
                    Add(errors, path, $"Constant index {instruction.Bx} is out of range.", pc);
                    break;
                case Lua54Opcode.LoadConstantExtra:
                    if (next?.Opcode != Lua54Opcode.ExtraArgument)
                    {
                        Add(errors, path, "LOADKX must be followed by EXTRAARG.", pc);
                    }
                    else if (next.Value.Ax >= prototype.Constants.Length)
                    {
                        Add(errors, path, $"Constant index {next.Value.Ax} is out of range.", pc);
                    }

                    break;
                case Lua54Opcode.LoadNil:
                    Require(RegisterRange(instruction.A, instruction.B + 1),
                        "LOADNIL register range is out of bounds.", pc);
                    break;
                case Lua54Opcode.NewTable when next?.Opcode != Lua54Opcode.ExtraArgument:
                    Add(errors, path, "NEWTABLE must be followed by EXTRAARG.", pc);
                    break;
                case Lua54Opcode.NewTable:
                    if (instruction.B > 31)
                    {
                        Add(errors, path, "NEWTABLE hash size exponent exceeds the runtime range.", pc);
                    }

                    if (instruction.K &&
                        (long)instruction.C + (long)next!.Value.Ax * 256 > int.MaxValue)
                    {
                        Add(errors, path, "NEWTABLE array size exceeds the runtime range.", pc);
                    }

                    break;
                case Lua54Opcode.SetList when instruction.K &&
                                               next?.Opcode != Lua54Opcode.ExtraArgument:
                    Add(errors, path, "SETLIST with k set must be followed by EXTRAARG.", pc);
                    break;
                case Lua54Opcode.Closure when instruction.Bx >= prototype.NestedPrototypes.Length:
                    Add(errors, path, $"Nested prototype index {instruction.Bx} is out of range.", pc);
                    break;
                case Lua54Opcode.GetUpvalue when instruction.B >= prototype.Upvalues.Length:
                case Lua54Opcode.SetUpvalue when instruction.B >= prototype.Upvalues.Length:
                    Add(errors, path, $"Upvalue index {instruction.B} is out of range.", pc);
                    break;
                case Lua54Opcode.SetUpvalue:
                    Require(Register(instruction.A), "SETUPVAL source register is out of range.", pc);
                    break;
                case Lua54Opcode.GetTableUpvalue when instruction.B >= prototype.Upvalues.Length:
                case Lua54Opcode.SetTableUpvalue when instruction.A >= prototype.Upvalues.Length:
                    Add(errors, path, "Table upvalue index is out of range.", pc);
                    break;
                case Lua54Opcode.GetTableUpvalue:
                    Require(Register(instruction.A) && Constant(instruction.C),
                        "GETTABUP register or constant index is out of range.", pc);
                    Require(IsStringConstant(prototype, instruction.C),
                        "GETTABUP key must be a string constant.", pc);
                    break;
                case Lua54Opcode.GetTable:
                    Require(Register(instruction.A) && Register(instruction.B) &&
                        Register(instruction.C), "GETTABLE register is out of range.", pc);
                    break;
                case Lua54Opcode.GetInteger:
                    Require(Register(instruction.A) && Register(instruction.B),
                        "GETI register is out of range.", pc);
                    break;
                case Lua54Opcode.GetField:
                    Require(Register(instruction.A) && Register(instruction.B) &&
                        Constant(instruction.C),
                        "GETFIELD register or constant index is out of range.", pc);
                    Require(IsStringConstant(prototype, instruction.C),
                        "GETFIELD key must be a string constant.", pc);
                    break;
                case Lua54Opcode.SetTableUpvalue:
                    Require(Constant(instruction.B) &&
                        (instruction.K ? Constant(instruction.C) : Register(instruction.C)),
                        "SETTABUP operand is out of range.", pc);
                    Require(IsStringConstant(prototype, instruction.B),
                        "SETTABUP key must be a string constant.", pc);
                    break;
                case Lua54Opcode.SetTable:
                    Require(Register(instruction.A) && Register(instruction.B) &&
                        (instruction.K ? Constant(instruction.C) : Register(instruction.C)),
                        "SETTABLE operand is out of range.", pc);
                    break;
                case Lua54Opcode.SetInteger:
                    Require(Register(instruction.A) &&
                        (instruction.K ? Constant(instruction.C) : Register(instruction.C)),
                        "SETI operand is out of range.", pc);
                    break;
                case Lua54Opcode.SetField:
                    Require(Register(instruction.A) && Constant(instruction.B) &&
                        (instruction.K ? Constant(instruction.C) : Register(instruction.C)),
                        "SETFIELD operand is out of range.", pc);
                    Require(IsStringConstant(prototype, instruction.B),
                        "SETFIELD key must be a string constant.", pc);
                    break;
                case Lua54Opcode.Self:
                    Require(RegisterRange(instruction.A, 2) && Register(instruction.B) &&
                        (instruction.K ? Constant(instruction.C) : Register(instruction.C)),
                        "SELF operand is out of range.", pc);
                    break;
                case Lua54Opcode.AddImmediate:
                case Lua54Opcode.ShiftRightImmediate:
                case Lua54Opcode.ShiftLeftImmediate:
                    Require(Register(instruction.A) && Register(instruction.B),
                        "Immediate arithmetic register is out of range.", pc);
                    Require(next?.Opcode == Lua54Opcode.MetamethodBinaryImmediate,
                        $"{opcode} must be followed by MMBINI.", pc);
                    break;
                case Lua54Opcode.AddConstant:
                case Lua54Opcode.SubtractConstant:
                case Lua54Opcode.MultiplyConstant:
                case Lua54Opcode.ModuloConstant:
                case Lua54Opcode.PowerConstant:
                case Lua54Opcode.DivideConstant:
                case Lua54Opcode.FloorDivideConstant:
                case Lua54Opcode.BitwiseAndConstant:
                case Lua54Opcode.BitwiseOrConstant:
                case Lua54Opcode.BitwiseXorConstant:
                    Require(Register(instruction.A) && Register(instruction.B) &&
                        Constant(instruction.C),
                        "Constant arithmetic operand is out of range.", pc);
                    Require(next?.Opcode == Lua54Opcode.MetamethodBinaryConstant,
                        $"{opcode} must be followed by MMBINK.", pc);
                    Require(IsNumberConstant(prototype, instruction.C),
                        "Arithmetic constant must be numeric.", pc);
                    break;
                case Lua54Opcode.Add:
                case Lua54Opcode.Subtract:
                case Lua54Opcode.Multiply:
                case Lua54Opcode.Modulo:
                case Lua54Opcode.Power:
                case Lua54Opcode.Divide:
                case Lua54Opcode.FloorDivide:
                case Lua54Opcode.BitwiseAnd:
                case Lua54Opcode.BitwiseOr:
                case Lua54Opcode.BitwiseXor:
                case Lua54Opcode.ShiftLeft:
                case Lua54Opcode.ShiftRight:
                    Require(Register(instruction.A) && Register(instruction.B) &&
                        Register(instruction.C), "Arithmetic register is out of range.", pc);
                    Require(next?.Opcode == Lua54Opcode.MetamethodBinary,
                        $"{opcode} must be followed by MMBIN.", pc);
                    break;
                case Lua54Opcode.MetamethodBinary:
                    Require(Register(instruction.A) && Register(instruction.B) &&
                        IsRegisterArithmetic(PreviousOpcode(prototype, pc)),
                        "MMBIN is not associated with register arithmetic.", pc);
                    Require(MetamethodOperandsMatch(prototype, pc),
                        "MMBIN operands or event do not match the preceding arithmetic.", pc);
                    break;
                case Lua54Opcode.MetamethodBinaryImmediate:
                    Require(Register(instruction.A) &&
                        IsImmediateArithmetic(PreviousOpcode(prototype, pc)),
                        "MMBINI is not associated with immediate arithmetic.", pc);
                    Require(MetamethodOperandsMatch(prototype, pc),
                        "MMBINI operands or event do not match the preceding arithmetic.", pc);
                    break;
                case Lua54Opcode.MetamethodBinaryConstant:
                    Require(Register(instruction.A) && Constant(instruction.B) &&
                        IsConstantArithmetic(PreviousOpcode(prototype, pc)),
                        "MMBINK is not associated with constant arithmetic.", pc);
                    Require(MetamethodOperandsMatch(prototype, pc),
                        "MMBINK operands or event do not match the preceding arithmetic.", pc);
                    break;
                case Lua54Opcode.UnaryMinus:
                case Lua54Opcode.BitwiseNot:
                case Lua54Opcode.LogicalNot:
                case Lua54Opcode.Length:
                    Require(Register(instruction.A) && Register(instruction.B),
                        "Unary operand register is out of range.", pc);
                    break;
                case Lua54Opcode.Concatenate:
                    Require(instruction.B >= 2 && RegisterRange(instruction.A, instruction.B),
                        "CONCAT register range is invalid.", pc);
                    break;
                case Lua54Opcode.Close:
                case Lua54Opcode.ToBeClosed:
                    Require(Register(instruction.A), "Close register is out of range.", pc);
                    break;
                case Lua54Opcode.Jump:
                    var target = (long)pc + 1 + instruction.SignedJump;
                    if (target < 0 || target >= prototype.Code.Length)
                    {
                        Add(errors, path, $"Jump target {target} is outside the code range.", pc);
                    }

                    break;
                case Lua54Opcode.Equal:
                case Lua54Opcode.LessThan:
                case Lua54Opcode.LessOrEqual:
                    Require(Register(instruction.A) && Register(instruction.B),
                        "Comparison register is out of range.", pc);
                    break;
                case Lua54Opcode.EqualConstant:
                    Require(Register(instruction.A) && Constant(instruction.B),
                        "EQK operand is out of range.", pc);
                    break;
                case Lua54Opcode.EqualImmediate:
                case Lua54Opcode.LessThanImmediate:
                case Lua54Opcode.LessOrEqualImmediate:
                case Lua54Opcode.GreaterThanImmediate:
                case Lua54Opcode.GreaterOrEqualImmediate:
                case Lua54Opcode.Test:
                    Require(Register(instruction.A), "Test register is out of range.", pc);
                    break;
                case Lua54Opcode.TestSet:
                    Require(Register(instruction.A) && Register(instruction.B),
                        "TESTSET register is out of range.", pc);
                    break;
                case Lua54Opcode.Call:
                    Require(Register(instruction.A) &&
                        (instruction.B == 0 || RegisterRange(instruction.A, instruction.B)) &&
                        (instruction.C == 0 || RegisterRange(instruction.A, instruction.C - 1)),
                        "CALL register window is out of range.", pc);
                    VerifyOpenTopUse(prototype, path, errors, pc, instruction.B == 0);
                    break;
                case Lua54Opcode.TailCall:
                    Require(Register(instruction.A) &&
                        (instruction.B == 0 || RegisterRange(instruction.A, instruction.B)),
                        "TAILCALL register window is out of range.", pc);
                    VerifyOpenTopUse(prototype, path, errors, pc, instruction.B == 0);
                    break;
                case Lua54Opcode.Return:
                    Require(Register(instruction.A) &&
                        (instruction.B == 0 || RegisterRange(instruction.A, instruction.B - 1)),
                        "RETURN register window is out of range.", pc);
                    VerifyOpenTopUse(prototype, path, errors, pc, instruction.B == 0);
                    break;
                case Lua54Opcode.ReturnOne:
                    Require(Register(instruction.A), "RETURN1 register is out of range.", pc);
                    break;
                case Lua54Opcode.NumericForLoop:
                    Require(RegisterRange(instruction.A, 4),
                        "FORLOOP register window is out of range.", pc);
                    VerifyBxTarget(prototype, path, errors, pc, pc + 1 - instruction.Bx);
                    break;
                case Lua54Opcode.NumericForPrepare:
                    Require(RegisterRange(instruction.A, 4),
                        "FORPREP register window is out of range.", pc);
                    VerifyBxTarget(prototype, path, errors, pc, pc + 2L + instruction.Bx);
                    break;
                case Lua54Opcode.GenericForPrepare:
                    Require(RegisterRange(instruction.A, 4),
                        "TFORPREP register window is out of range.", pc);
                    var callTarget = pc + 1L + instruction.Bx;
                    VerifyBxTarget(prototype, path, errors, pc, callTarget);
                    Require(callTarget >= 0 && callTarget < prototype.Code.Length &&
                        prototype.Code[(int)callTarget].Opcode == Lua54Opcode.GenericForCall &&
                        prototype.Code[(int)callTarget].A == instruction.A,
                        "TFORPREP must target its matching TFORCALL.", pc);
                    break;
                case Lua54Opcode.GenericForCall:
                    Require(RegisterRange(instruction.A, 7) &&
                        RegisterRange(instruction.A + 4, instruction.C),
                        "TFORCALL register window is out of range.", pc);
                    Require(next?.Opcode == Lua54Opcode.GenericForLoop && next.Value.A == instruction.A,
                        "TFORCALL must be followed by its matching TFORLOOP.", pc);
                    break;
                case Lua54Opcode.GenericForLoop:
                    Require(RegisterRange(instruction.A, 5),
                        "TFORLOOP register window is out of range.", pc);
                    VerifyBxTarget(prototype, path, errors, pc, pc + 1L - instruction.Bx);
                    break;
                case Lua54Opcode.SetList:
                    Require(Register(instruction.A) &&
                        (instruction.B == 0 || RegisterRange(instruction.A + 1, instruction.B)),
                        "SETLIST register window is out of range.", pc);
                    if (instruction.K && next is { } extra &&
                        (long)instruction.C + (long)extra.Ax * 256 >= int.MaxValue)
                    {
                        Add(errors, path, "SETLIST array index exceeds the runtime range.", pc);
                    }

                    VerifyOpenTopUse(prototype, path, errors, pc, instruction.B == 0);
                    break;
                case Lua54Opcode.VarArg:
                    Require(Register(instruction.A) &&
                        (instruction.C == 0 || RegisterRange(instruction.A, instruction.C - 1)),
                        "VARARG register window is out of range.", pc);
                    break;
                case Lua54Opcode.VarArgPrepare:
                    Require(prototype.VarArgFlags != 0 && instruction.A == prototype.ParameterCount,
                        "VARARGPREP does not match the function parameters.", pc);
                    break;
                case Lua54Opcode.ExtraArgument when !IsExpectedExtraArgument(prototype.Code, pc):
                    Add(errors, path, "EXTRAARG is not associated with a preceding instruction.", pc);
                    break;
            }

            if (Lua54OpcodeInfo.Get(opcode).IsTest && next?.Opcode != Lua54Opcode.Jump)
            {
                Add(errors, path, "Test instructions must be followed by JMP.", pc);
            }
        }

        VerifyControlFlowTargets(prototype, path, errors);
        VerifyCloseState(prototype, path, errors);
        if (prototype.Code[^1].Opcode is not (
                Lua54Opcode.Return or Lua54Opcode.ReturnZero or
                Lua54Opcode.ReturnOne or Lua54Opcode.TailCall))
        {
            Add(errors, path, "A prototype must end with a return or tail call instruction.");
        }
    }

    private static bool IsExpectedExtraArgument(
        ImmutableArray<Lua54Instruction> code,
        int programCounter)
    {
        if (programCounter == 0)
        {
            return false;
        }

        var previous = code[programCounter - 1];
        return previous.Opcode is Lua54Opcode.LoadConstantExtra or Lua54Opcode.NewTable ||
               previous.Opcode == Lua54Opcode.SetList && previous.K;
    }

    private static Lua54Opcode? PreviousOpcode(Lua54Prototype prototype, int programCounter) =>
        programCounter == 0 ? null : prototype.Code[programCounter - 1].Opcode;

    private static bool IsImmediateArithmetic(Lua54Opcode? opcode) => opcode is
        Lua54Opcode.AddImmediate or Lua54Opcode.ShiftRightImmediate or
        Lua54Opcode.ShiftLeftImmediate;

    private static bool IsConstantArithmetic(Lua54Opcode? opcode) => opcode is
        Lua54Opcode.AddConstant or Lua54Opcode.SubtractConstant or
        Lua54Opcode.MultiplyConstant or Lua54Opcode.ModuloConstant or
        Lua54Opcode.PowerConstant or Lua54Opcode.DivideConstant or
        Lua54Opcode.FloorDivideConstant or Lua54Opcode.BitwiseAndConstant or
        Lua54Opcode.BitwiseOrConstant or Lua54Opcode.BitwiseXorConstant;

    private static bool IsRegisterArithmetic(Lua54Opcode? opcode) => opcode is
        Lua54Opcode.Add or Lua54Opcode.Subtract or Lua54Opcode.Multiply or
        Lua54Opcode.Modulo or Lua54Opcode.Power or Lua54Opcode.Divide or
        Lua54Opcode.FloorDivide or Lua54Opcode.BitwiseAnd or Lua54Opcode.BitwiseOr or
        Lua54Opcode.BitwiseXor or Lua54Opcode.ShiftLeft or Lua54Opcode.ShiftRight;

    private static bool IsStringConstant(Lua54Prototype prototype, int index) =>
        (uint)index < (uint)prototype.Constants.Length &&
        prototype.Constants[index].Kind is
            Lua54ConstantKind.ShortString or Lua54ConstantKind.LongString;

    private static bool IsNumberConstant(Lua54Prototype prototype, int index) =>
        (uint)index < (uint)prototype.Constants.Length &&
        prototype.Constants[index].Kind is Lua54ConstantKind.Integer or Lua54ConstantKind.Float;

    private static bool MetamethodOperandsMatch(Lua54Prototype prototype, int programCounter)
    {
        if (programCounter == 0)
        {
            return false;
        }

        var arithmetic = prototype.Code[programCounter - 1];
        var metamethod = prototype.Code[programCounter];
        var expectedEvent = arithmetic.Opcode switch
        {
            Lua54Opcode.AddImmediate or Lua54Opcode.AddConstant or Lua54Opcode.Add => 6,
            Lua54Opcode.SubtractConstant or Lua54Opcode.Subtract => 7,
            Lua54Opcode.MultiplyConstant or Lua54Opcode.Multiply => 8,
            Lua54Opcode.ModuloConstant or Lua54Opcode.Modulo => 9,
            Lua54Opcode.PowerConstant or Lua54Opcode.Power => 10,
            Lua54Opcode.DivideConstant or Lua54Opcode.Divide => 11,
            Lua54Opcode.FloorDivideConstant or Lua54Opcode.FloorDivide => 12,
            Lua54Opcode.BitwiseAndConstant or Lua54Opcode.BitwiseAnd => 13,
            Lua54Opcode.BitwiseOrConstant or Lua54Opcode.BitwiseOr => 14,
            Lua54Opcode.BitwiseXorConstant or Lua54Opcode.BitwiseXor => 15,
            Lua54Opcode.ShiftLeftImmediate or Lua54Opcode.ShiftLeft => 16,
            Lua54Opcode.ShiftRightImmediate when arithmetic.SignedC < 0 => 16,
            Lua54Opcode.ShiftRightImmediate or Lua54Opcode.ShiftRight => 17,
            _ => -1,
        };
        if (metamethod.C != expectedEvent)
        {
            return false;
        }

        return metamethod.Opcode switch
        {
            Lua54Opcode.MetamethodBinary =>
                metamethod.A == arithmetic.B && metamethod.B == arithmetic.C,
            Lua54Opcode.MetamethodBinaryImmediate =>
                metamethod.A == arithmetic.B && ImmediateMetamethodMatches(arithmetic, metamethod),
            Lua54Opcode.MetamethodBinaryConstant =>
                metamethod.A == arithmetic.B && metamethod.B == arithmetic.C,
            _ => false,
        };
    }

    private static bool ImmediateMetamethodMatches(
        Lua54Instruction arithmetic,
        Lua54Instruction metamethod) => arithmetic.Opcode switch
        {
            Lua54Opcode.AddImmediate =>
                metamethod.SignedB == arithmetic.SignedC,
            Lua54Opcode.ShiftRightImmediate when arithmetic.SignedC < 0 =>
                !metamethod.K && metamethod.SignedB == -arithmetic.SignedC,
            Lua54Opcode.ShiftRightImmediate =>
                !metamethod.K && metamethod.SignedB == arithmetic.SignedC,
            Lua54Opcode.ShiftLeftImmediate =>
                metamethod.K && metamethod.SignedB == arithmetic.SignedC,
            _ => false,
        };

    private static void VerifyOpenTopUse(
        Lua54Prototype prototype,
        string path,
        ImmutableArray<Lua54VerificationError>.Builder errors,
        int programCounter,
        bool usesOpenTop)
    {
        if (!usesOpenTop)
        {
            return;
        }

        if (programCounter == 0 ||
            !Lua54OpcodeInfo.Get(prototype.Code[programCounter - 1].Opcode).SetsTop)
        {
            Add(
                errors,
                path,
                "An open stack window must immediately follow an instruction that sets top.",
                programCounter);
        }
    }

    private static void VerifyBxTarget(
        Lua54Prototype prototype,
        string path,
        ImmutableArray<Lua54VerificationError>.Builder errors,
        int programCounter,
        long target)
    {
        if (target < 0 || target >= prototype.Code.Length)
        {
            Add(errors, path, $"Control-flow target {target} is outside the code range.", programCounter);
        }
    }

    private static void VerifyControlFlowTargets(
        Lua54Prototype prototype,
        string path,
        ImmutableArray<Lua54VerificationError>.Builder errors)
    {
        for (var pc = 0; pc < prototype.Code.Length; pc++)
        {
            var instruction = prototype.Code[pc];
            long? target = instruction.Opcode switch
            {
                Lua54Opcode.Jump when !IsTestCompanion(prototype, pc) =>
                    pc + 1L + instruction.SignedJump,
                Lua54Opcode.NumericForLoop or Lua54Opcode.GenericForLoop =>
                    pc + 1L - instruction.Bx,
                Lua54Opcode.NumericForPrepare => pc + 2L + instruction.Bx,
                Lua54Opcode.GenericForPrepare => pc + 1L + instruction.Bx,
                _ => null,
            };
            if (Lua54OpcodeInfo.Get(instruction.Opcode).IsTest && pc + 1 < prototype.Code.Length)
            {
                var jump = prototype.Code[pc + 1];
                target = pc + 2L + jump.SignedJump;
            }

            if (target is not { } actual || actual < 0 || actual >= prototype.Code.Length)
            {
                continue;
            }

            if (IsForbiddenControlFlowEntry(prototype, (int)actual))
            {
                Add(
                    errors,
                    path,
                    $"Control flow enters associated instruction {actual}.",
                    pc);
            }
        }
    }

    private static bool IsTestCompanion(Lua54Prototype prototype, int programCounter) =>
        programCounter > 0 &&
        Lua54OpcodeInfo.Get(prototype.Code[programCounter - 1].Opcode).IsTest;

    private static bool IsForbiddenControlFlowEntry(
        Lua54Prototype prototype,
        int programCounter)
    {
        var opcode = prototype.Code[programCounter].Opcode;
        return opcode is Lua54Opcode.ExtraArgument or Lua54Opcode.MetamethodBinary or
                Lua54Opcode.MetamethodBinaryImmediate or Lua54Opcode.MetamethodBinaryConstant or
                Lua54Opcode.VarArgPrepare ||
            opcode == Lua54Opcode.Jump && IsTestCompanion(prototype, programCounter);
    }

    private static void VerifyCloseState(
        Lua54Prototype prototype,
        string path,
        ImmutableArray<Lua54VerificationError>.Builder errors)
    {
        if (prototype.Code.IsEmpty)
        {
            return;
        }

        var states = new int[]?[prototype.Code.Length];
        states[0] = [];
        var work = new Queue<int>();
        work.Enqueue(0);
        var reportedMerges = new HashSet<int>();
        while (work.TryDequeue(out var pc))
        {
            var instruction = prototype.Code[pc];
            var output = states[pc]!;
            if (instruction.Opcode is Lua54Opcode.ToBeClosed or Lua54Opcode.GenericForPrepare)
            {
                var register = instruction.Opcode == Lua54Opcode.ToBeClosed
                    ? instruction.A
                    : instruction.A + 3;
                if (output.Length != 0 && register <= output[^1])
                {
                    Add(errors, path, "To-be-closed registers must be marked in stack order.", pc);
                }
                else
                {
                    output = [.. output, register];
                }
            }
            else if (instruction.Opcode == Lua54Opcode.Close)
            {
                output = [.. output.Where(register => register < instruction.A)];
            }

            foreach (var successor in GetSuccessors(prototype, pc))
            {
                if ((uint)successor >= (uint)prototype.Code.Length)
                {
                    continue;
                }

                if (states[successor] is null)
                {
                    states[successor] = output;
                    work.Enqueue(successor);
                }
                else if (!states[successor]!.AsSpan().SequenceEqual(output) &&
                         reportedMerges.Add(successor))
                {
                    Add(
                        errors,
                        path,
                        "Control-flow paths disagree about active to-be-closed registers.",
                        successor);
                }
            }
        }
    }

    private static IEnumerable<int> GetSuccessors(Lua54Prototype prototype, int pc)
    {
        var instruction = prototype.Code[pc];
        switch (instruction.Opcode)
        {
            case Lua54Opcode.Return:
            case Lua54Opcode.ReturnZero:
            case Lua54Opcode.ReturnOne:
            case Lua54Opcode.TailCall:
                yield break;
            case Lua54Opcode.LoadFalseAndSkip:
                yield return pc + 2;
                yield break;
            case Lua54Opcode.LoadConstantExtra:
            case Lua54Opcode.NewTable:
                yield return pc + 2;
                yield break;
            case Lua54Opcode.SetList when instruction.K:
                yield return pc + 2;
                yield break;
            case Lua54Opcode.Jump:
                if (!IsTestCompanion(prototype, pc))
                {
                    yield return checked(pc + 1 + instruction.SignedJump);
                }

                yield break;
            case Lua54Opcode.NumericForPrepare:
                yield return pc + 1;
                yield return checked(pc + 2 + instruction.Bx);
                yield break;
            case Lua54Opcode.NumericForLoop:
            case Lua54Opcode.GenericForLoop:
                yield return pc + 1;
                yield return checked(pc + 1 - instruction.Bx);
                yield break;
            case Lua54Opcode.GenericForPrepare:
                yield return checked(pc + 1 + instruction.Bx);
                yield break;
        }

        if (Lua54OpcodeInfo.Get(instruction.Opcode).IsTest)
        {
            if (pc + 1 < prototype.Code.Length)
            {
                yield return checked(pc + 2 + prototype.Code[pc + 1].SignedJump);
                yield return pc + 2;
            }

            yield break;
        }

        if (pc + 1 < prototype.Code.Length)
        {
            yield return pc + 1;
        }
    }

    private static void Add(
        ImmutableArray<Lua54VerificationError>.Builder errors,
        string path,
        string message,
        int? programCounter = null) =>
        errors.Add(new Lua54VerificationError(path, message, programCounter));
}
