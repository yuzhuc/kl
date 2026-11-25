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
    public ObservableCollection<McpToolRow> McpTools { get; } = [];
    public string McpStatusText { get; private set; } = Loc.T("打开此页时从 KL Core 读取。");
    public bool ShowMcpTools => McpTools.Count > 0;

    /// <summary>Reads the MCP tool list from Core; the page stays read-only.</summary>
    public async Task RefreshMcpToolsAsync()
    {
        McpStatusText = Loc.T("正在读取…");
        OnPropertyChanged(nameof(McpStatusText));
        List<McpToolRow> rows;
        try
        {
            var result = await _core.ListMcpToolsAsync(CancellationToken.None);
            rows = ParseMcpTools(result);
            McpStatusText = rows.Count == 0 ? Loc.T("Core 当前没有可用的 MCP 工具。") : Loc.F("共 {0} 个 MCP 工具", (rows.Count));
        }
        catch (InvalidOperationException)
        {
            rows = [];
            McpStatusText = Loc.T("需要先打开一个聊天会话，然后点刷新。");
        }
        catch (Exception error)
        {
            rows = [];
            McpStatusText = Loc.F("无法读取 MCP 列表：{0}", (error.Message));
        }

        _dispatch(() =>
        {
            McpTools.Clear();
            foreach (var row in rows)
            {
                McpTools.Add(row);
            }

            OnPropertyChanged(nameof(McpStatusText));
            OnPropertyChanged(nameof(ShowMcpTools));
        });
    }

    private static List<McpToolRow> ParseMcpTools(JsonElement result)
    {
        var rows = new List<McpToolRow>();
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("tools", out var tools)
            || tools.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        foreach (var tool in tools.EnumerateArray())
        {
            var name = tool.TryGetProperty("name", out var nameValue)
                ? nameValue.GetString() ?? string.Empty
                : string.Empty;
            if (name.Length == 0)
            {
                continue;
            }

            var description = tool.TryGetProperty("description", out var descriptionValue)
                ? descriptionValue.GetString() ?? string.Empty
                : string.Empty;
            rows.Add(new McpToolRow(name, description));
        }

        return rows;
    }

    public ObservableCollection<ConfigValueRow> ConfigRows { get; } = [];
    public string ConfigPathText { get; private set; } = "—";
    public string CoreSandboxText { get; private set; } = "—";
    public string ConfigStatusText { get; private set; } = Loc.T("打开此页时从 KL Core 读取。");
    public bool ShowConfigRows => ConfigRows.Count > 0;
    public string KlHomeDirectory => ResolveCoreHomeText();
    public string ConfigFilePath => Path.Combine(KlHomeDirectory, "config.toml");

    public ObservableCollection<string> AgentPresetOptions { get; } = [];
    private readonly List<string> _agentPresetIds = [];

    /// <summary>0 = follow Core, 1 = fast, 2 = balanced, 3 = deep.</summary>
    public int QualityModeIndex
    {
        get => _state.QualityMode switch
        {
            "fast" => 1,
            "balanced" => 2,
            "deep" => 3,
            _ => 0
        };
        set
        {
            if (_loadingAgentSettings)
            {
                return;
            }
            var quality = value switch
            {
                1 => "fast",
                2 => "balanced",
                3 => "deep",
                _ => ""
            };
            if (_state.QualityMode == quality)
            {
                return;
            }
            _state.QualityMode = quality;
            SaveState();
            OnPropertyChanged();
            _core.SetTurnPreferences(_state.AgentPreset, _state.QualityMode);
        }
    }

    /// <summary>0 = follow Core, otherwise an entry from <c>agent.list</c>.</summary>
    public int AgentPresetIndex
    {
        get
        {
            var index = _agentPresetIds.IndexOf(_state.AgentPreset);
            return index < 0 ? 0 : index + 1;
        }
        set
        {
            if (_loadingAgentSettings)
            {
                return;
            }
            var id = value >= 1 && value <= _agentPresetIds.Count
                ? _agentPresetIds[value - 1]
                : string.Empty;
            if (_state.AgentPreset == id)
            {
                return;
            }
            _state.AgentPreset = id;
            SaveState();
            OnPropertyChanged();
            _core.SetTurnPreferences(_state.AgentPreset, _state.QualityMode);
        }
    }

    /// <summary>Reads <c>agent.list</c> so the preset picker reflects Core.</summary>
    public async Task RefreshAgentPresetsAsync()
    {
        _loadingAgentSettings = true;
        try
        {
            var result = await _core.ListAgentsAsync(CancellationToken.None).ConfigureAwait(true);
            var options = new List<string> { Loc.T("跟随 Core 默认") };
            _agentPresetIds.Clear();
            if (result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("agents", out var agents)
                && agents.ValueKind == JsonValueKind.Array)
            {
                foreach (var agent in agents.EnumerateArray())
                {
                    var id = JsonRead.GetOptionalString(agent, "id")?.Trim() ?? string.Empty;
                    if (id.Length == 0)
                    {
                        continue;
                    }
                    var name = JsonRead.GetOptionalString(agent, "name")?.Trim() ?? id;
                    _agentPresetIds.Add(id);
                    options.Add(name == id ? id : $"{name} ({id})");
                }
            }
            AgentPresetOptions.Clear();
            foreach (var option in options)
            {
                AgentPresetOptions.Add(option);
            }
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or FileNotFoundException)
        {
            // Agent presets are optional metadata; the default entry stays selected.
        }
        finally
        {
            _loadingAgentSettings = false;
            OnPropertyChanged(nameof(AgentPresetIndex));
        }
    }
    public bool RestoreLastSession
    {
        get => _state.RestoreLastSession;
        set
        {
            if (_state.RestoreLastSession == value)
            {
                return;
            }

            _state.RestoreLastSession = value;
            SaveState();
            OnPropertyChanged();
        }
    }

    /// <summary>Reads the redacted config from a one-shot Core connection.</summary>
    public async Task RefreshConfigAsync()
    {
        ConfigStatusText = Loc.T("正在读取…");
        OnPropertyChanged(nameof(ConfigStatusText));
        List<ConfigValueRow> rows;
        string path;
        string status;
        var sandbox = "—";
        try
        {
            var result = await _core.ReadConfigAsync(CancellationToken.None);
            path = result.TryGetProperty("path", out var pathValue)
                ? pathValue.GetString() ?? "—"
                : "—";
            if (result.TryGetProperty("config", out var config)
                && config.TryGetProperty("sandbox", out var sandboxConfig)
                && sandboxConfig.TryGetProperty("mode", out var mode)
                && mode.GetString() is { } modeValue)
            {
                sandbox = modeValue switch
                {
                    "strict" => Loc.T("严格隔离"),
                    "off" => Loc.T("无限制"),
                    _ => Loc.T("工作空间写入")
                };
            }
            rows = ParseConfig(result);
            status = rows.Count == 0 ? Loc.T("Core 未返回配置内容。") : string.Empty;
        }
        catch (Exception error)
        {
            rows = [];
            path = ConfigFilePath;
            status = Loc.F("无法读取配置：{0}", (error.Message));
        }

        _dispatch(() =>
        {
            ConfigPathText = path;
            CoreSandboxText = sandbox;
            OnPropertyChanged(nameof(CoreSandboxText));
            ConfigRows.Clear();
            foreach (var row in rows)
            {
                ConfigRows.Add(row);
            }

            ConfigStatusText = status;
            OnPropertyChanged(nameof(ConfigPathText));
            OnPropertyChanged(nameof(ConfigStatusText));
            OnPropertyChanged(nameof(ShowConfigRows));
        });
    }

    private static List<ConfigValueRow> ParseConfig(JsonElement result)
    {
        var rows = new List<ConfigValueRow>();
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("config", out var config)
            || config.ValueKind != JsonValueKind.Object)
        {
            return rows;
        }

        if (config.TryGetProperty("agent", out var agent) && agent.ValueKind == JsonValueKind.Object)
        {
            AddConfigRow(rows, agent, "provider", "Provider");
            AddConfigRow(rows, agent, "model", Loc.T("模型"));
            AddConfigRow(rows, agent, "approval_mode", Loc.T("审批模式"));
            AddConfigRow(rows, agent, "reasoning_effort", Loc.T("推理强度"));
        }

        if (result.TryGetProperty("providers", out var providers)
            && providers.ValueKind == JsonValueKind.Object
            && providers.TryGetProperty("providers", out var list)
            && list.ValueKind == JsonValueKind.Array)
        {
            var ids = new List<string>();
            foreach (var provider in list.EnumerateArray())
            {
                if (provider.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } value)
                {
                    ids.Add(value);
                }
            }

            if (ids.Count > 0)
            {
                rows.Add(new ConfigValueRow(Loc.T("提供方"), string.Join("、", ids)));
            }
        }

        return rows;
    }

    private static void AddConfigRow(List<ConfigValueRow> rows, JsonElement source, string property, string label)
    {
        if (!source.TryGetProperty(property, out var value))
        {
            return;
        }

        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => Loc.T("是"),
            JsonValueKind.False => Loc.T("否"),
            _ => string.Empty
        };
        if (text.Length > 0)
        {
            rows.Add(new ConfigValueRow(label, text));
        }
    }

    public string BackupStatusText { get; private set; } = string.Empty;
    public bool HasBackupStatus => BackupStatusText.Length > 0;

    public ObservableCollection<StorageFolderViewModel> StorageFolders { get; } = [];
    public string StorageTotalLabel { get; private set; } = Loc.T("KL 占用 —");
    public string StorageStatusText { get; private set; } = Loc.T("打开此页时计算。");
    public bool HasStorageScopeText => IsRemoteCore;
    public string StorageScopeText => IsRemoteCore
        ? Loc.T("以下统计的是本机 GUI 数据；远程 Core 的会话与附件在服务器上。")
        : string.Empty;
    public string StorageChatSizeLabel { get; private set; } = "—";
    public string StorageAttachmentSizeLabel { get; private set; } = "—";
    public string StoragePluginSizeLabel { get; private set; } = "—";
    public string StorageOtherSizeLabel { get; private set; } = "—";
    public ColumnDefinitions StorageBarColumns { get; private set; } = new("1*,1*,1*,1*");
    public bool HasStorageUsage { get; private set; }

    private sealed record StorageMeasurement(
        IReadOnlyList<StorageFolderViewModel> Folders,
        long TotalBytes,
        long ChatBytes,
        long AttachmentBytes,
        long PluginBytes,
        long OtherBytes);

    /// <summary>Measures the real home directory so 存储空间 is not a mock-up.</summary>
    public async Task RefreshStorageAsync()
    {
        StorageStatusText = Loc.T("正在计算…");
        OnPropertyChanged(nameof(StorageStatusText));
        var home = _homeDirectory;
        var measurement = await Task.Run(() => MeasureStorage(home)).ConfigureAwait(true);

        StorageFolders.Clear();
        foreach (var folder in measurement.Folders)
        {
            StorageFolders.Add(folder);
        }

        StorageTotalLabel = Loc.F("KL 占用 {0}", (StorageFolderViewModel.FormatBytes(measurement.TotalBytes)));
        StorageChatSizeLabel = StorageFolderViewModel.FormatBytes(measurement.ChatBytes);
        StorageAttachmentSizeLabel = StorageFolderViewModel.FormatBytes(measurement.AttachmentBytes);
        StoragePluginSizeLabel = StorageFolderViewModel.FormatBytes(measurement.PluginBytes);
        StorageOtherSizeLabel = StorageFolderViewModel.FormatBytes(measurement.OtherBytes);
        StorageBarColumns = BuildStorageBarColumns(
            measurement.ChatBytes,
            measurement.AttachmentBytes,
            measurement.PluginBytes,
            measurement.OtherBytes);
        HasStorageUsage = measurement.TotalBytes > 0;
        StorageStatusText = Loc.T("容量按文件当前大小统计。");

        OnPropertyChanged(nameof(StorageTotalLabel));
        OnPropertyChanged(nameof(StorageStatusText));
        OnPropertyChanged(nameof(StorageChatSizeLabel));
        OnPropertyChanged(nameof(StorageAttachmentSizeLabel));
        OnPropertyChanged(nameof(StoragePluginSizeLabel));
        OnPropertyChanged(nameof(StorageOtherSizeLabel));
        OnPropertyChanged(nameof(StorageBarColumns));
        OnPropertyChanged(nameof(HasStorageUsage));
    }

    private static StorageMeasurement MeasureStorage(string home)
    {
        var folders = new List<StorageFolderViewModel>();

        long AddFolder(string directory, string label)
        {
            var path = Path.Combine(home, directory);
            var bytes = DirectorySize(path);
            folders.Add(new StorageFolderViewModel(label, path, bytes));
            return bytes;
        }

        var chat = AddFolder("sessions", Loc.T("聊天（进行中）")) + AddFolder("archived_sessions", Loc.T("已归档聊天"));
        var attachments = AddFolder("attachments", Loc.T("附件"));
        var plugins = AddFolder("plugins", Loc.T("插件"));
        AddFolder("cache", Loc.T("缓存"));
        AddFolder("audit", Loc.T("审计日志"));
        AddFolder("gui", Loc.T("界面状态"));
        AddFolder("memories", Loc.T("记忆"));
        AddFolder("log", Loc.T("日志"));
        var total = DirectorySize(home);
        var other = Math.Max(0, total - chat - attachments - plugins);
        return new StorageMeasurement(folders, total, chat, attachments, plugins, other);
    }

    private static long DirectorySize(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // A file that disappears or is locked simply does not count.
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        return total;
    }

    private static ColumnDefinitions BuildStorageBarColumns(long chat, long attachments, long plugins, long other)
    {
        var total = chat + attachments + plugins + other;
        static int Weight(long value, long total) =>
            value <= 0 || total <= 0 ? 0 : Math.Max(1, (int)Math.Round(value * 1000.0 / total));
        return new ColumnDefinitions(
            $"{Weight(chat, total)}*,{Weight(attachments, total)}*,{Weight(plugins, total)}*,{Weight(other, total)}*");
    }

    /// <summary>Copies the GUI state file to a user-chosen backup location.</summary>
    public bool ExportStateBackup(string targetPath)
    {
        try
        {
            File.WriteAllText(targetPath, StateStore.ReadStateJson(_stateStore.Path));
            BackupStatusText = Loc.F("已导出到 {0}", (targetPath));
            OnPropertyChanged(nameof(BackupStatusText));
            OnPropertyChanged(nameof(HasBackupStatus));
            return true;
        }
        catch (Exception error)
        {
            BackupStatusText = Loc.F("导出失败：{0}", (error.Message));
            OnPropertyChanged(nameof(BackupStatusText));
            OnPropertyChanged(nameof(HasBackupStatus));
            return false;
        }
    }

    /// <summary>Replaces the GUI state file with a validated backup.</summary>
    public bool ImportStateBackup(string sourcePath)
    {
        try
        {
            using var document = JsonDocument.Parse(StateStore.ReadStateJson(sourcePath));
            var root = document.RootElement;
            var looksLikeState = root.ValueKind == JsonValueKind.Object
                && (root.TryGetProperty("Workspaces", out _)
                    || root.TryGetProperty("workspaces", out _));
            if (!looksLikeState)
            {
                throw new InvalidDataException(Loc.T("文件不是 KL 的 GUI 设置备份。"));
            }

            var statePath = _stateStore.Path;
            if (File.Exists(statePath))
            {
                File.Copy(
                    statePath,
                    $"{statePath}.before-import-{DateTimeOffset.Now:yyyyMMdd-HHmmss}",
                    overwrite: true);
            }

            var temporary = statePath + ".import.tmp";
            File.Copy(sourcePath, temporary, overwrite: true);
            File.Move(temporary, statePath, overwrite: true);
            _stateImported = true;
            BackupStatusText = Loc.T("已导入设置；重启 KL 后生效，重启前不会保存新的更改。原设置已自动备份。");
            OnPropertyChanged(nameof(BackupStatusText));
            OnPropertyChanged(nameof(HasBackupStatus));
            return true;
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException)
        {
            BackupStatusText = Loc.F("导入失败：{0}", (error.Message));
            OnPropertyChanged(nameof(BackupStatusText));
            OnPropertyChanged(nameof(HasBackupStatus));
            return false;
        }
    }

    private bool _isSkillsList;

    public bool IsSkillsList => IsPluginsPage && _isSkillsList;
    public bool IsPluginsList => IsPluginsPage && !_isSkillsList;
    public ObservableCollection<SkillRow> Skills { get; } = [];
    public string SkillsStatusText { get; private set; } = Loc.T("打开技能页时读取当前工作区。");
    public bool ShowSkills => Skills.Count > 0;

    public void ShowPluginsList()
    {
        if (!_isSkillsList)
        {
            return;
        }

        _isSkillsList = false;
        OnPropertyChanged(nameof(IsSkillsList));
        OnPropertyChanged(nameof(IsPluginsList));
        OnPropertyChanged(nameof(SectionTitle));
    }

    public void ShowSkillsList()
    {
        if (!_isSkillsList)
        {
            _isSkillsList = true;
            OnPropertyChanged(nameof(IsSkillsList));
            OnPropertyChanged(nameof(IsPluginsList));
            OnPropertyChanged(nameof(SectionTitle));
        }

        _ = RefreshSkillsAsync();
    }

    public ObservableCollection<AuditRow> AuditRecords { get; } = [];
    public string AuditStatusText { get; private set; } = Loc.T("打开此页时从 KL Core 读取。");
    public bool ShowAuditRecords => AuditRecords.Count > 0;

    private DateTimeOffset _lastAuditEventRefresh;

    /// <summary>Reads <c>audit.tail</c>; refreshing is also triggered by audit events.</summary>
    public async Task RefreshAuditAsync()
    {
        AuditStatusText = Loc.T("正在读取…");
        OnPropertyChanged(nameof(AuditStatusText));
        try
        {
            var result = await _core.AuditTailAsync(200, CancellationToken.None).ConfigureAwait(true);
            var rows = ParseAuditRecords(result);
            _dispatch(() =>
            {
                AuditRecords.Clear();
                foreach (var row in rows)
                {
                    AuditRecords.Add(row);
                }
                AuditStatusText = rows.Count == 0 ? Loc.T("还没有审计记录。") : Loc.F("共 {0} 条记录", (rows.Count));
                OnPropertyChanged(nameof(AuditStatusText));
                OnPropertyChanged(nameof(ShowAuditRecords));
            });
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException)
        {
            _dispatch(() =>
            {
                AuditStatusText = Loc.F("无法读取审计记录：{0}", (error.Message));
                OnPropertyChanged(nameof(AuditStatusText));
            });
        }
    }

    private static List<AuditRow> ParseAuditRecords(JsonElement result)
    {
        var rows = new List<AuditRow>();
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("records", out var records)
            || records.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        foreach (var record in records.EnumerateArray())
        {
            var timestamp = JsonRead.GetOptionalString(record, "timestamp") ?? string.Empty;
            var time = DateTimeOffset.TryParse(timestamp, out var parsed)
                ? parsed.ToLocalTime().ToString("HH:mm:ss")
                : timestamp;
            var session = JsonRead.GetOptionalString(record, "session_id") ?? string.Empty;
            if (session.Length > 8)
            {
                session = session[..8];
            }
            var ok = record.TryGetProperty("ok", out var okValue)
                && okValue.ValueKind == JsonValueKind.True;
            rows.Add(new AuditRow(
                time,
                JsonRead.GetOptionalString(record, "tool") ?? string.Empty,
                JsonRead.GetOptionalString(record, "action") ?? string.Empty,
                JsonRead.GetOptionalString(record, "decision") ?? string.Empty,
                session,
                JsonRead.GetOptionalString(record, "resource") ?? string.Empty,
                JsonRead.GetOptionalString(record, "error") ?? string.Empty,
                ok));
        }

        return rows;
    }

    /// <summary>Coalesces bursts of audit events into one tail refresh.</summary>
    private void OnAuditRecorded()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastAuditEventRefresh < TimeSpan.FromMilliseconds(500))
        {
            return;
        }
        _lastAuditEventRefresh = now;
        if (IsSettingsOpen && IsSettingsAudit)
        {
            _ = RefreshAuditAsync();
        }
    }
}
