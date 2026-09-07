using Lunil.Cli.Diagnostics;
using Lunil.Core.Diagnostics;
using Lunil.Core;
using Lunil.IR.Lua52;
using Lunil.IR.Lua51;
using Lunil.IR.Lua53;
using Lunil.IR;
using Lunil.IR.Lua54;
using Lunil.IR.Lua55;

namespace Lunil.Cli.Commands;

internal static class CheckCommand
{
    public static async Task<CliExitCode> ExecuteAsync(CliCommandContext context)
    {
        var inputs = new List<CliInputDocument>(context.Options.Inputs.Length);
        foreach (var inputPath in context.Options.Inputs)
        {
            inputs.Add(await CliInputDocument.LoadAsync(
                inputPath,
                context.Options,
                context.CurrentDirectory,
                context.StandardInput,
                context.CancellationToken).ConfigureAwait(false));
        }

        var diagnostics = new List<CliDiagnosticRecord>();
        foreach (var input in inputs.Where(static input => input.IsBinaryChunk))
        {
            try
            {
                _ = LuaChunkCodec.ReadPrototypeModule(
                    LuaVersionFeatureTable.Get(context.Options.LanguageVersion).ChunkFormat,
                    input.Bytes);
            }
            catch (Exception exception) when (exception is Lua52ChunkFormatException or
                Lua51ChunkFormatException or
                Lua53ChunkFormatException or Lua54ChunkFormatException or Lua55ChunkFormatException or
                InvalidDataException or ArgumentException)
            {
                diagnostics.Add(CliDiagnosticWriter.CreateProblem(
                    input.DisplayPath,
                    "LUA8001",
                    DiagnosticSeverity.Error,
                    "chunk",
                    exception.Message));
            }
        }

        var sources = inputs.Where(static input => !input.IsBinaryChunk).ToArray();
        if (sources.Length > 0)
        {
            using var workspaceService = context.CreateWorkspace(sources, out _);
            var workspace = await workspaceService.AnalyzeAsync(
                sources.Select(static input => input.ToWorkspaceDocument()),
                context.CancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(CliDiagnosticWriter.FromWorkspace(
                workspace,
                context.Options.WarningsAsErrors));
        }

        await CliDiagnosticWriter.WriteAsync(
            context.StandardError,
            diagnostics,
            context.Options.DiagnosticFormat,
            context.CancellationToken).ConfigureAwait(false);
        return CliDiagnosticWriter.HasErrors(diagnostics)
            ? CliExitCode.Diagnostics
            : CliExitCode.Success;
    }
}
