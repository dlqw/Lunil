using Lunil.Compiler;
using Lunil.IR.Canonical;
using Lunil.IR.Lua54;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.StandardLibrary;

namespace Lunil.Hosting;

/// <summary>
/// Reusable embedding boundary that composes compilation, runtime ownership, capabilities, and
/// reference execution without requiring consumers to assemble individual compiler layers.
/// </summary>
public sealed class LuaHost
{
    private readonly LuaExecutor _executor;

    public LuaHost(LuaHostOptions? options = null)
    {
        Options = options ?? LuaHostOptions.Default;
        ArgumentNullException.ThrowIfNull(Options.Compiler);
        ArgumentNullException.ThrowIfNull(Options.State);
        ArgumentNullException.ThrowIfNull(Options.Execution);
        if (!Enum.IsDefined(Options.Profile))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The host profile is invalid.");
        }

        Compiler = new LuaCompiler(Options.Compiler);
        StateOptions = ResolveStateOptions(Options);
        State = new LuaState(StateOptions);
        _executor = new LuaExecutor(new LuaExecutorOptions { Interpreter = Options.Execution });

        if (Options.InstallStandardLibrary)
        {
            StandardLibraryOptions = Options.StandardLibrary ??
                LuaHostCapabilityProfiles.Create(Options.Profile);
            LuaStandardLibrary.InstallAll(State, StandardLibraryOptions);
        }
    }

    public LuaHostOptions Options { get; }

    public LuaCompiler Compiler { get; }

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
        return _executor.Execute(State, State.CreateMainClosure(module), arguments);
    }

    public LuaExecutionResult ExecuteBinaryChunk(
        ReadOnlySpan<byte> binaryChunk,
        ReadOnlySpan<LuaValue> arguments = default,
        Lua54ChunkReaderOptions? readerOptions = null) =>
        _executor.ExecuteBinaryChunk(State, binaryChunk, arguments, readerOptions);

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
