using Lunil.Cli.Diagnostics;
using Lunil.Core.Diagnostics;
using Lunil.IR.Lua52;
using Lunil.IR.Lua51;
using Lunil.IR.Lua53;
using Lunil.IR.Lua54;
using Lunil.IR.Lua55;
using Lunil.Runtime;
using Lunil.Runtime.Execution;

namespace Lunil.Cli.Commands;

internal static class RunCommand
{
    public static async Task<CliExitCode> ExecuteAsync(CliCommandContext context)
    {
        var input = await CliInputDocument.LoadAsync(
            context.Options.Inputs[0],
            context.Options,
            context.CurrentDirectory,
            context.StandardInput,
            context.CancellationToken).ConfigureAwait(false);
        using var host = context.CreateHost([input], out _);
        var arguments = context.CreateScriptArguments(host, input.Input);
        if (input.IsBinaryChunk)
        {
            try
            {
                var result = host.ExecuteBinaryChunk(input.Bytes, arguments);
                return await FinishExecutionAsync(context, input.DisplayPath, result)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is Lua52ChunkFormatException or Lua53ChunkFormatException or
                Lua51ChunkFormatException or Lua54ChunkFormatException or Lua55ChunkFormatException or InvalidDataException or ArgumentException)
            {
                await WriteProblemAsync(
                    context,
                    input.DisplayPath,
                    "LUA8001",
                    "chunk",
                    exception.Message).ConfigureAwait(false);
                return CliExitCode.Diagnostics;
            }
            catch (LuaRuntimeException exception)
            {
                await WriteProblemAsync(
                    context,
                    input.DisplayPath,
                    "LUA9001",
                    "execution",
                    exception.Message).ConfigureAwait(false);
                return CliExitCode.Execution;
            }
        }

        var workspace = await host.AnalyzeWorkspaceAsync(
            [input.ToWorkspaceDocument()],
            context.CancellationToken).ConfigureAwait(false);
        var diagnostics = CliDiagnosticWriter.FromWorkspace(
            workspace,
            context.Options.WarningsAsErrors);
        await CliDiagnosticWriter.WriteAsync(
            context.StandardError,
            diagnostics,
            context.Options.DiagnosticFormat,
            context.CancellationToken).ConfigureAwait(false);
        if (CliDiagnosticWriter.HasErrors(diagnostics))
        {
            return CliExitCode.Diagnostics;
        }

        var root = workspace.GetModule(input.ModuleName);
        if (root?.Compilation.Module is null)
        {
            await WriteProblemAsync(
                context,
                input.DisplayPath,
                "LUA8002",
                "workspace",
                $"Workspace did not produce root module '{input.ModuleName}'.").ConfigureAwait(false);
            return CliExitCode.Diagnostics;
        }

        try
        {
            var execution = host.Execute(root.Compilation, arguments);
            return await FinishExecutionAsync(context, input.DisplayPath, execution)
                .ConfigureAwait(false);
        }
        catch (LuaRuntimeException exception)
        {
            await WriteProblemAsync(
                context,
                input.DisplayPath,
                "LUA9001",
                "execution",
                exception.Message).ConfigureAwait(false);
            return CliExitCode.Execution;
        }
    }

    private static async Task<CliExitCode> FinishExecutionAsync(
        CliCommandContext context,
        string source,
        LuaExecutionResult result)
    {
        if (result.Signal == LuaVmSignal.Completed)
        {
            return CliExitCode.Success;
        }

        var message = result.Values.IsDefaultOrEmpty
            ? result.Signal.ToString()
            : result.Values[0].ToString();
        await WriteProblemAsync(
            context,
            source,
            "LUA9001",
            "execution",
            message).ConfigureAwait(false);
        return CliExitCode.Execution;
    }

    private static Task WriteProblemAsync(
        CliCommandContext context,
        string source,
        string code,
        string phase,
        string message) =>
        CliDiagnosticWriter.WriteAsync(
            context.StandardError,
            [CliDiagnosticWriter.CreateProblem(
                source,
                code,
                DiagnosticSeverity.Error,
                phase,
                message)],
            context.Options.DiagnosticFormat,
            context.CancellationToken);
}
