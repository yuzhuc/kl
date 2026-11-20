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

/// <summary>One provider row on the model-provider settings page.</summary>
public sealed class ProviderCardRow : ObservableObject
{
    private string _name;
    private string _note;
    private string _website;
    private bool _enabled;

    public ProviderCardRow(
        string id,
        string name,
        string note,
        string website,
        bool isDefault,
        bool enabled,
        bool keyConfigured,
        IReadOnlyList<CoreModelOption> models,
        IReadOnlyDictionary<string, string>? protocols = null)
    {
        Id = id;
        _name = name;
        _note = note;
        _website = website;
        IsDefault = isDefault;
        _enabled = enabled;
        KeyConfigured = keyConfigured;
        Models = models;
        Protocols = protocols ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public string Id { get; }
    public bool IsDefault { get; }
    public bool KeyConfigured { get; private set; }
    public IReadOnlyList<CoreModelOption> Models { get; private set; }
    public IReadOnlyDictionary<string, string> Protocols { get; private set; }

    public void SetModels(IReadOnlyList<CoreModelOption> models) => Models = models;

    public void SetProtocols(IReadOnlyDictionary<string, string> protocols) => Protocols = protocols;

    public void SetKeyConfigured(bool configured) => KeyConfigured = configured;

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                OnPropertyChanged(nameof(HasName));
            }
        }
    }

    public string Note
    {
        get => _note;
        set
        {
            if (SetProperty(ref _note, value))
            {
                OnPropertyChanged(nameof(HasNote));
            }
        }
    }

    public string Website
    {
        get => _website;
        set
        {
            if (SetProperty(ref _website, value))
            {
                OnPropertyChanged(nameof(HasWebsite));
            }
        }
    }

    public bool HasName => Name.Length > 0;
    public bool HasNote => Note.Length > 0;
    public bool HasWebsite => Website.Length > 0;

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }
}
