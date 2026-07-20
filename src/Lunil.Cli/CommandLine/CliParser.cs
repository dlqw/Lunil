using System.Collections.Immutable;
using Lunil.Core;

namespace Lunil.Cli.CommandLine;

internal static class CliParser
{
    public static CliOptions Parse(IReadOnlyList<string> arguments, CliConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(configuration);

        var inputs = ImmutableArray.CreateBuilder<string>();
        var scriptArguments = ImmutableArray.CreateBuilder<string>();
        var moduleRoots = configuration.ModuleRoots.ToBuilder();
        var pathPatterns = configuration.PathPatterns.IsDefaultOrEmpty
            ? ImmutableArray.CreateBuilder<string>()
            : configuration.PathPatterns.ToBuilder();
        var command = CliCommand.None;
        var diagnosticFormat = configuration.DiagnosticFormat ?? CliDiagnosticFormat.Text;
        var profile = configuration.Profile ?? CliProfile.Trusted;
        var languageVersion = configuration.LanguageVersion ?? LuaLanguageVersions.Default;
        var executionBackend = configuration.ExecutionBackend ?? CliExecutionBackend.Auto;
        var buildTarget = configuration.BuildTarget ?? CliBuildTarget.Chunk;
        var dumpKind = configuration.DumpKind ?? CliDumpKind.Summary;
        var dumpFormat = configuration.DumpFormat ?? CliDumpFormat.Text;
        var warningsAsErrors = configuration.WarningsAsErrors ?? false;
        var stripDebug = configuration.StripDebug ?? false;
        var maximumInputBytes = configuration.MaximumInputBytes ?? 64L * 1024 * 1024;
        var maximumInstructionCount = configuration.MaximumInstructionCount ?? 100_000_000;
        var maximumStackSlots = configuration.MaximumStackSlots ?? 1_000_000;
        var maximumCallDepth = configuration.MaximumCallDepth ?? 20_000;
        var maximumHeapBytes = configuration.MaximumHeapBytes ?? 256L * 1024 * 1024;
        string? output = null;
        string? moduleName = null;
        var showHelp = false;
        var showVersion = false;
        var afterSeparator = false;
        var outputSpecified = false;
        var targetSpecified = false;
        var dumpKindSpecified = false;
        var dumpFormatSpecified = false;
        var stripDebugSpecified = false;

        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (afterSeparator)
            {
                scriptArguments.Add(argument);
                continue;
            }

            if (argument == "--")
            {
                if (command != CliCommand.Run)
                {
                    throw new CliUsageException("The '--' script-argument separator is valid only for 'run'.");
                }

                afterSeparator = true;
                continue;
            }

            if (argument.Length == 0 || argument == "-" || argument[0] != '-')
            {
                if (command == CliCommand.None && TryParseCommand(argument, out var parsedCommand))
                {
                    command = parsedCommand;
                }
                else
                {
                    inputs.Add(argument);
                }

                continue;
            }

            var (name, inlineValue) = SplitOption(argument);
            switch (name)
            {
                case "-h":
                case "--help":
                    EnsureNoValue(name, inlineValue);
                    showHelp = true;
                    break;
                case "--version":
                    EnsureNoValue(name, inlineValue);
                    showVersion = true;
                    break;
                case "-o":
                case "--output":
                    output = ReadValue(arguments, ref index, name, inlineValue);
                    outputSpecified = true;
                    break;
                case "--module-name":
                    moduleName = ReadValue(arguments, ref index, name, inlineValue);
                    break;
                case "--module-root":
                    moduleRoots.Add(ReadValue(arguments, ref index, name, inlineValue));
                    break;
                case "--path-pattern":
                    if (configuration.PathPatterns.IsDefaultOrEmpty && pathPatterns.Count == 0)
                    {
                        pathPatterns.Clear();
                    }

                    pathPatterns.Add(ReadValue(arguments, ref index, name, inlineValue));
                    break;
                case "--diagnostic-format":
                    diagnosticFormat = ParseDiagnosticFormat(
                        ReadValue(arguments, ref index, name, inlineValue));
                    break;
                case "--profile":
                    profile = ParseProfile(ReadValue(arguments, ref index, name, inlineValue));
                    break;
                case "--lua-version":
                    languageVersion = ParseLanguageVersion(
                        ReadValue(arguments, ref index, name, inlineValue));
                    break;
                case "--execution":
                    executionBackend = ParseExecutionBackend(
                        ReadValue(arguments, ref index, name, inlineValue));
                    break;
                case "--sandbox":
                    EnsureNoValue(name, inlineValue);
                    profile = CliProfile.Sandbox;
                    break;
                case "--deterministic":
                    EnsureNoValue(name, inlineValue);
                    profile = CliProfile.Deterministic;
                    break;
                case "--trusted":
                    EnsureNoValue(name, inlineValue);
                    profile = CliProfile.Trusted;
                    break;
                case "--warnings-as-errors":
                    EnsureNoValue(name, inlineValue);
                    warningsAsErrors = true;
                    break;
                case "--no-warnings-as-errors":
                    EnsureNoValue(name, inlineValue);
                    warningsAsErrors = false;
                    break;
                case "--strip-debug":
                    EnsureNoValue(name, inlineValue);
                    stripDebug = true;
                    stripDebugSpecified = true;
                    break;
                case "--target":
                    buildTarget = ParseBuildTarget(ReadValue(arguments, ref index, name, inlineValue));
                    targetSpecified = true;
                    break;
                case "--kind":
                    dumpKind = ParseDumpKind(ReadValue(arguments, ref index, name, inlineValue));
                    dumpKindSpecified = true;
                    break;
                case "--format":
                    dumpFormat = ParseDumpFormat(ReadValue(arguments, ref index, name, inlineValue));
                    dumpFormatSpecified = true;
                    break;
                case "--maximum-input-bytes":
                    var rawLimit = ReadValue(arguments, ref index, name, inlineValue);
                    if (!long.TryParse(rawLimit, out maximumInputBytes) || maximumInputBytes <= 0)
                    {
                        throw new CliUsageException($"Option '{name}' requires a positive integer.");
                    }

                    break;
                case "--maximum-instructions":
                    maximumInstructionCount = ParsePositiveLong(
                        name,
                        ReadValue(arguments, ref index, name, inlineValue));
                    break;
                case "--maximum-stack-slots":
                    maximumStackSlots = ParsePositiveInt(
                        name,
                        ReadValue(arguments, ref index, name, inlineValue));
                    break;
                case "--maximum-call-depth":
                    maximumCallDepth = ParsePositiveInt(
                        name,
                        ReadValue(arguments, ref index, name, inlineValue));
                    break;
                case "--maximum-heap-bytes":
                    maximumHeapBytes = ParsePositiveLong(
                        name,
                        ReadValue(arguments, ref index, name, inlineValue));
                    break;
                case "--config":
                    _ = ReadValue(arguments, ref index, name, inlineValue);
                    break;
                case "--no-config":
                    EnsureNoValue(name, inlineValue);
                    break;
                default:
                    throw new CliUsageException($"Unknown option '{name}'.");
            }
        }

        if (pathPatterns.Count == 0)
        {
            pathPatterns.Add("?.lua");
            pathPatterns.Add("?/init.lua");
        }

        if (!showHelp && !showVersion)
        {
            ValidateCommand(
                command,
                inputs.Count,
                output,
                buildTarget,
                outputSpecified,
                targetSpecified,
                dumpKindSpecified,
                dumpFormatSpecified,
                stripDebugSpecified);
        }

        return new CliOptions
        {
            Command = command,
            Inputs = inputs.ToImmutable(),
            ScriptArguments = scriptArguments.ToImmutable(),
            OutputPath = output,
            ModuleName = moduleName,
            ModuleRoots = moduleRoots.ToImmutable(),
            PathPatterns = pathPatterns.ToImmutable(),
            DiagnosticFormat = diagnosticFormat,
            Profile = profile,
            LanguageVersion = languageVersion,
            ExecutionBackend = executionBackend,
            BuildTarget = buildTarget,
            DumpKind = dumpKind,
            DumpFormat = dumpFormat,
            WarningsAsErrors = warningsAsErrors,
            StripDebug = stripDebug,
            ShowHelp = showHelp,
            ShowVersion = showVersion,
            MaximumInputBytes = maximumInputBytes,
            MaximumInstructionCount = maximumInstructionCount,
            MaximumStackSlots = maximumStackSlots,
            MaximumCallDepth = maximumCallDepth,
            MaximumHeapBytes = maximumHeapBytes,
        };
    }

    private static void ValidateCommand(
        CliCommand command,
        int inputCount,
        string? output,
        CliBuildTarget buildTarget,
        bool outputSpecified,
        bool targetSpecified,
        bool dumpKindSpecified,
        bool dumpFormatSpecified,
        bool stripDebugSpecified)
    {
        if (buildTarget == CliBuildTarget.Aot)
        {
            throw new CliRemovedFeatureException(
                "Lua AOT was removed in Lunil 0.8.0-alpha.12; use '--target chunk' or run the module with the interpreter/JIT backend.");
        }

        if (command == CliCommand.None)
        {
            throw new CliUsageException("A command is required: run, check, build, or dump.");
        }

        if (outputSpecified && command is not (CliCommand.Build or CliCommand.Dump))
        {
            throw new CliUsageException("Option '--output' is valid only for 'build' and 'dump'.");
        }

        if (targetSpecified && command != CliCommand.Build)
        {
            throw new CliUsageException("Option '--target' is valid only for 'build'.");
        }

        if (stripDebugSpecified && command != CliCommand.Build)
        {
            throw new CliUsageException("Option '--strip-debug' is valid only for 'build'.");
        }

        if ((dumpKindSpecified || dumpFormatSpecified) && command != CliCommand.Dump)
        {
            throw new CliUsageException("Options '--kind' and '--format' are valid only for 'dump'.");
        }

        if (command == CliCommand.Check)
        {
            if (inputCount == 0)
            {
                throw new CliUsageException("Command 'check' requires at least one input.");
            }

            return;
        }

        if (inputCount != 1)
        {
            throw new CliUsageException($"Command '{command.ToString().ToLowerInvariant()}' requires exactly one input.");
        }

        if (command == CliCommand.Build && string.IsNullOrWhiteSpace(output))
        {
            throw new CliUsageException("Command 'build' requires --output <path>.");
        }
    }

    private static bool TryParseCommand(string value, out CliCommand command)
    {
        command = value switch
        {
            "run" => CliCommand.Run,
            "check" => CliCommand.Check,
            "build" => CliCommand.Build,
            "dump" => CliCommand.Dump,
            _ => CliCommand.None,
        };
        return command != CliCommand.None;
    }

    private static (string Name, string? Value) SplitOption(string argument)
    {
        var separator = argument.IndexOf('=');
        return separator < 0
            ? (argument, null)
            : (argument[..separator], argument[(separator + 1)..]);
    }

    private static string ReadValue(
        IReadOnlyList<string> arguments,
        ref int index,
        string option,
        string? inlineValue)
    {
        if (inlineValue is not null)
        {
            if (inlineValue.Length == 0)
            {
                throw new CliUsageException($"Option '{option}' requires a value.");
            }

            return inlineValue;
        }

        if (++index >= arguments.Count)
        {
            throw new CliUsageException($"Option '{option}' requires a value.");
        }

        return arguments[index];
    }

    private static void EnsureNoValue(string option, string? value)
    {
        if (value is not null)
        {
            throw new CliUsageException($"Option '{option}' does not accept a value.");
        }
    }

    internal static CliDiagnosticFormat ParseDiagnosticFormat(string value) => value switch
    {
        "text" => CliDiagnosticFormat.Text,
        "json" => CliDiagnosticFormat.Json,
        _ => throw new CliUsageException("Diagnostic format must be 'text' or 'json'."),
    };

    internal static CliProfile ParseProfile(string value) => value switch
    {
        "trusted" => CliProfile.Trusted,
        "sandbox" or "restricted" => CliProfile.Sandbox,
        "deterministic" => CliProfile.Deterministic,
        _ => throw new CliUsageException("Profile must be 'trusted', 'sandbox', or 'deterministic'."),
    };

    internal static LuaLanguageVersion ParseLanguageVersion(string value)
    {
        if (LuaLanguageVersions.TryParse(value, out var version))
        {
            return version;
        }

        throw new CliUsageException("Lua version must be 5.1, 5.2, 5.3, 5.4, or 5.5.");
    }

    internal static CliExecutionBackend ParseExecutionBackend(string value) => value switch
    {
        "auto" => CliExecutionBackend.Auto,
        "interpreter" => CliExecutionBackend.Interpreter,
        "jit" => CliExecutionBackend.Jit,
        _ => throw new CliUsageException("Execution backend must be 'auto', 'interpreter', or 'jit'."),
    };

    internal static CliBuildTarget ParseBuildTarget(string value) => value switch
    {
        "chunk" => CliBuildTarget.Chunk,
        "aot" => CliBuildTarget.Aot,
        _ => throw new CliUsageException("Build target must be 'chunk'."),
    };

    internal static CliDumpKind ParseDumpKind(string value) => value switch
    {
        "summary" => CliDumpKind.Summary,
        "syntax" => CliDumpKind.Syntax,
        "annotations" => CliDumpKind.Annotations,
        "analysis" => CliDumpKind.Analysis,
        "ir" => CliDumpKind.Ir,
        "chunk" => CliDumpKind.Chunk,
        _ => throw new CliUsageException(
            "Dump kind must be summary, syntax, annotations, analysis, ir, or chunk."),
    };

    internal static CliDumpFormat ParseDumpFormat(string value) => value switch
    {
        "text" => CliDumpFormat.Text,
        "json" => CliDumpFormat.Json,
        _ => throw new CliUsageException("Dump format must be 'text' or 'json'."),
    };

    private static long ParsePositiveLong(string option, string value)
    {
        if (!long.TryParse(value, out var result) || result <= 0)
        {
            throw new CliUsageException($"Option '{option}' requires a positive integer.");
        }

        return result;
    }

    private static int ParsePositiveInt(string option, string value)
    {
        if (!int.TryParse(value, out var result) || result <= 0)
        {
            throw new CliUsageException($"Option '{option}' requires a positive 32-bit integer.");
        }

        return result;
    }
}
