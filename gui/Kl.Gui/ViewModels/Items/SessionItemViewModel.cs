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
                return Loc.T("刚刚");
            }

            if (elapsed.TotalHours < 1)
            {
                return Loc.F("{0} 分钟前", ((int)elapsed.TotalMinutes));
            }

            if (elapsed.TotalDays < 1)
            {
                return Loc.F("{0} 小时前", ((int)elapsed.TotalHours));
            }

            if (elapsed.TotalDays < 30)
            {
                return Loc.F("{0} 天前", ((int)elapsed.TotalDays));
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

    public bool IsUnread
    {
        get => State.IsUnread;
        set
        {
            if (State.IsUnread == value)
            {
                return;
            }

            State.IsUnread = value;
            OnPropertyChanged();
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
