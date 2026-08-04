using System.IO.Pipes;
using System.Text;

namespace Lunil.DebugAdapter;

public static class Program
{
    public static int Main(string[] args)
    {
        var stdioIndex = Array.IndexOf(args, "--stdio");
        var pipeIndex = Array.IndexOf(args, "--pipe");
        if (stdioIndex < 0 || pipeIndex >= args.Length - 1)
        {
            Console.Error.WriteLine(
                "Usage: lunil-debug-adapter --stdio [--pipe <name>]");
            return 2;
        }

        if (pipeIndex >= 0)
        {
            return RunAttachRelay(args[pipeIndex + 1]);
        }

        using var connection = new DapConnection(
            Console.OpenStandardInput(),
            Console.OpenStandardOutput());
        new DapSession(connection).Run();
        return 0;
    }

    /// <summary>
    /// Attach mode: connects to the host debug pipe and forwards DAP frames verbatim between the
    /// client (VS Code) and the host game loop. The host serves the protocol, so requests keep
    /// their original sequence numbers and responses match the client's pending requests exactly;
    /// the adapter is a transport bridge.
    /// </summary>
    private static int RunAttachRelay(string pipeName)
    {
        using var client = ConnectPipe(pipeName);
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        using var done = new ManualResetEventSlim();
        var writeLock = new object();
        var forwardClient = new Thread(() => ForwardFrames(input, client, writeLock, done))
        {
            Name = "lunil-dap-attach-forward",
            IsBackground = true,
        };
        var forwardPipe = new Thread(() => ForwardFrames(client, output, writeLock, done))
        {
            Name = "lunil-dap-attach-backward",
            IsBackground = true,
        };
        forwardClient.Start();
        forwardPipe.Start();
        done.Wait();
        return 0;
    }

    private static void ForwardFrames(
        Stream source,
        Stream target,
        object writeLock,
        ManualResetEventSlim done)
    {
        try
        {
            var header = new byte[64];
            while (ReadFrame(source, header) is { } length)
            {
                var body = new byte[length];
                ReadExactly(source, body, length);
                var headerBytes = Encoding.ASCII.GetBytes($"Content-Length: {length}\r\n\r\n");
                lock (writeLock)
                {
                    target.Write(headerBytes);
                    target.Write(body);
                    target.Flush();
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException)
        {
            Console.Error.WriteLine($"The debug attach relay ended: {exception.Message}");
        }
        finally
        {
            done.Set();
        }
    }

    private static int? ReadFrame(Stream source, byte[] header)
    {
        var offset = 0;
        while (true)
        {
            var read = source.Read(header, offset, 1);
            if (read == 0)
            {
                return null;
            }

            offset++;
            if (offset == header.Length)
            {
                throw new InvalidDataException("DAP header line is too long.");
            }

            if (offset >= 4 &&
                header[offset - 4] == (byte)'\r' &&
                header[offset - 3] == (byte)'\n' &&
                header[offset - 2] == (byte)'\r' &&
                header[offset - 1] == (byte)'\n')
            {
                var headerText = Encoding.ASCII.GetString(header, 0, offset - 4);
                foreach (var line in headerText.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(trimmed.AsSpan("Content-Length:".Length).Trim(), out var length))
                    {
                        if (length <= 0 || length > 64 * 1024 * 1024)
                        {
                            throw new InvalidDataException($"Invalid DAP Content-Length: {length}");
                        }

                        return length;
                    }
                }

                throw new InvalidDataException("DAP frame is missing Content-Length.");
            }
        }
    }

    private static void ReadExactly(Stream source, byte[] buffer, int count)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = source.Read(buffer, offset, count - offset);
            if (read == 0)
            {
                throw new EndOfStreamException("DAP message body is truncated.");
            }

            offset += read;
        }
    }

    private static NamedPipeClientStream ConnectPipe(string pipeName)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                var client = new NamedPipeClientStream(
                    ".",
                    pipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                client.Connect(2_000);
                return client;
            }
            catch (Exception exception) when (exception is IOException or
                TimeoutException or UnauthorizedAccessException)
            {
                last = exception;
                Thread.Sleep(100);
            }
        }

        throw new IOException(
            $"Could not connect to the Lunil host debug pipe '{pipeName}': {last?.Message}",
            last);
    }
}
