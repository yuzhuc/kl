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
    /// <summary>Reads skills discovered by Core for the active workspace.</summary>
    public async Task RefreshSkillsAsync()
    {
        SkillsStatusText = "正在读取…";
        OnPropertyChanged(nameof(SkillsStatusText));
        List<SkillRow> rows;
        string status;
        try
        {
            var result = await _core.ListSkillsAsync(_activeWorkspace.Path, CancellationToken.None);
            rows = ParseSkills(result);
            status = rows.Count == 0 ? "当前工作区没有技能。" : $"共 {rows.Count} 个技能";
        }
        catch (Exception error)
        {
            rows = [];
            status = $"无法读取技能：{error.Message}";
        }

        _dispatch(() =>
        {
            Skills.Clear();
            foreach (var row in rows)
            {
                Skills.Add(row);
            }

            SkillsStatusText = status;
            OnPropertyChanged(nameof(SkillsStatusText));
            OnPropertyChanged(nameof(ShowSkills));
        });
    }

    private static List<SkillRow> ParseSkills(JsonElement result)
    {
        var rows = new List<SkillRow>();
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("skills", out var skills)
            || skills.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        foreach (var skill in skills.EnumerateArray())
        {
            var name = skill.TryGetProperty("name", out var nameValue)
                ? nameValue.GetString() ?? string.Empty
                : string.Empty;
            if (name.Length == 0)
            {
                continue;
            }

            var description = skill.TryGetProperty("description", out var descriptionValue)
                ? descriptionValue.GetString() ?? string.Empty
                : string.Empty;
            rows.Add(new SkillRow(name, description));
        }

        return rows;
    }

    private string _memoryScope = "workspace";
    private string _memoryName = string.Empty;
    private string _memoryContent = string.Empty;
    private string _memoryStatus = "打开此页时从 KL Core 读取。";

    public ObservableCollection<MemoryEntryRow> MemoryEntries { get; } = [];
    public bool IsMemoryScopeWorkspace => _memoryScope == "workspace";
    public bool IsMemoryScopeUser => _memoryScope == "user";
    public string MemoryScopeHint => IsMemoryScopeUser
        ? "用户记忆保存在 KL Home，对所有项目生效。"
        : "工作区记忆保存在当前项目，只在该项目内生效。";
    public bool ShowMemoryEntries => MemoryEntries.Count > 0;

    public string MemoryName
    {
        get => _memoryName;
        set
        {
            if (SetProperty(ref _memoryName, value))
            {
                OnPropertyChanged(nameof(CanSaveMemory));
            }
        }
    }

    public string MemoryContent
    {
        get => _memoryContent;
        set => SetProperty(ref _memoryContent, value);
    }

    public string MemoryStatus
    {
        get => _memoryStatus;
        private set => SetProperty(ref _memoryStatus, value);
    }

    public bool CanSaveMemory => _memoryName.Trim().Length > 0;

    public void SetMemoryScope(string scope)
    {
        var value = scope == "user" ? "user" : "workspace";
        if (_memoryScope == value)
        {
            return;
        }

        _memoryScope = value;
        OnPropertyChanged(nameof(IsMemoryScopeWorkspace));
        OnPropertyChanged(nameof(IsMemoryScopeUser));
        OnPropertyChanged(nameof(MemoryScopeHint));
        _ = RefreshMemoryAsync();
    }

    /// <summary>Lists memory entries for the open Core session and current scope.</summary>
    public async Task RefreshMemoryAsync()
    {
        if (!TryMemorySession(out var sessionId))
        {
            MemoryEntries.Clear();
            OnPropertyChanged(nameof(ShowMemoryEntries));
            SetMemoryStatus("先打开一个聊天会话，然后点刷新。");
            return;
        }

        try
        {
            var result = await _core.RequestAsync(
                "memory.list",
                new { session_id = sessionId, scope = _memoryScope },
                CancellationToken.None);
            var entries = ParseMemoryEntries(result);
            _dispatch(() =>
            {
                MemoryEntries.Clear();
                foreach (var entry in entries)
                {
                    MemoryEntries.Add(entry);
                }

                OnPropertyChanged(nameof(ShowMemoryEntries));
                MemoryStatus = entries.Count == 0 ? "还没有记忆条目。" : $"共 {entries.Count} 条记忆";
            });
        }
        catch (Exception error)
        {
            SetMemoryStatus($"无法读取记忆：{error.Message}");
        }
    }

    /// <summary>Loads one entry into the editor.</summary>
    public async Task LoadMemoryAsync(string name)
    {
        if (!TryMemorySession(out var sessionId))
        {
            SetMemoryStatus("先打开一个聊天会话，然后点刷新。");
            return;
        }

        try
        {
            var result = await _core.RequestAsync(
                "memory.read",
                new { session_id = sessionId, name, scope = _memoryScope },
                CancellationToken.None);
            var content = result.TryGetProperty("content", out var contentValue)
                ? contentValue.GetString() ?? string.Empty
                : string.Empty;
            _dispatch(() =>
            {
                MemoryName = name;
                MemoryContent = content;
                MemoryStatus = $"已载入 {name}";
            });
        }
        catch (Exception error)
        {
            SetMemoryStatus($"无法读取 {name}：{error.Message}");
        }
    }

    public async Task SaveMemoryAsync()
    {
        var name = MemoryName.Trim();
        if (name.Length == 0)
        {
            SetMemoryStatus("先填写条目名称。");
            return;
        }

        if (!TryMemorySession(out var sessionId))
        {
            SetMemoryStatus("先打开一个聊天会话，然后点刷新。");
            return;
        }

        try
        {
            await _core.RequestAsync(
                "memory.write",
                new { session_id = sessionId, name, content = MemoryContent, scope = _memoryScope },
                CancellationToken.None);
            _dispatch(() => MemoryStatus = $"已保存 {name}");
            await RefreshMemoryAsync();
        }
        catch (Exception error)
        {
            SetMemoryStatus($"无法保存：{error.Message}");
        }
    }

    public async Task DeleteMemoryAsync()
    {
        var name = MemoryName.Trim();
        if (name.Length == 0)
        {
            SetMemoryStatus("先选择或填写要删除的条目。");
            return;
        }

        if (!TryMemorySession(out var sessionId))
        {
            SetMemoryStatus("先打开一个聊天会话，然后点刷新。");
            return;
        }

        try
        {
            await _core.RequestAsync(
                "memory.delete",
                new { session_id = sessionId, name, scope = _memoryScope },
                CancellationToken.None);
            _dispatch(() =>
            {
                MemoryName = string.Empty;
                MemoryContent = string.Empty;
                MemoryStatus = $"已删除 {name}";
            });
            await RefreshMemoryAsync();
        }
        catch (Exception error)
        {
            SetMemoryStatus($"无法删除：{error.Message}");
        }
    }

    /// <summary>Deletes every entry in the given scope; used by 个性化 → 删除 KL 记忆.</summary>
    public async Task DeleteAllMemoriesAsync(string scope = "user")
    {
        if (!TryMemorySession(out var sessionId))
        {
            SetMemoryStatus("先打开一个聊天会话，然后重试。");
            return;
        }

        try
        {
            var result = await _core.RequestAsync(
                "memory.list",
                new { session_id = sessionId, scope },
                CancellationToken.None);
            var entries = ParseMemoryEntries(result);
            foreach (var entry in entries)
            {
                await _core.RequestAsync(
                    "memory.delete",
                    new { session_id = sessionId, name = entry.Name, scope },
                    CancellationToken.None);
            }

            SetMemoryStatus($"已删除 {entries.Count} 条记忆");
            await RefreshMemoryAsync();
        }
        catch (Exception error)
        {
            SetMemoryStatus($"无法删除记忆：{error.Message}");
        }
    }

    public void StartNewMemory()
    {
        MemoryName = string.Empty;
        MemoryContent = string.Empty;
        MemoryStatus = "输入名称与内容后保存。";
    }

    private bool TryMemorySession(out string sessionId)
    {
        sessionId = _core.IsSessionOpen ? _core.SessionId : string.Empty;
        return !string.IsNullOrWhiteSpace(sessionId);
    }

    private void SetMemoryStatus(string text) =>
        _dispatch(() => MemoryStatus = text);

    private static List<MemoryEntryRow> ParseMemoryEntries(JsonElement result)
    {
        var rows = new List<MemoryEntryRow>();
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("entries", out var entries)
            || entries.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        foreach (var entry in entries.EnumerateArray())
        {
            var name = entry.TryGetProperty("name", out var nameValue)
                ? nameValue.GetString() ?? string.Empty
                : string.Empty;
            if (name.Length == 0)
            {
                continue;
            }

            var size = entry.TryGetProperty("size_bytes", out var sizeValue)
                       && sizeValue.ValueKind == JsonValueKind.Number
                       && sizeValue.TryGetInt64(out var parsed)
                ? $"{parsed:N0} B"
                : "—";
            rows.Add(new MemoryEntryRow(name, size));
        }

        return rows;
    }

    public void SetAutomationFilter(AutomationFilter filter)
    {
        _automationFilter = filter;
        OnPropertyChanged(nameof(IsAutomationFilterAll));
        OnPropertyChanged(nameof(IsAutomationFilterActive));
        OnPropertyChanged(nameof(IsAutomationFilterEnded));
        ApplyAutomationFilter();
    }

    public void SetInteractionMode(bool workMode)
    {
        var value = workMode ? "work" : "chat";
        if (_state.InteractionMode == value)
        {
            return;
        }
        _state.InteractionMode = value;
        // The reference product lists projects flat; this app expands their chats.
        foreach (var workspace in Workspaces)
        {
            workspace.IsExpanded = workMode;
        }
        OnPropertyChanged(nameof(IsWorkMode));
        OnPropertyChanged(nameof(IsChatMode));
        OnPropertyChanged(nameof(ActiveModeLabel));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(ComposerPlaceholder));
        OnPropertyChanged(nameof(ComposerMaxWidth));
        OnPropertyChanged(nameof(ComposerMargin));
        OnPropertyChanged(nameof(ShowWorkspaceStrip));
        OnPropertyChanged(nameof(ShowChatComposerChips));
        SaveState();
    }
}
