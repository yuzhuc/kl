using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Runtime.CompilerServices;
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
    Models,
    Data,
    About
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

    /// <summary>Short local time label shown under user messages (Codex-style).</summary>
    public string TimestampLabel
    {
        get => _timestampLabel;
        private set
        {
            if (SetProperty(ref _timestampLabel, value))
            {
                OnPropertyChanged(nameof(HasTimestamp));
            }
        }
    }

    public bool HasTimestamp => !string.IsNullOrWhiteSpace(TimestampLabel);

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
        var local = timestamp.ToLocalTime();
        if (local.Date == DateTimeOffset.Now.Date)
        {
            return local.ToString("HH:mm");
        }

        string[] weekdays = ["日", "一", "二", "三", "四", "五", "六"];
        return $"星期{weekdays[(int)local.DayOfWeek]} {local:HH:mm}";
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
                : timestamp.Value.ToLocalTime().ToString("HH:mm"));

    public static ChatItemViewModel Assistant(string text) =>
        new(ChatItemKind.Assistant, "kl", text);

    public static ChatItemViewModel Reasoning(string text = "") =>
        new(ChatItemKind.Reasoning, "思考", text);

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

    public static ChatItemViewModel FromStored(StoredMessage message)
    {
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
            message.Timestamp,
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

public sealed class PluginItemViewModel
{
    public PluginItemViewModel(LocalPluginInfo plugin)
    {
        Name = plugin.Name;
        Version = plugin.Version;
        Description = plugin.Description;
        ManifestPath = plugin.ManifestPath;
    }

    public string Name { get; }
    public string Version { get; }
    public string Description { get; }
    public string ManifestPath { get; }
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
public sealed class PaletteFileGroupViewModel
{
    public PaletteFileGroupViewModel(string relativePath, IReadOnlyList<PaletteLineMatch> lines)
    {
        RelativePath = relativePath;
        Lines = lines.Select(line => new PaletteLineViewModel(relativePath, line)).ToList();
    }

    public string RelativePath { get; }
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
    public bool HasLines => Lines.Count > 0;
}

/// <summary>One matching line under a file group in the palette.</summary>
public sealed class PaletteLineViewModel
{
    public PaletteLineViewModel(string relativePath, PaletteLineMatch match)
    {
        RelativePath = relativePath;
        LineNumber = match.LineNumber;
        Text = match.Text;
    }

    public string RelativePath { get; }
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
    private readonly StateStore _stateStore;
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
    private bool _isArchivedExpanded;
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
    private string _pluginStatus = string.Empty;
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
        Action<Action>? dispatcher = null)
    {
        _stateStore = stateStore ?? new StateStore();
        _pluginCatalogService = pluginCatalogService ?? new LocalPluginCatalogService();
        _core = coreSessionService ?? new UnavailableCoreSessionService();
        _dispatch = dispatcher ?? (static action => action());
        _core.EventReceived += OnCoreEventReceived;
        _state = _stateStore.Load();
        NormalizeState();
        RefreshPinnedRailItems();
        BuildWorkspaceItems();
        _activeWorkspace = Workspaces.First(workspace => workspace.Id == _state.ActiveWorkspaceId);
        _activeWorkspace.IsSelected = true;
        if (TryFindSession(_state.ActiveSessionId, out var activeWorkspace, out var activeSession))
        {
            SelectSession(activeWorkspace, activeSession.State);
        }
        else
        {
            StartDraftSession();
        }

        RefreshSettingsDraft();
        RefreshPlugins();
        BuildAutomationItems();
        foreach (var item in _state.LibraryItems)
        {
            LibraryItems.Add(new LibraryItemViewModel(item));
        }

        SaveState(silent: true);

        Messages.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasConversation));
            OnPropertyChanged(nameof(IsConversationEmpty));
            OnPropertyChanged(nameof(ShowConversationHeader));
            OnPropertyChanged(nameof(ComposerPlaceholder));
        };

        _ = InitializeCoreAsync();
    }

    public ObservableCollection<WorkspaceItemViewModel> Workspaces { get; } = [];
    public ObservableCollection<ChatItemViewModel> Messages { get; } = [];
    public ObservableCollection<AttachmentItemViewModel> PendingAttachments { get; } = [];
    public ObservableCollection<PluginItemViewModel> Plugins { get; } = [];
    public ObservableCollection<SessionItemViewModel> RecentSessions { get; } = [];
    public ObservableCollection<SessionItemViewModel> PinnedSessions { get; } = [];
    public ObservableCollection<SessionItemViewModel> ArchivedSessions { get; } = [];
    public ObservableCollection<PinnedRailItemViewModel> PinnedRailItems { get; } = [];
    public ObservableCollection<AutomationItemViewModel> Automations { get; } = [];
    public ObservableCollection<LibraryItemViewModel> LibraryItems { get; } = [];
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
    public IReadOnlyList<int> ContentFontSizes { get; } = [13, 14, 15, 16];
    public string VersionLabel => "0.3.0-gui.1";
    public AppState State => _state;
    public SessionItemViewModel? ActiveSessionItem => _activeSessionItem;

    public bool IsEmpty => Messages.Count == 0;
    public bool HasConversation => !IsEmpty;
    public bool IsConversationEmpty => IsEmpty;
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

    public bool IsArchivedExpanded
    {
        get => _isArchivedExpanded;
        private set => SetProperty(ref _isArchivedExpanded, value);
    }

    public void ToggleProjectSection()
    {
        IsProjectSectionExpanded = !IsProjectSectionExpanded;
        SaveState();
    }

    public void ToggleArchived()
    {
        IsArchivedExpanded = !IsArchivedExpanded;
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

    public string ComposerPlaceholder => HasConversation
        ? IsChatMode ? "和 kl 一起聊天" : "和 kl 一起工作"
        : "随心输入";

    /// <summary>Codex-style empty-state headline that follows the selected mode.</summary>
    public string EmptyStateTitle => IsChatMode ? "我们要做什么?" : "我们要构建什么?";
    public bool IsSettingsOpen
    {
        get => _isSettingsOpen;
        private set => SetProperty(ref _isSettingsOpen, value);
    }
    public bool IsSettingsGeneral => _settingsPane == SettingsPane.General;
    public bool IsSettingsModels => _settingsPane == SettingsPane.Models;
    public bool IsSettingsData => _settingsPane == SettingsPane.Data;
    public bool IsSettingsAbout => _settingsPane == SettingsPane.About;
    public string SettingsPaneTitle => _settingsPane switch
    {
        SettingsPane.Models => "模型",
        SettingsPane.Data => "本地数据",
        SettingsPane.About => "关于与快捷键",
        _ => "通用设置"
    };
    public string SettingsPaneSubtitle => _settingsPane switch
    {
        SettingsPane.Models => "查看 KL Core 运行时、模型与连接状态。",
        SettingsPane.Data => "GUI 本地状态保存在 ~/.kl/gui；会话与消息真值由 KL Core 保存在 ~/.kl/sessions。",
        SettingsPane.About => "版本、隐私和常用快捷键。",
        _ => "调整权限模式和当前工作方式。"
    };
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
        private set => SetProperty(ref _isRenameDialogOpen, value);
    }
    public bool IsDeleteDialogOpen
    {
        get => _isDeleteDialogOpen;
        private set => SetProperty(ref _isDeleteDialogOpen, value);
    }

    private bool _isCreateProjectDialogOpen;
    private string _projectNameDraft = string.Empty;
    private string _projectSourcePath = string.Empty;

    public bool IsCreateProjectDialogOpen
    {
        get => _isCreateProjectDialogOpen;
        private set => SetProperty(ref _isCreateProjectDialogOpen, value);
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
            IsExpanded = true
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
        private set => SetProperty(ref _isAutomationDialogOpen, value);
    }

    public bool IsAutomationDeleteDialogOpen
    {
        get => _isAutomationDeleteDialogOpen;
        private set => SetProperty(ref _isAutomationDeleteDialogOpen, value);
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
    public bool ShowNoPlugins => Plugins.Count == 0;
    public string PluginDirectoryPath => _pluginCatalogService.DirectoryPath;
    public string PluginStatus
    {
        get => _pluginStatus;
        private set => SetProperty(ref _pluginStatus, value);
    }
    public bool IsWorkMode => _state.InteractionMode != "chat";
    public bool IsChatMode => !IsWorkMode;
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
    public string SectionTitle => IsLibraryPage ? "资料库" : IsProjectsPage ? "项目" : IsPluginsPage ? "插件" : IsAutomationPage ? "自动化任务" : IsSettingsPage ? "设置" : "聊天";
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

    public string Composer
    {
        get => _composer;
        set
        {
            if (SetProperty(ref _composer, value))
            {
                OnPropertyChanged(nameof(CanSend));
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

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanSend));
                OnPropertyChanged(nameof(CanSwitchSession));
                OnPropertyChanged(nameof(CanCancel));
            }
        }
    }

    public bool CanSend => !IsBusy
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
        OnPropertyChanged(nameof(IsAutomationPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(ShowConversationHeader));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(SectionTitle));
        UpdatePinnedRailActiveState();
        if (section == AppSection.Plugins)
        {
            RefreshPlugins();
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
                            Array.Empty<PaletteLineMatch>()));
                    }

                    PaletteTextMatches.Clear();
                    foreach (var (relativePath, lines) in textMatches)
                    {
                        PaletteTextMatches.Add(new PaletteFileGroupViewModel(relativePath, lines));
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
        var list = Workspaces
            .SelectMany(workspace => workspace.Sessions)
            .Where(session => !session.IsArchived)
            .Where(session => query.Length == 0 || session.Matches(query))
            .OrderByDescending(session => session.State.UpdatedAt)
            .Take(8)
            .ToList();
        SyncList(PaletteSessions, list);
        OnPropertyChanged(nameof(ShowPaletteEmpty));
        OnPropertyChanged(nameof(PaletteEmptyText));
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

    public void ArchiveSession(SessionItemViewModel session)
    {
        session.IsArchived = true;
        SaveState();
    }

    public void UnarchiveSession(SessionItemViewModel session)
    {
        session.IsArchived = false;
        SaveState();
    }

    public void ToggleCodeReviewPinned()
    {
        _state.IsCodeReviewPinned = !_state.IsCodeReviewPinned;
        SaveState();
        OnPropertyChanged(nameof(IsCodeReviewPinned));
        RefreshPinnedRailItems();
    }

    public void StartCodeReviewDraft()
    {
        Navigate(AppSection.Chat);
        _ = CloseCoreSessionQuietlyAsync();
        StartDraftSession();
        Composer = "请审查当前项目的代码变更，重点检查潜在 bug、回归风险、安全问题和缺失测试。";
        _isCodeReviewDraftActive = true;
        UpdatePinnedRailActiveState();
    }

    public void OpenSettings(SettingsPane pane = SettingsPane.General)
    {
        RefreshSettingsDraft();
        SelectSettingsPane(pane);
        IsSettingsOpen = true;
    }

    public void CloseSettings() => IsSettingsOpen = false;

    public void SelectSettingsPane(SettingsPane pane)
    {
        _settingsPane = pane;
        OnPropertyChanged(nameof(IsSettingsGeneral));
        OnPropertyChanged(nameof(IsSettingsModels));
        OnPropertyChanged(nameof(IsSettingsData));
        OnPropertyChanged(nameof(IsSettingsAbout));
        OnPropertyChanged(nameof(SettingsPaneTitle));
        OnPropertyChanged(nameof(SettingsPaneSubtitle));
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
        OnPropertyChanged(nameof(IsWorkMode));
        OnPropertyChanged(nameof(IsChatMode));
        OnPropertyChanged(nameof(ActiveModeLabel));
        SaveState();
    }

    public async Task SelectWorkspaceAsync(WorkspaceItemViewModel workspace)
    {
        if (IsBusy || (workspace.Id == _activeWorkspace.Id && _activeSession is null))
        {
            return;
        }

        _activeWorkspace.IsSelected = false;
        _activeWorkspace = workspace;
        _activeWorkspace.IsSelected = true;
        _activeWorkspace.IsExpanded = true;
        _state.ActiveWorkspaceId = workspace.Id;
        OnPropertyChanged(nameof(WorkspaceName));
        OnPropertyChanged(nameof(WorkspacePath));

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
                _activeWorkspace.IsExpanded = true;
                _state.ActiveWorkspaceId = next.Id;
                OnPropertyChanged(nameof(WorkspaceName));
                OnPropertyChanged(nameof(WorkspacePath));
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
            IsExpanded = true
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
        if (!CanSend)
        {
            return;
        }

        var prompt = Composer.Trim();
        var attachments = PendingAttachments.ToList();
        var modelPrompt = BuildModelPrompt(prompt, attachments);
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

        var isFirstMessage = session.Messages.Count == 0;
        if (isFirstMessage)
        {
            session.Title = prompt.Length == 0 ? "附件会话" : TitleFromPrompt(prompt);
        }
        SessionTitle = session.Title;
        session.UpdatedAt = DateTimeOffset.Now;
        _activeSessionItem?.RefreshTitle();
        _activeSessionItem?.RefreshTime();

        var userItem = ChatItemViewModel.User(displayPrompt, modelPrompt, DateTimeOffset.Now);
        Messages.Add(userItem);
        SyncStoredMessages();
        UpdateSessionOrder();
        SaveState();

        IsBusy = true;
        _turnCancellation = new CancellationTokenSource();
        try
        {
            var outcome = await _core.RunTurnAsync(modelPrompt, _turnCancellation.Token).ConfigureAwait(true);
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
        }
        finally
        {
            if (IsBusy)
            {
                FinishTurn("完成");
            }
            _turnCancellation?.Dispose();
            _turnCancellation = null;
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
            }
            CloseDialogs();
            SaveState();
            return;
        }

        if (_dialogTarget is null)
        {
            return;
        }

        _dialogTarget.Title = title;
        _dialogTarget.RefreshTime();
        if (_dialogTarget.Id == _activeSession?.Id)
        {
            SessionTitle = title;
        }
        CloseDialogs();
        UpdateSessionOrder();
        SaveState();
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
        target.Workspace.State.Sessions.Remove(target.State);
        target.Workspace.Sessions.Remove(target);
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
    }

    public void CloseDialogs()
    {
        _dialogTarget = null;
        _renameWorkspaceTarget = null;
        IsRenameDialogOpen = false;
        IsDeleteDialogOpen = false;
        IsCreateProjectDialogOpen = false;
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
        _state.ContentFontSize = SettingsContentFontSize;
        OnPropertyChanged(nameof(ContentFontSize));
        OnPropertyChanged(nameof(PermissionMode));
        OnPropertyChanged(nameof(IsPermissionFull));
        OnPropertyChanged(nameof(IsPermissionAuto));
        OnPropertyChanged(nameof(IsPermissionAsk));
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
        SaveState();
        RaisePermissionModeChanged();
        _ = ApplyPermissionModeAsync(CancellationToken.None);
    }

    private void RaisePermissionModeChanged()
    {
        OnPropertyChanged(nameof(PermissionMode));
        OnPropertyChanged(nameof(IsPermissionFull));
        OnPropertyChanged(nameof(IsPermissionAuto));
        OnPropertyChanged(nameof(IsPermissionAsk));
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
            await _core.RequestAsync(
                Protocol.ProviderSetApiKey,
                new { id = provider, key },
                CancellationToken.None).ConfigureAwait(true);
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
        }
        catch (Exception error) when (error is JsonRpcException
            or IOException
            or InvalidOperationException
            or FileNotFoundException)
        {
            DoctorSummary = $"诊断失败：{error.Message}";
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
        SettingsPermissionMode = "完全访问";
        SettingsContentFontSize = 14;
        SetSettingsStatus("已恢复默认值，点击保存后生效。", isError: false);
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
        private set => SetProperty(ref _doctorSummary, value);
    }

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
        SaveState();
    }

    public void DeleteLibraryItem(LibraryItemViewModel item)
    {
        _state.LibraryItems.Remove(item.State);
        LibraryItems.Remove(item);
        OnPropertyChanged(nameof(HasLibraryItems));
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
            RefreshPlugins();
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ErrorMessage = $"导入插件失败：{error.Message}";
            return false;
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

    public void RefreshPlugins()
    {
        Plugins.Clear();
        try
        {
            foreach (var plugin in _pluginCatalogService.Load())
            {
                Plugins.Add(new PluginItemViewModel(plugin));
            }
            PluginStatus = Plugins.Count == 0
                ? "尚未发现本地插件。将插件目录放入上方路径后点击刷新。"
                : $"已发现 {Plugins.Count} 个本地插件。";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            PluginStatus = $"无法读取插件目录：{error.Message}";
        }
        OnPropertyChanged(nameof(HasPlugins));
        OnPropertyChanged(nameof(ShowNoPlugins));
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
        _state.ApiKey ??= string.Empty;
        _state.ApiKeyEnvironmentVariable = _state.ApiKeyEnvironmentVariable?.Trim() ?? string.Empty;
        if (string.Equals(_state.PermissionMode, "按需确认", StringComparison.Ordinal))
        {
            // The old two-mode label maps onto Codex's "请求批准".
            _state.PermissionMode = "请求批准";
        }

        _state.PermissionMode = SupportedPermissionModes.Contains(_state.PermissionMode)
            ? _state.PermissionMode
            : "完全访问";
        _state.ContentFontSize = _state.ContentFontSize is >= 13 and <= 16 ? _state.ContentFontSize : 14;
        _state.InteractionMode = string.Equals(_state.InteractionMode, "chat", StringComparison.OrdinalIgnoreCase)
            ? "chat"
            : "work";
        _state.PinnedSort = string.Equals(_state.PinnedSort, "recent", StringComparison.OrdinalIgnoreCase)
            ? "recent"
            : "manual";
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
            Workspaces.Add(new WorkspaceItemViewModel(workspaceState));
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
        _activeWorkspace.IsExpanded = true;
        _activeSession = session;
        _activeSessionItem = workspace.Sessions.First(item => item.Id == session.Id);
        _activeSessionItem.IsSelected = true;
        _state.ActiveWorkspaceId = workspace.Id;
        _state.ActiveSessionId = session.Id;
        SessionTitle = session.Title;
        Composer = string.Empty;
        ClearAttachments();
        LoadMessages(session);
        OnPropertyChanged(nameof(WorkspaceName));
        OnPropertyChanged(nameof(WorkspacePath));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(ShowNoSessions));
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
        OnPropertyChanged(nameof(WorkspacePath));
        OnPropertyChanged(nameof(HeaderTitle));
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
            Messages.Add(ChatItemViewModel.FromStored(message));
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
        _activeReasoning.Start();
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

    private void SaveState(bool silent = false)
    {
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