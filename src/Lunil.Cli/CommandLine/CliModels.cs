using System.Collections.Immutable;
using Lunil.Core;

namespace Lunil.Cli.CommandLine;

internal enum CliCommand
{
    None,
    Run,
    Check,
    Build,
    Dump,
}

internal enum CliDiagnosticFormat
{
    Text,
    Json,
}

internal enum CliProfile
{
    Trusted,
    Sandbox,
    Deterministic,
}

internal enum CliExecutionBackend
{
    Auto,
    Interpreter,
    Jit,
}

internal enum CliBuildTarget
{
    Chunk,
    Aot,
}

internal enum CliDumpKind
{
    Summary,
    Syntax,
    Annotations,
    Analysis,
    Ir,
    Chunk,
}

internal enum CliDumpFormat
{
    Text,
    Json,
}

internal sealed record CliOptions
{
    public CliCommand Command { get; init; }

    public ImmutableArray<string> Inputs { get; init; } = [];

    public ImmutableArray<string> ScriptArguments { get; init; } = [];

    public string? OutputPath { get; init; }

    public string? ModuleName { get; init; }

    public ImmutableArray<string> ModuleRoots { get; init; } = [];

    public ImmutableArray<string> PathPatterns { get; init; } = ["?.lua", "?/init.lua"];

    public CliDiagnosticFormat DiagnosticFormat { get; init; } = CliDiagnosticFormat.Text;

    public CliProfile Profile { get; init; } = CliProfile.Trusted;

    public LuaLanguageVersion LanguageVersion { get; init; } = LuaLanguageVersions.Default;

    public CliExecutionBackend ExecutionBackend { get; init; } = CliExecutionBackend.Auto;

    public CliBuildTarget BuildTarget { get; init; } = CliBuildTarget.Chunk;

    public CliDumpKind DumpKind { get; init; } = CliDumpKind.Summary;

    public CliDumpFormat DumpFormat { get; init; } = CliDumpFormat.Text;

    public bool WarningsAsErrors { get; init; }

    public bool StripDebug { get; init; }

    public bool ShowHelp { get; init; }

    public bool ShowVersion { get; init; }

    public long MaximumInputBytes { get; init; } = 64L * 1024 * 1024;

    public long MaximumInstructionCount { get; init; } = 100_000_000;

    public int MaximumStackSlots { get; init; } = 1_000_000;

    public int MaximumCallDepth { get; init; } = 20_000;

    public long MaximumHeapBytes { get; init; } = 256L * 1024 * 1024;
}

internal sealed record CliConfiguration
{
    public CliDiagnosticFormat? DiagnosticFormat { get; init; }

    public CliProfile? Profile { get; init; }

    public LuaLanguageVersion? LanguageVersion { get; init; }

    public CliExecutionBackend? ExecutionBackend { get; init; }

    public CliBuildTarget? BuildTarget { get; init; }

    public CliDumpKind? DumpKind { get; init; }

    public CliDumpFormat? DumpFormat { get; init; }

    public ImmutableArray<string> ModuleRoots { get; init; } = [];

    public ImmutableArray<string> PathPatterns { get; init; } = [];

    public bool? WarningsAsErrors { get; init; }

    public bool? StripDebug { get; init; }

    public long? MaximumInputBytes { get; init; }

    public long? MaximumInstructionCount { get; init; }

    public int? MaximumStackSlots { get; init; }

    public int? MaximumCallDepth { get; init; }

    public long? MaximumHeapBytes { get; init; }
}

internal sealed class CliUsageException(string message) : Exception(message);

internal sealed class CliRemovedFeatureException(string message) : Exception(message);

internal sealed class CliInputException(string message, Exception? innerException = null) :
    Exception(message, innerException);

internal sealed class CliBuildException(string message, Exception? innerException = null) :
    Exception(message, innerException);
