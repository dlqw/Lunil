using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Lunil.IR.Canonical;

namespace Lunil.CodeGen.Cil.Analysis;

/// <summary>
/// Owner-scoped memo for canonical module verification. Each owner (tier compiler, plan cache,
/// test) holds one instance; verification is deterministic over the immutable module, so every
/// owner verifies a module at most once while it holds the instance.
/// </summary>
internal sealed class LuaIrVerificationCache
{
    private readonly ConditionalWeakTable<
        LuaIrModule,
        Lazy<ImmutableArray<LuaIrVerificationError>>> _results = new();

    public ImmutableArray<LuaIrVerificationError> Verify(LuaIrModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return _results.GetValue(
            module,
            static value => new Lazy<ImmutableArray<LuaIrVerificationError>>(
                () => LuaIrVerifier.Verify(value),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }
}
