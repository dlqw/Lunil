using Lunil.Compiler;
using Lunil.Core.Text;

namespace Lunil.Workspace;

/// <summary>Immutable module source together with stable module and source identities.</summary>
public sealed record LuaWorkspaceDocument
{
    public LuaWorkspaceDocument(LuaModuleIdentity module, LuaSourceDocument source)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(source);
        Module = module;
        Source = source.SourceName is null
            ? new LuaSourceDocument(source.Text, CreateDefaultSourceIdentity(module))
            : source;
    }

    public LuaModuleIdentity Module { get; }

    public LuaSourceDocument Source { get; }

    public string SourceIdentity => Source.SourceName!;

    public static LuaWorkspaceDocument FromUtf8(
        string moduleName,
        string source,
        string? sourceIdentity = null) =>
        new(
            new LuaModuleIdentity(moduleName),
            LuaSourceDocument.FromUtf8(source, sourceIdentity));

    public static LuaWorkspaceDocument FromBytes(
        string moduleName,
        ReadOnlySpan<byte> source,
        string? sourceIdentity = null) =>
        new(
            new LuaModuleIdentity(moduleName),
            new LuaSourceDocument(new SourceText(source), sourceIdentity));

    private static string CreateDefaultSourceIdentity(LuaModuleIdentity module) =>
        "@" + module.Name.Replace('.', '/').TrimStart('@') + ".lua";
}
