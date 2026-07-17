using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Lunil.CodeGen.Cil.Analysis;
using Lunil.CodeGen.Cil.Emission;
using Lunil.CodeGen.Cil.Jit;
using Lunil.CodeGen.Cil.Planning;
using Lunil.CodeGen.Cil.Verification;
using Lunil.Core.Text;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.CodeGen.Cil.Tests;

public sealed class LuaCilCodeGeneratorTests
{
    [Fact]
    public void PlansAndVerifiesTheInitialCanonicalOpcodeSubset()
    {
        var module = CreateModule(
            registerCount: 2,
            constants: [LuaIrConstant.FromInteger(42)],
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 0, b: 0),
            new LuaIrInstruction(LuaIrOpcode.Move, a: 1, b: 0),
            new LuaIrInstruction(LuaIrOpcode.JumpIfFalse, a: 1, b: 4),
            new LuaIrInstruction(LuaIrOpcode.SetTop, a: 2),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));

        var result = LuaCilCodeGenerator.PlanFunction(module, 0);

        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(static d => d.Message)));
        Assert.NotNull(result.Plan);
        Assert.Contains(result.Plan.Instructions, instruction =>
            instruction.CallTarget?.Id == "LuaCodegenAbiV1.MaterializeConstant");
        Assert.Contains(result.Plan.Instructions, instruction =>
            instruction.CallTarget?.Id == "LuaCompiledExit.Return");
        Assert.Contains(result.Plan.GcMaps, map => map.CanonicalProgramCounter == 0);
        Assert.Equal(3, result.Plan.Blocks.Length);
        Assert.Equal(
            CilStackValueKind.Int64,
            Assert.Single(result.Plan.Locals, local => local.Name == "consumed").Kind);
        Assert.All(
            CilWellKnownCalls.All.Where(call => call.Id.StartsWith(
                "LuaCompiledExit.",
                StringComparison.Ordinal)),
            call => Assert.Equal(CilStackValueKind.Int64, call.ParameterKinds[1]));
    }

    [Fact]
    public void LowersNonNumericOpcodesThroughRuntimeAbiV3WithoutInterpreterSlowPath()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.NewTable, a: 0),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));

        var result = LuaCilCodeGenerator.PlanFunction(module, 0);

        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(static d => d.Message)));
        Assert.Contains(result.Plan!.Instructions, instruction =>
            instruction.CallTarget?.Id == "LuaCodegenAbiV3.ExecuteNewTable");
        Assert.DoesNotContain(result.Plan.Instructions, instruction =>
            instruction.CanonicalProgramCounter == 0 &&
            instruction.CallTarget?.Id == "LuaCodegenAbiV1.ExecuteCanonicalInstruction");
        Assert.Contains(0, result.Plan.DirectCanonicalProgramCounters);
        Assert.DoesNotContain(result.Plan.Instructions, instruction =>
            instruction.CanonicalProgramCounter == 0 &&
            instruction.CallTarget?.Id == "LuaCompiledExit.Deopt");
    }

    [Fact]
    public void LowersOrdinaryCallsThroughTheInMethodFramelessFastPath()
    {
        var module = CreateModule(
            registerCount: 2,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.Call, a: 0, b: 1, c: 1),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));

        var result = LuaCilCodeGenerator.PlanFunction(module, 0);

        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(static d => d.Message)));
        Assert.Contains(result.Plan!.Instructions, instruction =>
            instruction.CallTarget?.Id == "LuaCodegenAbiV3.TryExecuteFramelessCall");
        Assert.Contains(result.Plan.Instructions, instruction =>
            instruction.CallTarget?.Id == "LuaCompiledExit.Call");
        Assert.Equal(
            CilStackValueKind.Int64,
            Assert.Single(result.Plan.Locals, local => local.Name == "framelessConsumed").Kind);
        Assert.Contains(result.Plan.Instructions, instruction =>
            instruction.OpCode == CilPlanOpCode.ConvertInt64);
    }

    [Fact]
    public void RefusesUnverifiedCanonicalModulesBeforePlanning()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 0)) with
        {
            FormatVersion = int.MaxValue,
        };

        var result = LuaCilCodeGenerator.PlanFunction(module, 0);

        Assert.False(result.Succeeded);
        Assert.Null(result.Plan);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CIL1001");
    }

    [Fact]
    public void ReusesTheOwnerScopedPlanAndClearsCacheHitMetrics()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 0));

        var first = LuaCilCodeGenerator.PlanFunction(
            module,
            0,
            includeInstructionObservation: false);
        var cached = LuaCilCodeGenerator.PlanFunction(
            module,
            0,
            includeInstructionObservation: false);

        Assert.True(first.Succeeded);
        Assert.Same(first.Plan, cached.Plan);
        Assert.Equal(default, cached.Metrics);
    }

    [Fact]
    public void CanceledPlanningDoesNotPopulateTheOwnerScopedPlanCache()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 0));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => LuaCilCodeGenerator.PlanFunction(
            module,
            0,
            includeInstructionObservation: false,
            cancellationToken: cancellation.Token));

        var first = LuaCilCodeGenerator.PlanFunction(
            module,
            0,
            includeInstructionObservation: false);
        var cached = LuaCilCodeGenerator.PlanFunction(
            module,
            0,
            includeInstructionObservation: false);

        Assert.True(first.Succeeded);
        Assert.NotEqual(default, first.Metrics);
        Assert.Same(first.Plan, cached.Plan);
        Assert.Equal(default, cached.Metrics);
    }

    [Fact]
    public async Task ConcurrentFirstUseBuildsTheOwnerScopedPlanOnlyOnce()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 0));
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                return LuaCilCodeGenerator.PlanFunction(
                    module,
                    0,
                    includeInstructionObservation: false);
            }))
            .ToArray();

        start.Set();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, static result => Assert.True(result.Succeeded));
        Assert.All(results, result => Assert.Same(results[0].Plan, result.Plan));
    }

    [Fact]
    public void OwnerScopedPlanCacheDoesNotKeepTheModuleAlive()
    {
        var moduleReference = CreateCachedModuleWeakReference();

        for (var attempt = 0; attempt < 10 && moduleReference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(moduleReference.IsAlive);
    }

    [Fact]
    public void ComputesRegisterLivenessAndSafePointMaps()
    {
        var module = CreateModule(
            registerCount: 2,
            constants: [LuaIrConstant.FromString("value"u8)],
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 0, b: 0),
            new LuaIrInstruction(LuaIrOpcode.Move, a: 1, b: 0),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 1, b: 1));

        var result = LuaRegisterLiveness.Analyze(module, module.Functions[0]);

        Assert.Empty(result.LiveBefore[0]);
        Assert.True(result.LiveBefore[1].SequenceEqual([0]));
        Assert.True(result.LiveBefore[2].SequenceEqual([1]));
        Assert.True(result.GcMaps
            .Select(static map => map.CanonicalProgramCounter)
            .SequenceEqual([0, 2]));
    }

    [Fact]
    public void OwnerScopedLivenessCacheReusesAnalysisWithoutKeepingModuleAlive()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 0));

        var first = LuaRegisterLiveness.AnalyzeCached(
            module,
            module.Functions[0],
            out var firstHit);
        var second = LuaRegisterLiveness.AnalyzeCached(
            module,
            module.Functions[0],
            out var secondHit);

        Assert.False(firstHit);
        Assert.True(secondHit);
        Assert.Same(first, second);

        var moduleReference = CreateLivenessCachedModuleWeakReference();
        for (var attempt = 0; attempt < 10 && moduleReference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(moduleReference.IsAlive);
    }

    [Fact]
    public void RejectsEvaluationStackUnderflow()
    {
        var plan = MinimalPlan(
            CilPlanInstruction.Simple(CilPlanOpCode.Return));

        var result = CilMethodPlanVerifier.Verify(plan);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CIL0020");
    }

    [Fact]
    public void RejectsIncompatibleMergeStacks()
    {
        var merge = new CilLabel(1);
        var plan = MinimalPlan(
            CilPlanInstruction.WithInt32(CilPlanOpCode.LoadInt32, 1),
            CilPlanInstruction.WithLabel(CilPlanOpCode.BranchTrue, merge),
            CilPlanInstruction.WithInt32(CilPlanOpCode.LoadInt32, 2),
            CilPlanInstruction.MarkLabel(merge),
            CilPlanInstruction.WithInt32(CilPlanOpCode.LoadInt32, 0),
            CilPlanInstruction.Call(CilWellKnownCalls.ExitReturn),
            CilPlanInstruction.Simple(CilPlanOpCode.Return));

        var result = CilMethodPlanVerifier.Verify(plan);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CIL0004");
    }

    [Fact]
    public void RejectsReachableUndefinedBranchTargets()
    {
        var plan = MinimalPlan(
            CilPlanInstruction.WithLabel(CilPlanOpCode.Branch, new CilLabel(999)));

        var result = CilMethodPlanVerifier.Verify(plan);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CIL0019");
    }

    [Fact]
    public void RequiresGcMapsAndSpilledValuesAtSafePoints()
    {
        var target = new CilCallTarget(
            "safe",
            [CilStackValueKind.Int32],
            CilStackValueKind.Void,
            IsGcSafePoint: true);
        var plan = MinimalPlan(
            CilPlanInstruction.WithInt32(CilPlanOpCode.LoadInt32, 1),
            CilPlanInstruction.Call(target, canonicalProgramCounter: 3),
            CilPlanInstruction.WithInt32(CilPlanOpCode.LoadInt32, 0),
            CilPlanInstruction.WithInt32(CilPlanOpCode.LoadInt32, 0),
            CilPlanInstruction.Call(CilWellKnownCalls.ExitReturn),
            CilPlanInstruction.Simple(CilPlanOpCode.Return));

        var result = CilMethodPlanVerifier.Verify(plan);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CIL0016");
    }

    [Fact]
    public void RejectsUnspilledLuaValuesAcrossSafePoints()
    {
        var target = new CilCallTarget(
            "safe",
            [CilStackValueKind.Int32],
            CilStackValueKind.Void,
            IsGcSafePoint: true);
        var plan = MinimalPlan(
            CilPlanInstruction.WithInt32(CilPlanOpCode.LoadArgument, 0),
            CilPlanInstruction.WithInt32(CilPlanOpCode.LoadInt32, 1),
            CilPlanInstruction.Call(target, canonicalProgramCounter: 3),
            CilPlanInstruction.WithInt32(CilPlanOpCode.LoadInt32, 0),
            CilPlanInstruction.WithInt32(CilPlanOpCode.LoadInt32, 0),
            CilPlanInstruction.Call(CilWellKnownCalls.ExitReturn),
            CilPlanInstruction.Simple(CilPlanOpCode.Return)) with
        {
            ParameterKinds = [CilStackValueKind.LuaValue],
            GcMaps = [new CilGcMap(3, [0])],
        };

        var result = CilMethodPlanVerifier.Verify(plan);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CIL0015");
    }

    [Fact]
    public void EnforcesPlanSizeLimitsBeforeGraphTraversal()
    {
        var plan = MinimalPlan(
            CilPlanInstruction.Simple(CilPlanOpCode.Nop),
            CilPlanInstruction.Simple(CilPlanOpCode.Nop));

        var result = CilMethodPlanVerifier.Verify(plan, CilPlanLimits.Default with
        {
            MaximumInstructions = 1,
        });

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CIL0001");
    }

    [Fact]
    public void RejectsOversizedCanonicalFunctionsBeforePlanning()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.LoadNil, a: 0),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));

        var result = LuaCilCodeGenerator.PlanFunction(module, 0, CilPlanLimits.Default with
        {
            MaximumInstructions = 1,
        });

        Assert.False(result.Succeeded);
        Assert.Null(result.Plan);
        Assert.Null(result.Verification);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CIL0001");
    }

    [Fact]
    public void RejectsTamperedRuntimeAbiSignatures()
    {
        var tampered = CilWellKnownCalls.ExitReturn with
        {
            ParameterKinds = [CilStackValueKind.Int32],
        };
        var plan = MinimalPlan(
            CilPlanInstruction.WithInt32(CilPlanOpCode.LoadInt32, 0),
            CilPlanInstruction.Call(tampered),
            CilPlanInstruction.Simple(CilPlanOpCode.Return));

        var result = CilMethodPlanVerifier.Verify(plan);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CIL0024");
    }

    [Fact]
    public void RuntimeEmitterConsumesTheVerifiedPlan()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [LuaIrConstant.FromInteger(1)],
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 0, b: 0),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));
        var plan = LuaCilCodeGenerator.PlanFunction(module, 0).Plan!;
        var sink = new RecordingSink(CilEmitterFlavor.ReflectionEmit);

        var result = CilPlanEmitter.Emit(plan, sink);

        Assert.True(result.Succeeded);
        Assert.True(plan.Instructions.SequenceEqual(sink.Instructions));
        Assert.True(sink.MaximumStack > 0);
    }

    [Fact]
    public void EmissionCancellationDoesNotFinalizeTheInstructionSink()
    {
        var canonicalInstructions = Enumerable.Repeat(
                new LuaIrInstruction(LuaIrOpcode.Move, a: 0, b: 0),
                16)
            .Append(new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 0))
            .ToArray();
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            canonicalInstructions);
        var planning = LuaCilCodeGenerator.PlanFunction(module, 0);
        Assert.True(planning.Succeeded);
        Assert.True(planning.Plan!.Instructions.Length > 64);
        using var cancellation = new CancellationTokenSource();
        var sink = new CancelingSink(cancellation);

        Assert.Throws<OperationCanceledException>(() => CilPlanEmitter.EmitVerified(
            planning.Plan,
            sink,
            planning.Verification!,
            cancellation.Token));

        Assert.False(sink.Finalized);
    }

    [Fact]
    public void ReflectionEmitterExecutesThePlannedSubsetAgainstRuntimeAbiV1()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        var module = CreateModule(
            registerCount: 2,
            constants: [LuaIrConstant.FromInteger(42)],
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 0, b: 0),
            new LuaIrInstruction(LuaIrOpcode.Move, a: 1, b: 0),
            new LuaIrInstruction(LuaIrOpcode.JumpIfFalse, a: 1, b: 4),
            new LuaIrInstruction(LuaIrOpcode.SetTop, a: 2),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));
        var plan = LuaCilCodeGenerator.PlanFunction(module, 0).Plan!;
        var emission = ReflectionEmitCilPlanSink.Compile(plan);
        var referenceState = new LuaState();
        var reference = new LuaInterpreter().Execute(
            referenceState,
            referenceState.CreateMainClosure(module));
        var state = new LuaState();
        var thread = state.MainThread;
        var frame = new LuaFrame(
            state.CreateMainClosure(module),
            @base: 0,
            top: 0,
            returnBase: 0,
            expectedResults: 0,
            varArgs: []);
        var context = new LuaExecutionContext(state, thread, remainingInstructionCount: 100);

        var exit = emission.Method!(context, thread, frame);

        Assert.True(emission.Succeeded, string.Join("; ", emission.Diagnostics.Select(static d => d.Message)));
        Assert.Equal(
            plan.CanonicalInstructionCount,
            plan.DirectCanonicalInstructionCount + plan.SlowPathCanonicalInstructionCount);
        Assert.Equal(plan.CanonicalInstructionCount, plan.DirectCanonicalInstructionCount);
        Assert.True(emission.Metrics.PlanVerificationDuration >= TimeSpan.Zero);
        Assert.True(emission.Metrics.EmissionDuration >= TimeSpan.Zero);
        Assert.True(emission.Metrics.DelegateCreationDuration >= TimeSpan.Zero);
        Assert.Equal(LuaCompiledExitKind.Return, exit.Kind);
        Assert.Equal(4, exit.ProgramCounter);
        Assert.Equal(5, exit.InstructionsConsumed);
        Assert.Equal(LuaValue.FromInteger(42), thread.Stack[0]);
        Assert.Equal(LuaValue.FromInteger(42), thread.Stack[1]);
        Assert.True(reference.Values.SequenceEqual([thread.Stack[0]]));
    }

    [Fact]
    public void ReflectionEmitterPreservesInstructionCountsBeyondInt32()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        const long expected = (long)int.MaxValue + 10;
        var plan = new CilMethodPlan
        {
            Name = "long_instruction_count",
            FunctionId = 0,
            ParameterKinds =
            [
                CilStackValueKind.ExecutionContext,
                CilStackValueKind.Thread,
                CilStackValueKind.Frame,
            ],
            ReturnKind = CilStackValueKind.CompiledExit,
            Instructions =
            [
                CilPlanInstruction.WithInt32(CilPlanOpCode.LoadInt32, 7),
                CilPlanInstruction.WithInt64(CilPlanOpCode.LoadInt64, int.MaxValue),
                CilPlanInstruction.WithInt64(CilPlanOpCode.LoadInt64, 10),
                CilPlanInstruction.Simple(CilPlanOpCode.Add),
                CilPlanInstruction.Call(CilWellKnownCalls.ExitContinue),
                CilPlanInstruction.Simple(CilPlanOpCode.Return),
            ],
        };
        var emission = ReflectionEmitCilPlanSink.Compile(plan);
        Assert.True(emission.Succeeded, string.Join("; ", emission.Diagnostics.Select(
            static diagnostic => diagnostic.Message)));
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 0));
        var state = new LuaState();
        var thread = state.MainThread;
        var frame = new LuaFrame(
            state.CreateMainClosure(module),
            @base: 0,
            top: 0,
            returnBase: 0,
            expectedResults: 0,
            varArgs: []);
        var context = new LuaExecutionContext(state, thread, remainingInstructionCount: expected);

        var exit = emission.Method!(context, thread, frame);

        Assert.Equal(LuaCompiledExitKind.Continue, exit.Kind);
        Assert.Equal(7, exit.ProgramCounter);
        Assert.Equal(expected, exit.InstructionsConsumed);
    }

    [Fact]
    public void ReflectionEmitterReturnsBudgetPollBeforeExecutingACanonicalInstruction()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        var module = CreateModule(
            registerCount: 1,
            constants: [LuaIrConstant.FromInteger(1)],
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 0, b: 0),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));
        var method = ReflectionEmitCilPlanSink.Compile(
            LuaCilCodeGenerator.PlanFunction(module, 0).Plan!).Method!;
        var state = new LuaState();
        var thread = state.MainThread;
        var frame = new LuaFrame(
            state.CreateMainClosure(module),
            @base: 0,
            top: 0,
            returnBase: 0,
            expectedResults: 0,
            varArgs: []);
        var context = new LuaExecutionContext(state, thread, remainingInstructionCount: 0);

        var exit = method(context, thread, frame);

        Assert.Equal(LuaCompiledExitKind.Poll, exit.Kind);
        Assert.Equal(LuaCompiledExitReason.InstructionBudget, exit.Reason);
        Assert.Equal(0, exit.ProgramCounter);
        Assert.Equal(0, exit.InstructionsConsumed);
        Assert.Equal(LuaValue.Nil, thread.Stack[0]);
    }

    [Fact]
    public void AbiV2DirectSegmentStopsAtEveryExactBudgetBoundary()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        var module = CreateModule(
            registerCount: 5,
            constants:
            [
                LuaIrConstant.FromInteger(5),
                LuaIrConstant.FromInteger(3),
            ],
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 0, b: 0),
            new LuaIrInstruction(
                LuaIrOpcode.Unary,
                a: 1,
                b: 0,
                c: (int)LuaIrUnaryOperator.Negate),
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 2, b: 1),
            new LuaIrInstruction(
                LuaIrOpcode.Binary,
                a: 3,
                b: 1,
                c: 2,
                d: (int)LuaIrBinaryOperator.Add),
            new LuaIrInstruction(LuaIrOpcode.Move, a: 4, b: 3),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 4, b: 1));
        var method = ReflectionEmitCilPlanSink.Compile(
            LuaCilCodeGenerator.PlanFunction(
                module,
                0,
                includeInstructionObservation: false).Plan!).Method!;

        for (var remaining = 0; remaining <= 6; remaining++)
        {
            var state = new LuaState();
            var thread = state.MainThread;
            var frame = new LuaFrame(
                state.CreateMainClosure(module),
                @base: 0,
                top: 0,
                returnBase: 0,
                expectedResults: 0,
                varArgs: []);
            var context = new LuaExecutionContext(
                state,
                thread,
                remainingInstructionCount: remaining);

            var exit = method(context, thread, frame);

            if (remaining < 6)
            {
                Assert.Equal(LuaCompiledExitKind.Poll, exit.Kind);
                Assert.Equal(LuaCompiledExitReason.InstructionBudget, exit.Reason);
                Assert.Equal(remaining, exit.ProgramCounter);
                Assert.Equal(remaining, exit.InstructionsConsumed);
                Assert.Equal(remaining, frame.ProgramCounter);
            }
            else
            {
                Assert.Equal(LuaCompiledExitKind.Return, exit.Kind);
                Assert.Equal(5, exit.ProgramCounter);
                Assert.Equal(6, exit.InstructionsConsumed);
                Assert.Equal(LuaValue.FromInteger(-2), thread.Stack[4]);
            }
        }
    }

    [Fact]
    public void MalformedPlanFuzzingNeverEscapesTheVerifier()
    {
        var random = new Random(1);
        var opcodes = Enum.GetValues<CilPlanOpCode>();
        for (var pass = 0; pass < 250; pass++)
        {
            var instructions = Enumerable.Range(0, random.Next(1, 80))
                .Select(_ => CilPlanInstruction.WithInt32(
                    opcodes[random.Next(opcodes.Length)],
                    random.Next(-5, 20)))
                .ToImmutableArray();
            var plan = MinimalPlan(instructions.ToArray());

            var exception = Record.Exception(() => CilMethodPlanVerifier.Verify(plan));

            Assert.Null(exception);
        }
    }

    private static LuaJitModuleProfile TrainProfile(
        LuaIrModule module,
        params LuaValue[] arguments)
    {
        using var executor = new LuaJitExecutor(LuaJitExecutorOptions.Default with
        {
            FunctionEntryThreshold = 1,
            BackedgeThreshold = 1,
            EnableLoopOsr = false,
            Tier2InvocationThreshold = int.MaxValue,
            Tier2BackedgeThreshold = int.MaxValue,
            SynchronousCompilation = true,
        });
        var state = new LuaState();
        var closure = state.CreateMainClosure(module);
        _ = executor.Execute(state, closure, arguments);
        _ = executor.Execute(state, closure, arguments);
        return LuaJitProfileCodec.Deserialize(module, executor.ExportProfile(module));
    }

    private static LuaIrModule CreateModule(
        int registerCount,
        ImmutableArray<LuaIrConstant> constants,
        params LuaIrInstruction[] instructions)
    {
        var immutableInstructions = instructions.ToImmutableArray();
        return new LuaIrModule
        {
            MainFunctionId = 0,
            Functions =
            [
                new LuaIrFunction
                {
                    Id = 0,
                    Span = new TextSpan(0, 0),
                    RegisterCount = registerCount,
                    Constants = constants,
                    Instructions = immutableInstructions,
                    BasicBlocks = LuaIrControlFlow.Build(immutableInstructions),
                },
            ],
        };
    }

    private static LuaIrModule CompileSource(string source)
    {
        var parsing = LuaParser.Parse(SourceText.FromUtf8(source));
        var binding = LuaBinder.Bind(parsing);
        var lowering = LuaLowerer.Lower(binding);
        Assert.True(
            lowering.Succeeded,
            string.Join("; ", lowering.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        return Assert.IsType<LuaIrModule>(lowering.Module);
    }

    private static CilMethodPlan MinimalPlan(params CilPlanInstruction[] instructions) => new()
    {
        Name = "test",
        FunctionId = 0,
        ReturnKind = CilStackValueKind.CompiledExit,
        Instructions = instructions.ToImmutableArray(),
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateCachedModuleWeakReference()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 0));
        var result = LuaCilCodeGenerator.PlanFunction(
            module,
            0,
            includeInstructionObservation: false);
        Assert.True(result.Succeeded);
        return new WeakReference(module);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateLivenessCachedModuleWeakReference()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 0));
        _ = LuaRegisterLiveness.AnalyzeCached(
            module,
            module.Functions[0],
            out _);
        return new WeakReference(module);
    }

    private sealed class RecordingSink : ICilInstructionSink
    {
        public RecordingSink(CilEmitterFlavor flavor)
        {
            Flavor = flavor;
        }

        public CilEmitterFlavor Flavor { get; }

        public List<CilPlanInstruction> Instructions { get; } = [];

        public int MaximumStack { get; private set; }

        public void BeginMethod(CilMethodPlan plan, int maximumEvaluationStack)
        {
            MaximumStack = maximumEvaluationStack;
        }

        public void DeclareLocal(CilLocal local)
        {
        }

        public void Emit(CilPlanInstruction instruction) => Instructions.Add(instruction);

        public void EndMethod()
        {
        }
    }

    private sealed class CancelingSink(CancellationTokenSource cancellation) :
        ICilInstructionSink
    {
        private int _emitted;

        public CilEmitterFlavor Flavor => CilEmitterFlavor.ReflectionEmit;

        public bool Finalized { get; private set; }

        public void BeginMethod(CilMethodPlan plan, int maximumEvaluationStack)
        {
        }

        public void DeclareLocal(CilLocal local)
        {
        }

        public void Emit(CilPlanInstruction instruction)
        {
            if (Interlocked.Increment(ref _emitted) == 1)
            {
                cancellation.Cancel();
            }
        }

        public void EndMethod() => Finalized = true;
    }
}
