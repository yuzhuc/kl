namespace Kl.Client;

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

public interface ISessionApprovalDecisionPresenter
{
    ValueTask<string> RequestApprovalDecisionAsync(
        ApprovalRequestEvent request,
        CancellationToken cancellationToken);
}

public interface ISessionPresenter
{
    ValueTask OnEventAsync(SessionEvent sessionEvent, CancellationToken cancellationToken);

    ValueTask<bool> RequestApprovalAsync(
        ApprovalRequestEvent request,
        CancellationToken cancellationToken);
}