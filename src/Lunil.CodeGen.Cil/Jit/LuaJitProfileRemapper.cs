using System.Collections.Immutable;
using Lunil.IR.Canonical;
using Lunil.Runtime.Execution;

namespace Lunil.CodeGen.Cil.Jit;

public enum LuaJitProfileRemapStatus : byte
{
    Remapped,
    Rejected,
}

public sealed record LuaJitProfileRemapResult(
    LuaJitProfileRemapStatus Status,
    byte[]? Payload,
    int RemappedFunctionCount,
    int IncompatibleFunctionCount,
    int AddedFunctionCount,
    int RemovedFunctionCount,
    string? DiagnosticCode = null,
    string? Message = null)
{
    public bool Succeeded => Status == LuaJitProfileRemapStatus.Remapped;
}

/// <summary>
/// Conservatively carries JIT observations to a new module content identity. A function profile
/// is retained only when its lexical identity, signature, upvalue layout, and every canonical
/// instruction operand remain unchanged.
/// </summary>
public static class LuaJitProfileRemapper
{
    public static LuaJitProfileRemapResult Remap(
        LuaIrModule sourceModule,
        LuaIrModule targetModule,
        ReadOnlySpan<byte> sourcePayload)
    {
        ArgumentNullException.ThrowIfNull(sourceModule);
        ArgumentNullException.ThrowIfNull(targetModule);

        try
        {
            var sourceProfile = LuaJitProfileCodec.Deserialize(sourceModule, sourcePayload);
            return RemapCore(sourceModule, targetModule, sourceProfile);
        }
        catch (LuaJitProfileCodec.ProfileFormatException exception)
        {
            return Rejected(exception.DiagnosticCode, exception.Message);
        }
        catch (Exception exception) when (exception is ArgumentException or
            InvalidOperationException or OverflowException)
        {
            return Rejected(LuaJitProfileDiagnosticCodes.InvalidProfile, exception.Message);
        }
    }

    private static LuaJitProfileRemapResult RemapCore(
        LuaIrModule sourceModule,
        LuaIrModule targetModule,
        LuaJitModuleProfile sourceProfile)
    {
        var sourceByKey = sourceModule.Functions.ToDictionary(
            function => LuaFunctionIdentity.GetLogicalKey(sourceModule, function.Id),
            StringComparer.Ordinal);
        var targetByKey = targetModule.Functions.ToDictionary(
            function => LuaFunctionIdentity.GetLogicalKey(targetModule, function.Id),
            StringComparer.Ordinal);
        var sourceProfiles = sourceProfile.Functions.ToDictionary(
            static entry => entry.FunctionId);
        var targetContentId = LuaJitModuleIdentity.Create(targetModule);
        var selfTargetMap = sourceByKey.Values
            .Where(source => targetByKey.ContainsKey(LuaFunctionIdentity.GetLogicalKey(
                sourceModule,
                source.Id)))
            .ToDictionary(
                static source => source.Id,
                source => targetByKey[LuaFunctionIdentity.GetLogicalKey(sourceModule, source.Id)].Id);

        var profiles = new LuaJitFunctionProfile[targetModule.Functions.Length];
        var remapped = 0;
        var incompatible = 0;
        var added = 0;
        foreach (var target in targetModule.Functions)
        {
            var key = LuaFunctionIdentity.GetLogicalKey(targetModule, target.Id);
            if (!sourceByKey.TryGetValue(key, out var source))
            {
                profiles[target.Id] = Empty(target);
                added++;
                continue;
            }

            if (!IsStructurallyCompatible(source, target))
            {
                profiles[target.Id] = Empty(target);
                incompatible++;
                continue;
            }

            profiles[target.Id] = RemapProfile(
                sourceProfiles[source.Id].Profile,
                sourceProfile.ModuleContentId,
                targetContentId,
                selfTargetMap);
            remapped++;
        }

        var removed = sourceByKey.Keys.Count(key => !targetByKey.ContainsKey(key));
        var payload = LuaJitProfileCodec.Serialize(targetModule, profiles);
        return new LuaJitProfileRemapResult(
            LuaJitProfileRemapStatus.Remapped,
            payload,
            remapped,
            incompatible,
            added,
            removed);
    }

    private static bool IsStructurallyCompatible(
        LuaIrFunction source,
        LuaIrFunction target)
    {
        if (source.ParameterCount != target.ParameterCount ||
            source.IsVarArg != target.IsVarArg ||
            !string.Equals(
                LuaFunctionIdentity.GetUpvalueLayoutFingerprint(source),
                LuaFunctionIdentity.GetUpvalueLayoutFingerprint(target),
                StringComparison.Ordinal) ||
            source.Instructions.Length != target.Instructions.Length)
        {
            return false;
        }

        for (var programCounter = 0; programCounter < source.Instructions.Length;
             programCounter++)
        {
            var left = source.Instructions[programCounter];
            var right = target.Instructions[programCounter];
            if (left.Opcode != right.Opcode || left.A != right.A || left.B != right.B ||
                left.C != right.C || left.D != right.D)
            {
                return false;
            }
        }

        return true;
    }

    private static LuaJitFunctionProfile RemapProfile(
        LuaJitFunctionProfile profile,
        string sourceContentId,
        string targetContentId,
        IReadOnlyDictionary<int, int> selfTargetMap) => profile with
        {
            Sites = [.. profile.Sites.Select(site => site with
        {
            CallTargets = [.. site.CallTargets.SelectMany(target => RemapCallTarget(
                target,
                sourceContentId,
                targetContentId,
                selfTargetMap))],
        })],
        };

    private static IEnumerable<LuaJitCallTargetProfile> RemapCallTarget(
        LuaJitCallTargetProfile target,
        string sourceContentId,
        string targetContentId,
        IReadOnlyDictionary<int, int> selfTargetMap)
    {
        if (target.Kind != LuaJitCallTargetKind.Lua || !string.Equals(
            target.ModuleContentId,
            sourceContentId,
            StringComparison.Ordinal))
        {
            yield return target;
            yield break;
        }

        if (selfTargetMap.TryGetValue(target.FunctionId, out var targetFunctionId))
        {
            yield return target with
            {
                ModuleContentId = targetContentId,
                FunctionId = targetFunctionId,
            };
        }
    }

    private static LuaJitFunctionProfile Empty(LuaIrFunction function) => new(
        Samples: 0,
        Enumerable.Repeat(LuaJitValueKinds.None, function.ParameterCount).ToImmutableArray(),
        Sites: []);

    private static LuaJitProfileRemapResult Rejected(
        string diagnosticCode,
        string message) => new(
        LuaJitProfileRemapStatus.Rejected,
        Payload: null,
        RemappedFunctionCount: 0,
        IncompatibleFunctionCount: 0,
        AddedFunctionCount: 0,
        RemovedFunctionCount: 0,
        diagnosticCode,
        message);
}
