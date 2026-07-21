namespace Lunil.Core;

/// <summary>
/// Declares the observable runtime contract for one Lua language version.
/// The build-time version generator turns these declarations into a compact,
/// allocation-free lookup table consumed by the runtime and standard library.
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public sealed class LuaVersionProfileAttribute : Attribute
{
    /// <summary>
    /// Identifies the binary chunk adapter selected by this profile. A value of <c>None</c>
    /// means that the version has no adapter compiled into the current build yet.
    /// </summary>
    public LuaChunkFormat ChunkFormat { get; init; }

    public bool SynchronousFinalizerErrors { get; init; }

    public bool SupportsGenerationalCollection { get; init; }

    public bool PreservesDeadThreadOpenUpvalues { get; init; }

    public bool CachesClosuresByUpvalues { get; init; }

    /// <summary>
    /// Whether an integer-valued string coerced by an arithmetic operator produces a float.
    /// </summary>
    public bool ArithmeticStringCoercionProducesFloat { get; init; }

    /// <summary>Whether bitwise operators coerce numeric strings to integers.</summary>
    public bool CoercesNumericStringsForBitwiseOperations { get; init; }

    public bool HasWarnLibrary { get; init; }

    public bool HasCoroutineClose { get; init; }

    /// <summary>Whether locals and iterator resources participate in the to-be-closed protocol.</summary>
    public bool HasToBeClosedProtocol { get; init; }

    public bool HasUtf8Library { get; init; }

    public bool HasBit32Library { get; init; }

    // Standard-library surface capabilities.  These flags are generated alongside the
    // VM/GC profile so version adapters do not duplicate version switches in installers.
    public bool HasRawLength { get; init; }

    public bool HasGlobalUnpack { get; init; }

    public bool HasLoadString { get; init; }

    public bool HasModuleLibrary { get; init; }

    public bool HasTableMove { get; init; }

    public bool HasTablePack { get; init; }

    public bool HasTableCreate { get; init; }

    public bool HasStringPack { get; init; }

    public bool HasStringGFind { get; init; }

    public bool HasLegacyMath { get; init; }

    public bool HasDebugSetCStackLimit { get; init; }

    public bool HasPackageSearchers { get; init; }

    public bool HasPackageLoaders { get; init; }

    public bool HasPackageSeeAll { get; init; }

    public bool HasLegacyTable { get; init; }
}
