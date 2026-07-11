using System.Collections.Immutable;

namespace Luac.IR.Canonical;

public static class LuaIrControlFlow
{
    public static ImmutableArray<LuaIrBasicBlock> Build(
        ImmutableArray<LuaIrInstruction> instructions)
    {
        if (instructions.IsEmpty)
        {
            return [];
        }

        var leaders = new SortedSet<int> { 0 };
        for (var pc = 0; pc < instructions.Length; pc++)
        {
            var instruction = instructions[pc];
            switch (instruction.Opcode)
            {
                case LuaIrOpcode.Jump:
                case LuaIrOpcode.JumpIfFalse:
                case LuaIrOpcode.JumpIfTrue:
                case LuaIrOpcode.NumericForPrepare:
                case LuaIrOpcode.NumericForLoop:
                    if ((uint)instruction.B < (uint)instructions.Length)
                    {
                        leaders.Add(instruction.B);
                    }

                    if (pc + 1 < instructions.Length)
                    {
                        leaders.Add(pc + 1);
                    }

                    break;
                case LuaIrOpcode.Return:
                case LuaIrOpcode.TailCall:
                    if (pc + 1 < instructions.Length)
                    {
                        leaders.Add(pc + 1);
                    }

                    break;
            }
        }

        var starts = leaders.ToArray();
        var builder = ImmutableArray.CreateBuilder<LuaIrBasicBlock>(starts.Length);
        for (var index = 0; index < starts.Length; index++)
        {
            var start = starts[index];
            var end = index + 1 < starts.Length ? starts[index + 1] : instructions.Length;
            var last = instructions[end - 1];
            var successors = ImmutableArray.CreateBuilder<int>(2);
            switch (last.Opcode)
            {
                case LuaIrOpcode.Jump:
                    successors.Add(last.B);
                    break;
                case LuaIrOpcode.JumpIfFalse:
                case LuaIrOpcode.JumpIfTrue:
                case LuaIrOpcode.NumericForPrepare:
                case LuaIrOpcode.NumericForLoop:
                    successors.Add(last.B);
                    if (end < instructions.Length)
                    {
                        successors.Add(end);
                    }

                    break;
                case LuaIrOpcode.Return:
                case LuaIrOpcode.TailCall:
                    break;
                default:
                    if (end < instructions.Length)
                    {
                        successors.Add(end);
                    }

                    break;
            }

            builder.Add(new LuaIrBasicBlock(start, end - start, successors.ToImmutable()));
        }

        return builder.ToImmutable();
    }
}
