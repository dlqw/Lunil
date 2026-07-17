using Lunil.Cli.CommandLine;
using Lunil.Cli.Diagnostics;
using Lunil.Cli.IO;
using Lunil.Compiler;
using Lunil.Core.Diagnostics;
using Lunil.IR.Canonical;
using Lunil.IR.Lua54;

namespace Lunil.Cli.Commands;

internal static class BuildCommand
{
    public static async Task<CliExitCode> ExecuteAsync(CliCommandContext context)
    {
        var input = await CliInputDocument.LoadAsync(
            context.Options.Inputs[0],
            context.Options,
            context.CurrentDirectory,
            context.StandardInput,
            context.CancellationToken).ConfigureAwait(false);
        var modules = new List<BuildModule>();
        if (input.IsBinaryChunk)
        {
            try
            {
                modules.Add(new BuildModule(
                    input.ModuleName,
                    Lua54PrototypeConverter.Convert(input.Bytes)));
            }
            catch (Exception exception) when (exception is Lua54ChunkFormatException or
                InvalidDataException or ArgumentException)
            {
                await WriteDiagnosticsAsync(context, [CliDiagnosticWriter.CreateProblem(
                    input.DisplayPath,
                    "LUA8001",
                    DiagnosticSeverity.Error,
                    "chunk",
                    exception.Message)]).ConfigureAwait(false);
                return CliExitCode.Diagnostics;
            }
        }
        else
        {
            using var workspaceService = context.CreateWorkspace([input], out _);
            var workspace = await workspaceService.AnalyzeAsync(
                [input.ToWorkspaceDocument()],
                context.CancellationToken).ConfigureAwait(false);
            var diagnostics = CliDiagnosticWriter.FromWorkspace(
                workspace,
                context.Options.WarningsAsErrors);
            await WriteDiagnosticsAsync(context, diagnostics).ConfigureAwait(false);
            if (CliDiagnosticWriter.HasErrors(diagnostics))
            {
                return CliExitCode.Diagnostics;
            }

            foreach (var module in workspace.Modules)
            {
                if (module.Compilation.Module is null)
                {
                    continue;
                }

                modules.Add(new BuildModule(
                    module.Identity.Name,
                    module.Compilation.Module));
            }
        }

        if (modules.Count == 0)
        {
            throw new CliBuildException("No buildable modules were produced.");
        }

        ValidateArtifactPaths(modules);

        var output = Path.GetFullPath(context.Options.OutputPath!, context.CurrentDirectory);
        await EmitChunksAsync(context, input, modules, output).ConfigureAwait(false);

        return CliExitCode.Success;
    }

    private static async Task EmitChunksAsync(
        CliCommandContext context,
        CliInputDocument input,
        IReadOnlyList<BuildModule> modules,
        string output)
    {
        var outputIsDirectory = modules.Count > 1 || Directory.Exists(output) ||
            EndsInDirectorySeparator(context.Options.OutputPath!);
        if (outputIsDirectory)
        {
            Directory.CreateDirectory(output);
        }

        foreach (var module in modules.OrderBy(static module => module.Name, StringComparer.Ordinal))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var bytes = Lua54CanonicalPrototypeWriter.Write(
                module.Module,
                functionId: 0,
                context.Options.StripDebug);
            var path = outputIsDirectory
                ? Path.Combine(output, GetArtifactRelativePath(module.Name) + ".luac")
                : output;
            if (input.FilePath is not null && PathsEqual(path, input.FilePath))
            {
                throw new CliBuildException("The build output cannot overwrite its input file.");
            }

            await AtomicWriteAsync(path, bytes, context.CancellationToken).ConfigureAwait(false);
            await CliStreams.WriteTextAsync(
                context.StandardOutput,
                path + "\n",
                context.CancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task AtomicWriteAsync(
        string path,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, fullPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException or ArgumentException)
        {
            throw new CliBuildException($"Cannot write build output '{fullPath}': {exception.Message}", exception);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string GetArtifactRelativePath(string moduleName)
    {
        var segments = moduleName.Split(['.', '/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return "module";
        }

        return Path.Combine(segments.Select(SanitizeSegment).ToArray());
    }

    private static void ValidateArtifactPaths(IEnumerable<BuildModule> modules)
    {
        var paths = new Dictionary<string, string>(GetPathComparer());
        foreach (var module in modules)
        {
            var path = GetArtifactRelativePath(module.Name);
            if (paths.TryGetValue(path, out var previous))
            {
                throw new CliBuildException(
                    $"Modules '{previous}' and '{module.Name}' map to the same artifact path '{path}'.");
            }

            paths.Add(path, module.Name);
        }
    }

    private static string SanitizeSegment(string segment)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var value = new string(segment.Select(character => invalid.Contains(character) ||
            char.IsControl(character) ? '_' : character).ToArray());
        return value is "." or ".." or "" ? "_" : value;
    }

    private static bool EndsInDirectorySeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static StringComparer GetPathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static Task WriteDiagnosticsAsync(
        CliCommandContext context,
        IEnumerable<CliDiagnosticRecord> diagnostics) =>
        CliDiagnosticWriter.WriteAsync(
            context.StandardError,
            diagnostics,
            context.Options.DiagnosticFormat,
            context.CancellationToken);

    private sealed record BuildModule(
        string Name,
        LuaIrModule Module);
}
