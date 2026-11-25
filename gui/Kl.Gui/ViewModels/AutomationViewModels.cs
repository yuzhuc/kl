using Kl.Gui.Models;
using Loc = Kl.Gui.Services.Localization;

namespace Kl.Gui.ViewModels;

public sealed record AutomationIntervalOption(int Minutes, string Label);

public sealed class AutomationItemViewModel : ObservableObject
{
    private bool _isRunning;
    private bool _isVisible = true;

    public AutomationItemViewModel(AutomationState state)
    {
        State = state;
    }

    public AutomationState State { get; }
    public string Id => State.Id;
    public string Name => State.Name;
    public string Prompt => State.Prompt;
    public bool IsEnabled => State.IsEnabled;
    public int IntervalMinutes => State.IntervalMinutes;
    public string LastResult => State.LastResult;
    public bool HasLastResult => !string.IsNullOrWhiteSpace(LastResult);
    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public bool Matches(string query)
    {
        if (query.Length == 0)
        {
            return true;
        }
        return Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || Prompt.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || LastResult.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(StateLabel));
                OnPropertyChanged(nameof(ToggleLabel));
                OnPropertyChanged(nameof(RunButtonLabel));
                OnPropertyChanged(nameof(CanRun));
                OnPropertyChanged(nameof(CanEdit));
            }
        }
    }

    public bool CanRun => !IsRunning;
    public bool CanEdit => !IsRunning;
    public string StateLabel => IsRunning ? Loc.T("运行中") : IsEnabled ? Loc.T("已启用") : Loc.T("已暂停");
    public string ToggleLabel => IsEnabled ? Loc.T("暂停") : Loc.T("启用");
    public string RunButtonLabel => IsRunning ? Loc.T("运行中…") : Loc.T("立即运行");
    public string IntervalLabel => FormatInterval(IntervalMinutes);
    public string NextRunLabel => State.IsEnabled
        ? State.NextRunAt is { } next
            ? Loc.F("下次运行：{0:MM-dd HH:mm}", (next.ToLocalTime()))
            : Loc.T("下次运行：等待调度")
        : Loc.T("下次运行：未启用");
    public string LastRunLabel => State.LastRunAt is { } last
        ? Loc.F("上次运行：{0:MM-dd HH:mm}", (last.ToLocalTime()))
        : Loc.T("尚未运行");
    public string StatusLabel => State.LastStatus switch
    {
        "running" => Loc.T("运行中…"),
        "completed" => Loc.T("完成"),
        "failed" => Loc.T("失败"),
        "" => Loc.T("等待首次运行"),
        _ => State.LastStatus
    };

    public void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Prompt));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(IntervalMinutes));
        OnPropertyChanged(nameof(LastResult));
        OnPropertyChanged(nameof(HasLastResult));
        OnPropertyChanged(nameof(StateLabel));
        OnPropertyChanged(nameof(ToggleLabel));
        OnPropertyChanged(nameof(IntervalLabel));
        OnPropertyChanged(nameof(NextRunLabel));
        OnPropertyChanged(nameof(LastRunLabel));
        OnPropertyChanged(nameof(StatusLabel));
    }

    private static string FormatInterval(int minutes) => minutes switch
    {
        < 60 => Loc.F("每 {0} 分钟", (minutes)),
        < 1440 when minutes % 60 == 0 => Loc.F("每 {0} 小时", (minutes / 60)),
        1440 => Loc.T("每天"),
        _ => Loc.F("每 {0} 分钟", (minutes))
    };
}
