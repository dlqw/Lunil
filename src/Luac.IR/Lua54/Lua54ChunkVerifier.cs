using System.Collections.Immutable;

namespace Luac.IR.Lua54;

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
        }
    }

    private static void VerifyInstructions(
        Lua54Prototype prototype,
        string path,
        ImmutableArray<Lua54VerificationError>.Builder errors)
    {
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
                case Lua54Opcode.NewTable when next?.Opcode != Lua54Opcode.ExtraArgument:
                    Add(errors, path, "NEWTABLE must be followed by EXTRAARG.", pc);
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
                case Lua54Opcode.GetTableUpvalue when instruction.B >= prototype.Upvalues.Length:
                case Lua54Opcode.SetTableUpvalue when instruction.A >= prototype.Upvalues.Length:
                    Add(errors, path, "Table upvalue index is out of range.", pc);
                    break;
                case Lua54Opcode.Jump:
                    var target = (long)pc + 1 + instruction.SignedJump;
                    if (target < 0 || target >= prototype.Code.Length)
                    {
                        Add(errors, path, $"Jump target {target} is outside the code range.", pc);
                    }

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

    private static void Add(
        ImmutableArray<Lua54VerificationError>.Builder errors,
        string path,
        string message,
        int? programCounter = null) =>
        errors.Add(new Lua54VerificationError(path, message, programCounter));
}
