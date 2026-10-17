using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Kl.Cli;

public sealed record TurnOutcome(string Status, string Text);

/// <summary>
/// Owns one core session and serializes turn events. Events are delivered to a
/// presenter so the terminal UI never has to parse JSON-RPC notifications.
/// </summary>
public sealed class KlSessionClient : IAsyncDisposable
{
    private readonly KlCoreClient _client;
    private readonly CliOptions _options;
    private readonly ISessionPresenter _presenter;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _eventLoop;
    private readonly object _turnCancellationLock = new();
    private TaskCompletionSource<TurnOutcome>? _turnCompletion;
    private CancellationTokenSource? _turnCancellation;
    private string? _currentTurnId;
    private bool _cancelRequested;

    private KlSessionClient(
        KlCoreClient client,
        CliOptions options,
        ISessionPresenter presenter,
        JsonElement session)
    {
        _client = client;
        _options = options;
        _presenter = presenter;
        SessionId = session.GetProperty("session_id").GetString() ?? string.Empty;
        Workspace = session.GetProperty("workspace").GetString() ?? string.Empty;
        Provider = session.GetProperty("provider").GetString() ?? string.Empty;
        Model = session.GetProperty("model").GetString() ?? string.Empty;
        MessageCount = session.TryGetProperty("message_count", out var count)
            ? count.GetInt32()
            : 0;
        _eventLoop = Task.Run(() => ConsumeEventsAsync(_shutdown.Token));
    }

    public string SessionId { get; }
    public string Workspace { get; }
    public string Provider { get; }
    public string Model { get; }
    public int MessageCount { get; }

    public static async Task<KlSessionClient> StartAsync(
        CliOptions options,
        ISessionPresenter presenter,
        CancellationToken cancellationToken = default)
    {
        var client = KlCoreClient.Start(options);
        try
        {
            await client.HandshakeAsync(cancellationToken).ConfigureAwait(false);
            JsonElement session;
            if (!string.IsNullOrWhiteSpace(options.Session))
            {
                session = await client.ResumeSessionAsync(options.Session, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                var workspace = Path.GetFullPath(
                    options.Workspace ?? Directory.GetCurrentDirectory());
                session = await client.CreateSessionAsync(
                    workspace,
                    options.Provider,
                    options.Model,
                    cancellationToken).ConfigureAwait(false);
            }

            return new KlSessionClient(client, options, presenter, session);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<JsonElement> ListSessionsAsync(CancellationToken cancellationToken = default) =>
        _client.ListSessionsAsync(cancellationToken);

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
            var start = await _client.StartTurnAsync(
                SessionId,
                prompt,
                _options.Provider,
                _options.Model,
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
            case "audit.recorded" when _options.Verbose:
                await _presenter.OnEventAsync(
                    new SessionEvent(
                        SessionEventKind.AuditRecorded,
                        Text: GetString(data, "record_id")),
                    cancellationToken).ConfigureAwait(false);
                break;
            case "turn.completed":
                var status = GetString(data, "status");
                var text = GetString(data, "text");
                await _presenter.OnEventAsync(
                    new SessionEvent(
                        SessionEventKind.TurnCompleted,
                        Text: text,
                        Status: status),
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

        bool allow;
        try
        {
            allow = await _presenter.RequestApprovalAsync(request, approvalCancellation)
                .ConfigureAwait(false);
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
            await _client.RespondApprovalAsync(
                SessionId,
                approvalId,
                allow,
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
