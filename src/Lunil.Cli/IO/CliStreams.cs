using System.Text;
using Lunil.Cli.CommandLine;
using Lunil.StandardLibrary;

namespace Lunil.Cli.IO;

internal static class CliStreams
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static Task WriteTextAsync(
        Stream stream,
        string text,
        CancellationToken cancellationToken = default) =>
        stream.WriteAsync(Utf8.GetBytes(text), cancellationToken).AsTask();

    public static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        long maximumBytes,
        string description,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length > maximumBytes - read)
            {
                throw new CliInputException(
                    $"{description} exceeds the {maximumBytes}-byte input limit.");
            }

            output.Write(buffer, 0, read);
        }
    }
}

internal sealed class CliStreamConsole(
    Stream standardInput,
    Stream standardOutput,
    Stream standardError) : ILuaConsole
{
    private readonly object _outputLock = new();
    private readonly object _errorLock = new();

    public byte[] ReadStandardInput()
    {
        using var output = new MemoryStream();
        standardInput.CopyTo(output);
        return output.ToArray();
    }

    public void Write(ReadOnlyMemory<byte> bytes)
    {
        lock (_outputLock)
        {
            standardOutput.Write(bytes.Span);
            standardOutput.Flush();
        }
    }

    public void WriteLine() => Write("\n"u8.ToArray());

    public void WriteError(ReadOnlyMemory<byte> bytes)
    {
        lock (_errorLock)
        {
            standardError.Write(bytes.Span);
            standardError.Flush();
        }
    }

    public Stream OpenStandardInput() => standardInput;

    public Stream OpenStandardOutput() => standardOutput;

    public Stream OpenStandardError() => standardError;
}
