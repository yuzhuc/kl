namespace Kl.Gui.Models;

public sealed class AppState
{
    public string Endpoint { get; set; } = "https://api.deepseek.com";
    public string Model { get; set; } = "deepseek-chat";
    public string ApiKey { get; set; } = string.Empty;
    public string ApiKeyEnvironmentVariable { get; set; } = string.Empty;
    public string PermissionMode { get; set; } = "完全访问";
    public int ContentFontSize { get; set; } = 14;
    public string InteractionMode { get; set; } = "work";
    public bool IsAddProjectPinned { get; set; }
    public bool IsProjectSectionExpanded { get; set; } = true;
    public bool IsCodeReviewPinned { get; set; }
    public double SidebarWidth { get; set; } = 284;
    public string? ActiveWorkspaceId { get; set; }
    public string? ActiveSessionId { get; set; }
    public List<WorkspaceState> Workspaces { get; set; } = [];
    public List<AutomationState> Automations { get; set; } = [];
}

public sealed class WorkspaceState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsExpanded { get; set; } = true;
    public List<SessionState> Sessions { get; set; } = [];
}

public sealed class SessionState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "新会话";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
    public bool IsPinned { get; set; }
    public bool IsArchived { get; set; }
    public List<StoredMessage> Messages { get; set; } = [];
}

public sealed class StoredMessage
{
    public string Kind { get; set; } = "assistant";
    public string Header { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;
}

public sealed class AutomationState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新自动化";
    public string Prompt { get; set; } = string.Empty;
    public int IntervalMinutes { get; set; } = 60;
    public bool IsEnabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public string LastStatus { get; set; } = string.Empty;
    public string LastResult { get; set; } = string.Empty;
}
