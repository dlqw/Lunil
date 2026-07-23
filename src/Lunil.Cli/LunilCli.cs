using System.Reflection;
using Lunil.Cli.CommandLine;
using Lunil.Cli.Commands;
using Lunil.Cli.Configuration;
using Lunil.Cli.Diagnostics;
using Lunil.Cli.IO;
using Lunil.Core.Diagnostics;
using Lunil.Runtime;

namespace Lunil.Cli;

internal static class LunilCli
{
    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        Stream standardInput,
        Stream standardOutput,
        Stream standardError,
        string currentDirectory,
        Func<string, string?>? getEnvironmentVariable = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(standardInput);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;

        var diagnosticFormat = GuessDiagnosticFormat(arguments);
        try
        {
            var expanded = CliArgumentExpander.Expand(arguments, currentDirectory);
            var configuration = CliConfigurationLoader.Load(
                expanded,
                Path.GetFullPath(currentDirectory),
                getEnvironmentVariable);
            diagnosticFormat = configuration.DiagnosticFormat ?? diagnosticFormat;
            var options = CliParser.Parse(expanded, configuration);
            diagnosticFormat = options.DiagnosticFormat;
            if (options.ShowVersion)
            {
                await CliStreams.WriteTextAsync(
                    standardOutput,
                    GetVersion() + "\n",
                    cancellationToken).ConfigureAwait(false);
                return (int)CliExitCode.Success;
            }

            if (options.ShowHelp)
            {
                await CliStreams.WriteTextAsync(
                    standardOutput,
                    GetHelp(options.Command),
                    cancellationToken).ConfigureAwait(false);
                return (int)CliExitCode.Success;
            }

            if (options.Command == CliCommand.Check && options.Inputs.Length > 1 &&
                options.ModuleName is not null)
            {
                throw new CliUsageException(
                    "Option '--module-name' cannot be used with multiple check inputs.");
            }

            var context = new CliCommandContext(
                options,
                standardInput,
                standardOutput,
                standardError,
                Path.GetFullPath(currentDirectory),
                cancellationToken);
            var result = options.Command switch
            {
                CliCommand.Run => await RunCommand.ExecuteAsync(context).ConfigureAwait(false),
                CliCommand.Check => await CheckCommand.ExecuteAsync(context).ConfigureAwait(false),
                CliCommand.Build => await BuildCommand.ExecuteAsync(context).ConfigureAwait(false),
                CliCommand.Dump => await DumpCommand.ExecuteAsync(context).ConfigureAwait(false),
                CliCommand.Patch => await PatchCommand.ExecuteAsync(context).ConfigureAwait(false),
                _ => throw new CliUsageException("A command is required."),
            };
            return (int)result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await WriteProblemAsync(
                standardError,
                diagnosticFormat,
                "LUNIL0005",
                "cancelled",
                "The Lunil operation was cancelled.",
                CancellationToken.None).ConfigureAwait(false);
            return (int)CliExitCode.Cancelled;
        }
        catch (CliUsageException exception)
        {
            await WriteProblemAsync(
                standardError,
                diagnosticFormat,
                "LUNIL0001",
                "usage",
                exception.Message,
                CancellationToken.None).ConfigureAwait(false);
            return (int)CliExitCode.Usage;
        }
        catch (CliRemovedFeatureException exception)
        {
            await WriteProblemAsync(
                standardError,
                diagnosticFormat,
                "LUNIL0006",
                "removed-feature",
                exception.Message,
                CancellationToken.None).ConfigureAwait(false);
            return (int)CliExitCode.Usage;
        }
        catch (CliInputException exception)
        {
            await WriteProblemAsync(
                standardError,
                diagnosticFormat,
                "LUNIL0002",
                "input",
                exception.Message,
                CancellationToken.None).ConfigureAwait(false);
            return (int)CliExitCode.InputOutput;
        }
        catch (CliBuildException exception)
        {
            await WriteProblemAsync(
                standardError,
                diagnosticFormat,
                "LUNIL0003",
                "build",
                exception.Message,
                CancellationToken.None).ConfigureAwait(false);
            return (int)CliExitCode.Build;
        }
        catch (LuaRuntimeException exception)
        {
            await WriteProblemAsync(
                standardError,
                diagnosticFormat,
                "LUA9001",
                "execution",
                exception.Message,
                CancellationToken.None).ConfigureAwait(false);
            return (int)CliExitCode.Execution;
        }
        catch (Exception exception)
        {
            await WriteProblemAsync(
                standardError,
                diagnosticFormat,
                "LUNIL0004",
                "internal",
                $"{exception.GetType().Name}: {exception.Message}",
                CancellationToken.None).ConfigureAwait(false);
            return (int)CliExitCode.Build;
        }
    }

    private static Task WriteProblemAsync(
        Stream standardError,
        CliDiagnosticFormat format,
        string code,
        string phase,
        string message,
        CancellationToken cancellationToken) =>
        CliDiagnosticWriter.WriteAsync(
            standardError,
            [CliDiagnosticWriter.CreateProblem(
                "<cli>",
                code,
                DiagnosticSeverity.Error,
                phase,
                message)],
            format,
            cancellationToken);

    private static CliDiagnosticFormat GuessDiagnosticFormat(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == "--diagnostic-format" && index + 1 < arguments.Count)
            {
                return arguments[index + 1] == "json"
                    ? CliDiagnosticFormat.Json
                    : CliDiagnosticFormat.Text;
            }

            if (argument == "--diagnostic-format=json")
            {
                return CliDiagnosticFormat.Json;
            }
        }

        return CliDiagnosticFormat.Text;
    }

    private static string GetVersion()
    {
        var informational = typeof(LunilCli).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
        {
            return typeof(LunilCli).Assembly.GetName().Version?.ToString() ?? "unknown";
        }

        var metadata = informational.IndexOf('+');
        return metadata < 0 ? informational : informational[..metadata];
    }

    private static string GetHelp(CliCommand command)
    {
        const string global = """
Lunil Lua 5.4 compiler, analyzer, runner, and chunk builder

Usage:
  lunil run <input|-> [options] [-- script-args...]
  lunil check <input...> [options]
  lunil build <input> --output <path> [--target chunk] [options]
  lunil dump <input> [--kind <kind>] [--format text|json] [options]
  lunil patch <pack|verify|inspect|dry-run|diff> ... [options]

Global options:
  -h, --help                      Show help.
      --version                   Show the Lunil version.
      --config <path>             Read an explicit lunil.json file.
      --no-config                 Disable configuration discovery.
      --diagnostic-format <kind>  text or json diagnostics (default: text).
      --module-root <path>        Add a module resolver and sandbox root.
      --path-pattern <pattern>    Add a Lua '?' path pattern.
      --module-name <name>        Override the root logical module name.
      --trusted                   Enable trusted host capabilities (default).
      --lua-version <version>     Select Lua 5.1, 5.2, 5.3, 5.4, or 5.5 (default: 5.4).
      --sandbox                   Use a root-confined read-only file system.
      --deterministic             Use sandbox capabilities and deterministic time/hash.
      --execution <backend>       auto, interpreter, or jit (default: auto).
      --warnings-as-errors        Promote warnings to errors.
      --maximum-input-bytes <n>   Bound each input and resolved module.
      --maximum-instructions <n>  Bound VM instructions per execution.
      --maximum-stack-slots <n>   Bound VM stack slots.
      --maximum-call-depth <n>    Bound Lua call depth.
      --maximum-heap-bytes <n>    Bound logical Lua heap bytes.

Exit codes:
  0 success; 1 diagnostics; 2 usage/config; 3 input/output; 4 execution;
  5 build/internal; 130 cancelled.

Response files use @file, UTF-8 quoting, backslash escapes, and # comments.
Configuration precedence is defaults < lunil.json < LUNIL_* environment < CLI.
""";
        return command switch
        {
            CliCommand.Run => global + """

run options:
  Arguments after '--' become main-chunk varargs and the global arg table.
  Source input is workspace-preflighted; PUC Lua 5.4 chunks execute directly.
""",
            CliCommand.Check => global + """

check options:
  Multiple source roots share one workspace graph. Binary chunks are verified independently.
""",
            CliCommand.Build => global + """

build options:
  -o, --output <path>             Required output file/directory.
      --target <chunk>            Build a PUC Lua 5.4 chunk (default: chunk).
      --strip-debug               Strip chunk debug data.
""",
            CliCommand.Dump => global + """

dump options:
  -o, --output <path|->           Write the dump to a file or stdout.
      --kind <kind>               summary, syntax, annotations, analysis, ir, or chunk.
      --format <text|json>        Dump serialization format (default: text).
""",
            CliCommand.Patch => global + """

patch actions:
  pack <manifest.json> <payload-root> -o <bundle> --private-key <pem> --key-id <id>
  verify <bundle> --public-key <pem> --key-id <id>
  inspect <bundle> --public-key <pem> --key-id <id>
  dry-run <bundle> --public-key <pem> --key-id <id>
  diff <base-bundle> <target-bundle> --public-key <pem> --key-id <id>

  For verification actions, --trust-store <json> replaces --public-key and --key-id.
""",
            _ => global,
        };
    }
}
