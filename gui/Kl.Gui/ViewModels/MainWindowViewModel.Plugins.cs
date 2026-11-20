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
    public async Task InstallPluginAsync(string path)
    {
        try
        {
            await _core.InstallPluginAsync(path, CancellationToken.None).ConfigureAwait(true);
            await RefreshPluginsAsync().ConfigureAwait(true);
            PluginStatus = "插件已载入 KL Core。";
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or NotSupportedException)
        {
            PluginStatus = $"插件文件已复制，但 KL Core 未能载入：{error.Message}";
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(target, relative));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: false);
        }
    }

    private IReadOnlyList<CorePluginRow>? _corePlugins;

    public void RefreshPlugins()
    {
        _corePlugins = null;
        ApplyPluginRows();
    }

    /// <summary>Merges the local manifests with Core's installed/enabled plugin list.</summary>
    public async Task RefreshPluginsAsync()
    {
        try
        {
            var result = await _core.ListPluginsAsync(CancellationToken.None).ConfigureAwait(false);
            var rows = ParseCorePlugins(result);
            _dispatch(() =>
            {
                _corePlugins = rows;
                ApplyPluginRows();
            });
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or NotSupportedException)
        {
            _dispatch(() => PluginStatus = $"无法从 KL Core 读取插件：{error.Message}");
        }
    }

    private void ApplyPluginRows()
    {
        IReadOnlyList<LocalPluginInfo> local;
        try
        {
            local = _pluginCatalogService.Load();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _allPlugins = [];
            PluginStatus = $"无法读取插件目录：{error.Message}";
            ApplyPluginFilter(keepStatus: true);
            return;
        }

        var rows = new List<PluginItemViewModel>();
        if (_corePlugins is { Count: > 0 } core)
        {
            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var plugin in core)
            {
                var manifest = local.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, plugin.Id, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                        Path.GetFileName(Path.GetDirectoryName(candidate.ManifestPath)),
                        plugin.Id,
                        StringComparison.OrdinalIgnoreCase));
                if (manifest is not null)
                {
                    matched.Add(manifest.ManifestPath);
                }

                rows.Add(new PluginItemViewModel(
                    plugin.Id,
                    manifest?.Name ?? plugin.Id,
                    manifest?.Version ?? plugin.Version,
                    manifest?.Description ?? plugin.Detail ?? plugin.KindLabel,
                    manifest?.ManifestPath ?? string.Empty,
                    plugin.KindLabel,
                    plugin.Enabled,
                    plugin.Healthy,
                    plugin.Enabled
                        ? plugin.Healthy ? "已启用" : "运行异常"
                        : "已停用"));
            }

            foreach (var manifest in local.Where(item => !matched.Contains(item.ManifestPath)))
            {
                rows.Add(new PluginItemViewModel(manifest)
                {
                    StatusText = "Core 未加载"
                });
            }
        }
        else
        {
            rows.AddRange(local.Select(manifest => new PluginItemViewModel(manifest)));
        }

        _allPlugins = rows;
        ApplyPluginFilter();
    }

    private static IReadOnlyList<CorePluginRow> ParseCorePlugins(JsonElement result)
    {
        var rows = new List<CorePluginRow>();
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("plugins", out var plugins)
            || plugins.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        foreach (var plugin in plugins.EnumerateArray())
        {
            if (!plugin.TryGetProperty("id", out var id) || id.GetString() is not { Length: > 0 } value)
            {
                continue;
            }

            var kind = plugin.TryGetProperty("kind", out var kindValue)
                ? kindValue.GetString() ?? "plugin"
                : "plugin";
            var version = plugin.TryGetProperty("version", out var versionValue)
                ? versionValue.GetString() ?? string.Empty
                : string.Empty;
            var enabled = !plugin.TryGetProperty("enabled", out var enabledValue)
                || enabledValue.ValueKind == JsonValueKind.True;
            var healthy = !plugin.TryGetProperty("healthy", out var healthyValue)
                || healthyValue.ValueKind == JsonValueKind.True;
            var detail = plugin.TryGetProperty("detail", out var detailValue)
                ? detailValue.GetString()
                : null;
            rows.Add(new CorePluginRow(
                value,
                string.Equals(kind, "provider", StringComparison.OrdinalIgnoreCase) ? "Provider" : "工具",
                version,
                enabled,
                healthy,
                detail));
        }

        return rows;
    }

    /// <summary>Enables or disables a plugin through Core and updates the row.</summary>
    public async Task SetPluginEnabledAsync(PluginItemViewModel plugin, bool enabled)
    {
        if (!plugin.CanToggle || plugin.Enabled == enabled)
        {
            return;
        }

        try
        {
            await _core.SetPluginEnabledAsync(plugin.Id, enabled, CancellationToken.None)
                .ConfigureAwait(true);
            plugin.Enabled = enabled;
            plugin.StatusText = enabled ? "已启用" : "已停用";
            PluginStatus = $"已{(enabled ? "启用" : "停用")} {plugin.Name}。";
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or NotSupportedException)
        {
            plugin.Enabled = !enabled;
            PluginStatus = $"无法更新 {plugin.Name}：{error.Message}";
        }
    }

    private sealed record CorePluginRow(
        string Id,
        string KindLabel,
        string Version,
        bool Enabled,
        bool Healthy,
        string? Detail);

    private void ApplyPluginFilter(bool keepStatus = false)
    {
        var query = _pluginSearch.Trim();
        var matches = query.Length == 0
            ? _allPlugins
            : _allPlugins.Where(plugin =>
                plugin.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || plugin.Description.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        Plugins.Clear();
        foreach (var plugin in matches)
        {
            Plugins.Add(plugin);
        }
        if (!keepStatus)
        {
            PluginStatus = _allPlugins.Count == 0
                ? "尚未发现本地插件。将插件目录放入上方路径后点击刷新。"
                : query.Length == 0
                    ? $"已发现 {_allPlugins.Count} 个本地插件。"
                    : $"找到 {Plugins.Count} 个匹配插件。";
        }
        OnPropertyChanged(nameof(HasPlugins));
        OnPropertyChanged(nameof(ShowNoPlugins));
        OnPropertyChanged(nameof(ShowNoPluginMatches));
        OnPropertyChanged(nameof(PluginDirectoryPath));
    }
}
