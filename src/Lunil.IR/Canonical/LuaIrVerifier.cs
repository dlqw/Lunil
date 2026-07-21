using System.Collections.Immutable;
using Lunil.Core;

namespace Lunil.IR.Canonical;

public sealed record LuaIrVerificationError(int FunctionId, int ProgramCounter, string Message);

public sealed record LuaIrVerifierOptions
{
    public static LuaIrVerifierOptions Default { get; } = new();

    public int MaximumFunctions { get; init; } = 100_000;

    public int MaximumInstructionsPerFunction { get; init; } = 10_000_000;

    public int MaximumRegistersPerFunction { get; init; } = 1_000_000;
}

/// <summary>Validates canonical IR independently of its producer and execution backend.</summary>
public static class LuaIrVerifier
{
    public static ImmutableArray<LuaIrVerificationError> Verify(
        LuaIrModule module,
        LuaIrVerifierOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(module);
        options ??= LuaIrVerifierOptions.Default;
        var errors = ImmutableArray.CreateBuilder<LuaIrVerificationError>();

        if (module.Functions.IsDefault)
        {
            errors.Add(new(-1, -1, "The function array is not initialized."));
            return errors.ToImmutable();
        }

        if (module.FormatVersion != LuaIrModule.CurrentFormatVersion)
        {
            errors.Add(new(-1, -1, $"Unsupported canonical IR version {module.FormatVersion}."));
        }

        if (!LuaLanguageVersions.IsKnown(module.LanguageVersion))
        {
            errors.Add(new(
                -1,
                -1,
                $"Unsupported Lua language version identity 0x{(byte)module.LanguageVersion:X2}."));
        }

        if (module.Functions.Length > options.MaximumFunctions)
        {
            errors.Add(new(-1, -1, "The module exceeds the configured function budget."));
        }

        if ((uint)module.MainFunctionId >= (uint)module.Functions.Length)
        {
            errors.Add(new(-1, -1, "The main function index is outside the module."));
        }

        for (var index = 0; index < module.Functions.Length; index++)
        {
            var function = module.Functions[index];
            if (function is null)
            {
                errors.Add(new(index, -1, "The function entry is null."));
                continue;
            }

            if (function.Id != index)
            {
                errors.Add(new(function.Id, -1, "Function identifiers must be dense and ordered."));
            }

            VerifyFunction(module, function, options, errors);
        }

        return errors.ToImmutable();
    }

    private static void VerifyFunction(
        LuaIrModule module,
        LuaIrFunction function,
        LuaIrVerifierOptions options,
        ImmutableArray<LuaIrVerificationError>.Builder errors)
    {
        if (function.ParameterCount < 0 ||
            function.RegisterCount < function.ParameterCount ||
            function.RegisterCount > options.MaximumRegistersPerFunction)
        {
            errors.Add(new(function.Id, -1, "The function register count is invalid."));
        }

        if (function.Instructions.IsDefaultOrEmpty ||
            function.Instructions.Length > options.MaximumInstructionsPerFunction)
        {
            errors.Add(new(function.Id, -1, "The function instruction count is invalid."));
            return;
        }

        if (function.ParentFunctionId >= function.Id || function.ParentFunctionId < -1)
        {
            errors.Add(new(function.Id, -1, "The parent function identifier is invalid."));
        }

        if (function.Constants.IsDefault)
        {
            errors.Add(new(function.Id, -1, "The constant array is not initialized."));
        }

        if (function.SourceName.IsDefault || function.LocalVariables.IsDefault)
        {
            errors.Add(new(function.Id, -1, "The function debug metadata is not initialized."));
        }
        else
        {
            if (function.LineDefined < 0 || function.LastLineDefined < function.LineDefined)
            {
                errors.Add(new(function.Id, -1, "The function source line range is invalid."));
            }

            foreach (var local in function.LocalVariables)
            {
                if (local.Name.IsDefault || local.StartProgramCounter < 0 ||
                    local.EndProgramCounter < local.StartProgramCounter ||
                    local.EndProgramCounter > function.Instructions.Length)
                {
                    errors.Add(new(function.Id, -1, "A debug-local program-counter range is invalid."));
                }
            }
        }

        if (function.Upvalues.IsDefault)
        {
            errors.Add(new(function.Id, -1, "The upvalue array is not initialized."));
        }
        else
        {
            VerifyUpvalues(module, function, errors);
        }

        for (var pc = 0; pc < function.Instructions.Length; pc++)
        {
            if (function.Instructions[pc].SourceLine < 0 ||
                function.Instructions[pc].LogicalProgramCounter < -1)
            {
                errors.Add(new(function.Id, pc, "Instruction provenance is invalid."));
            }

            VerifyInstruction(module, function, pc, function.Instructions[pc], errors);
        }

        var expectedBlocks = LuaIrControlFlow.Build(function.Instructions);
        if (!BlocksMatch(expectedBlocks, function.BasicBlocks))
        {
            errors.Add(new(function.Id, -1, "Basic blocks do not match the instruction control flow."));
        }
    }

    private static void VerifyUpvalues(
        LuaIrModule module,
        LuaIrFunction function,
        ImmutableArray<LuaIrVerificationError>.Builder errors)
    {
        for (var index = 0; index < function.Upvalues.Length; index++)
        {
            var upvalue = function.Upvalues[index];
            if (upvalue.Kind > 3)
            {
                errors.Add(new(function.Id, -1, $"Upvalue '{upvalue.Name}' has an invalid kind."));
            }

            if (function.ParentFunctionId < 0)
            {
                // The first root upvalue is the language environment. Additional root
                // upvalues have no lexical parent; preserve their binary descriptor kind so
                // round-tripping multi-upvalue main chunks does not misidentify them as _ENV.
                if (index == 0 &&
                    upvalue.SourceKind != LuaIrUpvalueSourceKind.Environment)
                {
                    errors.Add(new(function.Id, -1, "The first root upvalue must be supplied by the environment."));
                }

                continue;
            }

            if ((uint)function.ParentFunctionId >= (uint)module.Functions.Length ||
                module.Functions[function.ParentFunctionId] is not { } parent)
            {
                errors.Add(new(function.Id, -1, $"Upvalue '{upvalue.Name}' has no valid parent function."));
                continue;
            }

            var valid = upvalue.SourceKind switch
            {
                LuaIrUpvalueSourceKind.Register =>
                    (uint)upvalue.SourceIndex < (uint)parent.RegisterCount,
                LuaIrUpvalueSourceKind.Upvalue =>
                    (uint)upvalue.SourceIndex < (uint)parent.Upvalues.Length,
                _ => false,
            };
            if (!valid)
            {
                errors.Add(new(function.Id, -1, $"Upvalue '{upvalue.Name}' has an invalid source."));
            }
        }
    }

    private static void VerifyInstruction(
        LuaIrModule module,
        LuaIrFunction function,
        int pc,
        LuaIrInstruction instruction,
        ImmutableArray<LuaIrVerificationError>.Builder errors)
    {
        bool Register(int value) => (uint)value < (uint)function.RegisterCount;
        bool RegisterRange(int start, int count) =>
            count >= 0 && Register(start) && (long)start + count <= function.RegisterCount;
        void Error(string message) => errors.Add(new(function.Id, pc, message));

        var valid = instruction.Opcode switch
        {
            LuaIrOpcode.LoadConstant => Register(instruction.A) &&
                (uint)instruction.B < (uint)function.Constants.Length,
            LuaIrOpcode.LoadNil => RegisterRange(instruction.A, instruction.B),
            LuaIrOpcode.Move => Register(instruction.A) && Register(instruction.B),
            LuaIrOpcode.SetTop => instruction.A >= 0 && instruction.A <= function.RegisterCount,
            LuaIrOpcode.GetUpvalue => Register(instruction.A) &&
                (uint)instruction.B < (uint)function.Upvalues.Length,
            LuaIrOpcode.SetUpvalue => (uint)instruction.A < (uint)function.Upvalues.Length &&
                Register(instruction.B),
            LuaIrOpcode.NewTable => Register(instruction.A) &&
                instruction.B is >= 0 and <= 31 && instruction.C >= 0,
            LuaIrOpcode.GetTable => Register(instruction.A) && Register(instruction.B) &&
                Register(instruction.C),
            LuaIrOpcode.SetTable => Register(instruction.A) && Register(instruction.B) &&
                Register(instruction.C),
            LuaIrOpcode.SetList => Register(instruction.A) && instruction.B >= 1 &&
                Register(instruction.C) && (instruction.D == -1 || RegisterRange(instruction.C, instruction.D)),
            LuaIrOpcode.Closure => Register(instruction.A) &&
                (uint)instruction.B < (uint)module.Functions.Length &&
                module.Functions[instruction.B] is { } nested &&
                nested.ParentFunctionId == function.Id,
            LuaIrOpcode.VarArg => Register(instruction.A) &&
                (instruction.B == -1 || RegisterRange(instruction.A, instruction.B)) &&
                (instruction.C == 0 || module.LanguageVersion == LuaLanguageVersion.Lua55 &&
                    Register(instruction.C - 1)),
            LuaIrOpcode.CreateVarArgTable =>
                module.LanguageVersion == LuaLanguageVersion.Lua55 &&
                Register(instruction.A) && function.IsVarArg,
            LuaIrOpcode.GetVarArg => Register(instruction.A) && Register(instruction.B) &&
                module.LanguageVersion == LuaLanguageVersion.Lua55 && function.IsVarArg,
            LuaIrOpcode.ErrorIfNotNil => Register(instruction.A) &&
                module.LanguageVersion == LuaLanguageVersion.Lua55 &&
                (instruction.B == -1 ||
                    (uint)instruction.B < (uint)function.Constants.Length &&
                    function.Constants[instruction.B].Kind == LuaIrConstantKind.String),
            LuaIrOpcode.Unary => Register(instruction.A) && Register(instruction.B) &&
                Enum.IsDefined((LuaIrUnaryOperator)instruction.C),
            LuaIrOpcode.Binary => Register(instruction.A) && Register(instruction.B) &&
                Register(instruction.C) && Enum.IsDefined((LuaIrBinaryOperator)instruction.D),
            LuaIrOpcode.Jump => Target(instruction.B) && CloseBase(instruction.C),
            LuaIrOpcode.JumpIfFalse or LuaIrOpcode.JumpIfTrue =>
                Register(instruction.A) && Target(instruction.B) &&
                (instruction.D == 0 && instruction.C == 0 ||
                    instruction.D == 1 && instruction.C >= 0 &&
                    instruction.C <= function.RegisterCount),
            LuaIrOpcode.Call => Register(instruction.A) && Count(instruction.B) && Count(instruction.C) &&
                Enum.IsDefined((LuaIrCallKind)instruction.D) &&
                (instruction.B < 0 || RegisterRange(instruction.A + 1, instruction.B)) &&
                (instruction.C < 0 || instruction.C == 0 || RegisterRange(instruction.A, instruction.C)),
            LuaIrOpcode.TailCall => Register(instruction.A) && Count(instruction.B) &&
                (instruction.B < 0 || RegisterRange(instruction.A + 1, instruction.B)),
            LuaIrOpcode.Return => Register(instruction.A) && Count(instruction.B) &&
                (instruction.B < 0 || instruction.B == 0 || RegisterRange(instruction.A, instruction.B)),
            LuaIrOpcode.Close => Register(instruction.A),
            LuaIrOpcode.MarkToBeClosed => Register(instruction.A),
            LuaIrOpcode.NumericForPrepare or LuaIrOpcode.NumericForLoop =>
                RegisterRange(instruction.A, 4) && Target(instruction.B),
            _ => false,
        };

        if (!valid)
        {
            Error($"Invalid operands for {instruction.Opcode}.");
        }

        bool Target(int value) => (uint)value < (uint)function.Instructions.Length;
        bool Count(int value) => value == -1 || value >= 0;
        bool CloseBase(int value) => value == -1 || Register(value);
    }

    private static bool BlocksMatch(
        ImmutableArray<LuaIrBasicBlock> expected,
        ImmutableArray<LuaIrBasicBlock> actual)
    {
        if (actual.IsDefault || expected.Length != actual.Length)
        {
            return false;
        }

        for (var index = 0; index < expected.Length; index++)
        {
            if (expected[index].Start != actual[index].Start ||
                expected[index].Length != actual[index].Length ||
                actual[index].Successors.IsDefault ||
                !expected[index].Successors.SequenceEqual(actual[index].Successors))
            {
                return false;
            }
        }

        return true;
    }
}
