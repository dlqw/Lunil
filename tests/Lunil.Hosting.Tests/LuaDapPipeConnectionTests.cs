using System.Text;
using Lunil.Hosting;

namespace Lunil.Hosting.Tests;

public sealed class LuaDapPipeConnectionTests
{
    [Fact]
    public void RequestIdComesFromTheIdFieldNotTheSequence()
    {
        // Third-party DAP clients are not required to keep 'id' and 'seq' equal; the
        // attach relay must correlate on 'id' (and 'request_seq' for responses).
        var framed = Frame("""{"seq":7,"type":"request","command":"attach","id":42,"arguments":{}}""");
        using var connection = new LuaDapPipeConnection(
            new MemoryStream(framed),
            new MemoryStream());

        var message = connection.ReadMessage();

        Assert.NotNull(message);
        Assert.Equal(42, message.Id);
        Assert.Equal("attach", message.Method);
    }

    [Fact]
    public void ResponsesCorrelateOnRequestSequence()
    {
        var framed = Frame(
            """{"seq":3,"type":"response","request_seq":42,"command":"attach","success":true}""");
        using var connection = new LuaDapPipeConnection(
            new MemoryStream(framed),
            new MemoryStream());

        var message = connection.ReadMessage();

        Assert.NotNull(message);
        Assert.Equal(42, message.Id);
    }

    private static byte[] Frame(string body)
    {
        var payload = Encoding.ASCII.GetBytes(body);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        return [.. header, .. payload];
    }
}
