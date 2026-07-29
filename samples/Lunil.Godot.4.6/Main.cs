using Godot;
using Lunil.Godot;
using Lunil.Hosting;

namespace Lunil.Godot.Sample;

public partial class Main : Node
{
    private LuaGodotGameLoop? _loop;

    public override void _Ready()
    {
        GD.Print("Lunil Godot sample starting.");
        Console.WriteLine("Lunil Godot sample starting.");
        var entry = new LuaGodotScriptResource
        {
            AssetId = "@res://main.lua",
            ModuleName = "main",
            Source = "counter = 1; coroutine.yield(); counter = counter + 1; return counter",
        };
        _loop = new LuaGodotGameLoop
        {
            Name = "LunilHost",
            EntryScript = entry,
            StartOnReady = false,
        };
        AddChild(_loop);
        _loop.SetProcess(true);
        _loop.TickCompleted += OnTickCompleted;
        _loop.Initialize();
    }

    private void OnTickCompleted(LuaGameLoopTickResult result)
    {
        if (!result.Succeeded || _loop?.EntryOperation is not { } operation)
        {
            GetTree().Quit(1);
            return;
        }

        if (operation.Status != LuaGameLoopOperationStatus.Completed)
        {
            return;
        }

        var value = operation.Values[0].AsInteger();
        GD.Print("Lunil Godot sample completed with ", value, ".");
        Console.WriteLine($"Lunil Godot sample completed with {value}.");
        GetTree().Quit(0);
    }
}
