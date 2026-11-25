using System.Collections.Concurrent;
using System.Threading.Channels;
using Kl.Client;

namespace Kl.Gui.Services;

/// <summary>
/// Bridges the shared <see cref="KlSessionClient"/> presenter callbacks into the
/// GUI. Events are queued on a channel and drained by <see cref="CoreSessionService"/>
/// so the view-model can marshal them onto the UI thread; approval requests keep
/// the core event loop parked until the UI resolves them.
/// </summary>
public sealed class CoreSessionPresenter : ISessionPresenter, ISessionApprovalDecisionPresenter
{
    private readonly Channel<SessionEvent> _events = Channel.CreateUnbounded<SessionEvent>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _approvals = new();

    public ChannelReader<SessionEvent> Events => _events.Reader;

    public ValueTask OnEventAsync(SessionEvent sessionEvent, CancellationToken cancellationToken)
    {
        _events.Writer.TryWrite(sessionEvent);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<bool> RequestApprovalAsync(
        ApprovalRequestEvent request,
        CancellationToken cancellationToken)
    {
        var decision = await RequestApprovalDecisionAsync(request, cancellationToken).ConfigureAwait(false);
        return decision is "allow" or "allow_session";
    }

    public async ValueTask<string> RequestApprovalDecisionAsync(
        ApprovalRequestEvent request,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _approvals[request.ApprovalId] = completion;

        _events.Writer.TryWrite(new SessionEvent(
            SessionEventKind.ApprovalRequested,
            Name: request.Name,
            Args: request.Args,
            ApprovalId: request.ApprovalId,
            Action: request.Action,
            Resource: request.Resource,
            Reason: request.Reason));

        try
        {
            return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Turn cancelled or session closed while the panel was open.
            _events.Writer.TryWrite(new SessionEvent(
                SessionEventKind.ApprovalResolved,
                ApprovalId: request.ApprovalId,
                Decision: "deny"));
            return "deny";
        }
        finally
        {
            _approvals.TryRemove(request.ApprovalId, out _);
        }
    }

    public bool ResolveApprovalDecision(string approvalId, string decision)
    {
        if (!_approvals.TryGetValue(approvalId, out var completion))
        {
            return false;
        }

        return completion.TrySetResult(decision);
    }

    public void Complete() => _events.Writer.TryComplete();
}
