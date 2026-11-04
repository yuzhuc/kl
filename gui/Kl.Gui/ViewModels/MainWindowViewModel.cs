using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Kl.Client;
using Kl.Gui.Models;
using Kl.Gui.Services;

namespace Kl.Gui.ViewModels;

public enum AppSection
{
    Chat,
    Image,
    Library,
    Map,
    Projects,
    Plugins,
    Automation,
    Settings
}

public enum SettingsPane
{
    General,
    Models,
    Data,
    About
}

public enum AutomationFilter
{
    All,
    Active,
    Ended
}

public enum ChatItemKind
{
    User,
    Assistant,
    Reasoning,
    Tool,
    Error
}

public sealed class ChatItemViewModel : ObservableObject
{
    private string _header;
    private string _text;
    private string _detail;
    private string _summary;
    private string _statusText;
    private string _timestampLabel;
    private bool _isRunning;
    private bool _isExpanded;

    private ChatItemViewModel(
        ChatItemKind kind,
        string header,
        string text = "",
        string detail = "",
        string summary = "",
        string status = "",
        string timestampLabel = "")
    {
        Kind = kind;
        _header = header;
        _text = text;
        _detail = detail;
        _summary = summary;
        _statusText = status;
        _timestampLabel = timestampLabel;
    }

    public ChatItemKind Kind { get; }
    public bool IsUser => Kind == ChatItemKind.User;
    public bool IsAssistant => Kind == ChatItemKind.Assistant;
    public bool IsReasoning => Kind == ChatItemKind.Reasoning;
    public bool IsTool => Kind == ChatItemKind.Tool;
    public bool IsError => Kind == ChatItemKind.Error;
    public bool IsActivity => IsReasoning || IsTool;
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);
    public bool ShowAssistantActions => IsAssistant && HasText && !IsRunning;
    public string ApiText => IsUser && !string.IsNullOrWhiteSpace(Detail) ? Detail : Text;

    /// <summary>Short local time label shown under user messages (Codex-style).</summary>
    public string TimestampLabel
    {
        get => _timestampLabel;
        private set => SetProperty(ref _timestampLabel, value);
    }

    public string Header
    {
        get => _header;
        private set => SetProperty(ref _header, value);
    }

    public string Text
    {
        get => _text;
        private set
        {
            if (SetProperty(ref _text, value))
            {
                OnPropertyChanged(nameof(HasText));
                OnPropertyChanged(nameof(CollapsedText));
                OnPropertyChanged(nameof(ShowAssistantActions));
            }
        }
    }

    public string Detail
    {
        get => _detail;
        private set
        {
            if (SetProperty(ref _detail, value))
            {
                OnPropertyChanged(nameof(HasDetail));
            }
        }
    }

    public string Summary
    {
        get => _summary;
        private set
        {
            if (SetProperty(ref _summary, value))
            {
                OnPropertyChanged(nameof(CollapsedText));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(ShowAssistantActions));
            }
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        private set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ShowExpanded));
                OnPropertyChanged(nameof(ShowCollapsed));
                OnPropertyChanged(nameof(ShowChevronDown));
                OnPropertyChanged(nameof(ShowChevronUp));
            }
        }
    }

    public bool ShowExpanded => IsActivity && IsExpanded;
    public bool ShowCollapsed => IsActivity && !IsExpanded;
    public bool ShowChevronDown => ShowCollapsed;
    public bool ShowChevronUp => ShowExpanded;

    public string CollapsedText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Summary))
            {
                return Summary;
            }

            var value = Text.Trim();
            var newline = value.IndexOf('\n');
            return newline < 0 ? value : value[..newline];
        }
    }

    public static ChatItemViewModel User(
        string text,
        string? apiText = null,
        DateTimeOffset? timestamp = null) =>
        new(
            ChatItemKind.User,
            "你",
            text,
            detail: apiText ?? string.Empty,
            timestampLabel: timestamp is null
                ? string.Empty
                : timestamp.Value.ToLocalTime().ToString("HH:mm"));

    public static ChatItemViewModel Assistant(string text) =>
        new(ChatItemKind.Assistant, "kl", text);

    public static ChatItemViewModel Reasoning(string text = "") =>
        new(ChatItemKind.Reasoning, "思考", text);

    public static ChatItemViewModel Tool(
        string header,
        string command,
        string output,
        string summary) =>
        new(ChatItemKind.Tool, header, command, output, summary);

    public static ChatItemViewModel Error(string text) =>
        new(ChatItemKind.Error, "请求失败", text, status: "错误");

    public static ChatItemViewModel FromStored(StoredMessage message)
    {
        var kind = message.Kind.ToLowerInvariant() switch
        {
            "user" => ChatItemKind.User,
            "reasoning" => ChatItemKind.Reasoning,
            "tool" => ChatItemKind.Tool,
            "error" => ChatItemKind.Error,
            _ => ChatItemKind.Assistant
        };
        var text = message.Text;
        var detail = message.Detail;
        var summary = message.Summary;
        if (kind == ChatItemKind.Tool)
        {
            if (text.TrimStart().StartsWith('{')
                && detail.StartsWith("模型请求调用工具", StringComparison.Ordinal))
            {
                (text, detail) = (detail, text);
            }
            if (string.Equals(summary, message.Header, StringComparison.Ordinal))
            {
                summary = string.Empty;
            }
        }
        return new ChatItemViewModel(
            kind,
            message.Header,
            text,
            detail,
            summary,
            message.Status,
            message.Timestamp);
    }

    public StoredMessage ToStored() => new()
    {
        Kind = Kind.ToString().ToLowerInvariant(),
        Header = Header,
        Text = Text,
        Detail = Detail,
        Summary = Summary,
        Status = StatusText,
        Timestamp = TimestampLabel
    };

    public void Start(string status = "进行中")
    {
        IsRunning = true;
        if (IsActivity)
        {
            IsExpanded = true;
        }
        StatusText = status;
    }

    public void AppendText(string text)
    {
        if (text.Length > 0)
        {
            Text += text;
        }
    }

    public void AppendDetail(string text)
    {
        if (text.Length > 0)
        {
            Detail += text;
        }
    }

    public void SetHeader(string header) => Header = header;
    public void SetSummary(string summary) => Summary = summary;

    public void Complete(string? status = "完成", string? header = null)
    {
        IsRunning = false;
        IsExpanded = false;
        if (header is not null)
        {
            Header = header;
        }
        StatusText = status ?? string.Empty;
    }

    public void Stop(string status = "已停止")
    {
        IsRunning = false;
        IsExpanded = false;
        StatusText = status;
    }

    public void Toggle()
    {
        if (IsActivity)
        {
            IsExpanded = !IsExpanded;
        }
    }
}

public sealed class SessionItemViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isVisible = true;

    public SessionItemViewModel(WorkspaceItemViewModel workspace, SessionState state)
    {
        Workspace = workspace;
        State = state;
    }

    public WorkspaceItemViewModel Workspace { get; }
    public SessionState State { get; }
    public string Id => State.Id;

    public string Title
    {
        get => State.Title;
        set
        {
            if (State.Title == value)
            {
                return;
            }
            State.Title = value;
            OnPropertyChanged();
        }
    }

    public string UpdatedLabel
    {
        get
        {
            var value = State.UpdatedAt.ToLocalTime();
            return value.Date == DateTimeOffset.Now.Date
                ? value.ToString("HH:mm")
                : value.ToString("MM-dd");
        }
    }

    /// <summary>Codex-style relative time used by the session preview card.</summary>
    public string RelativeLabel
    {
        get
        {
            var elapsed = DateTimeOffset.Now - State.UpdatedAt.ToLocalTime();
            if (elapsed.TotalMinutes < 1)
            {
                return "刚刚";
            }

            if (elapsed.TotalHours < 1)
            {
                return $"{(int)elapsed.TotalMinutes} 分钟前";
            }

            if (elapsed.TotalDays < 1)
            {
                return $"{(int)elapsed.TotalHours} 小时前";
            }

            if (elapsed.TotalDays < 30)
            {
                return $"{(int)elapsed.TotalDays} 天前";
            }

            return State.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd");
        }
    }

    public bool IsPinned
    {
        get => State.IsPinned;
        set
        {
            if (State.IsPinned == value)
            {
                return;
            }

            State.IsPinned = value;
            OnPropertyChanged();
        }
    }

    public bool IsArchived
    {
        get => State.IsArchived;
        set
        {
            if (State.IsArchived == value)
            {
                return;
            }

            State.IsArchived = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsListVisible));
        }
    }

    /// <summary>Visible in the sidebar lists (search filter + not archived).</summary>
    public bool IsListVisible => IsVisible && !IsArchived;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public void RefreshTime() => OnPropertyChanged(nameof(UpdatedLabel));

    public void RefreshTitle() => OnPropertyChanged(nameof(Title));

    public bool Matches(string query)
    {
        if (State.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
        {
            return true;
        }
        return State.Messages.Any(message =>
            message.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || message.Detail.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }
}

public sealed class WorkspaceItemViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isVisible = true;
    private bool _hasVisibleSessions = true;
    private bool _isSearchActive;

    public WorkspaceItemViewModel(WorkspaceState state)
    {
        State = state;
        Sessions = new ObservableCollection<SessionItemViewModel>(
            state.Sessions
                .OrderByDescending(session => session.UpdatedAt)
                .Select(session => new SessionItemViewModel(this, session)));
        foreach (var session in Sessions)
        {
            session.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SessionItemViewModel.IsVisible))
                {
                    RefreshVisibility();
                }
            };
        }

        Sessions.CollectionChanged += (_, _) => RefreshPreview();
    }

    public WorkspaceState State { get; }
    public ObservableCollection<SessionItemViewModel> Sessions { get; }
    public string Id => State.Id;
    public string Name => State.Name;
    public string Path => State.Path;

    /// <summary>Codex-style project preview details.</summary>
    public string ChatCountLabel => $"{Sessions.Count} 次聊天";

    public string ParentPath
    {
        get
        {
            try
            {
                var parent = System.IO.Path.GetDirectoryName(State.Path);
                return string.IsNullOrWhiteSpace(parent) ? State.Path : parent;
            }
            catch (ArgumentException)
            {
                return State.Path;
            }
        }
    }

    public bool IsPinned
    {
        get => State.IsPinned;
        set
        {
            if (State.IsPinned == value)
            {
                return;
            }

            State.IsPinned = value;
            OnPropertyChanged();
        }
    }

    public void RefreshName() => OnPropertyChanged(nameof(Name));

    public void RefreshPreview()
    {
        OnPropertyChanged(nameof(ChatCountLabel));
        OnPropertyChanged(nameof(ParentPath));
    }

    /// <summary>Re-orders the nested conversations for "聊天排序方式".</summary>
    public void ApplySessionSort(bool byCreated)
    {
        var ordered = (byCreated
                ? Sessions.OrderByDescending(session => session.State.CreatedAt)
                : Sessions.OrderByDescending(session => session.State.UpdatedAt))
            .ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var current = Sessions.IndexOf(ordered[index]);
            if (current != index)
            {
                Sessions.Move(current, index);
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public bool HasVisibleSessions
    {
        get => _hasVisibleSessions;
        private set => SetProperty(ref _hasVisibleSessions, value);
    }

    public bool IsSearchActive
    {
        get => _isSearchActive;
        private set
        {
            if (SetProperty(ref _isSearchActive, value))
            {
                OnPropertyChanged(nameof(ShowSessions));
            }
        }
    }

    public bool IsExpanded
    {
        get => State.IsExpanded;
        set
        {
            if (State.IsExpanded == value)
            {
                return;
            }
            State.IsExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowSessions));
        }
    }

    public bool ShowSessions => IsExpanded || IsSearchActive;

    public void ApplySearch(string query)
    {
        var workspaceMatches = query.Length == 0
            || Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || Path.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        IsSearchActive = query.Length > 0;
        foreach (var session in Sessions)
        {
            session.IsVisible = workspaceMatches || session.Matches(query);
        }
        IsVisible = workspaceMatches || Sessions.Any(session => session.IsVisible);
        RefreshVisibility();
    }

    private void RefreshVisibility()
    {
        HasVisibleSessions = Sessions.Any(session => session.IsVisible);
    }
}

public sealed class PluginItemViewModel
{
    public PluginItemViewModel(LocalPluginInfo plugin)
    {
        Name = plugin.Name;
        Version = plugin.Version;
        Description = plugin.Description;
        ManifestPath = plugin.ManifestPath;
    }

    public string Name { get; }
    public string Version { get; }
    public string Description { get; }
    public string ManifestPath { get; }
}

public sealed partial class MainWindowViewModel : ObservableObject
{
    private static readonly string[] SupportedPermissionModes = ["完全访问", "按需确认"];
    private static readonly HashSet<string> SupportedAttachmentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".jsonl", ".cs", ".ts", ".tsx", ".js", ".jsx", ".css", ".html",
        ".xml", ".yaml", ".yml", ".ps1", ".py", ".java", ".go", ".rs", ".sql", ".toml", ".ini",
        ".log", ".csv", ".csproj", ".props", ".targets", ".sln"
    };
    private const int MaxAttachmentCount = 8;
    private const int MaxAttachmentCharacters = 100_000;
    private readonly StateStore _stateStore;
    private readonly IPluginCatalogService _pluginCatalogService;
    private AppState _state;
    private WorkspaceItemViewModel _activeWorkspace;
    private SessionState? _activeSession;
    private SessionItemViewModel? _activeSessionItem;
    private SessionItemViewModel? _dialogTarget;
    private WorkspaceItemViewModel? _renameWorkspaceTarget;
    private ChatItemViewModel? _activeReasoning;
    private ChatItemViewModel? _activeAssistant;
    private string _composer = string.Empty;
    private string _searchText = string.Empty;
    private string _sessionTitle = "新会话";
    private string _settingsPermissionMode = "完全访问";
    private int _settingsContentFontSize = 14;
    private string _settingsStatus = string.Empty;
    private string _renameTitle = string.Empty;
    private string _deletePrompt = string.Empty;
    private bool _isBusy;
    private bool _isSidebarVisible = true;
    private bool _isSearchVisible;
    private bool _isRecentExpanded = true;
    private bool _isArchivedExpanded;
    private bool _isChatPage = true;
    private bool _isLibraryPage;
    private bool _isCodeReviewDraftActive;
    private bool _isProjectsPage;
    private bool _isPluginsPage;
    private bool _isAutomationPage;
    private bool _isSettingsPage;
    private bool _isRenameDialogOpen;
    private bool _isDeleteDialogOpen;
    private bool _isTestingConnection;
    private bool _settingsStatusIsError;
    private string _errorMessage = string.Empty;
    private string _pluginStatus = string.Empty;
    private AutomationItemViewModel? _automationDialogTarget;
    private AutomationItemViewModel? _automationDeleteTarget;
    private AutomationIntervalOption? _selectedAutomationInterval;
    private string _automationName = string.Empty;
    private string _automationPrompt = string.Empty;
    private string _automationDialogStatus = string.Empty;
    private string _automationDeletePrompt = string.Empty;
    private bool _isAutomationDialogOpen;
    private bool _isAutomationDeleteDialogOpen;
    private bool _automationSchedulerBusy;
    private bool _isSettingsOpen;
    private SettingsPane _settingsPane = SettingsPane.General;
    private AutomationFilter _automationFilter = AutomationFilter.All;
    private string _automationSearch = string.Empty;

    public MainWindowViewModel(
        StateStore? stateStore = null,
        IPluginCatalogService? pluginCatalogService = null,
        ICoreSessionService? coreSessionService = null,
        Action<Action>? dispatcher = null)
    {
        _stateStore = stateStore ?? new StateStore();
        _pluginCatalogService = pluginCatalogService ?? new LocalPluginCatalogService();
        _core = coreSessionService ?? new UnavailableCoreSessionService();
        _dispatch = dispatcher ?? (static action => action());
        _core.EventReceived += OnCoreEventReceived;
        _state = _stateStore.Load();
        NormalizeState();
        RefreshPinnedRailItems();
        BuildWorkspaceItems();
        _activeWorkspace = Workspaces.First(workspace => workspace.Id == _state.ActiveWorkspaceId);
        _activeWorkspace.IsSelected = true;
        if (TryFindSession(_state.ActiveSessionId, out var activeWorkspace, out var activeSession))
        {
            SelectSession(activeWorkspace, activeSession.State);
        }
        else
        {
            StartDraftSession();
        }

        RefreshSettingsDraft();
        RefreshPlugins();
        BuildAutomationItems();
        SaveState(silent: true);

        Messages.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasConversation));
            OnPropertyChanged(nameof(IsConversationEmpty));
            OnPropertyChanged(nameof(ShowConversationHeader));
            OnPropertyChanged(nameof(ComposerPlaceholder));
        };

        _ = InitializeCoreAsync();
    }

    public ObservableCollection<WorkspaceItemViewModel> Workspaces { get; } = [];
    public ObservableCollection<ChatItemViewModel> Messages { get; } = [];
    public ObservableCollection<AttachmentItemViewModel> PendingAttachments { get; } = [];
    public ObservableCollection<PluginItemViewModel> Plugins { get; } = [];
    public ObservableCollection<SessionItemViewModel> RecentSessions { get; } = [];
    public ObservableCollection<SessionItemViewModel> ArchivedSessions { get; } = [];
    public ObservableCollection<PinnedRailItemViewModel> PinnedRailItems { get; } = [];
    public ObservableCollection<AutomationItemViewModel> Automations { get; } = [];
    public IReadOnlyList<AutomationIntervalOption> AutomationIntervals { get; } =
    [
        new(15, "每 15 分钟"),
        new(60, "每小时"),
        new(360, "每 6 小时"),
        new(1440, "每天")
    ];
    public IReadOnlyList<string> PermissionModes => SupportedPermissionModes;
    public IReadOnlyList<int> ContentFontSizes { get; } = [13, 14, 15, 16];
    public string VersionLabel => "0.3.0-gui.1";
    public AppState State => _state;
    public SessionItemViewModel? ActiveSessionItem => _activeSessionItem;

    public bool IsEmpty => Messages.Count == 0;
    public bool HasConversation => !IsEmpty;
    public bool IsConversationEmpty => IsEmpty;
    public bool IsChatPage => _isChatPage;
    public bool IsLibraryPage => _isLibraryPage;
    public bool IsProjectsPage => _isProjectsPage;
    public bool IsPluginsPage => _isPluginsPage;
    public bool IsAutomationPage => _isAutomationPage;
    public bool IsSettingsPage => _isSettingsPage;
    public bool IsAddProjectPinned => _state.IsAddProjectPinned;
    public bool IsCodeReviewPinned => _state.IsCodeReviewPinned;
    public bool ShowConversationHeader => IsChatPage && HasConversation;

    public bool IsSidebarVisible
    {
        get => _isSidebarVisible;
        private set => SetProperty(ref _isSidebarVisible, value);
    }

    /// <summary>Expanded sidebar width; the rail collapses below 3/4 of this value.</summary>
    public double SidebarWidth => _state.SidebarWidth;

    public void SetSidebarWidth(double width)
    {
        var clamped = Math.Clamp(width, 238, 468);
        if (Math.Abs(_state.SidebarWidth - clamped) < 0.5)
        {
            return;
        }

        _state.SidebarWidth = clamped;
        SaveState();
        OnPropertyChanged(nameof(SidebarWidth));
    }

    public void SetSidebarVisible(bool visible) => IsSidebarVisible = visible;

    public bool IsSearchVisible
    {
        get => _isSearchVisible;
        private set => SetProperty(ref _isSearchVisible, value);
    }

    public bool IsRecentExpanded
    {
        get => _isRecentExpanded;
        private set => SetProperty(ref _isRecentExpanded, value);
    }

    public bool HasRecentSessions => RecentSessions.Count > 0;
    public bool ShowNoRecentSessions => !HasRecentSessions;
    public bool ShowArchivedSessions => ArchivedSessions.Count > 0;

    public bool IsProjectSectionExpanded
    {
        get => _state.IsProjectSectionExpanded;
        private set
        {
            if (_state.IsProjectSectionExpanded == value)
            {
                return;
            }

            _state.IsProjectSectionExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowProjectList));
        }
    }

    /// <summary>Codex "整理侧边栏": grouped shows projects, merged hides them.</summary>
    public bool IsSidebarGrouped => _state.IsSidebarGrouped;

    public bool ShowProjectList => IsSidebarGrouped && IsProjectSectionExpanded;

    /// <summary>Codex "聊天排序方式".</summary>
    public bool IsSessionSortByUpdated => _state.SessionSort != "created";

    public bool IsSessionSortByCreated => _state.SessionSort == "created";

    public void SetSidebarGrouped(bool grouped)
    {
        if (_state.IsSidebarGrouped == grouped)
        {
            return;
        }

        _state.IsSidebarGrouped = grouped;
        OnPropertyChanged(nameof(IsSidebarGrouped));
        OnPropertyChanged(nameof(ShowProjectList));
        SaveState();
    }

    public void SetSessionSort(string sort)
    {
        var value = string.Equals(sort, "created", StringComparison.OrdinalIgnoreCase) ? "created" : "updated";
        if (_state.SessionSort == value)
        {
            return;
        }

        _state.SessionSort = value;
        OnPropertyChanged(nameof(IsSessionSortByUpdated));
        OnPropertyChanged(nameof(IsSessionSortByCreated));
        foreach (var workspace in Workspaces)
        {
            workspace.ApplySessionSort(value == "created");
        }

        RefreshRecentSessions();
        SaveState();
    }

    public bool IsArchivedExpanded
    {
        get => _isArchivedExpanded;
        private set => SetProperty(ref _isArchivedExpanded, value);
    }

    public void ToggleProjectSection()
    {
        IsProjectSectionExpanded = !IsProjectSectionExpanded;
        SaveState();
    }

    public void ToggleArchived()
    {
        IsArchivedExpanded = !IsArchivedExpanded;
    }

    public string UserInitials
    {
        get
        {
            var name = Environment.UserName;
            var letters = new string(name.Where(char.IsLetter).ToArray());
            if (letters.Length >= 2)
            {
                return letters[..2].ToLowerInvariant();
            }

            return letters.Length == 1 ? letters.ToLowerInvariant() : "kl";
        }
    }

    public string UserDisplayName
    {
        get
        {
            var name = Environment.UserName;
            return string.IsNullOrWhiteSpace(name) ? "本地用户" : name;
        }
    }

    public string AccountPlanLabel => "Local";

    public string ComposerPlaceholder => HasConversation
        ? IsChatMode ? "和 kl 一起聊天" : "和 kl 一起工作"
        : "随心输入";

    /// <summary>Codex-style empty-state headline that follows the selected mode.</summary>
    public string EmptyStateTitle => IsChatMode ? "我们要做什么?" : "我们要构建什么?";
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        private set => SetProperty(ref _isSettingsOpen, value);
    }
    public bool IsSettingsGeneral => _settingsPane == SettingsPane.General;
    public bool IsSettingsModels => _settingsPane == SettingsPane.Models;
    public bool IsSettingsData => _settingsPane == SettingsPane.Data;
    public bool IsSettingsAbout => _settingsPane == SettingsPane.About;
    public string SettingsPaneTitle => _settingsPane switch
    {
        SettingsPane.Models => "模型",
        SettingsPane.Data => "本地数据",
        SettingsPane.About => "关于与快捷键",
        _ => "通用设置"
    };
    public string SettingsPaneSubtitle => _settingsPane switch
    {
        SettingsPane.Models => "查看 KL Core 运行时、模型与连接状态。",
        SettingsPane.Data => "GUI 本地状态保存在 ~/.kl/gui；会话与消息真值由 KL Core 保存在 ~/.kl/sessions。",
        SettingsPane.About => "版本、隐私和常用快捷键。",
        _ => "调整权限模式和当前工作方式。"
    };
    public bool IsAutomationFilterAll => _automationFilter == AutomationFilter.All;
    public bool IsAutomationFilterActive => _automationFilter == AutomationFilter.Active;
    public bool IsAutomationFilterEnded => _automationFilter == AutomationFilter.Ended;
    public string AutomationEmptyTitle => Automations.Count == 0 ? "还没有自动化任务" : "没有匹配的自动化任务";
    public string AutomationEmptyDescription => Automations.Count == 0
        ? "创建任务后可手动运行；启用后会按间隔自动执行。"
        : "调整筛选条件或搜索关键词后再试。";
    public bool IsRenameDialogOpen
    {
        get => _isRenameDialogOpen;
        private set => SetProperty(ref _isRenameDialogOpen, value);
    }
    public bool IsDeleteDialogOpen
    {
        get => _isDeleteDialogOpen;
        private set => SetProperty(ref _isDeleteDialogOpen, value);
    }
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool HasSettingsStatus => !string.IsNullOrWhiteSpace(SettingsStatus);
    public bool IsSettingsStatusError => _settingsStatusIsError;
    public bool IsSettingsStatusSuccess => HasSettingsStatus && !_settingsStatusIsError;
    public string TestConnectionButtonText => IsTestingConnection ? "测试中…" : "测试连接";
    public bool IsTestingConnection => _isTestingConnection;
    public bool CanTestConnection => !IsTestingConnection;
    public string StateFilePath => _stateStore.Path;
    public string StateDirectory => Path.GetDirectoryName(_stateStore.Path) ?? string.Empty;
    public int WorkspaceCount => Workspaces.Count;
    public int SavedSessionCount => Workspaces.Sum(workspace => workspace.Sessions.Count);
    public int AutomationCount => Automations.Count;
    public bool HasAutomations => Automations.Count > 0;
    public bool ShowNoAutomations => !Automations.Any(item => item.IsVisible);
    public string AutomationSearch
    {
        get => _automationSearch;
        set
        {
            if (SetProperty(ref _automationSearch, value))
            {
                ApplyAutomationFilter();
            }
        }
    }
    public string AutomationPageSummary
    {
        get
        {
            if (Automations.Count == 0)
            {
                return "尚未创建自动化任务。";
            }
            var enabled = Automations.Count(item => item.IsEnabled);
            return $"{Automations.Count} 个任务，{enabled} 个已启用。";
        }
    }

    public bool IsAutomationDialogOpen
    {
        get => _isAutomationDialogOpen;
        private set => SetProperty(ref _isAutomationDialogOpen, value);
    }

    public bool IsAutomationDeleteDialogOpen
    {
        get => _isAutomationDeleteDialogOpen;
        private set => SetProperty(ref _isAutomationDeleteDialogOpen, value);
    }

    public string AutomationDialogTitle => _automationDialogTarget is null ? "新建自动化任务" : "编辑自动化任务";

    public string AutomationName
    {
        get => _automationName;
        set => SetProperty(ref _automationName, value);
    }

    public string AutomationPrompt
    {
        get => _automationPrompt;
        set => SetProperty(ref _automationPrompt, value);
    }

    public AutomationIntervalOption? SelectedAutomationInterval
    {
        get => _selectedAutomationInterval;
        set => SetProperty(ref _selectedAutomationInterval, value);
    }

    public string AutomationDialogStatus
    {
        get => _automationDialogStatus;
        private set
        {
            if (SetProperty(ref _automationDialogStatus, value))
            {
                OnPropertyChanged(nameof(HasAutomationDialogStatus));
            }
        }
    }

    public bool HasAutomationDialogStatus => !string.IsNullOrWhiteSpace(AutomationDialogStatus);

    public string AutomationDeletePrompt
    {
        get => _automationDeletePrompt;
        private set => SetProperty(ref _automationDeletePrompt, value);
    }

    public bool HasPlugins => Plugins.Count > 0;
    public bool ShowNoPlugins => Plugins.Count == 0;
    public string PluginDirectoryPath => _pluginCatalogService.DirectoryPath;
    public string PluginStatus
    {
        get => _pluginStatus;
        private set => SetProperty(ref _pluginStatus, value);
    }
    public bool IsWorkMode => _state.InteractionMode != "chat";
    public bool IsChatMode => !IsWorkMode;
    public bool CanSwitchSession => !IsBusy;
    public bool HasSessions => Workspaces.Any(workspace => workspace.Sessions.Count > 0);
    public bool ShowNoSessions => !HasSessions && string.IsNullOrWhiteSpace(SearchText);
    public bool ShowNoSearchResults => !string.IsNullOrWhiteSpace(SearchText) && !Workspaces.Any(workspace => workspace.IsVisible);
    public string ActiveModeLabel => IsWorkMode ? "Work" : "Chat";
    public string SessionTitle
    {
        get => _sessionTitle;
        private set
        {
            if (SetProperty(ref _sessionTitle, value))
            {
                OnPropertyChanged(nameof(HeaderTitle));
            }
        }
    }
    public string HeaderTitle => IsChatPage ? SessionTitle : SectionTitle;
    public string SectionTitle => IsLibraryPage ? "资料库" : IsProjectsPage ? "项目" : IsPluginsPage ? "插件" : IsAutomationPage ? "自动化任务" : IsSettingsPage ? "设置" : "聊天";
    public string WorkspaceName => _activeWorkspace.Name;
    public string WorkspacePath => _activeWorkspace.Path;
    public string ModelName => _core.IsSessionOpen
        ? _core.Model
        : string.IsNullOrWhiteSpace(CoreCatalogModel) ? _state.Model : CoreCatalogModel;

    /// <summary>Codex-style provider/model label for the composer picker.</summary>
    public string ProviderModelLabel
    {
        get
        {
            var provider = _core.IsSessionOpen ? _core.Provider : CoreCatalogProvider;
            var model = _core.IsSessionOpen ? _core.Model : CoreCatalogModel;
            if (string.IsNullOrWhiteSpace(model))
            {
                return string.IsNullOrWhiteSpace(_state.Model) ? "未连接" : _state.Model;
            }

            if (string.IsNullOrWhiteSpace(provider))
            {
                return PrettifyWords(model);
            }

            var normalizedProvider = PrettifyWords(provider);
            return model.StartsWith(provider, StringComparison.OrdinalIgnoreCase)
                ? PrettifyWords(model)
                : $"{normalizedProvider} {PrettifyWords(model)}";
        }
    }

    private static string PrettifyWords(string value)
    {
        var parts = value
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return value;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }
            builder.Append(char.ToUpperInvariant(part[0]));
            if (part.Length > 1)
            {
                builder.Append(part[1..]);
            }
        }
        return builder.ToString();
    }
    public string PermissionMode => _state.PermissionMode;
    public int ContentFontSize => _state.ContentFontSize;

    public string Composer
    {
        get => _composer;
        set
        {
            if (SetProperty(ref _composer, value))
            {
                OnPropertyChanged(nameof(CanSend));
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplySearch();
                OnPropertyChanged(nameof(ShowNoSessions));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanSend));
                OnPropertyChanged(nameof(CanSwitchSession));
                OnPropertyChanged(nameof(CanCancel));
            }
        }
    }

    public bool CanSend => !IsBusy
        && !IsCoreReadOnly
        && (!string.IsNullOrWhiteSpace(Composer) || HasPendingAttachments);
    public bool HasPendingAttachments => PendingAttachments.Count > 0;
    public bool CanCancel => IsBusy;

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public string SettingsPermissionMode
    {
        get => _settingsPermissionMode;
        set => SetProperty(ref _settingsPermissionMode, value);
    }

    public int SettingsContentFontSize
    {
        get => _settingsContentFontSize;
        set => SetProperty(ref _settingsContentFontSize, value);
    }

    public string SettingsStatus
    {
        get => _settingsStatus;
        private set
        {
            if (SetProperty(ref _settingsStatus, value))
            {
                OnPropertyChanged(nameof(HasSettingsStatus));
            }
        }
    }

    public string RenameTitle
    {
        get => _renameTitle;
        set => SetProperty(ref _renameTitle, value);
    }

    public string DeletePrompt
    {
        get => _deletePrompt;
        private set => SetProperty(ref _deletePrompt, value);
    }

    public string RenameDialogTitle
    {
        get => _renameWorkspaceTarget is null ? "重命名会话" : "重命名项目";
    }

    public void Navigate(AppSection section)
    {
        if (section == AppSection.Settings)
        {
            OpenSettings();
            return;
        }

        _isChatPage = section == AppSection.Chat;
        _isLibraryPage = section == AppSection.Library;
        _isCodeReviewDraftActive = false;
        _isProjectsPage = section == AppSection.Projects;
        _isPluginsPage = section == AppSection.Plugins;
        _isAutomationPage = section == AppSection.Automation;
        _isSettingsPage = false;
        IsSettingsOpen = false;
        OnPropertyChanged(nameof(IsChatPage));
        OnPropertyChanged(nameof(IsLibraryPage));
        OnPropertyChanged(nameof(IsProjectsPage));
        OnPropertyChanged(nameof(IsPluginsPage));
        OnPropertyChanged(nameof(IsAutomationPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(ShowConversationHeader));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(SectionTitle));
        UpdatePinnedRailActiveState();
        if (section == AppSection.Plugins)
        {
            RefreshPlugins();
        }
        if (section == AppSection.Automation)
        {
            foreach (var automation in Automations)
            {
                automation.NotifyStateChanged();
            }
            ApplyAutomationFilter();
        }
    }

    public void ToggleAddProjectPinned()
    {
        _state.IsAddProjectPinned = !_state.IsAddProjectPinned;
        SaveState();
        OnPropertyChanged(nameof(IsAddProjectPinned));
        RefreshPinnedRailItems();
    }

    /// <summary>Codex-style pinned shortcuts below the rail divider.</summary>
    public void RefreshPinnedRailItems()
    {
        var desired = new List<PinnedRailItemViewModel>(2);
        if (_state.IsAddProjectPinned)
        {
            desired.Add(new PinnedRailItemViewModel(isProject: true, label: "项目")
            {
                IsActive = IsProjectsPage
            });
        }

        if (_state.IsCodeReviewPinned)
        {
            desired.Add(new PinnedRailItemViewModel(isProject: false, label: "代码审查")
            {
                IsActive = _isCodeReviewDraftActive
            });
        }

        if (PinnedRailItems.Count == desired.Count
            && PinnedRailItems.Zip(desired).All(pair => pair.First.IsProject == pair.Second.IsProject))
        {
            UpdatePinnedRailActiveState();
            return;
        }

        PinnedRailItems.Clear();
        foreach (var item in desired)
        {
            PinnedRailItems.Add(item);
        }
    }

    /// <summary>Keeps the pinned rail icons highlighted for the current section.</summary>
    public void UpdatePinnedRailActiveState()
    {
        foreach (var item in PinnedRailItems)
        {
            item.IsActive = item.IsProject ? IsProjectsPage : _isCodeReviewDraftActive;
        }
    }

    public void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    public void ToggleSearch() => IsSearchVisible = !IsSearchVisible;

    // ---- Codex-style notifications pane and command palette ----
    private bool _isNotificationsVisible;
    private bool _isSearchPaletteOpen;

    public bool IsNotificationsVisible
    {
        get => _isNotificationsVisible;
        private set => SetProperty(ref _isNotificationsVisible, value);
    }

    public bool IsSearchPaletteOpen
    {
        get => _isSearchPaletteOpen;
        private set => SetProperty(ref _isSearchPaletteOpen, value);
    }

    public ObservableCollection<NotificationItemViewModel> Notifications { get; } = [];
    public ObservableCollection<SessionItemViewModel> PaletteSessions { get; } = [];
    public bool HasNotifications => Notifications.Count > 0;
    public bool ShowNoNotifications => Notifications.Count == 0;

    public void ToggleNotifications()
    {
        if (!IsNotificationsVisible)
        {
            IsSearchPaletteOpen = false;
            RefreshNotifications();
        }

        IsNotificationsVisible = !IsNotificationsVisible;
    }

    public void OpenSearchPalette()
    {
        IsNotificationsVisible = false;
        RefreshPaletteSessions();
        IsSearchPaletteOpen = true;
    }

    public void CloseSearchPalette() => IsSearchPaletteOpen = false;

    public void RefreshNotifications()
    {
        Notifications.Clear();
        var today = DateTimeOffset.Now.Date;
        var sessions = Workspaces
            .SelectMany(workspace => workspace.Sessions)
            .Where(session => !session.IsArchived)
            .OrderByDescending(session => session.State.UpdatedAt)
            .Take(12)
            .ToList();
        string? lastGroup = null;
        foreach (var session in sessions)
        {
            var date = session.State.UpdatedAt.ToLocalTime().Date;
            var group = date == today
                ? "今天"
                : date == today.AddDays(-1) ? "昨天" : date.ToString("MM-dd");
            Notifications.Add(new NotificationItemViewModel(session, group, group != lastGroup));
            lastGroup = group;
        }

        OnPropertyChanged(nameof(HasNotifications));
        OnPropertyChanged(nameof(ShowNoNotifications));
    }

    public void RefreshPaletteSessions()
    {
        var query = SearchText.Trim();
        var list = Workspaces
            .SelectMany(workspace => workspace.Sessions)
            .Where(session => !session.IsArchived)
            .Where(session => query.Length == 0 || session.Matches(query))
            .OrderByDescending(session => session.State.UpdatedAt)
            .Take(8)
            .ToList();
        SyncList(PaletteSessions, list);
    }

    public void ToggleRecent() => IsRecentExpanded = !IsRecentExpanded;

    /// <summary>Codex-style edit action: put a user message back into the composer.</summary>
    public void BeginEditUserMessage(ChatItemViewModel item)
    {
        if (!item.IsUser)
        {
            return;
        }

        Composer = item.Text;
    }

    public void RefreshRecentSessions()
    {
        var query = SearchText.Trim();
        var all = Workspaces
            .SelectMany(workspace => workspace.Sessions)
            .Where(item => query.Length == 0 || item.Matches(query))
            .ToList();

        var items = all
            .Where(item => !item.IsArchived)
            .OrderByDescending(item => item.IsPinned)
            .ThenByDescending(item => IsSessionSortByCreated ? item.State.CreatedAt : item.State.UpdatedAt)
            .Take(100)
            .ToList();

        var archived = all
            .Where(item => item.IsArchived)
            .OrderByDescending(item => item.State.UpdatedAt)
            .Take(100)
            .ToList();

        // Rebuilding the observable collection on every save makes Avalonia's
        // item container generator re-render identical rows; only publish a
        // change when the visible sequence actually differs.
        SyncList(RecentSessions, items);
        SyncList(ArchivedSessions, archived);

        OnPropertyChanged(nameof(HasRecentSessions));
        OnPropertyChanged(nameof(ShowNoRecentSessions));
        OnPropertyChanged(nameof(ShowArchivedSessions));
    }

    private static void SyncList(ObservableCollection<SessionItemViewModel> target, List<SessionItemViewModel> items)
    {
        var unchanged = items.Count == target.Count;
        if (unchanged)
        {
            for (var index = 0; index < items.Count; index++)
            {
                if (!ReferenceEquals(items[index], target[index]))
                {
                    unchanged = false;
                    break;
                }
            }
        }

        if (unchanged)
        {
            return;
        }

        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    public void ToggleSessionPinned(SessionItemViewModel session)
    {
        session.IsPinned = !session.IsPinned;
        SaveState();
    }

    public void ArchiveSession(SessionItemViewModel session)
    {
        session.IsArchived = true;
        SaveState();
    }

    public void UnarchiveSession(SessionItemViewModel session)
    {
        session.IsArchived = false;
        SaveState();
    }

    public void ToggleCodeReviewPinned()
    {
        _state.IsCodeReviewPinned = !_state.IsCodeReviewPinned;
        SaveState();
        OnPropertyChanged(nameof(IsCodeReviewPinned));
        RefreshPinnedRailItems();
    }

    public void StartCodeReviewDraft()
    {
        Navigate(AppSection.Chat);
        _ = CloseCoreSessionQuietlyAsync();
        StartDraftSession();
        Composer = "请审查当前项目的代码变更，重点检查潜在 bug、回归风险、安全问题和缺失测试。";
        _isCodeReviewDraftActive = true;
        UpdatePinnedRailActiveState();
    }

    public void OpenSettings(SettingsPane pane = SettingsPane.General)
    {
        RefreshSettingsDraft();
        SelectSettingsPane(pane);
        IsSettingsOpen = true;
    }

    public void CloseSettings() => IsSettingsOpen = false;

    public void SelectSettingsPane(SettingsPane pane)
    {
        _settingsPane = pane;
        OnPropertyChanged(nameof(IsSettingsGeneral));
        OnPropertyChanged(nameof(IsSettingsModels));
        OnPropertyChanged(nameof(IsSettingsData));
        OnPropertyChanged(nameof(IsSettingsAbout));
        OnPropertyChanged(nameof(SettingsPaneTitle));
        OnPropertyChanged(nameof(SettingsPaneSubtitle));
    }

    public void SetAutomationFilter(AutomationFilter filter)
    {
        _automationFilter = filter;
        OnPropertyChanged(nameof(IsAutomationFilterAll));
        OnPropertyChanged(nameof(IsAutomationFilterActive));
        OnPropertyChanged(nameof(IsAutomationFilterEnded));
        ApplyAutomationFilter();
    }

    public void SetInteractionMode(bool workMode)
    {
        var value = workMode ? "work" : "chat";
        if (_state.InteractionMode == value)
        {
            return;
        }
        _state.InteractionMode = value;
        OnPropertyChanged(nameof(IsWorkMode));
        OnPropertyChanged(nameof(IsChatMode));
        OnPropertyChanged(nameof(ActiveModeLabel));
        SaveState();
    }

    public async Task SelectWorkspaceAsync(WorkspaceItemViewModel workspace)
    {
        if (IsBusy || (workspace.Id == _activeWorkspace.Id && _activeSession is null))
        {
            return;
        }

        _activeWorkspace.IsSelected = false;
        _activeWorkspace = workspace;
        _activeWorkspace.IsSelected = true;
        _activeWorkspace.IsExpanded = true;
        _state.ActiveWorkspaceId = workspace.Id;
        OnPropertyChanged(nameof(WorkspaceName));
        OnPropertyChanged(nameof(WorkspacePath));

        await CloseCoreSessionQuietlyAsync().ConfigureAwait(true);
        var recent = workspace.Sessions.FirstOrDefault();
        if (recent is null)
        {
            StartDraftSession();
        }
        else
        {
            await OpenSessionAsync(recent, save: false).ConfigureAwait(true);
        }

        SaveState();
    }

    public void ToggleWorkspace(WorkspaceItemViewModel workspace)
    {
        workspace.IsExpanded = !workspace.IsExpanded;
        SaveState();
    }

    /// <summary>Pin a project (Codex "置顶"): pinned projects sort above the rest.</summary>
    public void ToggleWorkspacePinned(WorkspaceItemViewModel workspace)
    {
        workspace.IsPinned = !workspace.IsPinned;
        var ordered = Workspaces.OrderByDescending(item => item.IsPinned).ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var current = Workspaces.IndexOf(ordered[index]);
            if (current != index)
            {
                Workspaces.Move(current, index);
            }
        }

        _state.Workspaces = ordered.Select(item => item.State).ToList();
        SaveState();
    }

    /// <summary>Archive every conversation of a project (Codex "归档聊天").</summary>
    public void ArchiveWorkspaceChats(WorkspaceItemViewModel workspace)
    {
        foreach (var session in workspace.Sessions)
        {
            session.IsArchived = true;
        }

        RefreshRecentSessions();
        SaveState();
    }

    /// <summary>Remove a project from the sidebar (Codex "移除项目").</summary>
    public void RemoveWorkspace(WorkspaceItemViewModel workspace)
    {
        if (Workspaces.Count <= 1)
        {
            return;
        }

        _state.Workspaces.Remove(workspace.State);
        Workspaces.Remove(workspace);
        OnPropertyChanged(nameof(WorkspaceCount));
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowNoSessions));
        OnPropertyChanged(nameof(SavedSessionCount));

        if (_activeWorkspace.Id == workspace.Id)
        {
            workspace.IsSelected = false;
            var next = Workspaces.FirstOrDefault();
            if (next is not null)
            {
                _activeWorkspace = next;
                _activeWorkspace.IsSelected = true;
                _activeWorkspace.IsExpanded = true;
                _state.ActiveWorkspaceId = next.Id;
                OnPropertyChanged(nameof(WorkspaceName));
                OnPropertyChanged(nameof(WorkspacePath));
                var recent = next.Sessions.FirstOrDefault();
                if (recent is null)
                {
                    StartDraftSession();
                }
                else
                {
                    SelectSession(next, recent.State);
                }
            }
        }

        RefreshRecentSessions();
        SaveState();
    }

    public bool AddWorkspace(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ErrorMessage = "工作区路径无效。";
            return false;
        }

        if (!Directory.Exists(fullPath))
        {
            ErrorMessage = "工作区目录不存在。";
            return false;
        }

        var existing = Workspaces.FirstOrDefault(workspace =>
            string.Equals(workspace.Path, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _ = SelectWorkspaceAsync(existing);
            return true;
        }

        var name = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = fullPath;
        }
        var state = new WorkspaceState
        {
            Name = name,
            Path = fullPath,
            IsExpanded = true
        };
        _state.Workspaces.Add(state);
        var workspaceVm = new WorkspaceItemViewModel(state);
        Workspaces.Add(workspaceVm);
        OnPropertyChanged(nameof(WorkspaceCount));
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowNoSessions));
        _ = SelectWorkspaceAsync(workspaceVm);
        SaveState();
        return true;
    }

    public async Task CancelAsync()
    {
        if (!IsBusy)
        {
            return;
        }

        _turnCancellation?.Cancel();
        try
        {
            await _core.CancelAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception error) when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            ErrorMessage = $"取消请求失败：{error.Message}";
        }
    }

    public async Task SendAsync()
    {
        if (!CanSend)
        {
            return;
        }

        var prompt = Composer.Trim();
        var attachments = PendingAttachments.ToList();
        var modelPrompt = BuildModelPrompt(prompt, attachments);
        var displayPrompt = BuildDisplayPrompt(prompt, attachments);
        ErrorMessage = string.Empty;
        SettingsStatus = string.Empty;
        _activeToolItem = null;
        _activeReasoning = null;
        _activeAssistant = null;

        if (!await EnsureCoreSessionAsync(CancellationToken.None).ConfigureAwait(true))
        {
            // Keep the draft text so a failed core start does not lose user input.
            return;
        }

        if (IsCoreReadOnly)
        {
            ErrorMessage = "另一客户端持有该会话的写权限，当前窗口只能观察。";
            return;
        }

        Composer = string.Empty;
        ClearAttachments();

        var session = _activeSession;
        if (session is null)
        {
            ErrorMessage = "当前没有可用会话。";
            return;
        }

        var isFirstMessage = session.Messages.Count == 0;
        if (isFirstMessage)
        {
            session.Title = prompt.Length == 0 ? "附件会话" : TitleFromPrompt(prompt);
        }
        SessionTitle = session.Title;
        session.UpdatedAt = DateTimeOffset.Now;
        _activeSessionItem?.RefreshTitle();
        _activeSessionItem?.RefreshTime();

        var userItem = ChatItemViewModel.User(displayPrompt, modelPrompt, DateTimeOffset.Now);
        Messages.Add(userItem);
        SyncStoredMessages();
        UpdateSessionOrder();
        SaveState();

        IsBusy = true;
        _turnCancellation = new CancellationTokenSource();
        try
        {
            var outcome = await _core.RunTurnAsync(modelPrompt, _turnCancellation.Token).ConfigureAwait(true);
            if (outcome.Status == "failed" && !string.IsNullOrWhiteSpace(outcome.Text))
            {
                AddError(outcome.Text);
            }
        }
        catch (OperationCanceledException)
        {
            AddError("已停止生成本次回复。");
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or NotSupportedException
            or FileNotFoundException)
        {
            AddError(ToUserFacingError(error));
        }
        finally
        {
            if (IsBusy)
            {
                FinishTurn("完成");
            }
            _turnCancellation?.Dispose();
            _turnCancellation = null;
        }
    }

    public void BeginRename(SessionItemViewModel session)
    {
        if (IsBusy)
        {
            return;
        }
        _dialogTarget = session;
        _renameWorkspaceTarget = null;
        RenameTitle = session.Title;
        OnPropertyChanged(nameof(RenameDialogTitle));
        IsRenameDialogOpen = true;
    }

    public void BeginRenameWorkspace(WorkspaceItemViewModel workspace)
    {
        if (IsBusy)
        {
            return;
        }
        _dialogTarget = null;
        _renameWorkspaceTarget = workspace;
        RenameTitle = workspace.Name;
        OnPropertyChanged(nameof(RenameDialogTitle));
        IsRenameDialogOpen = true;
    }

    public void ConfirmRename()
    {
        if (string.IsNullOrWhiteSpace(RenameTitle))
        {
            return;
        }

        var title = RenameTitle.Trim();
        if (title.Length > 80)
        {
            title = title[..80];
        }

        if (_renameWorkspaceTarget is { } workspace)
        {
            workspace.State.Name = title;
            workspace.RefreshName();
            if (workspace.Id == _activeWorkspace.Id)
            {
                OnPropertyChanged(nameof(WorkspaceName));
            }
            CloseDialogs();
            SaveState();
            return;
        }

        if (_dialogTarget is null)
        {
            return;
        }

        _dialogTarget.Title = title;
        _dialogTarget.State.UpdatedAt = DateTimeOffset.Now;
        _dialogTarget.RefreshTime();
        if (_dialogTarget.Id == _activeSession?.Id)
        {
            SessionTitle = title;
        }
        CloseDialogs();
        UpdateSessionOrder();
        SaveState();
    }

    public void BeginDelete(SessionItemViewModel session)
    {
        if (IsBusy)
        {
            return;
        }
        _dialogTarget = session;
        DeletePrompt = $"删除“{session.Title}”？此操作无法撤销。";
        IsDeleteDialogOpen = true;
    }

    public void ConfirmDelete()
    {
        if (_dialogTarget is null)
        {
            return;
        }

        var target = _dialogTarget;
        target.Workspace.State.Sessions.Remove(target.State);
        target.Workspace.Sessions.Remove(target);
        if (_activeSession?.Id == target.Id)
        {
            _activeSession = null;
            _activeSessionItem = null;
            var next = target.Workspace.Sessions.FirstOrDefault();
            if (next is null)
            {
                StartDraftSession();
            }
            else
            {
                SelectSession(next.Workspace, next.State);
            }
        }
        CloseDialogs();
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowNoSessions));
        OnPropertyChanged(nameof(SavedSessionCount));
        SaveState();
    }

    public void CloseDialogs()
    {
        _dialogTarget = null;
        _renameWorkspaceTarget = null;
        IsRenameDialogOpen = false;
        IsDeleteDialogOpen = false;
        DeletePrompt = string.Empty;
        CloseAutomationDialogs();
    }

    public void AddAttachments(IEnumerable<string> paths)
    {
        ErrorMessage = string.Empty;
        var added = 0;
        var skipped = new List<string>();
        foreach (var candidate in paths)
        {
            if (PendingAttachments.Count >= MaxAttachmentCount)
            {
                skipped.Add($"最多附加 {MaxAttachmentCount} 个文件");
                break;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(candidate);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                skipped.Add(Path.GetFileName(candidate));
                continue;
            }

            if (PendingAttachments.Any(item => string.Equals(item.Path, fullPath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            var extension = Path.GetExtension(fullPath);
            if (!SupportedAttachmentExtensions.Contains(extension))
            {
                skipped.Add($"{Path.GetFileName(fullPath)}（暂不支持该格式）");
                continue;
            }
            try
            {
                var info = new FileInfo(fullPath);
                if (!info.Exists)
                {
                    skipped.Add($"{info.Name}（文件不存在）");
                    continue;
                }
                if (info.Length > MaxAttachmentCharacters * 4L)
                {
                    skipped.Add($"{info.Name}（文件过大）");
                    continue;
                }
                var content = File.ReadAllText(fullPath);
                if (content.Length > MaxAttachmentCharacters)
                {
                    content = content[..MaxAttachmentCharacters] + "\n\n[文件内容已截断]";
                }
                PendingAttachments.Add(new AttachmentItemViewModel(fullPath, info.Name, content));
                added++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                skipped.Add($"{Path.GetFileName(fullPath)}（{error.Message}）");
            }
        }

        OnPropertyChanged(nameof(HasPendingAttachments));
        OnPropertyChanged(nameof(CanSend));
        if (skipped.Count > 0)
        {
            ErrorMessage = "部分附件未添加：" + string.Join("；", skipped);
        }
        else if (added == 0 && PendingAttachments.Count == 0)
        {
            ErrorMessage = "没有可添加的文本附件。";
        }
    }

    public void RemoveAttachment(AttachmentItemViewModel attachment)
    {
        PendingAttachments.Remove(attachment);
        OnPropertyChanged(nameof(HasPendingAttachments));
        OnPropertyChanged(nameof(CanSend));
    }

    public void ClearAttachments()
    {
        if (PendingAttachments.Count == 0)
        {
            return;
        }
        PendingAttachments.Clear();
        OnPropertyChanged(nameof(HasPendingAttachments));
        OnPropertyChanged(nameof(CanSend));
    }

    private static string BuildModelPrompt(string prompt, IReadOnlyList<AttachmentItemViewModel> attachments)
    {
        if (attachments.Count == 0)
        {
            return prompt;
        }

        var builder = new System.Text.StringBuilder(prompt);
        foreach (var attachment in attachments)
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine($"<attachment name=\"{attachment.Name}\" path=\"{attachment.Path}\">");
            builder.AppendLine(attachment.Content);
            builder.AppendLine("</attachment>");
        }
        return builder.ToString().Trim();
    }

    private static string BuildDisplayPrompt(string prompt, IReadOnlyList<AttachmentItemViewModel> attachments)
    {
        if (attachments.Count == 0)
        {
            return prompt;
        }
        var names = string.Join("、", attachments.Select(item => item.Name));
        return string.IsNullOrWhiteSpace(prompt) ? $"附件：{names}" : $"{prompt}\n\n附件：{names}";
    }

    public void DismissError() => ErrorMessage = string.Empty;

    public void ReportError(string message) => ErrorMessage = message;

    public async Task SaveSettingsAsync()
    {
        if (!SupportedPermissionModes.Contains(SettingsPermissionMode))
        {
            SetSettingsStatus("权限模式无效。", isError: true);
            return;
        }

        _state.PermissionMode = SettingsPermissionMode;
        _state.ContentFontSize = SettingsContentFontSize;
        OnPropertyChanged(nameof(ContentFontSize));
        OnPropertyChanged(nameof(PermissionMode));
        SaveState();
        await ApplyPermissionModeAsync(CancellationToken.None).ConfigureAwait(true);
        SetSettingsStatus("设置已保存。审批模式由 KL Core 执行。", isError: false);
    }

    public async Task TestConnectionAsync()
    {
        if (IsTestingConnection)
        {
            return;
        }

        _isTestingConnection = true;
        OnPropertyChanged(nameof(IsTestingConnection));
        OnPropertyChanged(nameof(CanTestConnection));
        OnPropertyChanged(nameof(TestConnectionButtonText));
        SetSettingsStatus("正在检查 KL Core…", isError: false);
        try
        {
            var snapshot = await _core.LoadSnapshotAsync(CancellationToken.None).ConfigureAwait(true);
            if (!snapshot.Success)
            {
                SetSettingsStatus($"KL Core 不可用：{snapshot.Error}", isError: true);
                return;
            }

            ApplyCatalogSnapshot(snapshot);
            SetSettingsStatus(
                $"KL Core {snapshot.CoreVersion}（protocol {snapshot.ProtocolVersion}）连通正常。",
                isError: false);
        }
        finally
        {
            _isTestingConnection = false;
            OnPropertyChanged(nameof(IsTestingConnection));
            OnPropertyChanged(nameof(CanTestConnection));
            OnPropertyChanged(nameof(TestConnectionButtonText));
        }
    }

    public void ResetSettingsDefaults()
    {
        SettingsPermissionMode = "完全访问";
        SettingsContentFontSize = 14;
        SetSettingsStatus("已恢复默认值，点击保存后生效。", isError: false);
    }

    public void RefreshSettingsDraft()
    {
        SettingsPermissionMode = _state.PermissionMode;
        SettingsContentFontSize = _state.ContentFontSize;
        SetSettingsStatus(string.Empty, isError: false);
    }

    public void BeginCreateAutomation()
    {
        _automationDialogTarget = null;
        AutomationName = string.Empty;
        AutomationPrompt = string.Empty;
        SelectedAutomationInterval = AutomationIntervals[1];
        AutomationDialogStatus = string.Empty;
        IsAutomationDialogOpen = true;
        OnPropertyChanged(nameof(AutomationDialogTitle));
    }

    public void BeginEditAutomation(AutomationItemViewModel automation)
    {
        _automationDialogTarget = automation;
        AutomationName = automation.Name;
        AutomationPrompt = automation.Prompt;
        SelectedAutomationInterval = AutomationIntervals.FirstOrDefault(
            option => option.Minutes == automation.IntervalMinutes) ?? AutomationIntervals[1];
        AutomationDialogStatus = string.Empty;
        IsAutomationDialogOpen = true;
        OnPropertyChanged(nameof(AutomationDialogTitle));
    }

    public void SaveAutomation()
    {
        var name = AutomationName.Trim();
        var prompt = AutomationPrompt.Trim();
        if (name.Length == 0)
        {
            AutomationDialogStatus = "任务名称不能为空。";
            return;
        }
        if (prompt.Length == 0)
        {
            AutomationDialogStatus = "任务指令不能为空。";
            return;
        }
        if (SelectedAutomationInterval is null)
        {
            AutomationDialogStatus = "请选择运行间隔。";
            return;
        }

        if (name.Length > 80)
        {
            name = name[..80];
        }

        if (_automationDialogTarget is null)
        {
            var state = new AutomationState
            {
                Name = name,
                Prompt = prompt,
                IntervalMinutes = SelectedAutomationInterval.Minutes,
                IsEnabled = false,
                CreatedAt = DateTimeOffset.Now
            };
            _state.Automations.Add(state);
            Automations.Insert(0, new AutomationItemViewModel(state));
        }
        else
        {
            _automationDialogTarget.State.Name = name;
            _automationDialogTarget.State.Prompt = prompt;
            _automationDialogTarget.State.IntervalMinutes = SelectedAutomationInterval.Minutes;
            _automationDialogTarget.NotifyStateChanged();
        }

        CloseAutomationDialogs();
        SaveState();
        NotifyAutomationCollectionChanged();
    }

    public void CloseAutomationDialogs()
    {
        IsAutomationDialogOpen = false;
        IsAutomationDeleteDialogOpen = false;
        _automationDialogTarget = null;
        _automationDeleteTarget = null;
        AutomationDialogStatus = string.Empty;
        AutomationDeletePrompt = string.Empty;
    }

    public void ToggleAutomation(AutomationItemViewModel automation)
    {
        if (automation.IsRunning)
        {
            return;
        }
        automation.State.IsEnabled = !automation.State.IsEnabled;
        automation.State.NextRunAt = automation.State.IsEnabled
            ? DateTimeOffset.Now.AddMinutes(automation.IntervalMinutes)
            : null;
        automation.NotifyStateChanged();
        SaveState();
        NotifyAutomationCollectionChanged();
    }

    public void BeginDeleteAutomation(AutomationItemViewModel automation)
    {
        _automationDeleteTarget = automation;
        AutomationDeletePrompt = $"删除自动化“{automation.Name}”？历史运行结果也会一起删除。";
        IsAutomationDeleteDialogOpen = true;
    }

    public void ConfirmDeleteAutomation()
    {
        if (_automationDeleteTarget is null)
        {
            return;
        }
        _state.Automations.Remove(_automationDeleteTarget.State);
        Automations.Remove(_automationDeleteTarget);
        CloseAutomationDialogs();
        SaveState();
        NotifyAutomationCollectionChanged();
    }

    public async Task RunAutomationAsync(AutomationItemViewModel automation)
    {
        if (automation.IsRunning)
        {
            return;
        }

        automation.IsRunning = true;
        automation.State.LastStatus = "运行中…";
        automation.NotifyStateChanged();
        try
        {
            var result = await _core.RunEphemeralTurnAsync(
                _activeWorkspace.Path,
                automation.Prompt,
                CancellationToken.None).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(result))
            {
                throw new InvalidOperationException("模型没有返回可保存的正文结果。");
            }
            automation.State.LastResult = result.Length > 12000 ? result[..12000] : result;
            automation.State.LastStatus = "完成";
        }
        catch (Exception error)
        {
            automation.State.LastResult = error.Message;
            automation.State.LastStatus = "失败";
        }
        finally
        {
            var now = DateTimeOffset.Now;
            automation.State.LastRunAt = now;
            automation.State.NextRunAt = automation.State.IsEnabled
                ? now.AddMinutes(automation.IntervalMinutes)
                : null;
            automation.IsRunning = false;
            automation.NotifyStateChanged();
            SaveState();
            OnPropertyChanged(nameof(AutomationPageSummary));
        }
    }

    public async Task RunDueAutomationsAsync(DateTimeOffset? nowValue = null)
    {
        if (_automationSchedulerBusy)
        {
            return;
        }

        _automationSchedulerBusy = true;
        try
        {
            var now = nowValue ?? DateTimeOffset.Now;
            var due = Automations
                .Where(item => item.IsEnabled && item.State.NextRunAt is { } next && next <= now)
                .ToList();
            foreach (var automation in due)
            {
                await RunAutomationAsync(automation);
            }
        }
        finally
        {
            _automationSchedulerBusy = false;
        }
    }

    private void BuildAutomationItems()
    {
        Automations.Clear();
        foreach (var automation in _state.Automations.OrderByDescending(item => item.CreatedAt))
        {
            Automations.Add(new AutomationItemViewModel(automation));
        }
        NotifyAutomationCollectionChanged();
    }

    private void NotifyAutomationCollectionChanged()
    {
        ApplyAutomationFilter();
        OnPropertyChanged(nameof(HasAutomations));
        OnPropertyChanged(nameof(AutomationPageSummary));
        OnPropertyChanged(nameof(AutomationCount));
        OnPropertyChanged(nameof(AutomationEmptyTitle));
        OnPropertyChanged(nameof(AutomationEmptyDescription));
    }

    private void ApplyAutomationFilter()
    {
        var query = AutomationSearch.Trim();
        foreach (var automation in Automations)
        {
            var matchesFilter = _automationFilter switch
            {
                AutomationFilter.Active => automation.IsEnabled,
                AutomationFilter.Ended => !automation.IsEnabled,
                _ => true
            };
            automation.IsVisible = matchesFilter && automation.Matches(query);
        }
        OnPropertyChanged(nameof(ShowNoAutomations));
        OnPropertyChanged(nameof(AutomationEmptyTitle));
        OnPropertyChanged(nameof(AutomationEmptyDescription));
    }

    public bool ImportPlugin(string sourceDirectory)
    {
        try
        {
            var source = Path.GetFullPath(sourceDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!File.Exists(Path.Combine(source, "plugin.json"))
                && !File.Exists(Path.Combine(source, "manifest.json")))
            {
                ErrorMessage = "所选目录中没有 plugin.json 或 manifest.json。";
                return false;
            }

            Directory.CreateDirectory(PluginDirectoryPath);
            var managedRoot = Path.GetFullPath(PluginDirectoryPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (source.StartsWith(managedRoot, StringComparison.OrdinalIgnoreCase))
            {
                ErrorMessage = "该目录已经位于插件管理目录中。";
                return false;
            }
            var baseName = Path.GetFileName(source);
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = "plugin";
            }
            var target = Path.Combine(PluginDirectoryPath, baseName);
            var suffix = 2;
            while (Directory.Exists(target))
            {
                target = Path.Combine(PluginDirectoryPath, $"{baseName}-{suffix++}");
            }

            var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
            CopyDirectory(source, temporary);
            Directory.Move(temporary, target);
            RefreshPlugins();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ErrorMessage = $"导入插件失败：{error.Message}";
            return false;
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(target, relative));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
        }
    }

    public void RefreshPlugins()
    {
        Plugins.Clear();
        try
        {
            foreach (var plugin in _pluginCatalogService.Load())
            {
                Plugins.Add(new PluginItemViewModel(plugin));
            }
            PluginStatus = Plugins.Count == 0
                ? "尚未发现本地插件。将插件目录放入上方路径后点击刷新。"
                : $"已发现 {Plugins.Count} 个本地插件。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            PluginStatus = $"无法读取插件目录：{error.Message}";
        }
        OnPropertyChanged(nameof(HasPlugins));
        OnPropertyChanged(nameof(ShowNoPlugins));
        OnPropertyChanged(nameof(PluginDirectoryPath));
    }

    private void SetSettingsStatus(string message, bool isError)
    {
        SettingsStatus = message;
        _settingsStatusIsError = isError;
        OnPropertyChanged(nameof(HasSettingsStatus));
        OnPropertyChanged(nameof(IsSettingsStatusError));
        OnPropertyChanged(nameof(IsSettingsStatusSuccess));
    }


    private void NormalizeState()
    {
        _state.Endpoint = string.IsNullOrWhiteSpace(_state.Endpoint) ? "https://api.deepseek.com" : _state.Endpoint.Trim();
        _state.Model = string.IsNullOrWhiteSpace(_state.Model) ? "deepseek-chat" : _state.Model.Trim();
        _state.ApiKey ??= string.Empty;
        _state.ApiKeyEnvironmentVariable = _state.ApiKeyEnvironmentVariable?.Trim() ?? string.Empty;
        _state.PermissionMode = SupportedPermissionModes.Contains(_state.PermissionMode)
            ? _state.PermissionMode
            : "完全访问";
        _state.ContentFontSize = _state.ContentFontSize is >= 13 and <= 16 ? _state.ContentFontSize : 14;
        _state.InteractionMode = string.Equals(_state.InteractionMode, "chat", StringComparison.OrdinalIgnoreCase)
            ? "chat"
            : "work";
        _state.Workspaces ??= [];
        _state.Automations ??= [];

        // Sidebar width: match Codex's 238-468 range and migrate the old 284 default.
        if (Math.Abs(_state.SidebarWidth - 284) < 0.5 || _state.SidebarWidth < 238)
        {
            _state.SidebarWidth = 238;
        }

        _state.SidebarWidth = Math.Clamp(_state.SidebarWidth, 238, 468);

        foreach (var workspace in _state.Workspaces)
        {
            workspace.Id = string.IsNullOrWhiteSpace(workspace.Id) ? Guid.NewGuid().ToString("N") : workspace.Id;
            workspace.Path = workspace.Path?.Trim() ?? string.Empty;
            workspace.Name = string.IsNullOrWhiteSpace(workspace.Name)
                ? Path.GetFileName(workspace.Path)
                : workspace.Name;
            workspace.Sessions ??= [];
            workspace.Sessions.RemoveAll(session => session.Messages is null || session.Messages.Count == 0);
            foreach (var session in workspace.Sessions)
            {
                session.Id = string.IsNullOrWhiteSpace(session.Id) ? Guid.NewGuid().ToString("N") : session.Id;
                session.Title = string.IsNullOrWhiteSpace(session.Title) ? "新会话" : session.Title;
                session.Messages ??= [];
            }
        }

        if (_state.Workspaces.Count == 0)
        {
            var workspacePath = Environment.GetEnvironmentVariable("KL_GUI_WORKSPACE");
            if (string.IsNullOrWhiteSpace(workspacePath) && Directory.Exists(@"D:\kl"))
            {
                workspacePath = @"D:\kl";
            }
            workspacePath = string.IsNullOrWhiteSpace(workspacePath)
                ? Environment.CurrentDirectory
                : Path.GetFullPath(workspacePath);
            _state.Workspaces.Add(new WorkspaceState
            {
                Name = Path.GetFileName(workspacePath.TrimEnd(Path.DirectorySeparatorChar)) ?? "工作区",
                Path = workspacePath
            });
        }

        _state.ActiveWorkspaceId = _state.Workspaces.Any(workspace => workspace.Id == _state.ActiveWorkspaceId)
            ? _state.ActiveWorkspaceId
            : _state.Workspaces[0].Id;

        var now = DateTimeOffset.Now;
        foreach (var automation in _state.Automations)
        {
            automation.Id = string.IsNullOrWhiteSpace(automation.Id)
                ? Guid.NewGuid().ToString("N")
                : automation.Id;
            automation.Name = string.IsNullOrWhiteSpace(automation.Name) ? "未命名自动化" : automation.Name.Trim();
            automation.Prompt ??= string.Empty;
            automation.LastResult ??= string.Empty;
            automation.LastStatus ??= string.Empty;
            automation.IntervalMinutes = automation.IntervalMinutes < 15 ? 60 : automation.IntervalMinutes;
            if (automation.IsEnabled && (automation.NextRunAt is null || automation.NextRunAt <= now))
            {
                automation.NextRunAt = now.AddMinutes(automation.IntervalMinutes);
            }
            if (!automation.IsEnabled)
            {
                automation.NextRunAt = null;
            }
        }
    }

    private void BuildWorkspaceItems()
    {
        Workspaces.Clear();
        foreach (var workspaceState in _state.Workspaces)
        {
            Workspaces.Add(new WorkspaceItemViewModel(workspaceState));
        }
    }

    private bool TryFindSession(
        string? sessionId,
        out WorkspaceItemViewModel workspace,
        out SessionItemViewModel session)
    {
        foreach (var candidate in Workspaces)
        {
            var found = candidate.Sessions.FirstOrDefault(item => item.Id == sessionId);
            if (found is not null)
            {
                workspace = candidate;
                session = found;
                return true;
            }
        }
        workspace = null!;
        session = null!;
        return false;
    }

    private void SelectSession(WorkspaceItemViewModel workspace, SessionState session)
    {
        _isCodeReviewDraftActive = false;
        UpdatePinnedRailActiveState();
        foreach (var item in Workspaces.SelectMany(candidate => candidate.Sessions))
        {
            item.IsSelected = false;
        }

        if (_activeWorkspace.Id != workspace.Id)
        {
            _activeWorkspace.IsSelected = false;
            _activeWorkspace = workspace;
            _activeWorkspace.IsSelected = true;
        }
        _activeWorkspace.IsExpanded = true;
        _activeSession = session;
        _activeSessionItem = workspace.Sessions.First(item => item.Id == session.Id);
        _activeSessionItem.IsSelected = true;
        _state.ActiveWorkspaceId = workspace.Id;
        _state.ActiveSessionId = session.Id;
        SessionTitle = session.Title;
        Composer = string.Empty;
        ClearAttachments();
        LoadMessages(session);
        OnPropertyChanged(nameof(WorkspaceName));
        OnPropertyChanged(nameof(WorkspacePath));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowNoSessions));
    }

    private void StartDraftSession()
    {
        _isCodeReviewDraftActive = false;
        UpdatePinnedRailActiveState();
        foreach (var item in Workspaces.SelectMany(candidate => candidate.Sessions))
        {
            item.IsSelected = false;
        }
        _activeSessionItem = null;
        _activeSession = new SessionState
        {
            Title = "新会话",
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now
        };
        _state.ActiveWorkspaceId = _activeWorkspace.Id;
        _state.ActiveSessionId = null;
        SessionTitle = "新会话";
        Messages.Clear();
        Composer = string.Empty;
        ClearAttachments();
        OnPropertyChanged(nameof(WorkspaceName));
        OnPropertyChanged(nameof(WorkspacePath));
        OnPropertyChanged(nameof(HeaderTitle));
    }

    private void EnsureSessionPersisted()
    {
        if (_activeSession is null)
        {
            StartDraftSession();
        }
        if (_activeWorkspace.State.Sessions.Contains(_activeSession!))
        {
            return;
        }

        _activeWorkspace.State.Sessions.Add(_activeSession!);
        var item = new SessionItemViewModel(_activeWorkspace, _activeSession!);
        _activeWorkspace.Sessions.Insert(0, item);
        _activeSessionItem = item;
        _state.ActiveSessionId = _activeSession!.Id;
        item.IsSelected = true;
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowNoSessions));
        OnPropertyChanged(nameof(SavedSessionCount));
    }

    private void LoadMessages(SessionState session)
    {
        Messages.Clear();
        foreach (var message in session.Messages)
        {
            Messages.Add(ChatItemViewModel.FromStored(message));
        }
    }

    private void SyncStoredMessages()
    {
        if (_activeSession is null)
        {
            return;
        }
        _activeSession.Messages = Messages.Select(item => item.ToStored()).ToList();
    }

    private void UpdateSessionOrder()
    {
        if (_activeSession is null || _activeSessionItem is null)
        {
            return;
        }
        _activeSessionItem.Title = _activeSession.Title;
        _activeSessionItem.RefreshTitle();
        _activeSessionItem.RefreshTime();
        var ordered = _activeWorkspace.Sessions
            .OrderByDescending(item => item.State.UpdatedAt)
            .ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var oldIndex = _activeWorkspace.Sessions.IndexOf(ordered[index]);
            if (oldIndex != index)
            {
                _activeWorkspace.Sessions.Move(oldIndex, index);
            }
        }
    }

    private ChatItemViewModel EnsureReasoningItem()
    {
        if (_activeReasoning is not null)
        {
            return _activeReasoning;
        }
        _activeReasoning = ChatItemViewModel.Reasoning();
        _activeReasoning.Start();
        Messages.Add(_activeReasoning);
        return _activeReasoning;
    }

    private ChatItemViewModel EnsureAssistantItem()
    {
        if (_activeAssistant is not null)
        {
            return _activeAssistant;
        }
        _activeAssistant = ChatItemViewModel.Assistant(string.Empty);
        _activeAssistant.Start("生成中");
        Messages.Add(_activeAssistant);
        return _activeAssistant;
    }

    private void AddError(string message)
    {
        ErrorMessage = message;
        Messages.Add(ChatItemViewModel.Error(message));
    }

    private string ToUserFacingError(Exception error)
    {
        if (error is FileNotFoundException)
        {
            return "未找到 kl-core 可执行文件，请先构建 Core 或设置 KL_CORE_PATH。";
        }
        if (error is JsonRpcException rpc)
        {
            return $"KL Core 返回错误：{rpc.Message}";
        }
        if (error is IOException)
        {
            return $"与 KL Core 的连接中断：{error.Message}";
        }
        return error.Message;
    }

    private static string TitleFromPrompt(string prompt)
    {
        var singleLine = string.Join(" ", prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return singleLine.Length <= 36 ? singleLine : singleLine[..35] + "…";
    }

    private void ApplySearch()
    {
        var query = SearchText.Trim();
        foreach (var workspace in Workspaces)
        {
            workspace.ApplySearch(query);
        }
        RefreshRecentSessions();
        OnPropertyChanged(nameof(ShowNoSessions));
        OnPropertyChanged(nameof(ShowNoSearchResults));
    }

    private void SaveState(bool silent = false)
    {
        try
        {
            _stateStore.Save(_state);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (!silent)
            {
                ErrorMessage = $"无法保存本地状态：{error.Message}";
            }
        }

        RefreshRecentSessions();
    }
}

/// <summary>One Codex-style pinned shortcut rendered below the rail divider.</summary>
public sealed class PinnedRailItemViewModel(bool isProject, string label) : INotifyPropertyChanged
{
    private bool _isActive;

    public bool IsProject { get; } = isProject;

    public string Label { get; } = label;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
            {
                return;
            }

            _isActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
/// <summary>One row in the Codex-style notifications pane.</summary>
public sealed class NotificationItemViewModel
{
    public NotificationItemViewModel(SessionItemViewModel session, string groupLabel, bool showGroup)
    {
        Session = session;
        GroupLabel = groupLabel;
        ShowGroup = showGroup;
    }

    public SessionItemViewModel Session { get; }
    public string GroupLabel { get; }
    public bool ShowGroup { get; }
    public string Title => Session.Title;
    public string WorkspaceName => Session.Workspace.Name;
    public string RelativeLabel => Session.RelativeLabel;
    public string Preview => Session.State.Messages.LastOrDefault()?.Text ?? string.Empty;
}