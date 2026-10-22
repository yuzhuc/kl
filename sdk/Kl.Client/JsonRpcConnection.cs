using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Kl.Client;

public sealed class JsonRpcException : Exception
{
    public JsonRpcException(long code, string message)
        : base(message)
    {
        Code = code;
    }

    public long Code { get; }
}

/// <summary>
/// Newline-delimited JSON-RPC connection over a duplex stream. The stream may
/// be a child process's stdio or a local socket.
/// Responses are matched to requests by id; `event` notifications are raised
/// on the calling thread's event loop.
/// </summary>
public sealed class JsonRpcConnection : IAsyncDisposable
{
    private readonly Process? _process;
    private readonly Stream _input;
    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Channel<JsonElement> _notifications = Channel.CreateUnbounded<JsonElement>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _readerTask;
    private readonly Task? _stderrTask;
    private readonly ConcurrentQueue<string> _stderrLines = new();
    private long _nextId;

    public JsonRpcConnection(Process process)
        : this(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, process)
    {
    }

    /// <summary>Creates a connection over an already connected duplex stream.</summary>
    public JsonRpcConnection(Stream input, Stream output, Process? process = null)
    {
        _process = process;
        _input = input;
        _writer = new StreamWriter(input, new UTF8Encoding(false))
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        _reader = new StreamReader(output, new UTF8Encoding(false));
        _readerTask = Task.Run(ReadLoopAsync);
        _stderrTask = process is null ? null : Task.Run(ReadStderrAsync);
    }

    public event Action<JsonElement>? NotificationReceived;
    public event Action<string>? Diagnostic;

    public ChannelReader<JsonElement> Notifications => _notifications.Reader;

    public async Task<JsonElement> RequestAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        var line = JsonLineCodec.SerializeRequest(id, method, parameters) + "\n";
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
            when (error is IOException or InvalidOperationException or ObjectDisposedException)
        {
            _pending.TryRemove(id, out _);
            await WaitForStderrAsync().ConfigureAwait(false);
            throw new IOException(ConnectionClosedMessage(), error);
        }
        finally
        {
            _writeLock.Release();
        }

        using var registration = cancellationToken.Register(
            () => completion.TrySetCanceled(cancellationToken));
        return await completion.Task.ConfigureAwait(false);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }


                if (!JsonLineCodec.TryParse(line, out var document) || document is null)
                {
                    Diagnostic?.Invoke($"core emitted invalid JSON: {line}");
                    continue;
                }

                using (document)
                {
                    Dispatch(document.RootElement.Clone());
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            Diagnostic?.Invoke($"core stdout closed: {error.Message}");
        }
        finally
        {
            await WaitForStderrAsync().ConfigureAwait(false);
            FailPending(new IOException(ConnectionClosedMessage()));
        }
    }

    private void Dispatch(JsonElement message)
    {
        if (message.TryGetProperty("kl_protocol", out var protocol)
            && protocol.ValueKind == JsonValueKind.Number
            && protocol.GetInt32() != Protocol.Version)
        {
            FailPending(new JsonRpcException(-32001, "core protocol version mismatch"));
            return;
        }

        if (message.TryGetProperty("method", out var method)
            && method.ValueKind == JsonValueKind.String
            && method.GetString() == Protocol.Event)
        {
            _notifications.Writer.TryWrite(message);
            NotificationReceived?.Invoke(message);
            return;
        }

        if (!message.TryGetProperty("id", out var idElement)
            || idElement.ValueKind != JsonValueKind.Number
            || !idElement.TryGetInt64(out var id)
            || !_pending.TryRemove(id, out var completion))
        {
            return;
        }

        if (message.TryGetProperty("error", out var error))
        {
            var code = error.TryGetProperty("code", out var codeElement)
                ? codeElement.GetInt64()
                : -32603;
            var text = error.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString() ?? "unknown core error"
                : "unknown core error";
            completion.TrySetException(new JsonRpcException(code, text));
            return;
        }

        if (message.TryGetProperty("result", out var result))
        {
            completion.TrySetResult(result.Clone());
            return;
        }

        completion.TrySetException(new InvalidOperationException("core response has no result or error"));
    }

    private async Task ReadStderrAsync()
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                _stderrLines.Enqueue(line);
                while (_stderrLines.Count > 20)
                {
                    _stderrLines.TryDequeue(out _);
                }
                Diagnostic?.Invoke(line);
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Diagnostic?.Invoke($"core stderr closed: {error.Message}");
        }
    }

    private async Task WaitForStderrAsync()
    {
        if (_stderrTask is null)
        {
            return;
        }

        try
        {
            await _stderrTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TimeoutException or IOException or ObjectDisposedException)
        {
            // Diagnostics are best-effort during shutdown.
        }
    }

    private string ConnectionClosedMessage()
    {
        var detail = string.Join(" | ", _stderrLines);
        if (detail.Length == 0)
        {
            return "kl-core connection closed";
        }

        const int limit = 2000;
        if (detail.Length > limit)
        {
            detail = detail[^limit..];
        }
        return $"kl-core connection closed: {detail}";
    }

    private void FailPending(Exception error)
    {
        foreach (var (_, completion) in _pending)
        {
            completion.TrySetException(error);
        }

        _pending.Clear();
        _notifications.Writer.TryComplete(error);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        try
        {
            _input.Close();
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException)
        {
            // The peer may already be gone.
        }

        if (_process is not null)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Process already exited.
                }
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or ObjectDisposedException)
            {
                // Shutdown races are expected.
            }
        }

        try
        {
            await _readerTask.ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Shutdown races are expected.
        }

        if (_stderrTask is not null)
        {
            try
            {
                await _stderrTask.ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // Shutdown races are expected.
            }
        }

        _notifications.Writer.TryComplete();
        DisposeQuietly(_reader);
        DisposeQuietly(_writer);
        _writeLock.Dispose();
        _shutdown.Dispose();
        if (_process is not null)
        {
            DisposeQuietly(_process);
        }
    }

    private static void DisposeQuietly(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Disposing a half-closed pipe is not actionable.
        }
    }
}
