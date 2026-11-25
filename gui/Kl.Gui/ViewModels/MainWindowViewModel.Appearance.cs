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
    private static readonly string[] AccentColors =
    [
        "#4D9FFF", "#3566F0", "#179E8A", "#FFCC38", "#FA70AB", "#FF8066", "#A25BFF", "#FFFFFF"
    ];

    private bool _isLightTheme;

    public string LanguageSetting => _state.Language;

    /// <summary>0 = follow the system, 1 = Simplified Chinese, 2 = English.</summary>
    public int LanguageIndex
    {
        get => _state.Language switch
        {
            Services.Localization.Chinese => 1,
            Services.Localization.English => 2,
            _ => 0
        };
        set
        {
            var language = value switch
            {
                1 => Services.Localization.Chinese,
                2 => Services.Localization.English,
                _ => string.Empty
            };
            if (_state.Language == language)
            {
                return;
            }
            _state.Language = language;
            SaveState();
            OnPropertyChanged();
            Services.Localization.Apply(language);
        }
    }

    /// <summary>Re-evaluates cached labels after the language changes.</summary>
    public override void RefreshLocalization()
    {
        OnPropertyChanged(string.Empty);
        BuildShortcutRows();
        foreach (var message in Messages)
        {
            message.RefreshLocalization();
        }
        foreach (var workspace in Workspaces)
        {
            workspace.RefreshLocalization();
            foreach (var session in workspace.Sessions)
            {
                session.RefreshLocalization();
            }
        }
        foreach (var session in ArchivedSessions)
        {
            session.RefreshLocalization();
        }
        foreach (var automation in Automations)
        {
            automation.RefreshLocalization();
        }
        foreach (var plugin in Plugins)
        {
            plugin.RefreshLocalization();
        }
        foreach (var candidate in ImportCandidates)
        {
            candidate.RefreshLocalization();
        }
        foreach (var card in ProviderCards)
        {
            card.RefreshLocalization();
        }
        foreach (var model in ProviderModels)
        {
            model.RefreshLocalization();
        }
        Rebind(PendingAttachments);
        Rebind(Notifications);
        Rebind(LibraryItems);
        Rebind(HomeSuggestions);
        Rebind(AuditRecords);
        Rebind(McpTools);
        Rebind(Skills);
        Rebind(ReviewFindings);
        Rebind(Worktrees);
        Rebind(UsageBars);
        Rebind(UsageRows);
        for (var index = 0; index < ConfigRows.Count; index++)
        {
            ConfigRows[index] = ConfigRows[index] with { Label = Loc.T(ConfigRows[index].Label) };
        }
        Rebind(MemoryEntries);
        Rebind(StorageFolders);
        RefreshPinnedRailItems();
        Rebind(DoctorChecks);
        Rebind(CleanupSessions);
        Rebind(PaletteSessions);
    }

    private static void Rebind<T>(ObservableCollection<T> items)
    {
        var snapshot = items.ToList();
        items.Clear();
        foreach (var item in snapshot)
        {
            items.Add(item);
        }
    }

    public string ThemeMode => _state.ThemeMode;

    public bool IsThemeLight => _state.ThemeMode == "light";
    public bool IsThemeDark => _state.ThemeMode == "dark";

    public void SetThemeMode(string mode)
    {
        var value = mode is "light" or "dark" ? mode : "system";
        if (_state.ThemeMode == value)
        {
            return;
        }

        _state.ThemeMode = value;
        SaveState();
        OnPropertyChanged(nameof(ThemeMode));
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsThemeDark));
    }

    public bool SeparateThemeSettings
    {
        get => _state.SeparateThemeSettings;
        set
        {
            if (_state.SeparateThemeSettings == value)
            {
                return;
            }

            if (value)
            {
                _state.LightAccentColor = _state.AccentColor;
                _state.DarkAccentColor = _state.AccentColor;
                _state.LightContrast = _state.Contrast;
                _state.DarkContrast = _state.Contrast;
            }

            _state.SeparateThemeSettings = value;
            SaveState();
            OnPropertyChanged();
            NotifyThemeAppearanceChanged();
        }
    }

    public void SetEffectiveTheme(bool isLight)
    {
        if (_isLightTheme == isLight)
        {
            return;
        }

        _isLightTheme = isLight;
        NotifyThemeAppearanceChanged();
    }

    public void NotifyThemeAppearanceChanged()
    {
        OnPropertyChanged(nameof(AccentColorHex));
        OnPropertyChanged(nameof(AccentColorBrush));
        OnPropertyChanged(nameof(ReasoningEffortBrush));
        OnPropertyChanged(nameof(AccentColorIndex));
        OnPropertyChanged(nameof(Contrast));
        OnPropertyChanged(nameof(ThemeBackgroundHex));
        OnPropertyChanged(nameof(ThemeForegroundHex));
    }

    public string ThemeBackgroundHex => _isLightTheme ? "#F7F7F8" : "#0F0F11";

    public string ThemeForegroundHex => _isLightTheme ? "#1B1C1E" : "#E3E4E6";

    private string CurrentAccentColor => _state.SeparateThemeSettings
        ? (_isLightTheme ? _state.LightAccentColor : _state.DarkAccentColor)
        : _state.AccentColor;

    private void SetCurrentAccentColor(string color)
    {
        if (_state.SeparateThemeSettings)
        {
            if (_isLightTheme)
            {
                _state.LightAccentColor = color;
            }
            else
            {
                _state.DarkAccentColor = color;
            }
        }
        else
        {
            _state.AccentColor = color;
        }
    }

    public string AccentColorHex => CurrentAccentColor;

    public Avalonia.Media.IBrush AccentColorBrush =>
        new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(CurrentAccentColor));

    public int AccentColorIndex
    {
        get
        {
            var index = Array.FindIndex(AccentColors,
                color => string.Equals(color, CurrentAccentColor, StringComparison.OrdinalIgnoreCase));
            return index < 0 ? 0 : index;
        }
        set
        {
            if (value < 0 || value >= AccentColors.Length)
            {
                return;
            }

            var color = AccentColors[value];
            if (string.Equals(CurrentAccentColor, color, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            SetCurrentAccentColor(color);
            SaveState();
            OnPropertyChanged();
            OnPropertyChanged(nameof(AccentColorHex));
            OnPropertyChanged(nameof(AccentColorBrush));
            OnPropertyChanged(nameof(ReasoningEffortBrush));
        }
    }

    public string DiffMarkerMode => _state.DiffMarkerMode;

    public bool IsDiffColorMode => _state.DiffMarkerMode != "markers";
    public bool IsDiffMarkers => _state.DiffMarkerMode == "markers";

    public void SetDiffMarkerMode(string mode)
    {
        var value = mode == "markers" ? "markers" : "color";
        if (_state.DiffMarkerMode == value)
        {
            return;
        }

        _state.DiffMarkerMode = value;
        SaveState();
        OnPropertyChanged(nameof(DiffMarkerMode));
        OnPropertyChanged(nameof(IsDiffColorMode));
        OnPropertyChanged(nameof(IsDiffMarkers));
    }

    private static readonly string[] TerminalShells = ["powershell", "cmd", "git-bash"];

    public int TerminalShellIndex
    {
        get => Math.Max(0, Array.IndexOf(TerminalShells, _state.TerminalShell));
        set
        {
            if (value < 0 || value >= TerminalShells.Length)
            {
                return;
            }

            var shell = TerminalShells[value];
            if (_state.TerminalShell == shell)
            {
                return;
            }

            _state.TerminalShell = shell;
            SaveState();
            OnPropertyChanged();
            OnPropertyChanged(nameof(TerminalShell));
        }
    }

    public string TerminalShell => _state.TerminalShell;

    public bool IsTerminalBottom => _state.TerminalPosition != "right";
    public bool IsTerminalRight => _state.TerminalPosition == "right";

    public void SetTerminalPosition(string position)
    {
        var value = position == "right" ? "right" : "bottom";
        if (_state.TerminalPosition == value)
        {
            return;
        }

        _state.TerminalPosition = value;
        SaveState();
        OnPropertyChanged(nameof(IsTerminalBottom));
        OnPropertyChanged(nameof(IsTerminalRight));
    }

    public bool ShowTerminalPanelControl
    {
        get => _state.ShowTerminalPanelControl;
        set
        {
            if (_state.ShowTerminalPanelControl == value)
            {
                return;
            }

            _state.ShowTerminalPanelControl = value;
            SaveState();
            OnPropertyChanged();
        }
    }

    public bool IsTerminalPanelOpen
    {
        get => _state.TerminalPanelOpen;
        set
        {
            if (_state.TerminalPanelOpen == value)
            {
                return;
            }

            _state.TerminalPanelOpen = value;
            SaveState();
            OnPropertyChanged();
        }
    }

    public void ToggleTerminalPanel() => IsTerminalPanelOpen = !IsTerminalPanelOpen;

    /// <summary>Panel height (bottom) or width (right); 0 in state means "use the default".</summary>
    public double TerminalPanelSize => _state.TerminalPanelSize > 0
        ? _state.TerminalPanelSize
        : IsTerminalRight ? 420 : 260;

    public void SetTerminalPanelSize(double size)
    {
        var clamped = Math.Clamp(size, 180, 600);
        if (Math.Abs(_state.TerminalPanelSize - clamped) < 0.5)
        {
            return;
        }

        _state.TerminalPanelSize = clamped;
        SaveState();
        OnPropertyChanged(nameof(TerminalPanelSize));
    }

    public bool IsReduceMotionSystem => _state.ReduceMotion == "system";
    public bool IsReduceMotionOn => _state.ReduceMotion == "on";
    public bool IsReduceMotionOff => _state.ReduceMotion == "off";

    public bool ReduceMotion => _state.ReduceMotion == "on"
        || (_state.ReduceMotion == "system" && SystemAccessibility.AnimationsDisabled());

    public void SetReduceMotionMode(string mode)
    {
        var value = mode is "on" or "off" ? mode : "system";
        if (_state.ReduceMotion == value)
        {
            return;
        }

        _state.ReduceMotion = value;
        SaveState();
        OnPropertyChanged(nameof(IsReduceMotionSystem));
        OnPropertyChanged(nameof(IsReduceMotionOn));
        OnPropertyChanged(nameof(IsReduceMotionOff));
        OnPropertyChanged(nameof(ReduceMotion));
    }

    public int CodeFontSize
    {
        get => _state.ContentCodeFontSize;
        set
        {
            var clamped = Math.Clamp(value, 9, 20);
            if (_state.ContentCodeFontSize == clamped)
            {
                return;
            }

            _state.ContentCodeFontSize = clamped;
            SaveState();
            OnPropertyChanged();
        }
    }

    public int UiFontIndex
    {
        get => _state.UiFontIndex;
        set => SetFontIndex(value, 1, nameof(UiFontIndex), () => _state.UiFontIndex, v => _state.UiFontIndex = v);
    }

    public int ContentFontIndex
    {
        get => _state.ContentFontIndex;
        set => SetFontIndex(value, 2, nameof(ContentFontIndex), () => _state.ContentFontIndex, v => _state.ContentFontIndex = v);
    }

    public int CodeFontIndex
    {
        get => _state.CodeFontIndex;
        set => SetFontIndex(value, 2, nameof(CodeFontIndex), () => _state.CodeFontIndex, v => _state.CodeFontIndex = v);
    }

    public int UiFontWeightIndex
    {
        get => _state.UiFontWeightIndex;
        set => SetFontIndex(value, 2, nameof(UiFontWeightIndex), () => _state.UiFontWeightIndex, v => _state.UiFontWeightIndex = v);
    }

    public int ContentFontWeightIndex
    {
        get => _state.ContentFontWeightIndex;
        set => SetFontIndex(value, 2, nameof(ContentFontWeightIndex), () => _state.ContentFontWeightIndex, v => _state.ContentFontWeightIndex = v);
    }

    public int CodeFontWeightIndex
    {
        get => _state.CodeFontWeightIndex;
        set => SetFontIndex(value, 2, nameof(CodeFontWeightIndex), () => _state.CodeFontWeightIndex, v => _state.CodeFontWeightIndex = v);
    }

    private int CurrentContrast => _state.SeparateThemeSettings
        ? (_isLightTheme ? _state.LightContrast : _state.DarkContrast)
        : _state.Contrast;

    public int Contrast
    {
        get => CurrentContrast;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            if (CurrentContrast == clamped)
            {
                return;
            }

            if (_state.SeparateThemeSettings)
            {
                if (_isLightTheme)
                {
                    _state.LightContrast = clamped;
                }
                else
                {
                    _state.DarkContrast = clamped;
                }
            }
            else
            {
                _state.Contrast = clamped;
            }

            OnPropertyChanged();
        }
    }

    public bool TranslucentSidebar
    {
        get => _state.TranslucentSidebar;
        set
        {
            if (_state.TranslucentSidebar == value)
            {
                return;
            }

            _state.TranslucentSidebar = value;
            SaveState();
            OnPropertyChanged();
        }
    }

    public bool CompressHistory
    {
        get => _state.CompressHistory;
        set
        {
            if (_state.CompressHistory == value)
            {
                return;
            }

            _state.CompressHistory = value;
            SaveState();
            OnPropertyChanged();
        }
    }

    private void SetFontIndex(int value, int max, string propertyName, Func<int> current, Action<int> update)
    {
        var clamped = Math.Clamp(value, 0, max);
        if (current() == clamped)
        {
            return;
        }

        update(clamped);
        SaveState();
        OnPropertyChanged(propertyName);
    }

    public void SaveAppearanceSettings() => SaveState();

    public bool UsePointerCursor
    {
        get => _state.UsePointerCursor;
        set
        {
            if (_state.UsePointerCursor == value)
            {
                return;
            }

            _state.UsePointerCursor = value;
            SaveState();
            OnPropertyChanged();
        }
    }

    public bool ShowContextUsage
    {
        get => _state.ShowContextUsage;
        set
        {
            if (_state.ShowContextUsage == value)
            {
                return;
            }

            _state.ShowContextUsage = value;
            SaveState();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasUsageText));
        }
    }

    public bool QuestionNotifications
    {
        get => _state.QuestionNotifications;
        set
        {
            if (_state.QuestionNotifications == value)
            {
                return;
            }

            _state.QuestionNotifications = value;
            SaveState();
            OnPropertyChanged();
        }
    }

    public bool ApprovalNotifications
    {
        get => _state.ApprovalNotifications;
        set
        {
            if (_state.ApprovalNotifications == value)
            {
                return;
            }

            _state.ApprovalNotifications = value;
            SaveState();
            OnPropertyChanged();
        }
    }

    public bool NotificationSound
    {
        get => _state.NotificationSound;
        set
        {
            if (_state.NotificationSound == value)
            {
                return;
            }

            _state.NotificationSound = value;
            SaveState();
            OnPropertyChanged();
            OnPropertyChanged(nameof(NotificationSoundIndex));
        }
    }

    public bool AutomationNotifications
    {
        get => _state.AutomationNotifications;
        set
        {
            if (_state.AutomationNotifications == value)
            {
                return;
            }

            _state.AutomationNotifications = value;
            SaveState();
            OnPropertyChanged();
        }
    }

    /// <summary>0 = 默认 sound, 1 = 关闭; matches the settings ComboBox order.</summary>
    public int NotificationSoundIndex
    {
        get => NotificationSound ? 0 : 1;
        set => NotificationSound = value == 0;
    }
}
