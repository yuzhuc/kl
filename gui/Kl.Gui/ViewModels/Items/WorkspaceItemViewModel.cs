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

public sealed class WorkspaceItemViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isVisible = true;
    private bool _hasVisibleSessions = true;
    private bool _isSearchActive;
    private string _pathLabel = string.Empty;

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

        Sessions.CollectionChanged += (_, _) =>
        {
            RefreshPreview();
            OnPropertyChanged(nameof(HasNoSessions));
        };
    }

    public WorkspaceState State { get; }
    public ObservableCollection<SessionItemViewModel> Sessions { get; }
    public string Id => State.Id;
    public string Name => State.Name;
    public string Path => State.Path;

    /// <summary>
    /// Shown only when two projects share a name, so the working directory can be
    /// told apart from a same-named sibling (e.g. "kl" vs "kl · D:\kl").
    /// </summary>
    public string PathLabel
    {
        get => _pathLabel;
        private set
        {
            if (SetProperty(ref _pathLabel, value))
            {
                OnPropertyChanged(nameof(HasPathLabel));
            }
        }
    }

    public bool HasPathLabel => !string.IsNullOrEmpty(PathLabel);

    public void SetPathLabel(string value) => PathLabel = value;

    /// <summary>Codex-style project preview details.</summary>
    public string ChatCountLabel => $"{Sessions.Count} 次聊天";

    public bool HasNoSessions => Sessions.Count == 0;

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
