using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Kl.Client;

public sealed record TurnOutcome(string Status, string Text);

/// <summary>
/// Owns one core session and serializes turn events. Events are delivered to a
/// presenter so clients never have to parse JSON-RPC notifications.
/// </summary>
public sealed class KlSessionClient : IAsyncDisposable
{
    private const int SessionBusyCode = -32008;

    private readonly KlClientOptions _clientOptions;
    private readonly KlSessionOptions _sessionOptions;
    private readonly ISessionPresenter _presenter;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly object _turnCancellationLock = new();
    private readonly object _sequenceLock = new();
    private readonly object _clientLock = new();
    private readonly Dictionary<string, long> _sequenceBySession = new();
    private readonly SemaphoreSlim _reconnectGate = new(1, 1);
    private readonly Task _eventLoop;
    private KlCoreClient? _client;
    private TaskCompletionSource<TurnOutcome>? _turnCompletion;
    private CancellationTokenSource? _turnCancellation;
    private CancellationTokenSource? _pumpCancellation;
    private Task? _pumpTask;
    private string? _currentTurnId;
    private bool _cancelRequested;
    private readonly bool _serverMode;

    private KlSessionClient(
        KlCoreClient client,
        KlClientOptions clientOptions,
        KlSessionOptions sessionOptions,
        ISessionPresenter presenter,
        JsonElement session,
        bool serverMode)
    {
        _client = client;
        _clientOptions = clientOptions;
        _sessionOptions = sessionOptions;
        _presenter = presenter;
        _serverMode = serverMode;
        SessionId = session.GetProperty("session_id").GetString() ?? string.Empty;
        Workspace = session.GetProperty("workspace").GetString() ?? string.Empty;
        Provider = session.GetProperty("provider").GetString() ?? string.Empty;
        Model = session.GetProperty("model").GetString() ?? string.Empty;
        ReasoningEffort = session.TryGetProperty("reasoning_effort", out var effort)
            ? effort.GetString() ?? string.Empty
            : string.Empty;
        MessageCount = session.TryGetProperty("message_count", out var count)
            ? count.GetInt32()
            : 0;
        _eventLoop = Task.Run(() => ConsumeEventsAsync(_shutdown.Token));
    }

    public string SessionId { get; }
    public string Workspace { get; }
    public string Provider { get; }
    public string Model { get; }
    public string ReasoningEffort { get; }
    public int MessageCount { get; }

    /// <summary>Highest event sequence consumed for this session.</summary>
    public long LastSeq
    {
        get
        {
            lock (_sequenceLock)
            {
                return _sequenceBySession.TryGetValue(SessionId, out var seq) ? seq : 0;
            }
        }
    }

    public void SetAgent(string? agent) => _sessionOptions.Agent = agent;

    public static async Task<KlSessionClient> StartAsync(
        KlClientOptions clientOptions,
        KlSessionOptions sessionOptions,
        ISessionPresenter presenter,
        CancellationToken cancellationToken = default,
        Action<string>? progress = null)
    {
        var endpoint = KlCoreClient.ResolveEndpoint(clientOptions);
        var serverMode = endpoint is not null;
        KlCoreClient client;
        if (serverMode)
        {
            progress?.Invoke("connecting");
            client = await KlCoreClient.ConnectAsync(clientOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            client = KlCoreClient.Start(clientOptions);
            progress?.Invoke("handshaking");
            await client.HandshakeAsync(cancellationToken).ConfigureAwait(false);
        }

        KlSessionClient? session = null;
        try
        {
            progress?.Invoke("opening-session");
            JsonElement info;
            if (!string.IsNullOrWhiteSpace(sessionOptions.SessionId))
            {
                info = await client.ResumeSessionAsync(sessionOptions.SessionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                var workspace = Path.GetFullPath(
                    sessionOptions.Workspace ?? Directory.GetCurrentDirectory());
                info = await client.CreateSessionAsync(
                    workspace,
                    sessionOptions.Provider,
                    sessionOptions.Model,
                    cancellationToken).ConfigureAwait(false);
            }

            session = new KlSessionClient(
                client,
                clientOptions,
                sessionOptions,
                presenter,
                info,
                serverMode);

            if (serverMode)
            {
                // The server only forwards events for attached sessions and only
                // accepts turn traffic from the lease holder.
                var attach = await client.AttachSessionAsync(
                    session.SessionId,
                    null,
                    cancellationToken).ConfigureAwait(false);
                await client.LeaseSessionAsync(session.SessionId, null, cancellationToken)
                    .ConfigureAwait(false);
                session.EnqueueReplay(attach);
            }

            session.StartPump(client);
            progress?.Invoke("session-ready");
            return session;
        }
        catch
        {
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
    }

    /// <summary>
    /// Reconnects to the local server, re-attaches the current session, replays
    /// everything after <see cref="LastSeq"/> and takes the write lease again.
    /// A failed reconnect keeps the session id so the caller can retry.
    /// </summary>
    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        if (!_serverMode)
        {
            throw new NotSupportedException("reconnect requires the local server transport");
        }

        await _reconnectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            KlCoreClient? previous;
            lock (_clientLock)
            {
                previous = _client;
                _client = null;
            }
            await StopPumpAsync().ConfigureAwait(false);
            if (previous is not null)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }

            // A turn that ran on the dropped connection cannot be resumed.
            _turnCompletion?.TrySetResult(
                new TurnOutcome("failed", "connection replaced by reconnect"));

            KlCoreClient next;
            try
            {
                next = await KlCoreClient.ConnectAsync(_clientOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"reconnect failed for session {SessionId}: {error.Message}",
                    error);
            }

            try
            {
                var attach = await next.AttachSessionAsync(
                    SessionId,
                    LastSeq,
                    cancellationToken).ConfigureAwait(false);
                await LeaseAsync(next, cancellationToken).ConfigureAwait(false);
                lock (_clientLock)
                {
                    _client = next;
                }
                EnqueueReplay(attach);
                StartPump(next);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                await next.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"reconnect failed for session {SessionId}: {error.Message}",
                    error);
            }
        }
        finally
        {
            _reconnectGate.Release();
        }
    }

    public Task<JsonElement> HistoryAsync(
        int limit = 20,
        CancellationToken cancellationToken = default) =>
        Client.SessionHistoryAsync(SessionId, limit, cancellationToken);

    public Task<JsonElement> ListSessionsAsync(CancellationToken cancellationToken = default) =>
        Client.ListSessionsAsync(cancellationToken);

    public async Task<string> ForkAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await Client.ForkSessionAsync(SessionId, cancellationToken)
            .ConfigureAwait(false);
        return result.TryGetProperty("session_id", out var id)
            && id.ValueKind == JsonValueKind.String
            ? id.GetString() ?? string.Empty
            : throw new JsonRpcException(-32603, "session.fork returned no session_id");
    }

    public Task<JsonElement> RenameSessionAsync(
        string sessionId,
        string name,
        CancellationToken cancellationToken = default) =>
        Client.RenameSessionAsync(sessionId, name, cancellationToken);

    public Task<JsonElement> DeleteSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        Client.DeleteSessionAsync(sessionId, cancellationToken);

    public async Task<TurnOutcome> RunTurnAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var client = Client;
        var completion = new TaskCompletionSource<TurnOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var turnCancellation = new CancellationTokenSource();
        lock (_turnCancellationLock)
        {
            _turnCancellation = turnCancellation;
        }
        _turnCompletion = completion;
        try
        {
            if (_serverMode)
            {
                await client.LeaseSessionAsync(SessionId, null, cancellationToken)
                    .ConfigureAwait(false);
            }
            var start = await client.StartTurnAsync(
                SessionId,
                prompt,
                _sessionOptions.Provider,
                _sessionOptions.Model,
                _sessionOptions.Quality,
                _sessionOptions.Agent,
                cancellationToken).ConfigureAwait(false);
            _currentTurnId = start.GetProperty("turn_id").GetString();
            if (_cancelRequested)
            {
                _cancelRequested = false;
                await CancelAsync(cancellationToken).ConfigureAwait(false);
            }
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_turnCancellationLock)
            {
                if (ReferenceEquals(_turnCancellation, turnCancellation))
                {
                    _turnCancellation = null;
                }
            }
            turnCancellation.Dispose();
            _currentTurnId = null;
            _turnCompletion = null;
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? turnCancellation;
        lock (_turnCancellationLock)
        {
            turnCancellation = _turnCancellation;
        }
        turnCancellation?.Cancel();

        var turnId = _currentTurnId;
        if (turnId is null)
        {
            _cancelRequested = true;
            return;
        }

        try
        {
            var client = Client;
            if (_serverMode)
            {
                await client.LeaseSessionAsync(SessionId, null, cancellationToken)
                    .ConfigureAwait(false);
            }
            await client.CancelTurnAsync(SessionId, turnId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error)
            when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            await _presenter.OnEventAsync(
                new SessionEvent(
                    SessionEventKind.Error,
                    Text: $"cancel failed: {error.Message}"),
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private KlCoreClient Client
    {
        get
        {
            lock (_clientLock)
            {
                return _client
                    ?? throw new InvalidOperationException(
                        $"session {SessionId} is disconnected; call ReconnectAsync to restore it");
            }
        }
    }

    /// <summary>
    /// Takes the write lease, retrying while the dropped connection still owns
    /// it. Windows named pipes release the lease only after the pipe drains.
    /// </summary>
    private async Task LeaseAsync(
        KlCoreClient client,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await client.LeaseSessionAsync(SessionId, null, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
            catch (JsonRpcException error)
                when (error.Code == SessionBusyCode && attempt < 40)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void StartPump(KlCoreClient client)
    {
        var cancellation = new CancellationTokenSource();
        _pumpCancellation = cancellation;
        _pumpTask = Task.Run(() => PumpAsync(client, cancellation.Token));
    }

    private async Task StopPumpAsync()
    {
        var cancellation = _pumpCancellation;
        var task = _pumpTask;
        _pumpCancellation = null;
        _pumpTask = null;
        if (cancellation is null || task is null)
        {
            return;
        }

        await cancellation.CancelAsync().ConfigureAwait(false);
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or ChannelClosedException)
        {
            // The connection is going away.
        }
        cancellation.Dispose();
    }

    private async Task PumpAsync(KlCoreClient client, CancellationToken cancellationToken)
    {
        try
 {
            await foreach (var message in client.Notifications.ReadAllAsync(cancellationToken))
            {
                if (message.TryGetProperty("params", out var parameters))
                {
                    _events.Writer.TryWrite(parameters);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Replaced by a newer connection.
        }
        catch (ChannelClosedException)
        {
            // Core exited.
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                // The connection dropped on its own. A running turn cannot
                // finish, so it fails instead of hanging.
                lock (_clientLock)
                {
                    if (ReferenceEquals(_client, client))
                    {
                        _client = null;
                    }
                }
                _turnCompletion?.TrySetResult(
                    new TurnOutcome("failed", "core connection closed"));
            }
        }
    }

    private async Task ConsumeEventsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var parameters in _events.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await HandleEventAsync(parameters, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error)
                    when (error is JsonException or InvalidOperationException or KeyNotFoundException)
                {
                    await _presenter.OnEventAsync(
                        new SessionEvent(
                            SessionEventKind.Error,
                            Text: $"bad core event: {error.Message}"),
                        cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (ChannelClosedException)
        {
            // Core exited.
        }
        finally
        {
            _turnCompletion?.TrySetResult(new TurnOutcome("failed", "core connection closed"));
        }
    }

    /// <summary>
    /// Handles one core event. <paramref name="parameters"/> is the
    /// <c>{session_id, seq, event}</c> payload the core uses for both live
    /// notifications and attach replays.
    /// </summary>
    private async Task HandleEventAsync(
        JsonElement parameters,
        CancellationToken cancellationToken)
    {
        if (!parameters.TryGetProperty("event", out var envelope)
            || !envelope.TryGetProperty("type", out var typeElement))
        {
            return;
        }

        var sessionId = GetString(parameters, "session_id");
        var seq = GetLong(parameters, "seq");
        if (seq > 0 && !TryAdvanceSequence(sessionId, seq))
        {
            // Duplicate replay or a stale out-of-order event.
            return;
        }

        var data = envelope.TryGetProperty("data", out var payload) ? payload : default;
        switch (typeElement.GetString())
        {
            case "turn.started":
                await _presenter.OnEventAsync(
                    new SessionEvent(
                        SessionEventKind.TurnStarted,
                        TurnId: GetString(data, "turn_id")),
                    cancellationToken).ConfigureAwait(false);
                break;
            case "turn.delta":
                await _presenter.OnEventAsync(
                    new SessionEvent(
                        SessionEventKind.AssistantDelta,
                        Text: Sanitize(GetString(data, "text"))),
                    cancellationToken).ConfigureAwait(false);
                break;
            case "turn.reasoning":
                await _presenter.OnEventAsync(
                    new SessionEvent(
                        SessionEventKind.ReasoningDelta,
                        Text: Sanitize(GetString(data, "text"))),
                    cancellationToken).ConfigureAwait(false);
                break;
            case "tool.started":
                await _presenter.OnEventAsync(
                    new SessionEvent(
                        SessionEventKind.ToolStarted,
                        Name: GetString(data, "name"),
                        Args: Sanitize(GetRaw(data, "args"))),
                    cancellationToken).ConfigureAwait(false);
                break;
            case "tool.output":
                await _presenter.OnEventAsync(
                    new SessionEvent(
                        SessionEventKind.ToolOutput,
                        Text: Sanitize(GetString(data, "chunk"))),
                    cancellationToken).ConfigureAwait(false);
                break;
            case "tool.approval.requested":
                await HandleApprovalAsync(data, cancellationToken).ConfigureAwait(false);
                break;
            case "tool.approval.resolved":
                await _presenter.OnEventAsync(
                    new SessionEvent(
                        SessionEventKind.ApprovalResolved,
                        Decision: GetString(data, "decision")),
                    cancellationToken).ConfigureAwait(false);
                break;
            case "tool.completed":
                await _presenter.OnEventAsync(
                    new SessionEvent(
                        SessionEventKind.ToolCompleted,
                        Name: GetString(data, "name"),
                        Summary: Sanitize(GetString(data, "summary")),
                        Diff: data.TryGetProperty("diff", out var diff)
                            && diff.ValueKind == JsonValueKind.String
                            ? Sanitize(diff.GetString() ?? string.Empty)
                            : null,
                        Ok: !data.TryGetProperty("ok", out var okElement)
                            || okElement.GetBoolean()),
                    cancellationToken).ConfigureAwait(false);
                break;
            case "audit.recorded" when _sessionOptions.Verbose:
                await _presenter.OnEventAsync(
                    new SessionEvent(
                        SessionEventKind.AuditRecorded,
                        Text: GetString(data, "record_id")),
                    cancellationToken).ConfigureAwait(false);
                break;
            case "turn.completed":
                var status = GetString(data, "status");
                var text = GetString(data, "text");
                var inputTokens = GetLong(data, "input_tokens");
                var outputTokens = GetLong(data, "output_tokens");
                double? costUsd = data.TryGetProperty("cost_usd", out var cost)
                    && cost.ValueKind == JsonValueKind.Number
                    && cost.TryGetDouble(out var parsedCost)
                        ? parsedCost
                        : null;
                await _presenter.OnEventAsync(
                    new SessionEvent(
                        SessionEventKind.TurnCompleted,
                        Text: text,
                        Status: status,
                        InputTokens: inputTokens,
                        OutputTokens: outputTokens,
                        CostUsd: costUsd),
                    cancellationToken).ConfigureAwait(false);
                _turnCompletion?.TrySetResult(new TurnOutcome(status, text));
                break;
            case "turn.failed":
                var failureText = GetString(data, "message");
                await _presenter.OnEventAsync(
                    new SessionEvent(
                        SessionEventKind.TurnFailed,
                        Text: failureText),
                    cancellationToken).ConfigureAwait(false);
                _turnCompletion?.TrySetResult(new TurnOutcome("failed", failureText));
                break;
        }
    }

    private async Task HandleApprovalAsync(
        JsonElement data,
        CancellationToken cancellationToken)
    {
        var approvalId = GetString(data, "approval_id");
        var request = new ApprovalRequestEvent(
            approvalId,
            Sanitize(GetString(data, "name")),
            Sanitize(GetString(data, "action")),
            Sanitize(GetString(data, "resource")),
            Sanitize(GetString(data, "reason")),
            Sanitize(GetRaw(data, "args")));

        CancellationToken approvalCancellation;
        lock (_turnCancellationLock)
        {
            approvalCancellation = _turnCancellation?.Token ?? CancellationToken.None;
        }

        string decision;
        try
        {
            decision = _presenter is ISessionApprovalDecisionPresenter decisionPresenter
                ? await decisionPresenter.RequestApprovalDecisionAsync(request, approvalCancellation)
                    .ConfigureAwait(false)
                : await _presenter.RequestApprovalAsync(request, approvalCancellation)
                    .ConfigureAwait(false) ? "allow" : "deny";
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (approvalCancellation.IsCancellationRequested)
        {
            return;
        }

        try
        {
            var client = Client;
            if (_serverMode)
            {
                await client.LeaseSessionAsync(SessionId, null, cancellationToken)
                    .ConfigureAwait(false);
            }
            await client.RespondApprovalAsync(
                SessionId,
                approvalId,
                decision,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
            when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            await _presenter.OnEventAsync(
                new SessionEvent(
                    SessionEventKind.Error,
                    Text: $"approval response failed: {error.Message}"),
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Accepts an event only when its sequence is newer than the last one seen
    /// for that session, so replays and duplicates are shown once and stale
    /// out-of-order events are dropped.
    /// </summary>
    private bool TryAdvanceSequence(string sessionId, long seq)
    {
        lock (_sequenceLock)
        {
            if (sessionId.Length > 0
                && _sequenceBySession.TryGetValue(sessionId, out var last)
                && seq <= last)
            {
                return false;
            }
            if (sessionId.Length > 0)
            {
                _sequenceBySession[sessionId] = seq;
            }
            return true;
        }
    }

    /// <summary>
    /// Queues the events the server replayed for this session. They travel
    /// through the same channel as live events so ordering and de-duplication
    /// behave identically.
    /// </summary>
    private void EnqueueReplay(JsonElement attach)
    {
        if (!attach.TryGetProperty("replay", out var replay)
            || replay.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var entry in replay.EnumerateArray())
        {
            _events.Writer.TryWrite(entry);
        }
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string GetRaw(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetRawText() : "{}";

    private static long GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var parsed)
            ? parsed
            : 0;

    private static string Sanitize(string text)
    {
        if (text.IndexOfAny(['\u001b', '\u0000', '\u0007']) < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (character is '\n' or '\r' or '\t'
                || (character >= ' ' && character != '\u007f'))
            {
                builder.Append(character);
            }
        }
        return builder.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        lock (_turnCancellationLock)
        {
            _turnCancellation?.Cancel();
        }
        _shutdown.Cancel();
        await StopPumpAsync().ConfigureAwait(false);
        try
        {
            await _eventLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        _events.Writer.TryComplete();
        _shutdown.Dispose();
        KlCoreClient? client;
        lock (_clientLock)
        {
            client = _client;
            _client = null;
        }
        if (client is not null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }
}
