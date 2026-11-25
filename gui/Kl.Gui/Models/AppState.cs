using Loc = Kl.Gui.Services.Localization;
namespace Kl.Gui.Models;

public sealed class AppState
{
    public string Endpoint { get; set; } = "https://api.deepseek.com";
    public string Model { get; set; } = "deepseek-chat";
    /// <summary>Approval mode code: full, auto or ask.</summary>
    public string PermissionMode { get; set; } = "full";
    /// <summary>User chose 不再显示 on the full-access warning card.</summary>
    public bool FullAccessWarningDismissed { get; set; }
    public int ContentFontSize { get; set; } = 14;
    /// <summary>0 = Inter, 1 = system UI font.</summary>
    public int UiFontIndex { get; set; }
    /// <summary>0 = same as UI, 1 = Inter, 2 = system UI font.</summary>
    public int ContentFontIndex { get; set; }
    /// <summary>0 = Geist Mono, 1 = JetBrains Mono, 2 = system monospace.</summary>
    public int CodeFontIndex { get; set; }
    /// <summary>0 = normal, 1 = medium, 2 = semibold.</summary>
    public int UiFontWeightIndex { get; set; }
    public int ContentFontWeightIndex { get; set; }
    public int CodeFontWeightIndex { get; set; }
    /// <summary>0..100; 50 keeps the original palette.</summary>
    public int Contrast { get; set; } = 50;
    public bool TranslucentSidebar { get; set; }
    /// <summary>Gzip the GUI state file (chat history and settings).</summary>
    public bool CompressHistory { get; set; } = true;
    public string InteractionMode { get; set; } = "work";
    public bool IsAddProjectPinned { get; set; }
    public bool IsProjectSectionExpanded { get; set; } = true;
    public bool IsCodeReviewPinned { get; set; }
    public double SidebarWidth { get; set; } = 238;
    public bool IsSidebarGrouped { get; set; } = true;
    public string SessionSort { get; set; } = "updated";
    public string PinnedSort { get; set; } = "manual";
    /// <summary>Agent preset id for new sessions; empty uses the Core default.</summary>
    public string AgentPreset { get; set; } = "";
    /// <summary>Quality mode override: empty follows Core, otherwise fast/balanced/deep.</summary>
    public string QualityMode { get; set; } = "";
    /// <summary>"provider/model" → the composer's reasoning effort for that model.</summary>
    public Dictionary<string, string> ModelReasoningEfforts { get; set; } = [];
    /// <summary>UI language: empty follows the system, otherwise zh-Hans or en.</summary>
    public string Language { get; set; } = "";
    public string NotificationFilter { get; set; } = "priority";

    /// <summary>Remote Core endpoint (tcp:// or tls://host:port); null means local.</summary>
    public string? RemoteCoreEndpoint { get; set; }
    /// <summary>SHA-256 of the TLS leaf certificate; empty for plain tcp://.</summary>
    public string? RemoteCoreTlsFingerprint { get; set; }
    /// <summary>"enter" sends on Enter (Shift+Enter is a newline); "ctrlEnter" requires Ctrl.</summary>
    public string SendShortcut { get; set; } = "enter";
    /// <summary>"always" confirms every close; "shortcut" only for Ctrl+Shift+W; "never" closes at once.</summary>
    public string CloseWindowAction { get; set; } = "shortcut";
    /// <summary>"system" opens files with the OS default; "vscode" uses the code CLI.</summary>
    public string FileOpenTarget { get; set; } = "system";
    public bool LaunchAtLogin { get; set; }
    /// <summary>Archive chats untouched for this many days on startup; 0 keeps them.</summary>
    public int AutoArchiveDays { get; set; }
    /// <summary>"unfocused" notifies only when the window is in the background; "always" or "never".</summary>
    public string TurnNotifications { get; set; } = "unfocused";
    public bool ApprovalNotifications { get; set; } = true;
    public bool QuestionNotifications { get; set; } = true;
    public bool AutomationNotifications { get; set; } = true;
    public bool NotificationSound { get; set; } = true;
    /// <summary>Show the active session's token usage in the chat header.</summary>
    public bool ShowContextUsage { get; set; } = true;
    public bool NumberShortcuts { get; set; } = true;
    /// <summary>Action id → gesture; missing entries use the built-in default.</summary>
    public Dictionary<string, string> ShortcutBindings { get; set; } = [];
    /// <summary>Hex accent used for focus rings, carets and slider tracks.</summary>
    public string AccentColor { get; set; } = "#4D9FFF";
    /// <summary>"dark", "light" or "system".</summary>
    public string ThemeMode { get; set; } = "dark";
    /// <summary>Keep separate accent and contrast values for light and dark.</summary>
    public bool SeparateThemeSettings { get; set; }
    public string LightAccentColor { get; set; } = "#3566F0";
    public string DarkAccentColor { get; set; } = "#4D9FFF";
    public int LightContrast { get; set; } = 50;
    public int DarkContrast { get; set; } = 50;
    /// <summary>"color" hides +/- and tints changed lines; "markers" keeps the signs.</summary>
    public string DiffMarkerMode { get; set; } = "color";
    /// <summary>"powershell", "cmd" or "git-bash".</summary>
    public string TerminalShell { get; set; } = "powershell";
    /// <summary>"bottom" or "right".</summary>
    public string TerminalPosition { get; set; } = "bottom";
    /// <summary>Panel height (bottom) or width (right); 0 uses the orientation default.</summary>
    public double TerminalPanelSize { get; set; }
    /// <summary>Show the title-bar control that toggles the terminal panel.</summary>
    public bool ShowTerminalPanelControl { get; set; } = true;
    public bool TerminalPanelOpen { get; set; }
    public bool UsePointerCursor { get; set; }
    public int ContentCodeFontSize { get; set; } = 12;
    /// <summary>"system", "on" (reduce) or "off"; controls sidebar and list animations.</summary>
    public string ReduceMotion { get; set; } = "system";
    /// <summary>"queue" holds a prompt until the running turn ends; "steer" is not supported by Core yet.</summary>
    public string FollowUpMode { get; set; } = "queue";
    /// <summary>"quick", "standard" or "strict".</summary>
    public string ReviewMode { get; set; } = "standard";
    /// <summary>Send changed files in full instead of only the diff.</summary>
    public bool ReviewFullFiles { get; set; }
    public string ReviewInstructions { get; set; } = string.Empty;
    /// <summary>Blank uses the project's .kl/worktrees directory.</summary>
    public string WorktreeRoot { get; set; } = string.Empty;
    public bool WorktreeFetchBeforeCreate { get; set; }
    public bool WorktreeConfirmDelete { get; set; } = true;
    /// <summary>Prefix for branches created from the Worktree settings.</summary>
    public string GitBranchPrefix { get; set; } = "kl/";
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
    public string Title { get; set; } = Loc.T("新会话");
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
    /// <summary>Semantic tool kind (Shell/Image/Edit/Read/Search/Web/Todo/Context); empty in legacy rows.</summary>
    public string ToolKind { get; set; } = string.Empty;
}

public sealed class AutomationState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = Loc.T("新自动化");
    public string Prompt { get; set; } = string.Empty;
    public int IntervalMinutes { get; set; } = 60;
    public bool IsEnabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public string LastStatus { get; set; } = string.Empty;
    public string LastResult { get; set; } = string.Empty;
}
