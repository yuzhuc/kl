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
    public async Task SelectWorkspaceAsync(WorkspaceItemViewModel workspace)
    {
        // Re-clicking the selected project must not reopen its latest session
        // (that clears the composer draft); only do it when nothing is open.
        if (IsBusy || (workspace.Id == _activeWorkspace.Id && _activeSession is not null))
        {
            return;
        }

        _activeWorkspace.IsSelected = false;
        _activeWorkspace = workspace;
        _activeWorkspace.IsSelected = true;
        _activeWorkspace.IsExpanded = IsWorkMode;
        _state.ActiveWorkspaceId = workspace.Id;
        OnPropertyChanged(nameof(WorkspaceName));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(WorkspacePath));
        OnPropertyChanged(nameof(CanRunReview));

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
                _activeWorkspace.IsExpanded = IsWorkMode;
                _state.ActiveWorkspaceId = next.Id;
                OnPropertyChanged(nameof(WorkspaceName));
                OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateTitle));
                OnPropertyChanged(nameof(WorkspacePath));
                OnPropertyChanged(nameof(CanRunReview));
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
        RefreshWorkspacePathLabels();
        SaveState();
    }

    public bool AddWorkspace(string path)
    {
        string fullPath;
        if (IsRemoteCore)
        {
            var trimmed = path.Trim();
            if (trimmed.Length == 0 || trimmed.Any(char.IsControl))
            {
                ErrorMessage = Loc.T("工作区路径无效。");
                return false;
            }
            fullPath = trimmed.Length > 1 ? trimmed.TrimEnd('/', '\\') : trimmed;
        }
        else
        {
            try
            {
                fullPath = Path.GetFullPath(path).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (Exception error) when (error is ArgumentException
                or NotSupportedException
                or PathTooLongException)
            {
                ErrorMessage = Loc.T("工作区路径无效。");
                return false;
            }

            if (!Directory.Exists(fullPath))
            {
                ErrorMessage = Loc.T("工作区目录不存在。");
                return false;
            }
        }

        var existing = Workspaces.FirstOrDefault(workspace =>
            string.Equals(workspace.Path, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _ = SelectWorkspaceAsync(existing);
            return true;
        }

        var name = WorkspaceNameFromPath(fullPath);
        var state = new WorkspaceState
        {
            Name = name,
            Path = fullPath,
            IsExpanded = IsWorkMode
        };
        _state.Workspaces.Add(state);
        var workspaceVm = new WorkspaceItemViewModel(state);
        Workspaces.Add(workspaceVm);
        RefreshWorkspacePathLabels();
        OnPropertyChanged(nameof(WorkspaceCount));
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowNoSessions));
        _ = InitializeWorkspaceAsync(fullPath);
        _ = SelectWorkspaceAsync(workspaceVm);
        SaveState();
        return true;
    }

    /// <summary>Forks the open session in Core and opens the new branch.</summary>
    public async Task ForkActiveSessionAsync()
    {
        if (IsBusy || IsCoreBusy)
        {
            return;
        }

        if (_activeSession is null || _activeSessionItem is null)
        {
            ReportError(Loc.T("请先打开一个 KL Core 会话，再复制为分支。"));
            return;
        }

        if (!await EnsureCoreSessionAsync(CancellationToken.None).ConfigureAwait(true))
        {
            ReportError(Loc.T("无法连接 KL Core 会话。"));
            return;
        }

        string newSessionId;
        IsCoreBusy = true;
        try
        {
            newSessionId = await _core.ForkAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception error) when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            ReportError(Loc.F("复制会话失败：{0}", (error.Message)));
            return;
        }
        finally
        {
            IsCoreBusy = false;
        }

        if (string.IsNullOrWhiteSpace(newSessionId))
        {
            ReportError(Loc.T("KL Core 没有返回新会话 ID。"));
            return;
        }

        var state = new SessionState
        {
            Id = newSessionId,
            Title = Loc.F("{0} · 分支", (_activeSession.Title)),
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now
        };
        _activeWorkspace.State.Sessions.Insert(0, state);
        var item = new SessionItemViewModel(_activeWorkspace, state);
        _activeWorkspace.Sessions.Insert(0, item);
        SaveState(silent: true);
        // Opens the branch; it refuses while IsCoreBusy is set, so the RPC above
        // has already released it.
        await OpenSessionAsync(item).ConfigureAwait(true);
    }

    /// <summary>Copies sent attachments into the home store for later recovery.</summary>
    private void PersistAttachments(string sessionId, IReadOnlyList<AttachmentItemViewModel> attachments)
    {
        if (attachments.Count == 0)
        {
            return;
        }

        try
        {
            AttachmentStore.Save(
                _homeDirectory,
                sessionId,
                attachments.Select(attachment => (attachment.Name, attachment.Content)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The turn already carries the attachment inline; a failed copy is not fatal.
        }
    }

    /// <summary>Asks Core to compact the open session and reloads the transcript.</summary>
    public async Task CompactActiveSessionAsync()
    {
        if (IsBusy || IsCoreBusy)
        {
            return;
        }

        if (_activeSession is null)
        {
            ReportError(Loc.T("当前没有打开的 KL Core 会话。"));
            return;
        }

        // A restored sidebar row has no live core session until it is opened.
        if (!await EnsureCoreSessionAsync(CancellationToken.None).ConfigureAwait(true))
        {
            ReportError(Loc.T("无法连接 KL Core 会话。"));
            return;
        }

        IsCoreBusy = true;
        CoreStatusText = Loc.T("正在压缩上下文…");
        _suppressCompactionUntil = DateTimeOffset.Now.AddSeconds(5);
        try
        {
            var result = await _core.CompactAsync(CancellationToken.None).ConfigureAwait(true);
            var omitted = result.TryGetProperty("omitted_messages", out var value)
                && value.TryGetInt32(out var count)
                ? count
                : 0;
            CoreStatusText = omitted > 0
                ? Loc.F("上下文已压缩（省略 {0} 条消息）", (omitted))
                : Loc.T("上下文已压缩");
            await LoadHistoryAsync().ConfigureAwait(true);
            var summary = omitted > 0 ? Loc.F("省略 {0} 条消息", (omitted)) : Loc.T("无需省略消息");
            // LoadHistoryAsync posts its rebuild, so the marker must be posted after
            // it or the rebuilt transcript would clear it again.
            _dispatch(() =>
            {
                var marker = ChatItemViewModel.Tool("Context", summary, string.Empty, summary);
                marker.Complete("completed");
                Messages.Add(marker);
            });
        }
        catch (Exception error) when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            ReportError(Loc.F("压缩上下文失败：{0}", (error.Message)));
        }
        finally
        {
            IsCoreBusy = false;
        }
    }


    /// <summary>Markdown transcript of the active session, or an empty string when there is none.</summary>
    public string BuildSessionMarkdown() => _activeSession is null
        ? string.Empty
        : SessionMarkdown.Build(_activeSession.Title, WorkspacePath, _activeSession.Messages);

    public string SessionExportFileName
    {
        get
        {
            var title = string.IsNullOrWhiteSpace(_activeSession?.Title) ? Loc.T("KL 会话") : _activeSession.Title.Trim();
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                title = title.Replace(invalid, '-');
            }
            return title + ".md";
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
        OnPropertyChanged(nameof(IsEditingWorkspace));
        OnPropertyChanged(nameof(RenameSourcePath));
        OnPropertyChanged(nameof(RenameSourceParentPath));
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
            RefreshWorkspacePathLabels();
            if (workspace.Id == _activeWorkspace.Id)
            {
                OnPropertyChanged(nameof(WorkspaceName));
                OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateTitle));
            }
            CloseDialogs();
            SaveState();
            return;
        }

        if (_dialogTarget is null)
        {
            return;
        }

        var target = _dialogTarget;
        var sessionId = target.Id;
        var oldTitle = target.Title;
        target.Title = title;
        target.RefreshTime();
        if (target.Id == _activeSession?.Id)
        {
            SessionTitle = title;
        }
        CloseDialogs();
        UpdateSessionOrder();
        SaveState();
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _ = PushRenameAsync(target, sessionId, title, oldTitle);
        }
    }

    /// <summary>Renames the session in Core and restores the local title if that fails.</summary>
    private async Task PushRenameAsync(
        SessionItemViewModel session,
        string sessionId,
        string title,
        string oldTitle)
    {
        try
        {
            await _core.RenameSessionAsync(sessionId, title, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or NotSupportedException)
        {
            _dispatch(() =>
            {
                session.Title = oldTitle;
                if (session.Id == _activeSession?.Id)
                {
                    SessionTitle = oldTitle;
                }
                UpdateSessionOrder();
                SaveState();
                SetCoreStatus(Loc.F("重命名失败：{0}", (error.Message)));
            });
        }
    }

    public void BeginDelete(SessionItemViewModel session)
    {
        if (IsBusy)
        {
            return;
        }
        _dialogTarget = session;
        DeletePrompt = Loc.F("删除“{0}”？此操作无法撤销。", (session.Title));
        IsDeleteDialogOpen = true;
    }

    public void ConfirmDelete()
    {
        if (_dialogTarget is null)
        {
            return;
        }

        var target = _dialogTarget;
        var sessionId = target.Id;
        target.Workspace.State.Sessions.Remove(target.State);
        target.Workspace.Sessions.Remove(target);
        CleanupSessions.Remove(target);
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
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _ = PushDeleteAsync(target, sessionId);
        }
    }

    /// <summary>Deletes the rollout in Core and restores the row if that fails.</summary>
    private async Task PushDeleteAsync(SessionItemViewModel session, string sessionId)
    {
        try
        {
            await _core.DeleteSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            _dispatch(() =>
            {
                if (!session.Workspace.State.Sessions.Contains(session.State))
                {
                    session.Workspace.State.Sessions.Insert(0, session.State);
                    session.Workspace.Sessions.Insert(0, session);
                    SaveState();
                }
                SetCoreStatus(Loc.F("删除失败：{0}", (error.Message)));
            });
        }
    }

    private bool _isCleanupDialogOpen;

    public bool IsCleanupDialogOpen
    {
        get => _isCleanupDialogOpen;
        private set => SetProperty(ref _isCleanupDialogOpen, value);
    }

    public ObservableCollection<SessionItemViewModel> CleanupSessions { get; } = [];
    public bool HasCleanupSessions => CleanupSessions.Count > 0;

    /// <summary>Lists chats that have seen no activity for 30 days.</summary>
    public void OpenCleanupDialog()
    {
        var cutoff = DateTimeOffset.Now.AddDays(-30);
        CleanupSessions.Clear();
        foreach (var session in Workspaces
                     .SelectMany(workspace => workspace.Sessions)
                     .Where(item => !item.IsArchived
                         && item.State.UpdatedAt < cutoff
                         && item.Id != _activeSession?.Id)
                     .OrderBy(item => item.State.UpdatedAt))
        {
            CleanupSessions.Add(session);
        }
        OnPropertyChanged(nameof(HasCleanupSessions));
        IsCleanupDialogOpen = true;
    }

    public void CloseCleanupDialog() => IsCleanupDialogOpen = false;

    public void CloseDialogs()
    {
        _dialogTarget = null;
        _renameWorkspaceTarget = null;
        IsRenameDialogOpen = false;
        IsDeleteDialogOpen = false;
        IsCreateProjectDialogOpen = false;
        IsCleanupDialogOpen = false;
        IsCloseConfirmOpen = false;
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
                skipped.Add(Loc.F("最多附加 {0} 个文件", (MaxAttachmentCount)));
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
                skipped.Add(Loc.F("{0}（暂不支持该格式）", (Path.GetFileName(fullPath))));
                continue;
            }
            try
            {
                var info = new FileInfo(fullPath);
                if (!info.Exists)
                {
                    skipped.Add(Loc.F("{0}（文件不存在）", (info.Name)));
                    continue;
                }
                if (info.Length > MaxAttachmentCharacters * 4L)
                {
                    skipped.Add(Loc.F("{0}（文件过大）", (info.Name)));
                    continue;
                }
                var content = File.ReadAllText(fullPath);
                if (content.Length > MaxAttachmentCharacters)
                {
                    content = content[..MaxAttachmentCharacters] + Loc.T("\n\n[文件内容已截断]");
                }
                PendingAttachments.Add(new AttachmentItemViewModel(fullPath, info.Name, content));
                OnPropertyChanged(nameof(CanQueueSend));
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
            ErrorMessage = Loc.T("部分附件未添加：") + string.Join("；", skipped);
        }
        else if (added == 0 && PendingAttachments.Count == 0)
        {
            ErrorMessage = Loc.T("没有可添加的文本附件。");
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
        OnPropertyChanged(nameof(CanQueueSend));
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
        return string.IsNullOrWhiteSpace(prompt)
            ? $"附件：{names}" // i18n-keep: model-facing prompt stays Chinese
            : $"{prompt}\n\n附件：{names}"; // i18n-keep
    }

}
