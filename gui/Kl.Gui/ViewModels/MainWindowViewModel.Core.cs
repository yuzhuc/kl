using System.Text.Json;
using Kl.Client;
using Kl.Gui.Models;
using Kl.Gui.Services;

namespace Kl.Gui.ViewModels;

/// <summary>
/// Core-session half of <see cref="MainWindowViewModel"/>. The GUI is a protocol
/// client only: sessions, turns, tools and approvals all happen inside kl-core
/// and are observed through <see cref="KlSessionClient"/> events.
/// </summary>
public sealed partial class MainWindowViewModel
{
    private readonly ICoreSessionService _core;
    private readonly Action<Action> _dispatch;
    private CancellationTokenSource? _turnCancellation;
    private CancellationTokenSource? _reconnectCancellation;
    private volatile bool _isAutoReconnecting;
    private DateTimeOffset _suppressCompactionUntil;
    private ChatItemViewModel? _activeToolItem;
    private readonly Dictionary<string, ChatItemViewModel> _toolItemsByCallId = new(StringComparer.Ordinal);
    private string? _activeApprovalId;

    private string _coreStatusText = "正在连接 KL Core…";
    private string _coreBannerText = string.Empty;
    private bool _isCoreReady;
    private bool _isCoreBusy;
    private bool _isCoreRetrying;
    private bool _isCoreReadOnly;
    private string _coreVersionText = "—";
    private string _coreCatalogModel = string.Empty;
    private string _coreCatalogProvider = string.Empty;
    private string _coreProviderText = "—";
    private string _coreKeyStatusText = "—";
    private string _corePathText = "—";
    private string _coreHomeText = "—";
    private bool _isApprovalOpen;
    private string _approvalTitle = string.Empty;
    private string _approvalAction = string.Empty;
    private string _approvalResource = string.Empty;
    private string _approvalReason = string.Empty;
    private string _approvalArgs = string.Empty;
    private long _usageInputTokens;
    private long _usageOutputTokens;
    private double _usageCostUsd;

    public string CoreStatusText
    {
        get => _coreStatusText;
        private set => SetProperty(ref _coreStatusText, value);
    }

    public string CoreBannerText
    {
        get => _coreBannerText;
        private set => SetProperty(ref _coreBannerText, value);
    }

    public bool IsCoreReady
    {
        get => _isCoreReady;
        private set
        {
            if (SetProperty(ref _isCoreReady, value))
            {
                OnPropertyChanged(nameof(ShowCoreBanner));
                OnPropertyChanged(nameof(CanSend));
                OnPropertyChanged(nameof(CoreConnectionLabel));
            }
        }
    }

    public bool IsCoreBusy
    {
        get => _isCoreBusy;
        private set => SetProperty(ref _isCoreBusy, value);
    }

    public bool IsCoreRetrying
    {
        get => _isCoreRetrying;
        private set
        {
            if (SetProperty(ref _isCoreRetrying, value))
            {
                OnPropertyChanged(nameof(CanRetryCore));
                OnPropertyChanged(nameof(CanSend));
            }
        }
    }

    public bool CanRetryCore => !IsCoreRetrying;

    public bool ShowCoreBanner => !string.IsNullOrWhiteSpace(CoreBannerText);

    public bool IsCoreReadOnly
    {
        get => _isCoreReadOnly;
        private set
        {
            if (SetProperty(ref _isCoreReadOnly, value))
            {
                OnPropertyChanged(nameof(ShowObserverNotice));
                OnPropertyChanged(nameof(CanSend));
                OnPropertyChanged(nameof(CoreBannerActionText));
            }
        }
    }

    public bool ShowObserverNotice => IsCoreReadOnly;

    /// <summary>The banner button retries the connection, or takes the write lease.</summary>
    public string CoreBannerActionText => IsCoreReadOnly ? "接管写入" : "重试";

    public string CoreVersionText
    {
        get => _coreVersionText;
        private set => SetProperty(ref _coreVersionText, value);
    }

    public string CoreCatalogModel
    {
        get => _coreCatalogModel;
        private set
        {
            if (SetProperty(ref _coreCatalogModel, value))
            {
                OnPropertyChanged(nameof(ModelName));
                OnPropertyChanged(nameof(ProviderModelLabel));
            }
        }
    }

    public string CoreCatalogProvider
    {
        get => _coreCatalogProvider;
        private set
        {
            if (SetProperty(ref _coreCatalogProvider, value))
            {
                OnPropertyChanged(nameof(ProviderModelLabel));
            }
        }
    }

    /// <summary>Codex-style connection chip label in the content header.</summary>
    public string CoreConnectionLabel => IsCoreReady ? "KL Core 已连接" : "KL Core 未连接";

    public string CoreProviderText
    {
        get => _coreProviderText;
        private set => SetProperty(ref _coreProviderText, value);
    }

    public string CoreKeyStatusText
    {
        get => _coreKeyStatusText;
        private set => SetProperty(ref _coreKeyStatusText, value);
    }

    public string CorePathText
    {
        get => _corePathText;
        private set => SetProperty(ref _corePathText, value);
    }

    public string CoreHomeText
    {
        get => _coreHomeText;
        private set => SetProperty(ref _coreHomeText, value);
    }

    public bool IsApprovalOpen
    {
        get => _isApprovalOpen;
        private set
        {
            if (SetProperty(ref _isApprovalOpen, value))
            {
                OnPropertyChanged(nameof(HasApprovalArgs));
            }
        }
    }

    public bool HasApprovalArgs => !string.IsNullOrWhiteSpace(ApprovalArgs);

    public string ApprovalTitle
    {
        get => _approvalTitle;
        private set => SetProperty(ref _approvalTitle, value);
    }

    public string ApprovalAction
    {
        get => _approvalAction;
        private set => SetProperty(ref _approvalAction, value);
    }

    public string ApprovalResource
    {
        get => _approvalResource;
        private set => SetProperty(ref _approvalResource, value);
    }

    public string ApprovalReason
    {
        get => _approvalReason;
        private set => SetProperty(ref _approvalReason, value);
    }

    public string ApprovalArgs
    {
        get => _approvalArgs;
        private set
        {
            if (SetProperty(ref _approvalArgs, value))
            {
                OnPropertyChanged(nameof(HasApprovalArgs));
            }
        }
    }

    public string UsageText => _usageInputTokens == 0 && _usageOutputTokens == 0
        ? string.Empty
        : $"tokens {_usageInputTokens}/{_usageOutputTokens}"
          + (_usageCostUsd > 0 ? $" · ${_usageCostUsd:0.000000}" : string.Empty);

    private void OnCoreEventReceived(SessionEvent sessionEvent) =>
        _dispatch(() => ApplyCoreEvent(sessionEvent));

    private async Task InitializeCoreAsync()
    {
        try
        {
            var snapshot = await _core.LoadSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            _dispatch(() => ApplyCatalogSnapshot(snapshot));
            await LoadReasoningEffortAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _dispatch(() => SetCoreFailure($"KL Core 连接失败：{error.Message}"));
        }

        // Test hooks (only active when explicitly enabled through environment
        // variables). They exercise the same code path as user interaction and
        // are used by the scripted-provider end-to-end checks.
        _ = RunEndToEndHookAsync();
    }

    private async Task RunEndToEndHookAsync()
    {
        var openLast = Environment.GetEnvironmentVariable("KL_GUI_E2E_OPEN_LAST");
        if (string.Equals(openLast, "1", StringComparison.Ordinal))
        {
            await Task.Delay(400).ConfigureAwait(false);
            var latest = Workspaces
                .SelectMany(workspace => workspace.Sessions)
                .OrderByDescending(item => item.State.UpdatedAt)
                .FirstOrDefault();
            if (latest is not null)
            {
                await RunOnUiThreadAsync(() => OpenSessionAsync(latest)).ConfigureAwait(false);
            }
        }

        var prompt = Environment.GetEnvironmentVariable("KL_GUI_E2E_PROMPT");
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        await Task.Delay(400).ConfigureAwait(false);
        await RunOnUiThreadAsync(async () =>
        {
            Composer = prompt;
            await SendAsync();
        }).ConfigureAwait(false);
    }

    private Task RunOnUiThreadAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dispatch(async () =>
        {
            try
            {
                await action().ConfigureAwait(true);
                completion.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                completion.TrySetCanceled();
            }
            catch (Exception error)
            {
                completion.TrySetException(error);
            }
        });
        return completion.Task;
    }

    public async Task RetryCoreAsync()
    {
        if (IsCoreRetrying)
        {
            return;
        }

        CancelAutoReconnect();
        IsCoreRetrying = true;
        SetCoreStatus("正在重新连接 KL Core…");
        try
        {
            var snapshot = await _core.LoadSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            ApplyCatalogSnapshot(snapshot);

            if (!_core.IsSessionOpen)
            {
                return;
            }

            var result = await _core.ReconnectAsync(CancellationToken.None).ConfigureAwait(false);
            if (result.Success)
            {
                await RunOnUiThreadAsync(() => ApplyCoreRestoredAsync(result.Replayed))
                    .ConfigureAwait(false);
            }
            else
            {
                SetCoreFailure($"无法恢复 KL Core 会话：{result.Error}");
            }
        }
        catch (Exception error)
        {
            SetCoreFailure($"KL Core 连接失败：{error.Message}");
        }
        finally
        {
            IsCoreRetrying = false;
        }
    }

    /// <summary>Takes the write lease back from the client that currently holds it.</summary>
    public async Task TakeOverWriteAsync()
    {
        if (!_core.IsSessionOpen || !IsCoreReadOnly)
        {
            return;
        }

        var taken = await _core.TryBecomeWriterAsync(CancellationToken.None);
        IsCoreReadOnly = !_core.IsWriter;
        if (taken)
        {
            CoreBannerText = string.Empty;
            CoreStatusText = "已接管该会话的写入。";
        }
        else
        {
            CoreBannerText = "另一客户端仍在写入此会话，请稍后再试。";
        }

        OnPropertyChanged(nameof(ShowCoreBanner));
        OnPropertyChanged(nameof(CanSend));
    }

    /// <summary>
    /// Re-attaches after a dropped connection, backing off between attempts. A
    /// local-server reconnect replays from LastSeq; a reopened stdio session
    /// cannot, so the transcript is reloaded from core history.
    /// </summary>
    private void BeginAutoReconnect()
    {
        if (_isAutoReconnecting || !_core.IsSessionOpen)
        {
            return;
        }

        _isAutoReconnecting = true;
        _reconnectCancellation = new CancellationTokenSource();
        var cancellationToken = _reconnectCancellation.Token;
        _ = Task.Run(() => AutoReconnectAsync(cancellationToken));
    }

    private async Task AutoReconnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            for (var attempt = 1; attempt <= 4; attempt++)
            {
                var delay = TimeSpan.FromMilliseconds(400 * attempt * attempt);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                var result = await _core.ReconnectAsync(cancellationToken).ConfigureAwait(false);
                if (result.Success)
                {
                    await RunOnUiThreadAsync(() => ApplyCoreRestoredAsync(result.Replayed))
                        .ConfigureAwait(false);
                    return;
                }

                _dispatch(() =>
                {
                    IsCoreRetrying = true;
                    CoreBannerText = $"与 KL Core 的连接已断开，正在重连（第 {attempt + 1} 次）…";
                    OnPropertyChanged(nameof(ShowCoreBanner));
                });
            }

            _dispatch(() =>
            {
                IsCoreRetrying = false;
                SetCoreFailure("与 KL Core 的连接已断开，请点击重试。");
            });
        }
        catch (OperationCanceledException)
        {
            // Session closed, a manual retry took over, or the window is closing.
        }
        catch (Exception)
        {
            _dispatch(() =>
            {
                IsCoreRetrying = false;
                SetCoreFailure("与 KL Core 的连接已断开，请点击重试。");
            });
        }
        finally
        {
            _isAutoReconnecting = false;
        }
    }

    private async Task ApplyCoreRestoredAsync(bool replayed)
    {
        IsCoreReady = true;
        IsCoreRetrying = false;
        IsCoreReadOnly = !_core.IsWriter;
        CoreStatusText = "KL Core 连接已恢复。";
        CoreBannerText = IsCoreReadOnly
            ? "另一客户端正在写入此会话；当前窗口为只读观察者。"
            : string.Empty;
        OnPropertyChanged(nameof(ShowCoreBanner));
        OnPropertyChanged(nameof(CanSend));
        if (!replayed)
        {
            await LoadHistoryAsync().ConfigureAwait(false);
        }

        await ApplyPermissionModeAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void ApplyCatalogSnapshot(CoreCatalogSnapshot snapshot)
    {
        if (!snapshot.Success)
        {
            SetCoreFailure($"KL Core 不可用：{snapshot.Error}");
            return;
        }

        ApplyCoreSessions(snapshot.Sessions);
        IsCoreReady = true;
        CoreBannerText = string.Empty;
        CoreStatusText = $"KL Core 已连接（{snapshot.CoreVersion}）";
        CoreVersionText = $"{snapshot.CoreVersion} · protocol {snapshot.ProtocolVersion}";
        CoreProviderText = string.IsNullOrWhiteSpace(snapshot.Provider)
            ? "未配置"
            : $"{snapshot.Provider} / {snapshot.Model}";
        CoreCatalogModel = snapshot.Model;
        CoreCatalogProvider = snapshot.Provider;
        CoreKeyStatusText = snapshot.KeyConfigured
            ? "API Key 已配置（由 KL Core 管理）"
            : "未配置 API Key（在 KL Core 配置中设置）";
        SettingsModelChoices.Clear();
        foreach (var option in snapshot.ModelOptions)
        {
            SettingsModelChoices.Add($"{option.Provider} / {option.Model}");
        }
        if (SettingsModelChoices.Count == 0)
        {
            foreach (var model in snapshot.Models)
            {
                SettingsModelChoices.Add(model);
            }
        }
        SettingsSelectedModel = SettingsModelChoices.FirstOrDefault(
                choice => choice == $"{snapshot.Provider} / {snapshot.Model}" || choice == snapshot.Model)
            ?? SettingsModelChoices.FirstOrDefault()
            ?? string.Empty;
        CorePathText = ResolveCorePathText();
        CoreHomeText = ResolveCoreHomeText();
        OnPropertyChanged(nameof(ShowCoreBanner));
        OnPropertyChanged(nameof(ModelName));
        OnPropertyChanged(nameof(CanSend));
    }

    private void ApplyCoreSessions(IReadOnlyList<CoreSessionSummary> sessions)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in sessions)
        {
            known.Add(session.SessionId);
        }

        foreach (var workspace in _state.Workspaces)
        {
            workspace.Sessions.RemoveAll(session =>
                !string.IsNullOrWhiteSpace(session.Id) && !known.Contains(session.Id));
        }

        foreach (var session in sessions)
        {
            var workspaceState = FindOrCreateWorkspaceState(session.Workspace);
            var updatedAt = CoreProtocolParser.ParseTimestamp(session.UpdatedAt, DateTimeOffset.Now);
            var existing = workspaceState.Sessions.FirstOrDefault(
                candidate => string.Equals(candidate.Id, session.SessionId, StringComparison.Ordinal));
            if (existing is null)
            {
                workspaceState.Sessions.Add(new SessionState
                {
                    Id = session.SessionId,
                    Title = CoreProtocolParser.DeriveTitle(session),
                    CreatedAt = updatedAt,
                    UpdatedAt = updatedAt
                });
            }
            else
            {
                existing.Title = CoreProtocolParser.DeriveTitle(session);
                existing.UpdatedAt = updatedAt;
            }
        }

        SyncWorkspaceSessionItems();
        OnPropertyChanged(nameof(WorkspaceCount));
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowNoSessions));
        OnPropertyChanged(nameof(ShowNoSearchResults));
        OnPropertyChanged(nameof(SavedSessionCount));

        var activeId = _activeSession?.Id;
        var activeItem = string.IsNullOrWhiteSpace(activeId)
            ? null
            : Workspaces.SelectMany(workspace => workspace.Sessions)
                .FirstOrDefault(item => string.Equals(item.Id, activeId, StringComparison.Ordinal));
        if (activeItem is not null)
        {
            activeItem.IsSelected = true;
            _activeSessionItem = activeItem;
        }
        else if (!string.IsNullOrWhiteSpace(activeId) && !known.Contains(activeId))
        {
            // The active session disappeared from core (deleted elsewhere).
            StartDraftSession();
        }

        SaveState(silent: true);
    }

    private WorkspaceState FindOrCreateWorkspaceState(string workspacePath)
    {
        var normalized = NormalizeWorkspacePath(workspacePath);
        foreach (var workspace in _state.Workspaces)
        {
            if (string.Equals(NormalizeWorkspacePath(workspace.Path), normalized, StringComparison.OrdinalIgnoreCase))
            {
                return workspace;
            }
        }

        var name = Path.GetFileName(normalized.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name))
        {
            name = normalized;
        }

        var state = new WorkspaceState
        {
            Name = name,
            Path = normalized,
            IsExpanded = true
        };
        _state.Workspaces.Add(state);
        Workspaces.Add(new WorkspaceItemViewModel(state));
        RefreshWorkspacePathLabels();
        return state;
    }

    private void SyncWorkspaceSessionItems()
    {
        foreach (var workspace in Workspaces)
        {
            workspace.Sessions.Clear();
            foreach (var session in workspace.State.Sessions
                         .OrderBy(item => item.SortOrder == 0 ? int.MaxValue : item.SortOrder)
                         .ThenByDescending(item => item.UpdatedAt))
            {
                workspace.Sessions.Add(new SessionItemViewModel(workspace, session));
            }
        }
    }

    public async Task OpenSessionAsync(SessionItemViewModel session, bool save = true)
    {
        if (IsBusy || IsCoreBusy)
        {
            return;
        }

        IsCoreBusy = true;
        SetCoreStatus("正在打开会话…");
        try
        {
            var result = await _core.OpenSessionAsync(
                new CoreSessionRequest
                {
                    Workspace = session.Workspace.Path,
                    SessionId = session.Id
                },
                progress => _dispatch(() => CoreStatusText = DescribeProgress(progress)),
                CancellationToken.None).ConfigureAwait(false);

            if (!result.Success)
            {
                SetCoreFailure($"无法打开会话：{result.Error}");
                return;
            }

            _dispatch(() =>
            {
                SelectSession(session.Workspace, session.State);
                ApplyCoreSessionInfo(result);
                SyncStoredMessages();
            });

            await LoadHistoryAsync().ConfigureAwait(false);
            await ApplyPermissionModeAsync(CancellationToken.None).ConfigureAwait(false);
            if (save)
            {
                SaveState(silent: true);
            }
        }
        finally
        {
            IsCoreBusy = false;
        }
    }

    public async Task NewSessionAsync()
    {
        if (IsBusy)
        {
            return;
        }

        await CloseCoreSessionQuietlyAsync().ConfigureAwait(false);
        _dispatch(() =>
        {
            StartDraftSession();
            SaveState();
        });
    }

    private async Task<bool> EnsureCoreSessionAsync(CancellationToken cancellationToken)
    {
        if (_core.IsSessionOpen && PathsEqual(_core.Workspace, _activeWorkspace.Path))
        {
            return true;
        }

        await CloseCoreSessionQuietlyAsync().ConfigureAwait(false);
        if (IsBusy)
        {
            // CloseCoreSessionQuietlyAsync killed the turn; the caller bails out.
            return false;
        }

        IsCoreBusy = true;
        try
        {
            if (_activeSession is null)
            {
                StartDraftSession();
            }

            // Only a session that already exists in the sidebar carries a real core
            // session id; a fresh draft must be created, not resumed.
            var sessionId = _activeSessionItem is null ? null : _activeSession!.Id;
            var result = await _core.OpenSessionAsync(
                new CoreSessionRequest
                {
                    Workspace = _activeWorkspace.Path,
                    SessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId
                },
                progress => _dispatch(() => CoreStatusText = DescribeProgress(progress)),
                cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                SetCoreFailure($"无法连接 KL Core：{result.Error}");
                return false;
            }

            _dispatch(() =>
            {
                ApplyCoreSessionInfo(result);
                if (_activeSessionItem is null)
                {
                    _activeSession!.Id = result.SessionId;
                    EnsureSessionPersisted();
                    _activeSession.UpdatedAt = DateTimeOffset.Now;
                    UpdateSessionOrder();
                    SaveState(silent: true);
                }

                IsCoreReady = true;
                CoreBannerText = string.Empty;
                CoreStatusText = "KL Core 就绪";
            });
            await ApplyPermissionModeAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            IsCoreBusy = false;
        }
    }

    private void ApplyCoreSessionInfo(CoreConnectResult result)
    {
        IsCoreReadOnly = !result.IsWriter;
        OnPropertyChanged(nameof(ModelName));
        OnPropertyChanged(nameof(ProviderModelLabel));
        OnPropertyChanged(nameof(WorkspaceName));
        OnPropertyChanged(nameof(WorkspacePath));
        if (IsCoreReadOnly)
        {
            CoreBannerText = "另一客户端正在写入此会话；当前窗口为只读观察者。";
        }
    }

    private async Task LoadHistoryAsync()
    {
        try
        {
            var history = await _core.HistoryAsync(200, CancellationToken.None).ConfigureAwait(false);
            var messages = CoreProtocolParser.ParseHistory(history);
            _dispatch(() =>
            {
                LoadHistoryMessages(messages);
                if (_activeSession is not null)
                {
                    // Opening/switching a chat must not reorder 最近: its position
                    // only changes when a message is sent or a reply arrives.
                    SyncStoredMessages();
                }
            });
        }
        catch (Exception error) when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            _dispatch(() => ErrorMessage = $"读取会话历史失败：{error.Message}");
        }
    }

    private void LoadHistoryMessages(IReadOnlyList<CoreHistoryMessage> history)
    {
        Messages.Clear();
        _toolItemsByCallId.Clear();
        _activeToolItem = null;
        _activeReasoning = null;
        _activeAssistant = null;
        _activeToolItem = null;

        foreach (var message in history)
        {
            switch (message.Role)
            {
                case "user":
                    Messages.Add(ChatItemViewModel.User(
                        message.Text,
                        timestamp: DateTimeOffset.TryParse(message.Timestamp, out var userTime)
                            ? userTime
                            : null));
                    break;
                case "assistant":
                    if (!string.IsNullOrWhiteSpace(message.Reasoning))
                    {
                        var reasoning = ChatItemViewModel.Reasoning(message.Reasoning);
                        reasoning.Complete("成功", "思考完成");
                        Messages.Add(reasoning);
                    }
                    Messages.Add(ChatItemViewModel.Assistant(message.Text));
                    break;
                case "tool":
                    var tool = ChatItemViewModel.Tool(
                        ChatItemViewModel.ToolHeader(message.ToolName, message.ToolSummary),
                        string.Empty,
                        message.ToolOutput ?? string.Empty,
                        message.ToolSummary ?? string.Empty,
                        ChatItemViewModel.ToolKindLabel(message.ToolName));
                    if (!string.IsNullOrWhiteSpace(message.ToolDiff))
                    {
                        tool.AppendDetail("\n" + message.ToolDiff);
                    }
                    tool.Complete(message.ToolOk == false ? "失败" : "成功");
                    AddToolItem(tool);
                    break;
            }
        }
    }

    private async Task ApplyPermissionModeAsync(CancellationToken cancellationToken)
    {
        if (!_core.IsSessionOpen)
        {
            return;
        }

        try
        {
            await _core.RequestAsync(
                Protocol.PermissionsSet,
                new { approval_mode = MapApprovalMode(_state.PermissionMode) },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            // Permission mode is best effort: the turn still works with the core default.
        }
    }

    public async Task RespondApprovalAsync(string decision)
    {
        var approvalId = _activeApprovalId;
        _activeApprovalId = null;
        IsApprovalOpen = false;
        if (approvalId is null)
        {
            return;
        }

        if (!_core.ResolveApproval(approvalId, decision))
        {
            ErrorMessage = "审批请求已过期。";
        }

        await Task.CompletedTask;
    }

    private void ApplyCoreEvent(SessionEvent sessionEvent)
    {
        switch (sessionEvent.Kind)
        {
            case SessionEventKind.TurnStarted:
                IsBusy = true;
                CoreStatusText = "正在生成…";
                OnPropertyChanged(nameof(IsBusy));
                break;
            case SessionEventKind.AssistantDelta:
                if (_activeReasoning is { IsRunning: true } reasoning)
                {
                    reasoning.Complete("成功", "思考完成");
                }
                EnsureAssistantItem().AppendText(sessionEvent.Text);
                break;
            case SessionEventKind.ReasoningDelta:
                EnsureReasoningItem().AppendText(sessionEvent.Text);
                break;
            case SessionEventKind.ToolStarted:
                _activeReasoning?.Complete("成功", "思考完成");
                _activeAssistant?.Complete(string.Empty);
                var command = ChatItemViewModel.ExtractCommand(sessionEvent.Args);
                var tool = ChatItemViewModel.Tool(
                    ChatItemViewModel.ToolHeader(sessionEvent.Name, command),
                    string.IsNullOrWhiteSpace(command) ? "…" : command,
                    string.Empty,
                    string.Empty,
                    ChatItemViewModel.ToolKindLabel(sessionEvent.Name));
                tool.Start("运行中");
                _activeToolItem = tool;
                if (!string.IsNullOrEmpty(sessionEvent.CallId))
                {
                    _toolItemsByCallId[sessionEvent.CallId] = tool;
                }
                AddToolItem(tool);
                break;
            case SessionEventKind.ToolOutput:
                AppendToolOutput(ResolveToolItem(sessionEvent.CallId), sessionEvent.Text);
                break;
            case SessionEventKind.ToolCompleted:
                ApplyToolCompleted(sessionEvent, ResolveToolItem(sessionEvent.CallId));
                break;
            case SessionEventKind.ApprovalRequested:
                _activeApprovalId = sessionEvent.ApprovalId;
                ApprovalTitle = string.IsNullOrWhiteSpace(sessionEvent.Name)
                    ? "工具调用审批"
                    : $"审批：{sessionEvent.Name}";
                ApprovalAction = sessionEvent.Action ?? string.Empty;
                ApprovalResource = sessionEvent.Resource ?? string.Empty;
                ApprovalReason = sessionEvent.Reason ?? string.Empty;
                ApprovalArgs = sessionEvent.Args ?? string.Empty;
                IsApprovalOpen = true;
                ScheduleEndToEndApproval();
                break;
            case SessionEventKind.ApprovalResolved:
                IsApprovalOpen = false;
                _activeApprovalId = null;
                break;
            case SessionEventKind.ContextCompacted
                when DateTimeOffset.Now < _suppressCompactionUntil:
                // The manual compact path reloads history and adds its own marker;
                // the event can arrive a moment after the RPC reply, so the window
                // stays open briefly instead of a single flag.
                _suppressCompactionUntil = default;
                break;
            case SessionEventKind.ContextCompacted:
                var compacted = ChatItemViewModel.Tool(
                    "上下文已压缩",
                    sessionEvent.Text,
                    sessionEvent.Summary,
                    sessionEvent.Text);
                compacted.Complete("完成");
                Messages.Add(compacted);
                break;
            case SessionEventKind.TurnCompleted:
                _usageInputTokens += sessionEvent.InputTokens;
                _usageOutputTokens += sessionEvent.OutputTokens;
                _usageCostUsd += sessionEvent.CostUsd ?? 0;
                OnPropertyChanged(nameof(UsageText));
                FinishTurn(sessionEvent.Status == "cancelled" ? "已停止" : "完成");
                break;
            case SessionEventKind.TurnFailed:
                AddError(string.IsNullOrWhiteSpace(sessionEvent.Text) ? "本次回合失败。" : sessionEvent.Text);
                FinishTurn("失败");
                break;
            case SessionEventKind.Error:
                AddError(sessionEvent.Text);
                if (IsBusy)
                {
                    FinishTurn("连接中断");
                }
                SetCoreFailure(sessionEvent.Text);
                if (sessionEvent.ConnectionLost)
                {
                    BeginAutoReconnect();
                }
                break;
            case SessionEventKind.AuditRecorded:
                break;
        }
    }

    /// <summary>
    /// Routes tool traffic by CallId so parallel tool calls keep writing to their
    /// own cards. Falls back to the newest card when the core omits a call id.
    /// </summary>
    private ChatItemViewModel? ResolveToolItem(string? callId) =>
        !string.IsNullOrEmpty(callId) && _toolItemsByCallId.TryGetValue(callId, out var item)
            ? item
            : _activeToolItem;

    private static void AppendToolOutput(ChatItemViewModel? item, string text)
    {
        if (item is null || string.IsNullOrEmpty(text))
        {
            return;
        }

        const int limit = 12_000;
        var current = item.Detail;
        if (current.Length >= limit)
        {
            return;
        }

        var remaining = limit - current.Length;
        item.AppendDetail(text.Length <= remaining ? text : text[..remaining] + "\n…");
    }

    private void ApplyToolCompleted(SessionEvent sessionEvent, ChatItemViewModel? resolved)
    {
        var item = resolved ?? _activeToolItem;
        if (item is null)
        {
            item = ChatItemViewModel.Tool(
                ChatItemViewModel.ToolHeader(sessionEvent.Name),
                string.Empty,
                string.Empty,
                sessionEvent.Summary,
                ChatItemViewModel.ToolKindLabel(sessionEvent.Name));
            item.Start("运行中");
            AddToolItem(item);
        }

        if (!string.IsNullOrWhiteSpace(sessionEvent.Summary))
        {
            item.SetSummary(sessionEvent.Summary);
        }
        if (!string.IsNullOrWhiteSpace(sessionEvent.Diff))
        {
            item.AppendDetail("\n" + sessionEvent.Diff);
        }
        item.Complete(sessionEvent.Ok ? "成功" : "失败");
        if (!string.IsNullOrEmpty(sessionEvent.CallId))
        {
            _toolItemsByCallId.Remove(sessionEvent.CallId);
        }

        if (ReferenceEquals(_activeToolItem, item))
        {
            _activeToolItem = null;
        }

        if (string.Equals(ChatItemViewModel.ToolKindLabel(sessionEvent.Name), "Image", StringComparison.Ordinal))
        {
            var count = CountImageMentions(item.Detail) + CountImageMentions(item.Text);
            item.SetHeader(count > 0 ? $"已查看 {count} 张图像" : "已查看图像");
        }
    }

    private static int CountImageMentions(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 0;
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" })
        {
            var index = 0;
            while ((index = text.IndexOf(extension, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                index += extension.Length;
            }
        }

        return count;
    }

    private void FinishTurn(string status)
    {
        IsBusy = false;
        CoreStatusText = IsCoreReady ? "KL Core 就绪" : CoreStatusText;
        if (_activeReasoning is { IsRunning: true } reasoning)
        {
            reasoning.Stop(status == "完成" ? "完成" : status);
        }
        if (_activeAssistant is { IsRunning: true } assistant)
        {
            assistant.Complete(status == "完成" ? string.Empty : status);
        }
        if (_activeToolItem is { IsRunning: true } tool)
        {
            tool.Stop(status == "完成" ? "成功" : status);
        }
        foreach (var pending in _toolItemsByCallId.Values)
        {
            if (pending.IsRunning)
            {
                pending.Stop(status == "完成" ? "成功" : status);
            }
        }
        _toolItemsByCallId.Clear();
        _activeToolItem = null;
        _activeAssistant = null;
        _activeReasoning = null;
        IsApprovalOpen = false;
        _activeApprovalId = null;
        if (_activeSession is not null)
        {
            // A completed reply is one of the two moments that may reorder 最近.
            _activeSession.UpdatedAt = DateTimeOffset.Now;
        }
        SyncStoredMessages();
        UpdateSessionOrder();
        SaveState();
    }

    private async Task CloseCoreSessionQuietlyAsync()
    {
        CancelAutoReconnect();
        try
        {
            await _core.CloseSessionAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Closing a stale core connection must not block the UI.
        }
        finally
        {
            _dispatch(() => IsCoreReadOnly = false);
        }
    }

    public async Task ShutdownAsync()
    {
        _turnCancellation?.Cancel();
        CancelAutoReconnect();
        await CloseCoreSessionQuietlyAsync().ConfigureAwait(false);
        await _core.DisposeAsync().ConfigureAwait(false);
    }

    private void CancelAutoReconnect()
    {
        var cancellation = _reconnectCancellation;
        _reconnectCancellation = null;
        _isAutoReconnecting = false;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void SetCoreStatus(string message) => CoreStatusText = message;

    /// <summary>Test hook: auto-resolves approvals when enabled by the harness.</summary>
    private void ScheduleEndToEndApproval()
    {
        var decision = Environment.GetEnvironmentVariable("KL_GUI_E2E_APPROVAL");
        if (string.IsNullOrWhiteSpace(decision))
        {
            return;
        }

        var delay = int.TryParse(
            Environment.GetEnvironmentVariable("KL_GUI_E2E_APPROVAL_DELAY_MS"),
            out var parsed)
            ? parsed
            : 300;
        _ = Task.Run(async () =>
        {
            await Task.Delay(delay).ConfigureAwait(false);
            _dispatch(() => _ = RespondApprovalAsync(decision));
        });
    }

    private void SetCoreFailure(string message)
    {
        IsCoreReady = false;
        CoreStatusText = message;
        CoreBannerText = message;
        OnPropertyChanged(nameof(ShowCoreBanner));
    }

    private static string DescribeProgress(string progress) => progress switch
    {
        "connecting" => "正在连接本地 KL Core…",
        "handshaking" => "正在与 KL Core 握手…",
        "opening-session" => "正在打开会话…",
        "session-ready" => "KL Core 就绪",
        _ => "正在连接 KL Core…"
    };

    private static string MapApprovalMode(string permissionMode) => permissionMode switch
    {
        "完全访问" => "dontAsk",
        "帮我批准" => "auto",
        _ => "ask"
    };

    private static string NormalizeWorkspacePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim();
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(NormalizeWorkspacePath(left), NormalizeWorkspacePath(right), StringComparison.OrdinalIgnoreCase);

    private static string ResolveCorePathText()
    {
        try
        {
            return CoreLocator.Find(null);
        }
        catch (Exception error) when (error is FileNotFoundException or InvalidOperationException)
        {
            return "未找到 kl-core（请构建 Core 或设置 KL_CORE_PATH）";
        }
    }

    private static string ResolveCoreHomeText()
    {
        var home = Environment.GetEnvironmentVariable("KL_HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            return Path.GetFullPath(home);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile) ? "—" : Path.Combine(profile, ".kl");
    }
}
