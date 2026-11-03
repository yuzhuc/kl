using Kl.Gui.Models;

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
        OnPropertyChanged(nameof(CanRun));
        OnPropertyChanged(nameof(CanEdit));
                OnPropertyChanged(nameof(ToggleLabel));
                OnPropertyChanged(nameof(RunButtonLabel));
                OnPropertyChanged(nameof(CanRun));
                OnPropertyChanged(nameof(CanEdit));
            }
        }
    }

    public bool CanRun => !IsRunning;
    public bool CanEdit => !IsRunning;
    public string StateLabel => IsRunning ? "运行中" : IsEnabled ? "已启用" : "已暂停";
    public string ToggleLabel => IsEnabled ? "暂停" : "启用";
    public string RunButtonLabel => IsRunning ? "运行中…" : "立即运行";
    public string IntervalLabel => FormatInterval(IntervalMinutes);
    public string NextRunLabel => State.IsEnabled
        ? State.NextRunAt is { } next
            ? $"下次运行：{next.ToLocalTime():MM-dd HH:mm}"
            : "下次运行：等待调度"
        : "下次运行：未启用";
    public string LastRunLabel => State.LastRunAt is { } last
        ? $"上次运行：{last.ToLocalTime():MM-dd HH:mm}"
        : "尚未运行";
    public string StatusLabel => string.IsNullOrWhiteSpace(State.LastStatus)
        ? "等待首次运行"
        : State.LastStatus;

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
        < 60 => $"每 {minutes} 分钟",
        < 1440 when minutes % 60 == 0 => $"每 {minutes / 60} 小时",
        1440 => "每天",
        _ => $"每 {minutes} 分钟"
    };
}
