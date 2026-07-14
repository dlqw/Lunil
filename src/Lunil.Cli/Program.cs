namespace Lunil.Cli;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            return await LunilCli.RunAsync(
                args,
                Console.OpenStandardInput(),
                Console.OpenStandardOutput(),
                Console.OpenStandardError(),
                Environment.CurrentDirectory,
                cancellationToken: cancellation.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }
}
