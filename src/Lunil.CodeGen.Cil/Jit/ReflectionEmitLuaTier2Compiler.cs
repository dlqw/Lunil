using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Lunil.CodeGen.Cil.Emission;
using Lunil.IR.Canonical;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.CodeGen.Cil.Jit;

internal readonly record struct LuaTier2EmissionMetrics(
    TimeSpan CilEmissionDuration,
    TimeSpan DelegateCreationDuration);

internal enum LuaTier2NumericSpecializationStatus : byte
{
    Eligible,
    NoNumericHotspot,
    PolymorphicNumericProfile,
    ManagedOptimizationRequired,
    UnsupportedInstruction,
    InsufficientTier2Work,
    HotLoopCallBoundary,
}

internal static class ReflectionEmitLuaTier2Compiler
{
    private delegate LuaCompiledExit LuaCompiledMethodWithSites(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        LuaTier2RuntimeSites sites);

    private static readonly Type[] CompiledMethodParameters =
    [
        typeof(LuaExecutionContext),
        typeof(LuaThread),
        typeof(LuaFrame),
        typeof(LuaTier2RuntimeSites),
    ];

    // Method tokens are obtained by creating a delegate of the exact signature and reading its
    // Method property, so overload selection happens at compile time and no reflection lookup is
    // required. LuaTier2RuntimeSites instance-method tokens bind through a method group over a
    // permanent zero-capacity binding instance whose delegates are never invoked.
    private delegate LuaCodegenPicExecutionResult CompilerProvenTablePicSignature(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        ref LuaTable? cachedTable,
        LuaCodegenTableSiteCache cache,
        int targetRegister,
        int keyRegister,
        int valueRegister);

    private static readonly LuaTier2RuntimeSites TokenBindingSites =
        new(0, null, new LuaJitModuleIdentity());

    private static readonly MethodInfo CanExecuteCompiledFrame =
        ((Func<LuaExecutionContext, LuaFrame, int, int, bool>)LuaCodegenAbiV2.CanExecuteCompiledFrame).Method;
    private static readonly MethodInfo ReadRegister =
        ((Func<LuaThread, LuaFrame, int, LuaValue>)LuaCodegenAbiV2.ReadRegisterUnchecked).Method;
    private static readonly MethodInfo WriteRegister =
        ((Action<LuaThread, LuaFrame, int, LuaValue>)LuaCodegenAbiV2.WriteRegisterUnchecked).Method;
    private static readonly MethodInfo ClearRegisters =
        ((Action<LuaThread, LuaFrame, int, int>)LuaCodegenAbiV2.ClearRegistersUnchecked).Method;
    private static readonly MethodInfo SetFrameTop =
        ((Action<LuaThread, LuaFrame, int>)LuaCodegenAbiV2.SetFrameTopUnchecked).Method;
    private static readonly MethodInfo ReadTruthyAndSetFrameTop =
        ((Func<LuaThread, LuaFrame, int, int, bool>)LuaCodegenAbiV2.ReadTruthyAndSetFrameTopUnchecked).Method;
    private static readonly MethodInfo ExecuteNumericForPrepare =
        ((Action<LuaThread, LuaFrame, int, int>)LuaCodegenAbiV2.ExecuteNumericForPrepare).Method;
    private static readonly MethodInfo ExecuteNumericForLoop =
        ((Action<LuaThread, LuaFrame, int, int>)LuaCodegenAbiV2.ExecuteNumericForLoop).Method;
    private static readonly MethodInfo MaterializeConstant =
        ((Func<LuaExecutionContext, LuaFrame, int, LuaValue>)LuaCodegenAbiV1.MaterializeConstant).Method;
    private static readonly MethodInfo ReadUpvalue =
        ((Func<LuaFrame, int, LuaValue>)LuaCodegenAbiV1.ReadUpvalue).Method;
    private static readonly MethodInfo WriteUpvalue =
        ((Action<LuaFrame, int, LuaValue>)LuaCodegenAbiV1.WriteUpvalue).Method;
    private static readonly MethodInfo ExecuteNewTable =
        ((Action<LuaExecutionContext, LuaThread, LuaFrame, int, int, int>)LuaCodegenAbiV3.ExecuteNewTable).Method;
    private static readonly MethodInfo ExecuteGetTable =
        ((Func<LuaExecutionContext, LuaThread, LuaFrame, int, int, int, bool>)LuaCodegenAbiV3.ExecuteGetTable).Method;
    private static readonly MethodInfo ExecuteSetTable =
        ((Func<LuaExecutionContext, LuaThread, LuaFrame, int, int, int, bool>)LuaCodegenAbiV3.ExecuteSetTable).Method;
    private static readonly MethodInfo ExecuteSetList =
        ((Action<LuaThread, LuaFrame, int, int, int, int>)LuaCodegenAbiV3.ExecuteSetList).Method;
    private static readonly MethodInfo ExecuteClosure =
        ((Action<LuaExecutionContext, LuaThread, LuaFrame, int, int>)LuaCodegenAbiV3.ExecuteClosure).Method;
    private static readonly MethodInfo ExecuteVarArg =
        ((Action<LuaThread, LuaFrame, int, int>)LuaCodegenAbiV3.ExecuteVarArg).Method;
    private static readonly MethodInfo TryExecuteTableGetPic =
        ((Func<LuaExecutionContext, LuaThread, LuaFrame, LuaCodegenTableSiteCache, int, int, int, LuaCodegenPicExecutionResult>)LuaCodegenAbiV3.TryExecuteTableGetPic).Method;
    private static readonly MethodInfo TryExecuteTableSetPic =
        ((Func<LuaExecutionContext, LuaThread, LuaFrame, LuaCodegenTableSiteCache, int, int, int, LuaCodegenPicExecutionResult>)LuaCodegenAbiV3.TryExecuteTableSetPic).Method;
    private static readonly MethodInfo TryExecuteCompilerProvenTableGetPic =
        ((CompilerProvenTablePicSignature)LuaCodegenAbiV3.TryExecuteCompilerProvenTableGetPic).Method;
    private static readonly MethodInfo TryExecuteCompilerProvenTableSetPic =
        ((CompilerProvenTablePicSignature)LuaCodegenAbiV3.TryExecuteCompilerProvenTableSetPic).Method;
    private static readonly MethodInfo TryExecuteCompilerProvenIntegerTableGetPic =
        ((CompilerProvenTablePicSignature)LuaCodegenAbiV3.TryExecuteCompilerProvenIntegerTableGetPic).Method;
    private static readonly MethodInfo TryExecuteCompilerProvenIntegerTableSetPic =
        ((CompilerProvenTablePicSignature)LuaCodegenAbiV3.TryExecuteCompilerProvenIntegerTableSetPic).Method;
    private static readonly MethodInfo CanExecuteKnownClosureCall =
        ((Func<LuaThread, LuaFrame, LuaCodegenCallSiteCache, int, int, bool>)LuaCodegenAbiV3.CanExecuteKnownClosureCall).Method;
    private static readonly MethodInfo TryExecuteDirectCompiledCall =
        ((Func<LuaExecutionContext, LuaThread, LuaFrame, LuaCodegenCallSiteCache, int, int, int, int, bool>)LuaCodegenAbiV4.TryExecuteDirectCompiledCall).Method;
    private static readonly MethodInfo TryExecuteFramelessCall =
        ((Func<LuaExecutionContext, LuaThread, LuaFrame, int, int, int, int>)LuaCodegenAbiV3.TryExecuteFramelessCall).Method;
    private static readonly MethodInfo CanContinueAfterFramelessCall =
        ((Func<LuaExecutionContext, LuaThread, LuaFrame, bool>)LuaCodegenAbiV3.CanContinueAfterFramelessCall).Method;
    private static readonly MethodInfo PollGcSafepoint =
        ((Func<LuaExecutionContext, LuaThread, LuaFrame, bool>)LuaCodegenAbiV3.PollGcSafepoint).Method;
    private static readonly MethodInfo ExecuteKnownClosureCall =
        ((Action<LuaExecutionContext, LuaThread, LuaFrame, int, int, int>)LuaCodegenAbiV3.ExecuteKnownClosureCall).Method;
    private static readonly MethodInfo ExecuteKnownClosureTailCall =
        ((Action<LuaExecutionContext, LuaThread, LuaFrame, int, int>)LuaCodegenAbiV3.ExecuteKnownClosureTailCall).Method;
    private static readonly MethodInfo GetTableSite =
        ((Func<int, LuaCodegenTableSiteCache>)TokenBindingSites.GetTableSite).Method;
    private static readonly MethodInfo GetCallSite =
        ((Func<int, string, LuaCodegenCallSiteCache>)TokenBindingSites.GetCallSite).Method;
    private static readonly MethodInfo RecordInlineDirectCallCompletion =
        ((Action)TokenBindingSites.RecordInlineDirectCallCompletion).Method;
    private static readonly MethodInfo RecordInlineDirectCallFallback =
        ((Action)TokenBindingSites.RecordInlineDirectCallFallback).Method;
    private static readonly MethodInfo CanSkipClose =
        ((Func<LuaThread, LuaFrame, int, bool>)LuaCodegenAbiV2.CanSkipClose).Method;
    private static readonly MethodInfo CanExecuteUnaryPrimitive =
        ((Func<LuaThread, LuaFrame, int, int, bool>)LuaCodegenAbiV2.CanExecuteUnaryPrimitive).Method;
    private static readonly MethodInfo ExecuteUnaryPrimitive =
        ((Action<LuaExecutionContext, LuaThread, LuaFrame, int, int, int>)LuaCodegenAbiV2.ExecuteUnaryPrimitive).Method;
    private static readonly MethodInfo CanExecuteBinaryPrimitive =
        ((Func<LuaThread, LuaFrame, int, int, int, bool>)LuaCodegenAbiV2.CanExecuteBinaryPrimitive).Method;
    private static readonly MethodInfo ExecuteBinaryPrimitive =
        ((Action<LuaExecutionContext, LuaThread, LuaFrame, int, int, int, int>)LuaCodegenAbiV2.ExecuteBinaryPrimitive).Method;
    // Instance method on LuaExecutionContext: the context constructor needs a live LuaState and
    // LuaThread, so no receiver exists at type-initialization time. Keep the reflection lookup.
    private static readonly MethodInfo ReserveInstructions = Method(
        typeof(LuaExecutionContext),
        nameof(LuaExecutionContext.TryReserveInstructions),
        [typeof(int)]);
    private static readonly MethodInfo GetInstructionsConsumed = PropertyGetter(
        typeof(LuaExecutionContext),
        "InstructionsConsumed");
    private static readonly MethodInfo GetProgramCounter = PropertyGetter(
        typeof(LuaFrame),
        nameof(LuaFrame.ProgramCounter));
    private static readonly MethodInfo SetProgramCounter = PropertySetter(
        typeof(LuaFrame),
        nameof(LuaFrame.ProgramCounter));
    private static readonly MethodInfo GetValueKind = PropertyGetter(
        typeof(LuaValue),
        nameof(LuaValue.Kind));
    private static readonly MethodInfo IsTruthy = PropertyGetter(
        typeof(LuaValue),
        nameof(LuaValue.IsTruthy));
    private static readonly MethodInfo UnaryInteger =
        ((Func<LuaIrUnaryOperator, LuaValue, LuaValue>)LuaValueOperations.UnaryIntegerSpecialized).Method;
    private static readonly MethodInfo UnaryFloat =
        ((Func<LuaIrUnaryOperator, LuaValue, LuaValue>)LuaValueOperations.UnaryFloatSpecialized).Method;
    private static readonly MethodInfo GenericUnary =
        ((Func<LuaIrUnaryOperator, LuaValue, LuaValue>)LuaValueOperations.Unary).Method;
    private static readonly MethodInfo BinaryInteger =
        ((Func<LuaIrBinaryOperator, LuaValue, LuaValue, LuaValue>)LuaValueOperations.BinaryIntegerSpecialized).Method;
    private static readonly MethodInfo BinaryFloat =
        ((Func<LuaIrBinaryOperator, LuaValue, LuaValue, LuaValue>)LuaValueOperations.BinaryFloatSpecialized).Method;
    private static readonly MethodInfo BinaryMixedNumeric =
        ((Func<LuaIrBinaryOperator, LuaValue, LuaValue, LuaValue>)LuaValueOperations.BinaryMixedNumericSpecialized).Method;
    private static readonly MethodInfo BinaryPrimitive =
        ((Func<LuaIrBinaryOperator, LuaValue, LuaValue, LuaValue>)LuaValueOperations.BinaryPrimitiveSpecialized).Method;
    private static readonly MethodInfo PollExit =
        ((Func<int, long, LuaCompiledExitReason, LuaCompiledExit>)LuaCompiledExit.Poll).Method;
    private static readonly MethodInfo ReturnExit =
        ((Func<int, long, LuaCompiledExit>)LuaCompiledExit.Return).Method;
    private static readonly MethodInfo ContinueExit =
        ((Func<int, long, LuaCompiledExit>)LuaCompiledExit.Continue).Method;
    private static readonly MethodInfo CallExit =
        ((Func<int, long, LuaCompiledExit>)LuaCompiledExit.Call).Method;
    private static readonly MethodInfo TailCallExit =
        ((Func<int, long, LuaCompiledExit>)LuaCompiledExit.TailCall).Method;
    private static readonly MethodInfo DeoptExit =
        ((Func<int, long, LuaCompiledExitReason, LuaCompiledExit>)LuaCompiledExit.Deopt).Method;
    [RequiresDynamicCode("Tier 2 CIL specialization requires Reflection.Emit support.")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "The caller checks dynamic-code support before requesting Tier 2 compilation.")]
    public static bool TryCompile(
        LuaIrFunction function,
        ImmutableDictionary<int, ProfileGuidedLuaTier2Compiler.OptimizedInstruction> optimized,
        ImmutableHashSet<int> numericRegionProgramCounters,
        IReadOnlyDictionary<int, LuaBoundDirectCall> boundDirectCalls,
        LuaJitModuleIdentity moduleIdentity,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out LuaCompiledMethod? method,
        [NotNullWhen(true)] out LuaTier2RuntimeSites? runtimeSites,
        out long estimatedCodeBytes,
        out LuaTier2EmissionMetrics metrics)
    {
        method = null;
        runtimeSites = null;
        estimatedCodeBytes = 0;
        metrics = default;
        if (!RuntimeFeature.IsDynamicCodeSupported ||
            EvaluateNumericSpecialization(
                function,
                optimized,
                numericRegionProgramCounters) !=
                LuaTier2NumericSpecializationStatus.Eligible)
        {
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var emissionStarted = Stopwatch.GetTimestamp();
        var dynamicMethod = new DynamicMethod(
            $"lunil_jit2_numeric_f{function.Id}",
            typeof(LuaCompiledExit),
            CompiledMethodParameters,
            typeof(ReflectionEmitLuaTier2Compiler).Module,
            skipVisibility: true);
        var generator = dynamicMethod.GetILGenerator();
        var firstValue = generator.DeclareLocal(typeof(LuaValue));
        var secondValue = generator.DeclareLocal(typeof(LuaValue));
        var picExecutionResult = generator.DeclareLocal(typeof(LuaCodegenPicExecutionResult));
        var framelessConsumed = generator.DeclareLocal(typeof(long));
        var safepointCountdown = generator.DeclareLocal(typeof(int));
        var tableSiteLocals = optimized
            .Where(static pair => pair.Value.Kind is
                LuaJitOptimizationKind.TableGetPic or LuaJitOptimizationKind.TableSetPic)
            .ToDictionary(
                static pair => pair.Key,
                _ => generator.DeclareLocal(typeof(LuaCodegenTableSiteCache)));
        var tableDefinitionLocals = optimized.Values
            .Where(static optimization => optimization.TableDefinitionProgramCounter >= 0)
            .Select(static optimization => optimization.TableDefinitionProgramCounter)
            .Distinct()
            .ToDictionary(
                static programCounter => programCounter,
                _ => generator.DeclareLocal(typeof(LuaTable)));
        var labels = new Label[function.Instructions.Length];
        var budgetExits = new Label[function.Instructions.Length];
        var guardExits = new Label[function.Instructions.Length];
        var slowPathExits = new Label[function.Instructions.Length];
        for (var pc = 0; pc < labels.Length; pc++)
        {
            labels[pc] = generator.DefineLabel();
            budgetExits[pc] = generator.DefineLabel();
            guardExits[pc] = generator.DefineLabel();
            slowPathExits[pc] = generator.DefineLabel();
        }

        var debugExit = generator.DefineLabel();
        var invalidatedExit = generator.DefineLabel();
        EmitInt32(generator, LuaCodegenAbiV3.CompiledBackedgeSafepointQuantum);
        generator.Emit(OpCodes.Stloc, safepointCountdown);
        EmitEntryGuard(generator, function, debugExit);
        foreach (var (programCounter, tableSiteLocal) in tableSiteLocals)
        {
            generator.Emit(OpCodes.Ldarg_3);
            EmitInt32(generator, programCounter);
            generator.Emit(OpCodes.Callvirt, GetTableSite);
            generator.Emit(OpCodes.Stloc, tableSiteLocal);
        }

        generator.Emit(OpCodes.Ldarg_2);
        generator.Emit(OpCodes.Callvirt, GetProgramCounter);
        generator.Emit(OpCodes.Switch, labels);
        generator.Emit(OpCodes.Br, invalidatedExit);

        for (var pc = 0; pc < function.Instructions.Length; pc++)
        {
            if ((pc & 31) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            generator.MarkLabel(labels[pc]);
            if (numericRegionProgramCounters.Contains(pc))
            {
                EmitContinue(generator, pc);
                continue;
            }

            var instruction = function.Instructions[pc];
            if (optimized.TryGetValue(pc, out var optimization))
            {
                EmitOptimizedInstruction(
                    generator,
                    function,
                    pc,
                    instruction,
                    optimization,
                    boundDirectCalls,
                    tableSiteLocals,
                    tableDefinitionLocals,
                    labels,
                    budgetExits[pc],
                    guardExits[pc],
                    slowPathExits[pc],
                    invalidatedExit,
                    firstValue,
                    secondValue,
                    picExecutionResult,
                    framelessConsumed,
                    safepointCountdown,
                    cancellationToken);
            }
            else
            {
                EmitDirectInstruction(
                    generator,
                    function,
                    pc,
                    instruction,
                    tableDefinitionLocals,
                    labels,
                    budgetExits[pc],
                    slowPathExits[pc],
                    invalidatedExit,
                    firstValue,
                    safepointCountdown);
            }
        }

        for (var pc = 0; pc < function.Instructions.Length; pc++)
        {
            generator.MarkLabel(budgetExits[pc]);
            EmitExit(generator, PollExit, pc, LuaCompiledExitReason.InstructionBudget);
            generator.MarkLabel(guardExits[pc]);
            EmitExit(generator, DeoptExit, pc, LuaCompiledExitReason.GuardFailure);
            generator.MarkLabel(slowPathExits[pc]);
            EmitSlowPath(generator, pc);
        }

        generator.MarkLabel(debugExit);
        EmitFrameProgramCounterExit(
            generator,
            DeoptExit,
            LuaCompiledExitReason.DebugModeChanged);
        generator.MarkLabel(invalidatedExit);
        EmitFrameProgramCounterExit(
            generator,
            DeoptExit,
            LuaCompiledExitReason.BackendInvalidated);

        cancellationToken.ThrowIfCancellationRequested();
        var cilEmissionDuration = Stopwatch.GetElapsedTime(emissionStarted);
        var delegateStarted = Stopwatch.GetTimestamp();
        var compiledWithSites = (LuaCompiledMethodWithSites)dynamicMethod.CreateDelegate(
            typeof(LuaCompiledMethodWithSites));
        var createdRuntimeSites = new LuaTier2RuntimeSites(
            function.Instructions.Length,
            boundDirectCalls,
            moduleIdentity);
        runtimeSites = createdRuntimeSites;
        method = (context, thread, frame) =>
            compiledWithSites(context, thread, frame, createdRuntimeSites);
        var delegateCreationDuration = Stopwatch.GetElapsedTime(delegateStarted);
        estimatedCodeBytes = checked(
            (function.Instructions.Length - numericRegionProgramCounters.Count) * 32L +
            optimized.Count(pair => !numericRegionProgramCounters.Contains(pair.Key)) * 48L +
            numericRegionProgramCounters.Count * 8L);
        metrics = new LuaTier2EmissionMetrics(
            cilEmissionDuration,
            delegateCreationDuration);
        return true;
    }

    internal static LuaTier2NumericSpecializationStatus EvaluateNumericSpecialization(
        LuaIrFunction function,
        ImmutableDictionary<int, ProfileGuidedLuaTier2Compiler.OptimizedInstruction> optimized) =>
        EvaluateNumericSpecialization(function, optimized, []);

    internal static LuaTier2NumericSpecializationStatus EvaluateNumericSpecialization(
        LuaIrFunction function,
        ImmutableDictionary<int, ProfileGuidedLuaTier2Compiler.OptimizedInstruction> optimized,
        ImmutableHashSet<int> numericRegionProgramCounters)
    {
        var hasSpecializableHotspot = !numericRegionProgramCounters.IsEmpty;
        foreach (var pair in optimized)
        {
            var pc = pair.Key;
            if (numericRegionProgramCounters.Contains(pc))
            {
                continue;
            }

            var optimization = pair.Value;
            switch (optimization.Kind)
            {
                case LuaJitOptimizationKind.DeadMove:
                case LuaJitOptimizationKind.BooleanBranch:
                    break;
                case LuaJitOptimizationKind.NumericUnary:
                    var unaryOperation = (LuaIrUnaryOperator)function.Instructions[pc].C;
                    if (unaryOperation == LuaIrUnaryOperator.Length
                        ? optimization.FirstKinds != LuaJitValueKinds.String
                        : !IsExactNumericKind(optimization.FirstKinds) ||
                            unaryOperation is not (LuaIrUnaryOperator.Negate or
                                LuaIrUnaryOperator.BitwiseNot or
                                LuaIrUnaryOperator.LogicalNot))
                    {
                        return LuaTier2NumericSpecializationStatus.PolymorphicNumericProfile;
                    }

                    hasSpecializableHotspot = true;
                    break;
                case LuaJitOptimizationKind.NumericBinary:
                    if (!IsExactNumericKind(optimization.FirstKinds) ||
                        !IsExactNumericKind(optimization.SecondKinds))
                    {
                        return LuaTier2NumericSpecializationStatus.PolymorphicNumericProfile;
                    }

                    hasSpecializableHotspot = true;
                    break;
                case LuaJitOptimizationKind.PrimitiveBinary:
                    if (!IsExactPrimitiveKind(optimization.FirstKinds) ||
                        !IsExactPrimitiveKind(optimization.SecondKinds))
                    {
                        return LuaTier2NumericSpecializationStatus.PolymorphicNumericProfile;
                    }

                    hasSpecializableHotspot = true;
                    break;
                case LuaJitOptimizationKind.TableGetPic:
                case LuaJitOptimizationKind.TableSetPic:
                case LuaJitOptimizationKind.KnownClosureCall:
                    hasSpecializableHotspot = true;
                    break;
                default:
                    return LuaTier2NumericSpecializationStatus.ManagedOptimizationRequired;
            }
        }

        for (var pc = 0; pc < function.Instructions.Length; pc++)
        {
            if (!numericRegionProgramCounters.Contains(pc) &&
                !optimized.ContainsKey(pc) &&
                !IsDirectlySupported(function.Instructions[pc]))
            {
                return LuaTier2NumericSpecializationStatus.UnsupportedInstruction;
            }
        }

        return hasSpecializableHotspot
            ? LuaTier2NumericSpecializationStatus.Eligible
            : LuaTier2NumericSpecializationStatus.NoNumericHotspot;
    }

    private static bool IsDirectlySupported(LuaIrInstruction instruction) =>
        instruction.Opcode switch
        {
            LuaIrOpcode.LoadConstant or
            LuaIrOpcode.LoadNil or
            LuaIrOpcode.Move or
            LuaIrOpcode.Unary or
            LuaIrOpcode.Binary or
            LuaIrOpcode.GetUpvalue or
            LuaIrOpcode.SetUpvalue or
            LuaIrOpcode.NewTable or
            LuaIrOpcode.GetTable or
            LuaIrOpcode.SetTable or
            LuaIrOpcode.SetList or
            LuaIrOpcode.Closure or
            LuaIrOpcode.SetTop or
            LuaIrOpcode.Close or
            LuaIrOpcode.JumpIfFalse or
            LuaIrOpcode.JumpIfTrue or
            LuaIrOpcode.Call or
            LuaIrOpcode.TailCall or
            LuaIrOpcode.Return or
            LuaIrOpcode.NumericForPrepare or
            LuaIrOpcode.NumericForLoop or
            LuaIrOpcode.VarArg => true,
            LuaIrOpcode.Jump => instruction.C < 0,
            _ => false,
        };

    private static bool IsExactNumericKind(LuaJitValueKinds kinds) =>
        kinds is LuaJitValueKinds.Integer or LuaJitValueKinds.Float;

    private static void EmitEntryGuard(
        ILGenerator generator,
        LuaIrFunction function,
        Label debugExit)
    {
        generator.Emit(OpCodes.Ldarg_0);
        generator.Emit(OpCodes.Ldarg_2);
        EmitInt32(generator, function.Id);
        EmitInt32(generator, function.RegisterCount);
        generator.Emit(OpCodes.Call, CanExecuteCompiledFrame);
        generator.Emit(OpCodes.Brfalse, debugExit);
    }

    private static void EmitOptimizedInstruction(
        ILGenerator generator,
        LuaIrFunction function,
        int pc,
        LuaIrInstruction instruction,
        ProfileGuidedLuaTier2Compiler.OptimizedInstruction optimization,
        IReadOnlyDictionary<int, LuaBoundDirectCall> boundDirectCalls,
        Dictionary<int, LocalBuilder> tableSiteLocals,
        Dictionary<int, LocalBuilder> tableDefinitionLocals,
        Label[] labels,
        Label budgetExit,
        Label guardExit,
        Label slowPathExit,
        Label invalidatedExit,
        LocalBuilder firstValue,
        LocalBuilder secondValue,
        LocalBuilder picExecutionResult,
        LocalBuilder framelessConsumed,
        LocalBuilder safepointCountdown,
        CancellationToken cancellationToken)
    {
        switch (optimization.Kind)
        {
            case LuaJitOptimizationKind.DeadMove:
                EmitReserve(generator, optimization.InstructionCount, budgetExit);
                // The Lua stack, rather than CIL liveness, defines GC roots. A dead
                // destination can still retain a collectible object below frame.Top,
                // so eliminate the copy but clear the stale root before any poll.
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                generator.Emit(OpCodes.Ldc_I4_1);
                generator.Emit(OpCodes.Call, ClearRegisters);
                EmitNext(
                    generator,
                    pc + optimization.InstructionCount,
                    pc,
                    labels,
                    invalidatedExit,
                    safepointCountdown);
                break;
            case LuaJitOptimizationKind.NumericUnary:
                EmitReadRegister(generator, instruction.B);
                generator.Emit(OpCodes.Stloc, firstValue);
                EmitKindGuard(generator, firstValue, optimization.FirstKinds, guardExit);
                EmitReserve(generator, optimization.InstructionCount, budgetExit);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                EmitInt32(generator, instruction.C);
                generator.Emit(OpCodes.Ldloc, firstValue);
                generator.Emit(
                    OpCodes.Call,
                    GetUnaryMethod(
                        optimization.FirstKinds,
                        (LuaIrUnaryOperator)instruction.C));
                generator.Emit(OpCodes.Call, WriteRegister);
                EmitNext(
                    generator,
                    pc + optimization.InstructionCount,
                    pc,
                    labels,
                    invalidatedExit,
                    safepointCountdown);
                break;
            case LuaJitOptimizationKind.NumericBinary:
            case LuaJitOptimizationKind.PrimitiveBinary:
                EmitReadRegister(generator, instruction.B);
                generator.Emit(OpCodes.Stloc, firstValue);
                EmitReadRegister(generator, instruction.C);
                generator.Emit(OpCodes.Stloc, secondValue);
                EmitKindGuard(generator, firstValue, optimization.FirstKinds, guardExit);
                EmitKindGuard(generator, secondValue, optimization.SecondKinds, guardExit);
                EmitReserve(generator, optimization.InstructionCount, budgetExit);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                EmitInt32(generator, instruction.D);
                generator.Emit(OpCodes.Ldloc, firstValue);
                generator.Emit(OpCodes.Ldloc, secondValue);
                generator.Emit(
                    OpCodes.Call,
                    optimization.Kind == LuaJitOptimizationKind.PrimitiveBinary
                        ? BinaryPrimitive
                        : GetBinaryMethod(optimization.FirstKinds, optimization.SecondKinds));
                generator.Emit(OpCodes.Call, WriteRegister);
                EmitNext(
                    generator,
                    pc + optimization.InstructionCount,
                    pc,
                    labels,
                    invalidatedExit,
                    safepointCountdown);
                break;
            case LuaJitOptimizationKind.BooleanBranch:
                EmitReadRegister(generator, instruction.A);
                generator.Emit(OpCodes.Stloc, firstValue);
                generator.Emit(OpCodes.Ldloca, firstValue);
                generator.Emit(OpCodes.Call, IsTruthy);
                if (instruction.Opcode == LuaIrOpcode.JumpIfFalse)
                {
                    generator.Emit(OpCodes.Ldc_I4_0);
                    generator.Emit(OpCodes.Ceq);
                }

                generator.Emit(
                    optimization.ExpectedBranchTaken ? OpCodes.Brfalse : OpCodes.Brtrue,
                    guardExit);
                EmitReserve(generator, optimization.InstructionCount, budgetExit);
                if (instruction.D != 0)
                {
                    generator.Emit(OpCodes.Ldarg_1);
                    generator.Emit(OpCodes.Ldarg_2);
                    EmitInt32(generator, instruction.C);
                    generator.Emit(OpCodes.Call, SetFrameTop);
                }

                EmitNext(
                    generator,
                    optimization.ExpectedBranchTaken ? instruction.B : pc + 1,
                    pc,
                    labels,
                    invalidatedExit,
                    safepointCountdown);
                break;
            case LuaJitOptimizationKind.TableGetPic:
            case LuaJitOptimizationKind.TableSetPic:
                {
                    var isGet = optimization.Kind == LuaJitOptimizationKind.TableGetPic;
                    var isExactIntegerTable = !optimization.TableShapes.IsEmpty &&
                        optimization.TableShapes.All(static shape =>
                            shape.KeyKinds == LuaJitValueKinds.Integer && !shape.HasMetatable);
                    MethodInfo executePic;
                    if (optimization.TableDefinitionProgramCounter < 0)
                    {
                        executePic = isGet ? TryExecuteTableGetPic : TryExecuteTableSetPic;
                    }
                    else if (isExactIntegerTable)
                    {
                        executePic = isGet
                            ? TryExecuteCompilerProvenIntegerTableGetPic
                            : TryExecuteCompilerProvenIntegerTableSetPic;
                    }
                    else
                    {
                        executePic = isGet
                            ? TryExecuteCompilerProvenTableGetPic
                            : TryExecuteCompilerProvenTableSetPic;
                    }

                    generator.Emit(OpCodes.Ldarg_0);
                    generator.Emit(OpCodes.Ldarg_1);
                    generator.Emit(OpCodes.Ldarg_2);
                    if (optimization.TableDefinitionProgramCounter >= 0)
                    {
                        generator.Emit(
                            OpCodes.Ldloca,
                            tableDefinitionLocals[optimization.TableDefinitionProgramCounter]);
                    }

                    generator.Emit(OpCodes.Ldloc, tableSiteLocals[pc]);
                    EmitInt32(generator, instruction.A);
                    EmitInt32(generator, instruction.B);
                    EmitInt32(generator, instruction.C);
                    generator.Emit(
                        OpCodes.Call,
                        executePic);
                    generator.Emit(OpCodes.Stloc, picExecutionResult);
                    generator.Emit(OpCodes.Ldloc, picExecutionResult);
                    EmitInt32(generator, (int)LuaCodegenPicExecutionResult.GuardFailure);
                    generator.Emit(OpCodes.Beq, guardExit);
                    generator.Emit(OpCodes.Ldloc, picExecutionResult);
                    EmitInt32(generator, (int)LuaCodegenPicExecutionResult.InstructionBudget);
                    generator.Emit(OpCodes.Beq, budgetExit);

                    EmitNext(
                        generator,
                        pc + 1,
                        pc,
                        labels,
                        invalidatedExit,
                        safepointCountdown);
                    break;
                }
            case LuaJitOptimizationKind.KnownClosureCall:
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                generator.Emit(OpCodes.Ldarg_3);
                EmitInt32(generator, pc);
                generator.Emit(OpCodes.Ldstr, optimization.CallTarget!.ModuleContentId);
                generator.Emit(OpCodes.Callvirt, GetCallSite);
                EmitInt32(generator, instruction.A);
                EmitInt32(generator, optimization.CallTarget.FunctionId);
                generator.Emit(OpCodes.Call, CanExecuteKnownClosureCall);
                generator.Emit(OpCodes.Brfalse, guardExit);
                EmitReserve(generator, optimization.InstructionCount, budgetExit);
                Label? slowCall = null;
                if (instruction.Opcode == LuaIrOpcode.Call)
                {
                    slowCall = generator.DefineLabel();
                    if (boundDirectCalls.TryGetValue(pc, out var boundDirectCall))
                    {
                        var inlineCompleted = generator.DefineLabel();
                        var inlineFallback = generator.DefineLabel();
                        ReflectionEmitLuaDirectCallCompiler.EmitInline(
                            boundDirectCall.Function,
                            boundDirectCall.TypePlan,
                            new ReflectionEmitLuaNumericRegionIlGenerator(generator),
                            instruction.A,
                            instruction.C,
                            new LuaNumericIlLabel(-1, inlineCompleted),
                            new LuaNumericIlLabel(-1, inlineFallback),
                            cancellationToken);
                        generator.MarkLabel(inlineCompleted);
                        generator.Emit(OpCodes.Ldarg_3);
                        generator.Emit(OpCodes.Callvirt, RecordInlineDirectCallCompletion);
                        EmitNext(
                            generator,
                            pc + 1,
                            pc,
                            labels,
                            invalidatedExit,
                            safepointCountdown);
                        generator.MarkLabel(inlineFallback);
                        generator.Emit(OpCodes.Ldarg_3);
                        generator.Emit(OpCodes.Callvirt, RecordInlineDirectCallFallback);
                    }
                    else
                    {
                        generator.Emit(OpCodes.Ldarg_0);
                        generator.Emit(OpCodes.Ldarg_1);
                        generator.Emit(OpCodes.Ldarg_2);
                        generator.Emit(OpCodes.Ldarg_3);
                        EmitInt32(generator, pc);
                        generator.Emit(OpCodes.Ldstr, optimization.CallTarget.ModuleContentId);
                        generator.Emit(OpCodes.Callvirt, GetCallSite);
                        EmitInt32(generator, instruction.A);
                        EmitInt32(generator, optimization.CallTarget.FunctionId);
                        EmitInt32(generator, instruction.B);
                        EmitInt32(generator, instruction.C);
                        generator.Emit(OpCodes.Call, TryExecuteDirectCompiledCall);
                        var directCallMiss = generator.DefineLabel();
                        generator.Emit(OpCodes.Brfalse, directCallMiss);
                        EmitNext(
                            generator,
                            pc + 1,
                            pc,
                            labels,
                            invalidatedExit,
                            safepointCountdown);
                        generator.MarkLabel(directCallMiss);
                    }

                    generator.Emit(OpCodes.Ldarg_0);
                    generator.Emit(OpCodes.Ldarg_1);
                    generator.Emit(OpCodes.Ldarg_2);
                    EmitInt32(generator, instruction.A);
                    EmitInt32(generator, instruction.B);
                    EmitInt32(generator, instruction.C);
                    generator.Emit(OpCodes.Call, TryExecuteFramelessCall);
                    generator.Emit(OpCodes.Conv_I8);
                    generator.Emit(OpCodes.Stloc, framelessConsumed);
                    generator.Emit(OpCodes.Ldloc, framelessConsumed);
                    generator.Emit(OpCodes.Brfalse, slowCall.Value);
                    var continueInMethod = generator.DefineLabel();
                    generator.Emit(OpCodes.Ldarg_0);
                    generator.Emit(OpCodes.Ldarg_1);
                    generator.Emit(OpCodes.Ldarg_2);
                    generator.Emit(OpCodes.Call, CanContinueAfterFramelessCall);
                    generator.Emit(OpCodes.Brtrue, continueInMethod);
                    EmitInt32(generator, pc + 1);
                    EmitInstructionsConsumed(generator);
                    generator.Emit(OpCodes.Call, ContinueExit);
                    generator.Emit(OpCodes.Ret);
                    generator.MarkLabel(continueInMethod);
                    EmitNext(
                        generator,
                        pc + 1,
                        pc,
                        labels,
                        invalidatedExit,
                        safepointCountdown);
                    generator.MarkLabel(slowCall.Value);
                }

                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                EmitInt32(generator, instruction.B);
                if (instruction.Opcode == LuaIrOpcode.TailCall)
                {
                    generator.Emit(OpCodes.Call, ExecuteKnownClosureTailCall);
                }
                else
                {
                    EmitInt32(generator, instruction.C);
                    generator.Emit(OpCodes.Call, ExecuteKnownClosureCall);
                }

                EmitInt32(
                    generator,
                    instruction.Opcode == LuaIrOpcode.TailCall ? pc : pc + 1);
                EmitInstructionsConsumed(generator);
                generator.Emit(OpCodes.Call, ContinueExit);
                generator.Emit(OpCodes.Ret);
                break;
            default:
                generator.Emit(OpCodes.Br, slowPathExit);
                break;
        }
    }

    private static void EmitDirectInstruction(
        ILGenerator generator,
        LuaIrFunction function,
        int pc,
        LuaIrInstruction instruction,
        Dictionary<int, LocalBuilder> tableDefinitionLocals,
        Label[] labels,
        Label budgetExit,
        Label slowPathExit,
        Label invalidatedExit,
        LocalBuilder firstValue,
        LocalBuilder safepointCountdown)
    {
        switch (instruction.Opcode)
        {
            case LuaIrOpcode.LoadConstant:
                EmitReserve(generator, 1, budgetExit);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.B);
                generator.Emit(OpCodes.Call, MaterializeConstant);
                generator.Emit(OpCodes.Call, WriteRegister);
                EmitNext(generator, pc + 1, pc, labels, invalidatedExit, safepointCountdown);
                break;
            case LuaIrOpcode.LoadNil:
                EmitReserve(generator, 1, budgetExit);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                EmitInt32(generator, instruction.B);
                generator.Emit(OpCodes.Call, ClearRegisters);
                EmitNext(generator, pc + 1, pc, labels, invalidatedExit, safepointCountdown);
                break;
            case LuaIrOpcode.Move:
                EmitReserve(generator, 1, budgetExit);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                EmitReadRegister(generator, instruction.B);
                generator.Emit(OpCodes.Call, WriteRegister);
                EmitNext(generator, pc + 1, pc, labels, invalidatedExit, safepointCountdown);
                break;
            case LuaIrOpcode.Unary:
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.C);
                EmitInt32(generator, instruction.B);
                generator.Emit(OpCodes.Call, CanExecuteUnaryPrimitive);
                generator.Emit(OpCodes.Brfalse, slowPathExit);
                EmitReserve(generator, 1, budgetExit);
                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                EmitInt32(generator, instruction.C);
                EmitInt32(generator, instruction.B);
                generator.Emit(OpCodes.Call, ExecuteUnaryPrimitive);
                EmitNext(generator, pc + 1, pc, labels, invalidatedExit, safepointCountdown);
                break;
            case LuaIrOpcode.Binary:
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.D);
                EmitInt32(generator, instruction.B);
                EmitInt32(generator, instruction.C);
                generator.Emit(OpCodes.Call, CanExecuteBinaryPrimitive);
                generator.Emit(OpCodes.Brfalse, slowPathExit);
                EmitReserve(generator, 1, budgetExit);
                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                EmitInt32(generator, instruction.D);
                EmitInt32(generator, instruction.B);
                EmitInt32(generator, instruction.C);
                generator.Emit(OpCodes.Call, ExecuteBinaryPrimitive);
                EmitNext(generator, pc + 1, pc, labels, invalidatedExit, safepointCountdown);
                break;
            case LuaIrOpcode.GetUpvalue:
                EmitReserve(generator, 1, budgetExit);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.B);
                generator.Emit(OpCodes.Call, ReadUpvalue);
                generator.Emit(OpCodes.Call, WriteRegister);
                EmitNext(generator, pc + 1, pc, labels, invalidatedExit, safepointCountdown);
                break;
            case LuaIrOpcode.SetUpvalue:
                EmitReserve(generator, 1, budgetExit);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                EmitReadRegister(generator, instruction.B);
                generator.Emit(OpCodes.Call, WriteUpvalue);
                EmitNext(generator, pc + 1, pc, labels, invalidatedExit, safepointCountdown);
                break;
            case LuaIrOpcode.NewTable:
                EmitReserve(generator, 1, budgetExit);
                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                EmitInt32(generator, instruction.B);
                EmitInt32(generator, instruction.C);
                generator.Emit(OpCodes.Call, ExecuteNewTable);
                if (tableDefinitionLocals.TryGetValue(pc, out var tableDefinitionLocal))
                {
                    generator.Emit(OpCodes.Ldnull);
                    generator.Emit(OpCodes.Stloc, tableDefinitionLocal);
                }

                EmitNext(generator, pc + 1, pc, labels, invalidatedExit, safepointCountdown);
                break;
            case LuaIrOpcode.GetTable:
            case LuaIrOpcode.SetTable:
                {
                    EmitReserve(generator, 1, budgetExit);
                    generator.Emit(OpCodes.Ldarg_0);
                    generator.Emit(OpCodes.Ldarg_1);
                    generator.Emit(OpCodes.Ldarg_2);
                    EmitInt32(generator, instruction.A);
                    EmitInt32(generator, instruction.B);
                    EmitInt32(generator, instruction.C);
                    generator.Emit(
                        OpCodes.Call,
                        instruction.Opcode == LuaIrOpcode.GetTable
                            ? ExecuteGetTable
                            : ExecuteSetTable);
                    var completed = generator.DefineLabel();
                    generator.Emit(OpCodes.Brtrue, completed);
                    EmitInt32(generator, pc + 1);
                    EmitInstructionsConsumed(generator);
                    generator.Emit(OpCodes.Call, ContinueExit);
                    generator.Emit(OpCodes.Ret);
                    generator.MarkLabel(completed);
                    EmitNext(generator, pc + 1, pc, labels, invalidatedExit, safepointCountdown);
                    break;
                }
            case LuaIrOpcode.SetList:
                EmitReserve(generator, 1, budgetExit);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                EmitInt32(generator, instruction.B);
                EmitInt32(generator, instruction.C);
                EmitInt32(generator, instruction.D);
                generator.Emit(OpCodes.Call, ExecuteSetList);
                EmitNext(generator, pc + 1, pc, labels, invalidatedExit, safepointCountdown);
                break;
            case LuaIrOpcode.Closure:
                EmitReserve(generator, 1, budgetExit);
                generator.Emit(OpCodes.Ldarg_0);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                EmitInt32(generator, instruction.B);
                generator.Emit(OpCodes.Call, ExecuteClosure);
                EmitNext(generator, pc + 1, pc, labels, invalidatedExit, safepointCountdown);
                break;
            case LuaIrOpcode.VarArg:
                EmitReserve(generator, 1, budgetExit);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                EmitInt32(generator, instruction.B);
                generator.Emit(OpCodes.Call, ExecuteVarArg);
                EmitNext(generator, pc + 1, pc, labels, invalidatedExit, safepointCountdown);
                break;
            case LuaIrOpcode.SetTop:
                EmitReserve(generator, 1, budgetExit);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                generator.Emit(OpCodes.Call, SetFrameTop);
                EmitNext(generator, pc + 1, pc, labels, invalidatedExit, safepointCountdown);
                break;
            case LuaIrOpcode.Close:
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                generator.Emit(OpCodes.Call, CanSkipClose);
                generator.Emit(OpCodes.Brfalse, slowPathExit);
                EmitReserve(generator, 1, budgetExit);
                EmitNext(generator, pc + 1, pc, labels, invalidatedExit, safepointCountdown);
                break;
            case LuaIrOpcode.Jump when instruction.C < 0:
                EmitReserve(generator, 1, budgetExit);
                EmitNext(generator, instruction.B, pc, labels, invalidatedExit, safepointCountdown);
                break;
            case LuaIrOpcode.JumpIfFalse:
            case LuaIrOpcode.JumpIfTrue:
                EmitReserve(generator, 1, budgetExit);
                if (instruction.D != 0)
                {
                    generator.Emit(OpCodes.Ldarg_1);
                    generator.Emit(OpCodes.Ldarg_2);
                    EmitInt32(generator, instruction.A);
                    EmitInt32(generator, instruction.C);
                    generator.Emit(OpCodes.Call, ReadTruthyAndSetFrameTop);
                }
                else
                {
                    EmitReadRegister(generator, instruction.A);
                    generator.Emit(OpCodes.Stloc, firstValue);
                    generator.Emit(OpCodes.Ldloca, firstValue);
                    generator.Emit(OpCodes.Call, IsTruthy);
                }
                var taken = generator.DefineLabel();
                generator.Emit(
                    instruction.Opcode == LuaIrOpcode.JumpIfTrue
                        ? OpCodes.Brtrue
                        : OpCodes.Brfalse,
                    taken);
                EmitNext(generator, pc + 1, pc, labels, invalidatedExit, safepointCountdown);
                generator.MarkLabel(taken);
                EmitNext(generator, instruction.B, pc, labels, invalidatedExit, safepointCountdown);
                break;
            case LuaIrOpcode.Return:
                EmitReserve(generator, 1, budgetExit);
                EmitInt32(generator, pc);
                EmitInstructionsConsumed(generator);
                generator.Emit(OpCodes.Call, ReturnExit);
                generator.Emit(OpCodes.Ret);
                break;
            case LuaIrOpcode.NumericForPrepare:
                EmitReserve(generator, 1, budgetExit);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                EmitInt32(generator, instruction.B);
                generator.Emit(OpCodes.Call, ExecuteNumericForPrepare);
                EmitFrameProgramCounterDispatch(
                    generator,
                    pc,
                    labels,
                    invalidatedExit,
                    safepointCountdown);
                break;
            case LuaIrOpcode.NumericForLoop:
                EmitReserve(generator, 1, budgetExit);
                generator.Emit(OpCodes.Ldarg_1);
                generator.Emit(OpCodes.Ldarg_2);
                EmitInt32(generator, instruction.A);
                EmitInt32(generator, instruction.B);
                generator.Emit(OpCodes.Call, ExecuteNumericForLoop);
                EmitFrameProgramCounterDispatch(
                    generator,
                    pc,
                    labels,
                    invalidatedExit,
                    safepointCountdown);
                break;
            case LuaIrOpcode.Call:
            case LuaIrOpcode.TailCall:
                EmitReserve(generator, 1, budgetExit);
                EmitInt32(generator, pc);
                EmitInstructionsConsumed(generator);
                generator.Emit(
                    OpCodes.Call,
                    instruction.Opcode == LuaIrOpcode.Call ? CallExit : TailCallExit);
                generator.Emit(OpCodes.Ret);
                break;
            default:
                generator.Emit(OpCodes.Br, slowPathExit);
                break;
        }
    }

    private static void EmitKindGuard(
        ILGenerator generator,
        LocalBuilder value,
        LuaJitValueKinds kinds,
        Label guardExit)
    {
        generator.Emit(OpCodes.Ldloca, value);
        generator.Emit(OpCodes.Call, GetValueKind);
        EmitInt32(generator, (int)GetExactValueKind(kinds));
        generator.Emit(OpCodes.Bne_Un, guardExit);
    }

    private static MethodInfo GetUnaryMethod(
        LuaJitValueKinds kinds,
        LuaIrUnaryOperator operation)
    {
        if (operation is LuaIrUnaryOperator.LogicalNot or LuaIrUnaryOperator.Length)
        {
            return GenericUnary;
        }

        return kinds == LuaJitValueKinds.Integer ? UnaryInteger : UnaryFloat;
    }

    private static MethodInfo GetBinaryMethod(
        LuaJitValueKinds first,
        LuaJitValueKinds second)
    {
        if (first == LuaJitValueKinds.Integer && second == LuaJitValueKinds.Integer)
        {
            return BinaryInteger;
        }

        return first == LuaJitValueKinds.Float && second == LuaJitValueKinds.Float
            ? BinaryFloat
            : BinaryMixedNumeric;
    }

    private static LuaValueKind GetExactValueKind(LuaJitValueKinds kinds) => kinds switch
    {
        LuaJitValueKinds.Nil => LuaValueKind.Nil,
        LuaJitValueKinds.Boolean => LuaValueKind.Boolean,
        LuaJitValueKinds.Integer => LuaValueKind.Integer,
        LuaJitValueKinds.Float => LuaValueKind.Float,
        LuaJitValueKinds.String => LuaValueKind.String,
        LuaJitValueKinds.Table => LuaValueKind.Table,
        LuaJitValueKinds.Function => LuaValueKind.Function,
        LuaJitValueKinds.Thread => LuaValueKind.Thread,
        LuaJitValueKinds.Userdata => LuaValueKind.Userdata,
        LuaJitValueKinds.LightUserdata => LuaValueKind.LightUserdata,
        _ => throw new InvalidOperationException($"Expected one exact Lua value kind, got {kinds}."),
    };

    private static bool IsExactPrimitiveKind(LuaJitValueKinds kinds) => kinds is
        LuaJitValueKinds.Nil or LuaJitValueKinds.Boolean or LuaJitValueKinds.String;

    private static void EmitReserve(
        ILGenerator generator,
        int instructionCount,
        Label budgetExit)
    {
        generator.Emit(OpCodes.Ldarg_0);
        EmitInt32(generator, instructionCount);
        generator.Emit(OpCodes.Callvirt, ReserveInstructions);
        generator.Emit(OpCodes.Brfalse, budgetExit);
    }

    private static void EmitReadRegister(ILGenerator generator, int register)
    {
        generator.Emit(OpCodes.Ldarg_1);
        generator.Emit(OpCodes.Ldarg_2);
        EmitInt32(generator, register);
        generator.Emit(OpCodes.Call, ReadRegister);
    }

    private static void EmitNext(
        ILGenerator generator,
        int nextProgramCounter,
        int sourceProgramCounter,
        Label[] labels,
        Label invalidatedExit,
        LocalBuilder safepointCountdown)
    {
        generator.Emit(OpCodes.Ldarg_2);
        EmitInt32(generator, nextProgramCounter);
        generator.Emit(OpCodes.Callvirt, SetProgramCounter);
        var destination = (uint)nextProgramCounter < (uint)labels.Length
            ? labels[nextProgramCounter]
            : invalidatedExit;
        if (nextProgramCounter <= sourceProgramCounter &&
            (uint)nextProgramCounter < (uint)labels.Length)
        {
            generator.Emit(OpCodes.Ldloc, safepointCountdown);
            generator.Emit(OpCodes.Ldc_I4_1);
            generator.Emit(OpCodes.Sub);
            generator.Emit(OpCodes.Dup);
            generator.Emit(OpCodes.Stloc, safepointCountdown);
            generator.Emit(OpCodes.Brtrue, destination);
            EmitInt32(generator, LuaCodegenAbiV3.CompiledBackedgeSafepointQuantum);
            generator.Emit(OpCodes.Stloc, safepointCountdown);
            generator.Emit(OpCodes.Ldarg_0);
            generator.Emit(OpCodes.Ldarg_1);
            generator.Emit(OpCodes.Ldarg_2);
            generator.Emit(OpCodes.Call, PollGcSafepoint);
            generator.Emit(OpCodes.Brtrue, destination);
            EmitExit(
                generator,
                PollExit,
                nextProgramCounter,
                LuaCompiledExitReason.GarbageCollection);
        }

        generator.Emit(
            OpCodes.Br,
            destination);
    }

    private static void EmitFrameProgramCounterDispatch(
        ILGenerator generator,
        int sourceProgramCounter,
        Label[] labels,
        Label invalidatedExit,
        LocalBuilder safepointCountdown)
    {
        var dispatch = generator.DefineLabel();
        generator.Emit(OpCodes.Ldarg_2);
        generator.Emit(OpCodes.Callvirt, GetProgramCounter);
        EmitInt32(generator, sourceProgramCounter);
        generator.Emit(OpCodes.Bgt, dispatch);
        generator.Emit(OpCodes.Ldloc, safepointCountdown);
        generator.Emit(OpCodes.Ldc_I4_1);
        generator.Emit(OpCodes.Sub);
        generator.Emit(OpCodes.Dup);
        generator.Emit(OpCodes.Stloc, safepointCountdown);
        generator.Emit(OpCodes.Brtrue, dispatch);
        EmitInt32(generator, LuaCodegenAbiV3.CompiledBackedgeSafepointQuantum);
        generator.Emit(OpCodes.Stloc, safepointCountdown);
        generator.Emit(OpCodes.Ldarg_0);
        generator.Emit(OpCodes.Ldarg_1);
        generator.Emit(OpCodes.Ldarg_2);
        generator.Emit(OpCodes.Call, PollGcSafepoint);
        generator.Emit(OpCodes.Brtrue, dispatch);
        EmitFrameProgramCounterExit(
            generator,
            PollExit,
            LuaCompiledExitReason.GarbageCollection);
        generator.MarkLabel(dispatch);
        generator.Emit(OpCodes.Ldarg_2);
        generator.Emit(OpCodes.Callvirt, GetProgramCounter);
        generator.Emit(OpCodes.Switch, labels);
        generator.Emit(OpCodes.Br, invalidatedExit);
    }

    private static void EmitSlowPath(ILGenerator generator, int programCounter)
    {
        EmitExit(
            generator,
            DeoptExit,
            programCounter,
            LuaCompiledExitReason.UnsupportedInstruction);
    }

    private static void EmitContinue(ILGenerator generator, int programCounter)
    {
        EmitInt32(generator, programCounter);
        EmitInstructionsConsumed(generator);
        generator.Emit(OpCodes.Call, ContinueExit);
        generator.Emit(OpCodes.Ret);
    }

    private static void EmitExit(
        ILGenerator generator,
        MethodInfo factory,
        int programCounter,
        LuaCompiledExitReason reason)
    {
        EmitInt32(generator, programCounter);
        EmitInstructionsConsumed(generator);
        EmitInt32(generator, (int)reason);
        generator.Emit(OpCodes.Call, factory);
        generator.Emit(OpCodes.Ret);
    }

    private static void EmitFrameProgramCounterExit(
        ILGenerator generator,
        MethodInfo factory,
        LuaCompiledExitReason reason)
    {
        generator.Emit(OpCodes.Ldarg_2);
        generator.Emit(OpCodes.Callvirt, GetProgramCounter);
        EmitInstructionsConsumed(generator);
        EmitInt32(generator, (int)reason);
        generator.Emit(OpCodes.Call, factory);
        generator.Emit(OpCodes.Ret);
    }

    private static void EmitInstructionsConsumed(ILGenerator generator)
    {
        generator.Emit(OpCodes.Ldarg_0);
        generator.Emit(OpCodes.Callvirt, GetInstructionsConsumed);
    }

    private static void EmitInt32(ILGenerator generator, int value)
    {
        switch (value)
        {
            case -1:
                generator.Emit(OpCodes.Ldc_I4_M1);
                break;
            case 0:
                generator.Emit(OpCodes.Ldc_I4_0);
                break;
            case 1:
                generator.Emit(OpCodes.Ldc_I4_1);
                break;
            case 2:
                generator.Emit(OpCodes.Ldc_I4_2);
                break;
            case 3:
                generator.Emit(OpCodes.Ldc_I4_3);
                break;
            case 4:
                generator.Emit(OpCodes.Ldc_I4_4);
                break;
            case 5:
                generator.Emit(OpCodes.Ldc_I4_5);
                break;
            case 6:
                generator.Emit(OpCodes.Ldc_I4_6);
                break;
            case 7:
                generator.Emit(OpCodes.Ldc_I4_7);
                break;
            case 8:
                generator.Emit(OpCodes.Ldc_I4_8);
                break;
            case >= sbyte.MinValue and <= sbyte.MaxValue:
                generator.Emit(OpCodes.Ldc_I4_S, (sbyte)value);
                break;
            default:
                generator.Emit(OpCodes.Ldc_I4, value);
                break;
        }
    }

    private static MethodInfo Method(
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicMethods |
            DynamicallyAccessedMemberTypes.NonPublicMethods)]
        Type type,
        string name,
        Type[] parameterTypes) =>
        type.GetMethod(
            name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static |
                BindingFlags.Instance,
            parameterTypes) ??
        throw new MissingMethodException(type.FullName, name);

    private static MethodInfo PropertyGetter(
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicProperties |
            DynamicallyAccessedMemberTypes.NonPublicProperties)]
        Type type,
        string name) =>
        type.GetProperty(
            name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                BindingFlags.Static)?.GetGetMethod(nonPublic: true) ??
        throw new MissingMethodException(type.FullName, $"get_{name}");

    private static MethodInfo PropertySetter(
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicProperties |
            DynamicallyAccessedMemberTypes.NonPublicProperties)]
        Type type,
        string name) =>
        type.GetProperty(
            name,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                BindingFlags.Static)?.GetSetMethod(nonPublic: true) ??
        throw new MissingMethodException(type.FullName, $"set_{name}");
}
