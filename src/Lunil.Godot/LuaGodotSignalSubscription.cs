using Godot;
using Lunil.Hosting;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Values;

namespace Lunil.Godot;

/// <summary>Roots a Lua callback while a Godot signal is connected and disconnects exactly once.</summary>
public sealed class LuaGodotSignalSubscription : IDisposable
{
    private readonly LuaGameLoopHost _host;
    private readonly GodotObject _source;
    private readonly StringName _signal;
    private readonly Callable _callable;
    private readonly LuaHandle _callback;
    private readonly LuaGameLoopStartOptions _options;
    private readonly Callable _lifecycleCallable;
    private readonly bool _hasLifecycleConnection;
    private int _disposed;

    private LuaGodotSignalSubscription(
        LuaGameLoopHost host,
        GodotObject source,
        StringName signal,
        LuaValue callback,
        LuaGameLoopStartOptions? options,
        Func<LuaGodotSignalSubscription, Callable> callableFactory)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(callableFactory);
        if (signal.IsEmpty)
        {
            throw new ArgumentException("A Godot signal name is required.", nameof(signal));
        }

        _host = host;
        _source = source;
        _signal = signal;
        _options = options ?? LuaGameLoopStartOptions.Default;
        _callback = host.Host.State.Heap.CreateHandle(callback);
        _callable = callableFactory(this);
        try
        {
            var error = source.Connect(signal, _callable);
            if (error != Error.Ok)
            {
                throw new InvalidOperationException(
                    $"Godot signal '{signal}' could not be connected: {error}.");
            }

            if (source is Node node && signal != Node.SignalName.TreeExiting)
            {
                _lifecycleCallable = Callable.From(Dispose);
                error = node.Connect(Node.SignalName.TreeExiting, _lifecycleCallable);
                if (error != Error.Ok)
                {
                    source.Disconnect(signal, _callable);
                    throw new InvalidOperationException(
                        $"Godot signal lifecycle cleanup could not be connected: {error}.");
                }

                _hasLifecycleConnection = true;
            }
        }
        catch
        {
            _callback.Dispose();
            throw;
        }
    }

    public static LuaGodotSignalSubscription Connect(
        LuaGameLoopHost host,
        GodotObject source,
        StringName signal,
        LuaValue callback,
        LuaGameLoopStartOptions? options = null) =>
        new(host, source, signal, callback, options,
            static subscription => Callable.From(subscription.ScheduleWithoutArguments));

    public static LuaGodotSignalSubscription Connect<[MustBeVariant] T1>(
        LuaGameLoopHost host,
        GodotObject source,
        StringName signal,
        LuaValue callback,
        Func<T1, LuaValue> convert1,
        LuaGameLoopStartOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(convert1);
        return new LuaGodotSignalSubscription(
            host,
            source,
            signal,
            callback,
            options,
            subscription => Callable.From<T1>(
                value1 => subscription.Schedule([convert1(value1)])));
    }

    public static LuaGodotSignalSubscription Connect<
        [MustBeVariant] T1,
        [MustBeVariant] T2>(
        LuaGameLoopHost host,
        GodotObject source,
        StringName signal,
        LuaValue callback,
        Func<T1, LuaValue> convert1,
        Func<T2, LuaValue> convert2,
        LuaGameLoopStartOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(convert1);
        ArgumentNullException.ThrowIfNull(convert2);
        return new LuaGodotSignalSubscription(
            host,
            source,
            signal,
            callback,
            options,
            subscription => Callable.From<T1, T2>((value1, value2) =>
                subscription.Schedule([convert1(value1), convert2(value2)])));
    }

    public static LuaGodotSignalSubscription Connect<
        [MustBeVariant] T1,
        [MustBeVariant] T2,
        [MustBeVariant] T3>(
        LuaGameLoopHost host,
        GodotObject source,
        StringName signal,
        LuaValue callback,
        Func<T1, LuaValue> convert1,
        Func<T2, LuaValue> convert2,
        Func<T3, LuaValue> convert3,
        LuaGameLoopStartOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(convert1);
        ArgumentNullException.ThrowIfNull(convert2);
        ArgumentNullException.ThrowIfNull(convert3);
        return new LuaGodotSignalSubscription(
            host,
            source,
            signal,
            callback,
            options,
            subscription => Callable.From<T1, T2, T3>((value1, value2, value3) =>
                subscription.Schedule(
                    [convert1(value1), convert2(value2), convert3(value3)])));
    }

    public bool IsConnected => Volatile.Read(ref _disposed) == 0 &&
        GodotObject.IsInstanceValid(_source) && _source.IsConnected(_signal, _callable);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (GodotObject.IsInstanceValid(_source) && _source.IsConnected(_signal, _callable))
            {
                _source.Disconnect(_signal, _callable);
            }

            if (_hasLifecycleConnection && _source is Node node &&
                GodotObject.IsInstanceValid(node) &&
                node.IsConnected(Node.SignalName.TreeExiting, _lifecycleCallable))
            {
                node.Disconnect(Node.SignalName.TreeExiting, _lifecycleCallable);
            }
        }
        finally
        {
            _callback.Dispose();
        }
    }

    private void ScheduleWithoutArguments() => Schedule([]);

    private void Schedule(LuaValue[] arguments)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        void Schedule()
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                if (arguments.Length != 0)
                {
                    _host.StartCallback(_callback.Value, arguments, _options);
                }
                else
                {
                    _host.StartCallback(_callback.Value, options: _options);
                }
            }
        }

        if (_host.Options.Dispatcher?.CheckAccess() is false)
        {
            _host.Options.Dispatcher.Post(Schedule);
        }
        else
        {
            Schedule();
        }
    }
}
