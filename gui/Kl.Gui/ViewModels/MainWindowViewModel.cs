using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Kl.Gui.Models;
using Kl.Gui.Services;

namespace Kl.Gui.ViewModels;

public enum AppSection
{
    Chat,
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
    private bool _isRunning;
    private bool _isExpanded;

    private ChatItemViewModel(
        ChatItemKind kind,
        string header,
        string text = "",
        string detail = "",
        string summary = "",
        string status = "")
    {
        Kind = kind;
        _header = header;
        _text = text;
        _detail = detail;
        _summary = summary;
        _statusText = status;
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

    public static ChatItemViewModel User(string text, string? apiText = null) =>
        new(ChatItemKind.User, "你", text, detail: apiText ?? string.Empty);

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
            message.Status);
    }

    public StoredMessage ToStored() => new()
    {
        Kind = Kind.ToString().ToLowerInvariant(),
        Header = Header,
        Text = Text,
        Detail = Detail,
        Summary = Summary,
        Status = StatusText
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
    }

    public WorkspaceState State { get; }
    public ObservableCollection<SessionItemViewModel> Sessions { get; }
    public string Id => State.Id;
    public string Name => State.Name;
    public string Path => State.Path;

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

public sealed class MainWindowViewModel : ObservableObject
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
    private readonly IChatCompletionService _chatService;
    private readonly IConnectionTestService _connectionTestService;
    private readonly IPluginCatalogService _pluginCatalogService;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Dictionary<int, ChatItemViewModel> _activeTools = [];
    private AppState _state;
    private WorkspaceItemViewModel _activeWorkspace;
    private SessionState? _activeSession;
    private SessionItemViewModel? _activeSessionItem;
    private SessionItemViewModel? _dialogTarget;
    private CancellationTokenSource? _streamCancellation;
    private ChatItemViewModel? _activeReasoning;
    private ChatItemViewModel? _activeAssistant;
    private string _composer = string.Empty;
    private string _searchText = string.Empty;
    private string _sessionTitle = "新会话";
    private string _settingsEndpoint = string.Empty;
    private string _settingsModel = string.Empty;
    private string _settingsApiKey = string.Empty;
    private string _settingsPermissionMode = "完全访问";
    private int _settingsContentFontSize = 14;
    private string _settingsStatus = string.Empty;
    private string _renameTitle = string.Empty;
    private string _deletePrompt = string.Empty;
    private bool _isBusy;
    private bool _isChatPage = true;
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
        IChatCompletionService? chatService = null,
        Func<string, string?>? getEnvironmentVariable = null,
        IConnectionTestService? connectionTestService = null,
        IPluginCatalogService? pluginCatalogService = null)
    {
        _stateStore = stateStore ?? new StateStore();
        _chatService = chatService ?? new OpenAiCompatibleChatService();
        _connectionTestService = connectionTestService ?? new OpenAiCompatibleConnectionTestService();
        _pluginCatalogService = pluginCatalogService ?? new LocalPluginCatalogService();
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        _state = _stateStore.Load();
        NormalizeState();
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
        };
    }

    public ObservableCollection<WorkspaceItemViewModel> Workspaces { get; } = [];
    public ObservableCollection<ChatItemViewModel> Messages { get; } = [];
    public ObservableCollection<AttachmentItemViewModel> PendingAttachments { get; } = [];
    public ObservableCollection<PluginItemViewModel> Plugins { get; } = [];
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
    public bool IsProjectsPage => _isProjectsPage;
    public bool IsPluginsPage => _isPluginsPage;
    public bool IsAutomationPage => _isAutomationPage;
    public bool IsSettingsPage => _isSettingsPage;
    public bool IsAddProjectPinned => _state.IsAddProjectPinned;
    public bool IsCodeReviewPinned => _state.IsCodeReviewPinned;
    public bool ShowConversationHeader => IsChatPage && HasConversation;
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
        SettingsPane.Models => "填入 OpenAI 兼容接口与 API Key。",
        SettingsPane.Data => "会话、工作区、自动化与设置保存在本机状态文件中。",
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
    public string SectionTitle => IsProjectsPage ? "项目" : IsPluginsPage ? "插件" : IsAutomationPage ? "自动化任务" : IsSettingsPage ? "设置" : "聊天";
    public string WorkspaceName => _activeWorkspace.Name;
    public string WorkspacePath => _activeWorkspace.Path;
    public string ModelName => _state.Model;
    public string PermissionMode => _state.PermissionMode;
    public int ContentFontSize => _state.ContentFontSize;
    public bool HasStoredApiKey => !string.IsNullOrWhiteSpace(_state.ApiKey);
    public string ApiKeyStatus
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(SettingsApiKey))
            {
                if (HasStoredApiKey
                    && string.Equals(_state.ApiKey, SettingsApiKey.Trim(), StringComparison.Ordinal))
                {
                    return "API Key 已保存。";
                }
                return HasStoredApiKey
                    ? "API Key 已填写；保存后会更新本机密钥。"
                    : "API Key 已填写，保存后生效。";
            }
            return HasStoredApiKey
                ? "已保存 API Key；输入框留空时保存不会覆盖现有密钥。"
                : "尚未填写 API Key。";
        }
    }

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

    public bool CanSend => !IsBusy && (!string.IsNullOrWhiteSpace(Composer) || HasPendingAttachments);
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

    public string SettingsEndpoint
    {
        get => _settingsEndpoint;
        set => SetProperty(ref _settingsEndpoint, value);
    }

    public string SettingsModel
    {
        get => _settingsModel;
        set => SetProperty(ref _settingsModel, value);
    }

    public string SettingsApiKey
    {
        get => _settingsApiKey;
        set
        {
            if (SetProperty(ref _settingsApiKey, value))
            {
                OnPropertyChanged(nameof(ApiKeyStatus));
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

    public void Navigate(AppSection section)
    {
        if (section == AppSection.Settings)
        {
            OpenSettings();
            return;
        }

        _isChatPage = section == AppSection.Chat;
        _isProjectsPage = section == AppSection.Projects;
        _isPluginsPage = section == AppSection.Plugins;
        _isAutomationPage = section == AppSection.Automation;
        _isSettingsPage = false;
        IsSettingsOpen = false;
        OnPropertyChanged(nameof(IsChatPage));
        OnPropertyChanged(nameof(IsProjectsPage));
        OnPropertyChanged(nameof(IsPluginsPage));
        OnPropertyChanged(nameof(IsAutomationPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(ShowConversationHeader));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(SectionTitle));
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
    }

    public void ToggleCodeReviewPinned()
    {
        _state.IsCodeReviewPinned = !_state.IsCodeReviewPinned;
        SaveState();
        OnPropertyChanged(nameof(IsCodeReviewPinned));
    }

    public void StartCodeReviewDraft()
    {
        Navigate(AppSection.Chat);
        StartDraftSession();
        Composer = "请审查当前项目的代码变更，重点检查潜在 bug、回归风险、安全问题和缺失测试。";
    }

    public void OpenSettings(SettingsPane pane = SettingsPane.General)
    {
        RefreshSettingsDraft();
        RefreshApiKeyStatus();
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

    public void NewSession()
    {
        if (IsBusy)
        {
            return;
        }
        StartDraftSession();
        SaveState();
    }

    public void SelectWorkspace(WorkspaceItemViewModel workspace)
    {
        if (IsBusy || workspace.Id == _activeWorkspace.Id && _activeSession is null)
        {
            return;
        }

        _activeWorkspace.IsSelected = false;
        _activeWorkspace = workspace;
        _activeWorkspace.IsSelected = true;
        _activeWorkspace.IsExpanded = true;
        _state.ActiveWorkspaceId = workspace.Id;
        var recent = workspace.Sessions.FirstOrDefault();
        if (recent is null)
        {
            StartDraftSession();
        }
        else
        {
            OpenSession(recent, save: false);
        }
        OnPropertyChanged(nameof(WorkspaceName));
        OnPropertyChanged(nameof(WorkspacePath));
        SaveState();
    }

    public void ToggleWorkspace(WorkspaceItemViewModel workspace)
    {
        workspace.IsExpanded = !workspace.IsExpanded;
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
            SelectWorkspace(existing);
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
        SelectWorkspace(workspaceVm);
        SaveState();
        return true;
    }

    public void OpenSession(SessionItemViewModel session, bool save = true)
    {
        if (IsBusy)
        {
            return;
        }
        SelectSession(session.Workspace, session.State);
        if (save)
        {
            SaveState();
        }
    }

    public async Task CancelAsync()
    {
        if (!IsBusy)
        {
            return;
        }
        _streamCancellation?.Cancel();
        await Task.Yield();
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
        Composer = string.Empty;
        ClearAttachments();
        ErrorMessage = string.Empty;
        SettingsStatus = string.Empty;
        _activeTools.Clear();
        _activeReasoning = null;
        _activeAssistant = null;

        var session = _activeSession ?? throw new InvalidOperationException("当前没有可用会话。");
        EnsureSessionPersisted();
        var isFirstMessage = session.Messages.Count == 0;
        if (isFirstMessage)
        {
            session.Title = prompt.Length == 0 ? "附件会话" : TitleFromPrompt(prompt);
        }
        SessionTitle = session.Title;
        session.UpdatedAt = DateTimeOffset.Now;
        _activeSessionItem?.RefreshTime();

        var userItem = ChatItemViewModel.User(displayPrompt, modelPrompt);
        Messages.Add(userItem);
        SyncStoredMessages();
        UpdateSessionOrder();
        SaveState();

        var requestMessages = Messages
            .Where(item => item.IsUser || item.IsAssistant)
            .Where(item => item.HasText)
            .Select(item => new ChatRequestMessage(item.IsUser ? "user" : "assistant", item.ApiText))
            .ToList();

        IsBusy = true;
        _streamCancellation = new CancellationTokenSource();
        var cancelled = false;
        var receivedContent = false;

        try
        {
            var apiKey = ResolveApiKey();
            await foreach (var delta in _chatService.StreamAsync(
                new ChatCompletionSettings(_state.Endpoint, _state.Model, apiKey),
                requestMessages,
                _streamCancellation.Token))
            {
                if (delta.Reasoning.Length > 0)
                {
                    EnsureReasoningItem().AppendText(delta.Reasoning);
                }

                if (delta.Content.Length > 0)
                {
                    if (_activeReasoning is not null && _activeReasoning.IsRunning)
                    {
                        _activeReasoning.Complete("完成", "思考完成");
                    }
                    EnsureAssistantItem().AppendText(delta.Content);
                    receivedContent = true;
                }

                foreach (var toolCall in delta.ToolCalls ?? [])
                {
                    if (_activeReasoning is not null && _activeReasoning.IsRunning)
                    {
                        _activeReasoning.Complete("完成", "思考完成");
                    }
                    AppendToolCall(toolCall);
                }
            }

            if (_activeReasoning is not null && _activeReasoning.IsRunning)
            {
                _activeReasoning.Complete("完成", "思考完成");
            }
            CompleteTools();
            if (!receivedContent && _activeAssistant is null)
            {
                AddError("模型请求已完成，但没有返回可显示的回复。");
            }
            else
            {
                _activeAssistant?.Complete(string.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            _activeReasoning?.Stop("已停止");
            StopTools();
            if (_activeAssistant is { HasText: true })
            {
                _activeAssistant.Stop("已停止");
            }
            else
            {
                RemoveEmptyAssistant();
                AddError("已停止生成本次回复。");
            }
        }
        catch (Exception error)
        {
            _activeReasoning?.Stop("已停止");
            StopTools();
            RemoveEmptyAssistant();
            AddError(ToUserFacingError(error));
        }
        finally
        {
            var finishedAt = DateTimeOffset.Now;
            if (_activeAssistant?.HasText == true && !cancelled)
            {
                _activeAssistant.Complete(string.Empty);
            }
            session.UpdatedAt = finishedAt;
            _activeSessionItem?.RefreshTime();
            SyncStoredMessages();
            UpdateSessionOrder();
            IsBusy = false;
            _streamCancellation.Dispose();
            _streamCancellation = null;
            SaveState();
        }
    }

    public void BeginRename(SessionItemViewModel session)
    {
        if (IsBusy)
        {
            return;
        }
        _dialogTarget = session;
        RenameTitle = session.Title;
        IsRenameDialogOpen = true;
    }

    public void ConfirmRename()
    {
        if (_dialogTarget is null || string.IsNullOrWhiteSpace(RenameTitle))
        {
            return;
        }

        var title = RenameTitle.Trim();
        if (title.Length > 80)
        {
            title = title[..80];
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

    public void SaveSettings()
    {
        if (!TryReadValidatedSettings(
                out var endpoint,
                out var model,
                out var permissionMode))
        {
            return;
        }

        var apiKey = SettingsApiKey.Trim();
        if (apiKey.Length == 0 && !HasStoredApiKey)
        {
            SetSettingsStatus("API Key 不能为空。", isError: true);
            return;
        }

        if (apiKey.Length > 0)
        {
            _state.ApiKey = apiKey;
        }

        _state.Endpoint = endpoint;
        _state.Model = model;
        _state.ApiKeyEnvironmentVariable = string.Empty;
        _state.PermissionMode = permissionMode;
        _state.ContentFontSize = SettingsContentFontSize;
        OnPropertyChanged(nameof(ModelName));
        OnPropertyChanged(nameof(ContentFontSize));
        OnPropertyChanged(nameof(PermissionMode));
        OnPropertyChanged(nameof(HasStoredApiKey));
        RefreshApiKeyStatus();
        SaveState();
        SetSettingsStatus("设置已保存。", isError: false);
    }

    public async Task TestConnectionAsync()
    {
        if (IsTestingConnection)
        {
            return;
        }
        if (!TryReadValidatedSettings(
                out var endpoint,
                out var model,
                out _))
        {
            return;
        }

        var apiKey = SettingsApiKey.Trim();
        if (apiKey.Length == 0)
        {
            apiKey = ResolveApiKey();
        }
        if (apiKey.Length == 0)
        {
            SetSettingsStatus("请先填写 API Key。", isError: true);
            return;
        }

        _isTestingConnection = true;
        OnPropertyChanged(nameof(IsTestingConnection));
        OnPropertyChanged(nameof(CanTestConnection));
        OnPropertyChanged(nameof(TestConnectionButtonText));
        SetSettingsStatus("正在测试连接…", isError: false);
        try
        {
            var result = await _connectionTestService.TestAsync(
                new ChatCompletionSettings(endpoint, model, apiKey));
            SetSettingsStatus(result.Message, !result.Success);
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
        SettingsEndpoint = "https://api.deepseek.com";
        SettingsModel = "deepseek-chat";
        SettingsPermissionMode = "完全访问";
        SettingsContentFontSize = 14;
        SetSettingsStatus("已填入默认值，点击保存后生效。API Key 保持不变。", isError: false);
    }

    public void ClearStoredApiKey()
    {
        _state.ApiKey = string.Empty;
        _state.ApiKeyEnvironmentVariable = string.Empty;
        SettingsApiKey = string.Empty;
        OnPropertyChanged(nameof(HasStoredApiKey));
        RefreshApiKeyStatus();
        SaveState();
        SetSettingsStatus("已清除保存的 API Key。", isError: false);
    }

    public void RefreshSettingsDraft()
    {
        SettingsEndpoint = _state.Endpoint;
        SettingsModel = _state.Model;
        SettingsPermissionMode = _state.PermissionMode;
        SettingsContentFontSize = _state.ContentFontSize;
        SettingsApiKey = ResolveApiKey();
        SetSettingsStatus(string.Empty, isError: false);
        RefreshApiKeyStatus();
    }

    public void RefreshApiKeyStatus() => OnPropertyChanged(nameof(ApiKeyStatus));

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
            var apiKey = ResolveApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                throw new InvalidOperationException("未找到 API Key。请到设置页面填写并保存。");
            }

            var content = new System.Text.StringBuilder();
            await foreach (var delta in _chatService.StreamAsync(
                new ChatCompletionSettings(_state.Endpoint, _state.Model, apiKey),
                [new ChatRequestMessage("user", automation.Prompt)]))
            {
                content.Append(delta.Content);
            }

            var result = content.ToString().Trim();
            if (result.Length == 0)
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

    private string ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(_state.ApiKey))
        {
            return _state.ApiKey;
        }

        var environmentVariable = _state.ApiKeyEnvironmentVariable?.Trim();
        return string.IsNullOrWhiteSpace(environmentVariable)
            ? string.Empty
            : _getEnvironmentVariable(environmentVariable) ?? string.Empty;
    }

    private bool TryReadValidatedSettings(
        out string endpoint,
        out string model,
        out string permissionMode)
    {
        endpoint = SettingsEndpoint.Trim().TrimEnd('/');
        model = SettingsModel.Trim();
        permissionMode = SettingsPermissionMode;

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            SetSettingsStatus("Endpoint 必须是完整的 HTTP(S) 地址。", isError: true);
            return false;
        }
        if (string.IsNullOrWhiteSpace(model))
        {
            SetSettingsStatus("模型名称不能为空。", isError: true);
            return false;
        }
        if (!SupportedPermissionModes.Contains(permissionMode))
        {
            SetSettingsStatus("权限模式无效。", isError: true);
            return false;
        }
        return true;
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

    private void AppendToolCall(ChatToolCallDelta delta)
    {
        if (!_activeTools.TryGetValue(delta.Index, out var item))
        {
            item = ChatItemViewModel.Tool(
                string.IsNullOrWhiteSpace(delta.Name) ? "工具调用" : $"调用 {delta.Name}",
                "模型请求调用工具。",
                delta.Arguments ?? string.Empty,
                string.Empty);
            item.Start("调用中");
            _activeTools[delta.Index] = item;
            Messages.Add(item);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(delta.Name))
            {
                item.SetHeader($"调用 {delta.Name}");
            }
            item.AppendDetail(delta.Arguments ?? string.Empty);
        }
    }

    private void CompleteTools()
    {
        foreach (var item in _activeTools.Values)
        {
            if (item.IsRunning)
            {
                item.Complete("完成");
            }
        }
    }

    private void StopTools()
    {
        foreach (var item in _activeTools.Values)
        {
            if (item.IsRunning)
            {
                item.Stop("已停止");
            }
        }
    }

    private void RemoveEmptyAssistant()
    {
        if (_activeAssistant is { HasText: false })
        {
            Messages.Remove(_activeAssistant);
            _activeAssistant = null;
        }
    }

    private void AddError(string message)
    {
        ErrorMessage = message;
        Messages.Add(ChatItemViewModel.Error(message));
    }

    private string ToUserFacingError(Exception error)
    {
        if (error is HttpRequestException)
        {
            return $"无法连接模型服务：{error.Message}";
        }
        if (error is UriFormatException)
        {
            return "模型 Endpoint 格式无效，请到设置页检查。";
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
    }
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



