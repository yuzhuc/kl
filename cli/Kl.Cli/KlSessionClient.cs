using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Kl.Cli;

public sealed record TurnOutcome(string Status, string Text);

/// <summary>
/// Owns one core session and serializes turn events. Events are consumed from
/// a channel so approval prompts can await a JSON-RPC response without
/// blocking the connection's stdout reader.
/// </summary>
public sealed class KlSessionClient : IAsyncDisposable
{
    private readonly KlCoreClient _client;
    private readonly CliOptions _options;
    private readonly ConsoleUi _ui;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _eventLoop;
    private TaskCompletionSource<TurnOutcome>? _turnCompletion;
    private string? _currentTurnId;
    private bool _cancelRequested;

    private KlSessionClient(
        KlCoreClient client,
        CliOptions options,
        ConsoleUi ui,
        JsonElement session)
    {
        _client = client;
        _options = options;
        _ui = ui;
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
        ConsoleUi ui,
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

            return new KlSessionClient(client, options, ui, session);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<TurnOutcome> RunTurnAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<TurnOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
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
            _currentTurnId = null;
            _turnCompletion = null;
        }
    }

    public async Task CancelAsync(CancellationToken cancellationToken = default)
    {
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
            _ui.Error($"cancel failed: {error.Message}");
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
                catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
                {
                    _ui.Error($"bad core event: {error.Message}");
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

    private async Task HandleEventAsync(JsonElement message, CancellationToken cancellationToken)
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
                _ui.Line($"\n[turn] {GetString(data, "turn_id")}");
                break;
            case "turn.delta":
                _ui.Delta(Sanitize(GetString(data, "text")));
                break;
            case "turn.reasoning" when _options.Verbose:
                _ui.Line($"\n[reasoning] {Sanitize(GetString(data, "text"))}");
                break;
            case "tool.started":
                _ui.Line(
                    $"[tool] {GetString(data, "name")} {Truncate(GetRaw(data, "args"), 240)}");
                break;
            case "tool.output":
                var chunk = Sanitize(GetString(data, "chunk"));
                if (chunk.Length > 0)
                {
                    _ui.Delta(chunk.EndsWith('\n') ? chunk : chunk + "\n");
                }
                break;
            case "tool.approval.requested":
                await HandleApprovalAsync(data, cancellationToken).ConfigureAwait(false);
                break;
            case "tool.approval.resolved":
                _ui.Line($"[approval] {GetString(data, "decision")}");
                break;
            case "tool.completed":
                var ok = data.TryGetProperty("ok", out var okElement) && okElement.GetBoolean();
                _ui.Line(
                    $"[{(ok ? "ok" : "fail")}] {GetString(data, "name")}: {Sanitize(GetString(data, "summary"))}");
                if (data.TryGetProperty("diff", out var diff)
                    && diff.ValueKind == JsonValueKind.String)
                {
                    var text = Sanitize(diff.GetString() ?? string.Empty);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        _ui.Diff(text);
                    }
                }
                break;
            case "audit.recorded" when _options.Verbose:
                _ui.Line($"[audit] {GetString(data, "record_id")}");
                break;
            case "turn.completed":
                _ui.Line();
                _turnCompletion?.TrySetResult(new TurnOutcome(
                    GetString(data, "status"),
                    GetString(data, "text")));
                break;
            case "turn.failed":
                _ui.Line();
                _turnCompletion?.TrySetResult(new TurnOutcome(
                    "failed",
                    GetString(data, "message")));
                break;
        }
    }

    private async Task HandleApprovalAsync(JsonElement data, CancellationToken cancellationToken)
    {
        var approvalId = GetString(data, "approval_id");
        var name = Sanitize(GetString(data, "name"));
        var action = Sanitize(GetString(data, "action"));
        var resource = Sanitize(GetString(data, "resource"));
        var reason = Sanitize(GetString(data, "reason"));
        var args = Truncate(Sanitize(GetRaw(data, "args")), 800);

        _ui.Line();
        _ui.Line($"[approval] {name} ({action}) {resource}");
        _ui.Line($"  reason: {reason}");
        _ui.Line($"  args: {args}");

        var allow = _options.AutoApprove;
        if (!allow)
        {
            if (Console.IsInputRedirected)
            {
                _ui.Error("stdin is not interactive; denying approval (use --yes to allow)");
            }
            else
            {
                _ui.Line("Approve? [y/N] ");
                var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
                allow = answer is "y" or "yes";
            }
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
            _ui.Error($"approval response failed: {error.Message}");
        }
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string GetRaw(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetRawText() : "{}";

    private static string Truncate(string text, int limit) =>
        text.Length <= limit ? text : text[..limit] + "...";

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
