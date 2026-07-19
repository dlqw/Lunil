using System.Collections.Immutable;
using System.Text;
using Lunil.Cli.CommandLine;
using Lunil.Cli.IO;
using Lunil.Core;
using Lunil.Hosting;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.StandardLibrary;
using Lunil.Workspace;

namespace Lunil.Cli.Commands;

internal sealed record CliCommandContext(
    CliOptions Options,
    Stream StandardInput,
    Stream StandardOutput,
    Stream StandardError,
    string CurrentDirectory,
    CancellationToken CancellationToken)
{
    public ImmutableArray<string> ResolveModuleRoots(IEnumerable<CliInputDocument> inputs)
    {
        var roots = new HashSet<string>(GetPathComparer());
        foreach (var root in Options.ModuleRoots)
        {
            roots.Add(Path.GetFullPath(root, CurrentDirectory));
        }

        foreach (var input in inputs)
        {
            if (input.FilePath is not null)
            {
                roots.Add(Path.GetDirectoryName(input.FilePath)!);
            }
        }

        roots.Add(Path.GetFullPath(CurrentDirectory));
        return [.. roots.OrderBy(static root => root, GetPathComparer())];
    }

    public LuaHost CreateHost(
        IEnumerable<CliInputDocument> inputs,
        out ImmutableArray<string> moduleRoots)
    {
        var inputArray = inputs.ToArray();
        moduleRoots = ResolveModuleRoots(inputArray);
        var resolver = CreateModuleResolver(moduleRoots);
        var console = new CliStreamConsole(StandardInput, StandardOutput, StandardError);
        var profile = Options.Profile switch
        {
            CliProfile.Trusted => LuaHostProfile.Trusted,
            CliProfile.Sandbox => LuaHostProfile.Restricted,
            CliProfile.Deterministic => LuaHostProfile.Deterministic,
            _ => throw new InvalidOperationException("Unknown CLI profile."),
        };
        var capabilities = profile == LuaHostProfile.Trusted
            ? LuaStandardLibraryOptions.Default with { Console = console }
            : LuaHostCapabilityProfiles.Create(profile) with
            {
                Console = console,
                FileSystem = new CliReadOnlyFileSystem(
                    CurrentDirectory,
                    moduleRoots,
                    Options.MaximumInputBytes),
            };
        var executionBackend = Options.ExecutionBackend switch
        {
            CliExecutionBackend.Auto => LuaHostExecutionBackend.Auto,
            CliExecutionBackend.Interpreter => LuaHostExecutionBackend.Interpreter,
            CliExecutionBackend.Jit => LuaHostExecutionBackend.Jit,
            _ => throw new InvalidOperationException("Unknown CLI execution backend."),
        };
        var host = new LuaHost(new LuaHostOptions
        {
            Profile = profile,
            LanguageVersion = Options.LanguageVersion,
            ExecutionBackend = executionBackend,
            InstallStandardLibrary = Options.LanguageVersion is
                LuaLanguageVersion.Lua53 or LuaLanguageVersion.Lua54,
            StandardLibrary = capabilities,
            ModuleResolver = resolver,
            State = LuaStateOptions.Default with
            {
                Heap = LuaStateOptions.Default.Heap with
                {
                    MaximumLogicalBytes = Options.MaximumHeapBytes,
                },
            },
            Execution = new LuaInterpreterOptions
            {
                MaximumInstructionCount = Options.MaximumInstructionCount,
                MaximumStackSlots = Options.MaximumStackSlots,
                MaximumCallDepth = Options.MaximumCallDepth,
            },
        });
        SetPackagePath(host, moduleRoots, Options.PathPatterns);
        return host;
    }

    public LuaWorkspace CreateWorkspace(
        IEnumerable<CliInputDocument> inputs,
        out ImmutableArray<string> moduleRoots)
    {
        var inputArray = inputs.ToArray();
        moduleRoots = ResolveModuleRoots(inputArray);
        return new LuaWorkspace(
            LuaWorkspaceOptions.Default with
            {
                LanguageVersion = Options.LanguageVersion,
            },
            CreateModuleResolver(moduleRoots));
    }

    public LuaValue[] CreateScriptArguments(LuaHost host, string input)
    {
        var arguments = Options.ScriptArguments
            .Select(argument => CreateString(host, argument))
            .ToArray();
        var table = host.State.CreateTable(arrayCapacity: arguments.Length + 1);
        table.Set(LuaValue.FromInteger(0), CreateString(host, input));
        for (var index = 0; index < arguments.Length; index++)
        {
            table.Set(LuaValue.FromInteger(index + 1), arguments[index]);
        }

        host.State.SetGlobal("arg", LuaValue.FromTable(table));
        return arguments;
    }

    private static void SetPackagePath(
        LuaHost host,
        ImmutableArray<string> roots,
        ImmutableArray<string> patterns)
    {
        var package = host.State.GetGlobal("package");
        if (package.Kind != LuaValueKind.Table)
        {
            return;
        }

        var path = string.Join(
            ';',
            roots.SelectMany(root => patterns.Select(pattern =>
                Path.GetFullPath(pattern.Replace('/', Path.DirectorySeparatorChar), root))));
        package.AsTable().Set(CreateString(host, "path"), CreateString(host, path));
    }

    private static LuaValue CreateString(LuaHost host, string value) =>
        LuaValue.FromString(host.State.Strings.GetOrCreate(Encoding.UTF8.GetBytes(value)));

    private LuaFileSystemModuleResolver CreateModuleResolver(
        ImmutableArray<string> moduleRoots) => new(new LuaFileSystemModuleResolverOptions
        {
            RootDirectories = moduleRoots,
            PathPatterns = Options.PathPatterns,
            MaximumFileBytes = Options.MaximumInputBytes,
        });

    private static StringComparer GetPathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
