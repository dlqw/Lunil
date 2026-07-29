#if TOOLS
using Godot;

#pragma warning disable CA1050 // Godot editor plugin scripts must remain in the global namespace.
[Tool]
public partial class LunilGodotPlugin : EditorPlugin
{
    public override void _EnterTree()
    {
        // The companion scripts are discovered as Godot global classes. The plugin keeps
        // addon activation explicit while the implementation remains in the NuGet package.
    }

    public override void _ExitTree()
    {
    }
}
#pragma warning restore CA1050
#endif
