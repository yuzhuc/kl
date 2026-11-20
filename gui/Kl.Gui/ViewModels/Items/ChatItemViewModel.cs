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
