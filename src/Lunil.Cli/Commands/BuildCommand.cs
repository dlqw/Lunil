using System.Collections.Immutable;
using System.Text.Json;
using Lunil.Cli.CommandLine;
using Lunil.Cli.Diagnostics;
using Lunil.Cli.IO;
using Lunil.CodeGen.Cil;
using Lunil.CodeGen.Cil.Artifacts;
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
                    input.SourceIdentity,
                    input.Bytes,
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
                    module.SourceIdentity,
                    module.Compilation.Source.Text.ToArray(),
                    module.Compilation.Module));
            }
        }

        if (modules.Count == 0)
        {
            throw new CliBuildException("No buildable modules were produced.");
        }

        ValidateArtifactPaths(modules);

        var output = Path.GetFullPath(context.Options.OutputPath!, context.CurrentDirectory);
        if (context.Options.BuildTarget == CliBuildTarget.Chunk)
        {
            await EmitChunksAsync(context, input, modules, output).ConfigureAwait(false);
        }
        else
        {
            if (!await EmitAotAsync(context, modules, output).ConfigureAwait(false))
            {
                return CliExitCode.Build;
            }
        }

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

    private static async Task<bool> EmitAotAsync(
        CliCommandContext context,
        IReadOnlyList<BuildModule> modules,
        string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var diagnostics = new List<CliDiagnosticRecord>();
        foreach (var module in modules.OrderBy(static module => module.Name, StringComparer.Ordinal))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var compilation = LuaAotCompiler.Compile(module.Module, new LuaAotCompilationOptions
            {
                EmitPortablePdb = !context.Options.StripDebug,
                SourceDocument = new LuaAotSourceDocument
                {
                    LogicalName = module.SourceIdentity,
                    Content = module.SourceBytes.ToImmutableArray(),
                },
            });
            if (!compilation.Succeeded || compilation.Artifact is null)
            {
                diagnostics.AddRange(compilation.Diagnostics.Select(diagnostic =>
                    CliDiagnosticWriter.CreateProblem(
                        module.SourceIdentity,
                        diagnostic.Code,
                        DiagnosticSeverity.Error,
                        "aot",
                        diagnostic.Message)));
                continue;
            }

            var artifact = compilation.Artifact;
            var stem = Path.Combine(outputDirectory, GetArtifactRelativePath(module.Name));
            var assemblyPath = stem + ".dll";
            await AtomicWriteAsync(
                assemblyPath,
                artifact.PeImage.ToArray(),
                context.CancellationToken).ConfigureAwait(false);
            if (!artifact.PortablePdbImage.IsDefaultOrEmpty)
            {
                await AtomicWriteAsync(
                    stem + ".pdb",
                    artifact.PortablePdbImage.ToArray(),
                    context.CancellationToken).ConfigureAwait(false);
            }
            else
            {
                DeleteStaleArtifact(stem + ".pdb");
            }

            await AtomicWriteAsync(
                stem + ".canonical.bin",
                LuaAotModuleIdentity.SerializeCanonicalModule(module.Module),
                context.CancellationToken).ConfigureAwait(false);
            await AtomicWriteAsync(
                stem + ".manifest.json",
                SerializeManifest(module.Name, artifact.Manifest),
                context.CancellationToken).ConfigureAwait(false);
            await CliStreams.WriteTextAsync(
                context.StandardOutput,
                assemblyPath + "\n",
                context.CancellationToken).ConfigureAwait(false);
        }

        await WriteDiagnosticsAsync(context, diagnostics).ConfigureAwait(false);
        return !CliDiagnosticWriter.HasErrors(diagnostics);
    }

    private static byte[] SerializeManifest(string moduleName, LuaAotArtifactManifest manifest)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "lunil.aot.manifest.v1");
            writer.WriteString("moduleName", moduleName);
            writer.WriteString("magic", manifest.Magic);
            writer.WriteNumber("artifactSchemaVersion", manifest.ArtifactSchemaVersion);
            writer.WriteNumber("irFormatVersion", manifest.IrFormatVersion);
            writer.WriteNumber("runtimeAbiVersion", manifest.RuntimeAbiVersion);
            writer.WriteNumber("codegenVersion", manifest.CodegenVersion);
            writer.WriteString("moduleContentId", manifest.ModuleContentId);
            writer.WriteString("moduleChecksum", manifest.ModuleChecksum);
            writer.WriteString("optionsFingerprint", manifest.OptionsFingerprint);
            writer.WriteBoolean("emitPortablePdb", manifest.EmitPortablePdb);
            writer.WriteString("assemblyName", manifest.AssemblyName);
            writer.WriteString("typeName", manifest.TypeName);
            writer.WriteString("portablePdbName", manifest.PortablePdbName);
            writer.WriteString("sourceDocumentName", manifest.SourceDocumentName);
            writer.WriteString("sourceDocumentChecksum", manifest.SourceDocumentChecksum);
            writer.WriteStartArray("functions");
            foreach (var function in manifest.Functions.OrderBy(static function => function.FunctionId))
            {
                writer.WriteStartObject();
                writer.WriteNumber("functionId", function.FunctionId);
                writer.WriteStartArray("shards");
                foreach (var shard in function.Shards)
                {
                    writer.WriteStartObject();
                    writer.WriteString("methodName", shard.MethodName);
                    writer.WriteNumber("startProgramCounter", shard.StartProgramCounter);
                    writer.WriteNumber("instructionCount", shard.InstructionCount);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
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

    private static void DeleteStaleArtifact(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException or ArgumentException)
        {
            throw new CliBuildException(
                $"Cannot remove stale build output '{Path.GetFullPath(path)}': {exception.Message}",
                exception);
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
        string SourceIdentity,
        byte[] SourceBytes,
        LuaIrModule Module);
}
