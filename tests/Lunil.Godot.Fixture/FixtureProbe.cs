using System.Security.Cryptography;
using System.Text;
using Godot;
using Lunil.Gameplay.Fixture;
using Lunil.Godot;
using Lunil.Hosting;
using Lunil.Runtime.Values;

namespace Lunil.Godot.Fixture;

public partial class FixtureProbe : Node
{
    private LunilGameLoop? _host;
    private LuaGodotScriptResource? _entry;
    private LuaGodotScriptResource? _module;
    private LuaGodotScriptResource? _gameplayRules;
    private FixtureSignalEmitter? _emitter;
    private LuaGodotSignalSubscription? _signalSubscription;
    private LuaGodotSignalSubscription? _typedSignalSubscription;
    private int _step;
    private int _waitFrames;
    private int _initialRegistryCount;
    private SharedEngineSoakSession? _soakSession;
    private bool _soakMode;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        try
        {
            (_entry, _module, _gameplayRules) = CreateAndReloadResources();
            VerifyPersistentStore();
            _initialRegistryCount = LuaGodotRuntimeRegistry.ActiveHostCount;
            CreateHost();
            var soakSeconds = ReadDoubleArgument("--lunil-soak-seconds=");
            if (soakSeconds > 0.0)
            {
                var warmupSeconds = ReadDoubleArgument(
                    "--lunil-soak-warmup-seconds=", Math.Min(soakSeconds / 3.0, 1800.0));
                var sampleSeconds = ReadDoubleArgument(
                    "--lunil-soak-sample-seconds=",
                    Math.Max(1.0, Math.Min(300.0, (soakSeconds - warmupSeconds) / 6.0)));
                _soakSession = new SharedEngineSoakSession(
                    _host!.GameLoop,
                    _host.TickUpdate,
                    "godot",
                    TimeSpan.FromSeconds(soakSeconds),
                    TimeSpan.FromSeconds(warmupSeconds),
                    TimeSpan.FromSeconds(sampleSeconds));
                _soakMode = true;
            }
            GD.Print("LUNIL_GODOT_STAGE_READY");
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    public override void _Process(double delta)
    {
        _ = delta;
        if (_host is null)
        {
            return;
        }

        try
        {
            if (_soakMode)
            {
                RunSoakFrame();
                return;
            }
            switch (_step)
            {
                case 0:
                    VerifyFirstProcessTick();
                    break;
                case 1:
                    VerifyPhysicsIsolationAndCompletion();
                    break;
                case 2:
                    VerifySignalAndPause();
                    break;
                case 3:
                    VerifyResumeAfterPause();
                    break;
                case 4:
                    VerifyPatchPublication();
                    break;
                case 5:
                    VerifySignalDisconnectAndQueueFree();
                    break;
                case 6:
                    VerifyQueueFreeAndReload();
                    break;
                case 7:
                    VerifyReloadQueueFree();
                    break;
            }
        }
        catch (Exception exception)
        {
            Fail(exception);
        }
    }

    private void RunSoakFrame()
    {
        var result = _soakSession!.Tick();
        if (result is null) return;
        GD.Print(result.ToMarker());
        _host!.Shutdown();
        Require(LuaGodotRuntimeRegistry.ActiveHostCount == _initialRegistryCount,
            "The Godot soak host remained registered after shutdown.");
        GetTree().Quit(0);
        _host = null;
        _soakMode = false;
    }

    private void VerifyFirstProcessTick()
    {
        var result = _host!.TickUpdate();
        Require(result is { Succeeded: true }, "The first Godot process tick failed.");
        Require(GlobalInteger("counter") == 1, "The entry coroutine did not yield after one process tick.");
        Require(_host.EntryOperation?.Status == LuaGameLoopOperationStatus.Suspended,
            "The Godot entry operation did not suspend.");
        _step++;
    }

    private void VerifyPhysicsIsolationAndCompletion()
    {
        var physics = _host!.TickPhysics();
        Require(physics is { Succeeded: true }, "The Godot physics tick failed.");
        Require(GlobalInteger("counter") == 1,
            "A physics tick advanced an Update-phase operation.");
        var update = _host.TickUpdate();
        Require(update is { Succeeded: true }, "The second Godot process tick failed.");
        Require(GlobalInteger("counter") == 2, "The entry coroutine did not resume.");
        Require(_host.EntryOperation?.Status == LuaGameLoopOperationStatus.Completed,
            "The Godot entry operation did not complete.");
        _step++;
    }

    private void VerifySignalAndPause()
    {
        _emitter!.EmitSignal(FixtureSignalEmitter.SignalName.Fired);
        _emitter.EmitSignal(FixtureSignalEmitter.SignalName.Values, 41L, "godot", true);
        _host!.TickUpdate();
        Require(GlobalInteger("signalCount") == 1, "The Godot signal did not invoke Lua.");
        Require(GlobalInteger("typedTotal") == 42 && GlobalString("typedText") == "godot",
            "The typed Godot signal did not convert its arguments for Lua.");
        Require(_signalSubscription!.IsConnected, "The Godot signal subscription was lost.");
        Require(_typedSignalSubscription!.IsConnected,
            "The typed Godot signal subscription was lost.");

        GetTree().Paused = true;
        _emitter.EmitSignal(FixtureSignalEmitter.SignalName.Fired);
        _emitter.EmitSignal(FixtureSignalEmitter.SignalName.Values, 41L, "paused", true);
        Require(_host.TickUpdate() is null, "A paused Godot tree advanced the Lunil host.");
        Require(GlobalInteger("signalCount") == 1, "A paused tree dispatched a queued signal.");
        Require(GlobalInteger("typedTotal") == 42,
            "A paused tree dispatched a queued typed signal.");
        GetTree().Paused = false;
        _step++;
    }

    private void VerifyResumeAfterPause()
    {
        _host!.TickUpdate();
        Require(GlobalInteger("signalCount") == 2,
            "The queued Godot signal did not resume after tree unpause.");
        Require(GlobalInteger("typedTotal") == 84 && GlobalString("typedText") == "paused",
            "The queued typed Godot signal did not resume after tree unpause.");
        _step++;
    }

    private void VerifyPatchPublication()
    {
        var initial = _host!.GameLoop.Host.RunUtf8("return require('patchable').value");
        Require(initial.Succeeded && initial.Execution!.Values[0].AsInteger() == 1,
            "The initial Godot module revision is invalid.");

        var bundle = CreatePatchBundle();
        var prepared = _host.GameLoop.Host.PreparePatch(bundle);
        Require(prepared.Succeeded, "Godot patch preparation failed: " + prepared.Message);
        _host.GameLoop.PublishAtFrameBoundary(host =>
        {
            var opened = host.TryOpenPatchUpdateWindow();
            if (!opened.Succeeded)
            {
                throw new InvalidOperationException("Godot patch window failed: " + opened.Message);
            }

            using (opened.Window)
            {
                var committed = host.CommitPatch(prepared.PreparedPatch!, opened.Window!);
                if (!committed.Succeeded)
                {
                    throw new InvalidOperationException("Godot patch commit failed: " + committed.Message);
                }
            }
        });
        var tick = _host.TickUpdate();
        Require(tick is { Succeeded: true }, "Godot frame-boundary patch publication failed.");
        var updated = _host.GameLoop.Host.RunUtf8("return require('patchable').value");
        Require(updated.Succeeded && updated.Execution!.Values[0].AsInteger() == 2,
            "The Godot patch was not visible after its frame boundary.");
        _step++;
    }

    private void VerifySignalDisconnectAndQueueFree()
    {
        _signalSubscription!.Dispose();
        Require(!_signalSubscription.IsConnected, "The Godot signal remained connected after dispose.");
        _emitter!.EmitSignal(FixtureSignalEmitter.SignalName.Fired);
        _host!.TickUpdate();
        Require(GlobalInteger("signalCount") == 2,
            "A disconnected Godot signal invoked Lua.");
        _host.QueueFree();
        _waitFrames = 0;
        _step++;
    }

    private void VerifyQueueFreeAndReload()
    {
        _waitFrames++;
        if (_waitFrames < 2)
        {
            return;
        }

        Require(LuaGodotRuntimeRegistry.ActiveHostCount == _initialRegistryCount,
            "QueueFree did not dispose the Godot Lunil host.");
        Require(!_typedSignalSubscription!.IsConnected,
            "Tree exit did not disconnect the typed Godot signal.");
        _host = null;
        CreateHost();
        Require(LuaGodotRuntimeRegistry.ActiveHostCount == _initialRegistryCount + 1,
            "Scene-style host reload did not register a fresh Godot host.");
        _host!.TickUpdate();
        var gameplay = SharedGameplayFixture.Run(
            _host.GameLoop,
            fixedTick => fixedTick ? _host.TickPhysics() : _host.TickUpdate(),
            "godot");
        GD.Print(gameplay.ToMarker());
        _host.QueueFree();
        _waitFrames = 0;
        _step++;
    }

    private void VerifyReloadQueueFree()
    {
        _waitFrames++;
        if (_waitFrames < 2)
        {
            return;
        }

        Require(LuaGodotRuntimeRegistry.ActiveHostCount == _initialRegistryCount,
            "The reloaded Godot host survived QueueFree.");
        Require(!_typedSignalSubscription!.IsConnected,
            "The reloaded host left its typed Godot signal connected.");
        GD.Print("LUNIL_GODOT_RESOURCE_TRACE asset=" + _entry!.GetEffectiveFixtureAssetId());
        GD.Print("LUNIL_GODOT_FIXTURE_OK");
        GetTree().Quit(0);
        _host = null;
    }

    private void CreateHost()
    {
        _host = new LunilGameLoop
        {
            Name = "LunilHost",
            EntryScript = _entry,
            StartOnReady = false,
            PauseWithTree = true,
        };
        _host.Modules.Add(_module!);
        _host.Modules.Add(_gameplayRules!);
        AddChild(_host);
        _host.SetProcess(false);
        _host.SetPhysicsProcess(false);
        _host.Initialize();

        _emitter = new FixtureSignalEmitter { Name = "SignalEmitter" };
        _host.AddChild(_emitter);
        var callbackResult = _host.GameLoop.Host.RunUtf8(
            "return function() signalCount=(signalCount or 0)+1 end");
        Require(callbackResult.Succeeded, "The Godot signal callback did not compile.");
        _signalSubscription = LuaGodotSignalSubscription.Connect(
            _host.GameLoop,
            _emitter,
            FixtureSignalEmitter.SignalName.Fired,
            callbackResult.Execution!.Values[0]);

        var typedCallback = _host.GameLoop.Host.RunUtf8(
            "return function(value,text,flag) " +
            "typedTotal=(typedTotal or 0)+value+(flag and 1 or 0);typedText=text end");
        Require(typedCallback.Succeeded, "The typed Godot signal callback did not compile.");
        _typedSignalSubscription = LuaGodotSignalSubscription.Connect<long, string, bool>(
            _host.GameLoop,
            _emitter,
            FixtureSignalEmitter.SignalName.Values,
            typedCallback.Execution!.Values[0],
            static value => LuaValue.FromInteger(value),
            value => LuaValue.FromString(_host.GameLoop.Host.State.Strings.GetOrCreate(
                Encoding.UTF8.GetBytes(value))),
            static value => LuaValue.FromBoolean(value));
    }

    private static void VerifyPersistentStore()
    {
        var store = new LuaGodotPersistentStore("LunilFixture");
        var key = "fixture/" + Guid.NewGuid().ToString("N");
        var expected = Encoding.UTF8.GetBytes("godot-persistent-store");
        store.WriteAsync(key, expected).AsTask().GetAwaiter().GetResult();
        var read = store.ReadAsync(key).AsTask().GetAwaiter().GetResult();
        Require(read.Found && read.Value.Span.SequenceEqual(expected),
            "The Godot persistent store did not round-trip exact bytes.");
        var missing = store.ReadAsync(key + "/missing").AsTask().GetAwaiter().GetResult();
        Require(!missing.Found, "The Godot persistent store returned a missing key.");
    }

    private static (
        LuaGodotScriptResource Entry,
        LuaGodotScriptResource Module,
        LuaGodotScriptResource GameplayRules)
        CreateAndReloadResources()
    {
        var unique = Guid.NewGuid().ToString("N");
        var entryPath = "user://lunil-entry-" + unique + ".tres";
        var modulePath = "user://lunil-module-" + unique + ".tres";
        var gameplayPath = "user://lunil-gameplay-" + unique + ".tres";
        var entry = new LunilScript
        {
            Source = "print('LUNIL_GODOT_CONSOLE_TRACE');" +
                "counter=(counter or 0)+1;coroutine.yield();counter=counter+1;return counter",
            AssetId = "@res://fixture/player.lua",
            ModuleName = "fixture.player",
        };
        var module = new LunilScript
        {
            Source = "return {value=1}",
            AssetId = "@res://fixture/patchable.lua",
            ModuleName = "patchable",
        };
        var gameplay = new LunilScript
        {
            Source = SharedGameplayFixture.InitialRulesSource,
            AssetId = "@" + SharedGameplayFixture.ModulePath,
            ModuleName = SharedGameplayFixture.ModuleName,
        };
        Require(ResourceSaver.Save(entry, entryPath) == Error.Ok,
            "Godot ResourceSaver could not write the entry resource.");
        Require(ResourceSaver.Save(module, modulePath) == Error.Ok,
            "Godot ResourceSaver could not write the module resource.");
        Require(ResourceSaver.Save(gameplay, gameplayPath) == Error.Ok,
            "Godot ResourceSaver could not write the gameplay resource.");
        var resolver = LuaGodotAssetResolver.Load([entryPath, modulePath, gameplayPath]);
        Require(resolver.FileExists("res://fixture/player.lua"),
            "Godot ResourceLoader resolver lost the entry file alias.");
        Require(Encoding.UTF8.GetString(resolver.ReadAllBytes("res://fixture/patchable.lua")) ==
            "return {value=1}", "Godot ResourceLoader resolver changed module bytes.");
        return (
            ResourceLoader.Load<LuaGodotScriptResource>(entryPath)!,
            ResourceLoader.Load<LuaGodotScriptResource>(modulePath)!,
            ResourceLoader.Load<LuaGodotScriptResource>(gameplayPath)!);
    }

    private static LuaPatchBundle CreatePatchBundle()
    {
        var signer = new FixturePatchSigner(
            "godot-fixture",
            Encoding.UTF8.GetBytes("godot-fixture-signing-key"));
        return LuaPatchBundle.Create(
            new LuaPatchManifest
            {
                PatchId = "godot-fixture-patch",
                Channel = "godot-fixture",
                TargetBuild = "godot-fixture-2",
                BaseRevision = "godot-fixture-1",
                TargetRevision = "godot-fixture-2",
                LanguageVersion = Lunil.Core.LuaLanguageVersion.Lua54,
                RuntimeAbi = "lunil-0.12",
                CreatedAt = new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero),
                ExpiresAt = new DateTimeOffset(2099, 7, 27, 0, 0, 0, TimeSpan.Zero),
                Nonce = "godot-fixture-patch-nonce",
            },
            [
                new LuaPatchEntry(
                    "modules/patchable.lua",
                    "patchable",
                    LuaPatchEntryKind.Source,
                    Encoding.UTF8.GetBytes("return {value=2}")),
            ],
            signer);
    }

    private long GlobalInteger(string name) =>
        _host!.GameLoop.Host.State.GetGlobal(name).AsInteger();

    private string GlobalString(string name) =>
        _host!.GameLoop.Host.State.GetGlobal(name).AsString().ToString();

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static double ReadDoubleArgument(string prefix, double defaultValue = 0.0)
    {
        foreach (var argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith(prefix, StringComparison.Ordinal))
                return double.Parse(argument[prefix.Length..],
                    System.Globalization.CultureInfo.InvariantCulture);
        }
        return defaultValue;
    }

    private void Fail(Exception exception)
    {
        GD.PushError(exception.ToString());
        GD.Print("LUNIL_GODOT_FIXTURE_FAILED " + exception.GetType().Name + ": " + exception.Message);
        GetTree().Quit(1);
        _host = null;
    }

    private sealed class FixturePatchSigner : ILuaPatchSigner, ILuaPatchSignatureVerifier
    {
        private readonly byte[] _key;

        public FixturePatchSigner(string keyId, byte[] key)
        {
            KeyId = keyId;
            _key = key;
        }

        public string Algorithm => "HMAC-SHA256-GODOT-FIXTURE";
        public string KeyId { get; }

        public byte[] SignDigest(ReadOnlySpan<byte> digest)
        {
            using var hmac = new HMACSHA256(_key);
            return hmac.ComputeHash(digest.ToArray());
        }

        public bool IsTrusted(string algorithm, string keyId) =>
            string.Equals(algorithm, Algorithm, StringComparison.Ordinal) &&
            string.Equals(keyId, KeyId, StringComparison.Ordinal);

        public bool VerifyDigest(
            string algorithm,
            string keyId,
            ReadOnlySpan<byte> digest,
            ReadOnlySpan<byte> signature) =>
            IsTrusted(algorithm, keyId) &&
            CryptographicOperations.FixedTimeEquals(SignDigest(digest), signature);
    }
}

public partial class FixtureSignalEmitter : Node
{
    [Signal]
    public delegate void FiredEventHandler();

    [Signal]
    public delegate void ValuesEventHandler(long value, string text, bool flag);
}

internal static class FixtureResourceExtensions
{
    public static string GetEffectiveFixtureAssetId(this LuaGodotScriptResource resource) =>
        string.IsNullOrWhiteSpace(resource.AssetId) ? "@" + resource.ResourcePath : resource.AssetId;
}
