using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Kl.Client;
using Kl.Gui.Models;
using Kl.Gui.Services;
using Loc = Kl.Gui.Services.Localization;

namespace Kl.Gui.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    /// <summary>Codex-style transcript ruler ticks down the left edge of the chat column.</summary>
    private static readonly string[] SupportedPermissionModes = ["full", "auto", "ask"];
    private static readonly HashSet<string> SupportedAttachmentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".jsonl", ".cs", ".ts", ".tsx", ".js", ".jsx", ".css", ".html",
        ".xml", ".yaml", ".yml", ".ps1", ".py", ".java", ".go", ".rs", ".sql", ".toml", ".ini",
        ".log", ".csv", ".csproj", ".props", ".targets", ".sln"
    };
    private const int MaxAttachmentCount = 8;
    private const int MaxAttachmentCharacters = 100_000;
    private readonly string _homeDirectory;
    private string _agentsPath;
    private readonly StateStore _stateStore;
    private readonly ProviderCacheStore _providerCacheStore;
    /// <summary>An imported state file must survive until restart instead of being overwritten by autosave.</summary>
    private bool _stateImported;
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
    private string _sessionTitle = Loc.T("新会话");
    private string _settingsPermissionMode = "full";
    private int _settingsContentFontSize = 14;
    private string _settingsStatus = string.Empty;
    private string _settingsSelectedModel = string.Empty;
    private string _libraryDraftTitle = string.Empty;
    private string _doctorSummary = string.Empty;
    private bool _isDoctorRunning;
    private string _libraryDraftContent = string.Empty;
    private string _settingsApiKeyDraft = string.Empty;
    private string _renameTitle = string.Empty;
    private string _deletePrompt = string.Empty;
    private bool _isBusy;
    private bool _isSidebarVisible = true;
    private bool _isSearchVisible;
    private bool _isRecentExpanded = true;
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
    private readonly ImportDiscoveryService _importService;
    private bool _isImportScanning;
    private string _importStatusText = Loc.T("点击“扫描”查找 Codex 或 Claude 的说明文件与用户技能。");
    private string _pluginStatus = string.Empty;
    private string _pluginSearch = string.Empty;
    private IReadOnlyList<PluginItemViewModel> _allPlugins = [];
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
        Action<Action>? dispatcher = null,
        string? homeDirectory = null,
        ProviderCacheStore? providerCacheStore = null,
        ImportDiscoveryService? importService = null)
    {
        _homeDirectory = homeDirectory ?? KlDataPaths.Default.HomeDirectory;
        _importService = importService ?? new ImportDiscoveryService(klHome: _homeDirectory);
        _agentsPath = Path.Combine(_homeDirectory, "AGENTS.md");
        _stateStore = stateStore ?? new StateStore();
        _providerCacheStore = providerCacheStore
            ?? new ProviderCacheStore(Path.Combine(
                Path.GetDirectoryName(_stateStore.Path) ?? _homeDirectory,
                "providers-cache.json"));
        _pluginCatalogService = pluginCatalogService ?? new LocalPluginCatalogService();
        _core = coreSessionService ?? new UnavailableCoreSessionService();
        _dispatch = dispatcher ?? (static action => action());
        _core.EventReceived += OnCoreEventReceived;
        _state = _stateStore.Load();
        NormalizeState();
        HydrateProviderCache();
        BuildShortcutRows();
        LoadRemoteCoreSettings();
        RefreshPinnedRailItems();
        BuildWorkspaceItems();
        _activeWorkspace = Workspaces.First(workspace => workspace.Id == _state.ActiveWorkspaceId);
        _activeWorkspace.IsSelected = true;
        if (_state.RestoreLastSession
            && TryFindSession(_state.ActiveSessionId, out var activeWorkspace, out var activeSession))
        {
            SelectSession(activeWorkspace, activeSession.State);
        }
        else
        {
            StartDraftSession();
        }

        MemoryEntries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowMemoryEntries));
        UsageRows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowUsageRows));
        McpTools.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowMcpTools));
        RefreshSettingsDraft();
        RefreshPlugins();
        BuildAutomationItems();
        foreach (var item in _state.LibraryItems)
        {
            LibraryItems.Add(new LibraryItemViewModel(item));
        }
        RefreshHomeSuggestions();

        SaveState(silent: true);

        Messages.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasConversation));
            OnPropertyChanged(nameof(ShowConversationHeader));
            OnPropertyChanged(nameof(ShowWorkspaceStrip));
            OnPropertyChanged(nameof(ShowChatComposerChips));
            OnPropertyChanged(nameof(ComposerMargin));
            OnPropertyChanged(nameof(ComposerMaxWidth));
            OnPropertyChanged(nameof(ComposerPlaceholder));
        };

        _ = InitializeCoreAsync();
    }

    public ObservableCollection<WorkspaceItemViewModel> Workspaces { get; } = [];
    public ObservableCollection<ChatItemViewModel> Messages { get; } = [];
    public ObservableCollection<AttachmentItemViewModel> PendingAttachments { get; } = [];
    public ObservableCollection<PluginItemViewModel> Plugins { get; } = [];
    public ObservableCollection<ImportCandidateRow> ImportCandidates { get; } = [];
    public ObservableCollection<SessionItemViewModel> RecentSessions { get; } = [];
    public ObservableCollection<SessionItemViewModel> PinnedSessions { get; } = [];
    public ObservableCollection<SessionItemViewModel> ArchivedSessions { get; } = [];
    public ObservableCollection<PinnedRailItemViewModel> PinnedRailItems { get; } = [];
    public ObservableCollection<AutomationItemViewModel> Automations { get; } = [];
    public ObservableCollection<LibraryItemViewModel> LibraryItems { get; } = [];
    public ObservableCollection<LibraryItemViewModel> HomeSuggestions { get; } = [];
    public bool HasHomeSuggestions => HomeSuggestions.Count > 0;
    public IReadOnlyList<AutomationIntervalOption> AutomationIntervals { get; } =
    [
        new(15, Loc.T("每 15 分钟")),
        new(60, Loc.T("每小时")),
        new(360, Loc.T("每 6 小时")),
        new(1440, Loc.T("每天"))
    ];
    public IReadOnlyList<ReasoningEffortOption> ReasoningEffortOptions { get; } =
    [
        new("default", "default"),
        new("low", "low"),
        new("medium", "medium"),
        new("high", "high")
    ];


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
            return string.IsNullOrWhiteSpace(name) ? Loc.T("本地用户") : name;
        }
    }

    public string ComposerPlaceholder => HasQueuedTurns
        ? Loc.F("已排队 {0} 条；回复完成后自动发送", (QueuedTurnCount))
        : HasConversation
            ? IsChatMode ? Loc.T("和 kl 一起聊天") : Loc.T("和 kl 一起工作")
            : IsChatMode ? Loc.T("使用 Chat") : Loc.T("随心输入");

    /// <summary>Codex-style empty-state headline that follows the selected mode.</summary>
    public string EmptyStateTitle => IsChatMode ? Loc.T("我们要做什么?") : Loc.F("我们应该在{0}中做些什么?", (WorkspaceName));
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        private set => SetProperty(ref _isSettingsOpen, value);
    }
    public SettingsPane ActiveSettingsPane => _settingsPane;
    public bool IsSettingsGeneral => _settingsPane == SettingsPane.General;
    public bool IsSettingsNotifications => _settingsPane == SettingsPane.Notifications;
    public bool IsSettingsAppearance => _settingsPane == SettingsPane.Appearance;
    public bool IsSettingsMemory => _settingsPane == SettingsPane.Memory;
    public bool IsSettingsArchived => _settingsPane == SettingsPane.Archived;
    public bool IsSettingsShortcuts => _settingsPane == SettingsPane.Shortcuts;
    public bool IsSettingsProviders => _settingsPane == SettingsPane.Providers;
    public bool IsSettingsModels => _settingsPane == SettingsPane.Models;
    public bool IsSettingsUsage => _settingsPane == SettingsPane.Usage;
    public bool IsSettingsConfig => _settingsPane == SettingsPane.Config;
    public WorkspaceItemViewModel ActiveWorkspace => _activeWorkspace;

    public bool IsSettingsImport => _settingsPane == SettingsPane.Import;
    public bool IsSettingsCloudPc => _settingsPane == SettingsPane.CloudPc;
    public bool IsSettingsCodeReview => _settingsPane == SettingsPane.CodeReview;
    public bool IsSettingsStorage => _settingsPane == SettingsPane.Storage;
    public bool IsSettingsPersonalization => _settingsPane == SettingsPane.Personalization;
    public bool IsSettingsPlugins => _settingsPane == SettingsPane.Plugins;
    public bool IsSettingsPasswords => _settingsPane == SettingsPane.Passwords;
    public bool IsSettingsHooks => _settingsPane == SettingsPane.Hooks;
    public bool IsSettingsConnections => _settingsPane == SettingsPane.Connections;
    public bool IsSettingsGit => _settingsPane == SettingsPane.Git;
    public bool IsSettingsEnvironments => _settingsPane == SettingsPane.Environments;
    public bool IsSettingsWorktrees => _settingsPane == SettingsPane.Worktrees;
    public bool IsSettingsAudit => _settingsPane == SettingsPane.Audit;
    public string SettingsPaneTitle => _settingsPane switch
    {
        SettingsPane.Notifications => Loc.T("通知"),
        SettingsPane.Import => Loc.T("导入"),
        SettingsPane.Appearance => Loc.T("外观"),
        SettingsPane.Archived => Loc.T("已归档的聊天"),
        SettingsPane.Storage => Loc.T("存储空间"),
        SettingsPane.Config => Loc.T("配置"),
        SettingsPane.Personalization => Loc.T("个性化"),
        SettingsPane.Shortcuts => Loc.T("键盘快捷键"),
        SettingsPane.Providers => Loc.T("模型提供商"),
        SettingsPane.Usage => Loc.T("使用情况和计费"),
        SettingsPane.Plugins => Loc.T("插件"),
        SettingsPane.Passwords => Loc.T("密码"),
        SettingsPane.CloudPc => Loc.T("云电脑"),
        SettingsPane.Hooks => Loc.T("钩子"),
        SettingsPane.Connections => Loc.T("连接"),
        SettingsPane.CodeReview => Loc.T("代码审查"),
        SettingsPane.Git => "Git",
        SettingsPane.Environments => Loc.T("环境"),
        SettingsPane.Worktrees => "Worktrees",
        SettingsPane.Memory => Loc.T("记忆"),
        SettingsPane.Models => Loc.T("模型与连接"),
        SettingsPane.Audit => Loc.T("审计日志"),
        _ => Loc.T("常规")
    };
    public string SettingsPaneSubtitle => _settingsPane switch
    {
        SettingsPane.Import => Loc.T("将其他 AI 应用的设置、项目和聊天导入 KL"),
        SettingsPane.Config => Loc.T("配置新聊天的权限、网页访问和智能体回复"),
        SettingsPane.Plugins => Loc.T("管理插件、技能和 MCP"),
        SettingsPane.CloudPc => Loc.T("管理云端浏览设置和已保存的网站 Cookie"),
        SettingsPane.Hooks => Loc.T("通过配置和已启用的插件管理生命周期钩子。"),
        SettingsPane.Environments => Loc.T("本地环境用于指导 KL 如何为项目设置工作树。"),
        SettingsPane.Memory => Loc.T("用户记忆对所有项目生效；工作区记忆只属于当前项目。"),
        SettingsPane.Providers => Loc.T("连接你使用的模型提供商；请求地址、协议和密钥由 KL Core 管理。"),
        SettingsPane.Models => Loc.T("KL Core 执行所有模型调用；这里只保存默认选择和凭据。"),
        SettingsPane.Audit => Loc.T("KL Core 记录的最近工具调用与审批决策。"),
        _ => string.Empty
    };

    public bool HasSettingsPaneSubtitle => SettingsPaneSubtitle.Length > 0;

    public bool IsAutomationFilterAll => _automationFilter == AutomationFilter.All;
    public bool IsAutomationFilterActive => _automationFilter == AutomationFilter.Active;
    public bool IsAutomationFilterEnded => _automationFilter == AutomationFilter.Ended;
    public string AutomationEmptyTitle => Automations.Count == 0 ? Loc.T("还没有自动化任务") : Loc.T("没有匹配的自动化任务");
    public string AutomationEmptyDescription => Automations.Count == 0
        ? Loc.T("创建任务后可手动运行；启用后会按间隔自动执行。")
        : Loc.T("调整筛选条件或搜索关键词后再试。");
    public bool IsRenameDialogOpen
    {
        get => _isRenameDialogOpen;
        private set
        {
            if (SetProperty(ref _isRenameDialogOpen, value))
            {
                OnPropertyChanged(nameof(IsModalDialogOpen));
            }
        }
    }
    public bool IsDeleteDialogOpen
    {
        get => _isDeleteDialogOpen;
        private set
        {
            if (SetProperty(ref _isDeleteDialogOpen, value))
            {
                OnPropertyChanged(nameof(IsModalDialogOpen));
            }
        }
    }

    /// <summary>Any modal dialog owns the keyboard; the terminal must not eat its keys.</summary>
    public bool IsModalDialogOpen => IsRenameDialogOpen || IsDeleteDialogOpen || IsCloseConfirmOpen
        || IsCreateProjectDialogOpen || IsAutomationDialogOpen || IsAutomationDeleteDialogOpen
        || IsWorktreeDeleteConfirmOpen;

    private bool _isCloseConfirmOpen;
    public bool IsCloseConfirmOpen
    {
        get => _isCloseConfirmOpen;
        private set
        {
            if (SetProperty(ref _isCloseConfirmOpen, value))
            {
                OnPropertyChanged(nameof(IsModalDialogOpen));
            }
        }
    }

    public void OpenCloseConfirm() => IsCloseConfirmOpen = true;

    public void CloseCloseConfirm() => IsCloseConfirmOpen = false;

    private const int MaxAgentsFileCharacters = 64 * 1024;
    private bool _isAgentsEditorOpen;
    private string _agentsDraft = string.Empty;
    private string _agentsStatus = string.Empty;

    public bool IsAgentsEditorOpen
    {
        get => _isAgentsEditorOpen;
        private set => SetProperty(ref _isAgentsEditorOpen, value);
    }

    public string AgentsDraft
    {
        get => _agentsDraft;
        set
        {
            if (SetProperty(ref _agentsDraft, value))
            {
                SetAgentsStatus(string.Empty);
            }
        }
    }

    public string AgentsStatus => _agentsStatus;
    public bool HasAgentsStatus => _agentsStatus.Length > 0;
    public string AgentsPathText => _agentsPath;

    private string _agentsEditorHint = string.Empty;
    public string AgentsEditorHint
    {
        get => _agentsEditorHint;
        private set => SetProperty(ref _agentsEditorHint, value);
    }

    /// <summary>Opens the editor with the global instructions file, if present.</summary>
    public void OpenAgentsEditor() => OpenAgentsEditorAt(
        Path.Combine(_homeDirectory, "AGENTS.md"),
        Loc.T("全局指令会附加到每次请求的系统提示中，保存后下一个回合生效。"));

    /// <summary>Edits the selected project's AGENTS.md; Core reads it for turns in that workspace.</summary>
    public void OpenProjectAgentsEditor(WorkspaceItemViewModel workspace) => OpenAgentsEditorAt(
        Path.Combine(workspace.Path, "AGENTS.md"),
        Loc.F("项目指令会附加到 {0} 的每次请求中，保存后下一个回合生效。", (workspace.Name)));

    private void OpenAgentsEditorAt(string path, string hint)
    {
        _agentsPath = path;
        AgentsEditorHint = hint;
        OnPropertyChanged(nameof(AgentsPathText));
        try
        {
            SetProperty(ref _agentsDraft, File.Exists(_agentsPath) ? File.ReadAllText(_agentsPath) : string.Empty,
                nameof(AgentsDraft));
            SetAgentsStatus(string.Empty);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetProperty(ref _agentsDraft, string.Empty, nameof(AgentsDraft));
            SetAgentsStatus(Loc.F("无法读取 AGENTS.md：{0}", (error.Message)));
        }

        IsAgentsEditorOpen = true;
    }

    public void CloseAgentsEditor() => IsAgentsEditorOpen = false;

    private string _remoteCoreEndpointDraft = string.Empty;
    private string _remoteCoreTokenDraft = string.Empty;
    private string _remoteCoreTlsFingerprintDraft = string.Empty;
    private string _remoteCoreStatus = string.Empty;

    private string RemoteTokenPath => Path.Combine(_homeDirectory, "credentials", "core.token");

    public string RemoteCoreEndpointDraft
    {
        get => _remoteCoreEndpointDraft;
        set
        {
            if (SetProperty(ref _remoteCoreEndpointDraft, value))
            {
                SetRemoteCoreStatus(string.Empty);
            }
        }
    }

    public string RemoteCoreTokenDraft
    {
        get => _remoteCoreTokenDraft;
        set
        {
            if (SetProperty(ref _remoteCoreTokenDraft, value))
            {
                SetRemoteCoreStatus(string.Empty);
            }
        }
    }

    public string RemoteCoreTlsFingerprintDraft
    {
        get => _remoteCoreTlsFingerprintDraft;
        set
        {
            if (SetProperty(ref _remoteCoreTlsFingerprintDraft, value))
            {
                SetRemoteCoreStatus(string.Empty);
            }
        }
    }

    public string RemoteCoreStatus => _remoteCoreStatus;
    public bool HasRemoteCoreStatus => _remoteCoreStatus.Length > 0;

    private bool _isRemoteCoreTesting;
    public bool IsRemoteCoreTesting
    {
        get => _isRemoteCoreTesting;
        private set
        {
            if (SetProperty(ref _isRemoteCoreTesting, value))
            {
                OnPropertyChanged(nameof(CanTestRemoteCore));
            }
        }
    }

    public bool CanTestRemoteCore => !IsRemoteCoreTesting;

    /// <summary>Tests the draft endpoint/token without saving them.</summary>
    public async Task TestRemoteCoreConnectionAsync()
    {
        if (IsRemoteCoreTesting)
        {
            return;
        }

        var endpoint = _remoteCoreEndpointDraft.Trim();
        var token = _remoteCoreTokenDraft.Trim();
        var fingerprint = LocalSocketConnection.NormalizeFingerprint(_remoteCoreTlsFingerprintDraft);
        if (endpoint.Length > 0 && !LocalSocketConnection.TryParseTcp(endpoint, out _, out _))
        {
            SetRemoteCoreStatus(Loc.T("地址格式应为 tcp:// 或 tls://主机:端口。"));
            return;
        }
        if (endpoint.Length > 0 && token.Length == 0 && !IsLoopbackEndpoint(endpoint))
        {
            SetRemoteCoreStatus(Loc.T("连接非本机地址需要访问令牌。"));
            return;
        }
        if (!ValidateTlsFingerprint(endpoint, fingerprint))
        {
            return;
        }

        IsRemoteCoreTesting = true;
        SetRemoteCoreStatus(Loc.T("正在测试 KL Core 连接…"));
        try
        {
            if (endpoint.Length == 0)
            {
                var snapshot = await _core.LoadSnapshotAsync(CancellationToken.None).ConfigureAwait(true);
                SetRemoteCoreStatus(snapshot.Success
                    ? Loc.F("当前 Core 可用：{0}（协议 v{1}）", (snapshot.CoreVersion), (snapshot.ProtocolVersion))
                    : Loc.F("当前 Core 连接失败：{0}", (snapshot.Error)));
                return;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var client = await KlCoreClient.ConnectAsync(
                new KlClientOptions
                {
                    Endpoint = endpoint,
                    Token = token,
                    TlsFingerprint = fingerprint.Length == 0 ? null : fingerprint
                },
                timeout.Token).ConfigureAwait(true);
            await client.PingAsync("remote-core-test", timeout.Token).ConfigureAwait(true);
            SetRemoteCoreStatus(Loc.F("连接成功：{0}。保存并重启 KL 后使用此 Core。", (endpoint)));
        }
        catch (OperationCanceledException)
        {
            SetRemoteCoreStatus(Loc.T("连接超时：10 秒内没有收到响应。"));
        }
        catch (Exception error)
        {
            SetRemoteCoreStatus(Loc.F("连接失败：{0}", (error.Message)));
        }
        finally
        {
            IsRemoteCoreTesting = false;
        }
    }

    private void LoadRemoteCoreSettings()
    {
        _remoteCoreEndpointDraft = _state.RemoteCoreEndpoint ?? string.Empty;
        _remoteCoreTlsFingerprintDraft = _state.RemoteCoreTlsFingerprint ?? string.Empty;
        try
        {
            _remoteCoreTokenDraft = File.Exists(RemoteTokenPath)
                ? File.ReadAllText(RemoteTokenPath).Trim()
                : string.Empty;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _remoteCoreTokenDraft = string.Empty;
        }
    }

    /// <summary>Saves the remote endpoint; CoreSessionService picks it up on restart.</summary>
    public void SaveRemoteCoreSettings()
    {
        var endpoint = _remoteCoreEndpointDraft.Trim();
        if (endpoint.Length > 0 && !LocalSocketConnection.TryParseTcp(endpoint, out _, out _))
        {
            SetRemoteCoreStatus(Loc.T("地址格式应为 tcp:// 或 tls://主机:端口。"));
            return;
        }

        var fingerprint = LocalSocketConnection.NormalizeFingerprint(_remoteCoreTlsFingerprintDraft);
        if (!ValidateTlsFingerprint(endpoint, fingerprint))
        {
            return;
        }

        var token = _remoteCoreTokenDraft.Trim();
        if (endpoint.Length > 0 && token.Length == 0 && !IsLoopbackEndpoint(endpoint))
        {
            SetRemoteCoreStatus(Loc.T("连接非本机地址需要访问令牌。"));
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(RemoteTokenPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            if (token.Length == 0)
            {
                if (File.Exists(RemoteTokenPath))
                {
                    File.Delete(RemoteTokenPath);
                }
            }
            else
            {
                File.WriteAllText(RemoteTokenPath, token);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetRemoteCoreStatus(Loc.F("保存令牌失败：{0}", (error.Message)));
            return;
        }

        _state.RemoteCoreEndpoint = endpoint.Length == 0 ? null : endpoint;
        _state.RemoteCoreTlsFingerprint = fingerprint.Length == 0 ? null : fingerprint;
        SaveState();
        OnPropertyChanged(nameof(RemoteCoreConnectionLabel));
        RefreshCredentials();
        SetRemoteCoreStatus(Loc.T("已保存。重启 KL 后生效。"));
    }

    public void ClearRemoteCoreSettings()
    {
        _remoteCoreEndpointDraft = string.Empty;
        _remoteCoreTokenDraft = string.Empty;
        _remoteCoreTlsFingerprintDraft = string.Empty;
        OnPropertyChanged(nameof(RemoteCoreEndpointDraft));
        OnPropertyChanged(nameof(RemoteCoreTokenDraft));
        OnPropertyChanged(nameof(RemoteCoreTlsFingerprintDraft));
        _state.RemoteCoreEndpoint = null;
        _state.RemoteCoreTlsFingerprint = null;
        try
        {
            if (File.Exists(RemoteTokenPath))
            {
                File.Delete(RemoteTokenPath);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        SaveState();
        OnPropertyChanged(nameof(RemoteCoreConnectionLabel));
        RefreshCredentials();
        SetRemoteCoreStatus(Loc.T("已清除。重启 KL 后使用本地 Core。"));
    }

    /// <summary>tls:// needs a 64-character SHA-256 fingerprint; plain tcp:// must not carry one.</summary>
    private bool ValidateTlsFingerprint(string endpoint, string fingerprint)
    {
        if (fingerprint.Length > 0
            && (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit)))
        {
            SetRemoteCoreStatus(Loc.T("TLS 指纹应为 64 位十六进制 SHA-256。"));
            return false;
        }
        if (endpoint.StartsWith("tls://", StringComparison.OrdinalIgnoreCase)
            && fingerprint.Length == 0)
        {
            SetRemoteCoreStatus(Loc.T("tls:// 地址需要服务器启动时打印的证书指纹。"));
            return false;
        }
        return true;
    }

    private static bool IsLoopbackEndpoint(string endpoint)
    {
        if (!LocalSocketConnection.TryParseTcp(endpoint, out var host, out _))
        {
            return false;
        }
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (System.Net.IPAddress.TryParse(host, out var address)
                && System.Net.IPAddress.IsLoopback(address));
    }

    private void SetRemoteCoreStatus(string status)
    {
        if (_remoteCoreStatus == status)
        {
            return;
        }
        _remoteCoreStatus = status;
        OnPropertyChanged(nameof(RemoteCoreStatus));
        OnPropertyChanged(nameof(HasRemoteCoreStatus));
    }

    /// <summary>Writes the draft to the home AGENTS.md; Core reads it on the next turn.</summary>
    public void SaveAgents()
    {
        if (_agentsDraft.Length > MaxAgentsFileCharacters)
        {
            SetAgentsStatus(Loc.F("AGENTS.md 不能超过 {0} KiB。", (MaxAgentsFileCharacters / 1024)));
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_agentsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(_agentsPath, _agentsDraft);
            SetAgentsStatus(Loc.T("已保存。下一个回合生效。"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetAgentsStatus(Loc.F("保存失败：{0}", (error.Message)));
        }
    }

    private void SetAgentsStatus(string status)
    {
        if (_agentsStatus == status)
        {
            return;
        }
        _agentsStatus = status;
        OnPropertyChanged(nameof(AgentsStatus));
        OnPropertyChanged(nameof(HasAgentsStatus));
    }

    /// <summary>Localized chip label for the stored approval mode code.</summary>
    public string PermissionModeLabel => Loc.T(_state.PermissionMode switch
    {
        "auto" => Loc.T("帮我批准"),
        "ask" => Loc.T("请求批准"),
        _ => Loc.T("完全访问")
    });
    public int ContentFontSize => _state.ContentFontSize;

    /// <summary>Raised for a finished turn or a pending approval; the window decides about focus.</summary>
    public event Action<AttentionKind, bool>? AttentionRequested;

    internal void RequestAttention(AttentionKind kind) =>
        AttentionRequested?.Invoke(kind, _state.NotificationSound);

    public bool SettingsFullAccess
    {
        get => IsPermissionFull;
        set
        {
            if (value == IsPermissionFull)
            {
                return;
            }

            SetPermissionMode(value ? "full" : "auto");
            OnPropertyChanged();
        }
    }

    public string RemoteTokenStatusText { get; private set; } = Loc.T("未保存");

    /// <summary>Re-reads which credential files exist for the 密码 pane.</summary>
    public void RefreshCredentials()
    {
        RemoteTokenStatusText = File.Exists(RemoteTokenPath) ? Loc.T("已保存") : Loc.T("未保存");
        OnPropertyChanged(nameof(RemoteTokenStatusText));
    }

    public string TurnNotificationMode => _state.TurnNotifications;

    public int TurnNotificationIndex
    {
        get => _state.TurnNotifications switch
        {
            "always" => 1,
            "never" => 2,
            _ => 0
        };
        set
        {
            var mode = value switch
            {
                1 => "always",
                2 => "never",
                _ => "unfocused"
            };
            if (_state.TurnNotifications == mode)
            {
                return;
            }

            _state.TurnNotifications = mode;
            SaveState();
            OnPropertyChanged();
            OnPropertyChanged(nameof(TurnNotificationMode));
        }
    }


    public bool IsFollowUpQueue => _state.FollowUpMode != "steer";
    public bool IsFollowUpSteer => _state.FollowUpMode == "steer";

    public void SetFollowUpMode(string mode)
    {
        var value = mode == "steer" ? "steer" : "queue";
        if (_state.FollowUpMode == value)
        {
            return;
        }

        _state.FollowUpMode = value;
        SaveState();
        OnPropertyChanged(nameof(IsFollowUpQueue));
        OnPropertyChanged(nameof(IsFollowUpSteer));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(CanQueueSend));
    }

    private readonly Queue<QueuedTurn> _queuedTurns = new();
    private bool _sendingQueuedTurn;

    public int QueuedTurnCount => _queuedTurns.Count;
    public bool HasQueuedTurns => _queuedTurns.Count > 0;

    private void NotifyQueueChanged()
    {
        OnPropertyChanged(nameof(QueuedTurnCount));
        OnPropertyChanged(nameof(HasQueuedTurns));
        OnPropertyChanged(nameof(ComposerPlaceholder));
        OnPropertyChanged(nameof(CanSend));
    }

    private void QueueTurn(string prompt, IReadOnlyList<AttachmentItemViewModel> attachments)
    {
        _queuedTurns.Enqueue(new QueuedTurn(prompt, attachments.ToList()));
        Composer = string.Empty;
        ClearAttachments();
        NotifyQueueChanged();
    }

    /// <summary>
    /// Sends queued prompts one by one. The nested SendAsync call returns here
    /// because this loop already owns the drain; that is what keeps messages
    /// queued during a later turn from being stranded.
    /// </summary>
    private async Task DrainQueuedTurnsAsync()
    {
        if (_sendingQueuedTurn)
        {
            return;
        }

        _sendingQueuedTurn = true;
        try
        {
            while (!IsBusy && _queuedTurns.Count > 0)
            {
                var next = _queuedTurns.Dequeue();
                NotifyQueueChanged();
                Composer = next.Prompt;
                foreach (var attachment in next.Attachments)
                {
                    if (!PendingAttachments.Contains(attachment))
                    {
                        PendingAttachments.Add(attachment);
                    }
                }
                OnPropertyChanged(nameof(HasPendingAttachments));
                await SendAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            _sendingQueuedTurn = false;
            NotifyQueueChanged();
        }
    }

    private void ClearQueuedTurns()
    {
        if (_queuedTurns.Count == 0)
        {
            return;
        }

        _queuedTurns.Clear();
        NotifyQueueChanged();
    }


    public string Composer
    {
        get => _composer;
        set
        {
            if (SetProperty(ref _composer, value))
            {
                OnPropertyChanged(nameof(CanSend));
                OnPropertyChanged(nameof(CanQueueSend));
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
                RefreshPaletteSessions();
                RefreshPaletteFileSearch();
            }
        }
    }

    private ChatItemViewModel? _editingUserItem;
    private string? _rewindModelText;
    private ChatItemViewModel? _pendingUserItem;
    private bool _suppressRewindReload;

    public bool IsEditingUserMessage => _editingUserItem is not null;

    public string EditMessageBanner =>
        Loc.T("正在编辑消息：发送后会替换这条消息及其后的回复。工具的改动不会被撤销。");

    private void RaiseEditState()
    {
        OnPropertyChanged(nameof(IsEditingUserMessage));
        OnPropertyChanged(nameof(EditMessageBanner));
    }

    public void CancelEditUserMessage()
    {
        if (_editingUserItem is null)
        {
            return;
        }
        _editingUserItem = null;
        _rewindModelText = null;
        RaiseEditState();
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanSend));
                OnPropertyChanged(nameof(CanQueueSend));
            }
        }
    }

    public bool CanSend => (!IsBusy || IsFollowUpQueue)
        && !IsCoreReadOnly
        && !IsCoreRetrying
        && (!string.IsNullOrWhiteSpace(Composer) || HasPendingAttachments);
    public bool CanQueueSend => IsBusy
        && IsFollowUpQueue
        && !IsCoreReadOnly
        && !IsCoreRetrying
        && (!string.IsNullOrWhiteSpace(Composer) || HasPendingAttachments);
    public bool HasPendingAttachments => PendingAttachments.Count > 0;
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

    public string RenameDialogTitle => _renameWorkspaceTarget is null ? Loc.T("重命名会话") : Loc.T("编辑项目");

    public bool IsEditingWorkspace => _renameWorkspaceTarget is not null;
    public string RenameSourcePath => _renameWorkspaceTarget?.Path ?? string.Empty;
    public string RenameSourceParentPath => _renameWorkspaceTarget?.ParentPath ?? string.Empty;

    public void RemoveWorkspaceFromDialog()
    {
        if (_renameWorkspaceTarget is not { } workspace)
        {
            return;
        }

        CloseDialogs();
        RemoveWorkspace(workspace);
    }

    public void SetWorkspaceSourcePath(string path)
    {
        if (_renameWorkspaceTarget is not { } workspace || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        workspace.State.Path = path;
        RefreshWorkspacePathLabels();
        OnPropertyChanged(nameof(RenameSourcePath));
        OnPropertyChanged(nameof(RenameSourceParentPath));
        SaveState();
    }

    public void Navigate(AppSection section)
    {
        if (section == AppSection.Settings)
        {
            OpenSettings();
            return;
        }

        // Re-clicking the section already on screen would replay the page fade
        // and reset list state; keep it still.
        var sameSection = !IsSettingsOpen && section switch
        {
            AppSection.Chat => _isChatPage,
            AppSection.Library => _isLibraryPage,
            AppSection.Projects => _isProjectsPage,
            AppSection.Plugins => _isPluginsPage,
            AppSection.Automation => _isAutomationPage,
            _ => false
        };
        if (sameSection)
        {
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
        OnPropertyChanged(nameof(IsPluginsList));
        OnPropertyChanged(nameof(IsSkillsList));
        OnPropertyChanged(nameof(IsAutomationPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(ShowConversationHeader));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(SectionTitle));
        UpdatePinnedRailActiveState();
        if (section == AppSection.Plugins)
        {
            if (_isSkillsList)
            {
                _isSkillsList = false;
                OnPropertyChanged(nameof(IsSkillsList));
                OnPropertyChanged(nameof(IsPluginsList));
                OnPropertyChanged(nameof(SectionTitle));
            }
            RefreshPlugins();
            _ = RefreshPluginsAsync();
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
            desired.Add(new PinnedRailItemViewModel(isProject: true, label: Loc.T("项目"))
            {
                IsActive = IsProjectsPage
            });
        }

        if (_state.IsCodeReviewPinned)
        {
            desired.Add(new PinnedRailItemViewModel(isProject: false, label: Loc.T("代码审查"))
            {
                IsActive = _isCodeReviewDraftActive
            });
        }

        if (PinnedRailItems.Count == desired.Count
            && PinnedRailItems.Zip(desired).All(pair =>
                pair.First.IsProject == pair.Second.IsProject
                && pair.First.Label == pair.Second.Label))
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
    public ObservableCollection<PaletteFileGroupViewModel> PaletteFiles { get; } = [];
    public ObservableCollection<PaletteFileGroupViewModel> PaletteTextMatches { get; } = [];
    public bool HasNotifications => Notifications.Count > 0;
    public bool ShowNoNotifications => Notifications.Count == 0;

    private string _paletteFilter = "all";
    private string _reasoningEffort = "default";
    private string? _reasoningEffortPreview;
    private bool _isComposerEffortFlyoutOpen;
    private bool _webSearch = true;
    private bool _networkAccess = true;
    private bool _memoryEnabled = true;
    private bool _pluginsEnabled = true;
    private string _outputVerbosity = "default";
    private string _reasoningSummary = "auto";
    private string _personalStyle = "default";
    private string _warmth = "default";
    private string _enthusiasm = "default";
    private string _emojiUsage = "default";
    private string _visualAnswers = "default";
    private string _headingStyle = "default";
    private bool _quickAnswers = true;
    private string _customInstructionsDraft = string.Empty;
    private bool _loadingAgentSettings;
    private CancellationTokenSource? _paletteSearchCancellation;

    public bool IsPaletteFilterAll => _paletteFilter == "all";
    public bool IsPaletteFilterActions => _paletteFilter == "actions";
    public bool IsPaletteFilterChats => _paletteFilter == "chats";
    public bool IsPaletteFilterFiles => _paletteFilter == "files";
    public bool IsPaletteFilterText => _paletteFilter == "text";
    public bool ShowPaletteActions => _paletteFilter is "all" or "actions";
    public bool ShowPaletteChats => _paletteFilter is "all" or "chats";
    public bool ShowPaletteFiles => _paletteFilter is "all" or "files" && PaletteFiles.Count > 0;
    public bool ShowPaletteText =>
        _paletteFilter is "all" or "text" && PaletteTextMatches.Count > 0;
    public bool ShowPaletteEmpty => _paletteFilter switch
    {
        "files" => PaletteFiles.Count == 0,
        "text" => PaletteTextMatches.Count == 0,
        "actions" => false,
        "chats" => PaletteSessions.Count == 0,
        _ => PaletteSessions.Count == 0 && PaletteFiles.Count == 0 && PaletteTextMatches.Count == 0
    };

    public string PaletteEmptyText => _paletteFilter switch
    {
        "files" => SearchText.Trim().Length < 2 ? Loc.T("输入至少 2 个字符搜索文件") : Loc.T("没有匹配的文件"),
        "text" => SearchText.Trim().Length < 2 ? Loc.T("输入至少 2 个字符搜索文本") : Loc.T("没有匹配的文本"),
        "actions" => string.Empty,
        _ => Loc.T("没有匹配项")
    };

    public void SetPaletteFilter(string filter)
    {
        var value = filter switch
        {
            "actions" => "actions",
            "chats" => "chats",
            "files" => "files",
            "text" => "text",
            _ => "all"
        };
        if (_paletteFilter == value)
        {
            return;
        }

        _paletteFilter = value;
        OnPropertyChanged(nameof(IsPaletteFilterAll));
        OnPropertyChanged(nameof(IsPaletteFilterActions));
        OnPropertyChanged(nameof(IsPaletteFilterChats));
        OnPropertyChanged(nameof(IsPaletteFilterFiles));
        OnPropertyChanged(nameof(IsPaletteFilterText));
        OnPropertyChanged(nameof(ShowPaletteActions));
        OnPropertyChanged(nameof(ShowPaletteChats));
        OnPropertyChanged(nameof(ShowPaletteFiles));
        OnPropertyChanged(nameof(ShowPaletteText));
        OnPropertyChanged(nameof(ShowPaletteEmpty));
        OnPropertyChanged(nameof(PaletteEmptyText));
        RefreshPaletteSessions();
    }

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
        SearchText = string.Empty;
        SetPaletteFilter("all");
        _historyPromptsLoaded = false;
        RefreshPaletteSessions();
        IsSearchPaletteOpen = true;
    }

    public void CloseSearchPalette()
    {
        IsSearchPaletteOpen = false;
        _paletteSearchCancellation?.Cancel();
        _paletteSearchCancellation = null;
        if (PaletteFiles.Count > 0 || PaletteTextMatches.Count > 0)
        {
            PaletteFiles.Clear();
            PaletteTextMatches.Clear();
            RaisePaletteSearchVisibility();
        }
    }

    private string _notificationFilter = "priority";

    public bool IsNotificationFilterPriority => _notificationFilter == "priority";
    public bool IsNotificationFilterPinned => _notificationFilter == "pinned";
    public bool IsNotificationFilterAutomations => _notificationFilter == "automations";

    public void SetNotificationFilter(string filter)
    {
        _notificationFilter = filter is "priority" or "pinned" or "automations" ? filter : "priority";
        _state.NotificationFilter = _notificationFilter;
        SaveState();
        OnPropertyChanged(nameof(IsNotificationFilterPriority));
        OnPropertyChanged(nameof(IsNotificationFilterPinned));
        OnPropertyChanged(nameof(IsNotificationFilterAutomations));
        RefreshNotifications();
    }

    public void MarkAllNotificationsRead()
    {
        Notifications.Clear();
        OnPropertyChanged(nameof(HasNotifications));
        OnPropertyChanged(nameof(ShowNoNotifications));
    }

    public void ArchiveNotificationSessions()
    {
        foreach (var notification in Notifications.ToList())
        {
            notification.Session.IsArchived = true;
        }

        RefreshNotifications();
        RefreshRecentSessions();
        SaveState();
    }

    public void RefreshNotifications()
    {
        Notifications.Clear();
        var today = DateTimeOffset.Now.Date;
        var sessions = Workspaces
            .SelectMany(workspace => workspace.Sessions)
            .Where(session => !session.IsArchived)
            .Where(session => _notificationFilter switch
            {
                "pinned" => session.IsPinned,
                "automations" => false,
                _ => true
            })
            .OrderByDescending(session => session.State.UpdatedAt)
            .Take(12)
            .ToList();
        string? lastGroup = null;
        foreach (var session in sessions)
        {
            var date = session.State.UpdatedAt.ToLocalTime().Date;
            var group = date == today
                ? Loc.T("今天")
                : date == today.AddDays(-1) ? Loc.T("昨天") : date.ToString("MM-dd");
            Notifications.Add(new NotificationItemViewModel(session, group, group != lastGroup));
            lastGroup = group;
        }

        OnPropertyChanged(nameof(HasNotifications));
        OnPropertyChanged(nameof(ShowNoNotifications));
    }

    /// <summary>
    /// Debounced file-name and file-content search for the palette. Runs off the
    /// UI thread and only while the palette is open.
    /// </summary>
    public void RefreshPaletteFileSearch()
    {
        var previous = _paletteSearchCancellation;
        _paletteSearchCancellation = null;
        previous?.Cancel();
        previous?.Dispose();

        if (PaletteFiles.Count > 0 || PaletteTextMatches.Count > 0)
        {
            PaletteFiles.Clear();
            PaletteTextMatches.Clear();
        }

        RaisePaletteSearchVisibility();

        var query = SearchText.Trim();
        if (!IsSearchPaletteOpen || query.Length < 2)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _paletteSearchCancellation = cancellation;
        var workspace = WorkspacePath;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(220, cancellation.Token).ConfigureAwait(false);
                var files = WorkspaceFileIndex.Files(workspace);
                var nameMatches = WorkspaceFileIndex.MatchPaths(files, query, 12);
                var textMatches = WorkspaceFileIndex.SearchContents(workspace, files, query, 6, 3);
                if (cancellation.IsCancellationRequested)
                {
                    return;
                }

                _dispatch(() =>
                {
                    if (cancellation.IsCancellationRequested
                        || !ReferenceEquals(_paletteSearchCancellation, cancellation))
                    {
                        return;
                    }

                    PaletteFiles.Clear();
                    foreach (var path in nameMatches)
                    {
                        PaletteFiles.Add(new PaletteFileGroupViewModel(
                            path,
                            Array.Empty<PaletteLineMatch>(),
                            query));
                    }

                    PaletteTextMatches.Clear();
                    foreach (var (relativePath, lines) in textMatches)
                    {
                        PaletteTextMatches.Add(new PaletteFileGroupViewModel(relativePath, lines, query));
                    }

                    RaisePaletteSearchVisibility();
                });
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer query.
            }
        });
    }

    private void RaisePaletteSearchVisibility()
    {
        OnPropertyChanged(nameof(ShowPaletteFiles));
        OnPropertyChanged(nameof(ShowPaletteText));
        OnPropertyChanged(nameof(ShowPaletteEmpty));
        OnPropertyChanged(nameof(PaletteEmptyText));
    }

    public void RefreshPaletteSessions()
    {
        var query = SearchText.Trim();
        HashSet<string>? historyMatches = null;
        if (query.Length > 0)
        {
            LoadHistoryPrompts();
            historyMatches = _historyPrompts
                .Where(entry => entry.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .Select(entry => entry.SessionId)
                .ToHashSet(StringComparer.Ordinal);
        }

        var list = Workspaces
            .SelectMany(workspace => workspace.Sessions)
            .Where(session => !session.IsArchived)
            .Where(session => query.Length == 0
                || session.Matches(query)
                || (historyMatches?.Contains(session.Id) ?? false))
            .OrderByDescending(session => session.State.UpdatedAt)
            .Take(8)
            .ToList();
        SyncList(PaletteSessions, list);
        OnPropertyChanged(nameof(ShowPaletteEmpty));
        OnPropertyChanged(nameof(PaletteEmptyText));
    }

    private readonly List<(string SessionId, string Text)> _historyPrompts = [];
    private bool _historyPromptsLoaded;

    /// <summary>Reads home/history.jsonl so the palette can match past prompts.</summary>
    private void LoadHistoryPrompts()
    {
        if (_historyPromptsLoaded)
        {
            return;
        }

        _historyPromptsLoaded = true;
        _historyPrompts.Clear();
        var path = Path.Combine(_homeDirectory, "history.jsonl");
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            foreach (var line in File.ReadLines(path))
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.TryGetProperty("session_id", out var id)
                        && root.TryGetProperty("text", out var text)
                        && id.GetString() is { Length: > 0 } sessionId
                        && text.GetString() is { Length: > 0 } prompt)
                    {
                        // Keep the index bounded; the palette only needs recent prompts.
                        _historyPrompts.Add((sessionId, prompt));
                        if (_historyPrompts.Count > 5000)
                        {
                            _historyPrompts.RemoveAt(0);
                        }
                    }
                }
                catch (JsonException)
                {
                    // A crash can leave a partial final line.
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
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
        _rewindModelText = null;
        _editingUserItem = item.Seq is null ? null : item;
        RaiseEditState();
        if (item.Seq is null)
        {
            ErrorMessage = Loc.T("这条消息没有关联的事件记录（可能来自 fork 或导入），只能作为新消息发送。");
        }
    }

    /// <summary>Regenerates the reply by rewinding to the user message before it.</summary>
    public async Task RegenerateAsync(ChatItemViewModel assistant)
    {
        if (!assistant.IsAssistant)
        {
            return;
        }
        if (IsBusy)
        {
            ErrorMessage = Loc.T("请等待当前回合结束后再重新生成。");
            return;
        }
        if (IsCoreReadOnly)
        {
            ErrorMessage = Loc.T("另一客户端持有该会话的写权限，当前窗口只能观察。");
            return;
        }

        var index = Messages.IndexOf(assistant);
        ChatItemViewModel? target = null;
        for (var cursor = index - 1; cursor >= 0; cursor--)
        {
            if (Messages[cursor].IsUser)
            {
                target = Messages[cursor];
                break;
            }
        }
        if (target?.Seq is null)
        {
            ErrorMessage = Loc.T("无法定位这条回复对应的用户记录（可能已被压缩、来自 fork 或导入）。");
            return;
        }

        Composer = target.Text;
        _editingUserItem = target;
        _rewindModelText = string.IsNullOrWhiteSpace(target.ApiText) ? target.Text : target.ApiText;
        RaiseEditState();
        await SendAsync().ConfigureAwait(true);
    }

    /// <summary>Resends a user message, replacing it and every reply after it.</summary>
    public async Task ResendUserAsync(ChatItemViewModel user)
    {
        if (!user.IsUser)
        {
            return;
        }
        if (IsBusy)
        {
            ErrorMessage = Loc.T("请等待当前回合结束后再重新发送。");
            return;
        }
        if (IsCoreReadOnly)
        {
            ErrorMessage = Loc.T("另一客户端持有该会话的写权限，当前窗口只能观察。");
            return;
        }
        if (user.Seq is null)
        {
            ErrorMessage = Loc.T("无法定位这条消息（可能已被压缩、来自 fork 或导入）。");
            return;
        }

        Composer = user.Text;
        _editingUserItem = user;
        _rewindModelText = string.IsNullOrWhiteSpace(user.ApiText) ? user.Text : user.ApiText;
        RaiseEditState();
        await SendAsync().ConfigureAwait(true);
    }

    public void RefreshRecentSessions()
    {
        var query = SearchText.Trim();
        var all = Workspaces
            .SelectMany(workspace => workspace.Sessions)
            .Where(item => query.Length == 0 || item.Matches(query))
            .ToList();

        var pinned = all
            .Where(item => !item.IsArchived && item.IsPinned)
            .ToList();
        pinned = IsPinnedSortByRecent
            ? pinned
                .OrderByDescending(item => IsSessionSortByCreated ? item.State.CreatedAt : item.State.UpdatedAt)
                .ToList()
            : pinned
                .OrderBy(item => item.State.PinnedOrder == 0 ? int.MaxValue : item.State.PinnedOrder)
                .ThenByDescending(item => item.State.UpdatedAt)
                .ToList();
        pinned = pinned.Take(50).ToList();

        var items = all
            .Where(item => !item.IsArchived && !item.IsPinned)
            .OrderByDescending(item => IsSessionSortByCreated ? item.State.CreatedAt : item.State.UpdatedAt)
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
        SyncList(PinnedSessions, pinned);
        SyncList(RecentSessions, items);
        SyncList(ArchivedSessions, archived);

        OnPropertyChanged(nameof(HasPinnedSessions));
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
        if (session.IsPinned)
        {
            MovePinnedSession(session, int.MaxValue);
            return;
        }

        session.State.PinnedOrder = 0;
        SaveState();
    }

    /// <summary>Codex 手动排序: drop a pinned chat at a new position in the 置顶 list.</summary>
    public void MovePinnedSession(SessionItemViewModel session, int targetIndex)
    {
        var ordered = Workspaces
            .SelectMany(workspace => workspace.Sessions)
            .Where(item => item.IsPinned && item.State.PinnedOrder > 0)
            .OrderBy(item => item.State.PinnedOrder)
            .ToList();

        if (!ordered.Contains(session))
        {
            ordered.Add(session);
        }

        var currentIndex = ordered.FindIndex(item => ReferenceEquals(item, session));
        if (currentIndex >= 0)
        {
            ordered.RemoveAt(currentIndex);
        }

        var index = Math.Clamp(targetIndex, 0, ordered.Count);
        ordered.Insert(index, session);
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].State.PinnedOrder = i + 1;
        }

        RefreshRecentSessions();
        SaveState();
    }

    /// <summary>Drag a chat inside its project list (Codex project rows).</summary>
    public void MoveSessionWithinWorkspace(SessionItemViewModel session, int targetIndex)
    {
        var workspace = session.Workspace;
        var ordered = workspace.Sessions.ToList();
        var current = ordered.IndexOf(session);
        if (current < 0)
        {
            return;
        }

        ordered.RemoveAt(current);
        var index = Math.Clamp(targetIndex, 0, ordered.Count);
        ordered.Insert(index, session);
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].State.SortOrder = i + 1;
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            var from = workspace.Sessions.IndexOf(ordered[i]);
            if (from != i)
            {
                workspace.Sessions.Move(from, i);
            }
        }

        workspace.State.Sessions = ordered.Select(item => item.State).ToList();
        SaveState();
    }

    /// <summary>Drag a project folder row to reorder the 项目 section.</summary>
    public void MoveWorkspace(WorkspaceItemViewModel workspace, int targetIndex)
    {
        var ordered = Workspaces.ToList();
        var current = ordered.IndexOf(workspace);
        if (current < 0)
        {
            return;
        }

        ordered.RemoveAt(current);
        var index = Math.Clamp(targetIndex, 0, ordered.Count);
        ordered.Insert(index, workspace);
        for (var i = 0; i < ordered.Count; i++)
        {
            var from = Workspaces.IndexOf(ordered[i]);
            if (from != i)
            {
                Workspaces.Move(from, i);
            }
        }

        _state.Workspaces = ordered.Select(item => item.State).ToList();
        SaveState();
    }

    /// <summary>Archives the chat currently on screen (Ctrl+Shift+A).</summary>
    public void ArchiveActiveSession()
    {
        if (_activeSessionItem is { } session)
        {
            ArchiveSession(session);
        }
    }

    /// <summary>Pins or unpins the chat currently on screen (Ctrl+Alt+P).</summary>
    public void ToggleActiveSessionPinned()
    {
        if (_activeSessionItem is { } session)
        {
            ToggleSessionPinned(session);
        }
    }

    public void MarkSessionUnread(SessionItemViewModel session)
    {
        session.IsUnread = true;
        SaveState();
    }

    /// <summary>Marks the chat currently on screen unread (Ctrl+Shift+U).</summary>
    public void ToggleActiveSessionUnread()
    {
        if (_activeSessionItem is { } session)
        {
            session.IsUnread = !session.IsUnread;
            SaveState();
        }
    }

    /// <summary>Last fenced code block in the newest assistant reply.</summary>
    public string? LastAssistantCodeBlock()
    {
        for (var index = Messages.Count - 1; index >= 0; index--)
        {
            if (Messages[index].IsAssistant
                && ExtractLastCodeBlock(Messages[index].Text) is { } code)
            {
                return code;
            }
        }

        return null;
    }

    public static string? ExtractLastCodeBlock(string markdown)
    {
        var blocks = new List<string>();
        var current = new System.Text.StringBuilder();
        var inside = false;
        foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inside)
                {
                    blocks.Add(current.ToString().TrimEnd().Replace(Environment.NewLine, "\n"));
                    current.Clear();
                }

                inside = !inside;
                continue;
            }

            if (inside)
            {
                current.AppendLine(line);
            }
        }

        return blocks.Count == 0 ? null : blocks[^1];
    }

    public void ArchiveSession(SessionItemViewModel session)
    {
        session.IsArchived = true;
        SaveState();
        _ = PushArchivedAsync(session, archived: true);
    }

    public void UnarchiveSession(SessionItemViewModel session)
    {
        session.IsArchived = false;
        SaveState();
        _ = PushArchivedAsync(session, archived: false);
    }

    /// <summary>Mirrors the local archive flag into the session store.</summary>
    private async Task PushArchivedAsync(SessionItemViewModel session, bool archived)
    {
        try
        {
            await _core.ArchiveAsync(session.Id, archived, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            _dispatch(() =>
            {
                session.IsArchived = !archived;
                SaveState();
                SetCoreStatus(Loc.F("{0}失败：{1}", (archived ? Loc.T("归档") : Loc.T("取消归档")), (error.Message)));
            });
        }
    }

    public void ToggleCodeReviewPinned()
    {
        _state.IsCodeReviewPinned = !_state.IsCodeReviewPinned;
        SaveState();
        OnPropertyChanged(nameof(IsCodeReviewPinned));
        RefreshPinnedRailItems();
    }

    public bool IsCodeReviewDraftActive => _isCodeReviewDraftActive;

    public void StartCodeReviewDraft()
    {
        Navigate(AppSection.Chat);
        _ = CloseCoreSessionQuietlyAsync();
        StartDraftSession();
        Composer = "请审查当前项目的代码变更，重点检查潜在 bug、回归风险、安全问题和缺失测试。"; // i18n-keep
        _isCodeReviewDraftActive = true;
        UpdatePinnedRailActiveState();
    }

    private bool _isReviewRunning;
    private string _reviewStatusText = Loc.T("运行审查后在此显示结果。");

    public ObservableCollection<ReviewFindingRow> ReviewFindings { get; } = [];
    public bool HasReviewFindings => ReviewFindings.Count > 0;
    public bool ShowNoReviewFindings => !HasReviewFindings;

    public int ReviewModeIndex
    {
        get => _state.ReviewMode switch
        {
            "quick" => 0,
            "strict" => 2,
            _ => 1
        };
        set
        {
            var mode = value switch { 0 => "quick", 2 => "strict", _ => "standard" };
            if (_state.ReviewMode == mode)
            {
                return;
            }
            _state.ReviewMode = mode;
            SaveState();
            OnPropertyChanged();
        }
    }

    public bool ReviewFullFiles
    {
        get => _state.ReviewFullFiles;
        set
        {
            if (_state.ReviewFullFiles == value)
            {
                return;
            }
            _state.ReviewFullFiles = value;
            SaveState();
            OnPropertyChanged();
        }
    }

    public string ReviewInstructions
    {
        get => _state.ReviewInstructions;
        set
        {
            if (_state.ReviewInstructions == value)
            {
                return;
            }
            _state.ReviewInstructions = value;
            SaveState();
            OnPropertyChanged();
        }
    }

    public string ReviewStatusText
    {
        get => _reviewStatusText;
        private set => SetProperty(ref _reviewStatusText, value);
    }

    public bool IsReviewRunning
    {
        get => _isReviewRunning;
        private set
        {
            if (SetProperty(ref _isReviewRunning, value))
            {
                OnPropertyChanged(nameof(CanRunReview));
            }
        }
    }

    public bool CanRunReview => !IsReviewRunning && !string.IsNullOrWhiteSpace(WorkspacePath);

    /// <summary>Runs Core's code review for the active workspace.</summary>
    public async Task RunReviewAsync()
    {
        if (IsReviewRunning)
        {
            return;
        }
        var workspace = WorkspacePath;
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            ReviewStatusText = Loc.T("当前项目路径不可用。");
            return;
        }

        IsReviewRunning = true;
        ReviewStatusText = Loc.T("正在审查…");
        try
        {
            var result = await _core.RunReviewAsync(
                    workspace,
                    _state.ReviewMode,
                    _state.ReviewInstructions,
                    _state.ReviewFullFiles,
                    CancellationToken.None)
                .ConfigureAwait(true);
            var rows = new List<ReviewFindingRow>();
            if (result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("findings", out var findings)
                && findings.ValueKind == JsonValueKind.Array)
            {
                foreach (var finding in findings.EnumerateArray())
                {
                    var severity = JsonRead.GetOptionalString(finding, "severity") ?? "info";
                    var file = JsonRead.GetOptionalString(finding, "file") ?? string.Empty;
                    var line = finding.TryGetProperty("line", out var lineValue)
                        && lineValue.TryGetInt32(out var parsedLine)
                        ? parsedLine
                        : 0;
                    var location = string.IsNullOrWhiteSpace(file)
                        ? "(workspace)"
                        : line > 0 ? $"{file}:{line}" : file;
                    rows.Add(new ReviewFindingRow(
                        severity,
                        location,
                        JsonRead.GetOptionalString(finding, "rule") ?? string.Empty,
                        JsonRead.GetOptionalString(finding, "evidence") ?? string.Empty,
                        JsonRead.GetOptionalString(finding, "recommendation") ?? string.Empty));
                }
            }

            ReviewFindings.Clear();
            foreach (var row in rows)
            {
                ReviewFindings.Add(row);
            }
            OnPropertyChanged(nameof(HasReviewFindings));
            OnPropertyChanged(nameof(ShowNoReviewFindings));
            ReviewStatusText = JsonRead.GetOptionalString(result, "summary")
                ?? (rows.Count == 0 ? Loc.T("审查完成：没有发现问题。") : Loc.F("审查完成：{0} 个发现。", (rows.Count)));
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or FileNotFoundException)
        {
            ReviewStatusText = Loc.F("审查失败：{0}", (error.Message));
        }
        finally
        {
            IsReviewRunning = false;
        }
    }

    public void OpenSettings(SettingsPane? pane = null)
    {
        RefreshSettingsDraft();
        var target = pane
            ?? (Enum.TryParse<SettingsPane>(_state.LastSettingsPane, out var saved)
                ? saved
                : SettingsPane.General);
        SelectSettingsPane(target);
        IsSettingsOpen = true;
    }

    public void CloseSettings() => IsSettingsOpen = false;

    public void SelectSettingsPane(SettingsPane pane)
    {
        _settingsPane = pane;
        if (_state.LastSettingsPane != pane.ToString())
        {
            _state.LastSettingsPane = pane.ToString();
            SaveState();
        }
        OnPropertyChanged(nameof(IsSettingsGeneral));
        OnPropertyChanged(nameof(IsSettingsNotifications));
        OnPropertyChanged(nameof(IsSettingsAppearance));
        OnPropertyChanged(nameof(IsSettingsMemory));
        OnPropertyChanged(nameof(IsSettingsArchived));
        OnPropertyChanged(nameof(IsSettingsShortcuts));
        OnPropertyChanged(nameof(IsSettingsProviders));
        OnPropertyChanged(nameof(IsSettingsModels));
        OnPropertyChanged(nameof(IsSettingsUsage));
        OnPropertyChanged(nameof(IsSettingsConfig));
        OnPropertyChanged(nameof(IsSettingsImport));
        OnPropertyChanged(nameof(IsSettingsCloudPc));
        OnPropertyChanged(nameof(IsSettingsCodeReview));
        OnPropertyChanged(nameof(IsSettingsStorage));
        OnPropertyChanged(nameof(IsSettingsPersonalization));
        OnPropertyChanged(nameof(IsSettingsPlugins));
        OnPropertyChanged(nameof(IsSettingsPasswords));
        OnPropertyChanged(nameof(IsSettingsHooks));
        OnPropertyChanged(nameof(IsSettingsConnections));
        OnPropertyChanged(nameof(IsSettingsGit));
        OnPropertyChanged(nameof(IsSettingsEnvironments));
        OnPropertyChanged(nameof(IsSettingsWorktrees));
        OnPropertyChanged(nameof(IsSettingsAudit));
        OnPropertyChanged(nameof(SettingsPaneTitle));
        OnPropertyChanged(nameof(SettingsPaneSubtitle));
        OnPropertyChanged(nameof(HasSettingsPaneSubtitle));
        if (pane == SettingsPane.General)
        {
            _ = RefreshAgentSettingsAsync();
        }
        else if (pane == SettingsPane.Personalization)
        {
            _ = RefreshMemoryAsync();
            _ = RefreshAgentSettingsAsync();
        }
        else if (pane == SettingsPane.Import)
        {
            _ = DiscoverImportsAsync();
        }
        else if (pane == SettingsPane.Plugins)
        {
            _ = RefreshMcpToolsAsync();
            _ = RefreshPluginsAsync();
        }
        else if (pane == SettingsPane.Memory)
        {
            _ = RefreshMemoryAsync();
        }
        else if (pane == SettingsPane.Archived)
        {
            RefreshRecentSessions();
        }
        else if (pane == SettingsPane.Usage)
        {
            RefreshUsage();
        }
        else if (pane == SettingsPane.Providers)
        {
            _ = RefreshProvidersAsync(force: HasProviderCards, announce: false);
        }
        else if (pane == SettingsPane.Passwords)
        {
            RefreshCredentials();
        }
        else if (pane == SettingsPane.Hooks)
        {
            RefreshHooks();
        }
        else if (pane == SettingsPane.Git)
        {
            _ = RefreshGitAsync();
        }
        else if (pane == SettingsPane.Worktrees)
        {
            _ = RefreshWorktreesAsync();
        }
        else if (pane == SettingsPane.Config)
        {
            _ = RefreshConfigAsync();
            _ = RefreshAgentSettingsAsync();
        }
        else if (pane == SettingsPane.Storage)
        {
            _ = RefreshStorageAsync();
        }
        else if (pane == SettingsPane.Audit)
        {
            _ = RefreshAuditAsync();
        }
    }

    public ObservableCollection<UsageSessionRow> UsageRows { get; } = [];
    public long UsageTotalInputTokens { get; private set; }
    public long UsageTotalOutputTokens { get; private set; }
    public double UsageTotalCostUsd { get; private set; }
    public string UsageTokensText => $"{UsageTotalInputTokens:N0} / {UsageTotalOutputTokens:N0}";
    public string UsageCostText => UsageTotalCostUsd > 0 ? $"${UsageTotalCostUsd:0.0000}" : "$0";
    public bool ShowUsageRows => UsageRows.Count > 0;

    /// <summary>Aggregates the per-session usage persisted on completed turns.</summary>
    public void RefreshUsage()
    {
        var metered = _state.Workspaces
            .SelectMany(workspace => workspace.Sessions)
            .Where(session => session.InputTokens > 0 || session.OutputTokens > 0 || session.CostUsd > 0)
            .ToList();
        UsageTotalInputTokens = metered.Sum(session => session.InputTokens);
        UsageTotalOutputTokens = metered.Sum(session => session.OutputTokens);
        UsageTotalCostUsd = metered.Sum(session => session.CostUsd);
        UsageRows.Clear();
        foreach (var session in metered
                     .OrderByDescending(session => session.InputTokens + session.OutputTokens)
                     .Take(20))
        {
            UsageRows.Add(new UsageSessionRow(
                session.Title,
                $"{session.InputTokens:N0} / {session.OutputTokens:N0}",
                session.CostUsd > 0 ? $"${session.CostUsd:0.0000}" : "—"));
        }

        // The analysis tab charts the 12 most recently touched metered chats.
        var recent = metered
            .OrderBy(session => session.UpdatedAt)
            .TakeLast(12)
            .ToList();
        var peak = recent.Count == 0
            ? 0d
            : recent.Max(session => (double)(session.InputTokens + session.OutputTokens));
        UsageBars.Clear();
        foreach (var session in recent)
        {
            var tokens = (double)(session.InputTokens + session.OutputTokens);
            UsageBars.Add(new UsageBarRow(peak > 0 ? Math.Max(4, Math.Round(110 * tokens / peak)) : 4));
        }

        OnPropertyChanged(nameof(UsageTotalInputTokens));
        OnPropertyChanged(nameof(UsageTotalOutputTokens));
        OnPropertyChanged(nameof(UsageTotalCostUsd));
        OnPropertyChanged(nameof(UsageTokensText));
        OnPropertyChanged(nameof(UsageCostText));
        OnPropertyChanged(nameof(ShowUsageRows));
        OnPropertyChanged(nameof(HasUsageBars));
    }

    public ObservableCollection<UsageBarRow> UsageBars { get; } = [];

    public bool HasUsageBars => UsageBars.Count > 0;

    public void DismissError() => ErrorMessage = string.Empty;

    public void ReportError(string message) => ErrorMessage = message;

    public async Task SaveSettingsAsync()
    {
        if (!SupportedPermissionModes.Contains(SettingsPermissionMode))
        {
            SetSettingsStatus(Loc.T("权限模式无效。"), isError: true);
            return;
        }

        _state.PermissionMode = SettingsPermissionMode;
        if (SettingsPermissionMode == "full" && !_state.FullAccessWarningDismissed)
        {
            _showFullAccessWarning = true;
        }
        SettingsContentFontSize = Math.Clamp(SettingsContentFontSize, 12, 20);
        _state.ContentFontSize = SettingsContentFontSize;
        CodeFontSize = Math.Clamp(CodeFontSize, 9, 20);
        OnPropertyChanged(nameof(ContentFontSize));
        OnPropertyChanged(nameof(PermissionModeLabel));
        OnPropertyChanged(nameof(IsPermissionFull));
        OnPropertyChanged(nameof(IsPermissionAuto));
        OnPropertyChanged(nameof(IsPermissionAsk));
        OnPropertyChanged(nameof(ShowFullAccessWarning));
        SaveState();
        await PushPermissionModeAsync(CancellationToken.None).ConfigureAwait(true);
        SetSettingsStatus(Loc.T("设置已保存。审批模式由 KL Core 执行。"), isError: false);
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
        SetSettingsStatus(Loc.T("正在检查 KL Core…"), isError: false);
        try
        {
            var snapshot = await _core.LoadSnapshotAsync(CancellationToken.None).ConfigureAwait(true);
            if (!snapshot.Success)
            {
                SetSettingsStatus(Loc.F("KL Core 不可用：{0}", (snapshot.Error)), isError: true);
                return;
            }

            ApplyCatalogSnapshot(snapshot);
            SetSettingsStatus(
                Loc.F("KL Core {0}（protocol {1}）连通正常。", (snapshot.CoreVersion), (snapshot.ProtocolVersion)),
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

    /// <summary>Applies the chosen model through Core; the GUI never calls a provider.</summary>
    public async Task SaveModelAsync()
    {
        var choice = SettingsSelectedModel;
        if (string.IsNullOrWhiteSpace(choice))
        {
            SetSettingsStatus(Loc.T("请选择要设为默认的模型。"), isError: true);
            return;
        }

        var (provider, model) = ParseModelChoice(choice);
        if (provider.Length == 0)
        {
            SetSettingsStatus(Loc.T("该模型没有对应的 Provider，无法切换。"), isError: true);
            return;
        }

        try
        {
            await _core.RequestAsync(
                Protocol.ConfigSetDefaultModel,
                new { provider, model },
                CancellationToken.None).ConfigureAwait(true);
            await RefreshCatalogQuietlyAsync().ConfigureAwait(true);
            SetSettingsStatus(Loc.F("默认模型已切换为 {0} / {1}。", (provider), (model)), isError: false);
        }
        catch (Exception error) when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            SetSettingsStatus(Loc.F("切换模型失败：{0}", (error.Message)), isError: true);
        }
    }

    /// <summary>Reads Core's reasoning effort and reflects it in the chip + picker.</summary>
    public async Task LoadReasoningEffortAsync()
    {
        try
        {
            ReasoningEffort = await _core.GetReasoningEffortAsync(CancellationToken.None)
                .ConfigureAwait(true);
            RefreshReasoningEffortSelection();
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or FileNotFoundException)
        {
            // Reasoning effort is optional metadata; the chip simply stays at 默认.
        }
    }

    /// <summary>Reads Core's global agent settings for the settings panes.</summary>
    public async Task RefreshAgentSettingsAsync()
    {
        await RefreshAgentPresetsAsync().ConfigureAwait(true);
        try
        {
            var result = await _core.GetAgentConfigAsync(CancellationToken.None).ConfigureAwait(true);
            if (result.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            _loadingAgentSettings = true;
            try
            {
                if (JsonRead.TryGetBool(result, "web_search", out var webSearch))
                {
                    WebSearch = webSearch;
                }
                if (JsonRead.TryGetBool(result, "network_access", out var networkAccess))
                {
                    NetworkAccess = networkAccess;
                }
                if (JsonRead.TryGetBool(result, "memory_enabled", out var memoryEnabled))
                {
                    MemoryEnabled = memoryEnabled;
                }
                if (JsonRead.TryGetBool(result, "plugins_enabled", out var pluginsEnabled))
                {
                    PluginsEnabled = pluginsEnabled;
                }
                if (result.TryGetProperty("output_verbosity", out var verbosity)
                    && verbosity.GetString() is { } verbosityValue)
                {
                    _outputVerbosity = verbosityValue is "concise" or "detailed"
                        ? verbosityValue
                        : "default";
                }
                if (result.TryGetProperty("reasoning_summary", out var summary)
                    && summary.GetString() is { } summaryValue)
                {
                    _reasoningSummary = summaryValue is "off" or "detailed"
                        ? summaryValue
                        : "auto";
                }
                if (result.TryGetProperty("custom_instructions", out var instructions)
                    && instructions.GetString() is { } instructionsValue)
                {
                    CustomInstructionsDraft = instructionsValue;
                }
                if (result.TryGetProperty("personal_style", out var style)
                    && style.GetString() is { } styleValue)
                {
                    _personalStyle = styleValue is "friendly" or "professional"
                        ? styleValue
                        : "default";
                }
                if (result.TryGetProperty("warmth", out var warmth)
                    && warmth.GetString() is { } warmthValue)
                {
                    _warmth = warmthValue is "low" or "high" ? warmthValue : "default";
                }
                if (result.TryGetProperty("enthusiasm", out var enthusiasm)
                    && enthusiasm.GetString() is { } enthusiasmValue)
                {
                    _enthusiasm = enthusiasmValue is "low" or "high" ? enthusiasmValue : "default";
                }
                if (result.TryGetProperty("emoji_usage", out var emoji)
                    && emoji.GetString() is { } emojiValue)
                {
                    _emojiUsage = emojiValue is "less" or "more" ? emojiValue : "default";
                }
                if (result.TryGetProperty("visual_answers", out var visual)
                    && visual.GetString() is { } visualValue)
                {
                    _visualAnswers = visualValue is "off" or "on" ? visualValue : "default";
                }
                if (result.TryGetProperty("heading_style", out var heading)
                    && heading.GetString() is { } headingValue)
                {
                    _headingStyle = headingValue is "concise" or "detailed"
                        ? headingValue
                        : "default";
                }
                if (JsonRead.TryGetBool(result, "quick_answers", out var quickAnswers))
                {
                    _quickAnswers = quickAnswers;
                }
            }
            finally
            {
                _loadingAgentSettings = false;
            }

            OnPropertyChanged(nameof(OutputVerbosityIndex));
            OnPropertyChanged(nameof(ReasoningSummaryIndex));
            OnPropertyChanged(nameof(PersonalStyleIndex));
            OnPropertyChanged(nameof(WarmthIndex));
            OnPropertyChanged(nameof(EnthusiasmIndex));
            OnPropertyChanged(nameof(EmojiIndex));
            OnPropertyChanged(nameof(VisualAnswersIndex));
            OnPropertyChanged(nameof(HeadingStyleIndex));
            OnPropertyChanged(nameof(QuickAnswers));
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or FileNotFoundException)
        {
            // Settings stay at their last known values when Core is unavailable.
        }
    }

    /// <summary>Applies a reasoning effort through Core and updates the picker.</summary>
    public async Task SetReasoningEffortAsync(string effort)
    {
        try
        {
            ReasoningEffort = await _core.SetReasoningEffortAsync(effort, CancellationToken.None)
                .ConfigureAwait(true);
            RefreshReasoningEffortSelection();
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or FileNotFoundException)
        {
            ReportError(Loc.F("切换推理强度失败：{0}", (error.Message)));
        }
    }

    private void RefreshReasoningEffortSelection()
    {
        foreach (var option in ReasoningEffortOptions)
        {
            option.IsSelected = string.Equals(option.Id, _reasoningEffort, StringComparison.Ordinal);
        }
        OnPropertyChanged(nameof(ReasoningEffortIndex));
    }

    /// <summary>Quick permission switch from the composer chip.</summary>
    public void SetPermissionMode(string mode)
    {
        if (!SupportedPermissionModes.Contains(mode))
        {
            return;
        }

        _state.PermissionMode = mode;
        SettingsPermissionMode = mode;
        if (mode == "full" && !_state.FullAccessWarningDismissed)
        {
            _showFullAccessWarning = true;
        }
        SaveState();
        RaisePermissionModeChanged();
        _ = PushPermissionModeAsync(CancellationToken.None);
    }

    /// <summary>Config pane index: 请求批准 / 帮我批准 / 完全访问.</summary>
    public int ApprovalModeIndex
    {
        get => _state.PermissionMode switch
        {
            "full" => 2,
            "auto" => 1,
            _ => 0
        };
        set
        {
            var mode = value switch
            {
                2 => "full",
                1 => "auto",
                _ => "ask"
            };
            if (mode == _state.PermissionMode)
            {
                return;
            }

            SetPermissionMode(mode);
            OnPropertyChanged();
        }
    }

    private void RaisePermissionModeChanged()
    {
        OnPropertyChanged(nameof(PermissionModeLabel));
        OnPropertyChanged(nameof(ApprovalModeIndex));
        OnPropertyChanged(nameof(IsPermissionFull));
        OnPropertyChanged(nameof(IsPermissionAuto));
        OnPropertyChanged(nameof(IsPermissionAsk));
        OnPropertyChanged(nameof(ShowFullAccessWarning));
    }

    /// <summary>Writes the API key into Core config; the draft is cleared afterwards.</summary>
    public async Task SaveApiKeyAsync()
    {
        var key = SettingsApiKeyDraft.Trim();
        if (key.Length == 0)
        {
            SetSettingsStatus(Loc.T("请先输入 API Key。"), isError: true);
            return;
        }

        var (provider, _) = ParseModelChoice(SettingsSelectedModel);
        if (provider.Length == 0)
        {
            provider = CoreCatalogProvider;
        }

        if (provider.Length == 0)
        {
            SetSettingsStatus(Loc.T("KL Core 尚未报告可用的 Provider。"), isError: true);
            return;
        }

        try
        {
            await _core.SetProviderApiKeyAsync(provider, key, CancellationToken.None).ConfigureAwait(true);
            SettingsApiKeyDraft = string.Empty;
            await RefreshCatalogQuietlyAsync().ConfigureAwait(true);
            SetSettingsStatus(Loc.F("已把 {0} 的 API Key 写入 KL Core。", (provider)), isError: false);
        }
        catch (Exception error) when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            SetSettingsStatus(Loc.F("保存 API Key 失败：{0}", (error.Message)), isError: true);
        }
    }

    private static (string Provider, string Model) ParseModelChoice(string choice)
    {
        var separator = choice.IndexOf(" / ", StringComparison.Ordinal);
        return separator > 0
            ? (choice[..separator], choice[(separator + 3)..])
            : (string.Empty, choice);
    }

    /// <summary>Runs kl.doctor and lists its checks in the Models settings pane.</summary>
    public async Task RunDoctorAsync()
    {
        if (IsDoctorRunning)
        {
            return;
        }

        IsDoctorRunning = true;
        DoctorChecks.Clear();
        DoctorSummary = Loc.T("正在运行诊断…");
        try
        {
            var result = await _core.DoctorAsync(CancellationToken.None).ConfigureAwait(true);
            var healthy = result.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
            if (result.TryGetProperty("checks", out var checks)
                && checks.ValueKind == JsonValueKind.Array)
            {
                foreach (var check in checks.EnumerateArray())
                {
                    var name = check.TryGetProperty("name", out var nameValue)
                        ? nameValue.GetString() ?? string.Empty
                        : string.Empty;
                    var passed = check.TryGetProperty("ok", out var passedValue)
                        && passedValue.ValueKind == JsonValueKind.True;
                    var detail = check.TryGetProperty("detail", out var detailValue)
                        ? detailValue.GetString() ?? string.Empty
                        : string.Empty;
                    DoctorChecks.Add($"{(passed ? "✓" : "✕")} {name}"
                        + (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" — {detail}"));
                }
            }

            var version = result.TryGetProperty("core_version", out var versionValue)
                ? versionValue.GetString() ?? string.Empty
                : string.Empty;
            DoctorSummary = healthy ? Loc.T("诊断通过。") : Loc.T("诊断发现问题。");
            if (!string.IsNullOrWhiteSpace(version))
            {
                DoctorSummary += $" Core {version}";
            }
            OnPropertyChanged(nameof(HasDoctorSummary));
            OnPropertyChanged(nameof(HasDoctorChecks));
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or FileNotFoundException)
        {
            DoctorSummary = Loc.F("诊断失败：{0}", (error.Message));
            OnPropertyChanged(nameof(HasDoctorSummary));
            OnPropertyChanged(nameof(HasDoctorChecks));
        }
        finally
        {
            IsDoctorRunning = false;
        }
    }

    private async Task RefreshCatalogQuietlyAsync()
    {
        try
        {
            var snapshot = await _core.LoadSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            if (snapshot.Success)
            {
                ApplyCatalogSnapshot(snapshot);
            }
        }
        catch (Exception error) when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            // The write already succeeded; a stale catalog is only cosmetic.
        }
    }

    public void ResetSettingsDefaults()
    {
        SetThemeMode("dark");
        AccentColorIndex = 0;
        UiFontIndex = 0;
        ContentFontIndex = 0;
        CodeFontIndex = 0;
        UiFontWeightIndex = 0;
        ContentFontWeightIndex = 0;
        CodeFontWeightIndex = 0;
        Contrast = 50;
        TranslucentSidebar = false;
        SettingsContentFontSize = 14;
        CodeFontSize = 12;
        SetReduceMotionMode("system");
        SetDiffMarkerMode("color");
        SaveAppearanceSettings();
        SetSettingsStatus(Loc.T("已恢复默认外观设置。"), isError: false);
    }

    public string ImportCodexHome => _importService.CodexHome;
    public string ImportClaudeHome => _importService.ClaudeHome;
    public bool IsImportScanning
    {
        get => _isImportScanning;
        private set
        {
            if (SetProperty(ref _isImportScanning, value))
            {
                OnPropertyChanged(nameof(CanImportSelected));
            }
        }
    }

    public string ImportStatusText
    {
        get => _importStatusText;
        private set => SetProperty(ref _importStatusText, value);
    }

    public bool HasImportCandidates => ImportCandidates.Count > 0;
    public bool CanImportSelected => !IsImportScanning && ImportCandidates.Any(item => item.IsSelected);

    private void NotifyImportSelection() => OnPropertyChanged(nameof(CanImportSelected));

    public async Task RunAutomationAsync(AutomationItemViewModel automation)
    {
        if (automation.IsRunning)
        {
            return;
        }

        automation.IsRunning = true;
        automation.State.LastStatus = "running";
        automation.NotifyStateChanged();
        try
        {
            var result = await _core.RunEphemeralTurnAsync(
                _activeWorkspace.Path,
                automation.Prompt,
                CancellationToken.None).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(result))
            {
                throw new InvalidOperationException(Loc.T("模型没有返回可保存的正文结果。"));
            }
            automation.State.LastResult = result.Length > 12000 ? result[..12000] : result;
            automation.State.LastStatus = "completed";
        }
        catch (Exception error)
        {
            automation.State.LastResult = error.Message;
            automation.State.LastStatus = "failed";
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
            if (_state.AutomationNotifications)
            {
                RequestAttention(AttentionKind.Automation);
            }
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
        LastImportedPluginPath = null;
        if (IsRemoteCore)
        {
            ErrorMessage = RemoteDisabled(Loc.T("插件导入"));
            return false;
        }
        try
        {
            var source = Path.GetFullPath(sourceDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!File.Exists(Path.Combine(source, "plugin.json"))
                && !File.Exists(Path.Combine(source, "manifest.json")))
            {
                ErrorMessage = Loc.T("所选目录中没有 plugin.json 或 manifest.json。");
                return false;
            }

            Directory.CreateDirectory(PluginDirectoryPath);
            var managedRoot = Path.GetFullPath(PluginDirectoryPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (source.StartsWith(managedRoot, StringComparison.OrdinalIgnoreCase))
            {
                ErrorMessage = Loc.T("该目录已经位于插件管理目录中。");
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
            LastImportedPluginPath = target;
            RefreshPlugins();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ErrorMessage = Loc.F("导入插件失败：{0}", (error.Message));
            return false;
        }
    }

    public string? LastImportedPluginPath { get; private set; }

    /// <summary>Registers a copied plugin directory with the running Core.</summary>
    private void SetSettingsStatus(string message, bool isError)
    {
        SettingsStatus = message;
        _settingsStatusIsError = isError;
        OnPropertyChanged(nameof(HasSettingsStatus));
        OnPropertyChanged(nameof(IsSettingsStatusError));
        OnPropertyChanged(nameof(IsSettingsStatusSuccess));
    }



    private string ToUserFacingError(Exception error)
    {
        if (error is FileNotFoundException)
        {
            return Loc.T("未找到 kl-core 可执行文件，请先构建 Core 或设置 KL_CORE_PATH。");
        }
        if (error is JsonRpcException rpc)
        {
            return Loc.F("KL Core 返回错误：{0}", (rpc.Message));
        }
        if (error is IOException)
        {
            return Loc.F("与 KL Core 的连接中断：{0}", (error.Message));
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

    /// <summary>Groups the Core provider catalog into the Usage → provider card.</summary>
    private void ApplyProviderCards(
        IReadOnlyList<CoreModelOption> options,
        IReadOnlyList<string> models,
        string provider,
        string activeProvider,
        bool keyConfigured,
        IReadOnlyList<CoreProviderProtocols> protocols)
    {
        if (options.Count == 0 && provider.Length > 0)
        {
            options = models
                .Select(model => new CoreModelOption(provider, model, true, keyConfigured))
                .ToList();
        }

        var profiles = _state.ProviderProfiles ??= [];
        var rows = options
            .Where(option => !string.IsNullOrWhiteSpace(option.Provider))
            .GroupBy(option => option.Provider, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var id = group.Key;
                profiles.TryGetValue(id.ToLowerInvariant(), out var profile);
                var protocolUrls = protocols
                    .FirstOrDefault(entry =>
                        string.Equals(entry.Provider, id, StringComparison.OrdinalIgnoreCase))
                    ?.AsMap();
                return new ProviderCardRow(
                    id,
                    ProviderText(profile?.DisplayName, ProviderDisplayName(id)),
                    ProviderText(profile?.Note, Loc.T("已接入的模型提供商")),
                    ProviderText(profile?.Website, string.Empty),
                    string.Equals(id, activeProvider, StringComparison.OrdinalIgnoreCase),
                    true,
                    group.Any(option => option.KeyConfigured),
                    group.ToList(),
                    protocolUrls);
            })
            .ToList();

        // A provider can exist with no models yet; keep it visible from its protocol URLs.
        foreach (var entry in protocols)
        {
            if (string.IsNullOrWhiteSpace(entry.Provider)
                || rows.Any(row => string.Equals(row.Id, entry.Provider, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            profiles.TryGetValue(entry.Provider.ToLowerInvariant(), out var profile);
            rows.Add(new ProviderCardRow(
                entry.Provider,
                ProviderText(profile?.DisplayName, ProviderDisplayName(entry.Provider)),
                ProviderText(profile?.Note, Loc.T("已接入的模型提供商")),
                ProviderText(profile?.Website, string.Empty),
                isDefault: false,
                enabled: true,
                keyConfigured: false,
                models: [],
                entry.AsMap()));
        }

        ApplyProviderRows(rows
            .OrderByDescending(row => row.IsDefault)
            .ThenByDescending(row => row.Enabled)
            .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList());
        RefreshComposerReasoningEfforts();
    }

    /// <summary>Applies provider rows in place so the ten-second refresh does not flicker.</summary>
    private void ApplyProviderRows(IReadOnlyList<ProviderCardRow> rows)
    {
        foreach (var row in rows)
        {
            var current = ProviderCards.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, row.Id, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                ProviderCards.Add(row);
                continue;
            }

            current.Name = row.Name;
            current.Note = row.Note;
            current.Website = row.Website;
            current.Enabled = row.Enabled;
            current.SetKeyConfigured(row.KeyConfigured);
            current.SetModels(row.Models);
            current.SetProtocols(row.Protocols);
        }

        foreach (var stale in ProviderCards
            .Where(card => !rows.Any(row =>
                string.Equals(row.Id, card.Id, StringComparison.OrdinalIgnoreCase)))
            .ToList())
        {
            ProviderCards.Remove(stale);
        }

        RemoveDuplicateProviderCards();
        EnsureUniqueProviderNames();
        NotifyProviderCardsChanged();
    }

    private void RemoveDuplicateProviderCards()
    {
        foreach (var duplicate in ProviderCards
            .GroupBy(card => card.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Skip(1))
            .ToList())
        {
            ProviderCards.Remove(duplicate);
        }
    }

    private void EnsureUniqueProviderNames()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in ProviderCards
            .OrderByDescending(card => card.IsDefault)
            .ThenByDescending(card => card.Enabled)
            .ThenBy(card => card.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(card => card.Id, StringComparer.OrdinalIgnoreCase)
            .ToList())
        {
            var baseName = string.IsNullOrWhiteSpace(card.Name) ? card.Id : card.Name.Trim();
            if (used.Add(baseName))
            {
                card.Name = baseName;
                continue;
            }

            var suffix = " copy";
            var candidate = baseName + suffix;
            while (!used.Add(candidate))
            {
                suffix += "copy";
                candidate = baseName + suffix;
            }

            card.Name = candidate;
        }
    }

    private void NotifyProviderCardsChanged()
    {
        OnPropertyChanged(nameof(HasProviderCards));
        OnPropertyChanged(nameof(ShowNoProviderCards));
        OnPropertyChanged(nameof(ProviderSummaryText));
    }

    private void HydrateProviderCache()
    {
        var cached = _providerCacheStore.Load();
        if (cached is null || cached.Providers.Count == 0)
        {
            return;
        }

        var profiles = _state.ProviderProfiles ??= [];
        var rows = new List<ProviderCardRow>();
        foreach (var entry in cached.Providers)
        {
            if (entry is null)
            {
                continue;
            }

            var id = entry.Id?.Trim() ?? string.Empty;
            if (id.Length == 0)
            {
                continue;
            }

            profiles.TryGetValue(id.ToLowerInvariant(), out var profile);
            rows.Add(new ProviderCardRow(
                id,
                string.IsNullOrWhiteSpace(profile?.DisplayName)
                    ? entry.Name ?? string.Empty
                    : profile.DisplayName.Trim(),
                profile is null ? entry.Note ?? string.Empty : profile.Note,
                profile is null ? entry.Website ?? string.Empty : profile.Website,
                entry.IsDefault,
                entry.Enabled,
                entry.KeyConfigured,
                entry.Models ?? [],
                entry.Protocols));
        }

        if (rows.Count > 0)
        {
            ApplyProviderRows(rows);
        }
    }

    /// <summary>Persists the cards the page shows; the file never contains API keys.</summary>
    private void SaveProviderCache()
    {
        RemoveDuplicateProviderCards();
        EnsureUniqueProviderNames();
        _providerCacheStore.Save(ProviderCards.Select(row => new ProviderCacheEntry
        {
            Id = row.Id,
            Name = row.Name,
            Note = row.Note,
            Website = row.Website,
            IsDefault = row.IsDefault,
            Enabled = row.Enabled,
            KeyConfigured = row.KeyConfigured,
            Models = row.Models.ToList(),
            Protocols = new Dictionary<string, string>(row.Protocols, StringComparer.OrdinalIgnoreCase)
        }).ToList());
    }

    private static string ProviderText(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    /// <summary>Adds disabled providers that the live router does not list.</summary>
    private void MergeConfiguredProviders(JsonElement configResult)
    {
        if (configResult.ValueKind != JsonValueKind.Object
            || !configResult.TryGetProperty("config", out var config)
            || !config.TryGetProperty("providers", out var providers)
            || providers.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var profiles = _state.ProviderProfiles ??= [];
        var added = false;
        foreach (var provider in providers.EnumerateObject())
        {
            var id = provider.Name;
            if (id.Length == 0
                || ProviderCards.Any(row => string.Equals(row.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var enabled = provider.Value.ValueKind == JsonValueKind.Object
                && provider.Value.TryGetProperty("enabled", out var enabledValue)
                && enabledValue.ValueKind == JsonValueKind.True;
            profiles.TryGetValue(id.ToLowerInvariant(), out var profile);
            ProviderCards.Add(new ProviderCardRow(
                id,
                ProviderText(profile?.DisplayName, ProviderDisplayName(id)),
                ProviderText(profile?.Note, Loc.T("已接入的模型提供商")),
                ProviderText(profile?.Website, string.Empty),
                isDefault: false,
                enabled,
                keyConfigured: false,
                models: []));
            added = true;
        }

        if (added)
        {
            NotifyProviderCardsChanged();
        }
    }

    private static string ProviderDisplayName(string id) => id.ToLowerInvariant() switch
    {
        "openai" => "OpenAI",
        "anthropic" => "Anthropic",
        "deepseek" => "DeepSeek",
        "opencode" => "OpenCode",
        "openrouter" => "OpenRouter",
        "google" or "gemini" => "Google Gemini",
        "xai" => "xAI",
        "ollama" => "Ollama",
        "fake" => "Scripted / Fake",
        _ => id
    };

    /// <summary>Asks Core to create the workspace's AGENTS.md template.</summary>
    private async Task InitializeWorkspaceAsync(string path)
    {
        try
        {
            await _core.RequestAsync(
                Protocol.WorkspaceInit,
                new { path },
                CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException)
        {
            _dispatch(() => ReportError(Loc.F("无法初始化工作区：{0}", (error.Message))));
        }
    }

    private void SaveState(bool silent = false)
    {
        if (_stateImported)
        {
            RefreshRecentSessions();
            return;
        }

        try
        {
            _stateStore.Save(_state);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (!silent)
            {
                ErrorMessage = Loc.F("无法保存本地状态：{0}", (error.Message));
            }
        }

        RefreshRecentSessions();
    }
}

