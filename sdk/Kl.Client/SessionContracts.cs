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
    ContextCompacted,
    TurnCompleted,
    TurnFailed,
    Error
}

/// <summary>
/// One presenter-facing session event. All correlation and ordering fields are
/// additive and optional, so existing consumers keep compiling unchanged.
/// </summary>
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
    string? TurnId = null,
    long InputTokens = 0,
    long OutputTokens = 0,
    double? CostUsd = null,
    long Seq = 0,
    string? CallId = null,
    string? Output = null,
    bool ConnectionLost = false);

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