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
    ImmutableArray<LuaJitCallTargetProfile> CallTargets)
{
    public ImmutableArray<LuaJitValueKinds> CallArgumentKinds { get; init; } = [];
}

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
                instruction.Opcode is LuaIrOpcode.Call or LuaIrOpcode.TailCall
                    ? Math.Max(0, instruction.B)
                    : 0,
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

    public void Merge(LuaJitFunctionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.ArgumentKinds.Length != _argumentKinds.Length)
        {
            throw new ArgumentException(
                "Imported profile parameter count does not match the function.",
                nameof(profile));
        }

        AddSaturating(ref _samples, profile.Samples);
        for (var argument = 0; argument < _argumentKinds.Length; argument++)
        {
            Interlocked.Or(ref _argumentKinds[argument], (long)profile.ArgumentKinds[argument]);
        }

        foreach (var importedSite in profile.Sites)
        {
            var site = _sites.GetOrAdd(
                importedSite.ProgramCounter,
                _ => new SiteAccumulator(
                    importedSite.ProgramCounter,
                    importedSite.Opcode,
                    importedSite.CallArgumentKinds.Length,
                    maximumPolymorphicShapes));
            site.Merge(importedSite);
        }
    }

    private static void AddKind(ref long kinds, LuaValue value) =>
        Interlocked.Or(ref kinds, 1L << (int)value.Kind);

    private sealed class SiteAccumulator(
        int programCounter,
        LuaIrOpcode opcode,
        int callArgumentCount,
        int maximumPolymorphicShapes)
    {
        private readonly Lock _signaturesGate = new();
        private readonly Dictionary<TableSignature, long> _tableShapes = [];
        private readonly Dictionary<CallTargetSignature, long> _callTargets = [];
        private long _samples;
        private long _firstOperandKinds;
        private long _secondOperandKinds;
        private long _thirdOperandKinds;
        private readonly long[] _callArgumentKinds = new long[callArgumentCount];
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
                case LuaIrOpcode.NumericForLoop:
                    AddKind(
                        ref _firstOperandKinds,
                        LuaCodegenAbiV1.ReadRegister(thread, frame, instruction.A));
                    AddKind(
                        ref _secondOperandKinds,
                        LuaCodegenAbiV1.ReadRegister(thread, frame, instruction.A + 1));
                    AddKind(
                        ref _thirdOperandKinds,
                        LuaCodegenAbiV1.ReadRegister(thread, frame, instruction.A + 2));
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
                    if (instruction.B >= 0 &&
                        instruction.B != _callArgumentKinds.Length)
                    {
                        throw new InvalidOperationException(
                            "The observed call argument window changed for a canonical site.");
                    }
                    for (var argument = 0; argument < Math.Max(0, instruction.B); argument++)
                    {
                        AddKind(
                            ref _callArgumentKinds[argument],
                            LuaCodegenAbiV1.ReadRegister(
                                thread,
                                frame,
                                instruction.A + argument + 1));
                    }
                    break;
            }
        }

        public LuaJitSiteProfile Snapshot()
        {
            lock (_signaturesGate)
            {
                var callArgumentKinds = ImmutableArray.CreateBuilder<LuaJitValueKinds>(
                    _callArgumentKinds.Length);
                for (var argument = 0; argument < _callArgumentKinds.Length; argument++)
                {
                    callArgumentKinds.Add((LuaJitValueKinds)Interlocked.Read(
                        ref _callArgumentKinds[argument]));
                }

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
                            pair.Value))])
                {
                    CallArgumentKinds = callArgumentKinds.MoveToImmutable(),
                };
            }
        }

        public void Merge(LuaJitSiteProfile profile)
        {
            if (profile.ProgramCounter != ProgramCounter || profile.Opcode != opcode)
            {
                throw new ArgumentException(
                    "Imported profile site does not match the accumulator.",
                    nameof(profile));
            }

            AddSaturating(ref _samples, profile.Samples);
            Interlocked.Or(ref _firstOperandKinds, (long)profile.FirstOperandKinds);
            Interlocked.Or(ref _secondOperandKinds, (long)profile.SecondOperandKinds);
            Interlocked.Or(ref _thirdOperandKinds, (long)profile.ThirdOperandKinds);
            if (profile.CallArgumentKinds.Length != _callArgumentKinds.Length)
            {
                throw new ArgumentException(
                    "Imported call argument count does not match the canonical site.",
                    nameof(profile));
            }
            for (var argument = 0; argument < _callArgumentKinds.Length; argument++)
            {
                Interlocked.Or(
                    ref _callArgumentKinds[argument],
                    (long)profile.CallArgumentKinds[argument]);
            }
            AddSaturating(ref _branchTaken, profile.BranchTaken);
            AddSaturating(ref _branchNotTaken, profile.BranchNotTaken);
            if (profile.IsMegamorphic)
            {
                Volatile.Write(ref _megamorphic, 1);
            }

            foreach (var shape in profile.TableShapes)
            {
                AddSignature(
                    _tableShapes,
                    new TableSignature(
                        shape.KeyKinds,
                        shape.ArrayCapacity,
                        shape.ShapeVersion,
                        shape.MetatableVersion,
                        shape.HasMetatable),
                    shape.Samples);
            }

            foreach (var target in profile.CallTargets)
            {
                AddSignature(
                    _callTargets,
                    new CallTargetSignature(
                        target.Kind,
                        target.ModuleContentId,
                        target.FunctionId,
                        target.NativeName),
                    target.Samples);
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
            AddSignature(_tableShapes, signature, 1);
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

            AddSignature(_callTargets, signature, 1);
        }

        private void AddSignature<T>(
            Dictionary<T, long> signatures,
            T signature,
            long samples)
            where T : notnull
        {
            lock (_signaturesGate)
            {
                if (signatures.TryGetValue(signature, out var existingSamples))
                {
                    signatures[signature] = SaturatingAdd(existingSamples, samples);
                    return;
                }

                if (signatures.Count >= maximumPolymorphicShapes)
                {
                    Volatile.Write(ref _megamorphic, 1);
                    return;
                }

                signatures.Add(signature, samples);
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

    private static void AddSaturating(ref long target, long value)
    {
        while (true)
        {
            var current = Interlocked.Read(ref target);
            var updated = SaturatingAdd(current, value);
            if (Interlocked.CompareExchange(ref target, updated, current) == current)
            {
                return;
            }
        }
    }

    private static long SaturatingAdd(long left, long right) =>
        left >= long.MaxValue - right ? long.MaxValue : left + right;
}
