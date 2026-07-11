using System.Collections.Immutable;

namespace Luac.IR.Canonical;

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

        if (module.FormatVersion != LuaIrModule.CurrentFormatVersion)
        {
            errors.Add(new(-1, -1, $"Unsupported canonical IR version {module.FormatVersion}."));
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
        if (function.RegisterCount < function.ParameterCount ||
            function.RegisterCount > options.MaximumRegistersPerFunction)
        {
            errors.Add(new(function.Id, -1, "The function register count is invalid."));
        }

        if (function.Instructions.IsEmpty ||
            function.Instructions.Length > options.MaximumInstructionsPerFunction)
        {
            errors.Add(new(function.Id, -1, "The function instruction count is invalid."));
            return;
        }

        if (function.ParentFunctionId >= function.Id || function.ParentFunctionId < -1)
        {
            errors.Add(new(function.Id, -1, "The parent function identifier is invalid."));
        }

        VerifyUpvalues(module, function, errors);

        for (var pc = 0; pc < function.Instructions.Length; pc++)
        {
            VerifyInstruction(module, function, pc, function.Instructions[pc], errors);
        }

        var expectedBlocks = LuaIrControlFlow.Build(function.Instructions);
        if (expectedBlocks.Length != function.BasicBlocks.Length ||
            expectedBlocks.Where((expected, index) =>
                expected.Start != function.BasicBlocks[index].Start ||
                expected.Length != function.BasicBlocks[index].Length ||
                !expected.Successors.SequenceEqual(function.BasicBlocks[index].Successors)).Any())
        {
            errors.Add(new(function.Id, -1, "Basic blocks do not match the instruction control flow."));
        }
    }

    private static void VerifyUpvalues(
        LuaIrModule module,
        LuaIrFunction function,
        ImmutableArray<LuaIrVerificationError>.Builder errors)
    {
        foreach (var upvalue in function.Upvalues)
        {
            if (function.ParentFunctionId < 0)
            {
                if (upvalue.SourceKind != LuaIrUpvalueSourceKind.Environment)
                {
                    errors.Add(new(function.Id, -1, "A root upvalue must be supplied by the environment."));
                }

                continue;
            }

            var parent = module.Functions[function.ParentFunctionId];
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
            LuaIrOpcode.GetUpvalue => Register(instruction.A) &&
                (uint)instruction.B < (uint)function.Upvalues.Length,
            LuaIrOpcode.SetUpvalue => (uint)instruction.A < (uint)function.Upvalues.Length &&
                Register(instruction.B),
            LuaIrOpcode.NewTable => Register(instruction.A),
            LuaIrOpcode.GetTable => Register(instruction.A) && Register(instruction.B) &&
                Register(instruction.C),
            LuaIrOpcode.SetTable => Register(instruction.A) && Register(instruction.B) &&
                Register(instruction.C),
            LuaIrOpcode.SetList => Register(instruction.A) && instruction.B >= 1 &&
                Register(instruction.C) && (instruction.D == -1 || RegisterRange(instruction.C, instruction.D)),
            LuaIrOpcode.Closure => Register(instruction.A) &&
                (uint)instruction.B < (uint)module.Functions.Length &&
                module.Functions[instruction.B].ParentFunctionId == function.Id,
            LuaIrOpcode.VarArg => Register(instruction.A) &&
                (instruction.B == -1 || RegisterRange(instruction.A, instruction.B)),
            LuaIrOpcode.Unary => Register(instruction.A) && Register(instruction.B) &&
                Enum.IsDefined((LuaIrUnaryOperator)instruction.C),
            LuaIrOpcode.Binary => Register(instruction.A) && Register(instruction.B) &&
                Register(instruction.C) && Enum.IsDefined((LuaIrBinaryOperator)instruction.D),
            LuaIrOpcode.Jump => Target(instruction.B) && CloseBase(instruction.C),
            LuaIrOpcode.JumpIfFalse or LuaIrOpcode.JumpIfTrue =>
                Register(instruction.A) && Target(instruction.B),
            LuaIrOpcode.Call => Register(instruction.A) && Count(instruction.B) && Count(instruction.C) &&
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
}
