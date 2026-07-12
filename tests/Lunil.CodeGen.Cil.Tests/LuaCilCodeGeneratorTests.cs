using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Lunil.CodeGen.Cil.Analysis;
using Lunil.CodeGen.Cil.Emission;
using Lunil.CodeGen.Cil.Planning;
using Lunil.CodeGen.Cil.Verification;
using Lunil.Core.Text;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

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
    }

    [Fact]
    public void EmitsExplicitDeoptimizationForUnsupportedOpcodes()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.NewTable, a: 0),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));

        var result = LuaCilCodeGenerator.PlanFunction(module, 0);

        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(static d => d.Message)));
        Assert.Contains(result.Plan!.Instructions, instruction =>
            instruction.CallTarget?.Id == "LuaCompiledExit.Deopt");
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
    public void BothEmitterFlavorsConsumeTheSameVerifiedPlan()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [LuaIrConstant.FromInteger(1)],
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 0, b: 0),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));
        var plan = LuaCilCodeGenerator.PlanFunction(module, 0).Plan!;
        var reflection = new RecordingSink(CilEmitterFlavor.ReflectionEmit);
        var metadata = new RecordingSink(CilEmitterFlavor.Metadata);

        var reflectionResult = CilPlanEmitter.Emit(plan, reflection);
        var metadataResult = CilPlanEmitter.Emit(plan, metadata);

        Assert.True(reflectionResult.Succeeded);
        Assert.True(metadataResult.Succeeded);
        Assert.Equal(reflection.Instructions, metadata.Instructions);
        Assert.Equal(reflection.MaximumStack, metadata.MaximumStack);
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
        Assert.Equal(LuaCompiledExitKind.Return, exit.Kind);
        Assert.Equal(4, exit.ProgramCounter);
        Assert.Equal(5, exit.InstructionsConsumed);
        Assert.Equal(LuaValue.FromInteger(42), thread.Stack[0]);
        Assert.Equal(LuaValue.FromInteger(42), thread.Stack[1]);
        Assert.True(reference.Values.SequenceEqual([thread.Stack[0]]));
    }

    [Fact]
    public void MetadataRecipeIsDeterministicAndPreservesTheVerifiedPlan()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [LuaIrConstant.FromInteger(1)],
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 0, b: 0),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));
        var plan = LuaCilCodeGenerator.PlanFunction(module, 0).Plan!;

        var first = MetadataCilPlanSink.CreateRecipe(plan);
        var second = MetadataCilPlanSink.CreateRecipe(plan);

        Assert.True(first.Verification.Succeeded);
        Assert.Equal(first.Recipe!.MethodName, second.Recipe!.MethodName);
        Assert.Equal(first.Recipe.MaximumEvaluationStack, second.Recipe.MaximumEvaluationStack);
        Assert.True(first.Recipe.Locals.SequenceEqual(second.Recipe.Locals));
        Assert.True(first.Recipe.Instructions.SequenceEqual(second.Recipe.Instructions));
        Assert.True(first.Recipe!.Instructions.SequenceEqual(plan.Instructions));
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

    private static CilMethodPlan MinimalPlan(params CilPlanInstruction[] instructions) => new()
    {
        Name = "test",
        FunctionId = 0,
        ReturnKind = CilStackValueKind.CompiledExit,
        Instructions = instructions.ToImmutableArray(),
    };

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
}
