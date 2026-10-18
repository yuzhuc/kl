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
        var showInput = state.Screen != ChatScreen.History;
        var bodyHeight = Math.Max(1, height - (showInput ? 4 : 3));

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
        if (showInput)
        {
            lines.Add(RenderInput(state, width));
        }
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
        var view = state.GetHistoryView();
        var selection = view.Tasks.Count == 0
            ? 0
            : Math.Clamp(state.HistorySelection, 0, view.Tasks.Count - 1);
        var selected = view.Selected(selection);
        var now = DateTimeOffset.UtcNow;
        var header = RenderHistoryHeader(state, view, width, language);

        if (width >= 90)
        {
            var detailWidth = Math.Clamp(width / 3, 30, 44);
            var leftWidth = width - detailWidth - 1;
            var left = BuildHistoryList(
                header,
                view,
                selection,
                leftWidth,
                bodyHeight,
                language,
                now);
            var right = RenderHistoryDetails(selected, detailWidth, language);
            return ComposeHistoryColumns(left, right, leftWidth, detailWidth, bodyHeight);
        }

        var details = RenderHistoryDetails(selected, width, language);
        var listHeight = Math.Max(1, bodyHeight - details.Count - 1);
        var stacked = BuildHistoryList(
            header,
            view,
            selection,
            width,
            listHeight,
            language,
            now);
        stacked.Add(string.Empty);
        stacked.AddRange(details);
        return PadBody(stacked, bodyHeight);
    }

    private static List<string> RenderHistoryHeader(
        ChatTuiState state,
        HistoryView view,
        int width,
        UiLanguage language)
    {
        var lines = new List<string>
        {
            state.HistorySearching || state.HistorySearch.Length > 0
                ? RenderHistorySearchLine(state, width, language)
                : ChatTexts.HistoryTitle(language) + "  " +
                    ChatTexts.HistoryGroupBy(language)
        };

        var tabLine = string.Empty;
        foreach (var filter in Enum.GetValues<HistoryFilter>())
        {
            var tab = ChatTexts.HistoryFilterLabel(language, filter) + " " +
                view.Count(filter);
            var candidate = tabLine.Length == 0 ? tab : tabLine + "   " + tab;
            if (tabLine.Length > 0 && DisplayText.Width(candidate) > width)
            {
                lines.Add(tabLine);
                tabLine = tab;
            }
            else
            {
                tabLine = candidate;
            }
        }
        if (tabLine.Length > 0)
        {
            lines.Add(tabLine);
        }

        lines.Add(new string('─', Math.Max(1, width)));
        return lines;
    }

    private static List<string> BuildHistoryList(
        IReadOnlyList<string> header,
        HistoryView view,
        int selection,
        int width,
        int height,
        UiLanguage language,
        DateTimeOffset now)
    {
        var lines = new List<string>(header);
        var groupLines = new List<string>();
        var selectedLine = -1;
        var taskIndex = 0;
        var statusWidth = Enum.GetValues<HistoryTaskStatus>()
            .Max(status => DisplayText.Width(ChatTexts.HistoryStatus(language, status)));
        var timeWidth = Math.Max(
            6,
            view.Tasks.Count == 0
                ? 0
                : view.Tasks.Max(task => DisplayText.Width(
                    FormatHistoryTime(task.Session.UpdatedAt, language, now))));

        foreach (var group in view.Groups)
        {
            groupLines.Add(
                DiffRender.Ansi.Fit(
                    WorkspaceLabel(group.Workspace) + "  " + group.Tasks.Count,
                    width));
            groupLines.Add(new string('─', Math.Max(1, width)));
            foreach (var task in group.Tasks)
            {
                if (taskIndex == selection)
                {
                    selectedLine = header.Count + groupLines.Count;
                }
                groupLines.Add(FormatHistoryTaskRow(
                    task,
                    taskIndex == selection,
                    width,
                    statusWidth,
                    timeWidth,
                    language,
                    now));
                taskIndex++;
            }
        }

        if (view.Tasks.Count == 0)
        {
            groupLines.Add(view.Search.Length > 0
                ? ChatTexts.NoSearchResults(language)
                : ChatTexts.NoSessions(language));
        }

        var available = Math.Max(0, height - header.Count);
        if (groupLines.Count > available && selectedLine >= header.Count)
        {
            var selectedRelative = selectedLine - header.Count;
            var start = Math.Clamp(
                selectedRelative - available + 1,
                0,
                Math.Max(0, groupLines.Count - available));
            lines.AddRange(groupLines.Skip(start).Take(available));
        }
        else
        {
            lines.AddRange(groupLines.Take(available));
        }
        return PadBody(lines, height);
    }

    private static string FormatHistoryTaskRow(
        HistoryTask task,
        bool selected,
        int width,
        int statusWidth,
        int timeWidth,
        UiLanguage language,
        DateTimeOffset now)
    {
        var marker = selected ? "› " : "  ";
        var status = DisplayText.PadToWidth(
            ChatTexts.HistoryStatus(language, task.Status),
            statusWidth);
        var time = PadLeftToWidth(
            FormatHistoryTime(task.Session.UpdatedAt, language, now),
            timeWidth);
        var titleWidth = Math.Max(
            1,
            width - DisplayText.Width(marker) - statusWidth - timeWidth - 4);
        var title = DisplayText.PadToWidth(
            TaskTitle(task.Session, language),
            titleWidth);
        var row = DiffRender.Ansi.Fit(
            marker + title + "  " + status + "  " + time,
            width);
        return selected
            ? DiffRender.Ansi.Inverse + row + DiffRender.Ansi.Reset
            : row;
    }

    private static List<string> RenderHistoryDetails(
        HistoryTask? task,
        int width,
        UiLanguage language)
    {
        var lines = new List<string> { ChatTexts.TaskDetails(language) };
        if (task is null)
        {
            lines.Add(ChatTexts.NoTaskSelected(language));
            return lines;
        }

        var session = task.Session;
        lines.AddRange(DisplayText.WrapToWidth(TaskTitle(session, language), width));
        lines.AddRange(DisplayText.WrapToWidth(
            ChatTexts.HistoryStatusLine(
                language,
                ChatTexts.HistoryStatus(language, task.Status)),
            width));
        var model = string.IsNullOrWhiteSpace(session.Provider)
            && string.IsNullOrWhiteSpace(session.Model)
                ? "—"
                : $"{session.Provider}/{session.Model}".Trim('/');
        lines.AddRange(DisplayText.WrapToWidth(
            ChatTexts.HistoryModelLine(language, model),
            width));
        lines.AddRange(DisplayText.WrapToWidth(
            ChatTexts.HistoryProjectLine(
                language,
                WorkspaceLabel(session.Workspace)),
            width));
        lines.AddRange(DisplayText.WrapToWidth(
            ChatTexts.HistoryPromptLine(language, ValueOrDash(session.Prompt)),
            width));
        lines.AddRange(DisplayText.WrapToWidth(
            ChatTexts.HistoryLastLine(language, ValueOrDash(session.LastMessage)),
            width));
        return lines;
    }

    private static List<string> ComposeHistoryColumns(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right,
        int leftWidth,
        int rightWidth,
        int height)
    {
        var lines = new List<string>(height);
        for (var row = 0; row < height; row++)
        {
            var leftText = row < left.Count ? left[row] : string.Empty;
            var rightText = row < right.Count ? right[row] : string.Empty;
            lines.Add(
                DiffRender.Ansi.Fit(leftText, leftWidth) + " " +
                DiffRender.Ansi.Fit(rightText, rightWidth));
        }
        return lines;
    }

    private static string RenderHistorySearchLine(
        ChatTuiState state,
        int width,
        UiLanguage language)
    {
        var prefix = ChatTexts.HistorySearch(language) + " › ";
        var contentWidth = Math.Max(0, width - DisplayText.Width(prefix));
        if (contentWidth == 0)
        {
            return prefix;
        }

        var query = state.HistorySearch;
        if (!state.HistorySearching)
        {
            return prefix + DiffRender.Ansi.TruncateVisible(query, contentWidth);
        }

        var cursor = Math.Clamp(state.HistorySearchCursor, 0, query.Length);
        if (DisplayText.Width(query) + 1 <= contentWidth)
        {
            return prefix + query.Insert(cursor, "|");
        }

        var available = Math.Max(0, contentWidth - 1);
        var leftWidth = (available + 1) / 2;
        var rightWidth = available - leftWidth;
        var before = query[..cursor];
        var after = query[cursor..];
        return prefix +
            TailToWidth(before, leftWidth) +
            "|" +
            DisplayText.TruncateToWidth(after, rightWidth);
    }

    private static string TailToWidth(string text, int width)
    {
        if (DisplayText.Width(text) <= width)
        {
            return text;
        }
        if (width <= 1)
        {
            return "…";
        }

        var budget = width - 1;
        var used = 0;
        var tail = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        var elements = new List<string>();
        while (enumerator.MoveNext())
        {
            elements.Add((string)enumerator.Current);
        }
        for (var index = elements.Count - 1; index >= 0; index--)
        {
            var elementWidth = DisplayText.Width(elements[index]);
            if (used + elementWidth > budget)
            {
                break;
            }
            tail.Add(elements[index]);
            used += elementWidth;
        }
        tail.Reverse();
        return "…" + string.Concat(tail);
    }

    private static string TaskTitle(HistorySession session, UiLanguage language) =>
        string.IsNullOrWhiteSpace(session.Task)
            ? ChatTexts.UntitledTask(language)
            : session.Task.Trim();

    private static string WorkspaceLabel(string workspace) =>
        string.IsNullOrWhiteSpace(workspace) ? "—" : workspace;

    private static string ValueOrDash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    private static string PadLeftToWidth(string value, int width)
    {
        var fitted = DiffRender.Ansi.TruncateVisible(value, width);
        var padding = Math.Max(0, width - DisplayText.Width(fitted));
        return new string(' ', padding) + fitted;
    }

    private static string FormatHistoryTime(
        string value,
        UiLanguage language,
        DateTimeOffset now)
    {
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return string.IsNullOrWhiteSpace(value) ? "—" : value;
        }

        var elapsed = now - parsed;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }
        if (elapsed.TotalSeconds < 60)
        {
            return ChatTexts.SecondsAgo(language, (int)elapsed.TotalSeconds);
        }
        if (elapsed.TotalMinutes < 60)
        {
            return ChatTexts.MinutesAgo(language, (int)elapsed.TotalMinutes);
        }
        if (elapsed.TotalHours < 24)
        {
            return ChatTexts.HoursAgo(language, (int)elapsed.TotalHours);
        }
        if (elapsed.TotalDays < 7)
        {
            return ChatTexts.DaysAgo(language, (int)elapsed.TotalDays);
        }
        return parsed.ToLocalTime().ToString("MM-dd", CultureInfo.InvariantCulture);
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
        if (state.Screen == ChatScreen.History)
        {
            return ChatTexts.HistoryFooter(language, state.HistorySearching);
        }

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


}
