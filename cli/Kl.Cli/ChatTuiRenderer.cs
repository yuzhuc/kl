using System.Text;

namespace Kl.Cli;

public static class ChatTuiRenderer
{
    public const string HeaderText = ">_ KL (v0.1.0)";
    private const string InputPlaceholder = "Ask KL to do anything";

    public static string Render(int width, int height, ChatTuiState state)
    {
        if (state.Screen == ChatScreen.Settings)
        {
            return SettingsTuiRenderer.Render(width, height, state.Settings);
        }

        width = Math.Max(20, width);
        height = Math.Max(8, height);
        var bodyHeight = Math.Max(1, height - 4);

        var lines = new List<string>
        {
            HeaderText,
            new string('-', width)
        };

        lines.AddRange(state.Screen switch
        {
            ChatScreen.History => RenderHistoryBody(state, width, bodyHeight),
            ChatScreen.Shortcuts => RenderShortcutsBody(state, width, bodyHeight),
            _ => RenderChatBody(state, width, bodyHeight)
        });

        while (lines.Count < 2 + bodyHeight)
        {
            lines.Add(string.Empty);
        }

        lines.Add(RenderInput(state, width));
        lines.Add(RenderFooter(state, width));

        return string.Join('\n', lines.Take(height).Select(line => Fit(line, width)));
    }

    public static bool NeedsRedraw(
        bool changed,
        bool dirty,
        int width,
        int height,
        int lastWidth,
        int lastHeight) =>
        changed || dirty || width != lastWidth || height != lastHeight;

    public static int MaxTranscriptScroll(ChatTuiState state, int width, int height)
    {
        var bodyHeight = Math.Max(1, Math.Max(8, height) - 4);
        return Math.Max(
            0,
            TranscriptLines(state, Math.Max(20, width)).Count - bodyHeight);
    }

    public static List<string> TranscriptLines(ChatTuiState state, int width)
    {
        var lines = new List<string>();
        foreach (var entry in state.Transcript)
        {
            lines.AddRange(RenderEntry(entry, width));
        }
        return lines;
    }

    private static List<string> RenderChatBody(
        ChatTuiState state,
        int width,
        int bodyHeight)
    {
        var lines = TranscriptLines(state, width);
        if (lines.Count == 0)
        {
            lines.Add("Type a message and press Enter.");
        }

        var maximum = Math.Max(0, lines.Count - bodyHeight);
        var scroll = Math.Clamp(state.TranscriptScroll, 0, maximum);
        var start = Math.Max(0, lines.Count - bodyHeight - scroll);
        return lines.Skip(start).Take(bodyHeight).ToList();
    }

    private static List<string> RenderHistoryBody(
        ChatTuiState state,
        int width,
        int bodyHeight)
    {
        var left = new List<string> { "History" };
        var ordered = new List<HistorySession>();
        var index = 0;
        foreach (var group in state.History.GroupBy(session => session.Workspace))
        {
            left.Add($"Workspace: {group.Key}");
            foreach (var session in group)
            {
                ordered.Add(session);
                var marker = index == state.HistorySelection ? ">" : " ";
                left.Add(
                    $"{marker} {session.Task ?? string.Empty}  " +
                    $"{session.Status ?? string.Empty}  " +
                    $"{FormatTime(session.UpdatedAt)}");
                index++;
            }
        }

        if (ordered.Count == 0)
        {
            left.Add("No sessions yet. Press n to start a new session.");
            return left.Take(bodyHeight).ToList();
        }

        var selected = ordered[
            Math.Clamp(state.HistorySelection, 0, ordered.Count - 1)];
        var right = new List<string>
        {
            "Selected",
            $"{selected.Provider}/{selected.Model}",
            $"messages: {selected.MessageCount}",
            $"last: {selected.LastMessage ?? string.Empty}",
            $"prompt: {selected.Prompt ?? string.Empty}"
        };

        if (width < 80)
        {
            var stacked = new List<string>(left);
            stacked.Add(string.Empty);
            stacked.AddRange(right);
            return stacked.Take(bodyHeight).ToList();
        }

        var leftWidth = Math.Max(30, width * 3 / 5);
        var rightWidth = Math.Max(10, width - leftWidth - 1);
        var lines = new List<string>(bodyHeight);
        for (var row = 0; row < bodyHeight; row++)
        {
            var leftText = row < left.Count ? left[row] : string.Empty;
            var rightText = row < right.Count ? right[row] : string.Empty;
            lines.Add(Fit(leftText, leftWidth) + " " + Fit(rightText, rightWidth));
        }
        return lines;
    }

    private static List<string> RenderShortcutsBody(
        ChatTuiState state,
        int width,
        int bodyHeight)
    {
        var lines = new List<string>
        {
            "Shortcuts",
            string.Empty,
            "Enter          send message",
            "Ctrl+C         cancel current turn, or exit when idle",
            "Left arrow     history when input is empty; move cursor otherwise",
            "Up / Down      scroll transcript",
            "?              show or close this shortcut page",
            "Esc            back"
        };
        return lines.Take(bodyHeight).ToList();
    }

    private static string RenderInput(ChatTuiState state, int width)
    {
        if (state.Screen == ChatScreen.History)
        {
            return "> History";
        }
        if (state.Screen == ChatScreen.Shortcuts)
        {
            return "> Shortcuts";
        }

        if (state.Input.Length == 0)
        {
            return "> " + InputPlaceholder;
        }

        var available = Math.Max(1, width - 3);
        var cursor = Math.Clamp(state.Cursor, 0, state.Input.Length);
        var start = Math.Clamp(
            cursor - available / 2,
            0,
            Math.Max(0, state.Input.Length - available));
        var length = Math.Min(available, state.Input.Length - start);
        var visible = state.Input.Substring(start, length);
        var relative = Math.Clamp(cursor - start, 0, visible.Length);
        return "> " + visible.Insert(relative, "|");
    }

    private static string RenderFooter(ChatTuiState state, int width)
    {
        var provider = string.IsNullOrWhiteSpace(state.Provider)
            ? "provider/model"
            : $"{state.Provider}/{state.Model}";
        var footer = state.Screen switch
        {
            ChatScreen.History =>
                $"{provider}  reasoning: default  ↑/↓ select  " +
                "Enter  n  Esc  ? shortcuts",
            ChatScreen.Shortcuts =>
                $"{provider}  reasoning: default  Esc back  ? shortcuts",
            _ => $"{provider}  reasoning: default  ← history  ? shortcuts"
        };

        if (state.HasPendingApproval)
        {
            footer += "  [a] allow  [d] deny";
        }
        else if (state.IsTurnActive)
        {
            footer += "  working  Ctrl+C cancel";
        }
        else if (!string.IsNullOrWhiteSpace(state.StatusMessage))
        {
            footer += $"  {state.StatusMessage}";
        }

        return footer;
    }

    private static IEnumerable<string> RenderEntry(TranscriptEntry entry, int width)
    {
        var prefix = entry.Kind switch
        {
            TranscriptKind.User => "You: ",
            TranscriptKind.Assistant => "KL: ",
            TranscriptKind.Tool when entry.Text.StartsWith("[ok]", StringComparison.Ordinal)
                || entry.Text.StartsWith("[fail]", StringComparison.Ordinal) => string.Empty,
            TranscriptKind.Tool => "[tool] ",
            TranscriptKind.Approval => "[approval] ",
            TranscriptKind.Diff => "  ",
            TranscriptKind.Error => "[error] ",
            _ => string.Empty
        };
        return Wrap(prefix + entry.Text, width);
    }

    private static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        foreach (var raw in normalized.Split('\n'))
        {
            var remaining = raw;
            if (remaining.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }
            while (remaining.Length > width)
            {
                var breakAt = remaining.LastIndexOf(' ', width);
                if (breakAt <= 0)
                {
                    breakAt = width;
                }
                lines.Add(remaining[..breakAt].TrimEnd());
                remaining = remaining[breakAt..].TrimStart();
            }
            lines.Add(remaining);
        }
        return lines;
    }

    private static string FormatTime(string value)
    {
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed.ToLocalTime().ToString("MM-dd HH:mm")
            : value;
    }

    private static string Fit(string text, int width)
    {
        if (text.Length > width)
        {
            return text[..width];
        }
        return text.PadRight(width);
    }
}
