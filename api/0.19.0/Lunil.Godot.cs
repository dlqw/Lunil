// Target Frameworks: net8.0
#nullable enable

namespace Lunil.Godot
{
    public sealed class LuaGodotAssetResolver : Lunil.Hosting.ILuaGameLoopAssetResolver, Lunil.StandardLibrary.ILuaFileSystem, Lunil.Workspace.ILuaModuleResolver
    {
        public LuaGodotAssetResolver(System.Collections.Generic.IEnumerable<Lunil.Godot.LuaGodotScriptResource> resources) { }
        public static Lunil.Godot.LuaGodotAssetResolver Load(System.Collections.Generic.IEnumerable<string> resourcePaths) => throw null;
        public System.Threading.Tasks.ValueTask<Lunil.Hosting.LuaGameLoopReadResult> ResolveAsync(string assetId, System.Threading.CancellationToken cancellationToken = null) => throw null;
        public System.Threading.Tasks.ValueTask<Lunil.Workspace.LuaWorkspaceDocument?> ResolveAsync(Lunil.Workspace.LuaModuleResolutionRequest request, System.Threading.CancellationToken cancellationToken = null) => throw null;
        public byte[] ReadAllBytes(string path) => throw null;
        public bool FileExists(string path) => throw null;
    }

    public sealed class LuaGodotConsole : Lunil.StandardLibrary.ILuaConsole
    {
        public byte[] ReadStandardInput() => throw null;
        public void Write(System.ReadOnlyMemory<byte> bytes) { }
        public void WriteError(System.ReadOnlyMemory<byte> bytes) { }
        public void WriteLine() { }
    }

    public sealed class LuaGodotDispatcher : Lunil.Hosting.ILuaGameLoopDispatcher, System.IDisposable
    {
        public bool CheckAccess() => throw null;
        public void Post(System.Action callback) { }
        public int Drain(int maximumCallbacks) => throw null;
        public void Dispose() { }
    }

    public class LuaGodotGameLoop : global::Godot.Node
    {
        public Lunil.Godot.LuaGodotScriptResource? EntryScript { get => throw null; set { } }
        public global::Godot.Collections.Array<Lunil.Godot.LuaGodotScriptResource> Modules { get => throw null; set { } }
        public bool StartOnReady { get => throw null; set { } }
        public bool PauseWithTree { get => throw null; set { } }
        public int MaximumDispatchedCallbacks { get => throw null; set { } }
        public bool IsInitialized { get => throw null; }
        public Lunil.Hosting.LuaGameLoopHost GameLoop { get => throw null; }
        public Lunil.Hosting.LuaGameLoopOperation? EntryOperation { get => throw null; }
        public System.Func<Lunil.Hosting.LuaGameLoopHostOptions, Lunil.Hosting.LuaGameLoopHostOptions>? ConfigureHostOptions { get => throw null; set { } }
        public event System.Action<Lunil.Hosting.LuaGameLoopTickResult>? TickCompleted;
        public event System.Action<System.Exception>? HostFailed;
        public override void _Ready() { }
        public override void _Process(double delta) { }
        public override void _PhysicsProcess(double delta) { }
        public override void _ExitTree() { }
        public override void _Notification(int what) { }
        public void Initialize() { }
        public Lunil.Hosting.LuaGameLoopTickResult? TickUpdate() => throw null;
        public Lunil.Hosting.LuaGameLoopTickResult? TickPhysics() => throw null;
        public void Shutdown() { }
        protected override bool InvokeGodotClassMethod(in global::Godot.NativeInterop.godot_string_name method, global::Godot.NativeInterop.NativeVariantPtrArgs args, out global::Godot.NativeInterop.godot_variant ret) => throw null;
        protected override bool HasGodotClassMethod(in global::Godot.NativeInterop.godot_string_name method) => throw null;
        protected override bool SetGodotClassPropertyValue(in global::Godot.NativeInterop.godot_string_name name, in global::Godot.NativeInterop.godot_variant value) => throw null;
        protected override bool GetGodotClassPropertyValue(in global::Godot.NativeInterop.godot_string_name name, out global::Godot.NativeInterop.godot_variant value) => throw null;
        protected override void SaveGodotObjectData(global::Godot.Bridge.GodotSerializationInfo? info) { }
        protected override void RestoreGodotObjectData(global::Godot.Bridge.GodotSerializationInfo? info) { }
        public class MethodName : MethodName
        {
            public static readonly global::Godot.StringName _Ready;
            public static readonly global::Godot.StringName _Process;
            public static readonly global::Godot.StringName _PhysicsProcess;
            public static readonly global::Godot.StringName _ExitTree;
            public static readonly global::Godot.StringName _Notification;
            public static readonly global::Godot.StringName Initialize;
            public static readonly global::Godot.StringName Shutdown;
            public static readonly global::Godot.StringName IsPausedByTree;
        }
        public class PropertyName : PropertyName
        {
            public static readonly global::Godot.StringName EntryScript;
            public static readonly global::Godot.StringName Modules;
            public static readonly global::Godot.StringName StartOnReady;
            public static readonly global::Godot.StringName PauseWithTree;
            public static readonly global::Godot.StringName MaximumDispatchedCallbacks;
            public static readonly global::Godot.StringName IsInitialized;
            public static readonly global::Godot.StringName _treePaused;
        }
        public class SignalName : SignalName
        {
        }
    }

    public sealed class LuaGodotPersistentStore : Lunil.Hosting.ILuaGameLoopPersistentStore
    {
        public LuaGodotPersistentStore(string subdirectory = "Lunil") { }
        public System.Threading.Tasks.ValueTask<Lunil.Hosting.LuaGameLoopReadResult> ReadAsync(string key, System.Threading.CancellationToken cancellationToken = null) => throw null;
        public System.Threading.Tasks.ValueTask WriteAsync(string key, System.ReadOnlyMemory<byte> value, System.Threading.CancellationToken cancellationToken = null) => throw null;
    }

    public static class LuaGodotRuntimeRegistry
    {
        public static int ActiveHostCount { get => throw null; }
        public static void DisposeAll() { }
    }

    public class LuaGodotScriptResource : global::Godot.Resource
    {
        public string Source { get => throw null; set { } }
        public string AssetId { get => throw null; set { } }
        public string ModuleName { get => throw null; set { } }
        public System.ReadOnlyMemory<byte> GetBytes() => throw null;
        protected override bool InvokeGodotClassMethod(in global::Godot.NativeInterop.godot_string_name method, global::Godot.NativeInterop.NativeVariantPtrArgs args, out global::Godot.NativeInterop.godot_variant ret) => throw null;
        protected override bool HasGodotClassMethod(in global::Godot.NativeInterop.godot_string_name method) => throw null;
        protected override bool SetGodotClassPropertyValue(in global::Godot.NativeInterop.godot_string_name name, in global::Godot.NativeInterop.godot_variant value) => throw null;
        protected override bool GetGodotClassPropertyValue(in global::Godot.NativeInterop.godot_string_name name, out global::Godot.NativeInterop.godot_variant value) => throw null;
        protected override void SaveGodotObjectData(global::Godot.Bridge.GodotSerializationInfo info) { }
        protected override void RestoreGodotObjectData(global::Godot.Bridge.GodotSerializationInfo info) { }
        public class MethodName : MethodName
        {
            public static readonly global::Godot.StringName GetEffectiveAssetId;
        }
        public class PropertyName : PropertyName
        {
            public static readonly global::Godot.StringName Source;
            public static readonly global::Godot.StringName AssetId;
            public static readonly global::Godot.StringName ModuleName;
            public static readonly global::Godot.StringName _source;
        }
        public class SignalName : SignalName
        {
        }
    }

    public sealed class LuaGodotSignalSubscription : System.IDisposable
    {
        public bool IsConnected { get => throw null; }
        public static Lunil.Godot.LuaGodotSignalSubscription Connect(Lunil.Hosting.LuaGameLoopHost host, global::Godot.GodotObject source, global::Godot.StringName signal, Lunil.Runtime.Values.LuaValue callback, Lunil.Hosting.LuaGameLoopStartOptions? options = null) => throw null;
        public static Lunil.Godot.LuaGodotSignalSubscription Connect<T1>(Lunil.Hosting.LuaGameLoopHost host, global::Godot.GodotObject source, global::Godot.StringName signal, Lunil.Runtime.Values.LuaValue callback, System.Func<T1, Lunil.Runtime.Values.LuaValue> convert1, Lunil.Hosting.LuaGameLoopStartOptions? options = null) => throw null;
        public static Lunil.Godot.LuaGodotSignalSubscription Connect<T1, T2>(Lunil.Hosting.LuaGameLoopHost host, global::Godot.GodotObject source, global::Godot.StringName signal, Lunil.Runtime.Values.LuaValue callback, System.Func<T1, Lunil.Runtime.Values.LuaValue> convert1, System.Func<T2, Lunil.Runtime.Values.LuaValue> convert2, Lunil.Hosting.LuaGameLoopStartOptions? options = null) => throw null;
        public static Lunil.Godot.LuaGodotSignalSubscription Connect<T1, T2, T3>(Lunil.Hosting.LuaGameLoopHost host, global::Godot.GodotObject source, global::Godot.StringName signal, Lunil.Runtime.Values.LuaValue callback, System.Func<T1, Lunil.Runtime.Values.LuaValue> convert1, System.Func<T2, Lunil.Runtime.Values.LuaValue> convert2, System.Func<T3, Lunil.Runtime.Values.LuaValue> convert3, Lunil.Hosting.LuaGameLoopStartOptions? options = null) => throw null;
        public void Dispose() { }
    }

    public sealed class LuaGodotTimeProvider : System.TimeProvider
    {
        public long TimestampFrequency { get => throw null; }
        public override long GetTimestamp() => throw null;
        public override System.DateTimeOffset GetUtcNow() => throw null;
    }
}
