using Godot;
using Lunil.Godot;

#pragma warning disable CA1050 // Godot global-class scripts must remain in the global namespace.
[GlobalClass]
public partial class LunilScript : LuaGodotScriptResource
{
    [Export(PropertyHint.MultilineText)]
    public override string Source
    {
        get => base.Source;
        set => base.Source = value;
    }

    [Export]
    public override string AssetId
    {
        get => base.AssetId;
        set => base.AssetId = value;
    }

    [Export]
    public override string ModuleName
    {
        get => base.ModuleName;
        set => base.ModuleName = value;
    }
}
#pragma warning restore CA1050
