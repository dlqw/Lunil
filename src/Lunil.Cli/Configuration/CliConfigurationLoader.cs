using System.Collections.Immutable;
using System.Text.Json;
using Lunil.Cli.CommandLine;

namespace Lunil.Cli.Configuration;

internal static class CliConfigurationLoader
{
    private static readonly HashSet<string> KnownProperties = new(StringComparer.Ordinal)
    {
        "profile",
        "diagnosticFormat",
        "buildTarget",
        "dumpKind",
        "dumpFormat",
        "moduleRoots",
        "pathPatterns",
        "warningsAsErrors",
        "stripDebug",
        "maximumInputBytes",
        "maximumInstructions",
        "maximumStackSlots",
        "maximumCallDepth",
        "maximumHeapBytes",
    };

    public static CliConfiguration Load(
        IReadOnlyList<string> arguments,
        string currentDirectory,
        Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentDirectory);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        var (configPath, noConfig) = FindConfigSelection(arguments, currentDirectory);
        var configuration = noConfig || configPath is null
            ? new CliConfiguration()
            : LoadFile(configPath);
        return ApplyEnvironment(configuration, getEnvironmentVariable);
    }

    private static (string? Path, bool NoConfig) FindConfigSelection(
        IReadOnlyList<string> arguments,
        string currentDirectory)
    {
        string? explicitPath = null;
        var noConfig = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument == "--")
            {
                break;
            }

            if (argument == "--no-config")
            {
                noConfig = true;
                continue;
            }

            if (argument.StartsWith("--config=", StringComparison.Ordinal))
            {
                explicitPath = argument["--config=".Length..];
                continue;
            }

            if (argument == "--config")
            {
                if (++index >= arguments.Count)
                {
                    throw new CliUsageException("Option '--config' requires a value.");
                }

                explicitPath = arguments[index];
            }
        }

        if (noConfig && explicitPath is not null)
        {
            throw new CliUsageException("Options '--config' and '--no-config' cannot be combined.");
        }

        if (noConfig)
        {
            return (null, true);
        }

        if (explicitPath is not null)
        {
            if (string.IsNullOrWhiteSpace(explicitPath))
            {
                throw new CliUsageException("Option '--config' requires a non-empty path.");
            }

            return (Path.GetFullPath(explicitPath, currentDirectory), false);
        }

        var discovered = Path.Combine(currentDirectory, "lunil.json");
        return File.Exists(discovered) ? (Path.GetFullPath(discovered), false) : (null, false);
    }

    private static CliConfiguration LoadFile(string path)
    {
        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new CliUsageException($"Configuration file '{path}' was not found.");
            }

            if (info.Length > 1024 * 1024)
            {
                throw new CliUsageException($"Configuration file '{path}' exceeds 1048576 bytes.");
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (CliUsageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException or ArgumentException)
        {
            throw new CliUsageException(
                $"Cannot read configuration file '{path}': {exception.Message}");
        }

        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 32,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new CliUsageException("The Lunil configuration root must be a JSON object.");
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!KnownProperties.Contains(property.Name))
                {
                    throw new CliUsageException(
                        $"Unknown Lunil configuration property '{property.Name}'.");
                }
            }

            var baseDirectory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
            return new CliConfiguration
            {
                Profile = ReadString(document.RootElement, "profile", CliParser.ParseProfile),
                DiagnosticFormat = ReadString(
                    document.RootElement,
                    "diagnosticFormat",
                    CliParser.ParseDiagnosticFormat),
                BuildTarget = ReadString(
                    document.RootElement,
                    "buildTarget",
                    CliParser.ParseBuildTarget),
                DumpKind = ReadString(document.RootElement, "dumpKind", CliParser.ParseDumpKind),
                DumpFormat = ReadString(document.RootElement, "dumpFormat", CliParser.ParseDumpFormat),
                ModuleRoots = ReadStringArray(document.RootElement, "moduleRoots", baseDirectory, paths: true),
                PathPatterns = ReadStringArray(document.RootElement, "pathPatterns", baseDirectory, paths: false),
                WarningsAsErrors = ReadBoolean(document.RootElement, "warningsAsErrors"),
                StripDebug = ReadBoolean(document.RootElement, "stripDebug"),
                MaximumInputBytes = ReadPositiveInteger(document.RootElement, "maximumInputBytes"),
                MaximumInstructionCount = ReadPositiveInteger(
                    document.RootElement,
                    "maximumInstructions"),
                MaximumStackSlots = ReadPositiveInt(document.RootElement, "maximumStackSlots"),
                MaximumCallDepth = ReadPositiveInt(document.RootElement, "maximumCallDepth"),
                MaximumHeapBytes = ReadPositiveInteger(document.RootElement, "maximumHeapBytes"),
            };
        }
        catch (CliUsageException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new CliUsageException(
                $"Configuration file '{path}' is invalid JSON: {exception.Message}");
        }
    }

    private static CliConfiguration ApplyEnvironment(
        CliConfiguration configuration,
        Func<string, string?> getEnvironmentVariable)
    {
        var profile = Get("LUNIL_PROFILE");
        var diagnostics = Get("LUNIL_DIAGNOSTIC_FORMAT");
        var buildTarget = Get("LUNIL_BUILD_TARGET");
        var dumpKind = Get("LUNIL_DUMP_KIND");
        var dumpFormat = Get("LUNIL_DUMP_FORMAT");
        var roots = Get("LUNIL_MODULE_ROOTS");
        var patterns = Get("LUNIL_PATH_PATTERNS");
        var warnings = Get("LUNIL_WARNINGS_AS_ERRORS");
        var strip = Get("LUNIL_STRIP_DEBUG");
        var limit = Get("LUNIL_MAXIMUM_INPUT_BYTES");
        var instructions = Get("LUNIL_MAXIMUM_INSTRUCTIONS");
        var stackSlots = Get("LUNIL_MAXIMUM_STACK_SLOTS");
        var callDepth = Get("LUNIL_MAXIMUM_CALL_DEPTH");
        var heapBytes = Get("LUNIL_MAXIMUM_HEAP_BYTES");
        return configuration with
        {
            Profile = profile is null ? configuration.Profile : CliParser.ParseProfile(profile),
            DiagnosticFormat = diagnostics is null
                ? configuration.DiagnosticFormat
                : CliParser.ParseDiagnosticFormat(diagnostics),
            BuildTarget = buildTarget is null
                ? configuration.BuildTarget
                : CliParser.ParseBuildTarget(buildTarget),
            DumpKind = dumpKind is null ? configuration.DumpKind : CliParser.ParseDumpKind(dumpKind),
            DumpFormat = dumpFormat is null
                ? configuration.DumpFormat
                : CliParser.ParseDumpFormat(dumpFormat),
            ModuleRoots = roots is null
                ? configuration.ModuleRoots
                : SplitNonEmpty(roots, Path.PathSeparator),
            PathPatterns = patterns is null
                ? configuration.PathPatterns
                : SplitNonEmpty(patterns, ';'),
            WarningsAsErrors = warnings is null
                ? configuration.WarningsAsErrors
                : ParseBoolean("LUNIL_WARNINGS_AS_ERRORS", warnings),
            StripDebug = strip is null
                ? configuration.StripDebug
                : ParseBoolean("LUNIL_STRIP_DEBUG", strip),
            MaximumInputBytes = limit is null
                ? configuration.MaximumInputBytes
                : ParsePositiveInteger("LUNIL_MAXIMUM_INPUT_BYTES", limit),
            MaximumInstructionCount = instructions is null
                ? configuration.MaximumInstructionCount
                : ParsePositiveInteger("LUNIL_MAXIMUM_INSTRUCTIONS", instructions),
            MaximumStackSlots = stackSlots is null
                ? configuration.MaximumStackSlots
                : ParsePositiveInt("LUNIL_MAXIMUM_STACK_SLOTS", stackSlots),
            MaximumCallDepth = callDepth is null
                ? configuration.MaximumCallDepth
                : ParsePositiveInt("LUNIL_MAXIMUM_CALL_DEPTH", callDepth),
            MaximumHeapBytes = heapBytes is null
                ? configuration.MaximumHeapBytes
                : ParsePositiveInteger("LUNIL_MAXIMUM_HEAP_BYTES", heapBytes),
        };

        string? Get(string name)
        {
            var value = getEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }

    private static T? ReadString<T>(
        JsonElement root,
        string name,
        Func<string, T> parser) where T : struct
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new CliUsageException($"Configuration property '{name}' must be a string.");
        }

        return parser(value.GetString()!);
    }

    private static ImmutableArray<string> ReadStringArray(
        JsonElement root,
        string name,
        string baseDirectory,
        bool paths)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new CliUsageException($"Configuration property '{name}' must be an array.");
        }

        var result = ImmutableArray.CreateBuilder<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(item.GetString()))
            {
                throw new CliUsageException(
                    $"Configuration property '{name}' must contain non-empty strings.");
            }

            var text = item.GetString()!;
            result.Add(paths ? Path.GetFullPath(text, baseDirectory) : text);
        }

        return result.ToImmutable();
    }

    private static bool? ReadBoolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new CliUsageException($"Configuration property '{name}' must be a boolean.");
        }

        return value.GetBoolean();
    }

    private static long? ReadPositiveInteger(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (!value.TryGetInt64(out var result) || result <= 0)
        {
            throw new CliUsageException(
                $"Configuration property '{name}' must be a positive integer.");
        }

        return result;
    }

    private static int? ReadPositiveInt(JsonElement root, string name)
    {
        var value = ReadPositiveInteger(root, name);
        if (value is null)
        {
            return null;
        }

        if (value > int.MaxValue)
        {
            throw new CliUsageException(
                $"Configuration property '{name}' exceeds a 32-bit integer.");
        }

        return (int)value.Value;
    }

    private static ImmutableArray<string> SplitNonEmpty(string value, char separator)
    {
        var result = value.Split(separator, StringSplitOptions.TrimEntries |
            StringSplitOptions.RemoveEmptyEntries);
        if (result.Length == 0)
        {
            throw new CliUsageException("An environment list must contain at least one value.");
        }

        return [.. result];
    }

    private static bool ParseBoolean(string name, string value)
    {
        if (!bool.TryParse(value, out var result))
        {
            throw new CliUsageException($"Environment variable '{name}' must be true or false.");
        }

        return result;
    }

    private static long ParsePositiveInteger(string name, string value)
    {
        if (!long.TryParse(value, out var result) || result <= 0)
        {
            throw new CliUsageException(
                $"Environment variable '{name}' must be a positive integer.");
        }

        return result;
    }

    private static int ParsePositiveInt(string name, string value)
    {
        var result = ParsePositiveInteger(name, value);
        if (result > int.MaxValue)
        {
            throw new CliUsageException(
                $"Environment variable '{name}' exceeds a 32-bit integer.");
        }

        return (int)result;
    }
}
