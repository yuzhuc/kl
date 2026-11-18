namespace Kl.Gui.Models;

public sealed class AppState
{
    public string Endpoint { get; set; } = "https://api.deepseek.com";
    public string Model { get; set; } = "deepseek-chat";
    public string ApiKey { get; set; } = string.Empty;
    public string ApiKeyEnvironmentVariable { get; set; } = string.Empty;
    public string PermissionMode { get; set; } = "完全访问";
    /// <summary>User chose 不再显示 on the full-access warning card.</summary>
    public bool FullAccessWarningDismissed { get; set; }
    public int ContentFontSize { get; set; } = 14;
    public string InteractionMode { get; set; } = "work";
    public bool IsAddProjectPinned { get; set; }
    public bool IsProjectSectionExpanded { get; set; } = true;
    public bool IsCodeReviewPinned { get; set; }
    public double SidebarWidth { get; set; } = 238;
    public bool IsSidebarGrouped { get; set; } = true;
    public string SessionSort { get; set; } = "updated";
    public string PinnedSort { get; set; } = "manual";
    public string NotificationFilter { get; set; } = "priority";

    /// <summary>Remote Core endpoint (tcp://host:port); null means local.</summary>
    public string? RemoteCoreEndpoint { get; set; }
    /// <summary>"enter" sends on Enter (Shift+Enter is a newline); "ctrlEnter" requires Ctrl.</summary>
    public string SendShortcut { get; set; } = "enter";
    /// <summary>"always" confirms every close; "shortcut" only for Ctrl+Shift+W; "never" closes at once.</summary>
    public string CloseWindowAction { get; set; } = "shortcut";
    public bool LaunchAtLogin { get; set; }
    /// <summary>Archive chats untouched for this many days on startup; 0 keeps them.</summary>
    public int AutoArchiveDays { get; set; }
    /// <summary>"unfocused" notifies only when the window is in the background; "always" or "never".</summary>
    public string TurnNotifications { get; set; } = "unfocused";
    public bool ApprovalNotifications { get; set; } = true;
    public bool QuestionNotifications { get; set; } = true;
    public bool NotificationSound { get; set; } = true;
    /// <summary>Show the active session's token usage in the chat header.</summary>
    public bool ShowContextUsage { get; set; } = true;
    public bool NumberShortcuts { get; set; } = true;
    /// <summary>Hex accent used for focus rings, carets and slider tracks.</summary>
    public string AccentColor { get; set; } = "#4D9FFF";
    public bool UsePointerCursor { get; set; }
    public int ContentCodeFontSize { get; set; } = 12;
    /// <summary>"system", "on" (reduce) or "off"; controls sidebar and list animations.</summary>
    public string ReduceMotion { get; set; } = "system";
    /// <summary>"queue" holds a prompt until the running turn ends; "steer" is not supported by Core yet.</summary>
    public string FollowUpMode { get; set; } = "queue";
    public string LastSettingsPane { get; set; } = "General";
    public bool RestoreLastSession { get; set; } = true;
    public string? ActiveWorkspaceId { get; set; }
    public string? ActiveSessionId { get; set; }
    public List<WorkspaceState> Workspaces { get; set; } = [];
    public List<AutomationState> Automations { get; set; } = [];
    public List<LibraryItemState> LibraryItems { get; set; } = [];
    /// <summary>GUI-local display name, note and website per provider id.</summary>
    public Dictionary<string, ProviderProfileState> ProviderProfiles { get; set; } = [];
}

/// <summary>Display metadata for one model provider (never credentials).</summary>
public sealed class ProviderProfileState
{
    public string DisplayName { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string Website { get; set; } = string.Empty;
}

/// <summary>One saved prompt or note on the Library page (GUI-local state).</summary>
public sealed class LibraryItemState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class WorkspaceState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsExpanded { get; set; } = true;
    public bool IsPinned { get; set; }
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
    public bool IsUnread { get; set; }

    /// <summary>Order inside the 置顶 section when 手动排序 is selected.</summary>
    public int PinnedOrder { get; set; }

    /// <summary>Manual order inside its project list after a drag.</summary>
    public int SortOrder { get; set; }

    /// <summary>Usage accumulated from TurnCompleted events (0 for older sessions).</summary>
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public double CostUsd { get; set; }
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
