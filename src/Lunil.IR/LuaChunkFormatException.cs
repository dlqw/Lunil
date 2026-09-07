namespace Lunil.IR;

/// <summary>
/// Common base of the version-specific binary chunk format exceptions. The message format
/// is identical to the historical per-version messages; the base only lets hosts catch any
/// Lunil chunk failure without knowing the Lua version.
/// </summary>
public class LuaChunkFormatException : FormatException
{
    private protected LuaChunkFormatException(string versionLabel, string reason, int offset)
        : base($"Bad {versionLabel} binary chunk at byte {offset}: {reason}")
    {
        Reason = reason;
        Offset = offset;
    }

    /// <summary>Gets the failure reason without the version and offset prefix.</summary>
    public string Reason { get; }

    /// <summary>Gets the byte offset at which the failure was detected.</summary>
    public int Offset { get; }
}
