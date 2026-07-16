using System.Runtime.CompilerServices;
using Lunil.CodeGen.Cil.Jit;
using Lunil.Compiler;
using Lunil.IR.Canonical;
using Lunil.IR.Lua54;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.StandardLibrary;
using Lunil.Workspace;

namespace Lunil.Hosting;

/// <summary>
/// Reusable embedding boundary that composes compilation, runtime ownership, capabilities, and
/// reference execution without requiring consumers to assemble individual compiler layers.
/// </summary>
public sealed partial class LuaHost : IDisposable
{
    private readonly LuaExecutor _interpreterExecutor;
    private readonly object _jitLock = new();
    private readonly object _executionGate = new();
    private LuaJitExecutor? _jitExecutor;
    private int _disposed;

    public LuaHost(LuaHostOptions? options = null)
    {
        Options = options ?? LuaHostOptions.Default;
        ArgumentNullException.ThrowIfNull(Options.Compiler);
        ArgumentNullException.ThrowIfNull(Options.State);
        ArgumentNullException.ThrowIfNull(Options.Execution);
        ArgumentNullException.ThrowIfNull(Options.Jit);
        ArgumentNullException.ThrowIfNull(Options.Workspace);
        if (!Enum.IsDefined(Options.Profile))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The host profile is invalid.");
        }

        if (!Enum.IsDefined(Options.ExecutionBackend))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The host execution backend is invalid.");
        }

        if (!Enum.IsDefined(Options.Jit.Policy))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The JIT policy is invalid.");
        }

        if (Options.ExecutionBackend == LuaHostExecutionBackend.Jit &&
            Options.Jit.Policy == LuaJitPolicy.InterpreterOnly)
        {
            throw new ArgumentException(
                "The JIT backend cannot use the interpreter-only JIT policy.",
                nameof(options));
        }

        IsDynamicCodeAvailable = RuntimeFeature.IsDynamicCodeSupported &&
            RuntimeFeature.IsDynamicCodeCompiled;
        SelectedExecutionBackend = ResolveExecutionBackend(
            Options.ExecutionBackend,
            IsDynamicCodeAvailable,
            Options.Jit.Policy);

        Compiler = new LuaCompiler(Options.Compiler);
        Workspace = new LuaWorkspace(
            Options.Workspace with { Compiler = Options.Compiler },
            Options.ModuleResolver);
        StateOptions = ResolveStateOptions(Options);
        State = new LuaState(StateOptions);
        _interpreterExecutor = new LuaExecutor(new LuaExecutorOptions
        {
            Interpreter = Options.Execution,
        });

        if (Options.InstallStandardLibrary)
        {
            StandardLibraryOptions = Options.StandardLibrary ??
                LuaHostCapabilityProfiles.Create(Options.Profile);
            LuaStandardLibrary.InstallAll(State, StandardLibraryOptions);
        }
    }

    public LuaHostOptions Options { get; }

    /// <summary>Gets whether the current runtime can compile and execute dynamic code.</summary>
    public bool IsDynamicCodeAvailable { get; }

    /// <summary>Gets the concrete execution backend selected for this host.</summary>
    public LuaHostExecutionBackend SelectedExecutionBackend { get; }

    /// <summary>
    /// Gets JIT statistics after the JIT executor has been initialized, or null when the host uses
    /// the interpreter or has not executed any code yet.
    /// </summary>
    public LuaJitStatistics? JitStatistics => _jitExecutor?.Statistics;

    public LuaCompiler Compiler { get; }

    public LuaWorkspace Workspace { get; }

    public LuaStateOptions StateOptions { get; }

    public LuaState State { get; }

    public LuaStandardLibraryOptions? StandardLibraryOptions { get; }

    public LuaBufferedConsole? BufferedConsole =>
        StandardLibraryOptions?.Console as LuaBufferedConsole;

    public LuaCompilationResult Compile(
        LuaSourceDocument source,
        CancellationToken cancellationToken = default) =>
        Compiler.Compile(source, cancellationToken);

    public LuaCompilationResult CompileUtf8(
        string source,
        string? sourceName = null,
        CancellationToken cancellationToken = default) =>
        Compiler.CompileUtf8(source, sourceName, cancellationToken);

    public LuaHostRunResult Run(
        LuaSourceDocument source,
        ReadOnlySpan<LuaValue> arguments = default,
        CancellationToken cancellationToken = default)
    {
        var compilation = Compile(source, cancellationToken);
        return compilation.Succeeded
            ? new LuaHostRunResult(compilation, Execute(compilation, arguments))
            : new LuaHostRunResult(compilation, null);
    }

    public LuaHostRunResult RunUtf8(
        string source,
        string? sourceName = null,
        ReadOnlySpan<LuaValue> arguments = default,
        CancellationToken cancellationToken = default) =>
        Run(
            LuaSourceDocument.FromUtf8(source, sourceName),
            arguments,
            cancellationToken);

    public LuaExecutionResult Execute(
        LuaCompilationResult compilation,
        ReadOnlySpan<LuaValue> arguments = default)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        if (!compilation.Succeeded || compilation.Module is null)
        {
            throw new ArgumentException(
                "The compilation must succeed before it can be executed.",
                nameof(compilation));
        }

        return Execute(compilation.Module, arguments);
    }

    public LuaExecutionResult Execute(
        LuaIrModule module,
        ReadOnlySpan<LuaValue> arguments = default)
    {
        ArgumentNullException.ThrowIfNull(module);
        lock (_executionGate)
        {
            ThrowIfDisposed();
            var closure = State.CreateMainClosure(module);
            return ExecuteClosure(closure, arguments);
        }
    }

    public LuaExecutionResult ExecuteBinaryChunk(
        ReadOnlySpan<byte> binaryChunk,
        ReadOnlySpan<LuaValue> arguments = default,
        Lua54ChunkReaderOptions? readerOptions = null)
    {
        lock (_executionGate)
        {
            ThrowIfDisposed();
            return SelectedExecutionBackend == LuaHostExecutionBackend.Jit
                ? GetJitExecutor().ExecuteBinaryChunk(State, binaryChunk, arguments, readerOptions)
                : _interpreterExecutor.ExecuteBinaryChunk(
                    State,
                    binaryChunk,
                    arguments,
                    readerOptions);
        }
    }

    public Task<LuaWorkspaceResult> AnalyzeWorkspaceAsync(
        IEnumerable<LuaWorkspaceDocument> roots,
        CancellationToken cancellationToken = default) =>
        Workspace.AnalyzeAsync(roots, cancellationToken);

    public void Dispose()
    {
        lock (_executionGate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            LuaJitExecutor? jit;
            lock (_jitLock)
            {
                jit = _jitExecutor;
                _jitExecutor = null;
            }

            jit?.Dispose();
            Workspace.Dispose();
        }
    }

    private LuaExecutionResult ExecuteClosure(
        LuaClosure closure,
        ReadOnlySpan<LuaValue> arguments) =>
        SelectedExecutionBackend == LuaHostExecutionBackend.Jit
            ? GetJitExecutor().Execute(State, closure, arguments)
            : _interpreterExecutor.Execute(State, closure, arguments);

    private LuaJitExecutor GetJitExecutor()
    {
        lock (_jitLock)
        {
            ThrowIfDisposed();
            return _jitExecutor ??= new LuaJitExecutor(Options.Jit with
            {
                Interpreter = Options.Execution,
            });
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private static LuaHostExecutionBackend ResolveExecutionBackend(
        LuaHostExecutionBackend requested,
        bool isDynamicCodeAvailable,
        LuaJitPolicy jitPolicy) => requested switch
        {
            LuaHostExecutionBackend.Auto when jitPolicy == LuaJitPolicy.InterpreterOnly =>
                LuaHostExecutionBackend.Interpreter,
            LuaHostExecutionBackend.Auto => isDynamicCodeAvailable
                ? LuaHostExecutionBackend.Jit
                : LuaHostExecutionBackend.Interpreter,
            LuaHostExecutionBackend.Interpreter => LuaHostExecutionBackend.Interpreter,
            LuaHostExecutionBackend.Jit when isDynamicCodeAvailable => LuaHostExecutionBackend.Jit,
            LuaHostExecutionBackend.Jit => throw new PlatformNotSupportedException(
                "The JIT execution backend requires dynamic-code support."),
            _ => throw new ArgumentOutOfRangeException(nameof(requested)),
        };

    private static LuaStateOptions ResolveStateOptions(LuaHostOptions options)
    {
        if (options.Profile != LuaHostProfile.Deterministic ||
            options.State.Heap.HashSeed is not null)
        {
            return options.State;
        }

        return options.State with
        {
            Heap = options.State.Heap with { HashSeed = 0 },
        };
    }
}
