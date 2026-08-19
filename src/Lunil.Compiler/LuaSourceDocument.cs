using Lunil.Core.Text;

namespace Lunil.Compiler;

/// <summary>An immutable Lua source input and its logical diagnostic/debug identity.</summary>
public sealed record LuaSourceDocument
{
    public LuaSourceDocument(SourceText text, string? sourceName = null)
    {
        LunilGuard.NotNull(text);
        if (sourceName is not null)
        {
            LunilGuard.NotNullOrWhiteSpace(sourceName);
        }

        Text = text;
        SourceName = sourceName;
    }

    public SourceText Text { get; }

    /// <summary>
    /// Gets the logical source identity written to canonical debug metadata. This is not
    /// interpreted as a host file-system path.
    /// </summary>
    public string? SourceName { get; }

    public static LuaSourceDocument FromUtf8(string text, string? sourceName = null) =>
        new(SourceText.FromUtf8(text), sourceName);

    public static LuaSourceDocument FromBytes(
        ReadOnlySpan<byte> bytes,
        string? sourceName = null) =>
        new(new SourceText(bytes), sourceName);

    /// <summary>
    /// Wraps an existing UTF-8 buffer without copying it. The caller must guarantee the
    /// array is never mutated afterward (for example a workspace document's canonical
    /// byte representation reused across analysis passes).
    /// </summary>
    public static LuaSourceDocument FromOwnedBytes(
        byte[] bytes,
        string? sourceName = null) =>
        new(SourceText.FromOwnedArray(bytes), sourceName);
}
