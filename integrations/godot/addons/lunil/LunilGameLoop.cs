using Godot;
using Lunil.Godot;

#pragma warning disable CA1050 // Godot global-class scripts must remain in the global namespace.
[GlobalClass]
public partial class LunilGameLoop : LuaGodotGameLoop
{
    public override void _Ready() => base._Ready();

    public override void _Process(double delta) => base._Process(delta);

    public override void _PhysicsProcess(double delta) => base._PhysicsProcess(delta);

    public override void _ExitTree() => base._ExitTree();

    public override void _Notification(int what) => base._Notification(what);

    [Export]
    public override LuaGodotScriptResource? EntryScript
    {
        get => base.EntryScript;
        set => base.EntryScript = value;
    }

    [Export]
    public override global::Godot.Collections.Array<LuaGodotScriptResource> Modules
    {
        get => base.Modules;
        set => base.Modules = value;
    }

    [Export]
    public override bool StartOnReady
    {
        get => base.StartOnReady;
        set => base.StartOnReady = value;
    }

    [Export]
    public override bool PauseWithTree
    {
        get => base.PauseWithTree;
        set => base.PauseWithTree = value;
    }

    [Export(PropertyHint.Range, "1,65536,1")]
    public override int MaximumDispatchedCallbacks
    {
        get => base.MaximumDispatchedCallbacks;
        set => base.MaximumDispatchedCallbacks = value;
    }
}
#pragma warning restore CA1050
