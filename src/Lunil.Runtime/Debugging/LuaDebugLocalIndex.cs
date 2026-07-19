using Lunil.IR.Canonical;

namespace Lunil.Runtime.Debugging;

/// <summary>
/// Provides allocation-free active-local lookups for debug and traceback paths.
/// </summary>
internal sealed class LuaDebugLocalIndex
{
    private readonly int[] _boundaries;
    private readonly LuaIrLocalVariable[][] _localsByBoundary;
    private readonly int _instructionCount;

    internal LuaDebugLocalIndex(LuaIrFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        _instructionCount = function.Instructions.Length;
        if (_instructionCount == 0)
        {
            _boundaries = [];
            _localsByBoundary = [];
            return;
        }

        var boundaries = new HashSet<int> { 0 };
        foreach (var local in function.LocalVariables)
        {
            var start = Math.Clamp(local.StartProgramCounter, 0, _instructionCount);
            var end = Math.Clamp(local.EndProgramCounter, start, _instructionCount);
            boundaries.Add(start);
            if (end < _instructionCount)
            {
                boundaries.Add(end);
            }
        }

        _boundaries = [.. boundaries.Order()];
        _localsByBoundary = new LuaIrLocalVariable[_boundaries.Length][];
        for (var boundaryIndex = 0; boundaryIndex < _boundaries.Length; boundaryIndex++)
        {
            var programCounter = _boundaries[boundaryIndex];
            var active = new List<LuaIrLocalVariable>();
            foreach (var local in function.LocalVariables)
            {
                if (local.StartProgramCounter <= programCounter &&
                    programCounter < local.EndProgramCounter)
                {
                    active.Add(local);
                }
            }

            _localsByBoundary[boundaryIndex] = [.. active];
        }
    }

    internal ReadOnlySpan<LuaIrLocalVariable> GetActive(int programCounter)
    {
        if ((uint)programCounter >= (uint)_instructionCount)
        {
            return [];
        }

        var boundaryIndex = Array.BinarySearch(_boundaries, programCounter);
        if (boundaryIndex < 0)
        {
            boundaryIndex = ~boundaryIndex - 1;
        }

        return _localsByBoundary[boundaryIndex];
    }
}
