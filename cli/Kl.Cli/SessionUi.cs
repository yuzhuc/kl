using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Kl.Cli;

public enum SessionEventKind
{
    TurnStarted,
    AssistantDelta,
    ReasoningDelta,
    ToolStarted,
    ToolOutput,
    ApprovalRequested,
    ApprovalResolved,
    ToolCompleted,
    AuditRecorded,
    TurnCompleted,
    TurnFailed,
    Error
}

public sealed record SessionEvent(
    SessionEventKind Kind,
    string Text = "",
    string Name = "",
    string Args = "",
    string Summary = "",
    string? Diff = null,
    bool Ok = true,
    string? ApprovalId = null,
    string? Action = null,
    string? Resource = null,
    string? Reason = null,
    string? Decision = null,
    string? Status = null,
    string? TurnId = null);

public sealed record ApprovalRequestEvent(
    string ApprovalId,
    string Name,
    string Action,
    string Resource,
    string Reason,
    string Args);

public interface ISessionPresenter
{
    ValueTask OnEventAsync(SessionEvent sessionEvent, CancellationToken cancellationToken);

    ValueTask<bool> RequestApprovalAsync(
        ApprovalRequestEvent request,
        CancellationToken cancellationToken);
}

public sealed class PlainTextSessionPresenter : ISessionPresenter
{
    private readonly CliOptions _options;
    private readonly ConsoleUi _ui;

    public PlainTextSessionPresenter(CliOptions options, ConsoleUi ui)
    {
        _options = options;
        _ui = ui;
    }

    public ValueTask OnEventAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
    {
        switch (sessionEvent.Kind)
        {
            case SessionEventKind.TurnStarted:
                _ui.Line($"\n[turn] {sessionEvent.TurnId}");
                break;
            case SessionEventKind.AssistantDelta:
                _ui.Delta(sessionEvent.Text);
                break;
            case SessionEventKind.ReasoningDelta when _options.Verbose:
                _ui.Line($"\n[reasoning] {sessionEvent.Text}");
                break;
            case SessionEventKind.ToolStarted:
                _ui.Line(
                    $"[tool] {sessionEvent.Name} {Truncate(sessionEvent.Args, 240)}");
                break;
            case SessionEventKind.ToolOutput:
                if (sessionEvent.Text.Length > 0)
                {
                    _ui.Delta(
                        sessionEvent.Text.EndsWith('\n')
                            ? sessionEvent.Text
                            : sessionEvent.Text + "\n");
                }
                break;
            case SessionEventKind.ApprovalResolved:
                _ui.Line($"[approval] {sessionEvent.Decision}");
                break;
            case SessionEventKind.ToolCompleted:
                _ui.Line(
                    $"[{(sessionEvent.Ok ? "ok" : "fail")}] {sessionEvent.Name}: {sessionEvent.Summary}");
                if (!string.IsNullOrWhiteSpace(sessionEvent.Diff))
                {
                    _ui.Diff(sessionEvent.Diff);
                }
                break;
            case SessionEventKind.AuditRecorded when _options.Verbose:
                _ui.Line($"[audit] {sessionEvent.Text}");
                break;
            case SessionEventKind.TurnCompleted:
            case SessionEventKind.TurnFailed:
                _ui.Line();
                break;
            case SessionEventKind.Error:
                _ui.Error(sessionEvent.Text);
                break;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> RequestApprovalAsync(
        ApprovalRequestEvent request,
        CancellationToken cancellationToken)
    {
        _ui.Line();
        _ui.Line($"[approval] {request.Name} ({request.Action}) {request.Resource}");
        _ui.Line($"  reason: {request.Reason}");
        _ui.Line($"  args: {Truncate(request.Args, 800)}");

        if (_options.AutoApprove)
        {
            return ValueTask.FromResult(true);
        }

        if (Console.IsInputRedirected)
        {
            _ui.Error("stdin is not interactive; denying approval (use --yes to allow)");
            return ValueTask.FromResult(false);
        }

        _ui.Line("Approve? [y/N] ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        return ValueTask.FromResult(answer is "y" or "yes");
    }

    private static string Truncate(string text, int limit) =>
        text.Length <= limit ? text : text[..limit] + "...";
}

public sealed class TuiSessionPresenter : ISessionPresenter
{
    private readonly Channel<SessionEvent> _events =
        Channel.CreateUnbounded<SessionEvent>(
            new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _approvals = new();
    private readonly bool _autoApprove;

    public TuiSessionPresenter(bool autoApprove)
    {
        _autoApprove = autoApprove;
    }

    public ChannelReader<SessionEvent> Events => _events.Reader;

    public void Publish(SessionEvent sessionEvent) =>
        _events.Writer.TryWrite(sessionEvent);

    public ValueTask OnEventAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
    {
        Publish(sessionEvent);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<bool> RequestApprovalAsync(
        ApprovalRequestEvent request,
        CancellationToken cancellationToken)
    {
        Publish(new SessionEvent(
            SessionEventKind.ApprovalRequested,
            Name: request.Name,
            Args: request.Args,
            ApprovalId: request.ApprovalId,
            Action: request.Action,
            Resource: request.Resource,
            Reason: request.Reason));

        if (_autoApprove)
        {
            return true;
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _approvals[request.ApprovalId] = completion;
        try
        {
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            _approvals.TryRemove(request.ApprovalId, out _);
        }
    }

    public bool ResolveApproval(string approvalId, bool allow)
    {
        if (!_approvals.TryGetValue(approvalId, out var completion))
        {
            return false;
        }

        return completion.TrySetResult(allow);
    }
}
