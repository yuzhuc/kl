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

namespace Kl.Gui.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    public AppState State => _state;
    public SessionItemViewModel? ActiveSessionItem => _activeSessionItem;

    public bool IsEmpty => Messages.Count == 0;
    public bool HasConversation => !IsEmpty;
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

    public bool HasPinnedSessions => PinnedSessions.Count > 0;

    private bool _isPinnedExpanded = true;

    public bool IsPinnedExpanded
    {
        get => _isPinnedExpanded;
        private set => SetProperty(ref _isPinnedExpanded, value);
    }

    public void TogglePinned() => IsPinnedExpanded = !IsPinnedExpanded;

    public bool IsPinnedSortByRecent =>
        string.Equals(_state.PinnedSort, "recent", StringComparison.OrdinalIgnoreCase);

    public bool IsPinnedSortByManual => !IsPinnedSortByRecent;

    public void SetPinnedSortMode(string mode)
    {
        var value = string.Equals(mode, "recent", StringComparison.OrdinalIgnoreCase) ? "recent" : "manual";
        if (string.Equals(_state.PinnedSort, value, StringComparison.Ordinal))
        {
            return;
        }

        _state.PinnedSort = value;
        OnPropertyChanged(nameof(IsPinnedSortByRecent));
        OnPropertyChanged(nameof(IsPinnedSortByManual));
        RefreshRecentSessions();
        SaveState();
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

    public void ToggleProjectSection()
    {
        IsProjectSectionExpanded = !IsProjectSectionExpanded;
        SaveState();
    }
}
