using System.Text.Json;

namespace Lunil.LanguageServer;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--version", StringComparer.Ordinal))
        {
            Console.WriteLine(ProductVersion.Current);
            return 0;
        }

        if (args.Any(argument => argument != "--stdio"))
        {
            Console.Error.WriteLine("Usage: lunil-language-server [--stdio] [--version]");
            return 2;
        }

        await using var connection = new JsonRpcConnection(
            Console.OpenStandardInput(),
            Console.OpenStandardOutput());
        using var server = new LuaLanguageServer(connection);
        try
        {
            await connection.RunAsync(server.DispatchAsync, server.ExitToken).ConfigureAwait(false);
            return server.ExitCode;
        }
        catch (OperationCanceledException) when (server.ExitToken.IsCancellationRequested)
        {
            return server.ExitCode;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}
