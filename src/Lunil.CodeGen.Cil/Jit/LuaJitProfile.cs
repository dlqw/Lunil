using System.Collections.Immutable;
using System.Collections.Concurrent;
using Lunil.IR.Canonical;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.CodeGen.Cil.Jit;

#pragma warning disable CA1720 // Integer, float, and string are exact Lua value kinds.
[Flags]
public enum LuaJitValueKinds : ushort
{
    None = 0,
    Nil = 1 << (int)LuaValueKind.Nil,
    Boolean = 1 << (int)LuaValueKind.Boolean,
    Integer = 1 << (int)LuaValueKind.Integer,
    Float = 1 << (int)LuaValueKind.Float,
    String = 1 << (int)LuaValueKind.String,
    Table = 1 << (int)LuaValueKind.Table,
    Function = 1 << (int)LuaValueKind.Function,
    Thread = 1 << (int)LuaValueKind.Thread,
    Userdata = 1 << (int)LuaValueKind.Userdata,
    LightUserdata = 1 << (int)LuaValueKind.LightUserdata,
}
#pragma warning restore CA1720

public enum LuaJitCallTargetKind : byte
{
    Lua,
    Native,
    Unknown,
}

public sealed record LuaJitTableShapeProfile(
    LuaJitValueKinds KeyKinds,
    int ArrayCapacity,
    ulong ShapeVersion,
    ulong MetatableVersion,
    bool HasMetatable,
    long Samples);

public sealed record LuaJitCallTargetProfile(
    LuaJitCallTargetKind Kind,
    string ModuleContentId,
    int FunctionId,
    string NativeName,
    long Samples);

public sealed record LuaJitSiteProfile(
    int ProgramCounter,
    LuaIrOpcode Opcode,
    long Samples,
    LuaJitValueKinds FirstOperandKinds,
    LuaJitValueKinds SecondOperandKinds,
    LuaJitValueKinds ThirdOperandKinds,
    long BranchTaken,
    long BranchNotTaken,
    bool IsMegamorphic,
    ImmutableArray<LuaJitTableShapeProfile> TableShapes,
    ImmutableArray<LuaJitCallTargetProfile> CallTargets);

public sealed record LuaJitFunctionProfile(
    long Samples,
    ImmutableArray<LuaJitValueKinds> ArgumentKinds,
    ImmutableArray<LuaJitSiteProfile> Sites);

internal sealed class LuaJitProfileAccumulator(
    int parameterCount,
    int maximumPolymorphicShapes)
{
    private readonly long[] _argumentKinds = new long[parameterCount];
    private readonly ConcurrentDictionary<int, SiteAccumulator> _sites = [];
    private long _samples;

    public long Samples => Interlocked.Read(ref _samples);

    public void Observe(
        LuaExecutionContext context,
        LuaThread thread,
        LuaFrame frame,
        int programCounter,
        LuaIrInstruction instruction,
        Func<LuaIrModule, string> getModuleContentId)
    {
        Interlocked.Increment(ref _samples);
        if (programCounter == 0)
        {
            for (var argument = 0; argument < _argumentKinds.Length; argument++)
            {
                AddKind(
                    ref _argumentKinds[argument],
                    LuaCodegenAbiV1.ReadRegister(thread, frame, argument));
            }
        }

        var site = _sites.GetOrAdd(
            programCounter,
            programCounter => new SiteAccumulator(
                programCounter,
                instruction.Opcode,
                maximumPolymorphicShapes));
        site.Observe(context, thread, frame, instruction, getModuleContentId);
    }

    public LuaJitFunctionProfile Snapshot()
    {
        var argumentKinds = ImmutableArray.CreateBuilder<LuaJitValueKinds>(
            _argumentKinds.Length);
        for (var argument = 0; argument < _argumentKinds.Length; argument++)
        {
            argumentKinds.Add((LuaJitValueKinds)Interlocked.Read(
                ref _argumentKinds[argument]));
        }

        return new LuaJitFunctionProfile(
            Samples,
            argumentKinds.MoveToImmutable(),
            [.. _sites.Values
                .OrderBy(static site => site.ProgramCounter)
                .Select(static site => site.Snapshot())]);
    }

    private static void AddKind(ref long kinds, LuaValue value) =>
        Interlocked.Or(ref kinds, 1L << (int)value.Kind);

    private sealed class SiteAccumulator(
        int programCounter,
        LuaIrOpcode opcode,
        int maximumPolymorphicShapes)
    {
        private readonly Lock _signaturesGate = new();
        private readonly Dictionary<TableSignature, long> _tableShapes = [];
        private readonly Dictionary<CallTargetSignature, long> _callTargets = [];
        private long _samples;
        private long _firstOperandKinds;
        private long _secondOperandKinds;
        private long _thirdOperandKinds;
        private long _branchTaken;
        private long _branchNotTaken;
        private int _megamorphic;

        public int ProgramCounter { get; } = programCounter;

        public void Observe(
            LuaExecutionContext context,
            LuaThread thread,
            LuaFrame frame,
            LuaIrInstruction instruction,
            Func<LuaIrModule, string> getModuleContentId)
        {
            Interlocked.Increment(ref _samples);
            switch (instruction.Opcode)
            {
                case LuaIrOpcode.Unary:
                    AddKind(
                        ref _firstOperandKinds,
                        LuaCodegenAbiV1.ReadRegister(thread, frame, instruction.B));
                    break;
                case LuaIrOpcode.Binary:
                    AddKind(
                        ref _firstOperandKinds,
                        LuaCodegenAbiV1.ReadRegister(thread, frame, instruction.B));
                    AddKind(
                        ref _secondOperandKinds,
                        LuaCodegenAbiV1.ReadRegister(thread, frame, instruction.C));
                    break;
                case LuaIrOpcode.JumpIfFalse:
                case LuaIrOpcode.JumpIfTrue:
                    ObserveBranch(thread, frame, instruction);
                    break;
                case LuaIrOpcode.GetTable:
                    ObserveTable(
                        LuaCodegenAbiV1.ReadRegister(thread, frame, instruction.B),
                        LuaCodegenAbiV1.ReadRegister(thread, frame, instruction.C));
                    break;
                case LuaIrOpcode.SetTable:
                    ObserveTable(
                        LuaCodegenAbiV1.ReadRegister(thread, frame, instruction.A),
                        LuaCodegenAbiV1.ReadRegister(thread, frame, instruction.B));
                    AddKind(
                        ref _thirdOperandKinds,
                        LuaCodegenAbiV1.ReadRegister(thread, frame, instruction.C));
                    break;
                case LuaIrOpcode.Call:
                case LuaIrOpcode.TailCall:
                    ObserveCallTarget(
                        LuaCodegenAbiV1.ReadRegister(thread, frame, instruction.A),
                        getModuleContentId);
                    break;
            }
        }

        public LuaJitSiteProfile Snapshot()
        {
            lock (_signaturesGate)
            {
                return new LuaJitSiteProfile(
                    ProgramCounter,
                    opcode,
                    Interlocked.Read(ref _samples),
                    (LuaJitValueKinds)Interlocked.Read(ref _firstOperandKinds),
                    (LuaJitValueKinds)Interlocked.Read(ref _secondOperandKinds),
                    (LuaJitValueKinds)Interlocked.Read(ref _thirdOperandKinds),
                    Interlocked.Read(ref _branchTaken),
                    Interlocked.Read(ref _branchNotTaken),
                    Volatile.Read(ref _megamorphic) != 0,
                    [.. _tableShapes
                        .OrderBy(static pair => pair.Key.ArrayCapacity)
                        .ThenBy(static pair => pair.Key.ShapeVersion)
                        .ThenBy(static pair => pair.Key.MetatableVersion)
                        .ThenBy(static pair => pair.Key.KeyKinds)
                        .Select(static pair => new LuaJitTableShapeProfile(
                            pair.Key.KeyKinds,
                            pair.Key.ArrayCapacity,
                            pair.Key.ShapeVersion,
                            pair.Key.MetatableVersion,
                            pair.Key.HasMetatable,
                            pair.Value))],
                    [.. _callTargets
                        .OrderBy(static pair => pair.Key.Kind)
                        .ThenBy(static pair => pair.Key.ModuleContentId, StringComparer.Ordinal)
                        .ThenBy(static pair => pair.Key.FunctionId)
                        .ThenBy(static pair => pair.Key.NativeName, StringComparer.Ordinal)
                        .Select(static pair => new LuaJitCallTargetProfile(
                            pair.Key.Kind,
                            pair.Key.ModuleContentId,
                            pair.Key.FunctionId,
                            pair.Key.NativeName,
                            pair.Value))]);
            }
        }

        private void ObserveBranch(
            LuaThread thread,
            LuaFrame frame,
            LuaIrInstruction instruction)
        {
            var truthy = LuaCodegenAbiV1.ReadRegister(
                thread,
                frame,
                instruction.A).IsTruthy;
            AddKind(
                ref _firstOperandKinds,
                LuaCodegenAbiV1.ReadRegister(thread, frame, instruction.A));
            var taken = instruction.Opcode == LuaIrOpcode.JumpIfTrue ? truthy : !truthy;
            if (taken)
            {
                Interlocked.Increment(ref _branchTaken);
            }
            else
            {
                Interlocked.Increment(ref _branchNotTaken);
            }
        }

        private void ObserveTable(LuaValue target, LuaValue key)
        {
            AddKind(ref _firstOperandKinds, target);
            AddKind(ref _secondOperandKinds, key);
            if (target.Kind != LuaValueKind.Table)
            {
                return;
            }

            var table = target.AsTable();
            var signature = new TableSignature(
                ToKinds(key),
                table.ArrayCapacity,
                table.ShapeVersion,
                table.MetatableVersion,
                table.Metatable is not null);
            AddSignature(_tableShapes, signature);
        }

        private void ObserveCallTarget(
            LuaValue function,
            Func<LuaIrModule, string> getModuleContentId)
        {
            AddKind(ref _firstOperandKinds, function);
            CallTargetSignature signature;
            if (function.TryGetClosure() is { } closure)
            {
                signature = new CallTargetSignature(
                    LuaJitCallTargetKind.Lua,
                    getModuleContentId(closure.Module),
                    closure.Function.Id,
                    string.Empty);
            }
            else if (function.TryGetNativeFunction() is { } native)
            {
                signature = new CallTargetSignature(
                    LuaJitCallTargetKind.Native,
                    string.Empty,
                    -1,
                    native.Name);
            }
            else
            {
                signature = new CallTargetSignature(
                    LuaJitCallTargetKind.Unknown,
                    string.Empty,
                    -1,
                    string.Empty);
            }

            AddSignature(_callTargets, signature);
        }

        private void AddSignature<T>(Dictionary<T, long> signatures, T signature)
            where T : notnull
        {
            lock (_signaturesGate)
            {
                if (signatures.TryGetValue(signature, out var samples))
                {
                    signatures[signature] = checked(samples + 1);
                    return;
                }

                if (signatures.Count >= maximumPolymorphicShapes)
                {
                    Volatile.Write(ref _megamorphic, 1);
                    return;
                }

                signatures.Add(signature, 1);
            }
        }

        private static LuaJitValueKinds ToKinds(LuaValue value) =>
            (LuaJitValueKinds)(1 << (int)value.Kind);

        private readonly record struct TableSignature(
            LuaJitValueKinds KeyKinds,
            int ArrayCapacity,
            ulong ShapeVersion,
            ulong MetatableVersion,
            bool HasMetatable);

        private readonly record struct CallTargetSignature(
            LuaJitCallTargetKind Kind,
            string ModuleContentId,
            int FunctionId,
            string NativeName);
    }
}
