using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Lunil.Core;
using Lunil.IR.Canonical;
using Lunil.IR.Lua54;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Operations;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Execution;

internal sealed partial class LuaExecutionEngine
{
    internal LuaCompiledExit ExecuteCodegenSlowPath(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        int programCounter)
    {
        LunilGuard.NotNull(context);
        LunilGuard.NotNull(thread);
        LunilGuard.NotNull(frame);
        if (!ReferenceEquals(context.ExecutionEngine, this) ||
            !ReferenceEquals(context.Thread, thread))
        {
            throw new InvalidOperationException(
                "The execution context does not belong to this execution engine and thread.");
        }

        var instructions = frame.Function.Instructions;
        LunilGuard.NotNegative(programCounter);
        LunilGuard.LessThan(
            programCounter,
            instructions.Length);
        if (frame.ProgramCounter != programCounter)
        {
            throw new InvalidOperationException(
                "The compiled slow path must start at the committed canonical program counter.");
        }

        var instructionArray = ImmutableCollectionsMarshal.AsArray(instructions)!;
        return _referenceInstructionExecutor.Execute(
            this,
            context,
            context.State,
            thread,
            frame,
            in instructionArray[programCounter]);
    }

    internal void ObserveCodegenInstruction(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        int programCounter)
    {
        if (!context.TryBeginInstructionObservation(programCounter))
        {
            return;
        }

        var instructions = frame.Function.Instructions;
        LunilGuard.NotNegative(programCounter);
        LunilGuard.LessThan(
            programCounter,
            instructions.Length);
        var instructionArray = ImmutableCollectionsMarshal.AsArray(instructions)!;
        _instructionExecutor.ObserveInstruction(
            context,
            thread,
            frame,
            programCounter,
            in instructionArray[programCounter]);
    }

    internal void ObserveLoopOsrBackedges(
        LuaFrame frame,
        int programCounter,
        int backedgeCount)
    {
        _instructionExecutor.ObserveLoopOsrBackedges(frame, programCounter, backedgeCount);
    }

    internal bool TryExecuteDirectCall(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame caller,
        LuaCodegenCallSiteCache cache,
        int functionRegister,
        int expectedFunctionId,
        int argumentCount,
        int expectedResults) =>
        _instructionExecutor is ILuaDirectCallExecutor directCallExecutor &&
        directCallExecutor.TryExecuteDirectCall(
            context,
            thread,
            caller,
            cache,
            functionRegister,
            expectedFunctionId,
            argumentCount,
            expectedResults);

    [Conditional("DEBUG")]
    private static void ValidateInstructionAccounting(
        LuaExecutionContext context,
        LuaCompiledExit exit)
    {
        if (exit.InstructionsConsumed != context.InstructionsConsumed)
        {
            throw new InvalidOperationException(
                "An execution backend returned an instruction count that does not match " +
                "the range reserved through Runtime ABI v1.");
        }
    }

    private static LuaValue[] ProtectedNativeCallbackFailure(
        LuaState state,
        LuaRuntimeException exception) =>
        [LuaValue.FromBoolean(false), MaterializeError(state, exception)];
}
