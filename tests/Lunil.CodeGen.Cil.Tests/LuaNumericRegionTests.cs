using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Lunil.CodeGen.Cil.Jit;
using Lunil.Core.Text;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.CodeGen.Cil.Tests;

public sealed class LuaNumericRegionTests
{
    [Fact]
    public void Tier2SelectsTheMaximalVersionedNestedNumericRegion()
    {
        var module = Compile("""
            local outer, total = 0, 0
            while outer < 3 do
                local inner = 0
                while inner < 4 do
                    total = total + outer + inner
                    inner = inner + 1
                end
                outer = outer + 1
            end
            return total
            """);
        using var executor = CreateTier2Executor();

        for (var execution = 0; execution < 3; execution++)
        {
            AssertValues(ExecuteFresh(executor, module), LuaValue.FromInteger(30));
        }

        var plan = Assert.IsType<LuaJitTier2Plan>(executor.GetTier2Plan(module, 0));
        Assert.Equal(1, plan.NumericRegionCount);
        Assert.True(plan.UnboxedNumericLocalCount > 0);
        Assert.True(plan.DirectNumericInstructionCount >= 6);
        Assert.True(plan.NumericRegionSafepointCount >= 2);
        Assert.Equal(0, plan.NumericRegionHotInstructionBudgetCheckCount);
    }

    [Fact]
    public void Tier2SelectsAQualifiedInnerRegionWhenTheOuterRegionFailsClosed()
    {
        var module = Compile("""
            local outer, total, ignored = 0, 0, false
            while outer < 3 do
                ignored = nil
                local inner = 0
                while inner < 4 do
                    total = total + inner
                    inner = inner + 1
                end
                outer = outer + 1
            end
            return total, ignored
            """);
        using var executor = CreateTier2Executor();

        for (var execution = 0; execution < 3; execution++)
        {
            AssertValues(
                ExecuteFresh(executor, module),
                LuaValue.FromInteger(18),
                LuaValue.Nil);
        }

        var plan = Assert.IsType<LuaJitTier2Plan>(executor.GetTier2Plan(module, 0));
        Assert.Equal(1, plan.NumericRegionCount);
        Assert.True(plan.UnboxedNumericLocalCount > 0);
        Assert.True(plan.DirectNumericInstructionCount >= 3);
        Assert.Equal(1, plan.NumericRegionSafepointCount);
        Assert.Equal(0, plan.NumericRegionHotInstructionBudgetCheckCount);
    }

    [Fact]
    public void Tier2RegionPreservesIntegerWrapFloorAndZeroDivisorDeopt()
    {
        var module = Compile("""
            local left, right = ...
            local quotient, remainder, total, index = left, left, left, 0
            while index < 3 do
                quotient = quotient // right
                remainder = remainder % right
                total = total + right
                index = index + 1
            end
            return quotient, remainder, total
            """);
        using var executor = CreateTier2Executor();

        for (var execution = 0; execution < 2; execution++)
        {
            AssertValues(
                ExecuteFresh(
                    executor,
                    module,
                    LuaValue.FromInteger(-17),
                    LuaValue.FromInteger(5)),
                LuaValue.FromInteger(-1),
                LuaValue.FromInteger(3),
                LuaValue.FromInteger(-2));
        }

        var plan = Assert.IsType<LuaJitTier2Plan>(executor.GetTier2Plan(module, 0));
        Assert.Equal(1, plan.NumericRegionCount);
        AssertValues(
            ExecuteFresh(
                executor,
                module,
                LuaValue.FromInteger(long.MinValue),
                LuaValue.FromInteger(-1)),
            LuaValue.FromInteger(long.MinValue),
            LuaValue.FromInteger(0),
            LuaValue.FromInteger(long.MaxValue - 2));

        Assert.Throws<LuaRuntimeException>(() => ExecuteFresh(
            executor,
            module,
            LuaValue.FromInteger(4),
            LuaValue.FromInteger(0)));
    }

    [Fact]
    public void Tier2RegionPreservesMixedNaNAndNegativeZeroSemantics()
    {
        var module = Compile("""
            local left, right = ...
            local result, less, index = 0.0, false, 0
            while index < 3 do
                result = left % 2.0
                less = right < left
                index = index + 1
            end
            return result, less
            """);
        using var executor = CreateTier2Executor();
        var warmArguments = new[]
        {
            LuaValue.FromFloat(5.5),
            LuaValue.FromInteger(2),
        };

        _ = ExecuteFresh(executor, module, warmArguments);
        _ = ExecuteFresh(executor, module, warmArguments);
        Assert.Equal(
            1,
            Assert.IsType<LuaJitTier2Plan>(executor.GetTier2Plan(module, 0))
                .NumericRegionCount);

        var nan = ExecuteFresh(
            executor,
            module,
            LuaValue.FromFloat(double.NaN),
            LuaValue.FromInteger(0));
        Assert.True(double.IsNaN(nan.Values[0].AsFloat()));
        Assert.Equal(LuaValue.FromBoolean(false), nan.Values[1]);

        var negativeZero = ExecuteFresh(
            executor,
            module,
            LuaValue.FromFloat(-0.0),
            LuaValue.FromInteger(0));
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(-0.0),
            BitConverter.DoubleToInt64Bits(negativeZero.Values[0].AsFloat()));
        Assert.Equal(LuaValue.FromBoolean(false), negativeZero.Values[1]);
    }

    [Fact]
    public void Tier2RegionGuardFailureResumesAtTheCanonicalHeader()
    {
        var module = Compile("""
            local remaining, total = ...
            while remaining > 0 do
                total = total + remaining
                remaining = remaining - 1
            end
            return total
            """);
        using var executor = CreateTier2Executor();

        _ = ExecuteFresh(
            executor,
            module,
            LuaValue.FromInteger(3),
            LuaValue.FromInteger(0));
        _ = ExecuteFresh(
            executor,
            module,
            LuaValue.FromInteger(3),
            LuaValue.FromInteger(0));
        Assert.Equal(
            1,
            Assert.IsType<LuaJitTier2Plan>(executor.GetTier2Plan(module, 0))
                .NumericRegionCount);

        AssertValues(
            ExecuteFresh(
                executor,
                module,
                LuaValue.FromFloat(3.5),
                LuaValue.FromFloat(0.5)),
            LuaValue.FromFloat(8.5));
        Assert.True(executor.Statistics.Tier2GuardFailures >= 1);
    }

    [Fact]
    public void LoopOsrRegionPublishesCapturedLocalsBeforeGcSafepointsAndExit()
    {
        var module = Compile("""
            local value, index = 0, 0
            local read = function() return value end
            while index < 2500 do
                if index % 2 == 0 then
                    value = index
                else
                    value = index + 0.5
                end
                index = index + 1
            end
            return read()
            """);
        using var executor = CreateLoopOsrExecutor();
        var state = new LuaState(new LuaStateOptions
        {
            Heap = LuaHeapOptions.Default with { StressEveryAllocation = true },
        });

        AssertValues(
            executor.Execute(state, state.CreateMainClosure(module)),
            LuaValue.FromFloat(2499.5));
        var plan = Assert.Single(executor.GetLoopOsrPlans(module, 0));
        Assert.Equal(1, plan.NumericRegionCount);
        Assert.True(plan.UnboxedNumericLocalCount > 0);
        Assert.True(plan.NumericRegionSafepointCount > 0);
        Assert.Equal(0, plan.NumericRegionHotInstructionBudgetCheckCount);
        Assert.True(state.Heap.CompletedCycleCount > 0);
    }

    [Fact]
    public void Tier2RegionPreservesSetTopShrinkThenGrowClearing()
    {
        var module = CreateSetTopLoopModule();
        using (var interpreter = new LuaJitExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.InterpreterOnly,
        }))
        {
            AssertValues(
                ExecuteFresh(
                    interpreter,
                    module,
                    LuaValue.FromInteger(2),
                    LuaValue.FromInteger(1)),
                LuaValue.Nil);
        }

        using var executor = CreateTier2Executor();

        _ = ExecuteFresh(
            executor,
            module,
            LuaValue.FromInteger(2),
            LuaValue.FromInteger(1));
        _ = ExecuteFresh(
            executor,
            module,
            LuaValue.FromInteger(2),
            LuaValue.FromInteger(1));
        AssertValues(
            ExecuteFresh(
                executor,
                module,
                LuaValue.FromInteger(2),
                LuaValue.FromInteger(1)),
            LuaValue.Nil);

        Assert.Equal(
            1,
            Assert.IsType<LuaJitTier2Plan>(executor.GetTier2Plan(module, 0))
                .NumericRegionCount);
    }

    [Fact]
    public void Tier2RegionPreservesAliasedOperandsAndMaterializesLiveSideExit()
    {
        var module = CreateAliasedLoopModule();
        using var executor = new LuaJitExecutor(LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.PreferJit,
            FunctionEntryThreshold = 1,
            BackedgeThreshold = 1,
            SynchronousCompilation = true,
            EnableTier2 = true,
            EnableLoopOsr = false,
            Tier2InvocationThreshold = 1,
            Tier2BackedgeThreshold = int.MaxValue,
        });

        for (var execution = 0; execution < 3; execution++)
        {
            var state = new LuaState();
            var result = executor.Execute(
                state,
                state.CreateMainClosure(module),
                [LuaValue.FromInteger(10), LuaValue.FromInteger(7)]);
            Assert.True(result.Values.SequenceEqual([LuaValue.FromInteger(17)]));
        }

        var plan = Assert.IsType<LuaJitTier2Plan>(executor.GetTier2Plan(module, 0));
        Assert.Equal(LuaJitTier2CodeKind.ExactNumericSpecializedCil, plan.CodeKind);
        Assert.Equal(1, plan.NumericRegionCount);
        Assert.True(plan.UnboxedNumericLocalCount >= 4);
        Assert.True(plan.DirectNumericInstructionCount >= 3);
        Assert.Equal(1, plan.NumericRegionSafepointCount);
        Assert.Equal(
            executor.Statistics.Tier2MethodEntries,
            executor.Statistics.Tier2CompletedInvocations);
    }

    [Fact]
    public void PlannerRejectsConflictingDefinitionsAcrossTheWholeNaturalLoop()
    {
        var instructions = ImmutableArray.Create(
            new LuaIrInstruction(
                LuaIrOpcode.Binary,
                a: 1,
                b: 0,
                c: 0,
                d: (int)LuaIrBinaryOperator.Add),
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 2, b: 2),
            new LuaIrInstruction(LuaIrOpcode.JumpIfFalse, a: 2, b: 5),
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 0, b: 0),
            new LuaIrInstruction(LuaIrOpcode.Jump, b: 6, c: -1),
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 0, b: 1),
            new LuaIrInstruction(LuaIrOpcode.Jump, b: 0, c: -1),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 1, b: 1));
        var function = new LuaIrFunction
        {
            Id = 0,
            Span = default,
            RegisterCount = 3,
            Constants =
            [
                LuaIrConstant.FromInteger(1),
                LuaIrConstant.FromFloat(1.0),
                LuaIrConstant.FromBoolean(false),
            ],
            Instructions = instructions,
            BasicBlocks = LuaIrControlFlow.Build(instructions),
        };
        var module = new LuaIrModule
        {
            MainFunctionId = 0,
            Functions = [function],
        };

        var region = Assert.Single(LuaNumericRegionAnalyzer.AnalyzeNaturalLoops(
            module,
            0,
            out _,
            CancellationToken.None));

        Assert.Null(LuaNumericRegionPlanner.TryCreate(function, region, []));
    }

    [Fact]
    public void PlannerRejectsNumericUseOfARegisterClearedBySetTop()
    {
        var instructions = ImmutableArray.Create(
            new LuaIrInstruction(LuaIrOpcode.SetTop, a: 1),
            new LuaIrInstruction(
                LuaIrOpcode.Unary,
                a: 0,
                b: 1,
                c: (int)LuaIrUnaryOperator.BitwiseNot),
            new LuaIrInstruction(LuaIrOpcode.Jump, b: 0, c: -1),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));
        var function = new LuaIrFunction
        {
            Id = 0,
            Span = default,
            RegisterCount = 2,
            Constants = [],
            Instructions = instructions,
            BasicBlocks = LuaIrControlFlow.Build(instructions),
        };
        var module = new LuaIrModule
        {
            MainFunctionId = 0,
            Functions = [function],
        };
        var region = Assert.Single(LuaNumericRegionAnalyzer.AnalyzeNaturalLoops(
            module,
            0,
            out _,
            CancellationToken.None));

        Assert.Null(LuaNumericRegionPlanner.TryCreate(function, region, []));
    }

    [Fact]
    public void PlannerRejectsUnknownTruthinessAtAConditionalBranch()
    {
        var instructions = ImmutableArray.Create(
            new LuaIrInstruction(LuaIrOpcode.JumpIfFalse, a: 0, b: 4),
            new LuaIrInstruction(
                LuaIrOpcode.Binary,
                a: 1,
                b: 1,
                c: 2,
                d: (int)LuaIrBinaryOperator.Add),
            new LuaIrInstruction(LuaIrOpcode.Move, a: 2, b: 1),
            new LuaIrInstruction(LuaIrOpcode.Jump, b: 0, c: -1),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 1, b: 1));
        var function = new LuaIrFunction
        {
            Id = 0,
            Span = default,
            RegisterCount = 3,
            Constants = [],
            Instructions = instructions,
            BasicBlocks = LuaIrControlFlow.Build(instructions),
        };
        var module = new LuaIrModule
        {
            MainFunctionId = 0,
            Functions = [function],
        };
        var region = Assert.Single(LuaNumericRegionAnalyzer.AnalyzeNaturalLoops(
            module,
            0,
            out _,
            CancellationToken.None));

        Assert.Null(LuaNumericRegionPlanner.TryCreate(
            function,
            region,
            [
                new LuaNumericRegionTypeHint(
                    1,
                    1,
                    LuaNumericRegionValueKind.Integer),
                new LuaNumericRegionTypeHint(
                    1,
                    2,
                    LuaNumericRegionValueKind.Integer),
            ]));
    }

    [Fact]
    public void BudgetPlanCutsBackedgesAndRecordsExactBlockAndSlowTailCosts()
    {
        var module = CreateBudgetLoopModule();
        var function = module.Functions[0];
        var region = Assert.Single(LuaNumericRegionAnalyzer.AnalyzeNaturalLoops(
            module,
            0,
            out _,
            CancellationToken.None));
        var plan = Assert.IsType<LuaNumericRegionPlan>(LuaNumericRegionPlanner.TryCreate(
            function,
            region,
            [
                new LuaNumericRegionTypeHint(0, 0, LuaNumericRegionValueKind.Integer),
                new LuaNumericRegionTypeHint(0, 1, LuaNumericRegionValueKind.Integer),
            ]));

        Assert.Equal(0, plan.HotInstructionBudgetCheckCount);
        Assert.Equal(4, plan.MaximumBackedgeSegmentInstructionCost);
        Assert.Equal(4, plan.GetBudgetSite(0).MaximumInstructionCostToSafepointOrExit);
        Assert.Equal(2, plan.GetBudgetSite(0).BasicBlockInstructionCost);
        Assert.Equal(2, plan.GetBudgetSite(0).FailureInstructionRollbackCount);
        Assert.Equal(1, plan.GetBudgetSite(1).RemainingBasicBlockInstructionCost);
        Assert.Equal(1, plan.GetBudgetSite(1).FailureInstructionRollbackCount);
        Assert.Equal(2, plan.GetBudgetSite(2).BasicBlockInstructionCost);
        Assert.Equal(2, plan.GetBudgetSite(2).ColdSlowTailProgramCounter);
        Assert.Equal(2, plan.GetBudgetSite(2).DeoptimizationProgramCounter);
    }

    [Fact]
    public void NumericRegionStopsAtEveryExactBudgetPcBeforeCheckingTags()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        var module = CreateBudgetLoopModule();
        var function = module.Functions[0];
        var region = Assert.Single(LuaNumericRegionAnalyzer.AnalyzeNaturalLoops(
            module,
            0,
            out _,
            CancellationToken.None));
        var plan = Assert.IsType<LuaNumericRegionPlan>(
            LuaNumericRegionPlanner.TryCreate(
                function,
                region,
                [
                    new LuaNumericRegionTypeHint(
                        0,
                        0,
                        LuaNumericRegionValueKind.Integer),
                    new LuaNumericRegionTypeHint(
                        0,
                        1,
                        LuaNumericRegionValueKind.Integer),
                ]));
        Assert.True(ReflectionEmitLuaNumericRegionCompiler.TryCompile(
            function,
            plan,
            new LuaNumericRegionEmissionMode(
                RequireLoopOsrEntry: false,
                ObserveLoopOsrBackedge: false),
            CancellationToken.None,
            out var compiled));

        for (var budget = 0; budget <= 14; budget++)
        {
            var (context, thread, frame) = CreateBudgetFrame(module, budget);

            var exit = compiled.Method(context, thread, frame);

            Assert.Equal(budget, exit.InstructionsConsumed);
            Assert.Equal(budget, context.InstructionsConsumed);
            Assert.Equal(0, context.RemainingInstructionCount);
            if (budget < 14)
            {
                Assert.Equal(LuaCompiledExitKind.Poll, exit.Kind);
                Assert.Equal(LuaCompiledExitReason.InstructionBudget, exit.Reason);
                Assert.Equal(budget % 4, exit.ProgramCounter);
                Assert.Equal(budget % 4, frame.ProgramCounter);
            }
            else
            {
                Assert.Equal(LuaCompiledExitKind.Continue, exit.Kind);
                Assert.Equal(4, exit.ProgramCounter);
                Assert.Equal(4, frame.ProgramCounter);
            }

            var completedSubtractions = Math.Min(3, (budget + 1) / 4);
            Assert.Equal(
                LuaValue.FromInteger(3 - completedSubtractions),
                thread.Stack[0]);
        }

        var (zeroContext, zeroThread, zeroFrame) = CreateBudgetFrame(module, 0);
        zeroThread.Stack[0] = LuaValue.FromFloat(3.0);

        var zeroExit = compiled.Method(zeroContext, zeroThread, zeroFrame);

        Assert.Equal(LuaCompiledExitKind.Poll, zeroExit.Kind);
        Assert.Equal(LuaCompiledExitReason.InstructionBudget, zeroExit.Reason);
        Assert.Equal(0, zeroExit.ProgramCounter);
        Assert.Equal(0, zeroExit.InstructionsConsumed);
    }

    [Fact]
    public void HotQuantumAndColdSlowTailCommitTheSameBranchedLoopState()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        var module = CreateBudgetLoopModule();
        var function = module.Functions[0];
        var region = Assert.Single(LuaNumericRegionAnalyzer.AnalyzeNaturalLoops(
            module,
            0,
            out _,
            CancellationToken.None));
        var plan = Assert.IsType<LuaNumericRegionPlan>(LuaNumericRegionPlanner.TryCreate(
            function,
            region,
            [
                new LuaNumericRegionTypeHint(0, 0, LuaNumericRegionValueKind.Integer),
                new LuaNumericRegionTypeHint(0, 1, LuaNumericRegionValueKind.Integer),
            ]));
        Assert.True(ReflectionEmitLuaNumericRegionCompiler.TryCompile(
            function,
            plan,
            new LuaNumericRegionEmissionMode(false, false),
            CancellationToken.None,
            out var compiled));

        foreach (var budget in new[] { 14, 10_000 })
        {
            var (context, thread, frame) = CreateBudgetFrame(module, budget);

            var exit = compiled.Method(context, thread, frame);

            Assert.Equal(LuaCompiledExitKind.Continue, exit.Kind);
            Assert.Equal(4, exit.ProgramCounter);
            Assert.Equal(14, exit.InstructionsConsumed);
            Assert.Equal(budget - 14, context.RemainingInstructionCount);
            Assert.Equal(LuaValue.FromInteger(0), thread.Stack[0]);
        }

        var wideBudget = (long)int.MaxValue + 14;
        var (wideContext, wideThread, wideFrame) = CreateBudgetFrame(module, wideBudget);
        Assert.True(wideContext.TryReserveInstructions(int.MaxValue));

        var wideExit = compiled.Method(wideContext, wideThread, wideFrame);

        Assert.Equal(LuaCompiledExitKind.Continue, wideExit.Kind);
        Assert.Equal(wideBudget, wideExit.InstructionsConsumed);
        Assert.Equal(wideBudget, wideContext.InstructionsConsumed);
        Assert.Equal(0, wideContext.RemainingInstructionCount);
        Assert.Equal(LuaValue.FromInteger(0), wideThread.Stack[0]);
    }

    [Theory]
    [InlineData(LuaIrBinaryOperator.FloorDivide)]
    [InlineData(LuaIrBinaryOperator.Modulo)]
    public void IntegerZeroDivisorDeoptCancelsTheFailingInstructionReservation(
        LuaIrBinaryOperator operation)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        var module = CreateZeroDivisorLoopModule(operation);
        var function = module.Functions[0];
        var region = Assert.Single(LuaNumericRegionAnalyzer.AnalyzeNaturalLoops(
            module,
            0,
            out _,
            CancellationToken.None));
        var plan = Assert.IsType<LuaNumericRegionPlan>(
            LuaNumericRegionPlanner.TryCreate(
                function,
                region,
                [
                    new LuaNumericRegionTypeHint(
                        0,
                        0,
                        LuaNumericRegionValueKind.Integer),
                    new LuaNumericRegionTypeHint(
                        0,
                        2,
                        LuaNumericRegionValueKind.Integer),
                    new LuaNumericRegionTypeHint(
                        3,
                        1,
                        LuaNumericRegionValueKind.Integer),
                ]));
        Assert.True(ReflectionEmitLuaNumericRegionCompiler.TryCompile(
            function,
            plan,
            new LuaNumericRegionEmissionMode(
                RequireLoopOsrEntry: false,
                ObserveLoopOsrBackedge: false),
            CancellationToken.None,
            out var compiled));
        var state = new LuaState();
        var thread = state.MainThread;
        var frame = new LuaFrame(
            state.CreateMainClosure(module),
            @base: 0,
            top: 5,
            returnBase: 0,
            expectedResults: 0,
            varArgs: []);
        thread.Stack[0] = LuaValue.FromInteger(3);
        thread.Stack[1] = LuaValue.FromInteger(1);
        thread.Stack[2] = LuaValue.FromInteger(0);
        var context = new LuaExecutionContext(
            state,
            thread,
            remainingInstructionCount: 100);

        var exit = compiled.Method(context, thread, frame);

        Assert.Equal(LuaCompiledExitKind.Deopt, exit.Kind);
        Assert.Equal(LuaCompiledExitReason.GuardFailure, exit.Reason);
        Assert.Equal(2, exit.ProgramCounter);
        Assert.Equal(2, frame.ProgramCounter);
        Assert.Equal(2, exit.InstructionsConsumed);
        Assert.Equal(2, context.InstructionsConsumed);
        Assert.Equal(98, context.RemainingInstructionCount);
        Assert.Equal(LuaValue.FromInteger(3), thread.Stack[0]);
        Assert.Equal(LuaValue.Nil, thread.Stack[4]);

        var hotState = new LuaState();
        var hotThread = hotState.MainThread;
        var hotFrame = new LuaFrame(
            hotState.CreateMainClosure(module),
            @base: 0,
            top: 5,
            returnBase: 0,
            expectedResults: 0,
            varArgs: []);
        hotThread.Stack[0] = LuaValue.FromInteger(3);
        hotThread.Stack[1] = LuaValue.FromInteger(1);
        hotThread.Stack[2] = LuaValue.FromInteger(0);
        var hotContext = new LuaExecutionContext(hotState, hotThread, 10_000);

        var hotExit = compiled.Method(hotContext, hotThread, hotFrame);

        Assert.Equal(LuaCompiledExitKind.Deopt, hotExit.Kind);
        Assert.Equal(2, hotExit.ProgramCounter);
        Assert.Equal(2, hotExit.InstructionsConsumed);
        Assert.Equal(9_998, hotContext.RemainingInstructionCount);
        Assert.Equal(LuaValue.FromInteger(3), hotThread.Stack[0]);
        Assert.Equal(LuaValue.Nil, hotThread.Stack[4]);
    }

    [Fact]
    public void BackedgeSafepointMaterializesStateAndReturnsForPendingFinalizers()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        var module = CreateBudgetLoopModule();
        var function = module.Functions[0];
        var region = Assert.Single(LuaNumericRegionAnalyzer.AnalyzeNaturalLoops(
            module,
            0,
            out _,
            CancellationToken.None));
        var plan = Assert.IsType<LuaNumericRegionPlan>(LuaNumericRegionPlanner.TryCreate(
            function,
            region,
            [
                new LuaNumericRegionTypeHint(0, 0, LuaNumericRegionValueKind.Integer),
                new LuaNumericRegionTypeHint(0, 1, LuaNumericRegionValueKind.Integer),
            ]));
        Assert.True(ReflectionEmitLuaNumericRegionCompiler.TryCompile(
            function,
            plan,
            new LuaNumericRegionEmissionMode(false, false),
            CancellationToken.None,
            out var compiled));
        var state = new LuaState(new LuaStateOptions
        {
            Heap = LuaHeapOptions.Default with { StressEveryAllocation = true },
        });
        var metatable = state.CreateTable();
        metatable.Set(
            LuaValue.FromString(state.Strings.GetOrCreate("__gc"u8)),
            LuaValue.FromFunction(new LuaNativeFunction("finalizer", static (_, _) => [])));
        var finalizable = state.CreateTable();
        finalizable.SetMetatable(metatable);
        state.Heap.CollectFull();
        Assert.True(state.Heap.PendingFinalizerCount > 0);
        var completedBefore = state.Heap.CompletedCycleCount;
        var thread = state.MainThread;
        var frame = new LuaFrame(
            state.CreateMainClosure(module),
            @base: 0,
            top: 3,
            returnBase: 0,
            expectedResults: 0,
            varArgs: []);
        thread.Stack[0] = LuaValue.FromInteger(2500);
        thread.Stack[1] = LuaValue.FromInteger(1);
        thread.PushFrame(frame);
        try
        {
            var context = new LuaExecutionContext(state, thread, 100_000);

            var exit = compiled.Method(context, thread, frame);

            Assert.Equal(LuaCompiledExitKind.Poll, exit.Kind);
            Assert.Equal(LuaCompiledExitReason.GarbageCollection, exit.Reason);
            Assert.Equal(0, exit.ProgramCounter);
            Assert.Equal(4096, exit.InstructionsConsumed);
            Assert.Equal(4096, context.InstructionsConsumed);
            Assert.Equal(95_904, context.RemainingInstructionCount);
            Assert.Equal(LuaValue.FromInteger(1476), thread.Stack[0]);
            Assert.Equal(0, frame.ProgramCounter);
            Assert.True(state.Heap.CompletedCycleCount > completedBefore);
        }
        finally
        {
            Assert.Same(frame, thread.PopFrame());
        }
    }

    [Fact]
    public void LoopOsrBatchesCallsButAccountsEveryLogicalBackedge()
    {
        var module = Compile("""
            local index = 0
            while index < 2500 do index = index + 1 end
            return index
            """);
        using var executor = CreateLoopOsrExecutor();

        AssertValues(
            ExecuteFresh(executor, module),
            LuaValue.FromInteger(2500));

        Assert.Equal(2500, executor.Statistics.Backedges);
        var plan = Assert.Single(executor.GetLoopOsrPlans(module, 0));
        Assert.Equal(1, plan.NumericRegionCount);
        Assert.True(plan.NumericRegionSafepointCount > 0);
        Assert.Equal(0, plan.NumericRegionHotInstructionBudgetCheckCount);
    }

    [Fact]
    public void MixedComparisonAndShiftHelpersPreserveLuaEdgeSemantics()
    {
        Assert.False(LuaNumericRegionRuntime.CompareMixed(
            long.MaxValue,
            9_223_372_036_854_775_808d,
            integerOnLeft: true,
            LuaIrBinaryOperator.GreaterThanOrEqual));
        Assert.True(LuaNumericRegionRuntime.CompareMixed(
            long.MaxValue,
            9_223_372_036_854_775_808d,
            integerOnLeft: false,
            LuaIrBinaryOperator.GreaterThan));
        Assert.True(LuaNumericRegionRuntime.CompareMixed(
            0,
            double.NaN,
            integerOnLeft: true,
            LuaIrBinaryOperator.NotEqual));
        Assert.Equal(0, LuaNumericRegionRuntime.Shift(1, 64, left: true));
        Assert.Equal(4, LuaNumericRegionRuntime.Shift(8, -1, left: true));
        Assert.Equal(0, LuaNumericRegionRuntime.Shift(1, long.MinValue, left: true));
        Assert.Equal(-2.0, LuaNumericRegionRuntime.FloatingModulo(5.0, -7.0));
    }

    private static LuaIrModule CreateAliasedLoopModule()
    {
        var instructions = ImmutableArray.Create(
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 2, b: 0),
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 4, b: 1),
            new LuaIrInstruction(
                LuaIrOpcode.Binary,
                a: 3,
                b: 1,
                c: 2,
                d: (int)LuaIrBinaryOperator.GreaterThan),
            new LuaIrInstruction(LuaIrOpcode.JumpIfFalse, a: 3, b: 7),
            new LuaIrInstruction(
                LuaIrOpcode.Binary,
                a: 0,
                b: 0,
                c: 4,
                d: (int)LuaIrBinaryOperator.Add),
            new LuaIrInstruction(
                LuaIrOpcode.Binary,
                a: 1,
                b: 1,
                c: 4,
                d: (int)LuaIrBinaryOperator.Subtract),
            new LuaIrInstruction(LuaIrOpcode.Jump, b: 2, c: -1),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));
        return new LuaIrModule
        {
            MainFunctionId = 0,
            Functions =
            [
                new LuaIrFunction
                {
                    Id = 0,
                    Span = new TextSpan(0, 0),
                    ParameterCount = 2,
                    RegisterCount = 5,
                    Constants =
                    [
                        LuaIrConstant.FromInteger(0),
                        LuaIrConstant.FromInteger(1),
                    ],
                    Instructions = instructions,
                    BasicBlocks = LuaIrControlFlow.Build(instructions),
                },
            ],
        };
    }

    private static LuaIrModule CreateBudgetLoopModule()
    {
        var instructions = ImmutableArray.Create(
            new LuaIrInstruction(
                LuaIrOpcode.Binary,
                a: 2,
                b: 0,
                c: 1,
                d: (int)LuaIrBinaryOperator.GreaterThanOrEqual),
            new LuaIrInstruction(LuaIrOpcode.JumpIfFalse, a: 2, b: 4),
            new LuaIrInstruction(
                LuaIrOpcode.Binary,
                a: 0,
                b: 0,
                c: 1,
                d: (int)LuaIrBinaryOperator.Subtract),
            new LuaIrInstruction(LuaIrOpcode.Jump, b: 0, c: -1),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));
        return new LuaIrModule
        {
            MainFunctionId = 0,
            Functions =
            [
                new LuaIrFunction
                {
                    Id = 0,
                    Span = default,
                    ParameterCount = 2,
                    RegisterCount = 3,
                    Constants = [],
                    Instructions = instructions,
                    BasicBlocks = LuaIrControlFlow.Build(instructions),
                },
            ],
        };
    }

    private static LuaIrModule CreateZeroDivisorLoopModule(
        LuaIrBinaryOperator operation)
    {
        var instructions = ImmutableArray.Create(
            new LuaIrInstruction(
                LuaIrOpcode.Binary,
                a: 3,
                b: 0,
                c: 2,
                d: (int)LuaIrBinaryOperator.GreaterThan),
            new LuaIrInstruction(LuaIrOpcode.JumpIfFalse, a: 3, b: 5),
            new LuaIrInstruction(
                LuaIrOpcode.Binary,
                a: 4,
                b: 0,
                c: 2,
                d: (int)operation),
            new LuaIrInstruction(
                LuaIrOpcode.Binary,
                a: 0,
                b: 0,
                c: 1,
                d: (int)LuaIrBinaryOperator.Subtract),
            new LuaIrInstruction(LuaIrOpcode.Jump, b: 0, c: -1),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 4, b: 1));
        return new LuaIrModule
        {
            MainFunctionId = 0,
            Functions =
            [
                new LuaIrFunction
                {
                    Id = 0,
                    Span = default,
                    ParameterCount = 3,
                    RegisterCount = 5,
                    Constants = [],
                    Instructions = instructions,
                    BasicBlocks = LuaIrControlFlow.Build(instructions),
                },
            ],
        };
    }

    private static (LuaExecutionContext Context, LuaThread Thread, LuaFrame Frame)
        CreateBudgetFrame(LuaIrModule module, long budget)
    {
        var state = new LuaState();
        var thread = state.MainThread;
        var frame = new LuaFrame(
            state.CreateMainClosure(module),
            @base: 0,
            top: 3,
            returnBase: 0,
            expectedResults: 0,
            varArgs: []);
        thread.Stack[0] = LuaValue.FromInteger(3);
        thread.Stack[1] = LuaValue.FromInteger(1);
        var context = new LuaExecutionContext(
            state,
            thread,
            remainingInstructionCount: budget);
        return (context, thread, frame);
    }

    private static LuaIrModule CreateSetTopLoopModule()
    {
        var instructions = ImmutableArray.Create(
            new LuaIrInstruction(
                LuaIrOpcode.Binary,
                a: 2,
                b: 0,
                c: 1,
                d: (int)LuaIrBinaryOperator.GreaterThan),
            new LuaIrInstruction(LuaIrOpcode.JumpIfFalse, a: 2, b: 7),
            new LuaIrInstruction(
                LuaIrOpcode.Binary,
                a: 3,
                b: 0,
                c: 1,
                d: (int)LuaIrBinaryOperator.Add),
            new LuaIrInstruction(LuaIrOpcode.SetTop, a: 2),
            new LuaIrInstruction(LuaIrOpcode.SetTop, a: 4),
            new LuaIrInstruction(
                LuaIrOpcode.Binary,
                a: 0,
                b: 0,
                c: 1,
                d: (int)LuaIrBinaryOperator.Subtract),
            new LuaIrInstruction(LuaIrOpcode.Jump, b: 0, c: -1),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 3, b: 1));
        return new LuaIrModule
        {
            MainFunctionId = 0,
            Functions =
            [
                new LuaIrFunction
                {
                    Id = 0,
                    Span = new TextSpan(0, 0),
                    ParameterCount = 2,
                    RegisterCount = 4,
                    Constants = [],
                    Instructions = instructions,
                    BasicBlocks = LuaIrControlFlow.Build(instructions),
                },
            ],
        };
    }

    private static LuaJitExecutor CreateTier2Executor() => new(
        LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.PreferJit,
            FunctionEntryThreshold = 1,
            BackedgeThreshold = 1,
            SynchronousCompilation = true,
            EnableTier2 = true,
            EnableLoopOsr = false,
            Tier2InvocationThreshold = 1,
            Tier2BackedgeThreshold = int.MaxValue,
        });

    private static LuaJitExecutor CreateLoopOsrExecutor() => new(
        LuaJitExecutorOptions.Default with
        {
            Policy = LuaJitPolicy.Auto,
            FunctionEntryThreshold = int.MaxValue,
            BackedgeThreshold = int.MaxValue,
            EnableTier2 = false,
            EnableLoopOsr = true,
            EnableLoopOsrManagedFallback = false,
            LoopOsrBackedgeThreshold = 1,
            SynchronousCompilation = true,
        });

    private static LuaExecutionResult ExecuteFresh(
        LuaJitExecutor executor,
        LuaIrModule module,
        params LuaValue[] arguments)
    {
        var state = new LuaState();
        return executor.Execute(state, state.CreateMainClosure(module), arguments);
    }

    private static void AssertValues(
        LuaExecutionResult result,
        params LuaValue[] expected) =>
        Assert.True(
            result.Values.SequenceEqual(expected),
            $"Expected [{string.Join(", ", expected)}], actual " +
            $"[{string.Join(", ", result.Values)}].");

    private static LuaIrModule Compile(string source)
    {
        var parsing = LuaParser.Parse(SourceText.FromUtf8(source));
        var binding = LuaBinder.Bind(parsing);
        var lowering = LuaLowerer.Lower(binding);
        Assert.True(lowering.Succeeded, string.Join(
            "; ",
            lowering.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        return Assert.IsType<LuaIrModule>(lowering.Module);
    }

}
