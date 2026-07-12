using System.Collections.Immutable;
using Lunil.IR.Canonical;
using Lunil.CodeGen.Cil.Planning;

namespace Lunil.CodeGen.Cil.Analysis;

public sealed record LuaRegisterLivenessResult(
    ImmutableArray<ImmutableArray<int>> LiveBefore,
    ImmutableArray<ImmutableArray<int>> LiveAfter,
    ImmutableArray<CilGcMap> GcMaps);

public static class LuaRegisterLiveness
{
    public static LuaRegisterLivenessResult Analyze(
        LuaIrModule module,
        LuaIrFunction function,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(function);
        cancellationToken.ThrowIfCancellationRequested();
        var instructionCount = function.Instructions.Length;
        var liveBefore = CreateMatrix(
            instructionCount,
            function.RegisterCount,
            cancellationToken);
        var liveAfter = CreateMatrix(
            instructionCount,
            function.RegisterCount,
            cancellationToken);
        var changed = true;
        while (changed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            changed = false;
            for (var pc = instructionCount - 1; pc >= 0; pc--)
            {
                if ((pc & 63) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var nextAfter = new bool[function.RegisterCount];
                foreach (var successor in Successors(function, pc))
                {
                    UnionInto(nextAfter, liveBefore[successor]);
                }

                var nextBefore = (bool[])nextAfter.Clone();
                ApplyDefinitions(function, function.Instructions[pc], nextBefore);
                ApplyUses(module, function, function.Instructions[pc], nextBefore);
                if (!nextAfter.AsSpan().SequenceEqual(liveAfter[pc]) ||
                    !nextBefore.AsSpan().SequenceEqual(liveBefore[pc]))
                {
                    liveAfter[pc] = nextAfter;
                    liveBefore[pc] = nextBefore;
                    changed = true;
                }
            }
        }

        var before = ImmutableArray.CreateBuilder<ImmutableArray<int>>(instructionCount);
        var after = ImmutableArray.CreateBuilder<ImmutableArray<int>>(instructionCount);
        var gcMaps = ImmutableArray.CreateBuilder<CilGcMap>();
        for (var pc = 0; pc < instructionCount; pc++)
        {
            if ((pc & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var liveBeforeRegisters = ToRegisters(liveBefore[pc]);
            before.Add(liveBeforeRegisters);
            after.Add(ToRegisters(liveAfter[pc]));
            if (function.Instructions[pc].Effects.HasFlag(
                LuaIrInstructionEffects.IsGcSafePoint))
            {
                gcMaps.Add(new CilGcMap(pc, liveBeforeRegisters));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new LuaRegisterLivenessResult(
            before.MoveToImmutable(),
            after.MoveToImmutable(),
            gcMaps.ToImmutable());
    }

    private static bool[][] CreateMatrix(
        int rows,
        int columns,
        CancellationToken cancellationToken)
    {
        var matrix = new bool[rows][];
        for (var row = 0; row < rows; row++)
        {
            if ((row & 63) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            matrix[row] = new bool[columns];
        }

        return matrix;
    }

    private static IEnumerable<int> Successors(LuaIrFunction function, int pc)
    {
        var instruction = function.Instructions[pc];
        switch (instruction.Opcode)
        {
            case LuaIrOpcode.Jump:
                yield return instruction.B;
                break;
            case LuaIrOpcode.JumpIfFalse:
            case LuaIrOpcode.JumpIfTrue:
            case LuaIrOpcode.NumericForPrepare:
            case LuaIrOpcode.NumericForLoop:
                yield return instruction.B;
                if (pc + 1 < function.Instructions.Length)
                {
                    yield return pc + 1;
                }

                break;
            case LuaIrOpcode.Return:
            case LuaIrOpcode.TailCall:
                break;
            default:
                if (pc + 1 < function.Instructions.Length)
                {
                    yield return pc + 1;
                }

                break;
        }
    }

    private static void ApplyDefinitions(
        LuaIrFunction function,
        LuaIrInstruction instruction,
        bool[] live)
    {
        switch (instruction.Opcode)
        {
            case LuaIrOpcode.LoadConstant:
            case LuaIrOpcode.Move:
            case LuaIrOpcode.GetUpvalue:
            case LuaIrOpcode.NewTable:
            case LuaIrOpcode.GetTable:
            case LuaIrOpcode.Closure:
            case LuaIrOpcode.Unary:
            case LuaIrOpcode.Binary:
                Kill(live, instruction.A, 1);
                break;
            case LuaIrOpcode.LoadNil:
                Kill(live, instruction.A, instruction.B);
                break;
            case LuaIrOpcode.SetTop:
                Kill(live, instruction.A, function.RegisterCount - instruction.A);
                break;
            case LuaIrOpcode.VarArg:
                Kill(
                    live,
                    instruction.A,
                    instruction.B < 0 ? function.RegisterCount - instruction.A : instruction.B);
                break;
            case LuaIrOpcode.Call:
                Kill(
                    live,
                    instruction.A,
                    instruction.C < 0 ? function.RegisterCount - instruction.A : instruction.C);
                break;
            case LuaIrOpcode.NumericForPrepare:
            case LuaIrOpcode.NumericForLoop:
                Kill(live, instruction.A, Math.Min(4, function.RegisterCount - instruction.A));
                break;
        }
    }

    private static void ApplyUses(
        LuaIrModule module,
        LuaIrFunction function,
        LuaIrInstruction instruction,
        bool[] live)
    {
        switch (instruction.Opcode)
        {
            case LuaIrOpcode.Move:
            case LuaIrOpcode.SetUpvalue:
                Use(live, instruction.B, 1);
                break;
            case LuaIrOpcode.GetTable:
                Use(live, instruction.B, 1);
                Use(live, instruction.C, 1);
                break;
            case LuaIrOpcode.SetTable:
                Use(live, instruction.A, 1);
                Use(live, instruction.B, 1);
                Use(live, instruction.C, 1);
                break;
            case LuaIrOpcode.SetList:
                Use(live, instruction.A, 1);
                Use(
                    live,
                    instruction.A + 1,
                    instruction.B < 0
                        ? function.RegisterCount - instruction.A - 1
                        : instruction.B);
                break;
            case LuaIrOpcode.Closure:
                foreach (var upvalue in module.Functions[instruction.B].Upvalues)
                {
                    if (upvalue.SourceKind == LuaIrUpvalueSourceKind.Register)
                    {
                        Use(live, upvalue.SourceIndex, 1);
                    }
                }

                break;
            case LuaIrOpcode.Unary:
                Use(live, instruction.B, 1);
                break;
            case LuaIrOpcode.Binary:
                Use(live, instruction.B, 1);
                Use(live, instruction.C, 1);
                break;
            case LuaIrOpcode.Jump:
                if (instruction.C >= 0)
                {
                    Use(live, instruction.C, function.RegisterCount - instruction.C);
                }

                break;
            case LuaIrOpcode.JumpIfFalse:
            case LuaIrOpcode.JumpIfTrue:
            case LuaIrOpcode.MarkToBeClosed:
                Use(live, instruction.A, 1);
                break;
            case LuaIrOpcode.Call:
            case LuaIrOpcode.TailCall:
                Use(
                    live,
                    instruction.A,
                    instruction.B < 0
                        ? function.RegisterCount - instruction.A
                        : instruction.B + 1);
                break;
            case LuaIrOpcode.Return:
                Use(
                    live,
                    instruction.A,
                    instruction.B < 0
                        ? function.RegisterCount - instruction.A
                        : instruction.B);
                break;
            case LuaIrOpcode.Close:
                Use(live, instruction.A, function.RegisterCount - instruction.A);
                break;
            case LuaIrOpcode.NumericForPrepare:
            case LuaIrOpcode.NumericForLoop:
                Use(live, instruction.A, Math.Min(4, function.RegisterCount - instruction.A));
                break;
        }
    }

    private static void Kill(bool[] live, int start, int count)
    {
        for (var index = 0; index < count; index++)
        {
            live[start + index] = false;
        }
    }

    private static void Use(bool[] live, int start, int count)
    {
        for (var index = 0; index < count; index++)
        {
            live[start + index] = true;
        }
    }

    private static void UnionInto(bool[] target, bool[] source)
    {
        for (var index = 0; index < target.Length; index++)
        {
            target[index] |= source[index];
        }
    }

    private static ImmutableArray<int> ToRegisters(bool[] values) =>
        values
            .Select((isLive, register) => (isLive, register))
            .Where(static item => item.isLive)
            .Select(static item => item.register)
            .ToImmutableArray();
}
