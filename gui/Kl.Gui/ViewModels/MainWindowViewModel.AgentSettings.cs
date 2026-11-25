using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Kl.Client;
using Kl.Gui.Models;
using Kl.Gui.Services;
using Loc = Kl.Gui.Services.Localization;

namespace Kl.Gui.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    public string ReasoningEffort
    {
        get => _reasoningEffort;
        private set
        {
            if (SetProperty(ref _reasoningEffort, value))
            {
                OnPropertyChanged(nameof(ReasoningEffortLabel));
                OnPropertyChanged(nameof(HasReasoningEffort));
                RaiseReasoningDisplay();
                OnPropertyChanged(nameof(IsReasoningDefault));
                OnPropertyChanged(nameof(IsReasoningLow));
                OnPropertyChanged(nameof(IsReasoningMedium));
                OnPropertyChanged(nameof(IsReasoningHigh));
            }
        }
    }

    public string ReasoningEffortLabel =>
        ReasoningEffortOptions.FirstOrDefault(option => option.Id == _reasoningEffort)?.Label ?? "default";

    /// <summary>Live value while the slider thumb is being dragged.</summary>
    public string ReasoningEffortDisplayLabel =>
        ComposerReasoningEffortOptions.FirstOrDefault(
                option => option.Id == (_reasoningEffortPreview ?? _composerReasoningEffort))
            ?.Label ?? "default";

    /// <summary>Effort text colour: accent blue at the lowest tier, purple at the top.</summary>
    public IBrush ReasoningEffortBrush
    {
        get
        {
            var id = _reasoningEffortPreview ?? _composerReasoningEffort;
            var index = 0;
            for (var i = 0; i < ComposerReasoningEffortOptions.Count; i++)
            {
                if (ComposerReasoningEffortOptions[i].Id == id)
                {
                    index = i;
                    break;
                }
            }
            var count = ComposerReasoningEffortOptions.Count;
            var fraction = count > 1 ? index / (count - 1.0) : 0;
            return new SolidColorBrush(LerpColor(
                Color.Parse(AccentColorHex),
                ReasoningPurple,
                Math.Pow(Math.Clamp(fraction, 0, 1), 0.8)));
        }
    }

    /// <summary>Slider fill / effort label purple for the current theme.</summary>
    public Color ReasoningPurple =>
        _isLightTheme ? Color.Parse("#7C3AED") : Color.Parse("#9333EA");

    private static Color LerpColor(Color from, Color to, double t) => Color.FromArgb(
        Lerp(from.A, to.A, t),
        Lerp(from.R, to.R, t),
        Lerp(from.G, to.G, t),
        Lerp(from.B, to.B, t));

    private static byte Lerp(byte from, byte to, double t) =>
        (byte)Math.Round(from + (to - from) * t);

    /// <summary>True while the composer effort flyout is open; the chip shows its action label.</summary>
    public bool IsComposerEffortFlyoutOpen
    {
        get => _isComposerEffortFlyoutOpen;
        set => SetProperty(ref _isComposerEffortFlyoutOpen, value);
    }

    /// <summary>Settings ComboBox index: default/low/medium/high, mapping 0-3.</summary>
    public int ReasoningEffortIndex
    {
        get
        {
            var index = 0;
            for (var i = 0; i < ReasoningEffortOptions.Count; i++)
            {
                if (ReasoningEffortOptions[i].Id == _reasoningEffort)
                {
                    index = i;
                    break;
                }
            }
            return index;
        }
        set
        {
            if (value < 0 || value >= ReasoningEffortOptions.Count)
            {
                return;
            }
            var id = ReasoningEffortOptions[value].Id;
            if (id == _reasoningEffort)
            {
                return;
            }
            _ = SetReasoningEffortAsync(id);
        }
    }

    public void PreviewReasoningEffort(string effort)
    {
        if (string.Equals(_reasoningEffortPreview, effort, StringComparison.Ordinal))
        {
            return;
        }

        _reasoningEffortPreview = effort;
        RaiseReasoningDisplay();
    }

    public void ClearReasoningEffortPreview()
    {
        if (_reasoningEffortPreview is null)
        {
            return;
        }

        _reasoningEffortPreview = null;
        RaiseReasoningDisplay();
    }

    private void RaiseReasoningDisplay()
    {
        OnPropertyChanged(nameof(ReasoningEffortDisplayLabel));
        OnPropertyChanged(nameof(ReasoningEffortBrush));
    }

    public bool HasReasoningEffort =>
        !string.Equals(_composerReasoningEffort, "default", StringComparison.Ordinal);

    public bool WebSearch
    {
        get => _webSearch;
        set
        {
            if (_webSearch == value)
            {
                return;
            }
            _webSearch = value;
            OnPropertyChanged();
            _ = SaveAgentSettingAsync(new { web_search = value });
        }
    }

    public bool NetworkAccess
    {
        get => _networkAccess;
        set
        {
            if (_networkAccess == value)
            {
                return;
            }
            _networkAccess = value;
            OnPropertyChanged();
            _ = SaveAgentSettingAsync(new { network_access = value });
        }
    }

    public bool MemoryEnabled
    {
        get => _memoryEnabled;
        set
        {
            if (_memoryEnabled == value)
            {
                return;
            }
            _memoryEnabled = value;
            OnPropertyChanged();
            _ = SaveAgentSettingAsync(new { memory_enabled = value });
        }
    }

    public bool PluginsEnabled
    {
        get => _pluginsEnabled;
        set
        {
            if (_pluginsEnabled == value)
            {
                return;
            }
            _pluginsEnabled = value;
            OnPropertyChanged();
            _ = SaveAgentSettingAsync(new { plugins_enabled = value });
        }
    }

    public int OutputVerbosityIndex
    {
        get => _outputVerbosity switch { "concise" => 1, "detailed" => 2, _ => 0 };
        set
        {
            var id = value switch { 1 => "concise", 2 => "detailed", _ => "default" };
            if (_outputVerbosity == id)
            {
                return;
            }
            _outputVerbosity = id;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OutputVerbosityIndex));
            _ = SaveAgentSettingAsync(new { output_verbosity = id });
        }
    }

    public int ReasoningSummaryIndex
    {
        get => _reasoningSummary switch { "off" => 1, "detailed" => 2, _ => 0 };
        set
        {
            var id = value switch { 1 => "off", 2 => "detailed", _ => "auto" };
            if (_reasoningSummary == id)
            {
                return;
            }
            _reasoningSummary = id;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ReasoningSummaryIndex));
            _ = SaveAgentSettingAsync(new { reasoning_summary = id });
        }
    }

    public int PersonalStyleIndex
    {
        get => _personalStyle switch { "friendly" => 1, "professional" => 2, _ => 0 };
        set
        {
            var id = value switch { 1 => "friendly", 2 => "professional", _ => "default" };
            if (_personalStyle == id)
            {
                return;
            }
            _personalStyle = id;
            OnPropertyChanged();
            _ = SaveAgentSettingAsync(new { personal_style = id });
        }
    }

    public int WarmthIndex
    {
        get => _warmth switch { "low" => 1, "high" => 2, _ => 0 };
        set
        {
            var id = value switch { 1 => "low", 2 => "high", _ => "default" };
            if (_warmth == id)
            {
                return;
            }
            _warmth = id;
            OnPropertyChanged();
            _ = SaveAgentSettingAsync(new { warmth = id });
        }
    }

    public int EnthusiasmIndex
    {
        get => _enthusiasm switch { "low" => 1, "high" => 2, _ => 0 };
        set
        {
            var id = value switch { 1 => "low", 2 => "high", _ => "default" };
            if (_enthusiasm == id)
            {
                return;
            }
            _enthusiasm = id;
            OnPropertyChanged();
            _ = SaveAgentSettingAsync(new { enthusiasm = id });
        }
    }

    public int EmojiIndex
    {
        get => _emojiUsage switch { "less" => 1, "more" => 2, _ => 0 };
        set
        {
            var id = value switch { 1 => "less", 2 => "more", _ => "default" };
            if (_emojiUsage == id)
            {
                return;
            }
            _emojiUsage = id;
            OnPropertyChanged();
            _ = SaveAgentSettingAsync(new { emoji_usage = id });
        }
    }

    public int VisualAnswersIndex
    {
        get => _visualAnswers switch { "off" => 1, "on" => 2, _ => 0 };
        set
        {
            var id = value switch { 1 => "off", 2 => "on", _ => "default" };
            if (_visualAnswers == id)
            {
                return;
            }
            _visualAnswers = id;
            OnPropertyChanged();
            _ = SaveAgentSettingAsync(new { visual_answers = id });
        }
    }

    public int HeadingStyleIndex
    {
        get => _headingStyle switch { "concise" => 1, "detailed" => 2, _ => 0 };
        set
        {
            var id = value switch { 1 => "concise", 2 => "detailed", _ => "default" };
            if (_headingStyle == id)
            {
                return;
            }
            _headingStyle = id;
            OnPropertyChanged();
            _ = SaveAgentSettingAsync(new { heading_style = id });
        }
    }

    public bool QuickAnswers
    {
        get => _quickAnswers;
        set
        {
            if (_quickAnswers == value)
            {
                return;
            }
            _quickAnswers = value;
            OnPropertyChanged();
            _ = SaveAgentSettingAsync(new { quick_answers = value });
        }
    }

    public string CustomInstructionsDraft
    {
        get => _customInstructionsDraft;
        set
        {
            if (_customInstructionsDraft == value)
            {
                return;
            }
            _customInstructionsDraft = value;
            OnPropertyChanged();
        }
    }

    public async Task SaveCustomInstructionsAsync()
    {
        await SaveAgentSettingAsync(new { custom_instructions = _customInstructionsDraft })
            .ConfigureAwait(true);
    }

    private async Task SaveAgentSettingAsync(object patch)
    {
        if (_loadingAgentSettings)
        {
            return;
        }
        try
        {
            await _core.SetAgentConfigAsync(patch, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or FileNotFoundException)
        {
            ReportError(Loc.F("保存设置失败：{0}", (error.Message)));
        }
    }

    public bool IsReasoningDefault => string.Equals(_reasoningEffort, "default", StringComparison.Ordinal);
    public bool IsReasoningLow => string.Equals(_reasoningEffort, "low", StringComparison.Ordinal);
    public bool IsReasoningMedium => string.Equals(_reasoningEffort, "medium", StringComparison.Ordinal);
    public bool IsReasoningHigh => string.Equals(_reasoningEffort, "high", StringComparison.Ordinal);

    public bool IsPermissionFull =>
        string.Equals(_state.PermissionMode, "full", StringComparison.Ordinal);

    public bool IsPermissionAuto =>
        string.Equals(_state.PermissionMode, "auto", StringComparison.Ordinal);

    public bool IsPermissionAsk =>
        string.Equals(_state.PermissionMode, "ask", StringComparison.Ordinal);

    private bool _showFullAccessWarning;

    /// <summary>Warning card shown right after switching into 完全访问.</summary>
    public bool ShowFullAccessWarning =>
        _showFullAccessWarning && IsPermissionFull && !_state.FullAccessWarningDismissed;

    public void DismissFullAccessWarning(bool forever)
    {
        _showFullAccessWarning = false;
        if (forever)
        {
            _state.FullAccessWarningDismissed = true;
            SaveState();
        }
        OnPropertyChanged(nameof(ShowFullAccessWarning));
    }
}
