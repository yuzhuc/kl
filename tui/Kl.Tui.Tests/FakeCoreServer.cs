using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Kl.Tui.Tests;

/// <summary>
/// Minimal JSON-RPC server that speaks the local core protocol over the same
/// transport the client uses, so attach replays and live events can be driven
/// deterministically.
/// </summary>
internal sealed class FakeCoreServer : IAsyncDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;
    private readonly List<StreamWriter> _writers = new();
    private readonly List<JsonElement> _attachRequests = new();
    private readonly Queue<JsonElement[]> _attachReplays = new();
    private readonly object _sync = new();
    private readonly string _endpoint;

    public FakeCoreServer(string sessionId = "session-1")
    {
        SessionId = sessionId;
        if (OperatingSystem.IsWindows())
        {
            _endpoint = "kl-fake-" + Guid.NewGuid().ToString("N");
        }
        else
        {
            _endpoint = Path.Combine(
                Path.GetTempPath(),
                "kl-fake-" + Guid.NewGuid().ToString("N") + ".sock");
        }
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public string SessionId { get; }

    /// <summary>Endpoint the client must be pointed at.</summary>
    public string Endpoint => _endpoint;

    /// <summary>After-sequence values requested by <c>session.attach</c>.</summary>
    public IReadOnlyList<long?> AttachAfterSeqs
    {
        get
        {
            lock (_sync)
            {
                return _attachRequests
                    .Select(request => request.TryGetProperty("after_seq", out var value)
                        && value.ValueKind == JsonValueKind.Number
                        && value.TryGetInt64(out var seq)
                            ? (long?)seq
                            : null)
                    .ToList();
            }
        }
    }

    /// <summary>Queues the replay returned by the next <c>session.attach</c>.</summary>
    public void QueueAttachReplay(params JsonElement[] replay)
    {
        lock (_sync)
        {
            _attachReplays.Enqueue(replay);
        }
    }

    /// <summary>Pushes one live event notification to every connected client.</summary>
    public void PushEvent(long seq, string type, object? data = null)
    {
        var payload = new
        {
            jsonrpc = "2.0",
            kl_protocol = 1,
            method = "event",
            @params = new
            {
                session_id = SessionId,
                seq,
                @event = new { type, data }
            }
        };
        var line = JsonSerializer.Serialize(payload);
        lock (_sync)
        {
            foreach (var writer in _writers)
            {
                writer.WriteLine(line);
                writer.Flush();
            }
        }
    }

    public static JsonElement Event(long seq, string type, object? data = null)
    {
        return JsonSerializer.SerializeToElement(new
        {
            session_id = "session-1",
            seq,
            @event = new { type, data }
        });
    }

    private async Task AcceptLoopAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var server = new System.IO.Pipes.NamedPipeServerStream(
                    _endpoint,
                    System.IO.Pipes.PipeDirection.InOut,
                    System.IO.Pipes.NamedPipeServerStream.MaxAllowedServerInstances,
                    System.IO.Pipes.PipeTransmissionMode.Byte,
                    System.IO.Pipes.PipeOptions.Asynchronous);
                try
                {
                    await server.WaitForConnectionAsync(_shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                    return;
                }
                _ = Task.Run(() => ServeAsync(server));
            }
            return;
        }

        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(_endpoint));
        listener.Listen(8);
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var socket = await listener.AcceptAsync(_shutdown.Token).ConfigureAwait(false);
                _ = Task.Run(() => ServeAsync(new NetworkStream(socket, ownsSocket: true)));
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            listener.Dispose();
        }
    }

    private async Task ServeAsync(Stream stream)
    {
        var writer = new StreamWriter(stream, new UTF8Encoding(false))
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        lock (_sync)
        {
            _writers.Add(writer);
        }
        try
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                var response = Handle(line);
                if (response is null)
                {
                    continue;
                }
                await writer.WriteLineAsync(response).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // The client went away.
        }
        finally
        {
            lock (_sync)
            {
                _writers.Remove(writer);
            }
        }
    }

    private string? Handle(string line)
    {
        using var document = JsonDocument.Parse(line);
        var request = document.RootElement;
        var id = request.GetProperty("id");
        var method = request.GetProperty("method").GetString() ?? string.Empty;
        request.TryGetProperty("params", out var parameters);

        object? result = method switch
        {
            "kl.handshake" => new
            {
                server_name = "kl-core",
                server_version = "0.1.0",
                protocol_version = 1,
                min_protocol_version = 1,
                features = Array.Empty<string>()
            },
            "session.create" or "session.resume" => new
            {
                session_id = SessionId,
                workspace = "/tmp/kl",
                provider = "scripted",
                model = "scripted",
                reasoning_effort = "medium",
                message_count = 0
            },
            "session.attach" => Attach(parameters),
            "session.lease" => new
            {
                session_id = SessionId,
                expires_at_unix_ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 300_000
            },
            "session.detach" => new { session_id = SessionId },
            _ => new { }
        };

        return JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            kl_protocol = 1,
            id,
            result
        });
    }

    private object Attach(JsonElement parameters)
    {
        JsonElement[] replay;
        lock (_sync)
        {
            _attachRequests.Add(parameters.Clone());
            replay = _attachReplays.Count > 0
                ? _attachReplays.Dequeue()
                : Array.Empty<JsonElement>();
        }
        var lastSeq = replay.Length == 0 ? 0 : replay[^1].GetProperty("seq").GetInt64();
        return new
        {
            session_id = SessionId,
            replay,
            last_seq = lastSeq,
            lease_held = false
        };
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        StreamWriter[] writers;
        lock (_sync)
        {
            writers = _writers.ToArray();
        }
        foreach (var writer in writers)
        {
            try
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                // The client may already have closed the stream.
            }
        }
        try
        {
            await _acceptLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.Delete(_endpoint);
            }
            catch (IOException)
            {
                // Best effort.
            }
        }
        _shutdown.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
