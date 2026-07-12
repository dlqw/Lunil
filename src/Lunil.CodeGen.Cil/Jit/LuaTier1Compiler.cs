using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Lunil.CodeGen.Cil.Artifacts;
using Lunil.CodeGen.Cil.Emission;
using Lunil.IR.Canonical;

namespace Lunil.CodeGen.Cil.Jit;

internal interface ILuaDynamicCodeCapabilities
{
    bool IsDynamicCodeSupported { get; }

    bool IsDynamicCodeCompiled { get; }
}

internal sealed class RuntimeDynamicCodeCapabilities : ILuaDynamicCodeCapabilities
{
    public static RuntimeDynamicCodeCapabilities Instance { get; } = new();

    public bool IsDynamicCodeSupported => RuntimeFeature.IsDynamicCodeSupported;

    public bool IsDynamicCodeCompiled => RuntimeFeature.IsDynamicCodeCompiled;
}

internal sealed record LuaTier1CompilationResult(
    LuaCompiledMethod? Method,
    long EstimatedCodeBytes,
    ImmutableArray<string> Diagnostics)
{
    public bool Succeeded => Method is not null && Diagnostics.IsEmpty;
}

internal interface ILuaTier1Compiler
{
    LuaTier1CompilationResult Compile(
        LuaIrModule module,
        int functionId,
        CancellationToken cancellationToken);
}

internal sealed class ReflectionEmitLuaTier1Compiler : ILuaTier1Compiler
{
    public static ReflectionEmitLuaTier1Compiler Instance { get; } = new();

    [RequiresDynamicCode("Tier 1 JIT compilation requires Reflection.Emit support.")]
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "The JIT registry checks RuntimeFeature before reaching this compiler.")]
    public LuaTier1CompilationResult Compile(
        LuaIrModule module,
        int functionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var planning = LuaCilCodeGenerator.PlanFunction(module, functionId);
        if (!planning.Succeeded)
        {
            return new LuaTier1CompilationResult(
                null,
                0,
                [.. planning.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}")]);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var emission = ReflectionEmitCilPlanSink.Compile(planning.Plan!);
        if (!emission.Succeeded)
        {
            return new LuaTier1CompilationResult(
                null,
                0,
                [.. emission.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}")]);
        }

        return new LuaTier1CompilationResult(
            emission.Method,
            checked(planning.Plan!.Instructions.Length * 8L),
            []);
    }
}

internal static class LuaJitModuleIdentity
{
    private static readonly ConditionalWeakTable<LuaIrModule, Identity> Identities = new();

    public static string Create(LuaIrModule module) => Identities.GetValue(
        module,
        static module => new Identity(LuaCanonicalModuleSerializer.Sha256Hex(
            LuaCanonicalModuleSerializer.Serialize(module))))
        .ContentId;

    private sealed record Identity(string ContentId);
}
