using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Lunil.IR.Canonical;

namespace Lunil.CodeGen.Cil.Analysis;

internal static class LuaIrVerificationCache
{
    private static readonly ConditionalWeakTable<
        LuaIrModule,
        Lazy<ImmutableArray<LuaIrVerificationError>>> Results = new();

    public static ImmutableArray<LuaIrVerificationError> Verify(LuaIrModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return Results.GetValue(
            module,
            static value => new Lazy<ImmutableArray<LuaIrVerificationError>>(
                () => LuaIrVerifier.Verify(value),
                LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }
}
