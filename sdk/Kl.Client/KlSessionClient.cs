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
    private readonly KlCoreClient _client;
    private readonly KlSessionOptions _sessionOptions;
    private readonly ISessionPresenter _presenter;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _eventLoop;
    private readonly object _turnCancellationLock = new();
    private TaskCompletionSource<TurnOutcome>? _turnCompletion;
    private CancellationTokenSource? _turnCancellation;
    private string? _currentTurnId;
    private bool _cancelRequested;
    private readonly bool _serverMode;

    private KlSessionClient(
        KlCoreClient client,
        KlSessionOptions sessionOptions,
        ISessionPresenter presenter,
        JsonElement session,
        bool serverMode)
    {
        _client = client;
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

        try
        {
            progress?.Invoke("opening-session");
            JsonElement session;
            if (!string.IsNullOrWhiteSpace(sessionOptions.SessionId))
            {
                session = await client.ResumeSessionAsync(sessionOptions.SessionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                var workspace = Path.GetFullPath(
                    sessionOptions.Workspace ?? Directory.GetCurrentDirectory());
                session = await client.CreateSessionAsync(
                    workspace,
                    sessionOptions.Provider,
                    sessionOptions.Model,
                    cancellationToken).ConfigureAwait(false);
            }

            if (serverMode)
            {
                // The server only forwards events for attached sessions and only
                // accepts turn traffic from the lease holder.
                var sessionId = session.GetProperty("session_id").GetString() ?? string.Empty;
                await client.AttachSessionAsync(sessionId, null, cancellationToken)
                    .ConfigureAwait(false);
                await client.LeaseSessionAsync(sessionId, null, cancellationToken)
                    .ConfigureAwait(false);
            }

            var result = new KlSessionClient(client, sessionOptions, presenter, session, serverMode);
            progress?.Invoke("session-ready");
            return result;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<JsonElement> HistoryAsync(
        int limit = 20,
        CancellationToken cancellationToken = default) =>
        _client.SessionHistoryAsync(SessionId, limit, cancellationToken);

    public Task<JsonElement> ListSessionsAsync(CancellationToken cancellationToken = default) =>
        _client.ListSessionsAsync(cancellationToken);

    public async Task<string> ForkAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await _client.ForkSessionAsync(SessionId, cancellationToken)
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
        _client.RenameSessionAsync(sessionId, name, cancellationToken);

    public Task<JsonElement> DeleteSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default) =>
        _client.DeleteSessionAsync(sessionId, cancellationToken);

    public async Task<TurnOutcome> RunTurnAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
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
                await _client.LeaseSessionAsync(SessionId, null, cancellationToken)
                    .ConfigureAwait(false);
            }
            var start = await _client.StartTurnAsync(
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
            if (_serverMode)
            {
                await _client.LeaseSessionAsync(SessionId, null, cancellationToken)
                    .ConfigureAwait(false);
            }
            await _client.CancelTurnAsync(SessionId, turnId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonRpcException error)
        {
            await _presenter.OnEventAsync(
                new SessionEvent(
                    SessionEventKind.Error,
                    Text: $"cancel failed: {error.Message}"),
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task ConsumeEventsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var message in _client.Notifications.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await HandleEventAsync(message, cancellationToken).ConfigureAwait(false);
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

    private async Task HandleEventAsync(
        JsonElement message,
        CancellationToken cancellationToken)
    {
        if (!message.TryGetProperty("params", out var parameters)
            || !parameters.TryGetProperty("event", out var envelope)
            || !envelope.TryGetProperty("type", out var typeElement))
        {
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
            if (_serverMode)
            {
                await _client.LeaseSessionAsync(SessionId, null, cancellationToken)
                    .ConfigureAwait(false);
            }
            await _client.RespondApprovalAsync(
                SessionId,
                approvalId,
                decision,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonRpcException error)
        {
            await _presenter.OnEventAsync(
                new SessionEvent(
                    SessionEventKind.Error,
                    Text: $"approval response failed: {error.Message}"),
                cancellationToken).ConfigureAwait(false);
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
        try
        {
            await _eventLoop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        _shutdown.Dispose();
        await _client.DisposeAsync().ConfigureAwait(false);
    }
}
