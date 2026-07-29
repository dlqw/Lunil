using Godot;
using Lunil.Compiler;
using Lunil.Hosting;

namespace Lunil.Godot;

/// <summary>Godot Process/PhysicsProcess owner for an engine-neutral Lunil game-loop host.</summary>
[GlobalClass]
public partial class LuaGodotGameLoop : Node
{
    private LuaGodotDispatcher? _dispatcher;
    private LuaGameLoopHost? _gameLoop;
    private LuaGameLoopOperation? _entryOperation;
    private bool _treePaused;

    [Export]
    public virtual LuaGodotScriptResource? EntryScript { get; set; }

    [Export]
    public virtual global::Godot.Collections.Array<LuaGodotScriptResource> Modules { get; set; } = [];

    [Export]
    public virtual bool StartOnReady { get; set; } = true;

    [Export]
    public virtual bool PauseWithTree { get; set; } = true;

    [Export(PropertyHint.Range, "1,65536,1")]
    public virtual int MaximumDispatchedCallbacks { get; set; } = 1_024;

    public bool IsInitialized => _gameLoop is not null;

    public LuaGameLoopHost GameLoop => _gameLoop ?? throw new InvalidOperationException(
        "The Godot game-loop host is not initialized.");

    public LuaGameLoopOperation? EntryOperation => _entryOperation;

    public event Action<LuaGameLoopTickResult>? TickCompleted;

    public event Action<Exception>? HostFailed;

    /// <summary>Applies host capabilities and generated bindings before initialization.</summary>
    public Func<LuaGameLoopHostOptions, LuaGameLoopHostOptions>? ConfigureHostOptions { get; set; }

    public override void _Ready()
    {
        if (!Engine.IsEditorHint() && StartOnReady)
        {
            Initialize();
        }
    }

    public override void _Process(double delta)
    {
        _ = delta;
        if (!IsPausedByTree())
        {
            TickUpdate();
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        _ = delta;
        if (!IsPausedByTree())
        {
            TickPhysics();
        }
    }

    public override void _ExitTree() => Shutdown();

    public override void _Notification(int what)
    {
        if (what == NotificationPaused)
        {
            _treePaused = true;
        }
        else if (what == NotificationUnpaused)
        {
            _treePaused = false;
        }
        else if (what == NotificationPredelete)
        {
            Shutdown();
        }
    }

    public void Initialize()
    {
        if (_gameLoop is not null)
        {
            return;
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumDispatchedCallbacks);
        var resources = new List<LuaGodotScriptResource>();
        if (EntryScript is not null)
        {
            resources.Add(EntryScript);
        }

        resources.AddRange(Modules.Where(static resource => resource is not null));
        var resolver = new LuaGodotAssetResolver(resources);
        _dispatcher = new LuaGodotDispatcher();
        var hostOptions = LuaHostOptions.Default with
        {
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
            ModuleResolver = resolver,
            StandardLibrary = LuaHostCapabilityProfiles.Create(LuaHostProfile.Trusted) with
            {
                FileSystem = resolver,
            },
        };
        var gameLoopOptions = new LuaGameLoopHostOptions
        {
            HostOptions = hostOptions,
            Dispatcher = _dispatcher,
            TimeProvider = new LuaGodotTimeProvider(),
            Console = new LuaGodotConsole(),
            ModuleResolver = resolver,
            AssetResolver = resolver,
            PersistentStore = new LuaGodotPersistentStore(),
        };
        if (ConfigureHostOptions is not null)
        {
            gameLoopOptions = ConfigureHostOptions(gameLoopOptions) ??
                throw new InvalidOperationException(
                    "The Godot host options callback returned null.");
        }

        try
        {
            _gameLoop = new LuaGameLoopHost(gameLoopOptions);
            LuaGodotRuntimeRegistry.Register(this);
            if (EntryScript is not null)
            {
                var source = LuaSourceDocument.FromBytes(
                    EntryScript.GetBytes().Span,
                    EntryScript.GetEffectiveAssetId());
                var compilation = _gameLoop.Host.Compile(source);
                if (!compilation.Succeeded)
                {
                    throw new InvalidOperationException(
                        "The Godot Lua entry script did not compile: " +
                        string.Join("; ", compilation.Diagnostics));
                }

                _entryOperation = _gameLoop.Start(compilation);
            }
        }
        catch
        {
            Shutdown();
            throw;
        }
    }

    public LuaGameLoopTickResult? TickUpdate() => Tick(fixedTick: false);

    public LuaGameLoopTickResult? TickPhysics() => Tick(fixedTick: true);

    public void Shutdown()
    {
        var gameLoop = _gameLoop;
        _gameLoop = null;
        _entryOperation = null;
        LuaGodotRuntimeRegistry.Unregister(this);
        _dispatcher?.Dispose();
        _dispatcher = null;
        gameLoop?.Dispose();
    }

    private LuaGameLoopTickResult? Tick(bool fixedTick)
    {
        if (_gameLoop is null)
        {
            Initialize();
        }

        if (IsPausedByTree())
        {
            return null;
        }

        try
        {
            _dispatcher!.Drain(MaximumDispatchedCallbacks);
            var result = fixedTick ? _gameLoop!.TickFixed() : _gameLoop!.Tick();
            TickCompleted?.Invoke(result);
            return result;
        }
        catch (Exception exception)
        {
            if (HostFailed is { } failed)
            {
                failed(exception);
            }
            else
            {
                GD.PushError(exception.ToString());
            }

            throw;
        }
    }

    private bool IsPausedByTree() => PauseWithTree &&
        (_treePaused || (IsInsideTree() && GetTree().Paused));
}
