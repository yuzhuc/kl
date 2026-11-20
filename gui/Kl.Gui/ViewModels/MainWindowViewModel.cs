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

public enum AppSection
{
    Chat,
    Library,
    Projects,
    Plugins,
    Automation,
    Settings
}

public enum SettingsPane
{
    General,
    Notifications,
    Import,
    Appearance,
    Archived,
    Storage,
    Config,
    Personalization,
    Shortcuts,
    Providers,
    Usage,
    Plugins,
    Passwords,
    CloudPc,
    Hooks,
    Connections,
    CodeReview,
    Git,
    Environments,
    Worktrees,
    Memory,
    Models,
}

public enum AttentionKind
{
    TurnCompleted,
    Approval,
    Question,
    Automation
}

public enum AutomationFilter
{
    All,
    Active,
    Ended
}

public enum ChatItemKind
{
    User,
    Assistant,
    Reasoning,
    Tool,
    ToolGroup,
    Error
}

public sealed class ChatItemViewModel : ObservableObject
{
    private string _header;
    private string _text;
    private string _detail;
    private string _summary;
    private string _statusText;
    private string _timestampLabel;
    private string _toolLabel;
    private string _groupKind = string.Empty;
    private bool _groupExpanded;
    private bool _isRunning;
    private bool _isJumpHighlighted;
    private bool _isExpanded;
    private long? _seq;

    private ChatItemViewModel(
        ChatItemKind kind,
        string header,
        string text = "",
        string detail = "",
        string summary = "",
        string status = "",
        string timestampLabel = "",
        string toolLabel = "")
    {
        Kind = kind;
        _header = header;
        _text = text;
        _detail = detail;
        _summary = summary;
        _statusText = status;
        _timestampLabel = timestampLabel;
        _toolLabel = toolLabel;
    }

    public ChatItemKind Kind { get; }
    public bool IsToolGroup => Kind == ChatItemKind.ToolGroup;
    public System.Collections.ObjectModel.ObservableCollection<ChatItemViewModel> Children { get; } = new();
    public string GroupKind => _groupKind;

    public bool GroupExpanded
    {
        get => _groupExpanded;
        private set => SetProperty(ref _groupExpanded, value);
    }

    public bool IsUser => Kind == ChatItemKind.User;
    public bool IsAssistant => Kind == ChatItemKind.Assistant;
    public bool IsReasoning => Kind == ChatItemKind.Reasoning;
    public bool IsTool => Kind == ChatItemKind.Tool;
    public bool IsError => Kind == ChatItemKind.Error;
    public bool IsActivity => IsReasoning || IsTool;
    public bool HasText => !string.IsNullOrWhiteSpace(Text);
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);
    public bool ShowAssistantActions => IsAssistant && HasText && !IsRunning;
    public string ApiText => IsUser && !string.IsNullOrWhiteSpace(Detail) ? Detail : Text;

    /// <summary>History event seq; null until the core reports it.</summary>
    public long? Seq
    {
        get => _seq;
        set
        {
            if (SetProperty(ref _seq, value))
            {
                OnPropertyChanged(nameof(HasSeq));
            }
        }
    }

    public bool HasSeq => _seq is not null;

    /// <summary>Short local time label shown under user messages (Codex-style).</summary>
    public string TimestampLabel
    {
        get => _timestampLabel;
        private set
        {
            if (SetProperty(ref _timestampLabel, value))
            {
                OnPropertyChanged(nameof(HasTimestamp));
                OnPropertyChanged(nameof(TurnLabel));
                OnPropertyChanged(nameof(HasTurnLabel));
            }
        }
    }

    public bool HasTimestamp => !string.IsNullOrWhiteSpace(TimestampLabel);

    /// <summary>Centred turn divider above user messages, e.g. "10月19日周一 0:36".</summary>
    public string TurnLabel
    {
        get
        {
            if (!IsUser)
            {
                return string.Empty;
            }
            var culture = CultureInfo.GetCultureInfo("zh-CN");
            return DateTime.TryParseExact(_timestampLabel, "M月d日 H:mm", culture,
                DateTimeStyles.None, out var value)
                ? value.ToString("M月d日ddd H:mm", culture)
                : string.Empty;
        }
    }

    public bool HasTurnLabel => !string.IsNullOrEmpty(TurnLabel);

    public string Header
    {
        get => _header;
        private set
        {
            if (SetProperty(ref _header, value))
            {
                OnPropertyChanged(nameof(ActivityPrefix));
                OnPropertyChanged(nameof(ActivityCommand));
                OnPropertyChanged(nameof(HasActivityCommand));
                OnPropertyChanged(nameof(ActivityChildLabel));
                OnPropertyChanged(nameof(IsImageTool));
                OnPropertyChanged(nameof(IsPlainTool));
            }
        }
    }

    public string Text
    {
        get => _text;
        private set
        {
            if (SetProperty(ref _text, value))
            {
                OnPropertyChanged(nameof(HasText));
                OnPropertyChanged(nameof(CollapsedText));
                OnPropertyChanged(nameof(ShowAssistantActions));
            }
        }
    }

    public string Detail
    {
        get => _detail;
        private set
        {
            if (SetProperty(ref _detail, value))
            {
                OnPropertyChanged(nameof(HasDetail));
            }
        }
    }

    public string Summary
    {
        get => _summary;
        private set
        {
            if (SetProperty(ref _summary, value))
            {
                OnPropertyChanged(nameof(CollapsedText));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(HasStatus));
                OnPropertyChanged(nameof(HasFailure));
            }
        }
    }

    public string ActivityPrefix => SplitActivity(Header).prefix;

    public string ActivityCommand => SplitActivity(Header).command;

    public bool HasActivityCommand => ActivityCommand.Length > 0;

    /// <summary>Codex's expanded tool groups show the bare command, indented under the group.</summary>
    public string ActivityChildLabel => HasActivityCommand ? ActivityCommand : ActivityPrefix;

    public bool IsImageTool => (IsTool || IsToolGroup) && Header.Contains("图像");

    public bool IsPlainTool => IsTool && !IsImageTool;

    private static (string prefix, string command) SplitActivity(string header)
    {
        var value = header ?? string.Empty;
        foreach (var verb in new[] { "已运行 ", "已读取 ", "已编辑 " })
        {
            if (value.StartsWith(verb, StringComparison.Ordinal))
            {
                return (verb.TrimEnd(), value[verb.Length..]);
            }
        }

        return (value, string.Empty);
    }

    public string ToolLabel
    {
        get => _toolLabel;
        private set => SetProperty(ref _toolLabel, value);
    }

    public bool HasFailure => IsActivity
        && (StatusText.Contains("失败") || StatusText.Contains("错误") || StatusText.Contains("停止"));

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(ShowAssistantActions));
            }
        }
    }

    /// <summary>Transient highlight set when jumping to this turn from the ruler preview.</summary>
    public bool IsJumpHighlighted
    {
        get => _isJumpHighlighted;
        private set => SetProperty(ref _isJumpHighlighted, value);
    }

    public void SetJumpHighlighted(bool value) => IsJumpHighlighted = value;

    public bool IsExpanded
    {
        get => _isExpanded;
        private set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ShowExpanded));
                OnPropertyChanged(nameof(ShowCollapsed));
                OnPropertyChanged(nameof(ShowChevronDown));
                OnPropertyChanged(nameof(ShowChevronUp));
            }
        }
    }

    public bool ShowExpanded => IsActivity && IsExpanded;
    public bool ShowCollapsed => IsActivity && !IsExpanded;
    public bool ShowChevronDown => ShowCollapsed;
    public bool ShowChevronUp => ShowExpanded;

    public string CollapsedText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Summary))
            {
                return Summary;
            }

            var value = Text.Trim();
            var newline = value.IndexOf('\n');
            return newline < 0 ? value : value[..newline];
        }
    }

    public static string FormatTimestamp(DateTimeOffset timestamp)
    {
        // Codex labels message times as "10月19日 0:36" (no leading zero hour).
        return timestamp.ToLocalTime().ToString("M月d日 H:mm");
    }

    /// <summary>Short Codex-style label shown above an expanded tool panel (Shell/Read/Search/...).</summary>
    public static string ToolKindLabel(string? name)
    {
        var lower = name?.Trim().ToLowerInvariant() ?? string.Empty;
        if (lower.Length == 0)
        {
            return string.Empty;
        }

        if (lower.Contains("shell") || lower.Contains("exec") || lower.Contains("command")
            || lower.Contains("bash") || lower.Contains("terminal") || lower.Contains("run"))
        {
            return "Shell";
        }

        if (lower.Contains("image") || lower.Contains("screenshot") || lower.Contains("photo"))
        {
            return "Image";
        }

        if (lower.Contains("write") || lower.Contains("edit") || lower.Contains("patch") || lower.Contains("apply"))
        {
            return "Edit";
        }

        if (lower.Contains("read") || lower.Contains("view") || lower.Contains("open"))
        {
            return "Read";
        }

        if (lower.Contains("glob") || lower.Contains("grep") || lower.Contains("search")
            || lower.Contains("list") || lower.Contains("find"))
        {
            return "Search";
        }

        if (lower.Contains("web") || lower.Contains("fetch") || lower.Contains("browse"))
        {
            return "Web";
        }

        return "Tool";
    }

    /// <summary>Pulls the real command/script out of JSON tool arguments so rows do not show {"command":...}.</summary>
    public static string ExtractCommand(string? raw)
    {
        var value = raw?.Trim() ?? string.Empty;
        if (!value.StartsWith("{", StringComparison.Ordinal))
        {
            return value;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in new[] { "command", "cmd", "script", "code", "args", "input" })
                {
                    if (document.RootElement.TryGetProperty(key, out var element))
                    {
                        return element.ValueKind switch
                        {
                            JsonValueKind.String => element.GetString() ?? value,
                            _ => element.GetRawText()
                        };
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Fall through and show the raw text.
        }

        return value;
    }

    private static string Compact(string? detail)
    {
        var value = detail?.Trim() ?? string.Empty;
        var newline = value.IndexOf('\n');
        if (newline >= 0)
        {
            value = value[..newline];
        }

        value = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return value.Length <= 110 ? value : value[..110] + "…";
    }

    /// <summary>Codex-style activity wording for tool calls; shell rows include the command preview.</summary>
    public static string ToolHeader(string? name, string? detail = null)
    {
        var value = name?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return "工具调用";
        }

        var lower = value.ToLowerInvariant();
        if (lower.Contains("image") || lower.Contains("screenshot") || lower.Contains("photo"))
        {
            return "已查看图像";
        }

        if (lower.Contains("shell") || lower.Contains("exec") || lower.Contains("command")
            || lower.Contains("bash") || lower.Contains("terminal") || lower.Contains("run"))
        {
            var preview = Compact(detail);
            return preview.Length > 0 ? "已运行 " + preview : "运行了命令";
        }

        if (lower.Contains("write") || lower.Contains("edit") || lower.Contains("patch")
            || lower.Contains("apply") || lower.Contains("rename"))
        {
            return "已编辑文件";
        }

        if (lower.Contains("read") || lower.Contains("view") || lower.Contains("open"))
        {
            return "已读取文件";
        }

        if (lower.Contains("glob") || lower.Contains("grep") || lower.Contains("search")
            || lower.Contains("list") || lower.Contains("find"))
        {
            return "已搜索文件";
        }

        if (lower.Contains("web") || lower.Contains("fetch") || lower.Contains("browse"))
        {
            return "已搜索网页";
        }

        if (lower.Contains("todo") || lower.Contains("plan"))
        {
            return "已更新计划";
        }

        return "已调用工具";
    }

    public static ChatItemViewModel User(
        string text,
        string? apiText = null,
        DateTimeOffset? timestamp = null) =>
        new(
            ChatItemKind.User,
            "你",
            text,
            detail: apiText ?? string.Empty,
            timestampLabel: timestamp is null
                ? string.Empty
                : FormatTimestamp(timestamp.Value));

    public static ChatItemViewModel Assistant(string text) =>
        new(ChatItemKind.Assistant, "kl", text);

    public static ChatItemViewModel Reasoning(string text = "") =>
        new(ChatItemKind.Reasoning, "思考中", text);

    public static ChatItemViewModel Tool(
        string header,
        string command,
        string output,
        string summary,
        string toolLabel = "") =>
        new(ChatItemKind.Tool, header, command, output, summary, toolLabel: toolLabel);

    public static ChatItemViewModel ToolGroup(string kindLabel) =>
        new(ChatItemKind.ToolGroup, GroupHeader(kindLabel), toolLabel: kindLabel)
        {
            _groupKind = kindLabel
        };

    private static string GroupHeader(string kindLabel) => kindLabel switch
    {
        "Shell" => "运行了命令",
        "Image" => "已查看图像",
        "Read" => "已读取文件",
        "Search" => "已搜索文件",
        "Edit" => "已编辑文件",
        "Web" => "已搜索网页",
        _ => "工具调用"
    };

    public void AddChild(ChatItemViewModel child)
    {
        Children.Add(child);
        if (string.Equals(_groupKind, "Image", StringComparison.Ordinal))
        {
            Header = $"已查看 {Children.Count} 张图像";
        }
    }

    public string EffectiveKindLabel()
    {
        if (IsToolGroup)
        {
            return _groupKind;
        }
        if (!string.IsNullOrEmpty(ToolLabel))
        {
            return ToolLabel;
        }

        var header = Header;
        if (header.Contains("运行")) return "Shell";
        if (header.Contains("图像")) return "Image";
        if (header.Contains("编辑")) return "Edit";
        if (header.Contains("读取")) return "Read";
        if (header.Contains("搜索")) return "Search";
        return "Tool";
    }

    public static ChatItemViewModel Error(string text) =>
        new(ChatItemKind.Error, "请求失败", text, status: "错误");

    public static ChatItemViewModel FromStored(StoredMessage message, DateTimeOffset? fallbackDate = null)
    {
        // Older GUI states stored only "H:mm"; date it from the session's last update
        // so the turn divider can still format "M月d日ddd H:mm".
        var timestamp = message.Timestamp;
        if (fallbackDate is not null
            && DateTime.TryParseExact(timestamp, "H:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var timeOfDay))
        {
            var dated = fallbackDate.Value.ToLocalTime().Date.Add(timeOfDay.TimeOfDay);
            timestamp = dated.ToString("M月d日 H:mm", CultureInfo.InvariantCulture);
        }
        var kind = message.Kind.ToLowerInvariant() switch
        {
            "user" => ChatItemKind.User,
            "reasoning" => ChatItemKind.Reasoning,
            "tool" => ChatItemKind.Tool,
            "error" => ChatItemKind.Error,
            _ => ChatItemKind.Assistant
        };
        var text = message.Text;
        var detail = message.Detail;
        var summary = message.Summary;
        var header = message.Header;
        var toolLabel = string.Empty;
        if (kind == ChatItemKind.Tool)
        {
            if (text.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                text = ExtractCommand(text);
            }

            if (header.StartsWith("调用 ", StringComparison.Ordinal))
            {
                var legacyName = header[3..];
                header = ToolHeader(legacyName, text);
                toolLabel = ToolKindLabel(legacyName);
            }
            else if (string.Equals(header, "工具调用", StringComparison.Ordinal))
            {
                header = string.Empty;
            }

            if (text.TrimStart().StartsWith('{')
                && detail.StartsWith("模型请求调用工具", StringComparison.Ordinal))
            {
                (text, detail) = (detail, text);
            }
            if (string.Equals(summary, message.Header, StringComparison.Ordinal))
            {
                summary = string.Empty;
            }
        }
        return new ChatItemViewModel(
            kind,
            header,
            text,
            detail,
            summary,
            message.Status,
            timestamp,
            toolLabel);
    }

    public StoredMessage ToStored() => new()
    {
        Kind = Kind.ToString().ToLowerInvariant(),
        Header = Header,
        Text = Text,
        Detail = Detail,
        Summary = Summary,
        Status = StatusText,
        Timestamp = TimestampLabel
    };

    public void Start(string status = "进行中")
    {
        IsRunning = true;
        if (IsActivity)
        {
            IsExpanded = true;
        }
        StatusText = status;
    }

    public void AppendText(string text)
    {
        if (text.Length > 0)
        {
            Text += text;
        }
    }

    public void AppendDetail(string text)
    {
        if (text.Length > 0)
        {
            Detail += text;
        }
    }

    public void SetHeader(string header) => Header = header;
    public void SetSummary(string summary) => Summary = summary;

    public void Complete(string? status = "完成", string? header = null)
    {
        IsRunning = false;
        IsExpanded = false;
        if (header is not null)
        {
            Header = header;
        }

        StatusText = status ?? string.Empty;
        if (IsAssistant && string.IsNullOrEmpty(TimestampLabel))
        {
            TimestampLabel = FormatTimestamp(DateTimeOffset.Now);
        }
    }

    public void Stop(string status = "已停止")
    {
        IsRunning = false;
        IsExpanded = false;
        StatusText = status;
    }

    public void Toggle()
    {
        if (IsToolGroup)
        {
            GroupExpanded = !GroupExpanded;
            return;
        }

        if (IsActivity)
        {
            IsExpanded = !IsExpanded;
        }
    }
}

public sealed class SessionItemViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isVisible = true;

    public SessionItemViewModel(WorkspaceItemViewModel workspace, SessionState state)
    {
        Workspace = workspace;
        State = state;
    }

    public WorkspaceItemViewModel Workspace { get; }
    public SessionState State { get; }
    public string Id => State.Id;

    public string Title
    {
        get => State.Title;
        set
        {
            if (State.Title == value)
            {
                return;
            }
            State.Title = value;
            OnPropertyChanged();
        }
    }

    public string UpdatedLabel
    {
        get
        {
            var value = State.UpdatedAt.ToLocalTime();
            return value.Date == DateTimeOffset.Now.Date
                ? value.ToString("HH:mm")
                : value.ToString("MM-dd");
        }
    }

    /// <summary>Codex-style relative time used by the session preview card.</summary>
    public string RelativeLabel
    {
        get
        {
            var elapsed = DateTimeOffset.Now - State.UpdatedAt.ToLocalTime();
            if (elapsed.TotalMinutes < 1)
            {
                return "刚刚";
            }

            if (elapsed.TotalHours < 1)
            {
                return $"{(int)elapsed.TotalMinutes} 分钟前";
            }

            if (elapsed.TotalDays < 1)
            {
                return $"{(int)elapsed.TotalHours} 小时前";
            }

            if (elapsed.TotalDays < 30)
            {
                return $"{(int)elapsed.TotalDays} 天前";
            }

            return State.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd");
        }
    }

    public bool IsPinned
    {
        get => State.IsPinned;
        set
        {
            if (State.IsPinned == value)
            {
                return;
            }

            State.IsPinned = value;
            OnPropertyChanged();
        }
    }

    public bool IsArchived
    {
        get => State.IsArchived;
        set
        {
            if (State.IsArchived == value)
            {
                return;
            }

            State.IsArchived = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsListVisible));
        }
    }

    public bool IsUnread
    {
        get => State.IsUnread;
        set
        {
            if (State.IsUnread == value)
            {
                return;
            }

            State.IsUnread = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Visible in the sidebar lists (search filter + not archived).</summary>
    public bool IsListVisible => IsVisible && !IsArchived;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public void RefreshTime() => OnPropertyChanged(nameof(UpdatedLabel));

    public void RefreshTitle() => OnPropertyChanged(nameof(Title));

    public bool Matches(string query)
    {
        if (State.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase))
        {
            return true;
        }
        return State.Messages.Any(message =>
            message.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || message.Detail.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }
}

public sealed class WorkspaceItemViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isVisible = true;
    private bool _hasVisibleSessions = true;
    private bool _isSearchActive;
    private string _pathLabel = string.Empty;

    public WorkspaceItemViewModel(WorkspaceState state)
    {
        State = state;
        Sessions = new ObservableCollection<SessionItemViewModel>(
            state.Sessions
                .OrderByDescending(session => session.UpdatedAt)
                .Select(session => new SessionItemViewModel(this, session)));
        foreach (var session in Sessions)
        {
            session.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SessionItemViewModel.IsVisible))
                {
                    RefreshVisibility();
                }
            };
        }

        Sessions.CollectionChanged += (_, _) =>
        {
            RefreshPreview();
            OnPropertyChanged(nameof(HasNoSessions));
        };
    }

    public WorkspaceState State { get; }
    public ObservableCollection<SessionItemViewModel> Sessions { get; }
    public string Id => State.Id;
    public string Name => State.Name;
    public string Path => State.Path;

    /// <summary>
    /// Shown only when two projects share a name, so the working directory can be
    /// told apart from a same-named sibling (e.g. "kl" vs "kl · D:\kl").
    /// </summary>
    public string PathLabel
    {
        get => _pathLabel;
        private set
        {
            if (SetProperty(ref _pathLabel, value))
            {
                OnPropertyChanged(nameof(HasPathLabel));
            }
        }
    }

    public bool HasPathLabel => !string.IsNullOrEmpty(PathLabel);

    public void SetPathLabel(string value) => PathLabel = value;

    /// <summary>Codex-style project preview details.</summary>
    public string ChatCountLabel => $"{Sessions.Count} 次聊天";

    public bool HasNoSessions => Sessions.Count == 0;

    public string ParentPath
    {
        get
        {
            try
            {
                var parent = System.IO.Path.GetDirectoryName(State.Path);
                return string.IsNullOrWhiteSpace(parent) ? State.Path : parent;
            }
            catch (ArgumentException)
            {
                return State.Path;
            }
        }
    }

    public bool IsPinned
    {
        get => State.IsPinned;
        set
        {
            if (State.IsPinned == value)
            {
                return;
            }

            State.IsPinned = value;
            OnPropertyChanged();
        }
    }

    public void RefreshName() => OnPropertyChanged(nameof(Name));

    public void RefreshPreview()
    {
        OnPropertyChanged(nameof(ChatCountLabel));
        OnPropertyChanged(nameof(ParentPath));
    }

    /// <summary>Re-orders the nested conversations for "聊天排序方式".</summary>
    public void ApplySessionSort(bool byCreated)
    {
        var ordered = (byCreated
                ? Sessions.OrderByDescending(session => session.State.CreatedAt)
                : Sessions.OrderByDescending(session => session.State.UpdatedAt))
            .ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var current = Sessions.IndexOf(ordered[index]);
            if (current != index)
            {
                Sessions.Move(current, index);
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }

    public bool HasVisibleSessions
    {
        get => _hasVisibleSessions;
        private set => SetProperty(ref _hasVisibleSessions, value);
    }

    public bool IsSearchActive
    {
        get => _isSearchActive;
        private set
        {
            if (SetProperty(ref _isSearchActive, value))
            {
                OnPropertyChanged(nameof(ShowSessions));
            }
        }
    }

    public bool IsExpanded
    {
        get => State.IsExpanded;
        set
        {
            if (State.IsExpanded == value)
            {
                return;
            }
            State.IsExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowSessions));
        }
    }

    public bool ShowSessions => IsExpanded || IsSearchActive;

    public void ApplySearch(string query)
    {
        var workspaceMatches = query.Length == 0
            || Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || Path.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        IsSearchActive = query.Length > 0;
        foreach (var session in Sessions)
        {
            session.IsVisible = workspaceMatches || session.Matches(query);
        }
        IsVisible = workspaceMatches || Sessions.Any(session => session.IsVisible);
        RefreshVisibility();
    }

    private void RefreshVisibility()
    {
        HasVisibleSessions = Sessions.Any(session => session.IsVisible);
    }
}

public sealed class PluginItemViewModel : ObservableObject
{
    private bool _enabled;
    private string _statusText = "本地文件";

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
    public string KindLabel { get; } = "插件";
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

/// <summary>One row in the composer's reasoning-effort picker.</summary>
public sealed class ReasoningEffortOption : ObservableObject
{
    private bool _isSelected;

    public ReasoningEffortOption(string id, string label)
    {
        Id = id;
        Label = label;
    }

    public string Id { get; }
    public string Label { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>One file group in the palette (name matches, or content matches).</summary>
/// <summary>One home subdirectory row in the 存储空间 settings pane.</summary>
public sealed class StorageFolderViewModel
{
    public StorageFolderViewModel(string name, string path, long bytes)
    {
        Name = name;
        Path = path;
        Bytes = bytes;
    }

    public string Name { get; }
    public string Path { get; }
    public long Bytes { get; }
    public string SizeLabel => FormatBytes(Bytes);

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }
}

public sealed class PaletteFileGroupViewModel
{
    public PaletteFileGroupViewModel(
        string relativePath,
        IReadOnlyList<PaletteLineMatch> lines,
        string query = "")
    {
        RelativePath = relativePath;
        Query = query;
        Lines = lines.Select(line => new PaletteLineViewModel(relativePath, line, query)).ToList();
    }

    public string RelativePath { get; }
    public string Query { get; }
    public string Name => Path.GetFileName(RelativePath);
    public string Folder
    {
        get
        {
            var folder = Path.GetDirectoryName(RelativePath)?.Replace('\\', '/');
            return string.IsNullOrEmpty(folder) ? "根目录" : folder;
        }
    }

    public IReadOnlyList<PaletteLineViewModel> Lines { get; }
    public string CountLabel => Lines.Count > 0 ? $"{Lines.Count} 处" : string.Empty;
}

/// <summary>One matching line under a file group in the palette.</summary>
public sealed class PaletteLineViewModel
{
    public PaletteLineViewModel(string relativePath, PaletteLineMatch match, string query = "")
    {
        RelativePath = relativePath;
        LineNumber = match.LineNumber;
        Text = match.Text;
        Query = query;
    }

    public string RelativePath { get; }
    public string Query { get; }
    public int LineNumber { get; }
    public string Text { get; }
    public string LineLabel => LineNumber.ToString();
}

/// <summary>One Library row. The GUI keeps these locally; Core owns session data.</summary>
public sealed class LibraryItemViewModel
{
    public LibraryItemViewModel(LibraryItemState state) => State = state;

    public LibraryItemState State { get; }
    public string Content => State.Content;
    public string Title => string.IsNullOrWhiteSpace(State.Title) ? FirstLine(State.Content) : State.Title;
    public string Preview => State.Content.Length <= 180 ? State.Content : State.Content[..180] + "…";
    public string CreatedLabel => State.CreatedAt.ToLocalTime().ToString("MM-dd");

    private static string FirstLine(string text)
    {
        var line = text.Split('\n').FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
        if (string.IsNullOrEmpty(line))
        {
            return "未命名条目";
        }

        return line.Length <= 32 ? line : line[..32] + "…";
    }
}

/// <summary>One discovered import item with the user's selection and result.</summary>
public sealed class ImportCandidateRow : ObservableObject
{
    private readonly Action _selectionChanged;
    private bool _isSelected = true;
    private string _status = string.Empty;

    public ImportCandidateRow(ImportCandidate candidate, Action selectionChanged)
    {
        Candidate = candidate;
        _selectionChanged = selectionChanged;
    }

    public ImportCandidate Candidate { get; }
    public string Name => Candidate.Name;
    public string Description => Candidate.Description;
    public string SourcePath => Candidate.SourcePath;
    public string DestinationPath => Candidate.DestinationPath;
    public string SizeLabel => Candidate.SizeLabel;
    public string KindLabel => Candidate.Kind == ImportKind.Instructions ? "说明文件" : "技能";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _selectionChanged();
            }
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (SetProperty(ref _status, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => _status.Length > 0;
}

public sealed partial class MainWindowViewModel : ObservableObject
{
    /// <summary>Codex-style transcript ruler ticks down the left edge of the chat column.</summary>
    public IReadOnlyList<int> RulerTicks { get; } = Enumerable.Range(0, 200).ToArray();

    private static readonly string[] SupportedPermissionModes = ["完全访问", "帮我批准", "请求批准"];
    private static readonly HashSet<string> SupportedAttachmentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".jsonl", ".cs", ".ts", ".tsx", ".js", ".jsx", ".css", ".html",
        ".xml", ".yaml", ".yml", ".ps1", ".py", ".java", ".go", ".rs", ".sql", ".toml", ".ini",
        ".log", ".csv", ".csproj", ".props", ".targets", ".sln"
    };
    private const int MaxAttachmentCount = 8;
    private const int MaxAttachmentCharacters = 100_000;
    private readonly string _homeDirectory;
    private string _agentsPath;
    private readonly StateStore _stateStore;
    private readonly ProviderCacheStore _providerCacheStore;
    /// <summary>An imported state file must survive until restart instead of being overwritten by autosave.</summary>
    private bool _stateImported;
    private readonly IPluginCatalogService _pluginCatalogService;
    private AppState _state;
    private WorkspaceItemViewModel _activeWorkspace;
    private SessionState? _activeSession;
    private SessionItemViewModel? _activeSessionItem;
    private SessionItemViewModel? _dialogTarget;
    private WorkspaceItemViewModel? _renameWorkspaceTarget;
    private ChatItemViewModel? _activeReasoning;
    private ChatItemViewModel? _activeAssistant;
    private string _composer = string.Empty;
    private string _searchText = string.Empty;
    private string _sessionTitle = "新会话";
    private string _settingsPermissionMode = "完全访问";
    private int _settingsContentFontSize = 14;
    private string _settingsStatus = string.Empty;
    private string _settingsSelectedModel = string.Empty;
    private string _libraryDraftTitle = string.Empty;
    private string _doctorSummary = string.Empty;
    private bool _isDoctorRunning;
    private string _libraryDraftContent = string.Empty;
    private string _settingsApiKeyDraft = string.Empty;
    private string _renameTitle = string.Empty;
    private string _deletePrompt = string.Empty;
    private bool _isBusy;
    private bool _isSidebarVisible = true;
    private bool _isSearchVisible;
    private bool _isRecentExpanded = true;
    private bool _isChatPage = true;
    private bool _isLibraryPage;
    private bool _isCodeReviewDraftActive;
    private bool _isProjectsPage;
    private bool _isPluginsPage;
    private bool _isAutomationPage;
    private bool _isSettingsPage;
    private bool _isRenameDialogOpen;
    private bool _isDeleteDialogOpen;
    private bool _isTestingConnection;
    private bool _settingsStatusIsError;
    private string _errorMessage = string.Empty;
    private readonly ImportDiscoveryService _importService;
    private bool _isImportScanning;
    private string _importStatusText = "点击“扫描”查找 Codex 或 Claude 的说明文件与用户技能。";
    private string _pluginStatus = string.Empty;
    private string _pluginSearch = string.Empty;
    private IReadOnlyList<PluginItemViewModel> _allPlugins = [];
    private AutomationItemViewModel? _automationDialogTarget;
    private AutomationItemViewModel? _automationDeleteTarget;
    private AutomationIntervalOption? _selectedAutomationInterval;
    private string _automationName = string.Empty;
    private string _automationPrompt = string.Empty;
    private string _automationDialogStatus = string.Empty;
    private string _automationDeletePrompt = string.Empty;
    private bool _isAutomationDialogOpen;
    private bool _isAutomationDeleteDialogOpen;
    private bool _automationSchedulerBusy;
    private bool _isSettingsOpen;
    private SettingsPane _settingsPane = SettingsPane.General;
    private AutomationFilter _automationFilter = AutomationFilter.All;
    private string _automationSearch = string.Empty;

    public MainWindowViewModel(
        StateStore? stateStore = null,
        IPluginCatalogService? pluginCatalogService = null,
        ICoreSessionService? coreSessionService = null,
        Action<Action>? dispatcher = null,
        string? homeDirectory = null,
        ProviderCacheStore? providerCacheStore = null,
        ImportDiscoveryService? importService = null)
    {
        _homeDirectory = homeDirectory ?? KlDataPaths.Default.HomeDirectory;
        _importService = importService ?? new ImportDiscoveryService(klHome: _homeDirectory);
        _agentsPath = Path.Combine(_homeDirectory, "AGENTS.md");
        _stateStore = stateStore ?? new StateStore();
        _providerCacheStore = providerCacheStore
            ?? new ProviderCacheStore(Path.Combine(
                Path.GetDirectoryName(_stateStore.Path) ?? _homeDirectory,
                "providers-cache.json"));
        _pluginCatalogService = pluginCatalogService ?? new LocalPluginCatalogService();
        _core = coreSessionService ?? new UnavailableCoreSessionService();
        _dispatch = dispatcher ?? (static action => action());
        _core.EventReceived += OnCoreEventReceived;
        _state = _stateStore.Load();
        NormalizeState();
        HydrateProviderCache();
        BuildShortcutRows();
        LoadRemoteCoreSettings();
        RefreshPinnedRailItems();
        BuildWorkspaceItems();
        _activeWorkspace = Workspaces.First(workspace => workspace.Id == _state.ActiveWorkspaceId);
        _activeWorkspace.IsSelected = true;
        if (_state.RestoreLastSession
            && TryFindSession(_state.ActiveSessionId, out var activeWorkspace, out var activeSession))
        {
            SelectSession(activeWorkspace, activeSession.State);
        }
        else
        {
            StartDraftSession();
        }

        MemoryEntries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowMemoryEntries));
        UsageRows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowUsageRows));
        McpTools.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowMcpTools));
        RefreshSettingsDraft();
        RefreshPlugins();
        BuildAutomationItems();
        foreach (var item in _state.LibraryItems)
        {
            LibraryItems.Add(new LibraryItemViewModel(item));
        }
        RefreshHomeSuggestions();

        SaveState(silent: true);

        Messages.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasConversation));
            OnPropertyChanged(nameof(ShowConversationHeader));
            OnPropertyChanged(nameof(ShowWorkspaceStrip));
            OnPropertyChanged(nameof(ShowChatComposerChips));
            OnPropertyChanged(nameof(ComposerMargin));
            OnPropertyChanged(nameof(ComposerMaxWidth));
            OnPropertyChanged(nameof(ComposerPlaceholder));
        };

        _ = InitializeCoreAsync();
    }

    public ObservableCollection<WorkspaceItemViewModel> Workspaces { get; } = [];
    public ObservableCollection<ChatItemViewModel> Messages { get; } = [];
    public ObservableCollection<AttachmentItemViewModel> PendingAttachments { get; } = [];
    public ObservableCollection<PluginItemViewModel> Plugins { get; } = [];
    public ObservableCollection<ImportCandidateRow> ImportCandidates { get; } = [];
    public ObservableCollection<SessionItemViewModel> RecentSessions { get; } = [];
    public ObservableCollection<SessionItemViewModel> PinnedSessions { get; } = [];
    public ObservableCollection<SessionItemViewModel> ArchivedSessions { get; } = [];
    public ObservableCollection<PinnedRailItemViewModel> PinnedRailItems { get; } = [];
    public ObservableCollection<AutomationItemViewModel> Automations { get; } = [];
    public ObservableCollection<LibraryItemViewModel> LibraryItems { get; } = [];
    public ObservableCollection<LibraryItemViewModel> HomeSuggestions { get; } = [];
    public bool HasHomeSuggestions => HomeSuggestions.Count > 0;
    public IReadOnlyList<AutomationIntervalOption> AutomationIntervals { get; } =
    [
        new(15, "每 15 分钟"),
        new(60, "每小时"),
        new(360, "每 6 小时"),
        new(1440, "每天")
    ];
    public IReadOnlyList<string> PermissionModes => SupportedPermissionModes;

    public IReadOnlyList<ReasoningEffortOption> ReasoningEffortOptions { get; } =
    [
        new("default", "默认"),
        new("low", "低"),
        new("medium", "中"),
        new("high", "高")
    ];

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
        ReasoningEffortOptions.FirstOrDefault(option => option.Id == _reasoningEffort)?.Label ?? "默认";

    /// <summary>Live value while the slider thumb is being dragged.</summary>
    public string ReasoningEffortDisplayLabel =>
        ReasoningEffortOptions.FirstOrDefault(option => option.Id == (_reasoningEffortPreview ?? _reasoningEffort))
            ?.Label ?? "默认";

    public bool IsReasoningHighDisplay =>
        string.Equals(_reasoningEffortPreview ?? _reasoningEffort, "high", StringComparison.Ordinal);

    public bool HasReasoningEffortDisplay =>
        !string.Equals(_reasoningEffortPreview ?? _reasoningEffort, "default", StringComparison.Ordinal);

    /// <summary>Shows the dragged tier immediately, before Core is written.</summary>
    /// <summary>Settings ComboBox index: 默认/低/中/高, mapping 0-3.</summary>
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
        OnPropertyChanged(nameof(IsReasoningHighDisplay));
        OnPropertyChanged(nameof(HasReasoningEffortDisplay));
    }

    public bool HasReasoningEffort => !string.Equals(_reasoningEffort, "default", StringComparison.Ordinal);

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
            ReportError($"保存设置失败：{error.Message}");
        }
    }

    public bool IsReasoningDefault => string.Equals(_reasoningEffort, "default", StringComparison.Ordinal);
    public bool IsReasoningLow => string.Equals(_reasoningEffort, "low", StringComparison.Ordinal);
    public bool IsReasoningMedium => string.Equals(_reasoningEffort, "medium", StringComparison.Ordinal);
    public bool IsReasoningHigh => string.Equals(_reasoningEffort, "high", StringComparison.Ordinal);

    public bool IsPermissionFull =>
        string.Equals(_state.PermissionMode, "完全访问", StringComparison.Ordinal);

    public bool IsPermissionAuto =>
        string.Equals(_state.PermissionMode, "帮我批准", StringComparison.Ordinal);

    public bool IsPermissionAsk =>
        string.Equals(_state.PermissionMode, "请求批准", StringComparison.Ordinal);

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
    public IReadOnlyList<int> ContentFontSizes { get; } = [13, 14, 15, 16];
    public string VersionLabel => "0.3.0-gui.1";
    public AppState State => _state;
    public SessionItemViewModel? ActiveSessionItem => _activeSessionItem;

    public bool IsEmpty => Messages.Count == 0;
    public bool HasConversation => !IsEmpty;
    public bool IsChatPage => _isChatPage;
    public bool IsLibraryPage => _isLibraryPage;
    public bool IsProjectsPage => _isProjectsPage;
    public bool IsPluginsPage => _isPluginsPage;
    public bool IsAutomationPage => _isAutomationPage;
    public bool IsSettingsPage => _isSettingsPage;
    public bool IsAddProjectPinned => _state.IsAddProjectPinned;
    public bool IsCodeReviewPinned => _state.IsCodeReviewPinned;
    public bool ShowConversationHeader => IsChatPage && HasConversation;

    public bool IsSidebarVisible
    {
        get => _isSidebarVisible;
        private set => SetProperty(ref _isSidebarVisible, value);
    }

    /// <summary>Expanded sidebar width; the rail collapses below 3/4 of this value.</summary>
    public double SidebarWidth => _state.SidebarWidth;

    public void SetSidebarWidth(double width)
    {
        var clamped = Math.Clamp(width, 238, 468);
        if (Math.Abs(_state.SidebarWidth - clamped) < 0.5)
        {
            return;
        }

        _state.SidebarWidth = clamped;
        SaveState();
        OnPropertyChanged(nameof(SidebarWidth));
    }

    public void SetSidebarVisible(bool visible) => IsSidebarVisible = visible;

    public bool IsSearchVisible
    {
        get => _isSearchVisible;
        private set => SetProperty(ref _isSearchVisible, value);
    }

    public bool IsRecentExpanded
    {
        get => _isRecentExpanded;
        private set => SetProperty(ref _isRecentExpanded, value);
    }

    public bool HasPinnedSessions => PinnedSessions.Count > 0;

    private bool _isPinnedExpanded = true;

    public bool IsPinnedExpanded
    {
        get => _isPinnedExpanded;
        private set => SetProperty(ref _isPinnedExpanded, value);
    }

    public void TogglePinned() => IsPinnedExpanded = !IsPinnedExpanded;

    public bool IsPinnedSortByRecent =>
        string.Equals(_state.PinnedSort, "recent", StringComparison.OrdinalIgnoreCase);

    public bool IsPinnedSortByManual => !IsPinnedSortByRecent;

    public void SetPinnedSortMode(string mode)
    {
        var value = string.Equals(mode, "recent", StringComparison.OrdinalIgnoreCase) ? "recent" : "manual";
        if (string.Equals(_state.PinnedSort, value, StringComparison.Ordinal))
        {
            return;
        }

        _state.PinnedSort = value;
        OnPropertyChanged(nameof(IsPinnedSortByRecent));
        OnPropertyChanged(nameof(IsPinnedSortByManual));
        RefreshRecentSessions();
        SaveState();
    }
    public bool HasRecentSessions => RecentSessions.Count > 0;
    public bool ShowNoRecentSessions => !HasRecentSessions;
    public bool ShowArchivedSessions => ArchivedSessions.Count > 0;

    public bool IsProjectSectionExpanded
    {
        get => _state.IsProjectSectionExpanded;
        private set
        {
            if (_state.IsProjectSectionExpanded == value)
            {
                return;
            }

            _state.IsProjectSectionExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowProjectList));
        }
    }

    /// <summary>Codex "整理侧边栏": grouped shows projects, merged hides them.</summary>
    public bool IsSidebarGrouped => _state.IsSidebarGrouped;

    public bool ShowProjectList => IsSidebarGrouped && IsProjectSectionExpanded;

    /// <summary>Codex "聊天排序方式".</summary>
    public bool IsSessionSortByUpdated => _state.SessionSort != "created";

    public bool IsSessionSortByCreated => _state.SessionSort == "created";

    public void SetSidebarGrouped(bool grouped)
    {
        if (_state.IsSidebarGrouped == grouped)
        {
            return;
        }

        _state.IsSidebarGrouped = grouped;
        OnPropertyChanged(nameof(IsSidebarGrouped));
        OnPropertyChanged(nameof(ShowProjectList));
        SaveState();
    }

    public void SetSessionSort(string sort)
    {
        var value = string.Equals(sort, "created", StringComparison.OrdinalIgnoreCase) ? "created" : "updated";
        if (_state.SessionSort == value)
        {
            return;
        }

        _state.SessionSort = value;
        OnPropertyChanged(nameof(IsSessionSortByUpdated));
        OnPropertyChanged(nameof(IsSessionSortByCreated));
        foreach (var workspace in Workspaces)
        {
            workspace.ApplySessionSort(value == "created");
        }

        RefreshRecentSessions();
        SaveState();
    }

    public void ToggleProjectSection()
    {
        IsProjectSectionExpanded = !IsProjectSectionExpanded;
        SaveState();
    }

    public string UserInitials
    {
        get
        {
            var name = Environment.UserName;
            var letters = new string(name.Where(char.IsLetter).ToArray());
            if (letters.Length >= 2)
            {
                return letters[..2].ToLowerInvariant();
            }

            return letters.Length == 1 ? letters.ToLowerInvariant() : "kl";
        }
    }

    public string UserDisplayName
    {
        get
        {
            var name = Environment.UserName;
            return string.IsNullOrWhiteSpace(name) ? "本地用户" : name;
        }
    }

    public string AccountPlanLabel => "Local";

    public string ComposerPlaceholder => HasQueuedTurns
        ? $"已排队 {QueuedTurnCount} 条；回复完成后自动发送"
        : HasConversation
            ? IsChatMode ? "和 kl 一起聊天" : "和 kl 一起工作"
            : IsChatMode ? "使用 Chat" : "随心输入";

    /// <summary>Codex-style empty-state headline that follows the selected mode.</summary>
    public string EmptyStateTitle => IsChatMode ? "我们要做什么?" : $"我们应该在{WorkspaceName}中做些什么?";
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        private set => SetProperty(ref _isSettingsOpen, value);
    }
    public SettingsPane ActiveSettingsPane => _settingsPane;
    public bool IsSettingsGeneral => _settingsPane == SettingsPane.General;
    public bool IsSettingsNotifications => _settingsPane == SettingsPane.Notifications;
    public bool IsSettingsAppearance => _settingsPane == SettingsPane.Appearance;
    public bool IsSettingsMemory => _settingsPane == SettingsPane.Memory;
    public bool IsSettingsArchived => _settingsPane == SettingsPane.Archived;
    public bool IsSettingsShortcuts => _settingsPane == SettingsPane.Shortcuts;
    public bool IsSettingsProviders => _settingsPane == SettingsPane.Providers;
    public bool IsSettingsModels => _settingsPane == SettingsPane.Models;
    public bool IsSettingsUsage => _settingsPane == SettingsPane.Usage;
    public bool IsSettingsConfig => _settingsPane == SettingsPane.Config;
    public WorkspaceItemViewModel ActiveWorkspace => _activeWorkspace;

    public bool IsSettingsImport => _settingsPane == SettingsPane.Import;
    public bool IsSettingsCloudPc => _settingsPane == SettingsPane.CloudPc;
    public bool IsSettingsCodeReview => _settingsPane == SettingsPane.CodeReview;
    public bool IsSettingsStorage => _settingsPane == SettingsPane.Storage;
    public bool IsSettingsPersonalization => _settingsPane == SettingsPane.Personalization;
    public bool IsSettingsPlugins => _settingsPane == SettingsPane.Plugins;
    public bool IsSettingsPasswords => _settingsPane == SettingsPane.Passwords;
    public bool IsSettingsHooks => _settingsPane == SettingsPane.Hooks;
    public bool IsSettingsConnections => _settingsPane == SettingsPane.Connections;
    public bool IsSettingsGit => _settingsPane == SettingsPane.Git;
    public bool IsSettingsEnvironments => _settingsPane == SettingsPane.Environments;
    public bool IsSettingsWorktrees => _settingsPane == SettingsPane.Worktrees;
    public string SettingsPaneTitle => _settingsPane switch
    {
        SettingsPane.Notifications => "通知",
        SettingsPane.Import => "导入",
        SettingsPane.Appearance => "外观",
        SettingsPane.Archived => "已归档的聊天",
        SettingsPane.Storage => "存储空间",
        SettingsPane.Config => "配置",
        SettingsPane.Personalization => "个性化",
        SettingsPane.Shortcuts => "键盘快捷键",
        SettingsPane.Providers => "模型提供商",
        SettingsPane.Usage => "使用情况和计费",
        SettingsPane.Plugins => "插件",
        SettingsPane.Passwords => "密码",
        SettingsPane.CloudPc => "云电脑",
        SettingsPane.Hooks => "钩子",
        SettingsPane.Connections => "连接",
        SettingsPane.CodeReview => "代码审查",
        SettingsPane.Git => "Git",
        SettingsPane.Environments => "环境",
        SettingsPane.Worktrees => "Worktrees",
        SettingsPane.Memory => "记忆",
        SettingsPane.Models => "模型与连接",
        _ => "常规"
    };
    public string SettingsPaneSubtitle => _settingsPane switch
    {
        SettingsPane.Import => "将其他 AI 应用的设置、项目和聊天导入 KL",
        SettingsPane.Config => "配置新聊天的权限、网页访问和智能体回复",
        SettingsPane.Plugins => "管理插件、技能和 MCP",
        SettingsPane.CloudPc => "管理云端浏览设置和已保存的网站 Cookie",
        SettingsPane.Hooks => "通过配置和已启用的插件管理生命周期钩子。",
        SettingsPane.Environments => "本地环境用于指导 KL 如何为项目设置工作树。",
        SettingsPane.Memory => "用户记忆对所有项目生效；工作区记忆只属于当前项目。",
        SettingsPane.Providers => "连接你使用的模型提供商；请求地址、协议和密钥由 KL Core 管理。",
        SettingsPane.Models => "KL Core 执行所有模型调用；这里只保存默认选择和凭据。",
        _ => string.Empty
    };

    public bool HasSettingsPaneSubtitle => SettingsPaneSubtitle.Length > 0;

    public bool IsAutomationFilterAll => _automationFilter == AutomationFilter.All;
    public bool IsAutomationFilterActive => _automationFilter == AutomationFilter.Active;
    public bool IsAutomationFilterEnded => _automationFilter == AutomationFilter.Ended;
    public string AutomationEmptyTitle => Automations.Count == 0 ? "还没有自动化任务" : "没有匹配的自动化任务";
    public string AutomationEmptyDescription => Automations.Count == 0
        ? "创建任务后可手动运行；启用后会按间隔自动执行。"
        : "调整筛选条件或搜索关键词后再试。";
    public bool IsRenameDialogOpen
    {
        get => _isRenameDialogOpen;
        private set
        {
            if (SetProperty(ref _isRenameDialogOpen, value))
            {
                OnPropertyChanged(nameof(IsModalDialogOpen));
            }
        }
    }
    public bool IsDeleteDialogOpen
    {
        get => _isDeleteDialogOpen;
        private set
        {
            if (SetProperty(ref _isDeleteDialogOpen, value))
            {
                OnPropertyChanged(nameof(IsModalDialogOpen));
            }
        }
    }

    /// <summary>Any modal dialog owns the keyboard; the terminal must not eat its keys.</summary>
    public bool IsModalDialogOpen => IsRenameDialogOpen || IsDeleteDialogOpen || IsCloseConfirmOpen
        || IsCreateProjectDialogOpen || IsAutomationDialogOpen || IsAutomationDeleteDialogOpen
        || IsWorktreeDeleteConfirmOpen;

    private bool _isCloseConfirmOpen;
    public bool IsCloseConfirmOpen
    {
        get => _isCloseConfirmOpen;
        private set
        {
            if (SetProperty(ref _isCloseConfirmOpen, value))
            {
                OnPropertyChanged(nameof(IsModalDialogOpen));
            }
        }
    }

    public void OpenCloseConfirm() => IsCloseConfirmOpen = true;

    public void CloseCloseConfirm() => IsCloseConfirmOpen = false;

    private const int MaxAgentsFileCharacters = 64 * 1024;
    private bool _isAgentsEditorOpen;
    private string _agentsDraft = string.Empty;
    private string _agentsStatus = string.Empty;

    public bool IsAgentsEditorOpen
    {
        get => _isAgentsEditorOpen;
        private set => SetProperty(ref _isAgentsEditorOpen, value);
    }

    public string AgentsDraft
    {
        get => _agentsDraft;
        set
        {
            if (SetProperty(ref _agentsDraft, value))
            {
                SetAgentsStatus(string.Empty);
            }
        }
    }

    public string AgentsStatus => _agentsStatus;
    public bool HasAgentsStatus => _agentsStatus.Length > 0;
    public string AgentsPathText => _agentsPath;

    private string _agentsEditorHint = string.Empty;
    public string AgentsEditorHint
    {
        get => _agentsEditorHint;
        private set => SetProperty(ref _agentsEditorHint, value);
    }

    /// <summary>Opens the editor with the global instructions file, if present.</summary>
    public void OpenAgentsEditor() => OpenAgentsEditorAt(
        Path.Combine(_homeDirectory, "AGENTS.md"),
        "全局指令会附加到每次请求的系统提示中，保存后下一个回合生效。");

    /// <summary>Edits the selected project's AGENTS.md; Core reads it for turns in that workspace.</summary>
    public void OpenProjectAgentsEditor(WorkspaceItemViewModel workspace) => OpenAgentsEditorAt(
        Path.Combine(workspace.Path, "AGENTS.md"),
        $"项目指令会附加到 {workspace.Name} 的每次请求中，保存后下一个回合生效。");

    private void OpenAgentsEditorAt(string path, string hint)
    {
        _agentsPath = path;
        AgentsEditorHint = hint;
        OnPropertyChanged(nameof(AgentsPathText));
        try
        {
            SetProperty(ref _agentsDraft, File.Exists(_agentsPath) ? File.ReadAllText(_agentsPath) : string.Empty,
                nameof(AgentsDraft));
            SetAgentsStatus(string.Empty);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetProperty(ref _agentsDraft, string.Empty, nameof(AgentsDraft));
            SetAgentsStatus($"无法读取 AGENTS.md：{error.Message}");
        }

        IsAgentsEditorOpen = true;
    }

    public void CloseAgentsEditor() => IsAgentsEditorOpen = false;

    private string _remoteCoreEndpointDraft = string.Empty;
    private string _remoteCoreTokenDraft = string.Empty;
    private string _remoteCoreTlsFingerprintDraft = string.Empty;
    private string _remoteCoreStatus = string.Empty;

    private string RemoteTokenPath => Path.Combine(_homeDirectory, "credentials", "core.token");

    public string RemoteCoreEndpointDraft
    {
        get => _remoteCoreEndpointDraft;
        set
        {
            if (SetProperty(ref _remoteCoreEndpointDraft, value))
            {
                SetRemoteCoreStatus(string.Empty);
            }
        }
    }

    public string RemoteCoreTokenDraft
    {
        get => _remoteCoreTokenDraft;
        set
        {
            if (SetProperty(ref _remoteCoreTokenDraft, value))
            {
                SetRemoteCoreStatus(string.Empty);
            }
        }
    }

    public string RemoteCoreTlsFingerprintDraft
    {
        get => _remoteCoreTlsFingerprintDraft;
        set
        {
            if (SetProperty(ref _remoteCoreTlsFingerprintDraft, value))
            {
                SetRemoteCoreStatus(string.Empty);
            }
        }
    }

    public string RemoteCoreStatus => _remoteCoreStatus;
    public bool HasRemoteCoreStatus => _remoteCoreStatus.Length > 0;

    private bool _isRemoteCoreTesting;
    public bool IsRemoteCoreTesting
    {
        get => _isRemoteCoreTesting;
        private set
        {
            if (SetProperty(ref _isRemoteCoreTesting, value))
            {
                OnPropertyChanged(nameof(CanTestRemoteCore));
            }
        }
    }

    public bool CanTestRemoteCore => !IsRemoteCoreTesting;

    /// <summary>Tests the draft endpoint/token without saving them.</summary>
    public async Task TestRemoteCoreConnectionAsync()
    {
        if (IsRemoteCoreTesting)
        {
            return;
        }

        var endpoint = _remoteCoreEndpointDraft.Trim();
        var token = _remoteCoreTokenDraft.Trim();
        var fingerprint = LocalSocketConnection.NormalizeFingerprint(_remoteCoreTlsFingerprintDraft);
        if (endpoint.Length > 0 && !LocalSocketConnection.TryParseTcp(endpoint, out _, out _))
        {
            SetRemoteCoreStatus("地址格式应为 tcp:// 或 tls://主机:端口。");
            return;
        }
        if (endpoint.Length > 0 && token.Length == 0 && !IsLoopbackEndpoint(endpoint))
        {
            SetRemoteCoreStatus("连接非本机地址需要访问令牌。");
            return;
        }
        if (!ValidateTlsFingerprint(endpoint, fingerprint))
        {
            return;
        }

        IsRemoteCoreTesting = true;
        SetRemoteCoreStatus("正在测试 KL Core 连接…");
        try
        {
            if (endpoint.Length == 0)
            {
                var snapshot = await _core.LoadSnapshotAsync(CancellationToken.None).ConfigureAwait(true);
                SetRemoteCoreStatus(snapshot.Success
                    ? $"当前 Core 可用：{snapshot.CoreVersion}（协议 v{snapshot.ProtocolVersion}）"
                    : $"当前 Core 连接失败：{snapshot.Error}");
                return;
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var client = await KlCoreClient.ConnectAsync(
                new KlClientOptions
                {
                    Endpoint = endpoint,
                    Token = token,
                    TlsFingerprint = fingerprint.Length == 0 ? null : fingerprint
                },
                timeout.Token).ConfigureAwait(true);
            await client.PingAsync("remote-core-test", timeout.Token).ConfigureAwait(true);
            SetRemoteCoreStatus($"连接成功：{endpoint}。保存并重启 KL 后使用此 Core。");
        }
        catch (OperationCanceledException)
        {
            SetRemoteCoreStatus("连接超时：10 秒内没有收到响应。");
        }
        catch (Exception error)
        {
            SetRemoteCoreStatus($"连接失败：{error.Message}");
        }
        finally
        {
            IsRemoteCoreTesting = false;
        }
    }

    private void LoadRemoteCoreSettings()
    {
        _remoteCoreEndpointDraft = _state.RemoteCoreEndpoint ?? string.Empty;
        _remoteCoreTlsFingerprintDraft = _state.RemoteCoreTlsFingerprint ?? string.Empty;
        try
        {
            _remoteCoreTokenDraft = File.Exists(RemoteTokenPath)
                ? File.ReadAllText(RemoteTokenPath).Trim()
                : string.Empty;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _remoteCoreTokenDraft = string.Empty;
        }
    }

    /// <summary>Saves the remote endpoint; CoreSessionService picks it up on restart.</summary>
    public void SaveRemoteCoreSettings()
    {
        var endpoint = _remoteCoreEndpointDraft.Trim();
        if (endpoint.Length > 0 && !LocalSocketConnection.TryParseTcp(endpoint, out _, out _))
        {
            SetRemoteCoreStatus("地址格式应为 tcp:// 或 tls://主机:端口。");
            return;
        }

        var fingerprint = LocalSocketConnection.NormalizeFingerprint(_remoteCoreTlsFingerprintDraft);
        if (!ValidateTlsFingerprint(endpoint, fingerprint))
        {
            return;
        }

        var token = _remoteCoreTokenDraft.Trim();
        if (endpoint.Length > 0 && token.Length == 0 && !IsLoopbackEndpoint(endpoint))
        {
            SetRemoteCoreStatus("连接非本机地址需要访问令牌。");
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(RemoteTokenPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            if (token.Length == 0)
            {
                if (File.Exists(RemoteTokenPath))
                {
                    File.Delete(RemoteTokenPath);
                }
            }
            else
            {
                File.WriteAllText(RemoteTokenPath, token);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetRemoteCoreStatus($"保存令牌失败：{error.Message}");
            return;
        }

        _state.RemoteCoreEndpoint = endpoint.Length == 0 ? null : endpoint;
        _state.RemoteCoreTlsFingerprint = fingerprint.Length == 0 ? null : fingerprint;
        SaveState();
        OnPropertyChanged(nameof(RemoteCoreConnectionLabel));
        RefreshCredentials();
        SetRemoteCoreStatus("已保存。重启 KL 后生效。");
    }

    public void ClearRemoteCoreSettings()
    {
        _remoteCoreEndpointDraft = string.Empty;
        _remoteCoreTokenDraft = string.Empty;
        _remoteCoreTlsFingerprintDraft = string.Empty;
        OnPropertyChanged(nameof(RemoteCoreEndpointDraft));
        OnPropertyChanged(nameof(RemoteCoreTokenDraft));
        OnPropertyChanged(nameof(RemoteCoreTlsFingerprintDraft));
        _state.RemoteCoreEndpoint = null;
        _state.RemoteCoreTlsFingerprint = null;
        try
        {
            if (File.Exists(RemoteTokenPath))
            {
                File.Delete(RemoteTokenPath);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
        SaveState();
        OnPropertyChanged(nameof(RemoteCoreConnectionLabel));
        RefreshCredentials();
        SetRemoteCoreStatus("已清除。重启 KL 后使用本地 Core。");
    }

    /// <summary>tls:// needs a 64-character SHA-256 fingerprint; plain tcp:// must not carry one.</summary>
    private bool ValidateTlsFingerprint(string endpoint, string fingerprint)
    {
        if (fingerprint.Length > 0
            && (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit)))
        {
            SetRemoteCoreStatus("TLS 指纹应为 64 位十六进制 SHA-256。");
            return false;
        }
        if (endpoint.StartsWith("tls://", StringComparison.OrdinalIgnoreCase)
            && fingerprint.Length == 0)
        {
            SetRemoteCoreStatus("tls:// 地址需要服务器启动时打印的证书指纹。");
            return false;
        }
        return true;
    }

    private static bool IsLoopbackEndpoint(string endpoint)
    {
        if (!LocalSocketConnection.TryParseTcp(endpoint, out var host, out _))
        {
            return false;
        }
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (System.Net.IPAddress.TryParse(host, out var address)
                && System.Net.IPAddress.IsLoopback(address));
    }

    private void SetRemoteCoreStatus(string status)
    {
        if (_remoteCoreStatus == status)
        {
            return;
        }
        _remoteCoreStatus = status;
        OnPropertyChanged(nameof(RemoteCoreStatus));
        OnPropertyChanged(nameof(HasRemoteCoreStatus));
    }

    /// <summary>Writes the draft to the home AGENTS.md; Core reads it on the next turn.</summary>
    public void SaveAgents()
    {
        if (_agentsDraft.Length > MaxAgentsFileCharacters)
        {
            SetAgentsStatus($"AGENTS.md 不能超过 {MaxAgentsFileCharacters / 1024} KiB。");
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_agentsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(_agentsPath, _agentsDraft);
            SetAgentsStatus("已保存。下一个回合生效。");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            SetAgentsStatus($"保存失败：{error.Message}");
        }
    }

    private void SetAgentsStatus(string status)
    {
        if (_agentsStatus == status)
        {
            return;
        }
        _agentsStatus = status;
        OnPropertyChanged(nameof(AgentsStatus));
        OnPropertyChanged(nameof(HasAgentsStatus));
    }

    private bool _isCreateProjectDialogOpen;
    private string _projectNameDraft = string.Empty;
    private string _projectSourcePath = string.Empty;

    public bool IsCreateProjectDialogOpen
    {
        get => _isCreateProjectDialogOpen;
        private set
        {
            if (SetProperty(ref _isCreateProjectDialogOpen, value))
            {
                OnPropertyChanged(nameof(IsModalDialogOpen));
            }
        }
    }

    public string ProjectNameDraft
    {
        get => _projectNameDraft;
        set
        {
            if (SetProperty(ref _projectNameDraft, value))
            {
                OnPropertyChanged(nameof(CanCreateProject));
            }
        }
    }

    public string ProjectSourcePath
    {
        get => _projectSourcePath;
        private set
        {
            if (SetProperty(ref _projectSourcePath, value))
            {
                OnPropertyChanged(nameof(CanCreateProject));
                OnPropertyChanged(nameof(HasProjectSourcePath));
            }
        }
    }

    public bool HasProjectSourcePath => !string.IsNullOrWhiteSpace(ProjectSourcePath);
    public bool CanCreateProject => !string.IsNullOrWhiteSpace(ProjectNameDraft) && HasProjectSourcePath;

    public void OpenCreateProjectDialog()
    {
        ProjectNameDraft = string.Empty;
        ProjectSourcePath = string.Empty;
        IsCreateProjectDialogOpen = true;
    }

    public void CloseCreateProjectDialog() => IsCreateProjectDialogOpen = false;

    public void SetProjectSourcePath(string path)
    {
        ProjectSourcePath = path;
        if (string.IsNullOrWhiteSpace(ProjectNameDraft))
        {
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (!string.IsNullOrWhiteSpace(name))
            {
                ProjectNameDraft = name;
            }
        }
    }

    public bool CreateProjectFromDraft()
    {
        var name = ProjectNameDraft.Trim();
        var path = ProjectSourcePath.Trim();
        if (name.Length == 0 || path.Length == 0)
        {
            return false;
        }

        if (Workspaces.Any(workspace => string.Equals(workspace.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var state = new WorkspaceState
        {
            Name = name,
            Path = path,
            IsExpanded = IsWorkMode
        };
        _state.Workspaces.Add(state);
        var item = new WorkspaceItemViewModel(state);
        Workspaces.Add(item);
        RefreshWorkspacePathLabels();
        _state.ActiveWorkspaceId = state.Id;
        foreach (var workspace in Workspaces)
        {
            workspace.IsSelected = ReferenceEquals(workspace, item);
        }

        CloseCreateProjectDialog();
        SaveState();
        return true;
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public ObservableCollection<string> SettingsModelChoices { get; } = [];
    public ObservableCollection<string> DoctorChecks { get; } = [];

    public string SettingsSelectedModel
    {
        get => _settingsSelectedModel;
        set => SetProperty(ref _settingsSelectedModel, value);
    }

    public string SettingsApiKeyDraft
    {
        get => _settingsApiKeyDraft;
        set => SetProperty(ref _settingsApiKeyDraft, value);
    }

    public bool HasSettingsStatus => !string.IsNullOrWhiteSpace(SettingsStatus);
    public bool IsSettingsStatusError => _settingsStatusIsError;
    public bool IsSettingsStatusSuccess => HasSettingsStatus && !_settingsStatusIsError;
    public string TestConnectionButtonText => IsTestingConnection ? "测试中…" : "测试连接";
    public bool IsTestingConnection => _isTestingConnection;
    public bool CanTestConnection => !IsTestingConnection;
    public string StateFilePath => _stateStore.Path;
    public string StateDirectory => Path.GetDirectoryName(_stateStore.Path) ?? string.Empty;
    public int WorkspaceCount => Workspaces.Count;
    public int SavedSessionCount => Workspaces.Sum(workspace => workspace.Sessions.Count);
    public int AutomationCount => Automations.Count;
    public bool HasAutomations => Automations.Count > 0;
    public bool ShowNoAutomations => !Automations.Any(item => item.IsVisible);
    public string AutomationSearch
    {
        get => _automationSearch;
        set
        {
            if (SetProperty(ref _automationSearch, value))
            {
                ApplyAutomationFilter();
            }
        }
    }
    public string AutomationPageSummary
    {
        get
        {
            if (Automations.Count == 0)
            {
                return "尚未创建自动化任务。";
            }
            var enabled = Automations.Count(item => item.IsEnabled);
            return $"{Automations.Count} 个任务，{enabled} 个已启用。";
        }
    }

    public bool IsAutomationDialogOpen
    {
        get => _isAutomationDialogOpen;
        private set
        {
            if (SetProperty(ref _isAutomationDialogOpen, value))
            {
                OnPropertyChanged(nameof(IsModalDialogOpen));
            }
        }
    }

    public bool IsAutomationDeleteDialogOpen
    {
        get => _isAutomationDeleteDialogOpen;
        private set
        {
            if (SetProperty(ref _isAutomationDeleteDialogOpen, value))
            {
                OnPropertyChanged(nameof(IsModalDialogOpen));
            }
        }
    }

    public string AutomationDialogTitle => _automationDialogTarget is null ? "新建自动化任务" : "编辑自动化任务";

    public string AutomationName
    {
        get => _automationName;
        set => SetProperty(ref _automationName, value);
    }

    public string AutomationPrompt
    {
        get => _automationPrompt;
        set => SetProperty(ref _automationPrompt, value);
    }

    public AutomationIntervalOption? SelectedAutomationInterval
    {
        get => _selectedAutomationInterval;
        set => SetProperty(ref _selectedAutomationInterval, value);
    }

    public string AutomationDialogStatus
    {
        get => _automationDialogStatus;
        private set
        {
            if (SetProperty(ref _automationDialogStatus, value))
            {
                OnPropertyChanged(nameof(HasAutomationDialogStatus));
            }
        }
    }

    public bool HasAutomationDialogStatus => !string.IsNullOrWhiteSpace(AutomationDialogStatus);

    public string AutomationDeletePrompt
    {
        get => _automationDeletePrompt;
        private set => SetProperty(ref _automationDeletePrompt, value);
    }

    public bool HasPlugins => Plugins.Count > 0;
    public bool ShowNoPlugins => _allPlugins.Count == 0;
    public bool ShowNoPluginMatches =>
        _pluginSearch.Trim().Length > 0 && Plugins.Count == 0 && _allPlugins.Count > 0;
    public string PluginSearch
    {
        get => _pluginSearch;
        set
        {
            if (SetProperty(ref _pluginSearch, value))
            {
                ApplyPluginFilter();
            }
        }
    }
    public string PluginDirectoryPath => _pluginCatalogService.DirectoryPath;
    public string PluginStatus
    {
        get => _pluginStatus;
        private set => SetProperty(ref _pluginStatus, value);
    }
    public bool IsWorkMode => _state.InteractionMode != "chat";
    public bool IsChatMode => !IsWorkMode;

    /// <summary>The desktop app's home composer is narrower than in a conversation.</summary>
    public double ComposerMaxWidth => IsChatMode && IsEmpty ? 640 : 734;

    /// <summary>The desktop home floats the composer under the headline instead of pinning it to the bottom.</summary>
    public Thickness ComposerMargin => IsChatMode && IsEmpty
        ? new Thickness(1, 0, 0, 326)
        : new Thickness(1, 0, 0, 20);

    /// <summary>Codex Work keeps the workspace strip above the composer.</summary>
    public bool ShowWorkspaceStrip => IsEmpty && IsWorkMode;

    /// <summary>Chat mode puts project/plugin chips below the composer instead.</summary>
    public bool ShowChatComposerChips => IsEmpty && IsChatMode;
    public bool CanSwitchSession => !IsBusy;
    public bool HasSessions => Workspaces.Any(workspace => workspace.Sessions.Count > 0);
    public bool ShowNoSessions => !HasSessions && string.IsNullOrWhiteSpace(SearchText);
    public bool ShowNoSearchResults => !string.IsNullOrWhiteSpace(SearchText) && !Workspaces.Any(workspace => workspace.IsVisible);
    public string ProductLabel => "kl";

    public string ActiveModeLabel => IsWorkMode ? "Work" : "Chat";
    public string SessionTitle
    {
        get => _sessionTitle;
        private set
        {
            if (SetProperty(ref _sessionTitle, value))
            {
                OnPropertyChanged(nameof(HeaderTitle));
            }
        }
    }
    public string HeaderTitle => IsChatPage ? SessionTitle : SectionTitle;
    public string SectionTitle => IsLibraryPage ? "资料库" : IsProjectsPage ? "项目" : IsPluginsPage ? (IsSkillsList ? "技能" : "插件") : IsAutomationPage ? "自动化任务" : IsSettingsPage ? "设置" : "聊天";
    public string WorkspaceName => _activeWorkspace.Name;
    public string WorkspacePath => _activeWorkspace.Path;
    public string ModelName => _core.IsSessionOpen
        ? _core.Model
        : string.IsNullOrWhiteSpace(CoreCatalogModel) ? _state.Model : CoreCatalogModel;

    /// <summary>Codex-style provider/model label for the composer picker.</summary>
    public string ProviderModelLabel
    {
        get
        {
            var provider = _core.IsSessionOpen ? _core.Provider : CoreCatalogProvider;
            var model = _core.IsSessionOpen ? _core.Model : CoreCatalogModel;
            if (string.IsNullOrWhiteSpace(model))
            {
                return string.IsNullOrWhiteSpace(_state.Model) ? "未连接" : _state.Model;
            }

            if (string.IsNullOrWhiteSpace(provider))
            {
                return PrettifyWords(model);
            }

            var normalizedProvider = PrettifyWords(provider);
            return model.StartsWith(provider, StringComparison.OrdinalIgnoreCase)
                ? PrettifyWords(model)
                : $"{normalizedProvider} {PrettifyWords(model)}";
        }
    }

    private static string PrettifyWords(string value)
    {
        var parts = value
            .Replace('_', ' ')
            .Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return value;
        }

        var builder = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }
            builder.Append(char.ToUpperInvariant(part[0]));
            if (part.Length > 1)
            {
                builder.Append(part[1..]);
            }
        }
        return builder.ToString();
    }
    public string PermissionMode => _state.PermissionMode;
    public int ContentFontSize => _state.ContentFontSize;

    /// <summary>Raised for a finished turn or a pending approval; the window decides about focus.</summary>
    public event Action<AttentionKind, bool>? AttentionRequested;

    internal void RequestAttention(AttentionKind kind) =>
        AttentionRequested?.Invoke(kind, _state.NotificationSound);

    public bool SettingsFullAccess
    {
        get => IsPermissionFull;
        set
        {
            if (value == IsPermissionFull)
            {
                return;
            }

            SetPermissionMode(value ? "完全访问" : "帮我批准");
            OnPropertyChanged();
        }
    }

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
        _capturingShortcut is null ? string.Empty : $"为“{_capturingShortcut.Title}”录制快捷键";

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
            ("newChat", "新聊天", "开始新聊天", "Ctrl+N"),
            ("archiveChat", "归档聊天", "归档当前聊天", "Ctrl+Shift+A"),
            ("copyLastCodeBlock", "复制最后一个代码块", "复制当前聊天中助手提供的最后一个代码块", "Ctrl+Shift+;"),
            ("markUnread", "标记为未读", "将当前聊天标记为未读", "Ctrl+Shift+U"),
            ("togglePin", "切换置顶状态", "置顶或取消置顶当前聊天", "Ctrl+Alt+P"),
            ("searchSessions", "搜索会话", "在侧边栏中搜索聊天", "Ctrl+F"),
            ("openSettings", "打开设置", "打开设置页面", "Ctrl+,"),
            ("toggleTerminal", "切换终端", "显示或隐藏集成终端面板", "Ctrl+`"),
            ("closeWindow", "关闭窗口", "关闭当前窗口", "Ctrl+Shift+W")
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
            ShortcutCaptureStatus = $"{gesture} 已用于“{duplicate.Title}”。";
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
                LaunchAtLoginStatus = value ? "已加入开机启动。" : "已取消开机启动。";
                SaveState();
            }
            catch (Exception error) when (error is InvalidOperationException
                or PlatformNotSupportedException
                or System.ComponentModel.Win32Exception)
            {
                LaunchAtLoginStatus = $"无法更新开机启动：{error.Message}";
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

    public string RemoteCoreConnectionLabel => string.IsNullOrWhiteSpace(_state.RemoteCoreEndpoint)
        ? "本机 Core（未配置远程）"
        : _state.RemoteCoreEndpoint.Trim();

    public string CredentialsDirectory => Path.Combine(_homeDirectory, "credentials");
    public string HooksFilePath => Path.Combine(_homeDirectory, "hooks.toml");

    private string _gitBranchText = "—";
    private string _gitStatusText = "打开此页时读取当前项目的 Git 状态。";
    public string GitBranchText
    {
        get => _gitBranchText;
        private set => SetProperty(ref _gitBranchText, value);
    }

    public string GitStatusText
    {
        get => _gitStatusText;
        private set => SetProperty(ref _gitStatusText, value);
    }

    /// <summary>Reads the active workspace's branch and dirty count with the git CLI.</summary>
    public async Task RefreshGitAsync()
    {
        var workspace = WorkspacePath;
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            GitBranchText = "—";
            GitStatusText = "当前项目路径不可用。";
            return;
        }

        var (isRepo, branch, status, log) = await Task.Run(() =>
        {
            var branch = RunGit(workspace, "symbolic-ref --short HEAD")
                ?? RunGit(workspace, "rev-parse --short HEAD");
            if (branch is null)
            {
                return (false, string.Empty, "当前项目不是 Git 仓库。", string.Empty);
            }

            var changes = RunGit(workspace, "status --porcelain");
            var count = string.IsNullOrWhiteSpace(changes)
                ? 0
                : changes.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            var log = RunGit(workspace, "log -1 --pretty=%h %s") ?? string.Empty;
            return (true, branch.Trim(), count == 0 ? "工作区干净" : $"{count} 个未提交更改", log.Trim());
        }).ConfigureAwait(true);

        GitBranchText = isRepo ? branch : "—";
        GitStatusText = isRepo
            ? $"{status}" + (log.Length > 0 ? $" · {log}" : string.Empty)
            : status;
    }

    public ObservableCollection<WorktreeRow> Worktrees { get; } = [];
    public bool HasWorktrees => Worktrees.Count > 0;
    public string WorktreesStatusText { get; private set; } = "打开此页时读取当前项目的 worktree。";

    /// <summary>Lists real git worktrees for the active project.</summary>
    public async Task RefreshWorktreesAsync()
    {
        var workspace = WorkspacePath;
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            Worktrees.Clear();
            WorktreesStatusText = "当前项目路径不可用。";
            NotifyWorktrees();
            return;
        }

        var output = await Task.Run(() => RunGit(workspace, "worktree list --porcelain")).ConfigureAwait(true);
        Worktrees.Clear();
        if (output is null)
        {
            WorktreesStatusText = "当前项目不是 Git 仓库。";
        }
        else
        {
            foreach (var row in ParseWorktrees(output, workspace))
            {
                Worktrees.Add(row);
            }
            WorktreesStatusText = Worktrees.Count == 0 ? "Git 没有报告 worktree。" : $"共 {Worktrees.Count} 个工作树";
        }
        NotifyWorktrees();
    }

    private void NotifyWorktrees()
    {
        OnPropertyChanged(nameof(HasWorktrees));
        OnPropertyChanged(nameof(WorktreesStatusText));
    }

    public string WorktreeRoot
    {
        get => _state.WorktreeRoot;
        set
        {
            var root = value?.Trim() ?? string.Empty;
            if (_state.WorktreeRoot == root)
            {
                return;
            }
            _state.WorktreeRoot = root;
            SaveState();
            OnPropertyChanged();
        }
    }

    public string GitBranchPrefix
    {
        get => _state.GitBranchPrefix;
        set
        {
            var prefix = value?.Trim() ?? string.Empty;
            if (_state.GitBranchPrefix == prefix)
            {
                return;
            }
            _state.GitBranchPrefix = prefix;
            SaveState();
            OnPropertyChanged();
        }
    }

    /// <summary>Shown as the placeholder when the root is left blank.</summary>
    public string DefaultWorktreeRoot => string.IsNullOrWhiteSpace(WorkspacePath)
        ? ".kl/worktrees"
        : Path.Combine(WorkspacePath, ".kl", "worktrees");

    public bool WorktreeFetchBeforeCreate
    {
        get => _state.WorktreeFetchBeforeCreate;
        set
        {
            if (_state.WorktreeFetchBeforeCreate == value)
            {
                return;
            }
            _state.WorktreeFetchBeforeCreate = value;
            SaveState();
            OnPropertyChanged();
        }
    }

    public bool WorktreeConfirmDelete
    {
        get => _state.WorktreeConfirmDelete;
        set
        {
            if (_state.WorktreeConfirmDelete == value)
            {
                return;
            }
            _state.WorktreeConfirmDelete = value;
            SaveState();
            OnPropertyChanged();
        }
    }

    private string _newWorktreeName = string.Empty;
    public string NewWorktreeName
    {
        get => _newWorktreeName;
        set
        {
            if (SetProperty(ref _newWorktreeName, value))
            {
                OnPropertyChanged(nameof(CanCreateWorktree));
            }
        }
    }

    public bool CanCreateWorktree => IsValidWorktreeName(_newWorktreeName.Trim());

    private static string ExpandHome(string path)
    {
        var trimmed = path.Trim();
        if (trimmed == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        if (trimmed.StartsWith("~/", StringComparison.Ordinal)
            || trimmed.StartsWith("~\\", StringComparison.Ordinal))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                trimmed[2..]);
        }
        return Environment.ExpandEnvironmentVariables(trimmed);
    }

    private static bool IsValidWorktreeName(string name) =>
        name.Length > 0
        && name != "."
        && name != ".."
        && name.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool IsValidBranchPrefix(string prefix) =>
        prefix.Length <= 64
        && !prefix.Contains("..", StringComparison.Ordinal)
        && !prefix.StartsWith('/')
        && !prefix.StartsWith('.')
        && !prefix.StartsWith('-')
        && prefix.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '/' or '-' or '_' or '.');

    public async Task CreateWorktreeAsync()
    {
        var workspace = WorkspacePath;
        var name = _newWorktreeName.Trim();
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            WorktreesStatusText = "当前项目路径不可用。";
            NotifyWorktrees();
            return;
        }
        if (!IsValidWorktreeName(name))
        {
            WorktreesStatusText = "工作树名称只能包含字母、数字、点、下划线和短横线。";
            NotifyWorktrees();
            return;
        }

        var branchPrefix = _state.GitBranchPrefix.Trim();
        if (!IsValidBranchPrefix(branchPrefix))
        {
            WorktreesStatusText = "分支前缀包含非法字符，请检查 Git 设置。";
            NotifyWorktrees();
            return;
        }
        var branch = branchPrefix + name;

        var root = string.IsNullOrWhiteSpace(_state.WorktreeRoot)
            ? Path.Combine(workspace, ".kl", "worktrees")
            : ExpandHome(_state.WorktreeRoot);
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (Directory.Exists(path) || File.Exists(path))
        {
            WorktreesStatusText = "目标工作树已存在。";
            NotifyWorktrees();
            return;
        }

        WorktreesStatusText = "正在创建工作树…";
        NotifyWorktrees();
        var fetch = _state.WorktreeFetchBeforeCreate;
        var (ok, output) = await Task.Run(() =>
        {
            if (fetch)
            {
                var fetched = RunGitArgs(workspace, "fetch", "--all", "--prune");
                if (!fetched.Ok)
                {
                    return fetched;
                }
            }
            try
            {
                Directory.CreateDirectory(root);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return (false, error.Message);
            }
            return RunGitArgs(workspace, "worktree", "add", "-b", branch, path, "HEAD");
        }).ConfigureAwait(true);

        if (ok)
        {
            _newWorktreeName = string.Empty;
            OnPropertyChanged(nameof(NewWorktreeName));
            OnPropertyChanged(nameof(CanCreateWorktree));
            await RefreshWorktreesAsync().ConfigureAwait(true);
            WorktreesStatusText = $"已创建工作树 {name}（分支 {branch}）。";
        }
        else
        {
            WorktreesStatusText = $"创建工作树失败：{output.Trim()}";
        }
        NotifyWorktrees();
    }

    private WorktreeRow? _pendingWorktreeDelete;
    public bool IsWorktreeDeleteConfirmOpen => _pendingWorktreeDelete is not null;
    public string WorktreeDeleteConfirmText => _pendingWorktreeDelete is null
        ? string.Empty
        : $"删除工作树 {_pendingWorktreeDelete.Path}？";

    public void RequestDeleteWorktree(WorktreeRow row)
    {
        if (!_state.WorktreeConfirmDelete)
        {
            _ = DeleteWorktreeAsync(row);
            return;
        }
        _pendingWorktreeDelete = row;
        OnPropertyChanged(nameof(IsWorktreeDeleteConfirmOpen));
        OnPropertyChanged(nameof(WorktreeDeleteConfirmText));
        OnPropertyChanged(nameof(IsModalDialogOpen));
    }

    public void CancelDeleteWorktree()
    {
        _pendingWorktreeDelete = null;
        OnPropertyChanged(nameof(IsWorktreeDeleteConfirmOpen));
        OnPropertyChanged(nameof(WorktreeDeleteConfirmText));
        OnPropertyChanged(nameof(IsModalDialogOpen));
    }

    public async Task ConfirmDeleteWorktreeAsync()
    {
        var row = _pendingWorktreeDelete;
        CancelDeleteWorktree();
        if (row is not null)
        {
            await DeleteWorktreeAsync(row).ConfigureAwait(true);
        }
    }

    private async Task DeleteWorktreeAsync(WorktreeRow row)
    {
        var workspace = WorkspacePath;
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            WorktreesStatusText = "当前项目路径不可用。";
            NotifyWorktrees();
            return;
        }

        WorktreesStatusText = "正在删除工作树…";
        NotifyWorktrees();
        var (ok, output) = await Task.Run(() => RunGitArgs(workspace, "worktree", "remove", row.Path))
            .ConfigureAwait(true);
        if (ok)
        {
            await RefreshWorktreesAsync().ConfigureAwait(true);
            WorktreesStatusText = "已删除工作树。";
        }
        else
        {
            WorktreesStatusText = $"删除工作树失败：{output.Trim()}";
        }
        NotifyWorktrees();
    }

    private static IEnumerable<WorktreeRow> ParseWorktrees(string output, string currentWorkspace)
    {
        var path = string.Empty;
        var branch = string.Empty;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                if (path.Length > 0)
                {
                    yield return BuildWorktreeRow(path, branch, currentWorkspace);
                }
                path = string.Empty;
                branch = string.Empty;
                continue;
            }

            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                path = line["worktree ".Length..];
            }
            else if (line.StartsWith("branch refs/heads/", StringComparison.Ordinal))
            {
                branch = line["branch refs/heads/".Length..];
            }
            else if (line == "detached")
            {
                branch = "detached";
            }
        }

        if (path.Length > 0)
        {
            yield return BuildWorktreeRow(path, branch, currentWorkspace);
        }
    }

    private static WorktreeRow BuildWorktreeRow(string path, string branch, string currentWorkspace) =>
        new(path,
            branch.Length == 0 ? "—" : branch,
            string.Equals(
                Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(currentWorkspace).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)
                ? "当前"
                : string.Empty);

    private static (bool Ok, string Output) RunGitArgs(string workspace, params string[] arguments)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("-C");
            start.ArgumentList.Add(workspace);
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using var process = System.Diagnostics.Process.Start(start);
            if (process is null)
            {
                return (false, "无法启动 git。");
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(15000);
            return process.ExitCode == 0
                ? (true, stdout)
                : (false, string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception
            or InvalidOperationException
            or IOException)
        {
            return (false, error.Message);
        }
    }

    private static string? RunGit(string workspace, string arguments)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"-C \"{workspace}\" {arguments}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private string _hooksStatusText = "打开此页时读取 hooks.toml。";
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
                HooksStatusText = $"未找到 hooks.toml（{HooksFilePath}）";
                return;
            }

            var lines = File.ReadLines(HooksFilePath).Count(line => !string.IsNullOrWhiteSpace(line));
            HooksStatusText = $"已配置 {lines} 行钩子；修改后重启 KL Core 生效。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            HooksStatusText = $"无法读取 hooks.toml：{error.Message}";
        }
    }
    public string RemoteTokenStatusText { get; private set; } = "未保存";

    /// <summary>Re-reads which credential files exist for the 密码 pane.</summary>
    public void RefreshCredentials()
    {
        RemoteTokenStatusText = File.Exists(RemoteTokenPath) ? "已保存" : "未保存";
        OnPropertyChanged(nameof(RemoteTokenStatusText));
    }

    public string TurnNotificationMode => _state.TurnNotifications;

    public int TurnNotificationIndex
    {
        get => _state.TurnNotifications switch
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
                _ => "unfocused"
            };
            if (_state.TurnNotifications == mode)
            {
                return;
            }

            _state.TurnNotifications = mode;
            SaveState();
            OnPropertyChanged();
            OnPropertyChanged(nameof(TurnNotificationMode));
        }
    }

    private static readonly string[] AccentColors =
    [
        "#4D9FFF", "#3566F0", "#179E8A", "#FFCC38", "#FA70AB", "#FF8066", "#A25BFF", "#FFFFFF"
    ];

    private bool _isLightTheme;

    public string ThemeMode => _state.ThemeMode;

    public bool IsThemeSystem => _state.ThemeMode == "system";
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
        OnPropertyChanged(nameof(IsThemeSystem));
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

    public string ReduceMotionMode => _state.ReduceMotion;

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
        OnPropertyChanged(nameof(ReduceMotionMode));
        OnPropertyChanged(nameof(IsReduceMotionSystem));
        OnPropertyChanged(nameof(IsReduceMotionOn));
        OnPropertyChanged(nameof(IsReduceMotionOff));
        OnPropertyChanged(nameof(ReduceMotion));
    }

    public bool IsFollowUpQueue => _state.FollowUpMode != "steer";
    public bool IsFollowUpSteer => _state.FollowUpMode == "steer";

    public void SetFollowUpMode(string mode)
    {
        var value = mode == "steer" ? "steer" : "queue";
        if (_state.FollowUpMode == value)
        {
            return;
        }

        _state.FollowUpMode = value;
        SaveState();
        OnPropertyChanged(nameof(IsFollowUpQueue));
        OnPropertyChanged(nameof(IsFollowUpSteer));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(CanQueueSend));
    }

    private readonly Queue<QueuedTurn> _queuedTurns = new();
    private bool _sendingQueuedTurn;

    public int QueuedTurnCount => _queuedTurns.Count;
    public bool HasQueuedTurns => _queuedTurns.Count > 0;

    private void NotifyQueueChanged()
    {
        OnPropertyChanged(nameof(QueuedTurnCount));
        OnPropertyChanged(nameof(HasQueuedTurns));
        OnPropertyChanged(nameof(ComposerPlaceholder));
        OnPropertyChanged(nameof(CanSend));
    }

    private void QueueTurn(string prompt, IReadOnlyList<AttachmentItemViewModel> attachments)
    {
        _queuedTurns.Enqueue(new QueuedTurn(prompt, attachments.ToList()));
        Composer = string.Empty;
        ClearAttachments();
        NotifyQueueChanged();
    }

    /// <summary>
    /// Sends queued prompts one by one. The nested SendAsync call returns here
    /// because this loop already owns the drain; that is what keeps messages
    /// queued during a later turn from being stranded.
    /// </summary>
    private async Task DrainQueuedTurnsAsync()
    {
        if (_sendingQueuedTurn)
        {
            return;
        }

        _sendingQueuedTurn = true;
        try
        {
            while (!IsBusy && _queuedTurns.Count > 0)
            {
                var next = _queuedTurns.Dequeue();
                NotifyQueueChanged();
                Composer = next.Prompt;
                foreach (var attachment in next.Attachments)
                {
                    if (!PendingAttachments.Contains(attachment))
                    {
                        PendingAttachments.Add(attachment);
                    }
                }
                OnPropertyChanged(nameof(HasPendingAttachments));
                await SendAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            _sendingQueuedTurn = false;
            NotifyQueueChanged();
        }
    }

    private void ClearQueuedTurns()
    {
        if (_queuedTurns.Count == 0)
        {
            return;
        }

        _queuedTurns.Clear();
        NotifyQueueChanged();
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

    public string Composer
    {
        get => _composer;
        set
        {
            if (SetProperty(ref _composer, value))
            {
                OnPropertyChanged(nameof(CanSend));
                OnPropertyChanged(nameof(CanQueueSend));
            }
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplySearch();
                OnPropertyChanged(nameof(ShowNoSessions));
                RefreshPaletteSessions();
                RefreshPaletteFileSearch();
            }
        }
    }

    private ChatItemViewModel? _editingUserItem;
    private string? _rewindModelText;
    private ChatItemViewModel? _pendingUserItem;
    private bool _suppressRewindReload;

    public bool IsEditingUserMessage => _editingUserItem is not null;

    public string EditMessageBanner =>
        "正在编辑消息：发送后会替换这条消息及其后的回复。工具的改动不会被撤销。";

    private void RaiseEditState()
    {
        OnPropertyChanged(nameof(IsEditingUserMessage));
        OnPropertyChanged(nameof(EditMessageBanner));
    }

    public void CancelEditUserMessage()
    {
        if (_editingUserItem is null)
        {
            return;
        }
        _editingUserItem = null;
        _rewindModelText = null;
        RaiseEditState();
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanSend));
                OnPropertyChanged(nameof(CanQueueSend));
                OnPropertyChanged(nameof(CanSwitchSession));
                OnPropertyChanged(nameof(CanCancel));
            }
        }
    }

    public bool CanSend => (!IsBusy || IsFollowUpQueue)
        && !IsCoreReadOnly
        && !IsCoreRetrying
        && (!string.IsNullOrWhiteSpace(Composer) || HasPendingAttachments);
    public bool CanQueueSend => IsBusy
        && IsFollowUpQueue
        && !IsCoreReadOnly
        && !IsCoreRetrying
        && (!string.IsNullOrWhiteSpace(Composer) || HasPendingAttachments);
    public bool HasPendingAttachments => PendingAttachments.Count > 0;
    public bool CanCancel => IsBusy;

    public string ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public string SettingsPermissionMode
    {
        get => _settingsPermissionMode;
        set => SetProperty(ref _settingsPermissionMode, value);
    }

    public int SettingsContentFontSize
    {
        get => _settingsContentFontSize;
        set => SetProperty(ref _settingsContentFontSize, value);
    }

    public string SettingsStatus
    {
        get => _settingsStatus;
        private set
        {
            if (SetProperty(ref _settingsStatus, value))
            {
                OnPropertyChanged(nameof(HasSettingsStatus));
            }
        }
    }

    public string RenameTitle
    {
        get => _renameTitle;
        set => SetProperty(ref _renameTitle, value);
    }

    public string DeletePrompt
    {
        get => _deletePrompt;
        private set => SetProperty(ref _deletePrompt, value);
    }

    public string RenameDialogTitle => _renameWorkspaceTarget is null ? "重命名会话" : "编辑项目";

    public bool IsEditingWorkspace => _renameWorkspaceTarget is not null;
    public string RenameSourcePath => _renameWorkspaceTarget?.Path ?? string.Empty;
    public string RenameSourceParentPath => _renameWorkspaceTarget?.ParentPath ?? string.Empty;

    public void RemoveWorkspaceFromDialog()
    {
        if (_renameWorkspaceTarget is not { } workspace)
        {
            return;
        }

        CloseDialogs();
        RemoveWorkspace(workspace);
    }

    public void SetWorkspaceSourcePath(string path)
    {
        if (_renameWorkspaceTarget is not { } workspace || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        workspace.State.Path = path;
        RefreshWorkspacePathLabels();
        OnPropertyChanged(nameof(RenameSourcePath));
        OnPropertyChanged(nameof(RenameSourceParentPath));
        SaveState();
    }

    public void Navigate(AppSection section)
    {
        if (section == AppSection.Settings)
        {
            OpenSettings();
            return;
        }

        // Re-clicking the section already on screen would replay the page fade
        // and reset list state; keep it still.
        var sameSection = !IsSettingsOpen && section switch
        {
            AppSection.Chat => _isChatPage,
            AppSection.Library => _isLibraryPage,
            AppSection.Projects => _isProjectsPage,
            AppSection.Plugins => _isPluginsPage,
            AppSection.Automation => _isAutomationPage,
            _ => false
        };
        if (sameSection)
        {
            return;
        }

        _isChatPage = section == AppSection.Chat;
        _isLibraryPage = section == AppSection.Library;
        _isCodeReviewDraftActive = false;
        _isProjectsPage = section == AppSection.Projects;
        _isPluginsPage = section == AppSection.Plugins;
        _isAutomationPage = section == AppSection.Automation;
        _isSettingsPage = false;
        IsSettingsOpen = false;
        OnPropertyChanged(nameof(IsChatPage));
        OnPropertyChanged(nameof(IsLibraryPage));
        OnPropertyChanged(nameof(IsProjectsPage));
        OnPropertyChanged(nameof(IsPluginsPage));
        OnPropertyChanged(nameof(IsPluginsList));
        OnPropertyChanged(nameof(IsSkillsList));
        OnPropertyChanged(nameof(IsAutomationPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(ShowConversationHeader));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(SectionTitle));
        UpdatePinnedRailActiveState();
        if (section == AppSection.Plugins)
        {
            if (_isSkillsList)
            {
                _isSkillsList = false;
                OnPropertyChanged(nameof(IsSkillsList));
                OnPropertyChanged(nameof(IsPluginsList));
                OnPropertyChanged(nameof(SectionTitle));
            }
            RefreshPlugins();
            _ = RefreshPluginsAsync();
        }
        if (section == AppSection.Automation)
        {
            foreach (var automation in Automations)
            {
                automation.NotifyStateChanged();
            }
            ApplyAutomationFilter();
        }
    }

    public void ToggleAddProjectPinned()
    {
        _state.IsAddProjectPinned = !_state.IsAddProjectPinned;
        SaveState();
        OnPropertyChanged(nameof(IsAddProjectPinned));
        RefreshPinnedRailItems();
    }

    /// <summary>Codex-style pinned shortcuts below the rail divider.</summary>
    public void RefreshPinnedRailItems()
    {
        var desired = new List<PinnedRailItemViewModel>(2);
        if (_state.IsAddProjectPinned)
        {
            desired.Add(new PinnedRailItemViewModel(isProject: true, label: "项目")
            {
                IsActive = IsProjectsPage
            });
        }

        if (_state.IsCodeReviewPinned)
        {
            desired.Add(new PinnedRailItemViewModel(isProject: false, label: "代码审查")
            {
                IsActive = _isCodeReviewDraftActive
            });
        }

        if (PinnedRailItems.Count == desired.Count
            && PinnedRailItems.Zip(desired).All(pair => pair.First.IsProject == pair.Second.IsProject))
        {
            UpdatePinnedRailActiveState();
            return;
        }

        PinnedRailItems.Clear();
        foreach (var item in desired)
        {
            PinnedRailItems.Add(item);
        }
    }

    /// <summary>Keeps the pinned rail icons highlighted for the current section.</summary>
    public void UpdatePinnedRailActiveState()
    {
        foreach (var item in PinnedRailItems)
        {
            item.IsActive = item.IsProject ? IsProjectsPage : _isCodeReviewDraftActive;
        }
    }

    public void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    public void ToggleSearch() => IsSearchVisible = !IsSearchVisible;

    // ---- Codex-style notifications pane and command palette ----
    private bool _isNotificationsVisible;
    private bool _isSearchPaletteOpen;

    public bool IsNotificationsVisible
    {
        get => _isNotificationsVisible;
        private set => SetProperty(ref _isNotificationsVisible, value);
    }

    public bool IsSearchPaletteOpen
    {
        get => _isSearchPaletteOpen;
        private set => SetProperty(ref _isSearchPaletteOpen, value);
    }

    public ObservableCollection<NotificationItemViewModel> Notifications { get; } = [];
    public ObservableCollection<SessionItemViewModel> PaletteSessions { get; } = [];
    public ObservableCollection<PaletteFileGroupViewModel> PaletteFiles { get; } = [];
    public ObservableCollection<PaletteFileGroupViewModel> PaletteTextMatches { get; } = [];
    public bool HasNotifications => Notifications.Count > 0;
    public bool ShowNoNotifications => Notifications.Count == 0;

    private string _paletteFilter = "all";
    private string _reasoningEffort = "default";
    private string? _reasoningEffortPreview;
    private bool _webSearch = true;
    private bool _networkAccess = true;
    private bool _memoryEnabled = true;
    private bool _pluginsEnabled = true;
    private string _outputVerbosity = "default";
    private string _reasoningSummary = "auto";
    private string _personalStyle = "default";
    private string _warmth = "default";
    private string _enthusiasm = "default";
    private string _emojiUsage = "default";
    private string _visualAnswers = "default";
    private string _headingStyle = "default";
    private bool _quickAnswers = true;
    private string _customInstructionsDraft = string.Empty;
    private bool _loadingAgentSettings;
    private CancellationTokenSource? _paletteSearchCancellation;

    public bool IsPaletteFilterAll => _paletteFilter == "all";
    public bool IsPaletteFilterActions => _paletteFilter == "actions";
    public bool IsPaletteFilterChats => _paletteFilter == "chats";
    public bool IsPaletteFilterFiles => _paletteFilter == "files";
    public bool IsPaletteFilterText => _paletteFilter == "text";
    public bool ShowPaletteActions => _paletteFilter is "all" or "actions";
    public bool ShowPaletteChats => _paletteFilter is "all" or "chats";
    public bool ShowPaletteFiles => _paletteFilter is "all" or "files" && PaletteFiles.Count > 0;
    public bool ShowPaletteText =>
        _paletteFilter is "all" or "text" && PaletteTextMatches.Count > 0;
    public bool ShowPaletteEmpty => _paletteFilter switch
    {
        "files" => PaletteFiles.Count == 0,
        "text" => PaletteTextMatches.Count == 0,
        "actions" => false,
        "chats" => PaletteSessions.Count == 0,
        _ => PaletteSessions.Count == 0 && PaletteFiles.Count == 0 && PaletteTextMatches.Count == 0
    };

    public string PaletteEmptyText => _paletteFilter switch
    {
        "files" => SearchText.Trim().Length < 2 ? "输入至少 2 个字符搜索文件" : "没有匹配的文件",
        "text" => SearchText.Trim().Length < 2 ? "输入至少 2 个字符搜索文本" : "没有匹配的文本",
        "actions" => string.Empty,
        _ => "没有匹配项"
    };

    public void SetPaletteFilter(string filter)
    {
        var value = filter switch
        {
            "actions" => "actions",
            "chats" => "chats",
            "files" => "files",
            "text" => "text",
            _ => "all"
        };
        if (_paletteFilter == value)
        {
            return;
        }

        _paletteFilter = value;
        OnPropertyChanged(nameof(IsPaletteFilterAll));
        OnPropertyChanged(nameof(IsPaletteFilterActions));
        OnPropertyChanged(nameof(IsPaletteFilterChats));
        OnPropertyChanged(nameof(IsPaletteFilterFiles));
        OnPropertyChanged(nameof(IsPaletteFilterText));
        OnPropertyChanged(nameof(ShowPaletteActions));
        OnPropertyChanged(nameof(ShowPaletteChats));
        OnPropertyChanged(nameof(ShowPaletteFiles));
        OnPropertyChanged(nameof(ShowPaletteText));
        OnPropertyChanged(nameof(ShowPaletteEmpty));
        OnPropertyChanged(nameof(PaletteEmptyText));
        RefreshPaletteSessions();
    }

    public void ToggleNotifications()
    {
        if (!IsNotificationsVisible)
        {
            IsSearchPaletteOpen = false;
            RefreshNotifications();
        }

        IsNotificationsVisible = !IsNotificationsVisible;
    }

    public void OpenSearchPalette()
    {
        IsNotificationsVisible = false;
        SearchText = string.Empty;
        SetPaletteFilter("all");
        _historyPromptsLoaded = false;
        RefreshPaletteSessions();
        IsSearchPaletteOpen = true;
    }

    public void CloseSearchPalette()
    {
        IsSearchPaletteOpen = false;
        _paletteSearchCancellation?.Cancel();
        _paletteSearchCancellation = null;
        if (PaletteFiles.Count > 0 || PaletteTextMatches.Count > 0)
        {
            PaletteFiles.Clear();
            PaletteTextMatches.Clear();
            RaisePaletteSearchVisibility();
        }
    }

    private string _notificationFilter = "priority";

    public bool IsNotificationFilterPriority => _notificationFilter == "priority";
    public bool IsNotificationFilterPinned => _notificationFilter == "pinned";
    public bool IsNotificationFilterAutomations => _notificationFilter == "automations";

    public void SetNotificationFilter(string filter)
    {
        _notificationFilter = filter is "priority" or "pinned" or "automations" ? filter : "priority";
        _state.NotificationFilter = _notificationFilter;
        SaveState();
        OnPropertyChanged(nameof(IsNotificationFilterPriority));
        OnPropertyChanged(nameof(IsNotificationFilterPinned));
        OnPropertyChanged(nameof(IsNotificationFilterAutomations));
        RefreshNotifications();
    }

    public void MarkAllNotificationsRead()
    {
        Notifications.Clear();
        OnPropertyChanged(nameof(HasNotifications));
        OnPropertyChanged(nameof(ShowNoNotifications));
    }

    public void ArchiveNotificationSessions()
    {
        foreach (var notification in Notifications.ToList())
        {
            notification.Session.IsArchived = true;
        }

        RefreshNotifications();
        RefreshRecentSessions();
        SaveState();
    }

    public void RefreshNotifications()
    {
        Notifications.Clear();
        var today = DateTimeOffset.Now.Date;
        var sessions = Workspaces
            .SelectMany(workspace => workspace.Sessions)
            .Where(session => !session.IsArchived)
            .Where(session => _notificationFilter switch
            {
                "pinned" => session.IsPinned,
                "automations" => false,
                _ => true
            })
            .OrderByDescending(session => session.State.UpdatedAt)
            .Take(12)
            .ToList();
        string? lastGroup = null;
        foreach (var session in sessions)
        {
            var date = session.State.UpdatedAt.ToLocalTime().Date;
            var group = date == today
                ? "今天"
                : date == today.AddDays(-1) ? "昨天" : date.ToString("MM-dd");
            Notifications.Add(new NotificationItemViewModel(session, group, group != lastGroup));
            lastGroup = group;
        }

        OnPropertyChanged(nameof(HasNotifications));
        OnPropertyChanged(nameof(ShowNoNotifications));
    }

    /// <summary>
    /// Debounced file-name and file-content search for the palette. Runs off the
    /// UI thread and only while the palette is open.
    /// </summary>
    public void RefreshPaletteFileSearch()
    {
        var previous = _paletteSearchCancellation;
        _paletteSearchCancellation = null;
        previous?.Cancel();
        previous?.Dispose();

        if (PaletteFiles.Count > 0 || PaletteTextMatches.Count > 0)
        {
            PaletteFiles.Clear();
            PaletteTextMatches.Clear();
        }

        RaisePaletteSearchVisibility();

        var query = SearchText.Trim();
        if (!IsSearchPaletteOpen || query.Length < 2)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _paletteSearchCancellation = cancellation;
        var workspace = WorkspacePath;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(220, cancellation.Token).ConfigureAwait(false);
                var files = WorkspaceFileIndex.Files(workspace);
                var nameMatches = WorkspaceFileIndex.MatchPaths(files, query, 12);
                var textMatches = WorkspaceFileIndex.SearchContents(workspace, files, query, 6, 3);
                if (cancellation.IsCancellationRequested)
                {
                    return;
                }

                _dispatch(() =>
                {
                    if (cancellation.IsCancellationRequested
                        || !ReferenceEquals(_paletteSearchCancellation, cancellation))
                    {
                        return;
                    }

                    PaletteFiles.Clear();
                    foreach (var path in nameMatches)
                    {
                        PaletteFiles.Add(new PaletteFileGroupViewModel(
                            path,
                            Array.Empty<PaletteLineMatch>(),
                            query));
                    }

                    PaletteTextMatches.Clear();
                    foreach (var (relativePath, lines) in textMatches)
                    {
                        PaletteTextMatches.Add(new PaletteFileGroupViewModel(relativePath, lines, query));
                    }

                    RaisePaletteSearchVisibility();
                });
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer query.
            }
        });
    }

    private void RaisePaletteSearchVisibility()
    {
        OnPropertyChanged(nameof(ShowPaletteFiles));
        OnPropertyChanged(nameof(ShowPaletteText));
        OnPropertyChanged(nameof(ShowPaletteEmpty));
        OnPropertyChanged(nameof(PaletteEmptyText));
    }

    public void RefreshPaletteSessions()
    {
        var query = SearchText.Trim();
        HashSet<string>? historyMatches = null;
        if (query.Length > 0)
        {
            LoadHistoryPrompts();
            historyMatches = _historyPrompts
                .Where(entry => entry.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .Select(entry => entry.SessionId)
                .ToHashSet(StringComparer.Ordinal);
        }

        var list = Workspaces
            .SelectMany(workspace => workspace.Sessions)
            .Where(session => !session.IsArchived)
            .Where(session => query.Length == 0
                || session.Matches(query)
                || (historyMatches?.Contains(session.Id) ?? false))
            .OrderByDescending(session => session.State.UpdatedAt)
            .Take(8)
            .ToList();
        SyncList(PaletteSessions, list);
        OnPropertyChanged(nameof(ShowPaletteEmpty));
        OnPropertyChanged(nameof(PaletteEmptyText));
    }

    private readonly List<(string SessionId, string Text)> _historyPrompts = [];
    private bool _historyPromptsLoaded;

    /// <summary>Reads home/history.jsonl so the palette can match past prompts.</summary>
    private void LoadHistoryPrompts()
    {
        if (_historyPromptsLoaded)
        {
            return;
        }

        _historyPromptsLoaded = true;
        _historyPrompts.Clear();
        var path = Path.Combine(_homeDirectory, "history.jsonl");
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            foreach (var line in File.ReadLines(path))
            {
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.TryGetProperty("session_id", out var id)
                        && root.TryGetProperty("text", out var text)
                        && id.GetString() is { Length: > 0 } sessionId
                        && text.GetString() is { Length: > 0 } prompt)
                    {
                        // Keep the index bounded; the palette only needs recent prompts.
                        _historyPrompts.Add((sessionId, prompt));
                        if (_historyPrompts.Count > 5000)
                        {
                            _historyPrompts.RemoveAt(0);
                        }
                    }
                }
                catch (JsonException)
                {
                    // A crash can leave a partial final line.
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    public void ToggleRecent() => IsRecentExpanded = !IsRecentExpanded;

    /// <summary>Codex-style edit action: put a user message back into the composer.</summary>
    public void BeginEditUserMessage(ChatItemViewModel item)
    {
        if (!item.IsUser)
        {
            return;
        }

        Composer = item.Text;
        _rewindModelText = null;
        _editingUserItem = item.Seq is null ? null : item;
        RaiseEditState();
        if (item.Seq is null)
        {
            ErrorMessage = "这条消息没有关联的事件记录（可能来自 fork 或导入），只能作为新消息发送。";
        }
    }

    /// <summary>Regenerates the reply by rewinding to the user message before it.</summary>
    public async Task RegenerateAsync(ChatItemViewModel assistant)
    {
        if (!assistant.IsAssistant)
        {
            return;
        }
        if (IsBusy)
        {
            ErrorMessage = "请等待当前回合结束后再重新生成。";
            return;
        }
        if (IsCoreReadOnly)
        {
            ErrorMessage = "另一客户端持有该会话的写权限，当前窗口只能观察。";
            return;
        }

        var index = Messages.IndexOf(assistant);
        ChatItemViewModel? target = null;
        for (var cursor = index - 1; cursor >= 0; cursor--)
        {
            if (Messages[cursor].IsUser)
            {
                target = Messages[cursor];
                break;
            }
        }
        if (target?.Seq is null)
        {
            ErrorMessage = "无法定位这条回复对应的用户记录（可能已被压缩、来自 fork 或导入）。";
            return;
        }

        Composer = target.Text;
        _editingUserItem = target;
        _rewindModelText = string.IsNullOrWhiteSpace(target.ApiText) ? target.Text : target.ApiText;
        RaiseEditState();
        await SendAsync().ConfigureAwait(true);
    }

    /// <summary>Resends a user message, replacing it and every reply after it.</summary>
    public async Task ResendUserAsync(ChatItemViewModel user)
    {
        if (!user.IsUser)
        {
            return;
        }
        if (IsBusy)
        {
            ErrorMessage = "请等待当前回合结束后再重新发送。";
            return;
        }
        if (IsCoreReadOnly)
        {
            ErrorMessage = "另一客户端持有该会话的写权限，当前窗口只能观察。";
            return;
        }
        if (user.Seq is null)
        {
            ErrorMessage = "无法定位这条消息（可能已被压缩、来自 fork 或导入）。";
            return;
        }

        Composer = user.Text;
        _editingUserItem = user;
        _rewindModelText = string.IsNullOrWhiteSpace(user.ApiText) ? user.Text : user.ApiText;
        RaiseEditState();
        await SendAsync().ConfigureAwait(true);
    }

    public void RefreshRecentSessions()
    {
        var query = SearchText.Trim();
        var all = Workspaces
            .SelectMany(workspace => workspace.Sessions)
            .Where(item => query.Length == 0 || item.Matches(query))
            .ToList();

        var pinned = all
            .Where(item => !item.IsArchived && item.IsPinned)
            .ToList();
        pinned = IsPinnedSortByRecent
            ? pinned
                .OrderByDescending(item => IsSessionSortByCreated ? item.State.CreatedAt : item.State.UpdatedAt)
                .ToList()
            : pinned
                .OrderBy(item => item.State.PinnedOrder == 0 ? int.MaxValue : item.State.PinnedOrder)
                .ThenByDescending(item => item.State.UpdatedAt)
                .ToList();
        pinned = pinned.Take(50).ToList();

        var items = all
            .Where(item => !item.IsArchived && !item.IsPinned)
            .OrderByDescending(item => IsSessionSortByCreated ? item.State.CreatedAt : item.State.UpdatedAt)
            .Take(100)
            .ToList();

        var archived = all
            .Where(item => item.IsArchived)
            .OrderByDescending(item => item.State.UpdatedAt)
            .Take(100)
            .ToList();

        // Rebuilding the observable collection on every save makes Avalonia's
        // item container generator re-render identical rows; only publish a
        // change when the visible sequence actually differs.
        SyncList(PinnedSessions, pinned);
        SyncList(RecentSessions, items);
        SyncList(ArchivedSessions, archived);

        OnPropertyChanged(nameof(HasPinnedSessions));
        OnPropertyChanged(nameof(HasRecentSessions));
        OnPropertyChanged(nameof(ShowNoRecentSessions));
        OnPropertyChanged(nameof(ShowArchivedSessions));
    }

    private static void SyncList(ObservableCollection<SessionItemViewModel> target, List<SessionItemViewModel> items)
    {
        var unchanged = items.Count == target.Count;
        if (unchanged)
        {
            for (var index = 0; index < items.Count; index++)
            {
                if (!ReferenceEquals(items[index], target[index]))
                {
                    unchanged = false;
                    break;
                }
            }
        }

        if (unchanged)
        {
            return;
        }

        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    public void ToggleSessionPinned(SessionItemViewModel session)
    {
        session.IsPinned = !session.IsPinned;
        if (session.IsPinned)
        {
            MovePinnedSession(session, int.MaxValue);
            return;
        }

        session.State.PinnedOrder = 0;
        SaveState();
    }

    /// <summary>Codex 手动排序: drop a pinned chat at a new position in the 置顶 list.</summary>
    public void MovePinnedSession(SessionItemViewModel session, int targetIndex)
    {
        var ordered = Workspaces
            .SelectMany(workspace => workspace.Sessions)
            .Where(item => item.IsPinned && item.State.PinnedOrder > 0)
            .OrderBy(item => item.State.PinnedOrder)
            .ToList();

        if (!ordered.Contains(session))
        {
            ordered.Add(session);
        }

        var currentIndex = ordered.FindIndex(item => ReferenceEquals(item, session));
        if (currentIndex >= 0)
        {
            ordered.RemoveAt(currentIndex);
        }

        var index = Math.Clamp(targetIndex, 0, ordered.Count);
        ordered.Insert(index, session);
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].State.PinnedOrder = i + 1;
        }

        RefreshRecentSessions();
        SaveState();
    }

    /// <summary>Drag a chat inside its project list (Codex project rows).</summary>
    public void MoveSessionWithinWorkspace(SessionItemViewModel session, int targetIndex)
    {
        var workspace = session.Workspace;
        var ordered = workspace.Sessions.ToList();
        var current = ordered.IndexOf(session);
        if (current < 0)
        {
            return;
        }

        ordered.RemoveAt(current);
        var index = Math.Clamp(targetIndex, 0, ordered.Count);
        ordered.Insert(index, session);
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].State.SortOrder = i + 1;
        }

        for (var i = 0; i < ordered.Count; i++)
        {
            var from = workspace.Sessions.IndexOf(ordered[i]);
            if (from != i)
            {
                workspace.Sessions.Move(from, i);
            }
        }

        workspace.State.Sessions = ordered.Select(item => item.State).ToList();
        SaveState();
    }

    /// <summary>Drag a project folder row to reorder the 项目 section.</summary>
    public void MoveWorkspace(WorkspaceItemViewModel workspace, int targetIndex)
    {
        var ordered = Workspaces.ToList();
        var current = ordered.IndexOf(workspace);
        if (current < 0)
        {
            return;
        }

        ordered.RemoveAt(current);
        var index = Math.Clamp(targetIndex, 0, ordered.Count);
        ordered.Insert(index, workspace);
        for (var i = 0; i < ordered.Count; i++)
        {
            var from = Workspaces.IndexOf(ordered[i]);
            if (from != i)
            {
                Workspaces.Move(from, i);
            }
        }

        _state.Workspaces = ordered.Select(item => item.State).ToList();
        SaveState();
    }

    /// <summary>Archives the chat currently on screen (Ctrl+Shift+A).</summary>
    public void ArchiveActiveSession()
    {
        if (_activeSessionItem is { } session)
        {
            ArchiveSession(session);
        }
    }

    /// <summary>Pins or unpins the chat currently on screen (Ctrl+Alt+P).</summary>
    public void ToggleActiveSessionPinned()
    {
        if (_activeSessionItem is { } session)
        {
            ToggleSessionPinned(session);
        }
    }

    public void MarkSessionUnread(SessionItemViewModel session)
    {
        session.IsUnread = true;
        SaveState();
    }

    /// <summary>Marks the chat currently on screen unread (Ctrl+Shift+U).</summary>
    public void ToggleActiveSessionUnread()
    {
        if (_activeSessionItem is { } session)
        {
            session.IsUnread = !session.IsUnread;
            SaveState();
        }
    }

    /// <summary>Last fenced code block in the newest assistant reply.</summary>
    public string? LastAssistantCodeBlock()
    {
        for (var index = Messages.Count - 1; index >= 0; index--)
        {
            if (Messages[index].IsAssistant
                && ExtractLastCodeBlock(Messages[index].Text) is { } code)
            {
                return code;
            }
        }

        return null;
    }

    public static string? ExtractLastCodeBlock(string markdown)
    {
        var blocks = new List<string>();
        var current = new System.Text.StringBuilder();
        var inside = false;
        foreach (var line in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inside)
                {
                    blocks.Add(current.ToString().TrimEnd().Replace(Environment.NewLine, "\n"));
                    current.Clear();
                }

                inside = !inside;
                continue;
            }

            if (inside)
            {
                current.AppendLine(line);
            }
        }

        return blocks.Count == 0 ? null : blocks[^1];
    }

    public void ArchiveSession(SessionItemViewModel session)
    {
        session.IsArchived = true;
        SaveState();
        _ = PushArchivedAsync(session, archived: true);
    }

    public void UnarchiveSession(SessionItemViewModel session)
    {
        session.IsArchived = false;
        SaveState();
        _ = PushArchivedAsync(session, archived: false);
    }

    /// <summary>Mirrors the local archive flag into the session store.</summary>
    private async Task PushArchivedAsync(SessionItemViewModel session, bool archived)
    {
        try
        {
            await _core.ArchiveAsync(session.Id, archived, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            _dispatch(() =>
            {
                session.IsArchived = !archived;
                SaveState();
                SetCoreStatus($"{(archived ? "归档" : "取消归档")}失败：{error.Message}");
            });
        }
    }

    public void ToggleCodeReviewPinned()
    {
        _state.IsCodeReviewPinned = !_state.IsCodeReviewPinned;
        SaveState();
        OnPropertyChanged(nameof(IsCodeReviewPinned));
        RefreshPinnedRailItems();
    }

    public bool IsCodeReviewDraftActive => _isCodeReviewDraftActive;

    public void StartCodeReviewDraft()
    {
        Navigate(AppSection.Chat);
        _ = CloseCoreSessionQuietlyAsync();
        StartDraftSession();
        Composer = "请审查当前项目的代码变更，重点检查潜在 bug、回归风险、安全问题和缺失测试。";
        _isCodeReviewDraftActive = true;
        UpdatePinnedRailActiveState();
    }

    private bool _isReviewRunning;
    private string _reviewStatusText = "运行审查后在此显示结果。";

    public ObservableCollection<ReviewFindingRow> ReviewFindings { get; } = [];
    public bool HasReviewFindings => ReviewFindings.Count > 0;
    public bool ShowNoReviewFindings => !HasReviewFindings;

    public int ReviewModeIndex
    {
        get => _state.ReviewMode switch
        {
            "quick" => 0,
            "strict" => 2,
            _ => 1
        };
        set
        {
            var mode = value switch { 0 => "quick", 2 => "strict", _ => "standard" };
            if (_state.ReviewMode == mode)
            {
                return;
            }
            _state.ReviewMode = mode;
            SaveState();
            OnPropertyChanged();
        }
    }

    public bool ReviewFullFiles
    {
        get => _state.ReviewFullFiles;
        set
        {
            if (_state.ReviewFullFiles == value)
            {
                return;
            }
            _state.ReviewFullFiles = value;
            SaveState();
            OnPropertyChanged();
        }
    }

    public string ReviewInstructions
    {
        get => _state.ReviewInstructions;
        set
        {
            if (_state.ReviewInstructions == value)
            {
                return;
            }
            _state.ReviewInstructions = value;
            SaveState();
            OnPropertyChanged();
        }
    }

    public string ReviewStatusText
    {
        get => _reviewStatusText;
        private set => SetProperty(ref _reviewStatusText, value);
    }

    public bool IsReviewRunning
    {
        get => _isReviewRunning;
        private set
        {
            if (SetProperty(ref _isReviewRunning, value))
            {
                OnPropertyChanged(nameof(CanRunReview));
            }
        }
    }

    public bool CanRunReview => !IsReviewRunning && !string.IsNullOrWhiteSpace(WorkspacePath);

    /// <summary>Runs Core's code review for the active workspace.</summary>
    public async Task RunReviewAsync()
    {
        if (IsReviewRunning)
        {
            return;
        }
        var workspace = WorkspacePath;
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            ReviewStatusText = "当前项目路径不可用。";
            return;
        }

        IsReviewRunning = true;
        ReviewStatusText = "正在审查…";
        try
        {
            var result = await _core.RunReviewAsync(
                    workspace,
                    _state.ReviewMode,
                    _state.ReviewInstructions,
                    _state.ReviewFullFiles,
                    CancellationToken.None)
                .ConfigureAwait(true);
            var rows = new List<ReviewFindingRow>();
            if (result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("findings", out var findings)
                && findings.ValueKind == JsonValueKind.Array)
            {
                foreach (var finding in findings.EnumerateArray())
                {
                    var severity = JsonString(finding, "severity") ?? "info";
                    var file = JsonString(finding, "file") ?? string.Empty;
                    var line = finding.TryGetProperty("line", out var lineValue)
                        && lineValue.TryGetInt32(out var parsedLine)
                        ? parsedLine
                        : 0;
                    var location = string.IsNullOrWhiteSpace(file)
                        ? "(workspace)"
                        : line > 0 ? $"{file}:{line}" : file;
                    rows.Add(new ReviewFindingRow(
                        severity,
                        location,
                        JsonString(finding, "rule") ?? string.Empty,
                        JsonString(finding, "evidence") ?? string.Empty,
                        JsonString(finding, "recommendation") ?? string.Empty));
                }
            }

            ReviewFindings.Clear();
            foreach (var row in rows)
            {
                ReviewFindings.Add(row);
            }
            OnPropertyChanged(nameof(HasReviewFindings));
            OnPropertyChanged(nameof(ShowNoReviewFindings));
            ReviewStatusText = JsonString(result, "summary")
                ?? (rows.Count == 0 ? "审查完成：没有发现问题。" : $"审查完成：{rows.Count} 个发现。");
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or FileNotFoundException)
        {
            ReviewStatusText = $"审查失败：{error.Message}";
        }
        finally
        {
            IsReviewRunning = false;
        }
    }

    private static string? JsonString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    public void OpenSettings(SettingsPane? pane = null)
    {
        RefreshSettingsDraft();
        var target = pane
            ?? (Enum.TryParse<SettingsPane>(_state.LastSettingsPane, out var saved)
                ? saved
                : SettingsPane.General);
        SelectSettingsPane(target);
        IsSettingsOpen = true;
    }

    public void CloseSettings() => IsSettingsOpen = false;

    public void SelectSettingsPane(SettingsPane pane)
    {
        _settingsPane = pane;
        if (_state.LastSettingsPane != pane.ToString())
        {
            _state.LastSettingsPane = pane.ToString();
            SaveState();
        }
        OnPropertyChanged(nameof(IsSettingsGeneral));
        OnPropertyChanged(nameof(IsSettingsNotifications));
        OnPropertyChanged(nameof(IsSettingsAppearance));
        OnPropertyChanged(nameof(IsSettingsMemory));
        OnPropertyChanged(nameof(IsSettingsArchived));
        OnPropertyChanged(nameof(IsSettingsShortcuts));
        OnPropertyChanged(nameof(IsSettingsProviders));
        OnPropertyChanged(nameof(IsSettingsModels));
        OnPropertyChanged(nameof(IsSettingsUsage));
        OnPropertyChanged(nameof(IsSettingsConfig));
        OnPropertyChanged(nameof(IsSettingsImport));
        OnPropertyChanged(nameof(IsSettingsCloudPc));
        OnPropertyChanged(nameof(IsSettingsCodeReview));
        OnPropertyChanged(nameof(IsSettingsStorage));
        OnPropertyChanged(nameof(IsSettingsPersonalization));
        OnPropertyChanged(nameof(IsSettingsPlugins));
        OnPropertyChanged(nameof(IsSettingsPasswords));
        OnPropertyChanged(nameof(IsSettingsHooks));
        OnPropertyChanged(nameof(IsSettingsConnections));
        OnPropertyChanged(nameof(IsSettingsGit));
        OnPropertyChanged(nameof(IsSettingsEnvironments));
        OnPropertyChanged(nameof(IsSettingsWorktrees));
        OnPropertyChanged(nameof(SettingsPaneTitle));
        OnPropertyChanged(nameof(SettingsPaneSubtitle));
        OnPropertyChanged(nameof(HasSettingsPaneSubtitle));
        if (pane == SettingsPane.General)
        {
            _ = RefreshAgentSettingsAsync();
        }
        else if (pane == SettingsPane.Personalization)
        {
            _ = RefreshMemoryAsync();
            _ = RefreshAgentSettingsAsync();
        }
        else if (pane == SettingsPane.Import)
        {
            _ = DiscoverImportsAsync();
        }
        else if (pane == SettingsPane.Plugins)
        {
            _ = RefreshMcpToolsAsync();
            _ = RefreshPluginsAsync();
        }
        else if (pane == SettingsPane.Memory)
        {
            _ = RefreshMemoryAsync();
        }
        else if (pane == SettingsPane.Archived)
        {
            RefreshRecentSessions();
        }
        else if (pane == SettingsPane.Usage)
        {
            RefreshUsage();
        }
        else if (pane == SettingsPane.Providers)
        {
            _ = RefreshProvidersAsync(force: HasProviderCards, announce: false);
        }
        else if (pane == SettingsPane.Passwords)
        {
            RefreshCredentials();
        }
        else if (pane == SettingsPane.Hooks)
        {
            RefreshHooks();
        }
        else if (pane == SettingsPane.Git)
        {
            _ = RefreshGitAsync();
        }
        else if (pane == SettingsPane.Worktrees)
        {
            _ = RefreshWorktreesAsync();
        }
        else if (pane == SettingsPane.Config)
        {
            _ = RefreshConfigAsync();
            _ = RefreshAgentSettingsAsync();
        }
        else if (pane == SettingsPane.Storage)
        {
            _ = RefreshStorageAsync();
        }
    }

    public ObservableCollection<UsageSessionRow> UsageRows { get; } = [];
    public long UsageTotalInputTokens { get; private set; }
    public long UsageTotalOutputTokens { get; private set; }
    public double UsageTotalCostUsd { get; private set; }
    public string UsageTokensText => $"{UsageTotalInputTokens:N0} / {UsageTotalOutputTokens:N0}";
    public string UsageCostText => UsageTotalCostUsd > 0 ? $"${UsageTotalCostUsd:0.0000}" : "$0";
    public bool ShowUsageRows => UsageRows.Count > 0;

    /// <summary>Aggregates the per-session usage persisted on completed turns.</summary>
    public void RefreshUsage()
    {
        var metered = _state.Workspaces
            .SelectMany(workspace => workspace.Sessions)
            .Where(session => session.InputTokens > 0 || session.OutputTokens > 0 || session.CostUsd > 0)
            .ToList();
        UsageTotalInputTokens = metered.Sum(session => session.InputTokens);
        UsageTotalOutputTokens = metered.Sum(session => session.OutputTokens);
        UsageTotalCostUsd = metered.Sum(session => session.CostUsd);
        UsageRows.Clear();
        foreach (var session in metered
                     .OrderByDescending(session => session.InputTokens + session.OutputTokens)
                     .Take(20))
        {
            UsageRows.Add(new UsageSessionRow(
                session.Title,
                $"{session.InputTokens:N0} / {session.OutputTokens:N0}",
                session.CostUsd > 0 ? $"${session.CostUsd:0.0000}" : "—"));
        }

        // The analysis tab charts the 12 most recently touched metered chats.
        var recent = metered
            .OrderBy(session => session.UpdatedAt)
            .TakeLast(12)
            .ToList();
        var peak = recent.Count == 0
            ? 0d
            : recent.Max(session => (double)(session.InputTokens + session.OutputTokens));
        UsageBars.Clear();
        foreach (var session in recent)
        {
            var tokens = (double)(session.InputTokens + session.OutputTokens);
            UsageBars.Add(new UsageBarRow(peak > 0 ? Math.Max(4, Math.Round(110 * tokens / peak)) : 4));
        }

        OnPropertyChanged(nameof(UsageTotalInputTokens));
        OnPropertyChanged(nameof(UsageTotalOutputTokens));
        OnPropertyChanged(nameof(UsageTotalCostUsd));
        OnPropertyChanged(nameof(UsageTokensText));
        OnPropertyChanged(nameof(UsageCostText));
        OnPropertyChanged(nameof(ShowUsageRows));
        OnPropertyChanged(nameof(HasUsageBars));
    }

    public ObservableCollection<UsageBarRow> UsageBars { get; } = [];

    public ObservableCollection<ProviderCardRow> ProviderCards { get; } = [];

    public bool HasProviderCards => ProviderCards.Count > 0;

    public bool ShowNoProviderCards => !HasProviderCards;

    public string ProviderSummaryText => ProviderCards.Count == 0
        ? "尚未接入模型提供商。"
        : $"已接入 {ProviderCards.Count} 个提供商 · 支持同时启用多个模型提供商";

    private string _providerStatusText = string.Empty;
    public string ProviderStatusText
    {
        get => _providerStatusText;
        private set
        {
            if (SetProperty(ref _providerStatusText, value))
            {
                OnPropertyChanged(nameof(HasProviderStatus));
            }
        }
    }

    public bool HasProviderStatus => ProviderStatusText.Length > 0;

    private bool _isProviderFormOpen;
    public bool IsProviderFormOpen
    {
        get => _isProviderFormOpen;
        private set => SetProperty(ref _isProviderFormOpen, value);
    }

    private string _providerDraftName = string.Empty;
    public string ProviderDraftName
    {
        get => _providerDraftName;
        set => SetProperty(ref _providerDraftName, value);
    }

    private string _providerDraftNote = string.Empty;
    public string ProviderDraftNote
    {
        get => _providerDraftNote;
        set => SetProperty(ref _providerDraftNote, value);
    }

    private string _providerDraftWebsite = string.Empty;
    public string ProviderDraftWebsite
    {
        get => _providerDraftWebsite;
        set => SetProperty(ref _providerDraftWebsite, value);
    }

    private string _providerDraftChatUrl = string.Empty;
    public string ProviderDraftChatUrl
    {
        get => _providerDraftChatUrl;
        set => SetProperty(ref _providerDraftChatUrl, value);
    }

    private string _providerDraftAnthropicUrl = string.Empty;
    public string ProviderDraftAnthropicUrl
    {
        get => _providerDraftAnthropicUrl;
        set => SetProperty(ref _providerDraftAnthropicUrl, value);
    }

    private string _providerDraftResponsesUrl = string.Empty;
    public string ProviderDraftResponsesUrl
    {
        get => _providerDraftResponsesUrl;
        set => SetProperty(ref _providerDraftResponsesUrl, value);
    }

    private string _providerDraftApiKey = string.Empty;
    public string ProviderDraftApiKey
    {
        get => _providerDraftApiKey;
        set => SetProperty(ref _providerDraftApiKey, value);
    }

    private int _providerChatSuffixIndex = 1;
    public int ProviderChatSuffixIndex
    {
        get => _providerChatSuffixIndex;
        set => SetProperty(ref _providerChatSuffixIndex, value);
    }

    private int _providerAnthropicSuffixIndex = 1;
    public int ProviderAnthropicSuffixIndex
    {
        get => _providerAnthropicSuffixIndex;
        set => SetProperty(ref _providerAnthropicSuffixIndex, value);
    }

    private int _providerResponsesSuffixIndex = 1;
    public int ProviderResponsesSuffixIndex
    {
        get => _providerResponsesSuffixIndex;
        set => SetProperty(ref _providerResponsesSuffixIndex, value);
    }

    private bool _providerChatProtocolEnabled = true;
    public bool ProviderChatProtocolEnabled
    {
        get => _providerChatProtocolEnabled;
        set => SetProperty(ref _providerChatProtocolEnabled, value);
    }

    private bool _providerAnthropicProtocolEnabled;
    public bool ProviderAnthropicProtocolEnabled
    {
        get => _providerAnthropicProtocolEnabled;
        set => SetProperty(ref _providerAnthropicProtocolEnabled, value);
    }

    private bool _providerResponsesProtocolEnabled;
    public bool ProviderResponsesProtocolEnabled
    {
        get => _providerResponsesProtocolEnabled;
        set => SetProperty(ref _providerResponsesProtocolEnabled, value);
    }

    private string _providerFormStatus = string.Empty;
    public string ProviderFormStatus
    {
        get => _providerFormStatus;
        private set
        {
            if (SetProperty(ref _providerFormStatus, value))
            {
                OnPropertyChanged(nameof(HasProviderFormStatus));
            }
        }
    }

    public bool HasProviderFormStatus => ProviderFormStatus.Length > 0;

    private bool _isProviderDetailOpen;
    public bool IsProviderDetailOpen
    {
        get => _isProviderDetailOpen;
        private set
        {
            if (SetProperty(ref _isProviderDetailOpen, value))
            {
                OnPropertyChanged(nameof(IsProviderListOpen));
            }
        }
    }

    public bool IsProviderListOpen => !IsProviderDetailOpen;

    private ProviderCardRow? _detailProvider;
    public string ProviderDetailName => _detailProvider?.Name ?? string.Empty;
    public string ProviderDetailId => _detailProvider?.Id ?? string.Empty;
    public string ProviderDetailNote => _detailProvider?.Note ?? string.Empty;
    public string ProviderDetailWebsite => _detailProvider?.Website ?? string.Empty;
    public bool HasProviderDetailNote => !string.IsNullOrWhiteSpace(ProviderDetailNote);
    public bool HasProviderDetailWebsite => !string.IsNullOrWhiteSpace(ProviderDetailWebsite);
    public Avalonia.Layout.Orientation ProviderDetailHeaderOrientation =>
        HasProviderDetailWebsite
            ? Avalonia.Layout.Orientation.Horizontal
            : Avalonia.Layout.Orientation.Vertical;
    public string ProviderDetailKeyStatus =>
        _detailProvider?.KeyConfigured == true ? "API Key 已配置" : "API Key 未配置";

    private string _providerDetailStatus = string.Empty;
    public string ProviderDetailStatus
    {
        get => _providerDetailStatus;
        private set
        {
            if (SetProperty(ref _providerDetailStatus, value))
            {
                OnPropertyChanged(nameof(HasProviderDetailStatus));
            }
        }
    }

    public bool HasProviderDetailStatus => ProviderDetailStatus.Length > 0;

    private string _providerConnectionStatus = string.Empty;
    public string ProviderConnectionStatus
    {
        get => _providerConnectionStatus;
        private set
        {
            if (SetProperty(ref _providerConnectionStatus, value))
            {
                OnPropertyChanged(nameof(HasProviderConnectionStatus));
            }
        }
    }

    public bool HasProviderConnectionStatus => ProviderConnectionStatus.Length > 0;

    private bool _isProviderDeleteConfirmOpen;
    public bool IsProviderDeleteConfirmOpen
    {
        get => _isProviderDeleteConfirmOpen;
        private set => SetProperty(ref _isProviderDeleteConfirmOpen, value);
    }

    public ObservableCollection<ProviderModelRow> ProviderModels { get; } = [];
    public bool HasProviderModels => ProviderModels.Count > 0;
    public bool ShowNoProviderModels => !HasProviderModels;

    private string _editingProviderId = string.Empty;

    private string _providerEditName = string.Empty;
    public string ProviderEditName
    {
        get => _providerEditName;
        set => SetProperty(ref _providerEditName, value);
    }

    private string _providerEditNote = string.Empty;
    public string ProviderEditNote
    {
        get => _providerEditNote;
        set => SetProperty(ref _providerEditNote, value);
    }

    private string _providerEditWebsite = string.Empty;
    public string ProviderEditWebsite
    {
        get => _providerEditWebsite;
        set => SetProperty(ref _providerEditWebsite, value);
    }

    private string _providerEditStatus = string.Empty;
    public string ProviderEditStatus
    {
        get => _providerEditStatus;
        private set
        {
            if (SetProperty(ref _providerEditStatus, value))
            {
                OnPropertyChanged(nameof(HasProviderEditStatus));
            }
        }
    }

    public bool HasProviderEditStatus => ProviderEditStatus.Length > 0;

    public void OpenProviderDetail(ProviderCardRow provider)
    {
        _detailProvider = provider;
        _editingProviderId = provider.Id.ToLowerInvariant();
        ProviderEditName = provider.Name;
        ProviderEditNote = provider.Note;
        ProviderEditWebsite = provider.Website;
        ProviderEditStatus = string.Empty;
        ProviderDetailStatus = string.Empty;
        ProviderDetailApiKey = string.Empty;
        IsProviderDeleteConfirmOpen = false;
        ProviderModels.Clear();
        foreach (var model in provider.Models)
        {
            ProviderModels.Add(new ProviderModelRow(
                model.Model,
                NormalizeModelProtocol(model.Protocol),
                model.Url,
                model.ContextWindow,
                model.CompactThreshold,
                model.MaxOutputTokens,
                model.Enabled,
                model.ReasoningEffort,
                model.ReasoningEfforts.Count > 0 ? model.ReasoningEfforts : DefaultReasoningLevels,
                model.Name));
        }

        LoadProviderConnections(provider);
        CloseProviderModelForm();
        IsProviderFormOpen = false;
        NotifyProviderModelsChanged();
        NotifyProviderDetailChanged();
        IsProviderDetailOpen = true;
    }

    public void CloseProviderDetail()
    {
        IsProviderDetailOpen = false;
        _detailProvider = null;
        _editingProviderId = string.Empty;
        ProviderEditStatus = string.Empty;
        ProviderDetailStatus = string.Empty;
        ProviderConnectionStatus = string.Empty;
        ProviderDetailApiKey = string.Empty;
        IsProviderDeleteConfirmOpen = false;
        ProviderModels.Clear();
        CloseProviderModelForm();
        NotifyProviderModelsChanged();
        NotifyProviderDetailChanged();
    }

    /// <summary>Fills the detail form with each protocol's complete request URL.</summary>
    private void LoadProviderConnections(ProviderCardRow provider)
    {
        string Find(string protocol)
        {
            if (provider.Protocols.TryGetValue(protocol, out var url)
                && !string.IsNullOrWhiteSpace(url))
            {
                return url.Trim();
            }

            return provider.Models.FirstOrDefault(model =>
                string.Equals(model.Protocol, protocol, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(model.Url))?.Url ?? string.Empty;
        }

        var chat = Find("openai-chat");
        var anthropic = Find("anthropic");
        var responses = Find("openai-responses");

        // 详情页直接编辑完整地址；suffix 置 0 让保存时原样写入。
        ProviderDraftChatUrl = chat;
        ProviderChatSuffixIndex = 0;
        ProviderChatProtocolEnabled = chat.Length > 0;

        ProviderDraftAnthropicUrl = anthropic;
        ProviderAnthropicSuffixIndex = 0;
        ProviderAnthropicProtocolEnabled = anthropic.Length > 0;

        ProviderDraftResponsesUrl = responses;
        ProviderResponsesSuffixIndex = 0;
        ProviderResponsesProtocolEnabled = responses.Length > 0;

        ProviderConnectionStatus = string.Empty;
    }

    /// <summary>Writes the detail page's protocol URLs without changing its models.</summary>
    public async Task SaveProviderConnectionAsync()
    {
        var provider = _detailProvider;
        if (provider is null)
        {
            ProviderConnectionStatus = "请先打开一个提供商。";
            return;
        }

        var models = ProviderModels.ToList();
        var protocols = new Dictionary<string, string?>();
        bool AddProtocol(string key, string url, bool enabled, string label)
        {
            if (!enabled)
            {
                return true;
            }

            url = url.Trim();
            if (url.Length == 0)
            {
                ProviderConnectionStatus = $"请填写 {label} 的请求地址。";
                return false;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                ProviderConnectionStatus = $"{label} 的请求地址必须是完整的 http:// 或 https:// 地址。";
                return false;
            }

            protocols[key] = url;
            return true;
        }

        if (!AddProtocol("openai-chat", ProviderChatEndpoint(), ProviderChatProtocolEnabled, "OpenAI Chat")
            || !AddProtocol("anthropic", ProviderAnthropicEndpoint(), ProviderAnthropicProtocolEnabled, "Anthropic")
            || !AddProtocol("openai-responses", ProviderResponsesEndpoint(), ProviderResponsesProtocolEnabled, "OpenAI Responses"))
        {
            return;
        }
        if (protocols.Count == 0)
        {
            ProviderConnectionStatus = "请至少启用一种协议。";
            return;
        }

        var parameters = new Dictionary<string, object?>
        {
            ["id"] = provider.Id,
            ["enabled"] = provider.Enabled,
            ["protocols"] = protocols,
            ["models"] = models.Select(model => new Dictionary<string, object?>
            {
                ["id"] = model.Id,
                ["name"] = model.Name,
                ["protocol"] = model.Protocol,
                ["context_window"] = model.ContextWindow,
                ["compact_threshold"] = model.CompactThreshold,
                ["max_output_tokens"] = model.MaxOutputTokens,
                ["url"] = model.Url,
                ["enabled"] = model.Enabled,
                ["reasoning_effort"] = model.ReasoningEffort,
                ["reasoning_efforts"] = model.ReasoningEfforts
            }).ToArray()
        };

        try
        {
            await _core.UpsertProviderAsync(parameters, CancellationToken.None).ConfigureAwait(true);
            provider.SetProtocols(SavedProtocols(protocols));
            SaveProviderCache();
            if (ReferenceEquals(_detailProvider, provider))
            {
                ProviderConnectionStatus = "连接配置已写入 KL Core；重新连接后应用到聊天。";
            }
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or NotSupportedException)
        {
            if (ReferenceEquals(_detailProvider, provider))
            {
                ProviderConnectionStatus = $"保存连接配置失败：{error.Message}";
            }
        }
    }

    public void OpenProviderDeleteConfirm()
    {
        if (_detailProvider is null)
        {
            return;
        }

        ProviderDetailStatus = string.Empty;
        IsProviderDeleteConfirmOpen = true;
    }

    public void CancelProviderDeleteConfirm()
    {
        IsProviderDeleteConfirmOpen = false;
        ProviderDetailStatus = string.Empty;
    }

    /// <summary>Removes the provider from the UI immediately, then confirms with Core.</summary>
    public async Task ConfirmDeleteProviderAsync()
    {
        if (_detailProvider is null || !IsProviderDeleteConfirmOpen)
        {
            return;
        }

        var id = _detailProvider.Id;
        var rows = ProviderCards
            .Where(card => string.Equals(card.Id, id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var row = rows.FirstOrDefault();
        var index = row is null ? -1 : ProviderCards.IndexOf(row);

        IsProviderDeleteConfirmOpen = false;
        foreach (var duplicate in rows)
        {
            ProviderCards.Remove(duplicate);
        }

        if (rows.Count > 0)
        {
            NotifyProviderCardsChanged();
        }

        CloseProviderDetail();
        ProviderStatusText = string.Empty;

        try
        {
            await _core.DeleteProviderAsync(id, CancellationToken.None).ConfigureAwait(true);
            var profiles = _state.ProviderProfiles;
            if (profiles is not null && profiles.Remove(id.ToLowerInvariant()))
            {
                SaveState();
            }

            SaveProviderCache();
            await RefreshProvidersAsync(force: true, announce: false).ConfigureAwait(true);
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or NotSupportedException
            or OperationCanceledException
            or TimeoutException)
        {
            if (row is not null
                && !ProviderCards.Any(card =>
                    string.Equals(card.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                ProviderCards.Insert(Math.Clamp(index, 0, ProviderCards.Count), row);
                NotifyProviderCardsChanged();
            }

            ProviderStatusText = $"无法删除提供商 {id}：{error.Message}";
        }
    }

    private void NotifyProviderDetailChanged()
    {
        OnPropertyChanged(nameof(ProviderDetailName));
        OnPropertyChanged(nameof(ProviderDetailNote));
        OnPropertyChanged(nameof(ProviderDetailWebsite));
        OnPropertyChanged(nameof(HasProviderDetailNote));
        OnPropertyChanged(nameof(HasProviderDetailWebsite));
        OnPropertyChanged(nameof(ProviderDetailHeaderOrientation));
        OnPropertyChanged(nameof(ProviderDetailKeyStatus));
    }

    private void NotifyProviderModelsChanged()
    {
        OnPropertyChanged(nameof(HasProviderModels));
        OnPropertyChanged(nameof(ShowNoProviderModels));
    }

    public bool SaveProviderProfile()
    {
        var name = ProviderEditName.Trim();
        if (name.Length == 0)
        {
            ProviderEditStatus = "请填写供应商名称。";
            return false;
        }

        var website = ProviderEditWebsite.Trim();
        if (website.Length > 0
            && (!Uri.TryCreate(website, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            ProviderEditStatus = "官网链接必须是完整的 http:// 或 https:// 地址。";
            return false;
        }

        var note = ProviderEditNote.Trim();
        var profiles = _state.ProviderProfiles ??= [];
        profiles[_editingProviderId] = new ProviderProfileState
        {
            DisplayName = name,
            Note = note,
            Website = website
        };
        SaveState();

        if (ProviderCards.FirstOrDefault(row =>
                string.Equals(row.Id, _editingProviderId, StringComparison.OrdinalIgnoreCase)) is { } row)
        {
            row.Name = name;
            row.Note = note;
            row.Website = website;
        }

        if (_detailProvider is not null)
        {
            _detailProvider.Name = name;
            _detailProvider.Note = note;
            _detailProvider.Website = website;
        }

        ProviderEditStatus = string.Empty;
        ProviderDetailStatus = $"已保存 {name} 的显示信息。";
        NotifyProviderDetailChanged();
        return true;
    }

    // ----- Provider model management -----

    private ProviderModelRow? _editingProviderModel;
    private bool _isProviderModelFormOpen;
    public bool IsProviderModelFormOpen
    {
        get => _isProviderModelFormOpen;
        private set => SetProperty(ref _isProviderModelFormOpen, value);
    }

    public string ProviderModelFormTitle =>
        _editingProviderModel is null ? "添加模型" : "编辑模型";

    private string _providerModelDraftName = string.Empty;
    public string ProviderModelDraftName
    {
        get => _providerModelDraftName;
        set => SetProperty(ref _providerModelDraftName, value);
    }

    private string _providerModelDraftId = string.Empty;
    public string ProviderModelDraftId
    {
        get => _providerModelDraftId;
        set => SetProperty(ref _providerModelDraftId, value);
    }

    private int _providerModelProtocolIndex;
    private int _providerModelCustomProtocolIndex;
    public int ProviderModelProtocolIndex
    {
        get => _providerModelProtocolIndex;
        set
        {
            if (!SetProperty(ref _providerModelProtocolIndex, value))
            {
                return;
            }

            if (value is >= 0 and <= 2)
            {
                _providerModelCustomProtocolIndex = value;
                var url = ProviderProtocolUrl(value);
                if (url.Length > 0)
                {
                    ProviderModelDraftUrl = url;
                }
            }
        }
    }

    public ObservableCollection<ReasoningLevelChoice> ProviderModelReasoningLevels { get; } = [];
    public ObservableCollection<string> ProviderModelDefaultReasoningChoices { get; } = [];

    private string _selectedProviderModelDefaultReasoning = string.Empty;
    public string SelectedProviderModelDefaultReasoning
    {
        get => _selectedProviderModelDefaultReasoning;
        set => SetProperty(ref _selectedProviderModelDefaultReasoning, value);
    }

    private static readonly string[] ReasoningLevels =
        ["none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"];
    private static readonly string[] DefaultReasoningLevels = ["low", "medium", "high", "max"];

    private string _providerModelDraftUrl = string.Empty;
    public string ProviderModelDraftUrl
    {
        get => _providerModelDraftUrl;
        set => SetProperty(ref _providerModelDraftUrl, value);
    }

    private string _providerModelDraftContextWindow = "128000";
    public string ProviderModelDraftContextWindow
    {
        get => _providerModelDraftContextWindow;
        set => SetProperty(ref _providerModelDraftContextWindow, value);
    }

    private string _providerModelDraftCompactThreshold = "0.8";
    public string ProviderModelDraftCompactThreshold
    {
        get => _providerModelDraftCompactThreshold;
        set => SetProperty(ref _providerModelDraftCompactThreshold, value);
    }

    private string _providerModelDraftMaxOutput = "8192";
    public string ProviderModelDraftMaxOutput
    {
        get => _providerModelDraftMaxOutput;
        set => SetProperty(ref _providerModelDraftMaxOutput, value);
    }

    private string _providerModelFormStatus = string.Empty;
    public string ProviderModelFormStatus
    {
        get => _providerModelFormStatus;
        private set
        {
            if (SetProperty(ref _providerModelFormStatus, value))
            {
                OnPropertyChanged(nameof(HasProviderModelFormStatus));
            }
        }
    }

    public bool HasProviderModelFormStatus => ProviderModelFormStatus.Length > 0;

    public ObservableCollection<string> ProviderFetchedModels { get; } = [];
    public bool HasProviderFetchedModels => ProviderFetchedModels.Count > 0;

    private string _selectedProviderFetchedModel = string.Empty;
    public string SelectedProviderFetchedModel
    {
        get => _selectedProviderFetchedModel;
        set
        {
            if (SetProperty(ref _selectedProviderFetchedModel, value)
                && !string.IsNullOrWhiteSpace(value))
            {
                ProviderModelDraftId = value;
            }
        }
    }

    private bool _isFetchingProviderModels;
    public bool IsFetchingProviderModels
    {
        get => _isFetchingProviderModels;
        private set
        {
            if (SetProperty(ref _isFetchingProviderModels, value))
            {
                OnPropertyChanged(nameof(CanFetchProviderModels));
            }
        }
    }

    public bool CanFetchProviderModels => !IsFetchingProviderModels;

    private string _providerModelFetchStatus = string.Empty;
    public string ProviderModelFetchStatus
    {
        get => _providerModelFetchStatus;
        private set
        {
            if (SetProperty(ref _providerModelFetchStatus, value))
            {
                OnPropertyChanged(nameof(HasProviderModelFetchStatus));
            }
        }
    }

    public bool HasProviderModelFetchStatus => ProviderModelFetchStatus.Length > 0;

    public async Task FetchProviderModelsAsync()
    {
        if (_detailProvider is null || IsFetchingProviderModels)
        {
            return;
        }

        IsFetchingProviderModels = true;
        ProviderModelFetchStatus = "正在从 /models 获取模型…";
        try
        {
            var result = await _core.FetchProviderModelsAsync(
                _detailProvider.Id,
                CancellationToken.None).ConfigureAwait(true);
            var models = new List<string>();
            if (result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("models", out var values)
                && values.ValueKind == JsonValueKind.Array)
            {
                foreach (var value in values.EnumerateArray())
                {
                    if (value.ValueKind == JsonValueKind.String
                        && value.GetString() is { Length: > 0 } model
                        && !models.Contains(model, StringComparer.OrdinalIgnoreCase))
                    {
                        models.Add(model);
                    }
                }
            }

            ProviderFetchedModels.Clear();
            foreach (var model in models)
            {
                ProviderFetchedModels.Add(model);
            }
            OnPropertyChanged(nameof(HasProviderFetchedModels));

            if (models.Count == 0)
            {
                ProviderModelFetchStatus = "提供商没有返回模型。";
                return;
            }

            if (result.TryGetProperty("protocol", out var protocol)
                && protocol.ValueKind == JsonValueKind.String
                && _editingProviderModel is null)
            {
                ProviderModelProtocolIndex = protocol.GetString() switch
                {
                    "anthropic" => 1,
                    "openai-responses" => 2,
                    _ => 0
                };
                if (result.TryGetProperty("url", out var url)
                    && url.ValueKind == JsonValueKind.String)
                {
                    ProviderModelDraftUrl = url.GetString() ?? string.Empty;
                }
            }

            SelectedProviderFetchedModel = ProviderFetchedModels[0];
            ProviderModelFetchStatus =
                $"已获取 {models.Count} 个模型；选择后自动填入模型 ID。";
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or NotSupportedException)
        {
            ProviderModelFetchStatus = $"获取模型列表失败：{error.Message}";
        }
        finally
        {
            IsFetchingProviderModels = false;
        }
    }

    private void ClearProviderFetchedModels()
    {
        ProviderFetchedModels.Clear();
        _selectedProviderFetchedModel = string.Empty;
        OnPropertyChanged(nameof(SelectedProviderFetchedModel));
        OnPropertyChanged(nameof(HasProviderFetchedModels));
        ProviderModelFetchStatus = string.Empty;
    }

    private void ResetProviderModelReasoningLevels(
        IEnumerable<string> enabledLevels,
        string defaultLevel)
    {
        var enabled = new HashSet<string>(enabledLevels, StringComparer.OrdinalIgnoreCase);
        ProviderModelReasoningLevels.Clear();
        foreach (var level in ReasoningLevels)
        {
            ProviderModelReasoningLevels.Add(new ReasoningLevelChoice(
                level,
                level,
                enabled.Contains(level),
                RefreshProviderModelReasoningChoices));
        }

        ProviderModelDefaultReasoningChoices.Clear();
        foreach (var level in ReasoningLevels.Where(enabled.Contains))
        {
            ProviderModelDefaultReasoningChoices.Add(level);
        }

        SelectedProviderModelDefaultReasoning = ResolveReasoningDefault(defaultLevel);
    }

    private void RefreshProviderModelReasoningChoices()
    {
        var current = SelectedProviderModelDefaultReasoning;
        var enabled = ProviderModelReasoningLevels
            .Where(level => level.IsEnabled)
            .Select(level => level.Value)
            .ToList();

        // Avoid Clear(): clearing the bound collection also clears the ComboBox
        // selection. Adding a level must leave the current default untouched.
        for (var index = ProviderModelDefaultReasoningChoices.Count - 1; index >= 0; index--)
        {
            if (!enabled.Contains(
                    ProviderModelDefaultReasoningChoices[index],
                    StringComparer.OrdinalIgnoreCase))
            {
                ProviderModelDefaultReasoningChoices.RemoveAt(index);
            }
        }

        for (var index = 0; index < enabled.Count; index++)
        {
            var existing = IndexOfReasoningChoice(enabled[index]);
            if (existing == index)
            {
                continue;
            }

            if (existing >= 0)
            {
                ProviderModelDefaultReasoningChoices.Move(existing, index);
            }
            else
            {
                ProviderModelDefaultReasoningChoices.Insert(index, enabled[index]);
            }
        }

        if (enabled.Contains(current, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        SelectedProviderModelDefaultReasoning = NextReasoningDefault(current, enabled);
    }

    private string ResolveReasoningDefault(string current)
    {
        if (ProviderModelDefaultReasoningChoices.Contains(
                current,
                StringComparer.OrdinalIgnoreCase))
        {
            return current;
        }

        if (ProviderModelDefaultReasoningChoices.Contains("medium", StringComparer.OrdinalIgnoreCase))
        {
            return "medium";
        }

        return ProviderModelDefaultReasoningChoices.FirstOrDefault() ?? string.Empty;
    }

    private int IndexOfReasoningChoice(string level)
    {
        for (var index = 0; index < ProviderModelDefaultReasoningChoices.Count; index++)
        {
            if (string.Equals(
                    ProviderModelDefaultReasoningChoices[index],
                    level,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static string NextReasoningDefault(
        string current,
        IReadOnlyList<string> enabled)
    {
        if (enabled.Count == 0)
        {
            return string.Empty;
        }

        var index = Array.FindIndex(
            ReasoningLevels,
            level => string.Equals(level, current, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            for (var next = index + 1; next < ReasoningLevels.Length; next++)
            {
                if (enabled.Contains(ReasoningLevels[next], StringComparer.OrdinalIgnoreCase))
                {
                    return ReasoningLevels[next];
                }
            }
        }

        if (string.IsNullOrWhiteSpace(current)
            && enabled.Contains("medium", StringComparer.OrdinalIgnoreCase))
        {
            return "medium";
        }

        return enabled[^1];
    }

    public void OpenAddProviderModel()
    {
        _editingProviderModel = null;
        ProviderModelDraftName = string.Empty;
        ProviderModelDraftId = string.Empty;
        ProviderModelDraftUrl = string.Empty;
        ProviderModelDraftContextWindow = "128000";
        ProviderModelDraftCompactThreshold = "0.8";
        ProviderModelDraftMaxOutput = "8192";
        ProviderModelProtocolIndex = 0;
        ProviderModelDraftUrl = ProviderProtocolUrl(0);
        ResetProviderModelReasoningLevels(DefaultReasoningLevels, "medium");
        ProviderModelFormStatus = string.Empty;
        ClearProviderFetchedModels();
        OnPropertyChanged(nameof(ProviderModelFormTitle));
        IsProviderModelFormOpen = true;
    }

    public void OpenEditProviderModel(ProviderModelRow model)
    {
        _editingProviderModel = model;
        ProviderModelDraftName = model.Name;
        ProviderModelDraftId = model.Id;
        ProviderModelDraftUrl = model.Url;
        ProviderModelDraftContextWindow = model.ContextWindow.ToString(CultureInfo.InvariantCulture);
        ProviderModelDraftCompactThreshold =
            model.CompactThreshold.ToString("0.##", CultureInfo.InvariantCulture);
        ProviderModelDraftMaxOutput = model.MaxOutputTokens.ToString(CultureInfo.InvariantCulture);
        var protocolIndex = model.Protocol switch
        {
            "anthropic" => 1,
            "openai-responses" => 2,
            _ => 0
        };
        var providerUrl = ProviderProtocolUrl(protocolIndex);
        var modelUrl = model.Url.Trim();
        var custom = modelUrl.Length > 0
            && providerUrl.Length > 0
            && !string.Equals(modelUrl, providerUrl, StringComparison.OrdinalIgnoreCase);
        _providerModelCustomProtocolIndex = protocolIndex;
        ProviderModelProtocolIndex = custom ? 3 : protocolIndex;
        ProviderModelDraftUrl = custom || modelUrl.Length > 0 ? modelUrl : providerUrl;
        var supportedLevels = model.ReasoningEfforts.Count > 0
            ? model.ReasoningEfforts
            : DefaultReasoningLevels;
        ResetProviderModelReasoningLevels(supportedLevels, model.ReasoningEffort);
        ProviderModelFormStatus = string.Empty;
        ClearProviderFetchedModels();
        OnPropertyChanged(nameof(ProviderModelFormTitle));
        IsProviderModelFormOpen = true;
    }

    public void CloseProviderModelForm()
    {
        IsProviderModelFormOpen = false;
        _editingProviderModel = null;
        ProviderModelFormStatus = string.Empty;
        ClearProviderFetchedModels();
    }

    public async Task SaveProviderModelAsync()
    {
        var id = ProviderModelDraftId.Trim();
        if (id.Length == 0)
        {
            ProviderModelFormStatus = "请填写模型 ID。";
            return;
        }

        var url = ProviderModelDraftUrl.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ProviderModelFormStatus = "模型 URL 必须是完整的 http:// 或 https:// 地址。";
            return;
        }

        if (!int.TryParse(
                ProviderModelDraftContextWindow.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var contextWindow)
            || contextWindow <= 0)
        {
            ProviderModelFormStatus = "上下文窗口必须是正整数。";
            return;
        }

        if (!double.TryParse(
                ProviderModelDraftCompactThreshold.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var compactThreshold)
            || compactThreshold <= 0
            || compactThreshold > 1)
        {
            ProviderModelFormStatus = "压缩阈值必须在 0 到 1 之间。";
            return;
        }

        if (!int.TryParse(
                ProviderModelDraftMaxOutput.Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var maxOutput)
            || maxOutput <= 0)
        {
            ProviderModelFormStatus = "最大输出 tokens 必须是正整数。";
            return;
        }

        var duplicate = ProviderModels.Any(model =>
            !ReferenceEquals(model, _editingProviderModel)
            && string.Equals(model.Id, id, StringComparison.OrdinalIgnoreCase));
        if (duplicate)
        {
            ProviderModelFormStatus = "这个提供商下已经有同名模型。";
            return;
        }

        var protocolIndex = ProviderModelProtocolIndex == 3
            ? _providerModelCustomProtocolIndex
            : ProviderModelProtocolIndex;
        var supportedLevels = ProviderModelReasoningLevels
            .Where(level => level.IsEnabled)
            .Select(level => level.Value)
            .ToList();
        if (supportedLevels.Count == 0)
        {
            ProviderModelFormStatus = "请至少启用一个思考等级。";
            return;
        }

        var defaultReasoning = ProviderModelDefaultReasoningChoices.Contains(
                SelectedProviderModelDefaultReasoning,
                StringComparer.OrdinalIgnoreCase)
            ? SelectedProviderModelDefaultReasoning
            : supportedLevels[0];
        var provider = _detailProvider;
        var previous = _editingProviderModel;
        var previousIndex = previous is null ? -1 : ProviderModels.IndexOf(previous);
        var row = new ProviderModelRow(
            id,
            ProviderProtocol(protocolIndex),
            url,
            contextWindow,
            compactThreshold,
            maxOutput,
            previous?.Enabled ?? true,
            defaultReasoning,
            supportedLevels,
            ProviderModelDraftName.Trim());
        if (previousIndex >= 0)
        {
            ProviderModels[previousIndex] = row;
        }
        else
        {
            ProviderModels.Add(row);
        }

        NotifyProviderModelsChanged();
        CloseProviderModelForm();
        if (!await SaveProviderModelsAsync().ConfigureAwait(true)
            && ReferenceEquals(_detailProvider, provider))
        {
            var rowIndex = ProviderModels.IndexOf(row);
            if (rowIndex >= 0)
            {
                if (previous is null)
                {
                    ProviderModels.RemoveAt(rowIndex);
                }
                else
                {
                    ProviderModels[rowIndex] = previous;
                }
            }

            NotifyProviderModelsChanged();
        }
    }

    public async Task DeleteProviderModelAsync(ProviderModelRow model)
    {
        var index = ProviderModels.IndexOf(model);
        if (index < 0)
        {
            return;
        }

        ProviderModels.RemoveAt(index);
        NotifyProviderModelsChanged();
        if (!await SaveProviderModelsAsync().ConfigureAwait(true))
        {
            ProviderModels.Insert(index, model);
            NotifyProviderModelsChanged();
        }
    }

    public async Task SetProviderModelEnabledAsync(ProviderModelRow model, bool enabled)
    {
        model.Enabled = enabled;
        if (!await SaveProviderModelsAsync().ConfigureAwait(true))
        {
            model.Enabled = !enabled;
        }
    }

    private async Task<bool> SaveProviderModelsAsync()
    {
        var provider = _detailProvider;
        if (provider is null)
        {
            ProviderDetailStatus = "请先打开一个提供商。";
            return false;
        }

        var models = ProviderModels.ToList();
        // Model edits must not remove protocols saved from the connection form.
        var protocols = provider.Protocols
            .ToDictionary(
                pair => pair.Key,
                pair => (string?)pair.Value,
                StringComparer.OrdinalIgnoreCase);
        void AddEnabled(string key, string url, bool enabled)
        {
            if (enabled && !string.IsNullOrWhiteSpace(url))
            {
                protocols[key] = url.Trim();
            }
        }

        AddEnabled("openai-chat", ProviderDraftChatUrl, ProviderChatProtocolEnabled);
        AddEnabled("anthropic", ProviderDraftAnthropicUrl, ProviderAnthropicProtocolEnabled);
        AddEnabled("openai-responses", ProviderDraftResponsesUrl, ProviderResponsesProtocolEnabled);
        foreach (var model in models)
        {
            if (!string.IsNullOrWhiteSpace(model.Url)
                && !protocols.ContainsKey(model.Protocol))
            {
                protocols[model.Protocol] = model.Url;
            }
        }
        var parameters = new Dictionary<string, object?>
        {
            ["id"] = provider.Id,
            ["enabled"] = provider.Enabled,
            ["protocols"] = protocols,
            ["models"] = models.Select(model => new Dictionary<string, object?>
            {
                ["id"] = model.Id,
                ["name"] = model.Name,
                ["protocol"] = model.Protocol,
                ["context_window"] = model.ContextWindow,
                ["compact_threshold"] = model.CompactThreshold,
                ["max_output_tokens"] = model.MaxOutputTokens,
                ["url"] = model.Url,
                ["enabled"] = model.Enabled,
                ["reasoning_effort"] = model.ReasoningEffort,
                ["reasoning_efforts"] = model.ReasoningEfforts
            }).ToArray()
        };

        try
        {
            await _core.UpsertProviderAsync(parameters, CancellationToken.None).ConfigureAwait(true);
            provider.SetProtocols(SavedProtocols(protocols));
            provider.SetModels(models
                .Select(model => new CoreModelOption(
                    provider.Id,
                    model.Id,
                    model.Enabled,
                    provider.KeyConfigured)
                {
                    Protocol = model.Protocol,
                    Url = model.Url,
                    ContextWindow = model.ContextWindow,
                    CompactThreshold = model.CompactThreshold,
                    MaxOutputTokens = model.MaxOutputTokens,
                    Name = model.Name,
                    ReasoningEffort = model.ReasoningEffort,
                    ReasoningEfforts = model.ReasoningEfforts
                })
                .ToList());
            SaveProviderCache();
            return true;
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or NotSupportedException)
        {
            if (ReferenceEquals(_detailProvider, provider))
            {
                ProviderDetailStatus = $"保存模型配置失败：{error.Message}";
            }

            return false;
        }
    }

    private string _providerDetailApiKey = string.Empty;
    public string ProviderDetailApiKey
    {
        get => _providerDetailApiKey;
        set => SetProperty(ref _providerDetailApiKey, value);
    }

    public async Task SaveProviderDetailApiKeyAsync()
    {
        var provider = _detailProvider;
        if (provider is null)
        {
            return;
        }

        var key = ProviderDetailApiKey.Trim();
        if (key.Length == 0)
        {
            ProviderDetailStatus = "请先输入 API Key。";
            return;
        }

        try
        {
            await _core.SetProviderApiKeyAsync(provider.Id, key, CancellationToken.None)
                .ConfigureAwait(true);
            provider.SetKeyConfigured(true);
            if (ReferenceEquals(_detailProvider, provider))
            {
                ProviderDetailApiKey = string.Empty;
                OnPropertyChanged(nameof(ProviderDetailKeyStatus));
            }
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or NotSupportedException)
        {
            if (ReferenceEquals(_detailProvider, provider))
            {
                ProviderDetailStatus = $"保存 API Key 失败：{error.Message}";
            }
        }
    }

    private static string NormalizeModelProtocol(string protocol) => protocol.ToLowerInvariant() switch
    {
        "anthropic" => "anthropic",
        "openai-responses" => "openai-responses",
        _ => "openai-chat"
    };

    private readonly HashSet<string> _providerTogglesInFlight = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ProviderRefreshCache = TimeSpan.FromMinutes(5);
    private DateTimeOffset _providersRefreshedAt;
    private bool _providersRefreshing;
    private bool _providerSaveInFlight;

    /// <summary>Persists a provider's enabled flag through Core.</summary>
    public async Task SetProviderEnabledAsync(ProviderCardRow provider, bool enabled)
    {
        if (!_providerTogglesInFlight.Add(provider.Id))
        {
            return;
        }

        try
        {
            await _core.SetProviderEnabledAsync(provider.Id, enabled, CancellationToken.None)
                .ConfigureAwait(true);
            provider.Enabled = enabled;
            ProviderStatusText =
                $"已{(enabled ? "启用" : "停用")} {provider.Name}；重新连接 KL Core 后应用到聊天。";
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or NotSupportedException)
        {
            provider.Enabled = !enabled;
            ProviderStatusText = $"无法更新 {provider.Name}：{error.Message}";
        }
        finally
        {
            _providerTogglesInFlight.Remove(provider.Id);
        }
    }

    public void OpenProviderForm()
    {
        ProviderFormStatus = string.Empty;
        ProviderStatusText = string.Empty;
        CloseProviderDetail();
        ProviderDraftName = string.Empty;
        ProviderDraftNote = string.Empty;
        ProviderDraftWebsite = string.Empty;
        ProviderDraftChatUrl = string.Empty;
        ProviderDraftAnthropicUrl = string.Empty;
        ProviderDraftResponsesUrl = string.Empty;
        ProviderChatSuffixIndex = 1;
        ProviderAnthropicSuffixIndex = 1;
        ProviderResponsesSuffixIndex = 1;
        ProviderDraftApiKey = string.Empty;
        ProviderChatProtocolEnabled = true;
        ProviderAnthropicProtocolEnabled = false;
        ProviderResponsesProtocolEnabled = false;
        IsProviderFormOpen = true;
    }

    public void CloseProviderForm()
    {
        IsProviderFormOpen = false;
        ProviderDraftApiKey = string.Empty;
        ProviderFormStatus = string.Empty;
    }

    /// <summary>Re-reads Core's provider catalog and config over one Core connection.</summary>
    public async Task RefreshProvidersAsync(bool force = false, bool announce = true)
    {
        if (_providersRefreshing
            || (!force && DateTimeOffset.Now - _providersRefreshedAt < ProviderRefreshCache))
        {
            return;
        }

        _providersRefreshing = true;

        try
        {
            var catalog = await _core.LoadProviderCatalogAsync(CancellationToken.None)
                .ConfigureAwait(true);
            var snapshot = catalog.Snapshot;
            if (!snapshot.Success)
            {
                ProviderStatusText = $"无法读取提供商：{snapshot.Error}";
                return;
            }

            if (!catalog.ProviderListRead)
            {
                ProviderStatusText = "无法刷新提供商：读取 provider.list 失败。";
                return;
            }

            ApplyProviderCards(
                snapshot.ModelOptions,
                snapshot.Models,
                snapshot.Provider,
                snapshot.Provider,
                snapshot.KeyConfigured,
                snapshot.ProviderProtocols);
            MergeConfiguredProviders(catalog.Config);
            EnsureUniqueProviderNames();
            NotifyProviderCardsChanged();
            _providersRefreshedAt = DateTimeOffset.Now;
            if (catalog.ConfigRead)
            {
                SaveProviderCache();
            }
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or NotSupportedException)
        {
            ProviderStatusText = $"无法刷新提供商：{error.Message}";
        }
        finally
        {
            _providersRefreshing = false;
        }
    }

    /// <summary>Writes a provider (and optional key) into Core config.</summary>
    public async Task SaveProviderAsync()
    {
        if (_providerSaveInFlight)
        {
            return;
        }

        _providerSaveInFlight = true;
        try
        {
            await SaveProviderCoreAsync().ConfigureAwait(true);
        }
        finally
        {
            _providerSaveInFlight = false;
        }
    }

    private async Task SaveProviderCoreAsync()
    {
        var name = ProviderDraftName.Trim();
        if (name.Length == 0)
        {
            ProviderFormStatus = "请填写提供商名称。";
            return;
        }

        var website = ProviderDraftWebsite.Trim();
        if (website.Length > 0
            && (!Uri.TryCreate(website, UriKind.Absolute, out var websiteUri)
                || (websiteUri.Scheme != Uri.UriSchemeHttp
                    && websiteUri.Scheme != Uri.UriSchemeHttps)))
        {
            ProviderFormStatus = "官网链接必须是完整的 http:// 或 https:// 地址。";
            return;
        }

        var baseUrl = ProviderDraftChatUrl.Trim();
        if (baseUrl.Length == 0)
        {
            ProviderFormStatus = "请填写请求地址。";
            return;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            ProviderFormStatus = "请求地址必须是完整的 http:// 或 https:// 地址。";
            return;
        }

        // 添加表单把共享的 chat URL 属性当作基础地址，一次生成三种协议。
        var suffixIndex = ProviderChatSuffixIndex;
        var protocols = new Dictionary<string, string?>
        {
            ["openai-chat"] = AppendEndpointSuffix(
                baseUrl, suffixIndex, "/chat/completions", "/v1/chat/completions"),
            ["anthropic"] = AppendEndpointSuffix(
                baseUrl, suffixIndex, "/messages", "/v1/messages"),
            ["openai-responses"] = AppendEndpointSuffix(
                baseUrl, suffixIndex, "/responses", "/v1/responses")
        };

        var id = ProviderIdFromName(name);
        if (ProviderCards.Any(row => string.Equals(row.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            ProviderFormStatus = "这个提供商已存在，请从列表中编辑它。";
            return;
        }

        var parameters = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["enabled"] = true,
            ["protocols"] = protocols,
            ["models"] = Array.Empty<object>()
        };

        var note = ProviderDraftNote.Trim();
        var key = ProviderDraftApiKey.Trim();
        var protocolMap = protocols
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value!,
                StringComparer.OrdinalIgnoreCase);

        // Show the card immediately; the Core write runs after the form closes.
        var inserted = false;
        var card = ProviderCards.FirstOrDefault(existing =>
            string.Equals(existing.Id, id, StringComparison.OrdinalIgnoreCase));
        if (card is null)
        {
            card = new ProviderCardRow(
                id,
                name,
                note,
                website,
                isDefault: false,
                enabled: true,
                keyConfigured: key.Length > 0,
                models: [],
                protocolMap);
            ProviderCards.Insert(0, card);
            inserted = true;
        }

        EnsureUniqueProviderNames();
        NotifyProviderCardsChanged();
        IsProviderFormOpen = false;
        ProviderStatusText = $"正在写入 {name}…";

        try
        {
            await _core.UpsertProviderWithKeyAsync(
                    parameters,
                    id,
                    key.Length > 0 ? key : null,
                    CancellationToken.None)
                .ConfigureAwait(true);

            var profiles = _state.ProviderProfiles ??= [];
            profiles[id] = new ProviderProfileState
            {
                DisplayName = name,
                Note = note,
                Website = website
            };
            SaveState();

            card.Name = name;
            card.Note = note;
            card.Website = website;
            card.SetKeyConfigured(key.Length > 0);
            card.SetProtocols(protocolMap);
            EnsureUniqueProviderNames();
            SaveProviderCache();

            ProviderDraftApiKey = string.Empty;
            ProviderFormStatus = string.Empty;
            ProviderStatusText = $"已写入 {id}；可在提供商详情中继续添加模型。";
            _ = RefreshProvidersAsync(force: true, announce: false);
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or NotSupportedException)
        {
            if (inserted)
            {
                ProviderCards.Remove(card);
                NotifyProviderCardsChanged();
            }

            IsProviderFormOpen = true;
            ProviderStatusText = string.Empty;
            ProviderFormStatus = $"保存提供商失败：{error.Message}";
        }
    }

    private static IReadOnlyDictionary<string, string> SavedProtocols(
        IReadOnlyDictionary<string, string?> protocols) =>
        protocols
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value!,
                StringComparer.OrdinalIgnoreCase);

    private string ProviderChatEndpoint() =>
        AppendEndpointSuffix(
            ProviderDraftChatUrl, ProviderChatSuffixIndex, "/chat/completions", "/v1/chat/completions");

    private string ProviderAnthropicEndpoint() =>
        AppendEndpointSuffix(
            ProviderDraftAnthropicUrl, ProviderAnthropicSuffixIndex, "/messages", "/v1/messages");

    private string ProviderResponsesEndpoint() =>
        AppendEndpointSuffix(
            ProviderDraftResponsesUrl, ProviderResponsesSuffixIndex, "/responses", "/v1/responses");

    private static string AppendEndpointSuffix(
        string url,
        int suffixIndex,
        string shortSuffix,
        string versionedSuffix)
    {
        url = url.Trim().TrimEnd('/');
        if (url.Length == 0)
        {
            return string.Empty;
        }

        var suffix = suffixIndex switch
        {
            1 => shortSuffix,
            2 => versionedSuffix,
            _ => string.Empty
        };
        return suffix.Length > 0 && !url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? url + suffix
            : url;
    }

    private static string ProviderIdFromName(string name)
    {
        var id = NormalizeProviderId(name);
        if (id.Length > 0)
        {
            return id;
        }

        // Non-ASCII names normalize to nothing; derive a stable id so the
        // same name cannot be added twice with different ids.
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(name.Trim()));
        return $"provider-{Convert.ToHexString(hash)[..12].ToLowerInvariant()}";
    }

    private static string NormalizeProviderId(string value)
    {
        var normalized = string.Concat(value
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character)
                || character is '-' or '_' or '.'
                    ? character
                    : '-'))
            .Trim('-', '_', '.');
        return normalized;
    }

    private string ProviderProtocolUrl(int index) => index switch
    {
        1 => ProviderDraftAnthropicUrl.Trim(),
        2 => ProviderDraftResponsesUrl.Trim(),
        _ => ProviderDraftChatUrl.Trim()
    };

    private static string ProviderProtocol(int index) => index switch
    {
        1 => "anthropic",
        2 => "openai-responses",
        _ => "openai-chat"
    };

    private static (int ContextWindow, int MaxOutputTokens) ProviderLimits(string protocol) =>
        protocol switch
        {
            "anthropic" => (200_000, 8_192),
            "openai-responses" => (256_000, 16_384),
            _ => (128_000, 8_192)
        };
    public bool HasUsageBars => UsageBars.Count > 0;

    public ObservableCollection<McpToolRow> McpTools { get; } = [];
    public string McpStatusText { get; private set; } = "打开此页时从 KL Core 读取。";
    public bool ShowMcpTools => McpTools.Count > 0;

    /// <summary>Reads the MCP tool list from Core; the page stays read-only.</summary>
    public async Task RefreshMcpToolsAsync()
    {
        McpStatusText = "正在读取…";
        OnPropertyChanged(nameof(McpStatusText));
        List<McpToolRow> rows;
        try
        {
            var result = await _core.ListMcpToolsAsync(CancellationToken.None);
            rows = ParseMcpTools(result);
            McpStatusText = rows.Count == 0 ? "Core 当前没有可用的 MCP 工具。" : $"共 {rows.Count} 个 MCP 工具";
        }
        catch (InvalidOperationException)
        {
            rows = [];
            McpStatusText = "需要先打开一个聊天会话，然后点刷新。";
        }
        catch (Exception error)
        {
            rows = [];
            McpStatusText = $"无法读取 MCP 列表：{error.Message}";
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
    public string ConfigStatusText { get; private set; } = "打开此页时从 KL Core 读取。";
    public bool ShowConfigRows => ConfigRows.Count > 0;
    public string KlHomeDirectory => ResolveCoreHomeText();
    public string ConfigFilePath => Path.Combine(KlHomeDirectory, "config.toml");
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
        ConfigStatusText = "正在读取…";
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
                    "strict" => "严格隔离",
                    "off" => "无限制",
                    _ => "工作空间写入"
                };
            }
            rows = ParseConfig(result);
            status = rows.Count == 0 ? "Core 未返回配置内容。" : string.Empty;
        }
        catch (Exception error)
        {
            rows = [];
            path = ConfigFilePath;
            status = $"无法读取配置：{error.Message}";
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
            AddConfigRow(rows, agent, "model", "模型");
            AddConfigRow(rows, agent, "approval_mode", "审批模式");
            AddConfigRow(rows, agent, "reasoning_effort", "推理强度");
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
                rows.Add(new ConfigValueRow("提供方", string.Join("、", ids)));
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
            JsonValueKind.True => "是",
            JsonValueKind.False => "否",
            _ => string.Empty
        };
        if (text.Length > 0)
        {
            rows.Add(new ConfigValueRow(label, text));
        }
    }

    public string BackupStatusText { get; private set; } = string.Empty;
    public bool HasBackupStatus => BackupStatusText.Length > 0;
    public string DataDirectoryPath => StateDirectory;

    public ObservableCollection<StorageFolderViewModel> StorageFolders { get; } = [];
    public string StorageTotalLabel { get; private set; } = "KL 占用 —";
    public string StorageStatusText { get; private set; } = "打开此页时计算。";
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
        StorageStatusText = "正在计算…";
        OnPropertyChanged(nameof(StorageStatusText));
        var home = _homeDirectory;
        var measurement = await Task.Run(() => MeasureStorage(home)).ConfigureAwait(true);

        StorageFolders.Clear();
        foreach (var folder in measurement.Folders)
        {
            StorageFolders.Add(folder);
        }

        StorageTotalLabel = $"KL 占用 {StorageFolderViewModel.FormatBytes(measurement.TotalBytes)}";
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
        StorageStatusText = "容量按文件当前大小统计。";

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

        var chat = AddFolder("sessions", "聊天（进行中）") + AddFolder("archived_sessions", "已归档聊天");
        var attachments = AddFolder("attachments", "附件");
        var plugins = AddFolder("plugins", "插件");
        AddFolder("cache", "缓存");
        AddFolder("audit", "审计日志");
        AddFolder("gui", "界面状态");
        AddFolder("memories", "记忆");
        AddFolder("log", "日志");
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
            BackupStatusText = $"已导出到 {targetPath}";
            OnPropertyChanged(nameof(BackupStatusText));
            OnPropertyChanged(nameof(HasBackupStatus));
            return true;
        }
        catch (Exception error)
        {
            BackupStatusText = $"导出失败：{error.Message}";
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
                throw new InvalidDataException("文件不是 KL 的 GUI 设置备份。");
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
            BackupStatusText = "已导入设置；重启 KL 后生效，重启前不会保存新的更改。原设置已自动备份。";
            OnPropertyChanged(nameof(BackupStatusText));
            OnPropertyChanged(nameof(HasBackupStatus));
            return true;
        }
        catch (Exception error) when (error is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException)
        {
            BackupStatusText = $"导入失败：{error.Message}";
            OnPropertyChanged(nameof(BackupStatusText));
            OnPropertyChanged(nameof(HasBackupStatus));
            return false;
        }
    }

    private bool _isSkillsList;

    public bool IsSkillsList => IsPluginsPage && _isSkillsList;
    public bool IsPluginsList => IsPluginsPage && !_isSkillsList;
    public ObservableCollection<SkillRow> Skills { get; } = [];
    public string SkillsStatusText { get; private set; } = "打开技能页时读取当前工作区。";
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

    public async Task SelectWorkspaceAsync(WorkspaceItemViewModel workspace)
    {
        // Re-clicking the selected project must not reopen its latest session
        // (that clears the composer draft); only do it when nothing is open.
        if (IsBusy || (workspace.Id == _activeWorkspace.Id && _activeSession is not null))
        {
            return;
        }

        _activeWorkspace.IsSelected = false;
        _activeWorkspace = workspace;
        _activeWorkspace.IsSelected = true;
        _activeWorkspace.IsExpanded = IsWorkMode;
        _state.ActiveWorkspaceId = workspace.Id;
        OnPropertyChanged(nameof(WorkspaceName));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(WorkspacePath));
        OnPropertyChanged(nameof(CanRunReview));

        await CloseCoreSessionQuietlyAsync().ConfigureAwait(true);
        var recent = workspace.Sessions.FirstOrDefault();
        if (recent is null)
        {
            StartDraftSession();
        }
        else
        {
            await OpenSessionAsync(recent, save: false).ConfigureAwait(true);
        }

        SaveState();
    }

    public void ToggleWorkspace(WorkspaceItemViewModel workspace)
    {
        workspace.IsExpanded = !workspace.IsExpanded;
        SaveState();
    }

    /// <summary>Pin a project (Codex "置顶"): pinned projects sort above the rest.</summary>
    public void ToggleWorkspacePinned(WorkspaceItemViewModel workspace)
    {
        workspace.IsPinned = !workspace.IsPinned;
        var ordered = Workspaces.OrderByDescending(item => item.IsPinned).ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var current = Workspaces.IndexOf(ordered[index]);
            if (current != index)
            {
                Workspaces.Move(current, index);
            }
        }

        _state.Workspaces = ordered.Select(item => item.State).ToList();
        SaveState();
    }

    /// <summary>Archive every conversation of a project (Codex "归档聊天").</summary>
    public void ArchiveWorkspaceChats(WorkspaceItemViewModel workspace)
    {
        foreach (var session in workspace.Sessions)
        {
            session.IsArchived = true;
        }

        RefreshRecentSessions();
        SaveState();
    }

    /// <summary>Remove a project from the sidebar (Codex "移除项目").</summary>
    public void RemoveWorkspace(WorkspaceItemViewModel workspace)
    {
        if (Workspaces.Count <= 1)
        {
            return;
        }

        _state.Workspaces.Remove(workspace.State);
        Workspaces.Remove(workspace);
        OnPropertyChanged(nameof(WorkspaceCount));
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowNoSessions));
        OnPropertyChanged(nameof(SavedSessionCount));

        if (_activeWorkspace.Id == workspace.Id)
        {
            workspace.IsSelected = false;
            var next = Workspaces.FirstOrDefault();
            if (next is not null)
            {
                _activeWorkspace = next;
                _activeWorkspace.IsSelected = true;
                _activeWorkspace.IsExpanded = IsWorkMode;
                _state.ActiveWorkspaceId = next.Id;
                OnPropertyChanged(nameof(WorkspaceName));
                OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateTitle));
                OnPropertyChanged(nameof(WorkspacePath));
                OnPropertyChanged(nameof(CanRunReview));
                var recent = next.Sessions.FirstOrDefault();
                if (recent is null)
                {
                    StartDraftSession();
                }
                else
                {
                    SelectSession(next, recent.State);
                }
            }
        }

        RefreshRecentSessions();
        RefreshWorkspacePathLabels();
        SaveState();
    }

    public bool AddWorkspace(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ErrorMessage = "工作区路径无效。";
            return false;
        }

        if (!Directory.Exists(fullPath))
        {
            ErrorMessage = "工作区目录不存在。";
            return false;
        }

        var existing = Workspaces.FirstOrDefault(workspace =>
            string.Equals(workspace.Path, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _ = SelectWorkspaceAsync(existing);
            return true;
        }

        var name = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = fullPath;
        }
        var state = new WorkspaceState
        {
            Name = name,
            Path = fullPath,
            IsExpanded = IsWorkMode
        };
        _state.Workspaces.Add(state);
        var workspaceVm = new WorkspaceItemViewModel(state);
        Workspaces.Add(workspaceVm);
        RefreshWorkspacePathLabels();
        OnPropertyChanged(nameof(WorkspaceCount));
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowNoSessions));
        _ = SelectWorkspaceAsync(workspaceVm);
        SaveState();
        return true;
    }

    /// <summary>Forks the open session in Core and opens the new branch.</summary>
    public async Task ForkActiveSessionAsync()
    {
        if (IsBusy || IsCoreBusy)
        {
            return;
        }

        if (_activeSession is null || _activeSessionItem is null)
        {
            ReportError("请先打开一个 KL Core 会话，再复制为分支。");
            return;
        }

        if (!await EnsureCoreSessionAsync(CancellationToken.None).ConfigureAwait(true))
        {
            ReportError("无法连接 KL Core 会话。");
            return;
        }

        string newSessionId;
        IsCoreBusy = true;
        try
        {
            newSessionId = await _core.ForkAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception error) when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            ReportError($"复制会话失败：{error.Message}");
            return;
        }
        finally
        {
            IsCoreBusy = false;
        }

        if (string.IsNullOrWhiteSpace(newSessionId))
        {
            ReportError("KL Core 没有返回新会话 ID。");
            return;
        }

        var state = new SessionState
        {
            Id = newSessionId,
            Title = $"{_activeSession.Title} · 分支",
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now
        };
        _activeWorkspace.State.Sessions.Insert(0, state);
        var item = new SessionItemViewModel(_activeWorkspace, state);
        _activeWorkspace.Sessions.Insert(0, item);
        SaveState(silent: true);
        // Opens the branch; it refuses while IsCoreBusy is set, so the RPC above
        // has already released it.
        await OpenSessionAsync(item).ConfigureAwait(true);
    }

    /// <summary>Copies sent attachments into the home store for later recovery.</summary>
    private void PersistAttachments(string sessionId, IReadOnlyList<AttachmentItemViewModel> attachments)
    {
        if (attachments.Count == 0)
        {
            return;
        }

        try
        {
            AttachmentStore.Save(
                _homeDirectory,
                sessionId,
                attachments.Select(attachment => (attachment.Name, attachment.Content)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The turn already carries the attachment inline; a failed copy is not fatal.
        }
    }

    /// <summary>Asks Core to compact the open session and reloads the transcript.</summary>
    public async Task CompactActiveSessionAsync()
    {
        if (IsBusy || IsCoreBusy)
        {
            return;
        }

        if (_activeSession is null)
        {
            ReportError("当前没有打开的 KL Core 会话。");
            return;
        }

        // A restored sidebar row has no live core session until it is opened.
        if (!await EnsureCoreSessionAsync(CancellationToken.None).ConfigureAwait(true))
        {
            ReportError("无法连接 KL Core 会话。");
            return;
        }

        IsCoreBusy = true;
        CoreStatusText = "正在压缩上下文…";
        _suppressCompactionUntil = DateTimeOffset.Now.AddSeconds(5);
        try
        {
            var result = await _core.CompactAsync(CancellationToken.None).ConfigureAwait(true);
            var omitted = result.TryGetProperty("omitted_messages", out var value)
                && value.TryGetInt32(out var count)
                ? count
                : 0;
            CoreStatusText = omitted > 0
                ? $"上下文已压缩（省略 {omitted} 条消息）"
                : "上下文已压缩";
            await LoadHistoryAsync().ConfigureAwait(true);
            var summary = omitted > 0 ? $"省略 {omitted} 条消息" : "无需省略消息";
            // LoadHistoryAsync posts its rebuild, so the marker must be posted after
            // it or the rebuilt transcript would clear it again.
            _dispatch(() =>
            {
                var marker = ChatItemViewModel.Tool($"上下文已压缩 · {summary}", summary, string.Empty, summary);
                marker.Complete("完成");
                Messages.Add(marker);
            });
        }
        catch (Exception error) when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            ReportError($"压缩上下文失败：{error.Message}");
        }
        finally
        {
            IsCoreBusy = false;
        }
    }

    public async Task CancelAsync()
    {
        if (!IsBusy)
        {
            return;
        }

        _turnCancellation?.Cancel();
        ClearQueuedTurns();
        try
        {
            await _core.CancelAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception error) when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            ErrorMessage = $"取消请求失败：{error.Message}";
        }
    }

    public async Task SendAsync()
    {
        if (IsBusy)
        {
            if (IsFollowUpQueue && CanSend)
            {
                QueueTurn(Composer.Trim(), PendingAttachments.ToList());
            }
            return;
        }

        if (!CanSend)
        {
            return;
        }

        var prompt = Composer.Trim();
        var attachments = PendingAttachments.ToList();
        var rewriteTarget = _editingUserItem;
        var rewindSeq = rewriteTarget?.Seq;
        // Regenerate/resend keep the original model text; edits use the typed text.
        var modelPrompt = _rewindModelText ?? BuildModelPrompt(prompt, attachments);
        var displayPrompt = BuildDisplayPrompt(prompt, attachments);
        ErrorMessage = string.Empty;
        SettingsStatus = string.Empty;
        _activeToolItem = null;
        _activeReasoning = null;
        _activeAssistant = null;

        if (!await EnsureCoreSessionAsync(CancellationToken.None).ConfigureAwait(true))
        {
            // Keep the draft text so a failed core start does not lose user input.
            return;
        }

        if (IsCoreReadOnly)
        {
            ErrorMessage = "另一客户端持有该会话的写权限，当前窗口只能观察。";
            return;
        }

        Composer = string.Empty;
        ClearAttachments();

        var session = _activeSession;
        if (session is null)
        {
            ErrorMessage = "当前没有可用会话。";
            return;
        }

        PersistAttachments(session.Id, attachments);

        var isFirstMessage = session.Messages.Count == 0;
        if (isFirstMessage)
        {
            session.Title = prompt.Length == 0 ? "附件会话" : TitleFromPrompt(prompt);
        }
        SessionTitle = session.Title;
        session.UpdatedAt = DateTimeOffset.Now;
        _activeSessionItem?.RefreshTitle();
        _activeSessionItem?.RefreshTime();

        if (rewindSeq is not null && rewriteTarget is not null)
        {
            var targetIndex = Messages.IndexOf(rewriteTarget);
            if (targetIndex < 0)
            {
                rewindSeq = null;
            }
            else
            {
                while (Messages.Count > targetIndex)
                {
                    Messages.RemoveAt(Messages.Count - 1);
                }
            }
        }

        var userItem = ChatItemViewModel.User(displayPrompt, modelPrompt, DateTimeOffset.Now);
        _pendingUserItem = rewindSeq is not null ? null : userItem;
        Messages.Add(userItem);
        SyncStoredMessages();
        UpdateSessionOrder();
        SaveState();

        IsBusy = true;
        _turnCancellation = new CancellationTokenSource();
        try
        {
            _suppressRewindReload = rewindSeq is not null;
            var outcome = await _core.RunTurnAsync(
                modelPrompt,
                _turnCancellation.Token,
                rewindSeq).ConfigureAwait(true);
            if (outcome.Status == "failed" && !string.IsNullOrWhiteSpace(outcome.Text))
            {
                AddError(outcome.Text);
            }
        }
        catch (OperationCanceledException)
        {
            AddError("已停止生成本次回复。");
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or NotSupportedException
            or FileNotFoundException)
        {
            AddError(ToUserFacingError(error));
            if (rewindSeq is not null)
            {
                // The core may already have truncated; reload so the UI matches.
                await LoadHistoryAsync().ConfigureAwait(true);
                Composer = displayPrompt;
            }
        }
        finally
        {
            if (IsBusy)
            {
                FinishTurn("完成");
            }
            _turnCancellation?.Dispose();
            _turnCancellation = null;
            _pendingUserItem = null;
            _suppressRewindReload = false;
            _rewindModelText = null;
            if (_editingUserItem is not null)
            {
                _editingUserItem = null;
                RaiseEditState();
            }
        }

        await DrainQueuedTurnsAsync().ConfigureAwait(true);
    }

    /// <summary>Markdown transcript of the active session, or an empty string when there is none.</summary>
    public string BuildSessionMarkdown() => _activeSession is null
        ? string.Empty
        : SessionMarkdown.Build(_activeSession.Title, WorkspacePath, _activeSession.Messages);

    public string SessionExportFileName
    {
        get
        {
            var title = string.IsNullOrWhiteSpace(_activeSession?.Title) ? "KL 会话" : _activeSession.Title.Trim();
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                title = title.Replace(invalid, '-');
            }
            return title + ".md";
        }
    }

    public void BeginRename(SessionItemViewModel session)
    {
        if (IsBusy)
        {
            return;
        }
        _dialogTarget = session;
        _renameWorkspaceTarget = null;
        RenameTitle = session.Title;
        OnPropertyChanged(nameof(RenameDialogTitle));
        IsRenameDialogOpen = true;
    }

    public void BeginRenameWorkspace(WorkspaceItemViewModel workspace)
    {
        if (IsBusy)
        {
            return;
        }
        _dialogTarget = null;
        _renameWorkspaceTarget = workspace;
        RenameTitle = workspace.Name;
        OnPropertyChanged(nameof(RenameDialogTitle));
        OnPropertyChanged(nameof(IsEditingWorkspace));
        OnPropertyChanged(nameof(RenameSourcePath));
        OnPropertyChanged(nameof(RenameSourceParentPath));
        IsRenameDialogOpen = true;
    }

    public void ConfirmRename()
    {
        if (string.IsNullOrWhiteSpace(RenameTitle))
        {
            return;
        }

        var title = RenameTitle.Trim();
        if (title.Length > 80)
        {
            title = title[..80];
        }

        if (_renameWorkspaceTarget is { } workspace)
        {
            workspace.State.Name = title;
            workspace.RefreshName();
            RefreshWorkspacePathLabels();
            if (workspace.Id == _activeWorkspace.Id)
            {
                OnPropertyChanged(nameof(WorkspaceName));
                OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateTitle));
            }
            CloseDialogs();
            SaveState();
            return;
        }

        if (_dialogTarget is null)
        {
            return;
        }

        var target = _dialogTarget;
        var sessionId = target.Id;
        var oldTitle = target.Title;
        target.Title = title;
        target.RefreshTime();
        if (target.Id == _activeSession?.Id)
        {
            SessionTitle = title;
        }
        CloseDialogs();
        UpdateSessionOrder();
        SaveState();
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _ = PushRenameAsync(target, sessionId, title, oldTitle);
        }
    }

    /// <summary>Renames the session in Core and restores the local title if that fails.</summary>
    private async Task PushRenameAsync(
        SessionItemViewModel session,
        string sessionId,
        string title,
        string oldTitle)
    {
        try
        {
            await _core.RenameSessionAsync(sessionId, title, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or NotSupportedException)
        {
            _dispatch(() =>
            {
                session.Title = oldTitle;
                if (session.Id == _activeSession?.Id)
                {
                    SessionTitle = oldTitle;
                }
                UpdateSessionOrder();
                SaveState();
                SetCoreStatus($"重命名失败：{error.Message}");
            });
        }
    }

    public void BeginDelete(SessionItemViewModel session)
    {
        if (IsBusy)
        {
            return;
        }
        _dialogTarget = session;
        DeletePrompt = $"删除“{session.Title}”？此操作无法撤销。";
        IsDeleteDialogOpen = true;
    }

    public void ConfirmDelete()
    {
        if (_dialogTarget is null)
        {
            return;
        }

        var target = _dialogTarget;
        var sessionId = target.Id;
        target.Workspace.State.Sessions.Remove(target.State);
        target.Workspace.Sessions.Remove(target);
        CleanupSessions.Remove(target);
        if (_activeSession?.Id == target.Id)
        {
            _activeSession = null;
            _activeSessionItem = null;
            var next = target.Workspace.Sessions.FirstOrDefault();
            if (next is null)
            {
                StartDraftSession();
            }
            else
            {
                SelectSession(next.Workspace, next.State);
            }
        }
        CloseDialogs();
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowNoSessions));
        OnPropertyChanged(nameof(SavedSessionCount));
        SaveState();
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _ = PushDeleteAsync(target, sessionId);
        }
    }

    /// <summary>Deletes the rollout in Core and restores the row if that fails.</summary>
    private async Task PushDeleteAsync(SessionItemViewModel session, string sessionId)
    {
        try
        {
            await _core.DeleteSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            _dispatch(() =>
            {
                if (!session.Workspace.State.Sessions.Contains(session.State))
                {
                    session.Workspace.State.Sessions.Insert(0, session.State);
                    session.Workspace.Sessions.Insert(0, session);
                    SaveState();
                }
                SetCoreStatus($"删除失败：{error.Message}");
            });
        }
    }

    private bool _isCleanupDialogOpen;

    public bool IsCleanupDialogOpen
    {
        get => _isCleanupDialogOpen;
        private set => SetProperty(ref _isCleanupDialogOpen, value);
    }

    public ObservableCollection<SessionItemViewModel> CleanupSessions { get; } = [];
    public bool HasCleanupSessions => CleanupSessions.Count > 0;

    /// <summary>Lists chats that have seen no activity for 30 days.</summary>
    public void OpenCleanupDialog()
    {
        var cutoff = DateTimeOffset.Now.AddDays(-30);
        CleanupSessions.Clear();
        foreach (var session in Workspaces
                     .SelectMany(workspace => workspace.Sessions)
                     .Where(item => !item.IsArchived
                         && item.State.UpdatedAt < cutoff
                         && item.Id != _activeSession?.Id)
                     .OrderBy(item => item.State.UpdatedAt))
        {
            CleanupSessions.Add(session);
        }
        OnPropertyChanged(nameof(HasCleanupSessions));
        IsCleanupDialogOpen = true;
    }

    public void CloseCleanupDialog() => IsCleanupDialogOpen = false;

    public void CloseDialogs()
    {
        _dialogTarget = null;
        _renameWorkspaceTarget = null;
        IsRenameDialogOpen = false;
        IsDeleteDialogOpen = false;
        IsCreateProjectDialogOpen = false;
        IsCleanupDialogOpen = false;
        IsCloseConfirmOpen = false;
        DeletePrompt = string.Empty;
        CloseAutomationDialogs();
    }

    public void AddAttachments(IEnumerable<string> paths)
    {
        ErrorMessage = string.Empty;
        var added = 0;
        var skipped = new List<string>();
        foreach (var candidate in paths)
        {
            if (PendingAttachments.Count >= MaxAttachmentCount)
            {
                skipped.Add($"最多附加 {MaxAttachmentCount} 个文件");
                break;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(candidate);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                skipped.Add(Path.GetFileName(candidate));
                continue;
            }

            if (PendingAttachments.Any(item => string.Equals(item.Path, fullPath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            var extension = Path.GetExtension(fullPath);
            if (!SupportedAttachmentExtensions.Contains(extension))
            {
                skipped.Add($"{Path.GetFileName(fullPath)}（暂不支持该格式）");
                continue;
            }
            try
            {
                var info = new FileInfo(fullPath);
                if (!info.Exists)
                {
                    skipped.Add($"{info.Name}（文件不存在）");
                    continue;
                }
                if (info.Length > MaxAttachmentCharacters * 4L)
                {
                    skipped.Add($"{info.Name}（文件过大）");
                    continue;
                }
                var content = File.ReadAllText(fullPath);
                if (content.Length > MaxAttachmentCharacters)
                {
                    content = content[..MaxAttachmentCharacters] + "\n\n[文件内容已截断]";
                }
                PendingAttachments.Add(new AttachmentItemViewModel(fullPath, info.Name, content));
                OnPropertyChanged(nameof(CanQueueSend));
                added++;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                skipped.Add($"{Path.GetFileName(fullPath)}（{error.Message}）");
            }
        }

        OnPropertyChanged(nameof(HasPendingAttachments));
        OnPropertyChanged(nameof(CanSend));
        if (skipped.Count > 0)
        {
            ErrorMessage = "部分附件未添加：" + string.Join("；", skipped);
        }
        else if (added == 0 && PendingAttachments.Count == 0)
        {
            ErrorMessage = "没有可添加的文本附件。";
        }
    }

    public void RemoveAttachment(AttachmentItemViewModel attachment)
    {
        PendingAttachments.Remove(attachment);
        OnPropertyChanged(nameof(HasPendingAttachments));
        OnPropertyChanged(nameof(CanSend));
    }

    public void ClearAttachments()
    {
        if (PendingAttachments.Count == 0)
        {
            return;
        }
        PendingAttachments.Clear();
        OnPropertyChanged(nameof(HasPendingAttachments));
        OnPropertyChanged(nameof(CanSend));
        OnPropertyChanged(nameof(CanQueueSend));
    }

    private static string BuildModelPrompt(string prompt, IReadOnlyList<AttachmentItemViewModel> attachments)
    {
        if (attachments.Count == 0)
        {
            return prompt;
        }

        var builder = new System.Text.StringBuilder(prompt);
        foreach (var attachment in attachments)
        {
            builder.AppendLine();
            builder.AppendLine();
            builder.AppendLine($"<attachment name=\"{attachment.Name}\" path=\"{attachment.Path}\">");
            builder.AppendLine(attachment.Content);
            builder.AppendLine("</attachment>");
        }
        return builder.ToString().Trim();
    }

    private static string BuildDisplayPrompt(string prompt, IReadOnlyList<AttachmentItemViewModel> attachments)
    {
        if (attachments.Count == 0)
        {
            return prompt;
        }
        var names = string.Join("、", attachments.Select(item => item.Name));
        return string.IsNullOrWhiteSpace(prompt) ? $"附件：{names}" : $"{prompt}\n\n附件：{names}";
    }

    public void DismissError() => ErrorMessage = string.Empty;

    public void ReportError(string message) => ErrorMessage = message;

    public async Task SaveSettingsAsync()
    {
        if (!SupportedPermissionModes.Contains(SettingsPermissionMode))
        {
            SetSettingsStatus("权限模式无效。", isError: true);
            return;
        }

        _state.PermissionMode = SettingsPermissionMode;
        if (SettingsPermissionMode == "完全访问" && !_state.FullAccessWarningDismissed)
        {
            _showFullAccessWarning = true;
        }
        SettingsContentFontSize = Math.Clamp(SettingsContentFontSize, 12, 20);
        _state.ContentFontSize = SettingsContentFontSize;
        CodeFontSize = Math.Clamp(CodeFontSize, 9, 20);
        OnPropertyChanged(nameof(ContentFontSize));
        OnPropertyChanged(nameof(PermissionMode));
        OnPropertyChanged(nameof(IsPermissionFull));
        OnPropertyChanged(nameof(IsPermissionAuto));
        OnPropertyChanged(nameof(IsPermissionAsk));
        OnPropertyChanged(nameof(ShowFullAccessWarning));
        SaveState();
        await ApplyPermissionModeAsync(CancellationToken.None).ConfigureAwait(true);
        SetSettingsStatus("设置已保存。审批模式由 KL Core 执行。", isError: false);
    }

    public async Task TestConnectionAsync()
    {
        if (IsTestingConnection)
        {
            return;
        }

        _isTestingConnection = true;
        OnPropertyChanged(nameof(IsTestingConnection));
        OnPropertyChanged(nameof(CanTestConnection));
        OnPropertyChanged(nameof(TestConnectionButtonText));
        SetSettingsStatus("正在检查 KL Core…", isError: false);
        try
        {
            var snapshot = await _core.LoadSnapshotAsync(CancellationToken.None).ConfigureAwait(true);
            if (!snapshot.Success)
            {
                SetSettingsStatus($"KL Core 不可用：{snapshot.Error}", isError: true);
                return;
            }

            ApplyCatalogSnapshot(snapshot);
            SetSettingsStatus(
                $"KL Core {snapshot.CoreVersion}（protocol {snapshot.ProtocolVersion}）连通正常。",
                isError: false);
        }
        finally
        {
            _isTestingConnection = false;
            OnPropertyChanged(nameof(IsTestingConnection));
            OnPropertyChanged(nameof(CanTestConnection));
            OnPropertyChanged(nameof(TestConnectionButtonText));
        }
    }

    /// <summary>Applies the chosen model through Core; the GUI never calls a provider.</summary>
    public async Task SaveModelAsync()
    {
        var choice = SettingsSelectedModel;
        if (string.IsNullOrWhiteSpace(choice))
        {
            SetSettingsStatus("请选择要设为默认的模型。", isError: true);
            return;
        }

        var (provider, model) = ParseModelChoice(choice);
        if (provider.Length == 0)
        {
            SetSettingsStatus("该模型没有对应的 Provider，无法切换。", isError: true);
            return;
        }

        try
        {
            await _core.RequestAsync(
                Protocol.ConfigSetDefaultModel,
                new { provider, model },
                CancellationToken.None).ConfigureAwait(true);
            await RefreshCatalogQuietlyAsync().ConfigureAwait(true);
            SetSettingsStatus($"默认模型已切换为 {provider} / {model}。", isError: false);
        }
        catch (Exception error) when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            SetSettingsStatus($"切换模型失败：{error.Message}", isError: true);
        }
    }

    /// <summary>Reads Core's reasoning effort and reflects it in the chip + picker.</summary>
    public async Task LoadReasoningEffortAsync()
    {
        try
        {
            ReasoningEffort = await _core.GetReasoningEffortAsync(CancellationToken.None)
                .ConfigureAwait(true);
            RefreshReasoningEffortSelection();
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or FileNotFoundException)
        {
            // Reasoning effort is optional metadata; the chip simply stays at 默认.
        }
    }

    /// <summary>Reads Core's global agent settings for the settings panes.</summary>
    public async Task RefreshAgentSettingsAsync()
    {
        try
        {
            var result = await _core.GetAgentConfigAsync(CancellationToken.None).ConfigureAwait(true);
            if (result.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            _loadingAgentSettings = true;
            try
            {
                if (TryReadBool(result, "web_search", out var webSearch))
                {
                    WebSearch = webSearch;
                }
                if (TryReadBool(result, "network_access", out var networkAccess))
                {
                    NetworkAccess = networkAccess;
                }
                if (TryReadBool(result, "memory_enabled", out var memoryEnabled))
                {
                    MemoryEnabled = memoryEnabled;
                }
                if (TryReadBool(result, "plugins_enabled", out var pluginsEnabled))
                {
                    PluginsEnabled = pluginsEnabled;
                }
                if (result.TryGetProperty("output_verbosity", out var verbosity)
                    && verbosity.GetString() is { } verbosityValue)
                {
                    _outputVerbosity = verbosityValue is "concise" or "detailed"
                        ? verbosityValue
                        : "default";
                }
                if (result.TryGetProperty("reasoning_summary", out var summary)
                    && summary.GetString() is { } summaryValue)
                {
                    _reasoningSummary = summaryValue is "off" or "detailed"
                        ? summaryValue
                        : "auto";
                }
                if (result.TryGetProperty("custom_instructions", out var instructions)
                    && instructions.GetString() is { } instructionsValue)
                {
                    CustomInstructionsDraft = instructionsValue;
                }
                if (result.TryGetProperty("personal_style", out var style)
                    && style.GetString() is { } styleValue)
                {
                    _personalStyle = styleValue is "friendly" or "professional"
                        ? styleValue
                        : "default";
                }
                if (result.TryGetProperty("warmth", out var warmth)
                    && warmth.GetString() is { } warmthValue)
                {
                    _warmth = warmthValue is "low" or "high" ? warmthValue : "default";
                }
                if (result.TryGetProperty("enthusiasm", out var enthusiasm)
                    && enthusiasm.GetString() is { } enthusiasmValue)
                {
                    _enthusiasm = enthusiasmValue is "low" or "high" ? enthusiasmValue : "default";
                }
                if (result.TryGetProperty("emoji_usage", out var emoji)
                    && emoji.GetString() is { } emojiValue)
                {
                    _emojiUsage = emojiValue is "less" or "more" ? emojiValue : "default";
                }
                if (result.TryGetProperty("visual_answers", out var visual)
                    && visual.GetString() is { } visualValue)
                {
                    _visualAnswers = visualValue is "off" or "on" ? visualValue : "default";
                }
                if (result.TryGetProperty("heading_style", out var heading)
                    && heading.GetString() is { } headingValue)
                {
                    _headingStyle = headingValue is "concise" or "detailed"
                        ? headingValue
                        : "default";
                }
                if (TryReadBool(result, "quick_answers", out var quickAnswers))
                {
                    _quickAnswers = quickAnswers;
                }
            }
            finally
            {
                _loadingAgentSettings = false;
            }

            OnPropertyChanged(nameof(OutputVerbosityIndex));
            OnPropertyChanged(nameof(ReasoningSummaryIndex));
            OnPropertyChanged(nameof(PersonalStyleIndex));
            OnPropertyChanged(nameof(WarmthIndex));
            OnPropertyChanged(nameof(EnthusiasmIndex));
            OnPropertyChanged(nameof(EmojiIndex));
            OnPropertyChanged(nameof(VisualAnswersIndex));
            OnPropertyChanged(nameof(HeadingStyleIndex));
            OnPropertyChanged(nameof(QuickAnswers));
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or FileNotFoundException)
        {
            // Settings stay at their last known values when Core is unavailable.
        }
    }

    private static bool TryReadBool(JsonElement element, string name, out bool value)
    {
        if (element.TryGetProperty(name, out var property)
            && property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            value = property.GetBoolean();
            return true;
        }
        value = false;
        return false;
    }

    /// <summary>Applies a reasoning effort through Core and updates the picker.</summary>
    public async Task SetReasoningEffortAsync(string effort)
    {
        try
        {
            ReasoningEffort = await _core.SetReasoningEffortAsync(effort, CancellationToken.None)
                .ConfigureAwait(true);
            RefreshReasoningEffortSelection();
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or FileNotFoundException)
        {
            ReportError($"切换推理强度失败：{error.Message}");
        }
    }

    private void RefreshReasoningEffortSelection()
    {
        foreach (var option in ReasoningEffortOptions)
        {
            option.IsSelected = string.Equals(option.Id, _reasoningEffort, StringComparison.Ordinal);
        }
        OnPropertyChanged(nameof(ReasoningEffortIndex));
    }

    /// <summary>Quick permission switch from the composer chip.</summary>
    public void SetPermissionMode(string mode)
    {
        if (!SupportedPermissionModes.Contains(mode))
        {
            return;
        }

        _state.PermissionMode = mode;
        SettingsPermissionMode = mode;
        if (mode == "完全访问" && !_state.FullAccessWarningDismissed)
        {
            _showFullAccessWarning = true;
        }
        SaveState();
        RaisePermissionModeChanged();
        _ = ApplyPermissionModeAsync(CancellationToken.None);
    }

    /// <summary>Config pane index: 请求批准 / 帮我批准 / 完全访问.</summary>
    public int ApprovalModeIndex
    {
        get => _state.PermissionMode switch
        {
            "完全访问" => 2,
            "帮我批准" => 1,
            _ => 0
        };
        set
        {
            var mode = value switch
            {
                2 => "完全访问",
                1 => "帮我批准",
                _ => "请求批准"
            };
            if (mode == _state.PermissionMode)
            {
                return;
            }

            SetPermissionMode(mode);
            OnPropertyChanged();
        }
    }

    private void RaisePermissionModeChanged()
    {
        OnPropertyChanged(nameof(PermissionMode));
        OnPropertyChanged(nameof(ApprovalModeIndex));
        OnPropertyChanged(nameof(IsPermissionFull));
        OnPropertyChanged(nameof(IsPermissionAuto));
        OnPropertyChanged(nameof(IsPermissionAsk));
        OnPropertyChanged(nameof(ShowFullAccessWarning));
    }

    /// <summary>Writes the API key into Core config; the draft is cleared afterwards.</summary>
    public async Task SaveApiKeyAsync()
    {
        var key = SettingsApiKeyDraft.Trim();
        if (key.Length == 0)
        {
            SetSettingsStatus("请先输入 API Key。", isError: true);
            return;
        }

        var (provider, _) = ParseModelChoice(SettingsSelectedModel);
        if (provider.Length == 0)
        {
            provider = CoreCatalogProvider;
        }

        if (provider.Length == 0)
        {
            SetSettingsStatus("KL Core 尚未报告可用的 Provider。", isError: true);
            return;
        }

        try
        {
            await _core.SetProviderApiKeyAsync(provider, key, CancellationToken.None).ConfigureAwait(true);
            SettingsApiKeyDraft = string.Empty;
            await RefreshCatalogQuietlyAsync().ConfigureAwait(true);
            SetSettingsStatus($"已把 {provider} 的 API Key 写入 KL Core。", isError: false);
        }
        catch (Exception error) when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            SetSettingsStatus($"保存 API Key 失败：{error.Message}", isError: true);
        }
    }

    private static (string Provider, string Model) ParseModelChoice(string choice)
    {
        var separator = choice.IndexOf(" / ", StringComparison.Ordinal);
        return separator > 0
            ? (choice[..separator], choice[(separator + 3)..])
            : (string.Empty, choice);
    }

    /// <summary>Runs kl.doctor and lists its checks in the Models settings pane.</summary>
    public async Task RunDoctorAsync()
    {
        if (IsDoctorRunning)
        {
            return;
        }

        IsDoctorRunning = true;
        DoctorChecks.Clear();
        DoctorSummary = "正在运行诊断…";
        try
        {
            var result = await _core.DoctorAsync(CancellationToken.None).ConfigureAwait(true);
            var healthy = result.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
            if (result.TryGetProperty("checks", out var checks)
                && checks.ValueKind == JsonValueKind.Array)
            {
                foreach (var check in checks.EnumerateArray())
                {
                    var name = check.TryGetProperty("name", out var nameValue)
                        ? nameValue.GetString() ?? string.Empty
                        : string.Empty;
                    var passed = check.TryGetProperty("ok", out var passedValue)
                        && passedValue.ValueKind == JsonValueKind.True;
                    var detail = check.TryGetProperty("detail", out var detailValue)
                        ? detailValue.GetString() ?? string.Empty
                        : string.Empty;
                    DoctorChecks.Add($"{(passed ? "✓" : "✕")} {name}"
                        + (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" — {detail}"));
                }
            }

            var version = result.TryGetProperty("core_version", out var versionValue)
                ? versionValue.GetString() ?? string.Empty
                : string.Empty;
            DoctorSummary = healthy ? "诊断通过。" : "诊断发现问题。";
            if (!string.IsNullOrWhiteSpace(version))
            {
                DoctorSummary += $" Core {version}";
            }
            OnPropertyChanged(nameof(HasDoctorSummary));
            OnPropertyChanged(nameof(HasDoctorChecks));
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or FileNotFoundException)
        {
            DoctorSummary = $"诊断失败：{error.Message}";
            OnPropertyChanged(nameof(HasDoctorSummary));
            OnPropertyChanged(nameof(HasDoctorChecks));
        }
        finally
        {
            IsDoctorRunning = false;
        }
    }

    private async Task RefreshCatalogQuietlyAsync()
    {
        try
        {
            var snapshot = await _core.LoadSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
            if (snapshot.Success)
            {
                ApplyCatalogSnapshot(snapshot);
            }
        }
        catch (Exception error) when (error is JsonRpcException or IOException or InvalidOperationException)
        {
            // The write already succeeded; a stale catalog is only cosmetic.
        }
    }

    public void ResetSettingsDefaults()
    {
        SetThemeMode("dark");
        AccentColorIndex = 0;
        UiFontIndex = 0;
        ContentFontIndex = 0;
        CodeFontIndex = 0;
        UiFontWeightIndex = 0;
        ContentFontWeightIndex = 0;
        CodeFontWeightIndex = 0;
        Contrast = 50;
        TranslucentSidebar = false;
        SettingsContentFontSize = 14;
        CodeFontSize = 12;
        SetReduceMotionMode("system");
        SetDiffMarkerMode("color");
        SaveAppearanceSettings();
        SetSettingsStatus("已恢复默认外观设置。", isError: false);
    }

    public string ImportCodexHome => _importService.CodexHome;
    public string ImportClaudeHome => _importService.ClaudeHome;
    public bool IsImportScanning
    {
        get => _isImportScanning;
        private set
        {
            if (SetProperty(ref _isImportScanning, value))
            {
                OnPropertyChanged(nameof(CanImportSelected));
            }
        }
    }

    public string ImportStatusText
    {
        get => _importStatusText;
        private set => SetProperty(ref _importStatusText, value);
    }

    public bool HasImportCandidates => ImportCandidates.Count > 0;
    public bool CanImportSelected => !IsImportScanning && ImportCandidates.Any(item => item.IsSelected);

    private void NotifyImportSelection() => OnPropertyChanged(nameof(CanImportSelected));

    public async Task DiscoverImportsAsync()
    {
        if (IsImportScanning)
        {
            return;
        }

        IsImportScanning = true;
        ImportStatusText = "正在扫描 Codex 和 Claude 的安全配置项…";
        try
        {
            var service = _importService;
            var candidates = await Task.Run(service.Discover).ConfigureAwait(true);
            ImportCandidates.Clear();
            foreach (var candidate in candidates)
            {
                ImportCandidates.Add(new ImportCandidateRow(candidate, NotifyImportSelection));
            }

            ImportStatusText = candidates.Count == 0
                ? "没有找到可导入项。"
                : $"找到 {candidates.Count} 个可导入项。";
        }
        catch (Exception error)
        {
            ImportStatusText = $"扫描失败：{error.Message}";
        }
        finally
        {
            IsImportScanning = false;
            OnPropertyChanged(nameof(HasImportCandidates));
            OnPropertyChanged(nameof(CanImportSelected));
        }
    }

    public async Task ImportSelectedAsync()
    {
        var selected = ImportCandidates.Where(item => item.IsSelected).ToList();
        if (selected.Count == 0)
        {
            return;
        }

        IsImportScanning = true;
        var imported = 0;
        try
        {
            foreach (var row in selected)
            {
                row.Status = "导入中…";
                var service = _importService;
                var candidate = row.Candidate;
                row.Status = await Task.Run(() => service.Import(candidate)).ConfigureAwait(true);
                if (row.Status.StartsWith("已合并", StringComparison.Ordinal)
                    || row.Status.StartsWith("已导入技能", StringComparison.Ordinal))
                {
                    imported++;
                }
            }

            ImportStatusText = $"已处理 {selected.Count} 项，成功 {imported} 项。";
        }
        catch (Exception error)
        {
            ImportStatusText = $"导入失败：{error.Message}";
        }
        finally
        {
            IsImportScanning = false;
            OnPropertyChanged(nameof(CanImportSelected));
        }
    }

    public void RefreshSettingsDraft()
    {
        SettingsPermissionMode = _state.PermissionMode;
        SettingsContentFontSize = _state.ContentFontSize;
        SetSettingsStatus(string.Empty, isError: false);
    }

    public string DoctorSummary
    {
        get => _doctorSummary;
        private set
        {
            if (SetProperty(ref _doctorSummary, value))
            {
                OnPropertyChanged(nameof(HasDoctorSummary));
            }
        }
    }

    public bool HasDoctorSummary => _doctorSummary.Length > 0;
    public bool HasDoctorChecks => DoctorChecks.Count > 0;

    public bool IsDoctorRunning
    {
        get => _isDoctorRunning;
        private set
        {
            if (SetProperty(ref _isDoctorRunning, value))
            {
                OnPropertyChanged(nameof(CanRunDoctor));
            }
        }
    }

    public bool CanRunDoctor => !IsDoctorRunning;

    public string LibraryDraftTitle
    {
        get => _libraryDraftTitle;
        set => SetProperty(ref _libraryDraftTitle, value);
    }

    public string LibraryDraftContent
    {
        get => _libraryDraftContent;
        set => SetProperty(ref _libraryDraftContent, value);
    }

    public bool HasLibraryItems => LibraryItems.Count > 0;

    /// <summary>The first four saved prompts, offered as chips on the empty home screen.</summary>
    private void RefreshHomeSuggestions()
    {
        HomeSuggestions.Clear();
        foreach (var item in LibraryItems.Take(4))
        {
            HomeSuggestions.Add(item);
        }
        OnPropertyChanged(nameof(HasHomeSuggestions));
    }

    public string LibraryEmptyText =>
        "资料库还没有条目。保存常用提示词或规范，之后可一键插入输入框。";

    /// <summary>Saves the draft as a GUI-local library entry.</summary>
    public void SaveLibraryItem()
    {
        var content = LibraryDraftContent.Trim();
        if (content.Length == 0)
        {
            return;
        }

        var state = new LibraryItemState
        {
            Title = LibraryDraftTitle.Trim(),
            Content = content
        };
        _state.LibraryItems.Insert(0, state);
        LibraryItems.Insert(0, new LibraryItemViewModel(state));
        LibraryDraftTitle = string.Empty;
        LibraryDraftContent = string.Empty;
        OnPropertyChanged(nameof(HasLibraryItems));
        RefreshHomeSuggestions();
        SaveState();
    }

    public void DeleteLibraryItem(LibraryItemViewModel item)
    {
        _state.LibraryItems.Remove(item.State);
        LibraryItems.Remove(item);
        OnPropertyChanged(nameof(HasLibraryItems));
        RefreshHomeSuggestions();
        SaveState();
    }

    /// <summary>Copies an entry into the composer and goes back to the chat draft.</summary>
    public void InsertLibraryItem(LibraryItemViewModel item)
    {
        Composer = string.IsNullOrWhiteSpace(Composer)
            ? item.Content
            : Composer + "\n" + item.Content;
        Navigate(AppSection.Chat);
    }

    public void BeginCreateAutomation()
    {
        _automationDialogTarget = null;
        AutomationName = string.Empty;
        AutomationPrompt = string.Empty;
        SelectedAutomationInterval = AutomationIntervals[1];
        AutomationDialogStatus = string.Empty;
        IsAutomationDialogOpen = true;
        OnPropertyChanged(nameof(AutomationDialogTitle));
    }

    public void BeginEditAutomation(AutomationItemViewModel automation)
    {
        _automationDialogTarget = automation;
        AutomationName = automation.Name;
        AutomationPrompt = automation.Prompt;
        SelectedAutomationInterval = AutomationIntervals.FirstOrDefault(
            option => option.Minutes == automation.IntervalMinutes) ?? AutomationIntervals[1];
        AutomationDialogStatus = string.Empty;
        IsAutomationDialogOpen = true;
        OnPropertyChanged(nameof(AutomationDialogTitle));
    }

    public void SaveAutomation()
    {
        var name = AutomationName.Trim();
        var prompt = AutomationPrompt.Trim();
        if (name.Length == 0)
        {
            AutomationDialogStatus = "任务名称不能为空。";
            return;
        }
        if (prompt.Length == 0)
        {
            AutomationDialogStatus = "任务指令不能为空。";
            return;
        }
        if (SelectedAutomationInterval is null)
        {
            AutomationDialogStatus = "请选择运行间隔。";
            return;
        }

        if (name.Length > 80)
        {
            name = name[..80];
        }

        if (_automationDialogTarget is null)
        {
            var state = new AutomationState
            {
                Name = name,
                Prompt = prompt,
                IntervalMinutes = SelectedAutomationInterval.Minutes,
                IsEnabled = false,
                CreatedAt = DateTimeOffset.Now
            };
            _state.Automations.Add(state);
            Automations.Insert(0, new AutomationItemViewModel(state));
        }
        else
        {
            _automationDialogTarget.State.Name = name;
            _automationDialogTarget.State.Prompt = prompt;
            _automationDialogTarget.State.IntervalMinutes = SelectedAutomationInterval.Minutes;
            _automationDialogTarget.NotifyStateChanged();
        }

        CloseAutomationDialogs();
        SaveState();
        NotifyAutomationCollectionChanged();
    }

    public void CloseAutomationDialogs()
    {
        IsAutomationDialogOpen = false;
        IsAutomationDeleteDialogOpen = false;
        _automationDialogTarget = null;
        _automationDeleteTarget = null;
        AutomationDialogStatus = string.Empty;
        AutomationDeletePrompt = string.Empty;
    }

    public void ToggleAutomation(AutomationItemViewModel automation)
    {
        if (automation.IsRunning)
        {
            return;
        }
        automation.State.IsEnabled = !automation.State.IsEnabled;
        automation.State.NextRunAt = automation.State.IsEnabled
            ? DateTimeOffset.Now.AddMinutes(automation.IntervalMinutes)
            : null;
        automation.NotifyStateChanged();
        SaveState();
        NotifyAutomationCollectionChanged();
    }

    public void BeginDeleteAutomation(AutomationItemViewModel automation)
    {
        _automationDeleteTarget = automation;
        AutomationDeletePrompt = $"删除自动化“{automation.Name}”？历史运行结果也会一起删除。";
        IsAutomationDeleteDialogOpen = true;
    }

    public void ConfirmDeleteAutomation()
    {
        if (_automationDeleteTarget is null)
        {
            return;
        }
        _state.Automations.Remove(_automationDeleteTarget.State);
        Automations.Remove(_automationDeleteTarget);
        CloseAutomationDialogs();
        SaveState();
        NotifyAutomationCollectionChanged();
    }

    public async Task RunAutomationAsync(AutomationItemViewModel automation)
    {
        if (automation.IsRunning)
        {
            return;
        }

        automation.IsRunning = true;
        automation.State.LastStatus = "运行中…";
        automation.NotifyStateChanged();
        try
        {
            var result = await _core.RunEphemeralTurnAsync(
                _activeWorkspace.Path,
                automation.Prompt,
                CancellationToken.None).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(result))
            {
                throw new InvalidOperationException("模型没有返回可保存的正文结果。");
            }
            automation.State.LastResult = result.Length > 12000 ? result[..12000] : result;
            automation.State.LastStatus = "完成";
        }
        catch (Exception error)
        {
            automation.State.LastResult = error.Message;
            automation.State.LastStatus = "失败";
        }
        finally
        {
            var now = DateTimeOffset.Now;
            automation.State.LastRunAt = now;
            automation.State.NextRunAt = automation.State.IsEnabled
                ? now.AddMinutes(automation.IntervalMinutes)
                : null;
            automation.IsRunning = false;
            automation.NotifyStateChanged();
            SaveState();
            OnPropertyChanged(nameof(AutomationPageSummary));
            if (_state.AutomationNotifications)
            {
                RequestAttention(AttentionKind.Automation);
            }
        }
    }

    public async Task RunDueAutomationsAsync(DateTimeOffset? nowValue = null)
    {
        if (_automationSchedulerBusy)
        {
            return;
        }

        _automationSchedulerBusy = true;
        try
        {
            var now = nowValue ?? DateTimeOffset.Now;
            var due = Automations
                .Where(item => item.IsEnabled && item.State.NextRunAt is { } next && next <= now)
                .ToList();
            foreach (var automation in due)
            {
                await RunAutomationAsync(automation);
            }
        }
        finally
        {
            _automationSchedulerBusy = false;
        }
    }

    private void BuildAutomationItems()
    {
        Automations.Clear();
        foreach (var automation in _state.Automations.OrderByDescending(item => item.CreatedAt))
        {
            Automations.Add(new AutomationItemViewModel(automation));
        }
        NotifyAutomationCollectionChanged();
    }

    private void NotifyAutomationCollectionChanged()
    {
        ApplyAutomationFilter();
        OnPropertyChanged(nameof(HasAutomations));
        OnPropertyChanged(nameof(AutomationPageSummary));
        OnPropertyChanged(nameof(AutomationCount));
        OnPropertyChanged(nameof(AutomationEmptyTitle));
        OnPropertyChanged(nameof(AutomationEmptyDescription));
    }

    private void ApplyAutomationFilter()
    {
        var query = AutomationSearch.Trim();
        foreach (var automation in Automations)
        {
            var matchesFilter = _automationFilter switch
            {
                AutomationFilter.Active => automation.IsEnabled,
                AutomationFilter.Ended => !automation.IsEnabled,
                _ => true
            };
            automation.IsVisible = matchesFilter && automation.Matches(query);
        }
        OnPropertyChanged(nameof(ShowNoAutomations));
        OnPropertyChanged(nameof(AutomationEmptyTitle));
        OnPropertyChanged(nameof(AutomationEmptyDescription));
    }

    public bool ImportPlugin(string sourceDirectory)
    {
        LastImportedPluginPath = null;
        try
        {
            var source = Path.GetFullPath(sourceDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!File.Exists(Path.Combine(source, "plugin.json"))
                && !File.Exists(Path.Combine(source, "manifest.json")))
            {
                ErrorMessage = "所选目录中没有 plugin.json 或 manifest.json。";
                return false;
            }

            Directory.CreateDirectory(PluginDirectoryPath);
            var managedRoot = Path.GetFullPath(PluginDirectoryPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (source.StartsWith(managedRoot, StringComparison.OrdinalIgnoreCase))
            {
                ErrorMessage = "该目录已经位于插件管理目录中。";
                return false;
            }
            var baseName = Path.GetFileName(source);
            if (string.IsNullOrWhiteSpace(baseName))
            {
                baseName = "plugin";
            }
            var target = Path.Combine(PluginDirectoryPath, baseName);
            var suffix = 2;
            while (Directory.Exists(target))
            {
                target = Path.Combine(PluginDirectoryPath, $"{baseName}-{suffix++}");
            }

            var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
            CopyDirectory(source, temporary);
            Directory.Move(temporary, target);
            LastImportedPluginPath = target;
            RefreshPlugins();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ErrorMessage = $"导入插件失败：{error.Message}";
            return false;
        }
    }

    public string? LastImportedPluginPath { get; private set; }

    /// <summary>Registers a copied plugin directory with the running Core.</summary>
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

    private void SetSettingsStatus(string message, bool isError)
    {
        SettingsStatus = message;
        _settingsStatusIsError = isError;
        OnPropertyChanged(nameof(HasSettingsStatus));
        OnPropertyChanged(nameof(IsSettingsStatusError));
        OnPropertyChanged(nameof(IsSettingsStatusSuccess));
    }


    private void NormalizeState()
    {
        var pinnedOrderSeed = 0;
        _state.Endpoint = string.IsNullOrWhiteSpace(_state.Endpoint) ? "https://api.deepseek.com" : _state.Endpoint.Trim();
        _state.Model = string.IsNullOrWhiteSpace(_state.Model) ? "deepseek-chat" : _state.Model.Trim();
        if (string.Equals(_state.PermissionMode, "按需确认", StringComparison.Ordinal))
        {
            // The old two-mode label maps onto Codex's "请求批准".
            _state.PermissionMode = "请求批准";
        }

        _state.PermissionMode = SupportedPermissionModes.Contains(_state.PermissionMode)
            ? _state.PermissionMode
            : "完全访问";
        _state.SendShortcut = _state.SendShortcut == "ctrlEnter" ? "ctrlEnter" : "enter";
        _state.CloseWindowAction = _state.CloseWindowAction is "always" or "never" ? _state.CloseWindowAction : "shortcut";
        _state.FileOpenTarget = string.Equals(_state.FileOpenTarget, "vscode", StringComparison.OrdinalIgnoreCase)
            ? "vscode"
            : "system";
        _state.TurnNotifications = _state.TurnNotifications is "always" or "never" ? _state.TurnNotifications : "unfocused";
        _state.AccentColor = AccentColors.Contains(_state.AccentColor, StringComparer.OrdinalIgnoreCase)
            ? _state.AccentColor
            : AccentColors[0];
        _state.ThemeMode = _state.ThemeMode is "light" or "system" ? _state.ThemeMode : "dark";
        _state.LightAccentColor = AccentColors.Contains(_state.LightAccentColor, StringComparer.OrdinalIgnoreCase)
            ? _state.LightAccentColor
            : AccentColors[1];
        _state.DarkAccentColor = AccentColors.Contains(_state.DarkAccentColor, StringComparer.OrdinalIgnoreCase)
            ? _state.DarkAccentColor
            : AccentColors[0];
        _state.LightContrast = Math.Clamp(_state.LightContrast, 0, 100);
        _state.DarkContrast = Math.Clamp(_state.DarkContrast, 0, 100);
        _state.DiffMarkerMode = _state.DiffMarkerMode == "markers" ? "markers" : "color";
        _state.TerminalShell = TerminalShells.Contains(_state.TerminalShell) ? _state.TerminalShell : TerminalShells[0];
        _state.TerminalPosition = _state.TerminalPosition == "right" ? "right" : "bottom";
        _state.TerminalPanelSize = _state.TerminalPanelSize is >= 180 and <= 600 ? _state.TerminalPanelSize : 0;
        _state.ContentCodeFontSize = Math.Clamp(_state.ContentCodeFontSize, 9, 20);
        _state.UiFontIndex = Math.Clamp(_state.UiFontIndex, 0, 1);
        _state.ContentFontIndex = Math.Clamp(_state.ContentFontIndex, 0, 2);
        _state.CodeFontIndex = Math.Clamp(_state.CodeFontIndex, 0, 2);
        _state.UiFontWeightIndex = Math.Clamp(_state.UiFontWeightIndex, 0, 2);
        _state.ContentFontWeightIndex = Math.Clamp(_state.ContentFontWeightIndex, 0, 2);
        _state.CodeFontWeightIndex = Math.Clamp(_state.CodeFontWeightIndex, 0, 2);
        _state.Contrast = Math.Clamp(_state.Contrast, 0, 100);
        _state.ReduceMotion = _state.ReduceMotion is "on" or "off" ? _state.ReduceMotion : "system";
        _state.FollowUpMode = _state.FollowUpMode == "steer" ? "steer" : "queue";
        _state.ReviewMode = _state.ReviewMode is "quick" or "strict" ? _state.ReviewMode : "standard";
        _state.GitBranchPrefix = (_state.GitBranchPrefix ?? string.Empty).Trim();
        _state.ReviewInstructions ??= string.Empty;
        _state.WorktreeRoot ??= string.Empty;
        _state.AutoArchiveDays = _state.AutoArchiveDays > 0 ? Math.Clamp(_state.AutoArchiveDays, 7, 3650) : 0;
        // Codex renders conversation text at 14px; 15 came from an earlier calibration.
        _state.ContentFontSize = _state.ContentFontSize is 13 or 14 or 15 ? 14 : _state.ContentFontSize;
        _state.ContentFontSize = _state.ContentFontSize is >= 13 and <= 16 ? _state.ContentFontSize : 14;
        _state.InteractionMode = string.Equals(_state.InteractionMode, "chat", StringComparison.OrdinalIgnoreCase)
            ? "chat"
            : "work";
        _state.PinnedSort = string.Equals(_state.PinnedSort, "recent", StringComparison.OrdinalIgnoreCase)
            ? "recent"
            : "manual";
        _state.NotificationFilter = _state.NotificationFilter is "priority" or "pinned" or "automations"
            ? _state.NotificationFilter
            : "priority";
        _notificationFilter = _state.NotificationFilter;
        _state.Workspaces ??= [];
        _state.Automations ??= [];

        // Sidebar width: match Codex's 238-468 range and migrate the old 284 default.
        if (Math.Abs(_state.SidebarWidth - 284) < 0.5 || _state.SidebarWidth < 238)
        {
            _state.SidebarWidth = 238;
        }

        _state.SidebarWidth = Math.Clamp(_state.SidebarWidth, 238, 468);

        foreach (var workspace in _state.Workspaces)
        {
            workspace.Id = string.IsNullOrWhiteSpace(workspace.Id) ? Guid.NewGuid().ToString("N") : workspace.Id;
            workspace.Path = workspace.Path?.Trim() ?? string.Empty;
            workspace.Name = string.IsNullOrWhiteSpace(workspace.Name)
                ? Path.GetFileName(workspace.Path)
                : workspace.Name;
            workspace.Sessions ??= [];
            // Messages live in KL Core; a gui-state session may legitimately have
            // none yet (fresh session, or one that was never opened in this client).
            // Dropping rows here used to lose pinned/archived flags on every restart.
            foreach (var session in workspace.Sessions)
            {
                session.Id = string.IsNullOrWhiteSpace(session.Id) ? Guid.NewGuid().ToString("N") : session.Id;
                session.Title = string.IsNullOrWhiteSpace(session.Title) ? "新会话" : session.Title;
                session.Messages ??= [];
                if (session.IsPinned)
                {
                    pinnedOrderSeed++;
                    if (session.PinnedOrder == 0)
                    {
                        session.PinnedOrder = pinnedOrderSeed;
                    }
                    else
                    {
                        pinnedOrderSeed = Math.Max(pinnedOrderSeed, session.PinnedOrder);
                    }
                }
            }
        }

        if (_state.Workspaces.Count == 0)
        {
            var workspacePath = Environment.GetEnvironmentVariable("KL_GUI_WORKSPACE");
            if (string.IsNullOrWhiteSpace(workspacePath) && Directory.Exists(@"D:\kl"))
            {
                workspacePath = @"D:\kl";
            }
            workspacePath = string.IsNullOrWhiteSpace(workspacePath)
                ? Environment.CurrentDirectory
                : Path.GetFullPath(workspacePath);
            _state.Workspaces.Add(new WorkspaceState
            {
                Name = Path.GetFileName(workspacePath.TrimEnd(Path.DirectorySeparatorChar)) ?? "工作区",
                Path = workspacePath
            });
        }

        _state.ActiveWorkspaceId = _state.Workspaces.Any(workspace => workspace.Id == _state.ActiveWorkspaceId)
            ? _state.ActiveWorkspaceId
            : _state.Workspaces[0].Id;

        var now = DateTimeOffset.Now;
        foreach (var automation in _state.Automations)
        {
            automation.Id = string.IsNullOrWhiteSpace(automation.Id)
                ? Guid.NewGuid().ToString("N")
                : automation.Id;
            automation.Name = string.IsNullOrWhiteSpace(automation.Name) ? "未命名自动化" : automation.Name.Trim();
            automation.Prompt ??= string.Empty;
            automation.LastResult ??= string.Empty;
            automation.LastStatus ??= string.Empty;
            automation.IntervalMinutes = automation.IntervalMinutes < 15 ? 60 : automation.IntervalMinutes;
            if (automation.IsEnabled && (automation.NextRunAt is null || automation.NextRunAt <= now))
            {
                automation.NextRunAt = now.AddMinutes(automation.IntervalMinutes);
            }
            if (!automation.IsEnabled)
            {
                automation.NextRunAt = null;
            }
        }
    }

    private void BuildWorkspaceItems()
    {
        Workspaces.Clear();
        foreach (var workspaceState in _state.Workspaces)
        {
            Workspaces.Add(new WorkspaceItemViewModel(workspaceState) { IsExpanded = IsWorkMode });
        }

        RefreshWorkspacePathLabels();
    }

    /// <summary>
    /// Same-named projects get a dim path suffix; unique names stay clean.
    /// </summary>
    private void RefreshWorkspacePathLabels()
    {
        var duplicated = Workspaces
            .GroupBy(workspace => workspace.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToHashSet();
        foreach (var workspace in Workspaces)
        {
            workspace.SetPathLabel(duplicated.Contains(workspace) ? workspace.Path : string.Empty);
        }
    }

    private bool TryFindSession(
        string? sessionId,
        out WorkspaceItemViewModel workspace,
        out SessionItemViewModel session)
    {
        foreach (var candidate in Workspaces)
        {
            var found = candidate.Sessions.FirstOrDefault(item => item.Id == sessionId);
            if (found is not null)
            {
                workspace = candidate;
                session = found;
                return true;
            }
        }
        workspace = null!;
        session = null!;
        return false;
    }

    private void SelectSession(WorkspaceItemViewModel workspace, SessionState session)
    {
        _isCodeReviewDraftActive = false;
        UpdatePinnedRailActiveState();
        foreach (var item in Workspaces.SelectMany(candidate => candidate.Sessions))
        {
            item.IsSelected = false;
        }

        if (_activeWorkspace.Id != workspace.Id)
        {
            _activeWorkspace.IsSelected = false;
            _activeWorkspace = workspace;
            _activeWorkspace.IsSelected = true;
        }
        _activeWorkspace.IsExpanded = IsWorkMode;
        _activeSession = session;
        _activeSessionItem = workspace.Sessions.First(item => item.Id == session.Id);
        _activeSessionItem.IsSelected = true;
        if (_activeSessionItem.IsUnread)
        {
            _activeSessionItem.IsUnread = false;
            SaveState(silent: true);
        }
        _state.ActiveWorkspaceId = workspace.Id;
        _state.ActiveSessionId = session.Id;
        SessionTitle = session.Title;
        Composer = string.Empty;
        ClearAttachments();
        LoadMessages(session);
        OnPropertyChanged(nameof(WorkspaceName));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(WorkspacePath));
        OnPropertyChanged(nameof(CanRunReview));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowNoSessions));
        OnPropertyChanged(nameof(UsageText));
        OnPropertyChanged(nameof(HasUsageText));
    }

    private void StartDraftSession()
    {
        _isCodeReviewDraftActive = false;
        UpdatePinnedRailActiveState();
        foreach (var item in Workspaces.SelectMany(candidate => candidate.Sessions))
        {
            item.IsSelected = false;
        }
        _activeSessionItem = null;
        _activeSession = new SessionState
        {
            Title = "新会话",
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now
        };
        _state.ActiveWorkspaceId = _activeWorkspace.Id;
        _state.ActiveSessionId = null;
        SessionTitle = "新会话";
        Messages.Clear();
        Composer = string.Empty;
        ClearAttachments();
        OnPropertyChanged(nameof(WorkspaceName));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(WorkspacePath));
        OnPropertyChanged(nameof(CanRunReview));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(UsageText));
        OnPropertyChanged(nameof(HasUsageText));
    }

    private void EnsureSessionPersisted()
    {
        if (_activeSession is null)
        {
            StartDraftSession();
        }
        if (_activeWorkspace.State.Sessions.Contains(_activeSession!))
        {
            return;
        }

        _activeWorkspace.State.Sessions.Add(_activeSession!);
        var item = new SessionItemViewModel(_activeWorkspace, _activeSession!);
        _activeWorkspace.Sessions.Insert(0, item);
        _activeSessionItem = item;
        _state.ActiveSessionId = _activeSession!.Id;
        item.IsSelected = true;
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowNoSessions));
        OnPropertyChanged(nameof(SavedSessionCount));
    }

    private void LoadMessages(SessionState session)
    {
        Messages.Clear();
        foreach (var message in session.Messages)
        {
            Messages.Add(ChatItemViewModel.FromStored(message, session.UpdatedAt));
        }

        GroupToolMessages();
    }

    /// <summary>
    /// Codex groups only repeated calls: a single tool call renders on its own row,
    /// two or more consecutive calls of the same kind collapse into a 运行了命令 group.
    /// </summary>
    private void AddToolItem(ChatItemViewModel tool)
    {
        var kind = tool.EffectiveKindLabel();
        if (Messages.Count > 0
            && Messages[^1] is { IsToolGroup: true } group
            && string.Equals(group.GroupKind, kind, StringComparison.Ordinal))
        {
            group.AddChild(tool);
            return;
        }

        if (Messages.Count > 0
            && Messages[^1] is { IsTool: true } previous
            && !previous.IsToolGroup
            && string.Equals(previous.EffectiveKindLabel(), kind, StringComparison.Ordinal))
        {
            var promoted = ChatItemViewModel.ToolGroup(kind);
            promoted.AddChild(previous);
            promoted.AddChild(tool);
            Messages[^1] = promoted;
            return;
        }

        Messages.Add(tool);
    }

    private void GroupToolMessages()
    {
        if (Messages.Count == 0)
        {
            return;
        }

        var flat = Messages.ToList();
        Messages.Clear();
        var buffer = new List<ChatItemViewModel>();
        foreach (var item in flat)
        {
            if (item.IsTool)
            {
                if (buffer.Count > 0 && !string.Equals(buffer[0].EffectiveKindLabel(), item.EffectiveKindLabel(), StringComparison.Ordinal))
                {
                    FlushToolBuffer(buffer);
                }

                buffer.Add(item);
            }
            else
            {
                FlushToolBuffer(buffer);
                Messages.Add(item);
            }
        }

        FlushToolBuffer(buffer);
    }

    private void FlushToolBuffer(List<ChatItemViewModel> buffer)
    {
        if (buffer.Count == 0)
        {
            return;
        }

        if (buffer.Count == 1)
        {
            Messages.Add(buffer[0]);
        }
        else
        {
            var group = ChatItemViewModel.ToolGroup(buffer[0].EffectiveKindLabel());
            foreach (var item in buffer)
            {
                group.AddChild(item);
            }

            Messages.Add(group);
        }

        buffer.Clear();
    }

    private void SyncStoredMessages()
    {
        if (_activeSession is null)
        {
            return;
        }
        _activeSession.Messages = Messages
            .SelectMany(item => (IEnumerable<ChatItemViewModel>)(item.IsToolGroup ? item.Children : new[] { item }))
            .Select(item => item.ToStored())
            .ToList();
    }

    private void UpdateSessionOrder()
    {
        if (_activeSession is null || _activeSessionItem is null)
        {
            return;
        }
        _activeSessionItem.Title = _activeSession.Title;
        _activeSessionItem.RefreshTitle();
        _activeSessionItem.RefreshTime();
        var ordered = _activeWorkspace.Sessions
            .OrderByDescending(item => item.State.UpdatedAt)
            .ToList();
        for (var index = 0; index < ordered.Count; index++)
        {
            var oldIndex = _activeWorkspace.Sessions.IndexOf(ordered[index]);
            if (oldIndex != index)
            {
                _activeWorkspace.Sessions.Move(oldIndex, index);
            }
        }
    }

    private ChatItemViewModel EnsureReasoningItem()
    {
        if (_activeReasoning is not null)
        {
            return _activeReasoning;
        }
        _activeReasoning = ChatItemViewModel.Reasoning();
        _activeReasoning.Start(string.Empty);
        Messages.Add(_activeReasoning);
        return _activeReasoning;
    }

    private ChatItemViewModel EnsureAssistantItem()
    {
        if (_activeAssistant is not null)
        {
            return _activeAssistant;
        }
        _activeAssistant = ChatItemViewModel.Assistant(string.Empty);
        _activeAssistant.Start("生成中");
        Messages.Add(_activeAssistant);
        return _activeAssistant;
    }

    private void AddError(string message)
    {
        ErrorMessage = message;
        Messages.Add(ChatItemViewModel.Error(message));
    }

    private string ToUserFacingError(Exception error)
    {
        if (error is FileNotFoundException)
        {
            return "未找到 kl-core 可执行文件，请先构建 Core 或设置 KL_CORE_PATH。";
        }
        if (error is JsonRpcException rpc)
        {
            return $"KL Core 返回错误：{rpc.Message}";
        }
        if (error is IOException)
        {
            return $"与 KL Core 的连接中断：{error.Message}";
        }
        return error.Message;
    }

    private static string TitleFromPrompt(string prompt)
    {
        var singleLine = string.Join(" ", prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return singleLine.Length <= 36 ? singleLine : singleLine[..35] + "…";
    }

    private void ApplySearch()
    {
        var query = SearchText.Trim();
        foreach (var workspace in Workspaces)
        {
            workspace.ApplySearch(query);
        }
        RefreshRecentSessions();
        OnPropertyChanged(nameof(ShowNoSessions));
        OnPropertyChanged(nameof(ShowNoSearchResults));
    }

    /// <summary>Groups the Core provider catalog into the Usage → provider card.</summary>
    private void ApplyProviderCards(
        IReadOnlyList<CoreModelOption> options,
        IReadOnlyList<string> models,
        string provider,
        string activeProvider,
        bool keyConfigured,
        IReadOnlyList<CoreProviderProtocols> protocols)
    {
        if (options.Count == 0 && provider.Length > 0)
        {
            options = models
                .Select(model => new CoreModelOption(provider, model, true, keyConfigured))
                .ToList();
        }

        var profiles = _state.ProviderProfiles ??= [];
        var rows = options
            .Where(option => !string.IsNullOrWhiteSpace(option.Provider))
            .GroupBy(option => option.Provider, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var id = group.Key;
                profiles.TryGetValue(id.ToLowerInvariant(), out var profile);
                var protocolUrls = protocols
                    .FirstOrDefault(entry =>
                        string.Equals(entry.Provider, id, StringComparison.OrdinalIgnoreCase))
                    ?.AsMap();
                return new ProviderCardRow(
                    id,
                    ProviderText(profile?.DisplayName, ProviderDisplayName(id)),
                    ProviderText(profile?.Note, "已接入的模型提供商"),
                    ProviderText(profile?.Website, string.Empty),
                    string.Equals(id, activeProvider, StringComparison.OrdinalIgnoreCase),
                    true,
                    group.Any(option => option.KeyConfigured),
                    group.ToList(),
                    protocolUrls);
            })
            .ToList();

        // A provider can exist with no models yet; keep it visible from its protocol URLs.
        foreach (var entry in protocols)
        {
            if (string.IsNullOrWhiteSpace(entry.Provider)
                || rows.Any(row => string.Equals(row.Id, entry.Provider, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            profiles.TryGetValue(entry.Provider.ToLowerInvariant(), out var profile);
            rows.Add(new ProviderCardRow(
                entry.Provider,
                ProviderText(profile?.DisplayName, ProviderDisplayName(entry.Provider)),
                ProviderText(profile?.Note, "已接入的模型提供商"),
                ProviderText(profile?.Website, string.Empty),
                isDefault: false,
                enabled: true,
                keyConfigured: false,
                models: [],
                entry.AsMap()));
        }

        ApplyProviderRows(rows
            .OrderByDescending(row => row.IsDefault)
            .ThenByDescending(row => row.Enabled)
            .ThenBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList());
    }

    /// <summary>Applies provider rows in place so the ten-second refresh does not flicker.</summary>
    private void ApplyProviderRows(IReadOnlyList<ProviderCardRow> rows)
    {
        foreach (var row in rows)
        {
            var current = ProviderCards.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, row.Id, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                ProviderCards.Add(row);
                continue;
            }

            current.Name = row.Name;
            current.Note = row.Note;
            current.Website = row.Website;
            current.Enabled = row.Enabled;
            current.SetKeyConfigured(row.KeyConfigured);
            current.SetModels(row.Models);
            current.SetProtocols(row.Protocols);
        }

        foreach (var stale in ProviderCards
            .Where(card => !rows.Any(row =>
                string.Equals(row.Id, card.Id, StringComparison.OrdinalIgnoreCase)))
            .ToList())
        {
            ProviderCards.Remove(stale);
        }

        RemoveDuplicateProviderCards();
        EnsureUniqueProviderNames();
        NotifyProviderCardsChanged();
    }

    private void RemoveDuplicateProviderCards()
    {
        foreach (var duplicate in ProviderCards
            .GroupBy(card => card.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Skip(1))
            .ToList())
        {
            ProviderCards.Remove(duplicate);
        }
    }

    private void EnsureUniqueProviderNames()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in ProviderCards
            .OrderByDescending(card => card.IsDefault)
            .ThenByDescending(card => card.Enabled)
            .ThenBy(card => card.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(card => card.Id, StringComparer.OrdinalIgnoreCase)
            .ToList())
        {
            var baseName = string.IsNullOrWhiteSpace(card.Name) ? card.Id : card.Name.Trim();
            if (used.Add(baseName))
            {
                card.Name = baseName;
                continue;
            }

            var suffix = " copy";
            var candidate = baseName + suffix;
            while (!used.Add(candidate))
            {
                suffix += "copy";
                candidate = baseName + suffix;
            }

            card.Name = candidate;
        }
    }

    private void NotifyProviderCardsChanged()
    {
        OnPropertyChanged(nameof(HasProviderCards));
        OnPropertyChanged(nameof(ShowNoProviderCards));
        OnPropertyChanged(nameof(ProviderSummaryText));
    }

    private void HydrateProviderCache()
    {
        var cached = _providerCacheStore.Load();
        if (cached is null || cached.Providers.Count == 0)
        {
            return;
        }

        var profiles = _state.ProviderProfiles ??= [];
        var rows = new List<ProviderCardRow>();
        foreach (var entry in cached.Providers)
        {
            if (entry is null)
            {
                continue;
            }

            var id = entry.Id?.Trim() ?? string.Empty;
            if (id.Length == 0)
            {
                continue;
            }

            profiles.TryGetValue(id.ToLowerInvariant(), out var profile);
            rows.Add(new ProviderCardRow(
                id,
                string.IsNullOrWhiteSpace(profile?.DisplayName)
                    ? entry.Name ?? string.Empty
                    : profile.DisplayName.Trim(),
                profile is null ? entry.Note ?? string.Empty : profile.Note,
                profile is null ? entry.Website ?? string.Empty : profile.Website,
                entry.IsDefault,
                entry.Enabled,
                entry.KeyConfigured,
                entry.Models ?? [],
                entry.Protocols));
        }

        if (rows.Count > 0)
        {
            ApplyProviderRows(rows);
        }
    }

    /// <summary>Persists the cards the page shows; the file never contains API keys.</summary>
    private void SaveProviderCache()
    {
        RemoveDuplicateProviderCards();
        EnsureUniqueProviderNames();
        _providerCacheStore.Save(ProviderCards.Select(row => new ProviderCacheEntry
        {
            Id = row.Id,
            Name = row.Name,
            Note = row.Note,
            Website = row.Website,
            IsDefault = row.IsDefault,
            Enabled = row.Enabled,
            KeyConfigured = row.KeyConfigured,
            Models = row.Models.ToList(),
            Protocols = new Dictionary<string, string>(row.Protocols, StringComparer.OrdinalIgnoreCase)
        }).ToList());
    }

    private static string ProviderText(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    /// <summary>Adds disabled providers that the live router does not list.</summary>
    private void MergeConfiguredProviders(JsonElement configResult)
    {
        if (configResult.ValueKind != JsonValueKind.Object
            || !configResult.TryGetProperty("config", out var config)
            || !config.TryGetProperty("providers", out var providers)
            || providers.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var profiles = _state.ProviderProfiles ??= [];
        var added = false;
        foreach (var provider in providers.EnumerateObject())
        {
            var id = provider.Name;
            if (id.Length == 0
                || ProviderCards.Any(row => string.Equals(row.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var enabled = provider.Value.ValueKind == JsonValueKind.Object
                && provider.Value.TryGetProperty("enabled", out var enabledValue)
                && enabledValue.ValueKind == JsonValueKind.True;
            profiles.TryGetValue(id.ToLowerInvariant(), out var profile);
            ProviderCards.Add(new ProviderCardRow(
                id,
                ProviderText(profile?.DisplayName, ProviderDisplayName(id)),
                ProviderText(profile?.Note, "已接入的模型提供商"),
                ProviderText(profile?.Website, string.Empty),
                isDefault: false,
                enabled,
                keyConfigured: false,
                models: []));
            added = true;
        }

        if (added)
        {
            NotifyProviderCardsChanged();
        }
    }

    private static string ProviderDisplayName(string id) => id.ToLowerInvariant() switch
    {
        "openai" => "OpenAI",
        "anthropic" => "Anthropic",
        "deepseek" => "DeepSeek",
        "opencode" => "OpenCode",
        "openrouter" => "OpenRouter",
        "google" or "gemini" => "Google Gemini",
        "xai" => "xAI",
        "ollama" => "Ollama",
        "fake" => "Scripted / Fake",
        _ => id
    };

    private void SaveState(bool silent = false)
    {
        if (_stateImported)
        {
            RefreshRecentSessions();
            return;
        }

        try
        {
            _stateStore.Save(_state);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (!silent)
            {
                ErrorMessage = $"无法保存本地状态：{error.Message}";
            }
        }

        RefreshRecentSessions();
    }
}

/// <summary>One Codex-style pinned shortcut rendered below the rail divider.</summary>
public sealed class PinnedRailItemViewModel(bool isProject, string label) : INotifyPropertyChanged
{
    private bool _isActive;

    public bool IsProject { get; } = isProject;

    public string Label { get; } = label;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
            {
                return;
            }

            _isActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
/// <summary>One row in the Codex-style notifications pane.</summary>
public sealed class NotificationItemViewModel
{
    public NotificationItemViewModel(SessionItemViewModel session, string groupLabel, bool showGroup)
    {
        Session = session;
        GroupLabel = groupLabel;
        ShowGroup = showGroup;
    }

    public SessionItemViewModel Session { get; }
    public string GroupLabel { get; }
    public bool ShowGroup { get; }
    public string Title => Session.Title;
    public string WorkspaceName => Session.Workspace.Name;
    public string RelativeLabel => Session.RelativeLabel;
    public string Preview => Session.State.Messages.LastOrDefault()?.Text ?? string.Empty;
}

/// <summary>One row in the Worktrees settings page.</summary>
public sealed record WorktreeRow(string Path, string Branch, string CurrentLabel)
{
    public bool IsCurrent => CurrentLabel.Length > 0;
}

/// <summary>One finding rendered on the code-review settings page.</summary>
public sealed record ReviewFindingRow(
    string Severity,
    string Location,
    string Rule,
    string Evidence,
    string Recommendation)
{
    public bool IsHigh => Severity is "critical" or "high";
}

/// <summary>A follow-up prompt waiting for the current turn to finish.</summary>
public sealed record QueuedTurn(string Prompt, IReadOnlyList<AttachmentItemViewModel> Attachments);

/// <summary>One session row on the usage settings page.</summary>
public sealed record UsageSessionRow(string Title, string Tokens, string Cost);

/// <summary>One bar in the local usage chart (height in pixels).</summary>
public sealed record UsageBarRow(double Height);

/// <summary>One editable keyboard shortcut on the shortcuts settings page.</summary>
public sealed class ShortcutRowViewModel : ObservableObject
{
    private string _gesture;
    private bool _isVisible = true;

    public ShortcutRowViewModel(
        string actionId,
        string title,
        string description,
        string defaultGesture,
        string gesture)
    {
        ActionId = actionId;
        Title = title;
        Description = description;
        DefaultGesture = defaultGesture;
        _gesture = gesture;
    }

    public string ActionId { get; }
    public string Title { get; }
    public string Description { get; }
    public string DefaultGesture { get; }

    public string Gesture
    {
        get => _gesture;
        set
        {
            if (SetProperty(ref _gesture, value))
            {
                OnPropertyChanged(nameof(HasGesture));
                OnPropertyChanged(nameof(GestureLabel));
            }
        }
    }

    public bool HasGesture => Gesture.Length > 0;
    public string GestureLabel => HasGesture ? Gesture : "未分配";

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, value);
    }
}

/// <summary>One selectable reasoning level in the model form.</summary>
public sealed class ReasoningLevelChoice : ObservableObject
{
    private readonly Action _changed;
    private bool _isEnabled;

    public ReasoningLevelChoice(string value, string label, bool isEnabled, Action changed)
    {
        Value = value;
        Label = label;
        _isEnabled = isEnabled;
        _changed = changed;
    }

    public string Value { get; }
    public string Label { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                _changed();
            }
        }
    }
}

/// <summary>One model row on a provider's detail page.</summary>
public sealed class ProviderModelRow : ObservableObject
{
    private bool _enabled;

    public ProviderModelRow(
        string id,
        string protocol,
        string url,
        int contextWindow,
        double compactThreshold,
        int maxOutputTokens,
        bool enabled = true,
        string reasoningEffort = "default",
        IReadOnlyList<string>? reasoningEfforts = null,
        string name = "")
    {
        Id = id;
        Name = name;
        Protocol = protocol;
        Url = url;
        ContextWindow = contextWindow;
        CompactThreshold = compactThreshold;
        MaxOutputTokens = maxOutputTokens;
        ReasoningEffort = reasoningEffort;
        ReasoningEfforts = reasoningEfforts ?? Array.Empty<string>();
        _enabled = enabled;
    }

    public string Id { get; }
    public string Name { get; }
    public string Protocol { get; }
    public string Url { get; }
    public int ContextWindow { get; }
    public double CompactThreshold { get; }
    public int MaxOutputTokens { get; }
    public string ReasoningEffort { get; }
    public IReadOnlyList<string> ReasoningEfforts { get; }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;

    public string ProtocolLabel => Protocol switch
    {
        "anthropic" => "Anthropic",
        "openai-responses" => "OpenAI Responses",
        _ => "OpenAI Chat"
    };

    public string ReasoningLabel => ReasoningEffort switch
    {
        "none" => "思考 none",
        "minimal" => "思考 minimal",
        "low" => "思考 low",
        "medium" => "思考 medium",
        "high" => "思考 high",
        "xhigh" => "思考 xhigh",
        "max" => "思考 max",
        "ultra" => "思考 ultra",
        _ => "思考 默认"
    };

    public string Summary =>
        $"{ProtocolLabel} · {ReasoningLabel} · {FormatTokens(ContextWindow)} 上下文 · {FormatTokens(MaxOutputTokens)} 输出";

    private static string FormatTokens(int value) =>
        value >= 1000
            ? $"{value / 1000.0:0.#}k"
            : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

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

/// <summary>One MCP tool row on the integration settings page.</summary>
public sealed record McpToolRow(string Name, string Description);

/// <summary>One memory entry on the memory settings page.</summary>
public sealed record MemoryEntryRow(string Name, string SizeLabel);

/// <summary>One label/value row on the config settings page.</summary>
public sealed record ConfigValueRow(string Label, string Value);

/// <summary>One skill row on the plugins page.</summary>
public sealed record SkillRow(string Name, string Description);
