namespace Lunil.Workspace;

/// <summary>Stable logical identity of one Lua module inside a workspace.</summary>
public sealed record LuaModuleIdentity
{
    public LuaModuleIdentity(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A module identity cannot contain a null character.", nameof(name));
        }

        Name = name.Trim().Replace('\\', '/');
    }

    public string Name { get; }

    public override string ToString() => Name;
}
