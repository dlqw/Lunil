using System.Text;
using Godot;

namespace Lunil.Godot;

/// <summary>A Godot resource that preserves an exact Lunil asset and module identity.</summary>
[GlobalClass]
public partial class LuaGodotScriptResource : Resource
{
    private string _source = string.Empty;

    /// <summary>UTF-8 Lua source stored by the Godot resource.</summary>
    [Export(PropertyHint.MultilineText)]
    public virtual string Source
    {
        get => _source;
        set => _source = value ?? string.Empty;
    }

    /// <summary>Stable compiler source identity, normally beginning with <c>@res://</c>.</summary>
    [Export]
    public virtual string AssetId { get; set; } = string.Empty;

    /// <summary>Exact name exposed to Lua <c>require</c>.</summary>
    [Export]
    public virtual string ModuleName { get; set; } = string.Empty;

    public ReadOnlyMemory<byte> GetBytes() => Encoding.UTF8.GetBytes(_source);

    internal string GetEffectiveAssetId()
    {
        if (!string.IsNullOrWhiteSpace(AssetId))
        {
            return AssetId;
        }

        if (!string.IsNullOrWhiteSpace(ResourcePath))
        {
            return "@" + ResourcePath;
        }

        throw new InvalidOperationException(
            "A Lunil Godot script resource must have an AssetId or ResourcePath.");
    }
}
