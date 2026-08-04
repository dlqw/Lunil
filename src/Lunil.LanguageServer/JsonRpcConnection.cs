using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lunil.LanguageServer;

internal sealed record JsonRpcRequest(string Method, JsonElement Parameters, JsonElement? Id)
{
    public bool IsNotification => Id is null;
}

internal sealed class JsonRpcException : Exception
{
    public JsonRpcException(int code, string message, JsonNode? data = null)
        : base(message)
    {
        Code = code;
        DataNode = data;
    }

    public int Code { get; }

    public JsonNode? DataNode { get; }
}

internal sealed class JsonRpcConnection : IAsyncDisposable
{
    private const int MaximumHeaderBytes = 16 * 1024;
    private const int MaximumMessageBytes = 32 * 1024 * 1024;
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly TextWriter _errorOutput;
    private readonly object _errorGate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _requests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _outbound = new(StringComparer.Ordinal);
    private readonly ConcurrentBag<Task> _inflight = [];
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
    private long _nextOutboundId;

    public JsonRpcConnection(Stream input, Stream output, TextWriter? errorOutput = null)
    {
        _input = input;
        _output = output;
        _errorOutput = errorOutput ?? Console.Error;
    }

    public async Task RunAsync(
        Func<JsonRpcRequest, CancellationToken, Task<JsonNode?>> dispatcher,
        CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var payload = await ReadMessageAsync(cancellationToken).ConfigureAwait(false);
            if (payload is null)
            {
                break;
            }

            if (System.Environment.GetEnvironmentVariable("LUNIL_DEBUG_MSG") == "1")
            {
                using var dbg = JsonDocument.Parse(payload);
                var root = dbg.RootElement;
                var method = root.TryGetProperty("method", out var m) ? m.GetString() : "<response>";
                Console.Error.WriteLine($"DBG recv {method} at {DateTime.UtcNow:HH:mm:ss.fff}");
            }

            if (TryHandleResponse(payload))
            {
                continue;
            }

            JsonRpcRequest request;
            try
            {
                request = Parse(payload);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                await SendErrorAsync(null, -32700, exception.Message, null, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (request.Method == "$/cancelRequest")
            {
                CancelRequest(request.Parameters);
                continue;
            }

            if (request.IsNotification)
            {
                await DispatchNotificationAsync(dispatcher, request, cancellationToken).ConfigureAwait(false);
                continue;
            }

            var task = DispatchRequestAsync(dispatcher, request, cancellationToken);
            _inflight.Add(task);
        }

        await WaitForInflightAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WaitForInflightAsync(CancellationToken cancellationToken)
    {
        var inflight = _inflight.ToArray();
        if (inflight.Length == 0)
        {
            return;
        }

        // In-flight requests are linked to the server cancellation token, so exit cancels them.
        // A CPU-bound request may not observe cancellation promptly, so bound the wait rather
        // than hanging the shutdown path (the client force-kills the process after its stop
        // timeout and reports "Server process exited with code 1").
        try
        {
            await Task.WhenAll(inflight)
                .WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
        }
    }

    public Task SendNotificationAsync(
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken = default) =>
        SendPayloadAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("method", method);
            if (parameters is not null)
            {
                writer.WritePropertyName("params");
                parameters.WriteTo(writer, _json);
            }

            writer.WriteEndObject();
        }, cancellationToken);

    public async Task<JsonNode?> SendRequestAsync(
        string method,
        JsonNode? parameters,
        CancellationToken cancellationToken = default)
    {
        var id = "server-" + Interlocked.Increment(ref _nextOutboundId)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_outbound.TryAdd("s:" + id, completion))
        {
            throw new InvalidOperationException("Could not allocate an outbound JSON-RPC request id.");
        }

        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        try
        {
            await SendPayloadAsync(writer =>
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WriteString("id", id);
                writer.WriteString("method", method);
                if (parameters is not null)
                {
                    writer.WritePropertyName("params");
                    parameters.WriteTo(writer, _json);
                }

                writer.WriteEndObject();
            }, cancellationToken).ConfigureAwait(false);
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _outbound.TryRemove("s:" + id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var source in _requests.Values)
        {
            source.Cancel();
            source.Dispose();
        }

        foreach (var completion in _outbound.Values)
        {
            completion.TrySetCanceled();
        }

        _writeGate.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task DispatchRequestAsync(
        Func<JsonRpcRequest, CancellationToken, Task<JsonNode?>> dispatcher,
        JsonRpcRequest request,
        CancellationToken serverCancellationToken)
    {
        var key = GetIdKey(request.Id!.Value);
        using var source = CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken);
        if (!_requests.TryAdd(key, source))
        {
            await SendErrorAsync(request.Id, -32600, "Duplicate request id.", null, serverCancellationToken)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            var result = await dispatcher(request, source.Token).ConfigureAwait(false);
            await SendResultAsync(request.Id.Value, result, serverCancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)        {
            await SendErrorAsync(request.Id, -32800, "Request cancelled.", null, serverCancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonRpcException exception)
        {
            await SendErrorAsync(
                request.Id,
                exception.Code,
                exception.Message,
                exception.DataNode,
                serverCancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not AccessViolationException)
        {
            LogInternalError(request, exception);
            await SendErrorAsync(
                request.Id,
                -32603,
                "Internal error.",
                JsonValue.Create(exception.GetType().Name + ": " + exception.Message),
                serverCancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _requests.TryRemove(key, out _);
        }
    }

    private void LogInternalError(JsonRpcRequest request, Exception exception)
    {
        try
        {
            lock (_errorGate)
            {
                _errorOutput.WriteLine(
                    $"Lunil language server request failed: method={request.Method}, id={request.Id?.GetRawText()}");
                _errorOutput.WriteLine(exception.ToString());
                _errorOutput.Flush();
            }
        }
        catch (Exception loggingException) when (loggingException is IOException or ObjectDisposedException)
        {
            // Failure to write a local diagnostic must not replace the JSON-RPC response.
        }
    }

    private async Task DispatchNotificationAsync(
        Func<JsonRpcRequest, CancellationToken, Task<JsonNode?>> dispatcher,
        JsonRpcRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await dispatcher(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not AccessViolationException)
        {
            // JSON-RPC notifications intentionally have no response channel.
            if (exception is not JsonRpcException and not JsonException and not ArgumentException)
            {
                LogInternalError(request, exception);
            }
        }
    }

    private async Task<byte[]?> ReadMessageAsync(CancellationToken cancellationToken)
    {
        var header = new List<byte>();
        var single = new byte[1];
        while (header.Count < MaximumHeaderBytes)
        {
            var read = await _input.ReadAsync(single, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return header.Count == 0 ? null : throw new EndOfStreamException("Incomplete JSON-RPC header.");
            }

            header.Add(single[0]);
            var count = header.Count;
            if (count >= 4 && header[count - 4] == '\r' && header[count - 3] == '\n' &&
                header[count - 2] == '\r' && header[count - 1] == '\n')
            {
                break;
            }
        }

        if (header.Count >= MaximumHeaderBytes)
        {
            throw new InvalidDataException("JSON-RPC header exceeds its byte limit.");
        }

        var text = Encoding.ASCII.GetString([.. header]);
        var contentLength = -1;
        foreach (var line in text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(':');
            if (separator > 0 && line.AsSpan(0, separator).Equals("Content-Length", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line.AsSpan(separator + 1).Trim(), out var parsed))
            {
                contentLength = parsed;
            }
        }

        if (contentLength < 0 || contentLength > MaximumMessageBytes)
        {
            throw new InvalidDataException("JSON-RPC Content-Length is missing or outside the supported range.");
        }

        var payload = new byte[contentLength];
        await _input.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    private static JsonRpcRequest Parse(byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("jsonrpc", out var version) || version.GetString() != "2.0" ||
            !root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("Invalid JSON-RPC 2.0 message.");
        }

        var parameters = root.TryGetProperty("params", out var paramsElement)
            ? paramsElement.Clone()
            : default;
        JsonElement? id = root.TryGetProperty("id", out var idElement) && idElement.ValueKind != JsonValueKind.Null
            ? idElement.Clone()
            : null;
        return new JsonRpcRequest(methodElement.GetString()!, parameters, id);
    }

    private bool TryHandleResponse(byte[] payload)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("method", out _) ||
            !root.TryGetProperty("id", out var id))
        {
            return false;
        }

        if (!_outbound.TryRemove(GetIdKey(id), out var completion))
        {
            return true;
        }

        if (root.TryGetProperty("error", out var error))
        {
            var code = error.TryGetProperty("code", out var codeElement) ? codeElement.GetInt32() : -32603;
            var message = error.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString() ?? "JSON-RPC request failed."
                : "JSON-RPC request failed.";
            completion.TrySetException(new JsonRpcException(code, message));
        }
        else
        {
            completion.TrySetResult(root.TryGetProperty("result", out var result)
                ? JsonNode.Parse(result.GetRawText())
                : null);
        }

        return true;
    }

    private void CancelRequest(JsonElement parameters)
    {
        if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("id", out var id) &&
            _requests.TryGetValue(GetIdKey(id), out var source))
        {
            source.Cancel();
        }
    }

    private Task SendResultAsync(JsonElement id, JsonNode? result, CancellationToken cancellationToken) =>
        SendPayloadAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            id.WriteTo(writer);
            writer.WritePropertyName("result");
            if (result is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                result.WriteTo(writer, _json);
            }

            writer.WriteEndObject();
        }, cancellationToken);

    private Task SendErrorAsync(
        JsonElement? id,
        int code,
        string message,
        JsonNode? data,
        CancellationToken cancellationToken) =>
        SendPayloadAsync(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            if (id is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                id.Value.WriteTo(writer);
            }

            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteNumber("code", code);
            writer.WriteString("message", message);
            if (data is not null)
            {
                writer.WritePropertyName("data");
                data.WriteTo(writer, _json);
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }, cancellationToken);

    private async Task SendPayloadAsync(Action<Utf8JsonWriter> write, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        var header = Encoding.ASCII.GetBytes($"Content-Length: {buffer.Length}\r\n\r\n");
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;
            await buffer.CopyToAsync(_output, cancellationToken).ConfigureAwait(false);
            await _output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static string GetIdKey(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.String => "s:" + id.GetString(),
        JsonValueKind.Number => "n:" + id.GetRawText(),
        _ => throw new InvalidDataException("JSON-RPC id must be a string or number."),
    };
}
