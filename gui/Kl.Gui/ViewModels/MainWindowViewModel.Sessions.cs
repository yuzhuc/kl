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
    private void NormalizeState()
    {
        var pinnedOrderSeed = 0;
        _state.Endpoint = string.IsNullOrWhiteSpace(_state.Endpoint) ? "https://api.deepseek.com" : _state.Endpoint.Trim();
        _state.Model = string.IsNullOrWhiteSpace(_state.Model) ? "deepseek-chat" : _state.Model.Trim();
        // Migrate legacy Chinese labels persisted by older builds.
        _state.PermissionMode = _state.PermissionMode switch
        {
            "完全访问" or "full" => "full", // i18n-keep
            "帮我批准" or "auto" => "auto", // i18n-keep
            "请求批准" or "按需确认" or "ask" => "ask", // i18n-keep
            _ => "full"
        };
        _state.SendShortcut = _state.SendShortcut == "ctrlEnter" ? "ctrlEnter" : "enter";
        _state.CloseWindowAction = _state.CloseWindowAction is "always" or "never" ? _state.CloseWindowAction : "shortcut";
        _state.FileOpenTarget = string.Equals(_state.FileOpenTarget, "vscode", StringComparison.OrdinalIgnoreCase)
            ? "vscode"
            : "system";
        _state.TurnNotifications = _state.TurnNotifications is "always" or "never" ? _state.TurnNotifications : "unfocused";
        _state.AccentColor = AccentColors.Contains(_state.AccentColor, StringComparer.OrdinalIgnoreCase)
            ? _state.AccentColor
            : AccentColors[0];
        _state.ThemeMode = _state.ThemeMode is "light" or "system" ? _state.ThemeMode : "dark";
        _state.LightAccentColor = AccentColors.Contains(_state.LightAccentColor, StringComparer.OrdinalIgnoreCase)
            ? _state.LightAccentColor
            : AccentColors[1];
        _state.DarkAccentColor = AccentColors.Contains(_state.DarkAccentColor, StringComparer.OrdinalIgnoreCase)
            ? _state.DarkAccentColor
            : AccentColors[0];
        _state.LightContrast = Math.Clamp(_state.LightContrast, 0, 100);
        _state.DarkContrast = Math.Clamp(_state.DarkContrast, 0, 100);
        _state.DiffMarkerMode = _state.DiffMarkerMode == "markers" ? "markers" : "color";
        _state.TerminalShell = TerminalShells.Contains(_state.TerminalShell) ? _state.TerminalShell : TerminalShells[0];
        _state.TerminalPosition = _state.TerminalPosition == "right" ? "right" : "bottom";
        _state.TerminalPanelSize = _state.TerminalPanelSize is >= 180 and <= 600 ? _state.TerminalPanelSize : 0;
        _state.ContentCodeFontSize = Math.Clamp(_state.ContentCodeFontSize, 9, 20);
        _state.UiFontIndex = Math.Clamp(_state.UiFontIndex, 0, 1);
        _state.ContentFontIndex = Math.Clamp(_state.ContentFontIndex, 0, 2);
        _state.CodeFontIndex = Math.Clamp(_state.CodeFontIndex, 0, 2);
        _state.UiFontWeightIndex = Math.Clamp(_state.UiFontWeightIndex, 0, 2);
        _state.ContentFontWeightIndex = Math.Clamp(_state.ContentFontWeightIndex, 0, 2);
        _state.CodeFontWeightIndex = Math.Clamp(_state.CodeFontWeightIndex, 0, 2);
        _state.Contrast = Math.Clamp(_state.Contrast, 0, 100);
        _state.ReduceMotion = _state.ReduceMotion is "on" or "off" ? _state.ReduceMotion : "system";
        _state.FollowUpMode = _state.FollowUpMode == "steer" ? "steer" : "queue";
        _state.ReviewMode = _state.ReviewMode is "quick" or "strict" ? _state.ReviewMode : "standard";
        _state.GitBranchPrefix = (_state.GitBranchPrefix ?? string.Empty).Trim();
        _state.ReviewInstructions ??= string.Empty;
        _state.WorktreeRoot ??= string.Empty;
        _state.AutoArchiveDays = _state.AutoArchiveDays > 0 ? Math.Clamp(_state.AutoArchiveDays, 7, 3650) : 0;
        // Codex renders conversation text at 14px; 15 came from an earlier calibration.
        _state.ContentFontSize = _state.ContentFontSize is 13 or 14 or 15 ? 14 : _state.ContentFontSize;
        _state.ContentFontSize = _state.ContentFontSize is >= 13 and <= 16 ? _state.ContentFontSize : 14;
        _state.InteractionMode = string.Equals(_state.InteractionMode, "chat", StringComparison.OrdinalIgnoreCase)
            ? "chat"
            : "work";
        _state.PinnedSort = string.Equals(_state.PinnedSort, "recent", StringComparison.OrdinalIgnoreCase)
            ? "recent"
            : "manual";
        _state.NotificationFilter = _state.NotificationFilter is "priority" or "pinned" or "automations"
            ? _state.NotificationFilter
            : "priority";
        _notificationFilter = _state.NotificationFilter;
        _state.Workspaces ??= [];
        _state.Automations ??= [];
        _state.ModelReasoningEfforts ??= [];

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
                ? WorkspaceNameFromPath(workspace.Path)
                : workspace.Name;
            workspace.Sessions ??= [];
            // Messages live in KL Core; a gui-state session may legitimately have
            // none yet (fresh session, or one that was never opened in this client).
            // Dropping rows here used to lose pinned/archived flags on every restart.
            foreach (var session in workspace.Sessions)
            {
                session.Id = string.IsNullOrWhiteSpace(session.Id) ? Guid.NewGuid().ToString("N") : session.Id;
                session.Title = string.IsNullOrWhiteSpace(session.Title) ? Loc.T("新会话") : session.Title;
                session.Messages ??= [];
                if (session.IsPinned)
                {
                    pinnedOrderSeed++;
                    if (session.PinnedOrder == 0)
                    {
                        session.PinnedOrder = pinnedOrderSeed;
                    }
                    else
                    {
                        pinnedOrderSeed = Math.Max(pinnedOrderSeed, session.PinnedOrder);
                    }
                }
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
                Name = Path.GetFileName(workspacePath.TrimEnd(Path.DirectorySeparatorChar)) ?? Loc.T("工作区"),
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
            automation.Name = string.IsNullOrWhiteSpace(automation.Name) ? Loc.T("未命名自动化") : automation.Name.Trim();
            automation.Prompt ??= string.Empty;
            automation.LastResult ??= string.Empty;
            automation.LastStatus = automation.LastStatus switch
            {
                "running" or "运行中…" => "running", // i18n-keep
                "completed" or "完成" => "completed", // i18n-keep
                "failed" or "失败" => "failed", // i18n-keep
                _ => string.Empty
            };
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
            Workspaces.Add(new WorkspaceItemViewModel(workspaceState) { IsExpanded = IsWorkMode });
        }

        RefreshWorkspacePathLabels();
    }

    /// <summary>
    /// Same-named projects get a dim path suffix; unique names stay clean.
    /// </summary>
    private void RefreshWorkspacePathLabels()
    {
        var duplicated = Workspaces
            .GroupBy(workspace => workspace.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToHashSet();
        foreach (var workspace in Workspaces)
        {
            workspace.SetPathLabel(duplicated.Contains(workspace) ? workspace.Path : string.Empty);
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
        _activeWorkspace.IsExpanded = IsWorkMode;
        _activeSession = session;
        _activeSessionItem = workspace.Sessions.First(item => item.Id == session.Id);
        _activeSessionItem.IsSelected = true;
        if (_activeSessionItem.IsUnread)
        {
            _activeSessionItem.IsUnread = false;
            SaveState(silent: true);
        }
        _state.ActiveWorkspaceId = workspace.Id;
        _state.ActiveSessionId = session.Id;
        SessionTitle = session.Title;
        Composer = string.Empty;
        ClearAttachments();
        LoadMessages(session);
        OnPropertyChanged(nameof(WorkspaceName));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(WorkspacePath));
        OnPropertyChanged(nameof(CanRunReview));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowNoSessions));
        OnPropertyChanged(nameof(UsageText));
        OnPropertyChanged(nameof(HasUsageText));
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
        ResetComposerModel();
        _activeSession = new SessionState
        {
            Title = Loc.T("新会话"),
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now
        };
        _state.ActiveWorkspaceId = _activeWorkspace.Id;
        _state.ActiveSessionId = null;
        SessionTitle = Loc.T("新会话");
        Messages.Clear();
        Composer = string.Empty;
        ClearAttachments();
        OnPropertyChanged(nameof(WorkspaceName));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(WorkspacePath));
        OnPropertyChanged(nameof(CanRunReview));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(UsageText));
        OnPropertyChanged(nameof(HasUsageText));
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
            Messages.Add(ChatItemViewModel.FromStored(message, session.UpdatedAt));
        }

        GroupToolMessages();
    }

    /// <summary>
    /// Codex groups only repeated calls: a single tool call renders on its own row,
    /// two or more consecutive calls of the same kind collapse into a 运行了命令 group.
    /// </summary>
    private void AddToolItem(ChatItemViewModel tool)
    {
        var kind = tool.EffectiveKindLabel();
        if (Messages.Count > 0
            && Messages[^1] is { IsToolGroup: true } group
            && string.Equals(group.GroupKind, kind, StringComparison.Ordinal))
        {
            group.AddChild(tool);
            return;
        }

        if (Messages.Count > 0
            && Messages[^1] is { IsTool: true } previous
            && !previous.IsToolGroup
            && string.Equals(previous.EffectiveKindLabel(), kind, StringComparison.Ordinal))
        {
            var promoted = ChatItemViewModel.ToolGroup(kind);
            promoted.AddChild(previous);
            promoted.AddChild(tool);
            Messages[^1] = promoted;
            return;
        }

        Messages.Add(tool);
    }

    private void GroupToolMessages()
    {
        if (Messages.Count == 0)
        {
            return;
        }

        var flat = Messages.ToList();
        Messages.Clear();
        var buffer = new List<ChatItemViewModel>();
        foreach (var item in flat)
        {
            if (item.IsTool)
            {
                if (buffer.Count > 0 && !string.Equals(buffer[0].EffectiveKindLabel(), item.EffectiveKindLabel(), StringComparison.Ordinal))
                {
                    FlushToolBuffer(buffer);
                }

                buffer.Add(item);
            }
            else
            {
                FlushToolBuffer(buffer);
                Messages.Add(item);
            }
        }

        FlushToolBuffer(buffer);
    }

    private void FlushToolBuffer(List<ChatItemViewModel> buffer)
    {
        if (buffer.Count == 0)
        {
            return;
        }

        if (buffer.Count == 1)
        {
            Messages.Add(buffer[0]);
        }
        else
        {
            var group = ChatItemViewModel.ToolGroup(buffer[0].EffectiveKindLabel());
            foreach (var item in buffer)
            {
                group.AddChild(item);
            }

            Messages.Add(group);
        }

        buffer.Clear();
    }

    private void SyncStoredMessages()
    {
        if (_activeSession is null)
        {
            return;
        }
        _activeSession.Messages = Messages
            .SelectMany(item => (IEnumerable<ChatItemViewModel>)(item.IsToolGroup ? item.Children : new[] { item }))
            .Select(item => item.ToStored())
            .ToList();
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
        _activeReasoning.Start("running");
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
        _activeAssistant.Start("running");
        Messages.Add(_activeAssistant);
        return _activeAssistant;
    }

    private void AddError(string message)
    {
        ErrorMessage = message;
        Messages.Add(ChatItemViewModel.Error(message));
    }
}
