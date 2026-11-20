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

/// <summary>One selectable reasoning level in the model form.</summary>
public sealed class ReasoningLevelChoice : ObservableObject
{
    private readonly Action _changed;
    private bool _isEnabled;

    public ReasoningLevelChoice(string value, string label, bool isEnabled, Action changed)
    {
        Value = value;
        Label = label;
        _isEnabled = isEnabled;
        _changed = changed;
    }

    public string Value { get; }
    public string Label { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                _changed();
            }
        }
    }
}
