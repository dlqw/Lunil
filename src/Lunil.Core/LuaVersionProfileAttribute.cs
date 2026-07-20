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

    public bool HasWarnLibrary { get; init; }

    public bool HasCoroutineClose { get; init; }
}
