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
    public async Task CancelAsync()
    {
        if (!IsBusy)
        {
            return;
        }

        _turnCancellation?.Cancel();
        ClearQueuedTurns();
        try
        {
            await _core.CancelAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception error) when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            ErrorMessage = Loc.F("取消请求失败：{0}", (error.Message));
        }
    }

    public async Task SendAsync()
    {
        if (IsBusy)
        {
            if (IsFollowUpQueue && CanSend)
            {
                QueueTurn(Composer.Trim(), PendingAttachments.ToList());
            }
            return;
        }

        if (!CanSend)
        {
            return;
        }

        var prompt = Composer.Trim();
        var attachments = PendingAttachments.ToList();
        var rewriteTarget = _editingUserItem;
        var rewindSeq = rewriteTarget?.Seq;
        // Regenerate/resend keep the original model text; edits use the typed text.
        var modelPrompt = _rewindModelText ?? BuildModelPrompt(prompt, attachments);
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
            ErrorMessage = Loc.T("另一客户端持有该会话的写权限，当前窗口只能观察。");
            return;
        }

        Composer = string.Empty;
        ClearAttachments();

        var session = _activeSession;
        if (session is null)
        {
            ErrorMessage = Loc.T("当前没有可用会话。");
            return;
        }

        PersistAttachments(session.Id, attachments);

        var isFirstMessage = session.Messages.Count == 0;
        if (isFirstMessage)
        {
            session.Title = prompt.Length == 0 ? Loc.T("附件会话") : TitleFromPrompt(prompt);
        }
        SessionTitle = session.Title;
        session.UpdatedAt = DateTimeOffset.Now;
        _activeSessionItem?.RefreshTitle();
        _activeSessionItem?.RefreshTime();

        if (rewindSeq is not null && rewriteTarget is not null)
        {
            var targetIndex = Messages.IndexOf(rewriteTarget);
            if (targetIndex < 0)
            {
                rewindSeq = null;
            }
            else
            {
                while (Messages.Count > targetIndex)
                {
                    Messages.RemoveAt(Messages.Count - 1);
                }
            }
        }

        var userItem = ChatItemViewModel.User(displayPrompt, modelPrompt, DateTimeOffset.Now);
        _pendingUserItem = rewindSeq is not null ? null : userItem;
        Messages.Add(userItem);
        SyncStoredMessages();
        UpdateSessionOrder();
        SaveState();

        IsBusy = true;
        _turnCancellation = new CancellationTokenSource();
        try
        {
            _suppressRewindReload = rewindSeq is not null;
            var outcome = await _core.RunTurnAsync(
                modelPrompt,
                _turnCancellation.Token,
                rewindSeq).ConfigureAwait(true);
            if (outcome.Status == "failed" && !string.IsNullOrWhiteSpace(outcome.Text))
            {
                AddError(outcome.Text);
            }
        }
        catch (OperationCanceledException)
        {
            AddError(Loc.T("已停止生成本次回复。"));
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or NotSupportedException
            or FileNotFoundException)
        {
            AddError(ToUserFacingError(error));
            if (rewindSeq is not null)
            {
                // The core may already have truncated; reload so the UI matches.
                await LoadHistoryAsync().ConfigureAwait(true);
                Composer = displayPrompt;
            }
        }
        finally
        {
            if (IsBusy)
            {
                FinishTurn("completed");
            }
            _turnCancellation?.Dispose();
            _turnCancellation = null;
            _pendingUserItem = null;
            _suppressRewindReload = false;
            _rewindModelText = null;
            if (_editingUserItem is not null)
            {
                _editingUserItem = null;
                RaiseEditState();
            }
        }

        await DrainQueuedTurnsAsync().ConfigureAwait(true);
    }
}
