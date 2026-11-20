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

/// <summary>One editable keyboard shortcut on the shortcuts settings page.</summary>
public sealed class ShortcutRowViewModel : ObservableObject
{
    private string _gesture;
    private bool _isVisible = true;

    public ShortcutRowViewModel(
        string actionId,
        string title,
        string description,
        string defaultGesture,
        string gesture)
    {
        ActionId = actionId;
        Title = title;
        Description = description;
        DefaultGesture = defaultGesture;
        _gesture = gesture;
    }

    public string ActionId { get; }
    public string Title { get; }
    public string Description { get; }
    public string DefaultGesture { get; }

    public string Gesture
    {
        get => _gesture;
        set
        {
            if (SetProperty(ref _gesture, value))
            {
                OnPropertyChanged(nameof(HasGesture));
                OnPropertyChanged(nameof(GestureLabel));
            }
        }
    }

    public bool HasGesture => Gesture.Length > 0;
    public string GestureLabel => HasGesture ? Gesture : "未分配";

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }
}
