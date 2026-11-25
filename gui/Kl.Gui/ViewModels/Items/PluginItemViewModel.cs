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

public sealed class PluginItemViewModel : ObservableObject
{
    private bool _enabled;
    private string _statusText = Loc.T("本地文件");

    public PluginItemViewModel(LocalPluginInfo plugin)
    {
        Id = plugin.Name;
        Name = plugin.Name;
        Version = plugin.Version;
        Description = plugin.Description;
        ManifestPath = plugin.ManifestPath;
    }

    public PluginItemViewModel(
        string id,
        string name,
        string version,
        string description,
        string manifestPath,
        string kindLabel,
        bool enabled,
        bool healthy,
        string statusText)
    {
        Id = id;
        Name = name;
        Version = version;
        Description = description;
        ManifestPath = manifestPath;
        KindLabel = kindLabel;
        _enabled = enabled;
        _statusText = statusText;
        Healthy = healthy;
    }

    public string Id { get; set; }
    public string Name { get; }
    public string Version { get; }
    public string Description { get; }
    public string ManifestPath { get; }
    public string KindLabel { get; } = Loc.T("插件");
    public bool Healthy { get; } = true;
    public bool CanToggle => Id.Length > 0;

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }
}
