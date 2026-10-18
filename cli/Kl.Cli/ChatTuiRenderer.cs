using System.Globalization;

namespace Kl.Cli;

public static class ChatTuiRenderer
{
    public const string HeaderText = ">_ KL (v0.1.0)";

    public static string Render(int width, int height, ChatTuiState state)
    {
        if (state.Screen == ChatScreen.Settings)
        {
            return SettingsTuiRenderer.Render(width, height, state.Settings);
        }

        width = Math.Max(20, width);
        height = Math.Max(8, height);
        var bodyHeight = Math.Max(1, height - 4);

        var body = state.Screen switch
        {
            ChatScreen.History => RenderHistoryBody(state, width, bodyHeight),
            ChatScreen.Shortcuts => RenderShortcutsBody(state, width, bodyHeight),
            _ => RenderChatBody(state, width, bodyHeight)
        };

        ApplyOverlay(state, width, bodyHeight, body);

        var lines = new List<string>
        {
            HeaderText,
            new string('-', width)
        };
        lines.AddRange(body);
        lines.Add(RenderInput(state, width));
        lines.Add(RenderFooter(state, width));

        return string.Join('\n', lines.Take(height).Select(line =>
            DiffRender.Ansi.Fit(line, width)));
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
        width = Math.Max(1, width);
        var language = state.Language;
        var lines = new List<string>();
        var hiddenReasoningShown = false;
        foreach (var entry in state.Transcript)
        {
            if (entry.Kind == TranscriptKind.Reasoning)
            {
                if (!state.ReasoningVisible)
                {
                    if (hiddenReasoningShown)
                    {
                        continue;
                    }
                    hiddenReasoningShown = true;
                    lines.AddRange(ColorLines(
                        WrapPlain(ChatTexts.ThinkingHidden(language), width),
                        DiffRender.Ansi.Dim));
                    continue;
                }

                lines.AddRange(ColorLines(
                    WrapPlain(ChatTexts.ThinkingLabel(language) + entry.Text, width),
                    DiffRender.Ansi.Dim));
                continue;
            }

            lines.AddRange(RenderEntry(entry, width, language));
        }
        return lines;
    }

    private static List<string> RenderChatBody(
        ChatTuiState state,
        int width,
        int bodyHeight)
    {
        var lines = TranscriptLines(state, width);
        if (lines.Count == 0 && !state.IsTurnActive)
        {
            lines = RenderHome(state, width);
        }

        var maximum = Math.Max(0, lines.Count - bodyHeight);
        var scroll = Math.Clamp(state.TranscriptScroll, 0, maximum);
        var start = Math.Max(0, lines.Count - bodyHeight - scroll);
        var visible = PadBody(
            lines.Skip(start).Take(bodyHeight).ToList(),
            bodyHeight);

        if (state.SelectionAnchor is { } anchor
            && state.SelectionCursor is { } cursor)
        {
            var selectionStart = Math.Min(anchor, cursor);
            var selectionEnd = Math.Max(anchor, cursor);
            for (var index = 0; index < visible.Count; index++)
            {
                var absolute = start + index;
                if (absolute >= selectionStart
                    && absolute <= selectionEnd
                    && absolute < lines.Count)
                {
                    visible[index] =
                        DiffRender.Ansi.Inverse
                        + visible[index]
                        + DiffRender.Ansi.Reset;
                }
            }
        }
        return visible;
    }

    private static List<string> RenderHome(ChatTuiState state, int width)
    {
        var language = state.Language;
        var lines = new List<string>();
        foreach (var line in ChatTexts.Home(
            language,
            !string.IsNullOrWhiteSpace(state.Provider)))
        {
            lines.AddRange(DisplayText.WrapToWidth(line, width));
        }
        return lines;
    }

    private static List<string> RenderHistoryBody(
        ChatTuiState state,
        int width,
        int bodyHeight)
    {
        var language = state.Language;
        var left = new List<string> { ChatTexts.HistoryTitle(language) };
        var ordered = new List<HistorySession>();
        var index = 0;
        foreach (var group in state.History.GroupBy(session => session.Workspace))
        {
            left.Add(ChatTexts.Workspace(language, group.Key));
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
            left.Add(ChatTexts.NoSessions(language));
            return PadBody(left.Take(bodyHeight).ToList(), bodyHeight);
        }

        var selected = ordered[
            Math.Clamp(state.HistorySelection, 0, ordered.Count - 1)];
        var right = new List<string>
        {
            ChatTexts.Selected(language),
            $"{selected.Provider}/{selected.Model}",
            ChatTexts.Messages(language, selected.MessageCount),
            ChatTexts.Last(language, selected.LastMessage ?? string.Empty),
            ChatTexts.Prompt(language, selected.Prompt ?? string.Empty)
        };

        if (width < 80)
        {
            var stacked = new List<string>(left);
            stacked.Add(string.Empty);
            stacked.AddRange(right);
            return PadBody(stacked.Take(bodyHeight).ToList(), bodyHeight);
        }

        var leftWidth = Math.Max(30, width * 3 / 5);
        var rightWidth = Math.Max(10, width - leftWidth - 1);
        var lines = new List<string>(bodyHeight);
        for (var row = 0; row < bodyHeight; row++)
        {
            var leftText = row < left.Count ? left[row] : string.Empty;
            var rightText = row < right.Count ? right[row] : string.Empty;
            lines.Add(
                DiffRender.Ansi.Fit(leftText, leftWidth) + " " +
                DiffRender.Ansi.Fit(rightText, rightWidth));
        }
        return lines;
    }

    private static List<string> RenderShortcutsBody(
        ChatTuiState state,
        int width,
        int bodyHeight)
    {
        var language = state.Language;
        var lines = new List<string>
        {
            ChatTexts.ShortcutsTitle(language),
            string.Empty
        };
        lines.AddRange(ChatTexts.Shortcuts(language));
        var wrapped = new List<string>();
        foreach (var line in lines)
        {
            wrapped.AddRange(DisplayText.WrapToWidth(line, width));
        }
        return PadBody(wrapped.Take(bodyHeight).ToList(), bodyHeight);
    }

    private static void ApplyOverlay(
        ChatTuiState state,
        int width,
        int bodyHeight,
        List<string> body)
    {
        List<string> overlay;
        if (state.ModelPicker.IsOpen)
        {
            overlay = ChatModelPicker.Render(
                state.ModelPicker,
                width,
                bodyHeight,
                state.Language);
        }
        else if (ChatCommandPalette.IsOpen(state))
        {
            overlay = RenderCommandPalette(state, width, bodyHeight);
        }
        else
        {
            return;
        }

        var count = Math.Min(overlay.Count, bodyHeight);
        for (var index = 0; index < count; index++)
        {
            body[bodyHeight - count + index] = DiffRender.Ansi.Fit(overlay[index], width);
        }
    }

    public static List<string> CommandPaletteLines(ChatTuiState state, int width) =>
        RenderCommandPalette(state, Math.Max(20, width), int.MaxValue);

    private static List<string> RenderCommandPalette(
        ChatTuiState state,
        int width,
        int maximumLines)
    {
        var matches = ChatCommandPalette.Matches(state.Input);
        if (matches.Count == 0 || maximumLines <= 0)
        {
            return [];
        }

        var language = state.Language;
        var nameWidth = matches.Max(command => DisplayText.Width(command.Name));
        var descriptionColumn = 2 + nameWidth + 2;
        var descriptionWidth = Math.Max(
            (width * 30 + 99) / 100,
            width - descriptionColumn);
        descriptionWidth = Math.Max(
            1,
            Math.Min(descriptionWidth, Math.Max(1, width - descriptionColumn)));

        var visible = Math.Min(ChatCommandPalette.MaxVisible, maximumLines);
        var selected = Math.Clamp(
            state.CommandPaletteSelection,
            0,
            matches.Count - 1);
        var start = Math.Clamp(
            selected - visible + 1,
            0,
            Math.Max(0, matches.Count - visible));
        var count = Math.Min(visible, matches.Count - start);

        var lines = new List<string>(count);
        for (var index = start; index < start + count; index++)
        {
            var command = matches[index];
            var description = DiffRender.Ansi.TruncateVisible(
                ChatCommandPalette.Description(command, language),
                descriptionWidth);
            var row =
                $"{(index == selected ? "› " : "  ")}" +
                DisplayText.PadToWidth(command.Name, nameWidth) +
                "  " +
                description;
            var fitted = DiffRender.Ansi.Fit(row, width);
            lines.Add(index == selected
                ? DiffRender.Ansi.Inverse + fitted + DiffRender.Ansi.Reset
                : fitted);
        }
        return lines;
    }

    private static string RenderInput(ChatTuiState state, int width)
    {
        if (state.Screen == ChatScreen.History)
        {
            return "> " + ChatTexts.HistoryTitle(state.Language);
        }
        if (state.Screen == ChatScreen.Shortcuts)
        {
            return "> " + ChatTexts.ShortcutsTitle(state.Language);
        }

        if (state.Input.Length == 0)
        {
            return "> " + ChatTexts.InputPlaceholder(state.Language);
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
        var language = state.Language;
        var provider = string.IsNullOrWhiteSpace(state.Provider)
            ? "provider/model"
            : $"{state.Provider}/{state.Model}";
        var parts = new List<string> { provider };
        if (!string.IsNullOrWhiteSpace(state.StatusMessage))
        {
            parts.Add(state.StatusMessage);
        }

        if (state.ModelPicker.IsOpen)
        {
            parts.Add(ChatTexts.ModelPickerFooter(language));
        }
        else if (ChatCommandPalette.IsOpen(state))
        {
            parts.Add(ChatTexts.PaletteFooter(language));
        }
        else if (state.HasPendingApproval)
        {
            parts.Add(ChatTexts.ApprovalFooter(language));
        }
        else if (state.IsTurnActive)
        {
            parts.Add(ChatTexts.WorkingFooter(language));
        }
        else
        {
            parts.Add(ChatTexts.IdleFooter(language));
        }

        return string.Join("  ", parts);
    }

    private static IEnumerable<string> RenderEntry(
        TranscriptEntry entry,
        int width,
        UiLanguage language)
    {
        switch (entry.Kind)
        {
            case TranscriptKind.User:
                return WrapPlain(
                    ChatTexts.UserLabel(language) + entry.Text,
                    width);
            case TranscriptKind.Assistant:
                return WrapPlain(
                    ChatTexts.AssistantLabel(language) + entry.Text,
                    width);
            case TranscriptKind.Tool:
                return RenderTool(entry.Text, width, language);
            case TranscriptKind.Approval:
                return RenderApproval(entry.Text, width, language);
            case TranscriptKind.Diff:
                return RenderDiff(entry.Text, width, language);
            case TranscriptKind.Error:
                return ColorLines(
                    WrapPlain(ChatTexts.ErrorLabel(language) + entry.Text, width),
                    DiffRender.Ansi.Red);
            case TranscriptKind.Worked:
                return ColorLines(
                    WrapPlain(RenderWorked(entry.Text, language), width),
                    DiffRender.Ansi.Dim);
            default:
                return WrapPlain(entry.Text, width);
        }
    }

    private static IEnumerable<string> RenderTool(
        string text,
        int width,
        UiLanguage language)
    {
        var lines = DiffRender.TruncateToolOutput(
            text,
            DiffRender.ToolOutputMaxLines,
            language);
        var success = text.StartsWith("[ok]", StringComparison.Ordinal);
        var failure = text.StartsWith("[fail]", StringComparison.Ordinal);
        var prefix = success || failure
            ? string.Empty
            : ChatTexts.ToolLabel(language);

        var rendered = new List<string>();
        for (var index = 0; index < lines.Count; index++)
        {
            var line = (index == 0 ? prefix : string.Empty) + lines[index];
            rendered.AddRange(WrapPlain(line, width));
        }

        if (success)
        {
            return ColorLines(rendered, DiffRender.Ansi.Green);
        }
        if (failure)
        {
            return ColorLines(rendered, DiffRender.Ansi.Red);
        }
        return rendered;
    }

    private static IEnumerable<string> RenderApproval(
        string text,
        int width,
        UiLanguage language)
    {
        var fields = text.Split(ChatTuiState.FieldSeparator);
        string Field(int index) => index < fields.Length ? fields[index] : string.Empty;

        var name = DiffRender.RedactSensitive(Field(0));
        var action = DiffRender.RedactSensitive(Field(1));
        var resource = DiffRender.RedactSensitive(Field(2));
        var reason = DiffRender.RedactSensitive(Field(3));
        var target = string.Join(
            " ",
            new[] { action, resource }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (string.IsNullOrWhiteSpace(target))
        {
            target = DiffRender.RedactSensitive(Field(1));
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            reason = ChatTexts.ApprovalDefaultRisk(language);
        }

        var lines = new List<string>();
        lines.AddRange(WrapPlain(ChatTexts.ApprovalTitle(language), width));
        lines.AddRange(WrapPlain(
            "  " + ChatTexts.ApprovalTool(language) + ": " + name,
            width));
        if (!string.IsNullOrWhiteSpace(target))
        {
            lines.AddRange(WrapPlain(
                "  " + ChatTexts.ApprovalCommand(language) + ": " + target,
                width));
        }
        lines.AddRange(WrapPlain(
            "  " + ChatTexts.ApprovalRisk(language) + ": " + reason,
            width));
        lines.Add(
            "  " + DiffRender.Ansi.BoldGreen + "[a] " +
            ChatTexts.ApprovalAllow(language) + DiffRender.Ansi.Reset +
            "  " + DiffRender.Ansi.BoldRed + "[d] " +
            ChatTexts.ApprovalDeny(language) + DiffRender.Ansi.Reset);
        return lines;
    }

    private static IEnumerable<string> RenderDiff(
        string text,
        int width,
        UiLanguage language)
    {
        var lines = new List<string> { ChatTexts.DiffTitle(language) };
        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        foreach (var raw in normalized.Split('\n'))
        {
            foreach (var wrapped in DisplayText.WrapToWidth(raw, width))
            {
                lines.Add(wrapped.Length == 0
                    ? string.Empty
                    : DiffRender.ColorizeDiffLine(wrapped));
            }
        }
        return lines;
    }

    private static string RenderWorked(string text, UiLanguage language)
    {
        var fields = text.Split(ChatTuiState.FieldSeparator);
        if (fields.Length == 2
            && double.TryParse(
                fields[0],
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var seconds))
        {
            return ChatTexts.WorkedFor(language, seconds, fields[1]);
        }
        return text;
    }

    private static List<string> WrapPlain(string? text, int width)
    {
        var normalized = (text ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = new List<string>();
        foreach (var raw in normalized.Split('\n'))
        {
            lines.AddRange(DisplayText.WrapToWidth(raw, Math.Max(1, width)));
        }
        return lines;
    }

    private static List<string> ColorLines(IEnumerable<string> lines, string color) =>
        lines.Select(line => line.Length == 0
                ? line
                : DiffRender.Ansi.Colorize(line, color))
            .ToList();

    private static List<string> PadBody(List<string> lines, int bodyHeight)
    {
        while (lines.Count < bodyHeight)
        {
            lines.Add(string.Empty);
        }
        return lines.Take(bodyHeight).ToList();
    }

    private static string FormatTime(string value)
    {
        return DateTimeOffset.TryParse(value, out var parsed)
            ? parsed.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
            : value;
    }
}
