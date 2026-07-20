using System.Security.Cryptography;
using System.Text;
using Lunil.IR.Canonical;
using Lunil.Runtime.Debugging;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Execution;

/// <summary>Stable lexical and captured-upvalue identities used across module versions.</summary>
public static class LuaFunctionIdentity
{
    public static string GetLogicalKey(LuaIrModule module, int functionId)
    {
        ArgumentNullException.ThrowIfNull(module);
        var function = GetFunction(module, functionId);
        if (function.Id == module.MainFunctionId)
        {
            return "root";
        }

        var segments = new Stack<int>();
        var visited = new HashSet<int>();
        while (function.Id != module.MainFunctionId)
        {
            if (!visited.Add(function.Id) || function.ParentFunctionId < 0)
            {
                throw new ArgumentException(
                    "The canonical function parent graph does not reach the main function.",
                    nameof(module));
            }

            var siblings = module.Functions
                .Where(candidate => candidate.ParentFunctionId == function.ParentFunctionId)
                .OrderBy(static candidate => candidate.Span.Start)
                .ThenBy(static candidate => candidate.Id)
                .ToArray();
            var ordinal = Array.FindIndex(siblings, candidate => candidate.Id == function.Id);
            if (ordinal < 0)
            {
                throw new ArgumentException(
                    "The canonical function is absent from its lexical parent.",
                    nameof(module));
            }

            segments.Push(ordinal);
            function = GetFunction(module, function.ParentFunctionId);
        }

        return "root/" + string.Join('/', segments);
    }

    public static string GetUpvalueLayoutFingerprint(LuaIrFunction function)
    {
        ArgumentNullException.ThrowIfNull(function);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(function.Upvalues.Length);
            foreach (var upvalue in function.Upvalues)
            {
                writer.Write(upvalue.Name);
                writer.Write((byte)upvalue.SourceKind);
                writer.Write(upvalue.SourceIndex);
                writer.Write(upvalue.Kind);
            }
        }

        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }

    private static LuaIrFunction GetFunction(LuaIrModule module, int functionId) =>
        module.Functions.FirstOrDefault(function => function.Id == functionId) ??
        throw new ArgumentOutOfRangeException(
            nameof(functionId),
            "The canonical function id is not present in the module.");
}

/// <summary>
/// Immutable code identity captured by a frame on entry. Mutable specialization and constant
/// caches are owned by this version and never reused by another canonical function version.
/// </summary>
public sealed class LuaFunctionVersion
{
    private readonly LuaFunctionVersionCaches _caches;

    internal LuaFunctionVersion(
        LuaModuleRuntimeData runtimeData,
        LuaIrFunction function,
        long generation)
    {
        RuntimeData = runtimeData;
        Function = function;
        Generation = generation;
        LogicalKey = LuaFunctionIdentity.GetLogicalKey(runtimeData.Module, function.Id);
        UpvalueLayoutFingerprint = LuaFunctionIdentity.GetUpvalueLayoutFingerprint(function);
        _caches = new LuaFunctionVersionCaches(function);
    }

    public LuaIrModule Module => RuntimeData.Module;

    public LuaIrFunction Function { get; }

    public long Generation { get; }

    public string LogicalKey { get; }

    public string UpvalueLayoutFingerprint { get; }

    internal LuaModuleRuntimeData RuntimeData { get; }

    internal bool HasSourceLineInformation => _caches.HasSourceLineInformation;

    internal int FramelessInstructionCount => _caches.FramelessInstructionCount;

    internal ReadOnlySpan<LuaIrLocalVariable> GetActiveDebugLocals(int programCounter) =>
        _caches.GetActiveDebugLocals(programCounter);

    internal bool TryEnterFramelessCall() => _caches.TryEnterFramelessCall();

    internal LuaString GetOrCreateStringConstant(LuaState state, int constantIndex) =>
        _caches.GetOrCreateStringConstant(state, this, constantIndex);

    internal LuaTableAllocationHint GetOrCreateTableAllocationHint(int programCounter) =>
        _caches.GetOrCreateTableAllocationHint(programCounter);

    internal LuaFunctionVersion CreateSuccessor(long generation) => new(
        RuntimeData,
        Function,
        generation);

    internal void Traverse(LuaGcVisitor visitor) => RuntimeData.StringConstants.Traverse(visitor);
}

internal sealed class LuaFunctionSlot(LuaFunctionVersion initial)
{
    private LuaFunctionVersion _current = initial;

    public LuaFunctionVersion Current => Volatile.Read(ref _current);

    public bool TryPublish(LuaFunctionVersion expected, LuaFunctionVersion replacement) =>
        ReferenceEquals(Interlocked.CompareExchange(ref _current, replacement, expected), expected);
}

internal sealed class LuaModuleRuntimeData
{
    private readonly WeakReference<LuaClosure>?[] _closureCache;
    private readonly LuaFunctionVersion?[] _versions;

    public LuaModuleRuntimeData(LuaIrModule module)
    {
        Module = module;
        StringConstants = new LuaModuleStringConstants();
        _versions = new LuaFunctionVersion?[module.Functions.Length];
        _closureCache = new WeakReference<LuaClosure>?[module.Functions.Length];
    }

    public LuaIrModule Module { get; }

    public LuaModuleStringConstants StringConstants { get; }

    public LuaClosure? GetCachedClosure(
        int functionId,
        ReadOnlySpan<LuaUpvalue> upvalues)
    {
        var weakReference = _closureCache[functionId];
        if (weakReference is null || !weakReference.TryGetTarget(out var closure) ||
            !closure.IsAlive || closure.Upvalues.Count != upvalues.Length)
        {
            return null;
        }

        for (var index = 0; index < upvalues.Length; index++)
        {
            if (!ReferenceEquals(closure.Upvalues[index], upvalues[index]))
            {
                return null;
            }
        }

        return closure;
    }

    public void CacheClosure(int functionId, LuaClosure closure) =>
        _closureCache[functionId] = new WeakReference<LuaClosure>(closure);

    public LuaFunctionVersion GetVersion(int functionId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(functionId);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(functionId, _versions.Length);
        if (Volatile.Read(ref _versions[functionId]) is { } existing)
        {
            return existing;
        }

        var function = Module.Functions.FirstOrDefault(candidate => candidate.Id == functionId) ??
            throw new ArgumentOutOfRangeException(
                nameof(functionId),
                "The canonical function id is not present in the module.");
        var candidate = new LuaFunctionVersion(this, function, generation: 1);
        return Interlocked.CompareExchange(ref _versions[functionId], candidate, null) ?? candidate;
    }
}

internal sealed class LuaFunctionVersionCaches
{
    private readonly LuaDebugLocalIndex _debugLocalIndex;
    private readonly LuaString?[] _materializedStringConstants;
    private readonly LuaTableAllocationHint?[] _tableAllocationHints;
    private readonly object _constantGate = new();
    private int _framelessCallEntries;

    public LuaFunctionVersionCaches(LuaIrFunction function)
    {
        _debugLocalIndex = new LuaDebugLocalIndex(function);
        HasSourceLineInformation = function.Instructions.Any(
            static instruction => instruction.SourceLine > 0);
        _materializedStringConstants = new LuaString?[function.Constants.Length];
        _tableAllocationHints = new LuaTableAllocationHint?[function.Instructions.Length];
        FramelessInstructionCount = GetFramelessInstructionCount(function);
    }

    public bool HasSourceLineInformation { get; }

    public int FramelessInstructionCount { get; }

    public ReadOnlySpan<LuaIrLocalVariable> GetActiveDebugLocals(int programCounter) =>
        _debugLocalIndex.GetActive(programCounter);

    public bool TryEnterFramelessCall()
    {
        if (FramelessInstructionCount == 0)
        {
            return false;
        }

        if (Volatile.Read(ref _framelessCallEntries) > 2)
        {
            return true;
        }

        return Interlocked.Increment(ref _framelessCallEntries) > 2;
    }

    public LuaString GetOrCreateStringConstant(
        LuaState state,
        LuaFunctionVersion version,
        int constantIndex)
    {
        var existing = Volatile.Read(ref _materializedStringConstants[constantIndex]);
        if (existing is not null && existing.IsAlive)
        {
            return existing;
        }

        lock (_constantGate)
        {
            existing = _materializedStringConstants[constantIndex];
            if (existing is not null && existing.IsAlive)
            {
                return existing;
            }

            var constant = version.Function.Constants[constantIndex];
            if (constant.Kind != LuaIrConstantKind.String)
            {
                throw new InvalidOperationException("The cached constant is not a string.");
            }

            existing = version.RuntimeData.StringConstants.GetOrCreate(
                state,
                constant.Bytes.AsSpan());
            Volatile.Write(ref _materializedStringConstants[constantIndex], existing);
            return existing;
        }
    }

    public LuaTableAllocationHint GetOrCreateTableAllocationHint(int programCounter)
    {
        var existing = Volatile.Read(ref _tableAllocationHints[programCounter]);
        if (existing is not null)
        {
            return existing;
        }

        var candidate = new LuaTableAllocationHint();
        return Interlocked.CompareExchange(
            ref _tableAllocationHints[programCounter],
            candidate,
            null) ?? candidate;
    }

    private static int GetFramelessInstructionCount(LuaIrFunction function)
    {
        const int maximumInstructions = 16;
        const int maximumRegisters = 32;
        if (function.IsVarArg || function.Upvalues.Length != 0 ||
            function.Instructions.Length is 0 or > maximumInstructions ||
            function.RegisterCount > maximumRegisters)
        {
            return 0;
        }

        for (var programCounter = 0; programCounter < function.Instructions.Length;
             programCounter++)
        {
            var instruction = function.Instructions[programCounter];
            switch (instruction.Opcode)
            {
                case LuaIrOpcode.Move:
                case LuaIrOpcode.LoadNil:
                    break;
                case LuaIrOpcode.Unary when
                    (LuaIrUnaryOperator)instruction.C is LuaIrUnaryOperator.Negate or
                        LuaIrUnaryOperator.BitwiseNot or LuaIrUnaryOperator.LogicalNot:
                    break;
                case LuaIrOpcode.Binary when
                    (LuaIrBinaryOperator)instruction.D is not
                        (LuaIrBinaryOperator.Concatenate or LuaIrBinaryOperator.FloorDivide or
                            LuaIrBinaryOperator.Modulo):
                    break;
                case LuaIrOpcode.Return when instruction.B >= 0:
                    return programCounter + 1;
                default:
                    return 0;
            }
        }

        return 0;
    }
}
