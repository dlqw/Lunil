using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lunil.DebugAdapter;

/// <summary>DAP message kinds over the JSON-RPC-style framing shared with LSP.</summary>
internal enum DapMessageKind
{
    Request,
    Response,
    Event,
}

/// <summary>A decoded Debug Adapter Protocol message.</summary>
internal sealed record DapMessage(DapMessageKind Kind, string? Method, JsonNode? Body, int? Id)
{
    public static DapMessage Request(JsonNode body, int id) => new(
        DapMessageKind.Request,
        (string?)body["command"],
        body["arguments"],
        id);

    public static DapMessage Response(int id, JsonNode body) => new(
        DapMessageKind.Response,
        null,
        body,
        id);

    public static DapMessage Event(string name, JsonNode? body = null) => new(
        DapMessageKind.Event,
        name,
        body,
        null);
}

/// <summary>
/// Reads and writes DAP messages over a stream using the Content-Length framed JSON transport
/// shared with LSP. A single connection is active at a time.
/// </summary>
internal sealed class DapConnection : IDisposable
{
    private const int MaxMessageBytes = 64 * 1024 * 1024;
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly object _writeLock = new();
    private int _sequence;
    private readonly byte[] _headerBuffer = new byte[64];
    private readonly MemoryStream _bodyBuffer = new();
    private bool _disposed;

    public DapConnection(Stream input, Stream output)
    {
        _input = input;
        _output = output;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _input.Dispose();
            _output.Dispose();
        }
    }

    /// <summary>Reads the next message; returns null at end of stream.</summary>
    public DapMessage? ReadMessage()
    {
        var length = ReadFrameLength();
        if (length is null)
        {
            return null;
        }

        _bodyBuffer.SetLength(0);
        var buffer = new byte[length.Value];
        ReadExactly(buffer, length.Value);
        var body = JsonNode.Parse(buffer);
        if (body is null)
        {
            throw new InvalidDataException("DAP message body is not valid JSON.");
        }

        if (body["command"] is not null && body["seq"] is JsonNode sequence)
        {
            var id = sequence.GetValue<int>();
            return DapMessage.Request(body, id);
        }

        if (body["type"]?.GetValue<string>() == "response" && body["request_seq"] is JsonNode requestSequence)
        {
            return DapMessage.Response(requestSequence.GetValue<int>(), body);
        }

        if (body["type"]?.GetValue<string>() == "event" && body["event"] is JsonNode eventName)
        {
            return DapMessage.Event(
                eventName.GetValue<string>(),
                body["body"] as JsonObject);
        }

        throw new InvalidDataException($"Unsupported DAP message: {body}");
    }

    public void WriteMessage(DapMessage message)
    {
        lock (_writeLock)
        {
            WriteMessageCore(message);
        }
    }

    private void WriteMessageCore(DapMessage message)
    {
        var body = new JsonObject
        {
            ["seq"] = ++_sequence,
        };
        switch (message.Kind)
        {
            case DapMessageKind.Response:
                body["type"] = "response";
                body["request_seq"] = message.Id;
                if (message.Body is not null)
                {
                    foreach (var property in message.Body.AsObject())
                    {
                        body[property.Key] = property.Value?.DeepClone();
                    }
                }

                break;
            case DapMessageKind.Event:
                body["type"] = "event";
                body["event"] = message.Method;
                if (message.Body is not null)
                {
                    body["body"] = message.Body.DeepClone();
                }

                break;
            case DapMessageKind.Request:
                body["type"] = "request";
                body["command"] = message.Method;
                if (message.Body is not null)
                {
                    body["arguments"] = message.Body.DeepClone();
                }

                break;
            default:
                throw new InvalidOperationException($"Unsupported DAP message kind: {message.Kind}");
        }

        var json = body.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n");
        _output.Write(header);
        _output.Write(bytes);
        _output.Flush();
    }

    private int? ReadFrameLength()
    {
        var offset = 0;
        while (true)
        {
            var read = _input.Read(_headerBuffer, offset, 1);
            if (read == 0)
            {
                return null;
            }

            offset++;
            if (offset == _headerBuffer.Length)
            {
                throw new InvalidDataException("DAP header line is too long.");
            }

            if (offset >= 4 &&
                _headerBuffer[offset - 4] == (byte)'\r' &&
                _headerBuffer[offset - 3] == (byte)'\n' &&
                _headerBuffer[offset - 2] == (byte)'\r' &&
                _headerBuffer[offset - 1] == (byte)'\n')
            {
                var headerText = Encoding.ASCII.GetString(_headerBuffer, 0, offset - 4);
                foreach (var line in headerText.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                        int.TryParse(trimmed.AsSpan("Content-Length:".Length).Trim(), out var length))
                    {
                        if (length <= 0 || length > MaxMessageBytes)
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

    private void ReadExactly(byte[] buffer, int count)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = _input.Read(buffer, offset, count - offset);
            if (read == 0)
            {
                throw new EndOfStreamException("DAP message body is truncated.");
            }

            offset += read;
        }
    }
}
