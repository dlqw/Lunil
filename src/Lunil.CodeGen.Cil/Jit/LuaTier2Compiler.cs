using System.Collections.Immutable;
using Lunil.CodeGen.Cil.Analysis;
using Lunil.CodeGen.Cil.Emission;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.CodeGen.Cil.Jit;

public enum LuaJitOptimizationKind : byte
{
    ConstantFold,
    DeadMove,
    NumericUnary,
    NumericBinary,
    BooleanBranch,
    TableGetPic,
    TableSetPic,
    KnownClosureCall,
    FixedResultWindowReuse,
}

public sealed record LuaJitOptimization(
    int ProgramCounter,
    LuaJitOptimizationKind Kind,
    int CanonicalInstructionCount,
    string Guard);

public sealed record LuaJitDeoptMapEntry(
    int ProgramCounter,
    ImmutableArray<int> MaterializedRegisters,
    bool FrameTopMaterialized,
    bool PendingTransformMaterialized);

public sealed record LuaJitTier2Plan(
    int FunctionId,
    ImmutableArray<LuaJitOptimization> Optimizations,
    ImmutableArray<LuaJitDeoptMapEntry> DeoptMap);

internal sealed record LuaTier2CompilationResult(
    LuaCompiledMethod? Method,
    LuaJitTier2Plan? Plan,
    long EstimatedCodeBytes,
    ImmutableArray<string> Diagnostics)
{
    public bool Succeeded => Method is not null && Plan is not null && Diagnostics.IsEmpty;
}

internal interface ILuaTier2Compiler
{
    LuaTier2CompilationResult Compile(
        LuaIrModule module,
        int functionId,
        LuaJitFunctionProfile profile,
        CancellationToken cancellationToken);
}

internal sealed class ProfileGuidedLuaTier2Compiler : ILuaTier2Compiler
{
    public static ProfileGuidedLuaTier2Compiler Instance { get; } = new();

    public LuaTier2CompilationResult Compile(
        LuaIrModule module,
        int functionId,
        LuaJitFunctionProfile profile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var errors = LuaIrVerifier.Verify(module);
        if (!errors.IsEmpty || functionId < 0 || functionId >= module.Functions.Length)
        {
            return new LuaTier2CompilationResult(
                null,
                null,
                0,
                !errors.IsEmpty
                    ? [.. errors.Select(static error => error.Message)]
                    : ["Function id is outside the verified module."]);
        }

        var function = module.Functions[functionId];
        var liveness = LuaRegisterLiveness.Analyze(module, function);
        var optimized = BuildOptimizations(function, profile, liveness);
        var optimizationDescriptions = optimized.Values
            .OrderBy(static item => item.ProgramCounter)
            .SelectMany(static item => item.ReusesFixedResultWindow
                ? new LuaJitOptimization[]
                {
                    item.ToDescription(),
                    new(
                        item.ProgramCounter,
                        LuaJitOptimizationKind.FixedResultWindowReuse,
                        item.InstructionCount,
                        "callee results remain in the verified caller stack interval"),
                }
                : [item.ToDescription()])
            .ToImmutableArray();
        var deoptMap = optimized.Values
            .Where(static item => item.HasGuard)
            .OrderBy(static item => item.ProgramCounter)
            .Select(item => new LuaJitDeoptMapEntry(
                item.ProgramCounter,
                liveness.LiveBefore[item.ProgramCounter],
                FrameTopMaterialized: true,
                PendingTransformMaterialized: true))
            .ToImmutableArray();
        var plan = new LuaJitTier2Plan(functionId, optimizationDescriptions, deoptMap);
        var program = new Tier2Program(function, optimized);
        return new LuaTier2CompilationResult(
            program.Execute,
            plan,
            checked(function.Instructions.Length * 12L + optimized.Count * 64L),
            []);
    }

    private static ImmutableDictionary<int, OptimizedInstruction> BuildOptimizations(
        LuaIrFunction function,
        LuaJitFunctionProfile profile,
        LuaRegisterLivenessResult liveness)
    {
        var result = ImmutableDictionary.CreateBuilder<int, OptimizedInstruction>();
        AddConstantFolds(function, liveness, result);
        foreach (var site in profile.Sites)
        {
            if (result.ContainsKey(site.ProgramCounter) || result.Values.Any(
                optimization => optimization.Kind == LuaJitOptimizationKind.ConstantFold &&
                    site.ProgramCounter > optimization.ProgramCounter &&
                    site.ProgramCounter < optimization.ProgramCounter +
                        optimization.InstructionCount))
            {
                continue;
            }

            var instruction = function.Instructions[site.ProgramCounter];
            switch (instruction.Opcode)
            {
                case LuaIrOpcode.Move when
                    !liveness.LiveAfter[site.ProgramCounter].Contains(instruction.A):
                    result.Add(
                        site.ProgramCounter,
                        OptimizedInstruction.DeadMove(site.ProgramCounter));
                    break;
                case LuaIrOpcode.Unary when IsStableUnary(instruction, site):
                    result.Add(
                        site.ProgramCounter,
                        OptimizedInstruction.Unary(
                            site.ProgramCounter,
                            site.FirstOperandKinds));
                    break;
                case LuaIrOpcode.Binary when IsStableNumeric(site.FirstOperandKinds) &&
                    IsStableNumeric(site.SecondOperandKinds) &&
                    instruction.D != (int)LuaIrBinaryOperator.Concatenate:
                    result.Add(
                        site.ProgramCounter,
                        OptimizedInstruction.Binary(
                            site.ProgramCounter,
                            site.FirstOperandKinds,
                            site.SecondOperandKinds));
                    break;
                case LuaIrOpcode.JumpIfFalse:
                case LuaIrOpcode.JumpIfTrue:
                    if (site.Samples != 0 &&
                        (site.BranchTaken == site.Samples || site.BranchNotTaken == site.Samples))
                    {
                        result.Add(
                            site.ProgramCounter,
                            OptimizedInstruction.Branch(
                                site.ProgramCounter,
                                site.BranchTaken == site.Samples));
                    }

                    break;
                case LuaIrOpcode.GetTable when IsStableTablePic(site):
                    result.Add(
                        site.ProgramCounter,
                        OptimizedInstruction.Table(
                            site.ProgramCounter,
                            LuaJitOptimizationKind.TableGetPic,
                            site.TableShapes));
                    break;
                case LuaIrOpcode.SetTable when IsStableTablePic(site):
                    result.Add(
                        site.ProgramCounter,
                        OptimizedInstruction.Table(
                            site.ProgramCounter,
                            LuaJitOptimizationKind.TableSetPic,
                            site.TableShapes));
                    break;
                case LuaIrOpcode.Call:
                case LuaIrOpcode.TailCall:
                    if (!site.IsMegamorphic && site.CallTargets.Length == 1 &&
                        site.CallTargets[0].Kind == LuaJitCallTargetKind.Lua)
                    {
                        result.Add(
                            site.ProgramCounter,
                            OptimizedInstruction.KnownCall(
                                site.ProgramCounter,
                                site.CallTargets[0],
                                instruction.Opcode == LuaIrOpcode.Call && instruction.C >= 0));
                    }

                    break;
            }
        }

        return result.ToImmutable();
    }

    private static void AddConstantFolds(
        LuaIrFunction function,
        LuaRegisterLivenessResult liveness,
        ImmutableDictionary<int, OptimizedInstruction>.Builder result)
    {
        var blocks = function.BasicBlocks.IsDefaultOrEmpty
            ? LuaIrControlFlow.Build(function.Instructions)
            : function.BasicBlocks;
        foreach (var block in blocks)
        {
            for (var pc = block.Start; pc + 2 < block.End; pc++)
            {
                var first = function.Instructions[pc];
                var second = function.Instructions[pc + 1];
                var binary = function.Instructions[pc + 2];
                if (first.Opcode != LuaIrOpcode.LoadConstant ||
                    second.Opcode != LuaIrOpcode.LoadConstant ||
                    binary.Opcode != LuaIrOpcode.Binary ||
                    first.A == second.A || binary.B != first.A || binary.C != second.A ||
                    liveness.LiveAfter[pc + 2].Contains(first.A) && first.A != binary.A ||
                    liveness.LiveAfter[pc + 2].Contains(second.A) && second.A != binary.A ||
                    !TryMaterializePrimitive(function.Constants[first.B], out var left) ||
                    !TryMaterializePrimitive(function.Constants[second.B], out var right) ||
                    !TryFoldBinary((LuaIrBinaryOperator)binary.D, left, right, out var value))
                {
                    continue;
                }

                result.Add(pc, OptimizedInstruction.ConstantFold(pc, binary.A, value));
                pc += 2;
            }
        }
    }

    private static bool TryMaterializePrimitive(LuaIrConstant constant, out LuaValue value)
    {
        value = constant.Kind switch
        {
            LuaIrConstantKind.Nil => LuaValue.Nil,
            LuaIrConstantKind.Boolean => LuaValue.FromBoolean(constant.Boolean),
            LuaIrConstantKind.Integer => LuaValue.FromInteger(constant.Integer),
            LuaIrConstantKind.Float => LuaValue.FromFloat(constant.Float),
            _ => LuaValue.Nil,
        };
        return constant.Kind != LuaIrConstantKind.String;
    }

    private static bool TryFoldBinary(
        LuaIrBinaryOperator operation,
        LuaValue left,
        LuaValue right,
        out LuaValue value)
    {
        if (operation == LuaIrBinaryOperator.Concatenate)
        {
            value = LuaValue.Nil;
            return false;
        }

        try
        {
            value = LuaValueOperations.Binary(null!, operation, left, right);
            return true;
        }
        catch (LuaRuntimeException)
        {
            value = LuaValue.Nil;
            return false;
        }
    }

    private static bool IsStableUnary(
        LuaIrInstruction instruction,
        LuaJitSiteProfile site) => (LuaIrUnaryOperator)instruction.C switch
        {
            LuaIrUnaryOperator.Negate or LuaIrUnaryOperator.BitwiseNot =>
                IsStableNumeric(site.FirstOperandKinds),
            LuaIrUnaryOperator.LogicalNot => site.FirstOperandKinds != LuaJitValueKinds.None,
            LuaIrUnaryOperator.Length => site.FirstOperandKinds is LuaJitValueKinds.String,
            _ => false,
        };

    private static bool IsStableNumeric(LuaJitValueKinds kinds) =>
        kinds != LuaJitValueKinds.None &&
        (kinds & ~(LuaJitValueKinds.Integer | LuaJitValueKinds.Float)) == 0;

    private static bool IsStableTablePic(LuaJitSiteProfile site) =>
        !site.IsMegamorphic &&
        site.FirstOperandKinds == LuaJitValueKinds.Table &&
        !site.TableShapes.IsEmpty &&
        site.TableShapes.All(static shape => !shape.HasMetatable);

    private sealed class Tier2Program
    {
        private readonly LuaIrFunction _function;
        private readonly OptimizedInstruction?[] _optimized;

        public Tier2Program(
            LuaIrFunction function,
            ImmutableDictionary<int, OptimizedInstruction> optimized)
        {
            _function = function;
            _optimized = new OptimizedInstruction?[function.Instructions.Length];
            foreach (var pair in optimized)
            {
                _optimized[pair.Key] = pair.Value;
            }
        }

        public LuaCompiledExit Execute(
            LuaExecutionContext context,
            LuaThread thread,
            LuaFrame frame)
        {
            if (!LuaCodegenAbiV1.CanExecuteCompiled(context))
            {
                return LuaCompiledExit.Deopt(
                    frame.ProgramCounter,
                    context.InstructionsConsumed,
                    LuaCompiledExitReason.DebugModeChanged);
            }

            while ((uint)frame.ProgramCounter < (uint)_function.Instructions.Length)
            {
                var pc = frame.ProgramCounter;
                LuaCodegenAbiV1.ObserveCanonicalInstruction(context, thread, frame, pc);
                var instruction = _function.Instructions[pc];
                if (_optimized[pc] is { } optimization)
                {
                    var optimizedExit = ExecuteOptimized(
                        context,
                        thread,
                        frame,
                        instruction,
                        optimization);
                    if (optimizedExit is { } optimizedResult)
                    {
                        return optimizedResult;
                    }

                    continue;
                }

                var directExit = ExecuteDirect(context, thread, frame, instruction);
                if (directExit is { } directResult)
                {
                    return directResult;
                }
            }

            return LuaCompiledExit.Deopt(
                frame.ProgramCounter,
                context.InstructionsConsumed,
                LuaCompiledExitReason.BackendInvalidated);
        }

        private static LuaCompiledExit? ExecuteOptimized(
            LuaExecutionContext context,
            LuaThread thread,
            LuaFrame frame,
            LuaIrInstruction instruction,
            OptimizedInstruction optimization)
        {
            switch (optimization.Kind)
            {
                case LuaJitOptimizationKind.ConstantFold:
                    if (context.RemainingInstructionCount < optimization.InstructionCount)
                    {
                        return ExecuteDirect(context, thread, frame, instruction);
                    }

                    _ = context.TryReserveInstructions(optimization.InstructionCount);
                    LuaCodegenAbiV1.WriteRegister(
                        thread,
                        frame,
                        optimization.DestinationRegister,
                        optimization.FoldedValue);
                    frame.ProgramCounter += optimization.InstructionCount;
                    return null;
                case LuaJitOptimizationKind.DeadMove:
                    if (!Reserve(context, frame.ProgramCounter))
                    {
                        return BudgetPoll(context, frame.ProgramCounter);
                    }

                    frame.ProgramCounter++;
                    return null;
                case LuaJitOptimizationKind.NumericUnary:
                    {
                        var operand = LuaCodegenAbiV1.ReadRegister(
                            thread,
                            frame,
                            instruction.B);
                        if (!optimization.MatchesFirst(operand))
                        {
                            return GuardFailure(context, frame.ProgramCounter);
                        }

                        if (!Reserve(context, frame.ProgramCounter))
                        {
                            return BudgetPoll(context, frame.ProgramCounter);
                        }

                        var value = LuaValueOperations.Unary(
                            (LuaIrUnaryOperator)instruction.C,
                            operand);
                        LuaCodegenAbiV1.WriteRegister(thread, frame, instruction.A, value);
                        frame.ProgramCounter++;
                        return null;
                    }
                case LuaJitOptimizationKind.NumericBinary:
                    {
                        var left = LuaCodegenAbiV1.ReadRegister(thread, frame, instruction.B);
                        var right = LuaCodegenAbiV1.ReadRegister(thread, frame, instruction.C);
                        if (!optimization.MatchesFirst(left) ||
                            !optimization.MatchesSecond(right))
                        {
                            return GuardFailure(context, frame.ProgramCounter);
                        }

                        if (!Reserve(context, frame.ProgramCounter))
                        {
                            return BudgetPoll(context, frame.ProgramCounter);
                        }

                        var value = LuaValueOperations.Binary(
                            context.State,
                            (LuaIrBinaryOperator)instruction.D,
                            left,
                            right);
                        LuaCodegenAbiV1.WriteRegister(thread, frame, instruction.A, value);
                        frame.ProgramCounter++;
                        return null;
                    }
                case LuaJitOptimizationKind.BooleanBranch:
                    {
                        var truthy = LuaCodegenAbiV1.ReadRegister(
                            thread,
                            frame,
                            instruction.A).IsTruthy;
                        var taken = instruction.Opcode == LuaIrOpcode.JumpIfTrue
                            ? truthy
                            : !truthy;
                        if (taken != optimization.ExpectedBranchTaken)
                        {
                            return GuardFailure(context, frame.ProgramCounter);
                        }

                        if (!Reserve(context, frame.ProgramCounter))
                        {
                            return BudgetPoll(context, frame.ProgramCounter);
                        }

                        frame.ProgramCounter = taken ? instruction.B : frame.ProgramCounter + 1;
                        return null;
                    }
                case LuaJitOptimizationKind.TableGetPic:
                case LuaJitOptimizationKind.TableSetPic:
                    {
                        var tableRegister = optimization.Kind == LuaJitOptimizationKind.TableGetPic
                            ? instruction.B
                            : instruction.A;
                        var keyRegister = optimization.Kind == LuaJitOptimizationKind.TableGetPic
                            ? instruction.C
                            : instruction.B;
                        var target = LuaCodegenAbiV1.ReadRegister(thread, frame, tableRegister);
                        var key = LuaCodegenAbiV1.ReadRegister(thread, frame, keyRegister);
                        if (target.Kind != LuaValueKind.Table ||
                            !optimization.MatchesTable(target.AsTable(), key))
                        {
                            return GuardFailure(context, frame.ProgramCounter);
                        }

                        if (!Reserve(context, frame.ProgramCounter))
                        {
                            return BudgetPoll(context, frame.ProgramCounter);
                        }

                        var table = target.AsTable();
                        if (optimization.Kind == LuaJitOptimizationKind.TableGetPic)
                        {
                            LuaCodegenAbiV1.WriteRegister(
                                thread,
                                frame,
                                instruction.A,
                                table.Get(key));
                        }
                        else
                        {
                            table.Set(
                                key,
                                LuaCodegenAbiV1.ReadRegister(thread, frame, instruction.C));
                        }

                        frame.ProgramCounter++;
                        return LuaCompiledExit.Continue(
                            frame.ProgramCounter,
                            context.InstructionsConsumed);
                    }
                case LuaJitOptimizationKind.KnownClosureCall:
                    {
                        var functionValue = LuaCodegenAbiV1.ReadRegister(
                            thread,
                            frame,
                            instruction.A);
                        var closure = functionValue.TryGetClosure();
                        if (closure is null ||
                            closure.Function.Id != optimization.CallTarget!.FunctionId ||
                            LuaJitModuleIdentity.Create(closure.Module) !=
                            optimization.CallTarget.ModuleContentId)
                        {
                            return GuardFailure(context, frame.ProgramCounter);
                        }

                        return ExecuteSlowPath(context, thread, frame);
                    }
                default:
                    throw new InvalidOperationException(
                        $"Unknown Tier 2 optimization {optimization.Kind}.");
            }
        }

        private static LuaCompiledExit? ExecuteDirect(
            LuaExecutionContext context,
            LuaThread thread,
            LuaFrame frame,
            LuaIrInstruction instruction)
        {
            var pc = frame.ProgramCounter;
            switch (instruction.Opcode)
            {
                case LuaIrOpcode.LoadConstant:
                    if (!Reserve(context, pc))
                    {
                        return BudgetPoll(context, pc);
                    }

                    LuaCodegenAbiV1.WriteRegister(
                        thread,
                        frame,
                        instruction.A,
                        LuaCodegenAbiV1.MaterializeConstant(
                            context,
                            frame,
                            instruction.B));
                    frame.ProgramCounter++;
                    return null;
                case LuaIrOpcode.LoadNil:
                    if (!Reserve(context, pc))
                    {
                        return BudgetPoll(context, pc);
                    }

                    LuaCodegenAbiV1.ClearRegisters(
                        thread,
                        frame,
                        instruction.A,
                        instruction.B);
                    frame.ProgramCounter++;
                    return null;
                case LuaIrOpcode.Move:
                    if (!Reserve(context, pc))
                    {
                        return BudgetPoll(context, pc);
                    }

                    LuaCodegenAbiV1.WriteRegister(
                        thread,
                        frame,
                        instruction.A,
                        LuaCodegenAbiV1.ReadRegister(thread, frame, instruction.B));
                    frame.ProgramCounter++;
                    return null;
                case LuaIrOpcode.SetTop:
                    if (!Reserve(context, pc))
                    {
                        return BudgetPoll(context, pc);
                    }

                    LuaCodegenAbiV1.SetFrameTop(thread, frame, instruction.A);
                    frame.ProgramCounter++;
                    return null;
                case LuaIrOpcode.Jump when instruction.C < 0:
                    if (!Reserve(context, pc))
                    {
                        return BudgetPoll(context, pc);
                    }

                    frame.ProgramCounter = instruction.B;
                    return null;
                case LuaIrOpcode.JumpIfFalse:
                case LuaIrOpcode.JumpIfTrue:
                    if (!Reserve(context, pc))
                    {
                        return BudgetPoll(context, pc);
                    }

                    var truthy = LuaCodegenAbiV1.ReadRegister(
                        thread,
                        frame,
                        instruction.A).IsTruthy;
                    var taken = instruction.Opcode == LuaIrOpcode.JumpIfTrue
                        ? truthy
                        : !truthy;
                    frame.ProgramCounter = taken ? instruction.B : pc + 1;
                    return null;
                case LuaIrOpcode.Return:
                    if (!Reserve(context, pc))
                    {
                        return BudgetPoll(context, pc);
                    }

                    return LuaCompiledExit.Return(pc, context.InstructionsConsumed);
                default:
                    return ExecuteSlowPath(context, thread, frame);
            }
        }

        private static LuaCompiledExit ExecuteSlowPath(
            LuaExecutionContext context,
            LuaThread thread,
            LuaFrame frame)
        {
            LuaCodegenAbiV1.CommitProgramCounter(frame, frame.ProgramCounter);
            return LuaCodegenAbiV1.ExecuteCanonicalInstruction(
                context,
                thread,
                frame,
                frame.ProgramCounter);
        }

        private static bool Reserve(LuaExecutionContext context, int programCounter)
        {
            if (!context.TryReserveInstructions(1))
            {
                return false;
            }

            return true;
        }

        private static LuaCompiledExit BudgetPoll(
            LuaExecutionContext context,
            int programCounter) => LuaCompiledExit.Poll(
                programCounter,
                context.InstructionsConsumed,
                LuaCompiledExitReason.InstructionBudget);

        private static LuaCompiledExit GuardFailure(
            LuaExecutionContext context,
            int programCounter) => LuaCompiledExit.Deopt(
                programCounter,
                context.InstructionsConsumed,
                LuaCompiledExitReason.GuardFailure);
    }

    private sealed record OptimizedInstruction
    {
        public required int ProgramCounter { get; init; }

        public required LuaJitOptimizationKind Kind { get; init; }

        public int InstructionCount { get; init; } = 1;

        public required string Guard { get; init; }

        public LuaJitValueKinds FirstKinds { get; init; }

        public LuaJitValueKinds SecondKinds { get; init; }

        public bool ExpectedBranchTaken { get; init; }

        public ImmutableArray<LuaJitTableShapeProfile> TableShapes { get; init; } = [];

        public LuaJitCallTargetProfile? CallTarget { get; init; }

        public bool ReusesFixedResultWindow { get; init; }

        public int DestinationRegister { get; init; }

        public LuaValue FoldedValue { get; init; }

        public bool HasGuard => Kind != LuaJitOptimizationKind.DeadMove;

        public LuaJitOptimization ToDescription() => new(
            ProgramCounter,
            Kind,
            InstructionCount,
            Guard);

        public bool MatchesFirst(LuaValue value) => Matches(FirstKinds, value);

        public bool MatchesSecond(LuaValue value) => Matches(SecondKinds, value);

        public bool MatchesTable(LuaTable table, LuaValue key) =>
            table.Metatable is null && TableShapes.Any(shape =>
                shape.ArrayCapacity == table.ArrayCapacity &&
                shape.ShapeVersion == table.ShapeVersion &&
                shape.MetatableVersion == table.MetatableVersion &&
                (shape.KeyKinds & ToKinds(key)) != 0);

        public static OptimizedInstruction DeadMove(int pc) => new()
        {
            ProgramCounter = pc,
            Kind = LuaJitOptimizationKind.DeadMove,
            Guard = "destination register is dead on every successor",
        };

        public static OptimizedInstruction ConstantFold(
            int pc,
            int destinationRegister,
            LuaValue value) => new()
            {
                ProgramCounter = pc,
                Kind = LuaJitOptimizationKind.ConstantFold,
                InstructionCount = 3,
                Guard = "verified primitive constants and side-effect-free binary operation",
                DestinationRegister = destinationRegister,
                FoldedValue = value,
            };

        public static OptimizedInstruction Unary(int pc, LuaJitValueKinds kinds) => new()
        {
            ProgramCounter = pc,
            Kind = LuaJitOptimizationKind.NumericUnary,
            Guard = $"operand kind in {kinds}",
            FirstKinds = kinds,
        };

        public static OptimizedInstruction Binary(
            int pc,
            LuaJitValueKinds first,
            LuaJitValueKinds second) => new()
            {
                ProgramCounter = pc,
                Kind = LuaJitOptimizationKind.NumericBinary,
                Guard = $"operand kinds in {first} x {second}",
                FirstKinds = first,
                SecondKinds = second,
            };

        public static OptimizedInstruction Branch(int pc, bool taken) => new()
        {
            ProgramCounter = pc,
            Kind = LuaJitOptimizationKind.BooleanBranch,
            Guard = taken ? "branch remains taken" : "branch remains not-taken",
            ExpectedBranchTaken = taken,
        };

        public static OptimizedInstruction Table(
            int pc,
            LuaJitOptimizationKind kind,
            ImmutableArray<LuaJitTableShapeProfile> shapes) => new()
            {
                ProgramCounter = pc,
                Kind = kind,
                Guard = $"table shape matches one of {shapes.Length} PIC entries",
                TableShapes = shapes,
            };

        public static OptimizedInstruction KnownCall(
            int pc,
            LuaJitCallTargetProfile target,
            bool reusesFixedResultWindow) => new()
            {
                ProgramCounter = pc,
                Kind = LuaJitOptimizationKind.KnownClosureCall,
                Guard = $"callee remains {target.ModuleContentId}/{target.FunctionId}",
                CallTarget = target,
                ReusesFixedResultWindow = reusesFixedResultWindow,
            };

        private static bool Matches(LuaJitValueKinds kinds, LuaValue value) =>
            (kinds & ToKinds(value)) != 0;

        private static LuaJitValueKinds ToKinds(LuaValue value) =>
            (LuaJitValueKinds)(1 << (int)value.Kind);
    }
}
