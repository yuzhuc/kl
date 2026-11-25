using System.Globalization;
using System.Text.Json;
using Kl.Gui.Models;
using Avalonia;
using Kl.Gui.Services;
using Loc = Kl.Gui.Services.Localization;

namespace Kl.Gui.ViewModels;

public sealed class ChatItemViewModel : ObservableObject
{
    private string _text;
    private string _detail;
    private string _summary;
    private DateTimeOffset? _timestamp;
    private string _toolLabel;
    private string _toolKind = string.Empty;
    private string _command = string.Empty;
    private string _statusKind = string.Empty;
    private int _imageCount;
    private string _groupKind = string.Empty;
    private bool _groupExpanded;
    private bool _isRunning;
    private bool _isJumpHighlighted;
    private bool _isExpanded;
    private long? _seq;

    private ChatItemViewModel(
        ChatItemKind kind,
        string text = "",
        string detail = "",
        string summary = "",
        string statusKind = "",
        DateTimeOffset? timestamp = null,
        string toolLabel = "",
        string toolKind = "",
        string command = "")
    {
        Kind = kind;
        _text = text;
        _detail = detail;
        _summary = summary;
        _statusKind = NormalizeStatus(statusKind);
        _timestamp = timestamp;
        _toolLabel = toolLabel;
        _toolKind = toolKind;
        _command = command;
    }

    public ChatItemKind Kind { get; }
    public bool IsToolGroup => Kind == ChatItemKind.ToolGroup;
    public System.Collections.ObjectModel.ObservableCollection<ChatItemViewModel> Children { get; } = new();
    public string GroupKind => _groupKind;
    public string ToolKind => _toolKind;

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
            }
        }
    }

    /// <summary>Short local time label shown under user messages (Codex-style).</summary>
    public string TimestampLabel => _timestamp is { } value ? FormatTimestamp(value) : string.Empty;

    public bool HasTimestamp => !string.IsNullOrWhiteSpace(TimestampLabel);

    /// <summary>Centred turn divider above user messages, e.g. "10月19日周一 0:36".</summary>
    public string TurnLabel
    {
        get
        {
            if (!IsUser || _timestamp is not { } value)
            {
                return string.Empty;
            }

            var local = value.ToLocalTime();
            return Localization.IsEnglish
                ? local.ToString("ddd, MMM d H:mm", CultureInfo.GetCultureInfo("en-US")) // i18n-keep
                : local.ToString("M月d日ddd H:mm", CultureInfo.GetCultureInfo("zh-CN")); // i18n-keep
        }
    }

    public bool HasTurnLabel => !string.IsNullOrEmpty(TurnLabel);

    /// <summary>Row heading, derived from the semantic kind so a language switch re-renders it.</summary>
    public string Header => Kind switch
    {
        ChatItemKind.User => Localization.T("你"),
        ChatItemKind.Assistant => "kl",
        ChatItemKind.Reasoning => Localization.T(IsRunning ? Loc.T("思考中") : Loc.T("思考完成")),
        ChatItemKind.Error => Localization.T("请求失败"),
        ChatItemKind.ToolGroup => GroupHeader,
        _ => ToolHeaderText
    };

    private string GroupHeader => _groupKind switch
    {
        "Shell" => Localization.T("运行了命令"),
        "Image" => Localization.T("已查看图像"),
        "Read" => Localization.T("已读取文件"),
        "Search" => Localization.T("已搜索文件"),
        "Edit" => Localization.T("已编辑文件"),
        "Web" => Localization.T("已搜索网页"),
        _ => Localization.T("工具调用")
    };

    private string ToolHeaderText => _toolKind switch
    {
        "Image" => _imageCount > 0
            ? Localization.F("已查看 {0} 张图像", _imageCount)
            : Localization.T("已查看图像"),
        "Shell" => _command.Length > 0
            ? Localization.F("已运行 {0}", Compact(_command))
            : Localization.T("运行了命令"),
        "Edit" => Localization.T("已编辑文件"),
        "Read" => Localization.T("已读取文件"),
        "Search" => Localization.T("已搜索文件"),
        "Web" => Localization.T("已搜索网页"),
        "Todo" => Localization.T("已更新计划"),
        "Context" => _command.Length > 0
            ? Localization.F("上下文已压缩 · {0}", _command)
            : Localization.T("上下文已压缩"),
        _ => Localization.T("已调用工具")
    };

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

    public string StatusText => _statusKind switch
    {
        "running" => Localization.T("运行中"),
        "success" => Localization.T("成功"),
        "completed" => Localization.T("完成"),
        "failed" => Localization.T("失败"),
        "cancelled" => Localization.T("已停止"),
        "error" => Localization.T("错误"),
        "disconnected" => Localization.T("连接中断"),
        _ => string.Empty
    };

    public string ActivityPrefix => HasActivityCommand
        ? Localization.T("已运行")
        : _toolKind switch
        {
            "Image" => Localization.T("已查看图像"),
            "Edit" => Localization.T("已编辑文件"),
            "Read" => Localization.T("已读取文件"),
            "Search" => Localization.T("已搜索文件"),
            "Web" => Localization.T("已搜索网页"),
            "Todo" => Localization.T("已更新计划"),
            "Context" => Localization.T("上下文已压缩"),
            "Shell" => Localization.T("运行了命令"),
            _ => Localization.T("工具调用")
        };

    public string ActivityCommand => IsTool && _toolKind == "Shell" ? _command : string.Empty;

    public bool HasActivityCommand => ActivityCommand.Length > 0;

    /// <summary>Codex's expanded tool groups show the bare command, indented under the group.</summary>
    public string ActivityChildLabel => HasActivityCommand ? ActivityCommand : ActivityPrefix;

    public bool IsImageTool => (IsTool || IsToolGroup) && _toolKind == "Image";

    public bool IsPlainTool => IsTool && !IsImageTool;

    public string ToolLabel
    {
        get => _toolLabel;
        private set => SetProperty(ref _toolLabel, value);
    }

    public bool HasFailure => _statusKind is "failed" or "cancelled" or "error" or "disconnected";

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(ShowAssistantActions));
                OnPropertyChanged(nameof(Header));
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
            }
        }
    }

    public bool ShowExpanded => IsActivity && IsExpanded;

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

    public static string FormatTimestamp(DateTimeOffset timestamp) =>
        Localization.IsEnglish
            ? timestamp.ToLocalTime().ToString("MMM d, H:mm", CultureInfo.GetCultureInfo("en-US")) // i18n-keep
            : timestamp.ToLocalTime().ToString("M月d日 H:mm", CultureInfo.GetCultureInfo("zh-CN")); // i18n-keep

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

        if (lower.Contains("todo") || lower.Contains("plan"))
        {
            return "Todo";
        }

        return "Tool";
    }

    /// <summary>Pulls the real command/script out of JSON tool arguments so rows do not show raw JSON.</summary>
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

    public static int CountImageMentions(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 0;
        foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" })
        {
            var index = 0;
            while ((index = text.IndexOf(extension, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                index += extension.Length;
            }
        }

        return count;
    }

    private static string NormalizeStatus(string? status) => status switch
    {
        "running" or "运行中" or "运行中…" or "生成中" or "进行中" => "running", // i18n-keep
        "success" or "成功" => "success", // i18n-keep
        "completed" or "完成" => "completed", // i18n-keep
        "failed" or "失败" => "failed", // i18n-keep
        "cancelled" or "已停止" or "已取消" => "cancelled", // i18n-keep
        "error" or "错误" => "error", // i18n-keep
        "disconnected" or "连接中断" => "disconnected", // i18n-keep
        _ => string.Empty
    };

    private static string ToolKindFromHeader(string header)
    {
        if (header.Contains("图像")) return "Image"; // i18n-keep
        if (header.Contains("运行")) return "Shell"; // i18n-keep
        if (header.Contains("编辑")) return "Edit"; // i18n-keep
        if (header.Contains("读取")) return "Read"; // i18n-keep
        if (header.Contains("搜索")) return "Search"; // i18n-keep
        if (header.Contains("压缩")) return "Context"; // i18n-keep
        return "Tool";
    }

    public static ChatItemViewModel User(
        string text,
        string? apiText = null,
        DateTimeOffset? timestamp = null) =>
        new(
            ChatItemKind.User,
            text,
            detail: apiText ?? string.Empty,
            timestamp: timestamp);

    public static ChatItemViewModel Assistant(string text) =>
        new(ChatItemKind.Assistant, text);

    public static ChatItemViewModel Reasoning(string text = "") =>
        new(ChatItemKind.Reasoning, text);

    public static ChatItemViewModel Tool(
        string toolKind,
        string command = "",
        string output = "",
        string summary = "") =>
        new(
            ChatItemKind.Tool,
            command,
            output,
            summary,
            toolLabel: toolKind,
            toolKind: toolKind,
            command: command);

    public static ChatItemViewModel ToolGroup(string kindLabel) =>
        new(ChatItemKind.ToolGroup, toolLabel: kindLabel, toolKind: kindLabel)
        {
            _groupKind = kindLabel
        };

    public void AddChild(ChatItemViewModel child)
    {
        Children.Add(child);
        if (string.Equals(_groupKind, "Image", StringComparison.Ordinal))
        {
            OnPropertyChanged(nameof(Header));
        }
    }

    public string EffectiveKindLabel() =>
        IsToolGroup ? _groupKind
        : !string.IsNullOrEmpty(_toolKind) ? _toolKind
        : !string.IsNullOrEmpty(_toolLabel) ? _toolLabel
        : "Tool";

    public static ChatItemViewModel Error(string text) =>
        new(ChatItemKind.Error, text, statusKind: "error");

    private static DateTimeOffset? ParseTimestamp(string? value, DateTimeOffset? fallbackDate)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed;
        }

        var zh = CultureInfo.GetCultureInfo("zh-CN");
        if (DateTime.TryParseExact(value, "M月d日 H:mm", zh, DateTimeStyles.None, out var legacy)) // i18n-keep
        {
            return new DateTimeOffset(legacy);
        }

        if (fallbackDate is not null
            && DateTime.TryParseExact(value, "H:mm", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var timeOfDay))
        {
            var dated = fallbackDate.Value.ToLocalTime().Date.Add(timeOfDay.TimeOfDay);
            return new DateTimeOffset(dated);
        }

        return null;
    }

    public static ChatItemViewModel FromStored(StoredMessage message, DateTimeOffset? fallbackDate = null)
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
        var toolKind = message.ToolKind ?? string.Empty;
        var command = string.Empty;
        if (kind == ChatItemKind.Tool)
        {
            if (text.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                text = ExtractCommand(text);
            }

            if (string.IsNullOrEmpty(toolKind))
            {
                toolKind = message.Header.StartsWith("调用 ", StringComparison.Ordinal) // i18n-keep
                    ? ToolKindLabel(message.Header[3..])
                    : string.Equals(message.Header, Loc.T("工具调用"), StringComparison.Ordinal)
                        ? string.Empty
                        : ToolKindFromHeader(message.Header);
            }

            if (text.TrimStart().StartsWith('{')
                && detail.StartsWith("模型请求调用工具", StringComparison.Ordinal)) // i18n-keep
            {
                (text, detail) = (detail, text);
            }
            if (string.Equals(summary, message.Header, StringComparison.Ordinal))
            {
                summary = string.Empty;
            }
            command = text;
        }

        return new ChatItemViewModel(
            kind,
            text,
            detail,
            summary,
            message.Status,
            ParseTimestamp(message.Timestamp, fallbackDate),
            toolKind,
            toolKind,
            command)
        {
            _imageCount = kind == ChatItemKind.Tool && toolKind == "Image"
                ? CountImageMentions(text) + CountImageMentions(detail)
                : 0
        };
    }

    public StoredMessage ToStored() => new()
    {
        Kind = Kind.ToString().ToLowerInvariant(),
        Header = Header,
        Text = Text,
        Detail = Detail,
        Summary = Summary,
        Status = _statusKind,
        Timestamp = _timestamp?.ToLocalTime().ToString("o", CultureInfo.InvariantCulture) ?? string.Empty,
        ToolKind = _toolKind
    };

    public void Start(string status = "running")
    {
        IsRunning = true;
        if (IsActivity)
        {
            IsExpanded = true;
        }
        _statusKind = NormalizeStatus(status);
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatus));
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

    public void SetSummary(string summary) => Summary = summary;

    public void SetImageCount(int count)
    {
        if (count == _imageCount)
        {
            return;
        }

        _imageCount = count;
        OnPropertyChanged(nameof(Header));
    }

    public void Complete(string? status = "completed")
    {
        IsRunning = false;
        IsExpanded = false;
        _statusKind = NormalizeStatus(status);
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(HasFailure));
        if (IsAssistant && _timestamp is null)
        {
            _timestamp = DateTimeOffset.Now;
            OnPropertyChanged(nameof(TimestampLabel));
            OnPropertyChanged(nameof(HasTimestamp));
            OnPropertyChanged(nameof(TurnLabel));
            OnPropertyChanged(nameof(HasTurnLabel));
        }
    }

    public void Stop(string status = "cancelled")
    {
        IsRunning = false;
        IsExpanded = false;
        _statusKind = NormalizeStatus(status);
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(HasFailure));
    }

    /// <summary>Re-renders every computed label after a language switch.</summary>
    public override void RefreshLocalization()
    {
        OnPropertyChanged(string.Empty);
        foreach (var child in Children)
        {
            child.RefreshLocalization();
        }
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
