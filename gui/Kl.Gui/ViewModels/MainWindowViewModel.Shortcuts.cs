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
    public int SendShortcutIndex
    {
        get => _state.SendShortcut == "ctrlEnter" ? 1 : 0;
        set
        {
            var mode = value == 1 ? "ctrlEnter" : "enter";
            if (_state.SendShortcut == mode)
            {
                return;
            }

            _state.SendShortcut = mode;
            SaveState();
            OnPropertyChanged();
            OnPropertyChanged(nameof(SendsOnEnter));
        }
    }

    public bool SendsOnEnter => _state.SendShortcut != "ctrlEnter";

    public bool NumberShortcuts => _state.NumberShortcuts;

    public int NumberShortcutsIndex
    {
        get => _state.NumberShortcuts ? 0 : 1;
        set
        {
            var enabled = value == 0;
            if (_state.NumberShortcuts == enabled)
            {
                return;
            }

            _state.NumberShortcuts = enabled;
            SaveState();
            OnPropertyChanged();
            OnPropertyChanged(nameof(NumberShortcuts));
        }
    }

    /// <summary>Opens the nth visible session in the active workspace.</summary>
    public async Task OpenSessionShortcutAsync(int slot)
    {
        var session = _activeWorkspace.Sessions
            .Where(candidate => candidate.IsVisible)
            .Skip(slot)
            .FirstOrDefault();
        if (session is null)
        {
            return;
        }

        CloseSettings();
        Navigate(AppSection.Chat);
        await OpenSessionAsync(session).ConfigureAwait(true);
    }

    public ObservableCollection<ShortcutRowViewModel> ShortcutRows { get; } = [];

    private string _shortcutSearch = string.Empty;
    public string ShortcutSearch
    {
        get => _shortcutSearch;
        set
        {
            if (SetProperty(ref _shortcutSearch, value))
            {
                ApplyShortcutFilter();
            }
        }
    }

    public bool ShowNoShortcutMatches => ShortcutRows.Count > 0 && ShortcutRows.All(row => !row.IsVisible);

    private ShortcutRowViewModel? _capturingShortcut;
    public bool IsShortcutCapturing => _capturingShortcut is not null;
    public string ShortcutCaptureTitle =>
        _capturingShortcut is null ? string.Empty : Loc.F("为“{0}”录制快捷键", (_capturingShortcut.Title));

    private string _shortcutCaptureStatus = string.Empty;
    public string ShortcutCaptureStatus
    {
        get => _shortcutCaptureStatus;
        private set
        {
            if (SetProperty(ref _shortcutCaptureStatus, value))
            {
                OnPropertyChanged(nameof(HasShortcutCaptureStatus));
            }
        }
    }

    public bool HasShortcutCaptureStatus => ShortcutCaptureStatus.Length > 0;

    private void BuildShortcutRows()
    {
        ShortcutRows.Clear();
        var bindings = _state.ShortcutBindings ??= [];
        var definitions = new (string Id, string Title, string Description, string Default)[]
        {
            ("newChat", Loc.T("新聊天"), Loc.T("开始新聊天"), "Ctrl+N"),
            ("archiveChat", Loc.T("归档聊天"), Loc.T("归档当前聊天"), "Ctrl+Shift+A"),
            ("copyLastCodeBlock", Loc.T("复制最后一个代码块"), Loc.T("复制当前聊天中助手提供的最后一个代码块"), "Ctrl+Shift+;"),
            ("markUnread", Loc.T("标记为未读"), Loc.T("将当前聊天标记为未读"), "Ctrl+Shift+U"),
            ("togglePin", Loc.T("切换置顶状态"), Loc.T("置顶或取消置顶当前聊天"), "Ctrl+Alt+P"),
            ("searchSessions", Loc.T("搜索会话"), Loc.T("在侧边栏中搜索聊天"), "Ctrl+F"),
            ("openSettings", Loc.T("打开设置"), Loc.T("打开设置页面"), "Ctrl+,"),
            ("toggleTerminal", Loc.T("切换终端"), Loc.T("显示或隐藏集成终端面板"), "Ctrl+`"),
            ("closeWindow", Loc.T("关闭窗口"), Loc.T("关闭当前窗口"), "Ctrl+Shift+W")
        };

        foreach (var definition in definitions)
        {
            var gesture = bindings.TryGetValue(definition.Id, out var custom)
                && !string.IsNullOrWhiteSpace(custom)
                    ? custom
                    : definition.Default;
            ShortcutRows.Add(new ShortcutRowViewModel(
                definition.Id,
                definition.Title,
                definition.Description,
                definition.Default,
                gesture));
        }

        ApplyShortcutFilter();
    }

    private void ApplyShortcutFilter()
    {
        var query = ShortcutSearch.Trim();
        foreach (var row in ShortcutRows)
        {
            row.IsVisible = query.Length == 0
                || row.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || row.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || row.Gesture.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        }

        OnPropertyChanged(nameof(ShowNoShortcutMatches));
    }

    /// <summary>Returns the custom binding matching one formatted gesture.</summary>
    public ShortcutRowViewModel? FindShortcut(string gesture) =>
        ShortcutRows.FirstOrDefault(row =>
            row.Gesture.Length > 0
            && string.Equals(row.Gesture, gesture, StringComparison.OrdinalIgnoreCase));

    public void BeginShortcutCapture(ShortcutRowViewModel row)
    {
        _capturingShortcut = row;
        ShortcutCaptureStatus = string.Empty;
        OnPropertyChanged(nameof(IsShortcutCapturing));
        OnPropertyChanged(nameof(ShortcutCaptureTitle));
    }

    public void CancelShortcutCapture()
    {
        _capturingShortcut = null;
        ShortcutCaptureStatus = string.Empty;
        OnPropertyChanged(nameof(IsShortcutCapturing));
        OnPropertyChanged(nameof(ShortcutCaptureTitle));
    }

    public bool ApplyShortcutGesture(string gesture)
    {
        if (_capturingShortcut is null)
        {
            return false;
        }

        var duplicate = ShortcutRows.FirstOrDefault(row =>
            !ReferenceEquals(row, _capturingShortcut)
            && string.Equals(row.Gesture, gesture, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
        {
            ShortcutCaptureStatus = Loc.F("{0} 已用于“{1}”。", (gesture), (duplicate.Title));
            return false;
        }

        _state.ShortcutBindings[_capturingShortcut.ActionId] = gesture;
        _capturingShortcut.Gesture = gesture;
        SaveState(silent: true);
        CancelShortcutCapture();
        return true;
    }

    public void ResetShortcut(ShortcutRowViewModel row)
    {
        _state.ShortcutBindings.Remove(row.ActionId);
        row.Gesture = row.DefaultGesture;
        SaveState(silent: true);
    }

    public string CloseWindowAction => _state.CloseWindowAction;

    public int CloseWindowActionIndex
    {
        get => _state.CloseWindowAction switch
        {
            "always" => 1,
            "never" => 2,
            _ => 0
        };
        set
        {
            var mode = value switch
            {
                1 => "always",
                2 => "never",
                _ => "shortcut"
            };
            if (_state.CloseWindowAction == mode)
            {
                return;
            }

            _state.CloseWindowAction = mode;
            SaveState();
            OnPropertyChanged();
            OnPropertyChanged(nameof(CloseWindowAction));
        }
    }

    public int FileOpenTargetIndex
    {
        get => string.Equals(_state.FileOpenTarget, "vscode", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        set
        {
            var target = value == 1 ? "vscode" : "system";
            if (string.Equals(_state.FileOpenTarget, target, StringComparison.Ordinal))
            {
                return;
            }

            _state.FileOpenTarget = target;
            SaveState();
            OnPropertyChanged();
        }
    }

    public int AutoArchiveIndex
    {
        get => _state.AutoArchiveDays switch        {
            > 0 and <= 7 => 1,
            > 7 => 2,
            _ => 0
        };
        set
        {
            var days = value switch
            {
                1 => 7,
                2 => 30,
                _ => 0
            };
            if (_state.AutoArchiveDays == days)
            {
                return;
            }

            _state.AutoArchiveDays = days;
            SaveState();
            OnPropertyChanged();
        }
    }

    private string _launchAtLoginStatus = string.Empty;
    public bool LaunchAtLogin
    {
        get => _state.LaunchAtLogin;
        set
        {
            if (_state.LaunchAtLogin == value)
            {
                return;
            }

            try
            {
                StartupRegistration.SetEnabled(value);
                _state.LaunchAtLogin = value;
                LaunchAtLoginStatus = value ? Loc.T("已加入开机启动。") : Loc.T("已取消开机启动。");
                SaveState();
            }
            catch (Exception error) when (error is InvalidOperationException
                or PlatformNotSupportedException
                or System.ComponentModel.Win32Exception)
            {
                LaunchAtLoginStatus = Loc.F("无法更新开机启动：{0}", (error.Message));
            }

            OnPropertyChanged();
        }
    }

    public string LaunchAtLoginStatus
    {
        get => _launchAtLoginStatus;
        private set
        {
            if (SetProperty(ref _launchAtLoginStatus, value))
            {
                OnPropertyChanged(nameof(HasLaunchAtLoginStatus));
            }
        }
    }

    public bool HasLaunchAtLoginStatus => _launchAtLoginStatus.Length > 0;

    private string _hooksStatusText = Loc.T("打开此页时读取 hooks.toml。");
    public string HooksStatusText
    {
        get => _hooksStatusText;
        private set => SetProperty(ref _hooksStatusText, value);
    }

    /// <summary>Reports the local hooks config that Core loads at startup.</summary>
    public void RefreshHooks()
    {
        try
        {
            if (!File.Exists(HooksFilePath))
            {
                HooksStatusText = Loc.F("未找到 hooks.toml（{0}）", (HooksFilePath));
                return;
            }

            var lines = File.ReadLines(HooksFilePath).Count(line => !string.IsNullOrWhiteSpace(line));
            HooksStatusText = Loc.F("已配置 {0} 行钩子；修改后重启 KL Core 生效。", (lines));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            HooksStatusText = Loc.F("无法读取 hooks.toml：{0}", (error.Message));
        }
    }
}
