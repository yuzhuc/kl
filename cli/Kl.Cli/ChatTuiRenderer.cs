using System.Globalization;

namespace Kl.Cli;

public static class ChatTuiRenderer
{
    public static string HeaderText => KlBrand.TitleLine;

    private static readonly string[] TaskTitleColors =
    [
        "\u001b[38;5;255m",
        "\u001b[38;5;224m",
        "\u001b[38;5;225m",
        "\u001b[38;5;183m",
        "\u001b[38;5;211m",
        "\u001b[38;5;217m",
        "\u001b[38;5;216m",
        "\u001b[38;5;223m",
        "\u001b[38;5;151m",
        "\u001b[38;5;159m",
        "\u001b[38;5;117m",
        "\u001b[38;5;111m",
        "\u001b[38;5;75m",
        "\u001b[38;5;147m"
    ];

    public static string Render(int width, int height, ChatTuiState state)
    {
        if (state.Screen == ChatScreen.Settings)
        {
            return SettingsTuiRenderer.Render(width, height, state.Settings);
        }

        width = Math.Max(20, width);
        height = Math.Max(8, height);
        var chatLike = state.Screen == ChatScreen.Chat
            || (state.Screen == ChatScreen.Shortcuts
                && state.ShortcutReturnScreen == ChatScreen.Chat);
        var showHeader = state.Screen == ChatScreen.Shortcuts
            || (state.Screen == ChatScreen.Chat && IsHome(state));
        var showInput = chatLike;
        var showModelLine = chatLike;
        var topMargin = showHeader && chatLike ? 1 : 0;
        var inputHeight = showInput ? ComposerHeight(state, width, height) : 0;
        var bodyHeight = Math.Max(
            1,
            height - topMargin - (showHeader ? 2 : 0) -
            inputHeight - (showModelLine ? 1 : 0) - 1);

        var body = state.Screen switch
        {
            ChatScreen.History => RenderHistoryBody(state, width, bodyHeight),
            ChatScreen.Shortcuts => RenderShortcutsBody(state, width, bodyHeight),
            _ => RenderChatBody(state, width, bodyHeight)
        };

        ApplyOverlay(state, width, bodyHeight, body);

        var lines = new List<string>();
        if (topMargin > 0)
        {
            lines.Add(string.Empty);
        }
        if (showHeader)
        {
            lines.Add(RenderHeaderTitle());
            lines.Add(RenderHeaderPath(state, width));
        }
        lines.AddRange(body);
        if (showInput)
        {
            lines.AddRange(RenderInputLines(state, width, height));
        }
        if (showModelLine)
        {
            lines.Add(RenderModelLine(state, width));
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
        var bodyHeight = Math.Max(
            1,
            Math.Max(8, height) - 2 - ComposerHeight(state, width, height));
        return Math.Max(
            0,
            TranscriptLines(state, Math.Max(20, width)).Count - bodyHeight);
    }

    public static bool CanMoveInputCursorVertical(ChatTuiState state, int width) =>
        WrapComposerRanges(state.Input, Math.Max(1, width - 5)).Count > 1;

    public static bool MoveInputCursorVertical(
        ChatTuiState state,
        int width,
        int delta)
    {
        var ranges = WrapComposerRanges(state.Input, Math.Max(1, width - 5));
        if (ranges.Count <= 1)
        {
            return false;
        }

        var (row, column) = LocateComposerCursor(state.Input, ranges, state.Cursor);
        var target = Math.Clamp(row + delta, 0, ranges.Count - 1);
        state.Cursor = IndexAtVisualColumn(
            state.Input,
            ranges[target].Start,
            ranges[target].End,
            column);
        return true;
    }
    public static int ComposerHeight(ChatTuiState state, int width, int height)
    {
        var (lines, _) = BuildComposerDisplay(state, width);
        return Math.Min(lines.Count, ComposerTextRowLimit(height)) + 2;
    }
    public static TranscriptViewport? TranscriptViewport(
        ChatTuiState state,
        int width,
        int height)
    {
        if (state.Screen != ChatScreen.Chat
            || state.ModelPicker.IsOpen
            || MentionPicker.IsOpen(state)
            || ChatCommandPalette.IsOpen(state)
            || IsHome(state))
        {
            return null;
        }

        width = Math.Max(20, width);
        height = Math.Max(8, height);
        var bodyHeight = Math.Max(1, height - 2 - ComposerHeight(state, width, height));
        var layout = BuildTranscriptLayout(state, width);
        if (layout.Lines.Count == 0)
        {
            return null;
        }

        var window = BuildChatTranscriptWindow(
            state,
            width,
            bodyHeight,
            layout);
        return new TranscriptViewport(
            window.Header is null ? 0 : 1,
            window.Start,
            window.Visible);
    }

    public static bool ToggleTranscriptItemAt(
        ChatTuiState state,
        int width,
        int height,
        int screenRow)
    {
        if (state.Screen != ChatScreen.Chat
            || state.ModelPicker.IsOpen
            || MentionPicker.IsOpen(state)
            || ChatCommandPalette.IsOpen(state))
        {
            return false;
        }

        width = Math.Max(20, width);
        height = Math.Max(8, height);
        var bodyHeight = Math.Max(1, height - 2 - ComposerHeight(state, width, height));
        var layout = BuildTranscriptLayout(state, width);
        if (layout.Lines.Count == 0)
        {
            return false;
        }

        var window = BuildChatTranscriptWindow(state, width, bodyHeight, layout);
        var localRow = screenRow - (window.Header is null ? 0 : 1);
        if (localRow < 0 || localRow >= window.Toggles.Count)
        {
            return false;
        }
        var toggle = window.Toggles[localRow];
        if (toggle is null)
        {
            return false;
        }
        if (toggle.Kind == TranscriptToggleKind.Reasoning)
        {
            state.ToggleReasoning();
        }
        else
        {
            state.ToggleTranscriptItem(toggle.Key);
        }
        return true;
    }
    public static List<string> TranscriptLines(ChatTuiState state, int width) =>
        BuildTranscriptLayout(state, width).Lines;

    private static TranscriptLayout BuildTranscriptLayout(ChatTuiState state, int width)
    {
        width = Math.Max(1, width);
        var language = state.Language;
        var lines = new List<string>();
        var toggles = new List<TranscriptToggle?>();
        var prompts = new List<UserPromptSpan>();
        var hiddenReasoningShown = false;

        void Add(TranscriptLine line)
        {
            lines.Add(line.Text);
            toggles.Add(line.Toggle);
        }

        void AddRange(IEnumerable<TranscriptLine> rendered)
        {
            foreach (var line in rendered)
            {
                Add(line);
            }
        }

        void AddPlain(IEnumerable<string> rendered)
        {
            foreach (var line in rendered)
            {
                Add(new TranscriptLine(line));
            }
        }

        for (var entryIndex = 0; entryIndex < state.Transcript.Count; entryIndex++)
        {
            var entry = state.Transcript[entryIndex];
            var previousKind = entryIndex > 0
                ? state.Transcript[entryIndex - 1].Kind
                : (TranscriptKind?)null;
            if (previousKind is { } previous
                && ((entry.Kind == TranscriptKind.User && previous != TranscriptKind.User)
                    || (entry.Kind != TranscriptKind.User && previous == TranscriptKind.User)))
            {
                Add(new TranscriptLine(string.Empty));
            }
            if (entry.Kind == TranscriptKind.Reasoning)
            {
                if (!state.ReasoningVisible)
                {
                    if (hiddenReasoningShown)
                    {
                        continue;
                    }
                    hiddenReasoningShown = true;
                    var hidden = ColorLines(
                        WrapPlain(ChatTexts.ThinkingHidden(language), width),
                        DiffRender.Ansi.Dim);
                    for (var index = 0; index < hidden.Count; index++)
                    {
                        Add(new TranscriptLine(
                            hidden[index],
                            index == 0
                                ? new TranscriptToggle("reasoning", TranscriptToggleKind.Reasoning)
                                : null));
                    }
                    continue;
                }

                AddPlain(ColorLines(
                    WrapPlain(ChatTexts.ThinkingLabel(language) + entry.Text, width),
                    DiffRender.Ansi.Dim));
                continue;
            }

            if (entry.Kind == TranscriptKind.User)
            {
                prompts.Add(new UserPromptSpan(lines.Count, entry.Text));
            }
            if (entry.Kind == TranscriptKind.Assistant)
            {
                AddRange(RenderAssistantLines(
                    state,
                    entryIndex,
                    entry.Text,
                    width,
                    language));
                continue;
            }
            if (entry.Kind == TranscriptKind.Diff)
            {
                AddRange(RenderDiffLines(
                    state,
                    entryIndex,
                    entry.Text,
                    width,
                    language));
                continue;
            }
            if (entry.Kind == TranscriptKind.Tool)
            {
                AddRange(RenderToolLines(
                    state,
                    entryIndex,
                    entry.Text,
                    width,
                    language));
                continue;
            }
            AddPlain(RenderEntry(entry, width, language));
        }
        return new TranscriptLayout(lines, prompts, toggles);
    }
    private static List<string> RenderChatBody(
        ChatTuiState state,
        int width,
        int bodyHeight)
    {
        var layout = BuildTranscriptLayout(state, width);
        if (layout.Lines.Count == 0 && !state.IsTurnActive)
        {
            layout = new TranscriptLayout(RenderHome(state, width), [], []);
        }

        var window = BuildChatTranscriptWindow(
            state,
            width,
            bodyHeight,
            layout);
        var start = window.Start;
        var visible = PadBody(window.Visible.ToList(), window.VisibleHeight);

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
                    && absolute < layout.Lines.Count)
                {
                    visible[index] = DiffRender.Ansi.ApplySelection(
                        visible[index],
                        0,
                        width);
                }
            }
        }
        else
        {
            for (var index = 0; index < visible.Count; index++)
            {
                if (state.MouseSelection.TryGetColumns(
                        start + index,
                        out var selectionStart,
                        out var selectionEnd))
                {
                    visible[index] = DiffRender.Ansi.ApplySelection(
                        visible[index],
                        selectionStart,
                        selectionEnd);
                }
            }
        }
        if (window.Header is not null)
        {
            visible.Insert(0, window.Header);
        }
        return visible;
    }

    private static ChatTranscriptWindow BuildChatTranscriptWindow(
        ChatTuiState state,
        int width,
        int bodyHeight,
        TranscriptLayout layout)
    {
        var lines = layout.Lines;
        var maximum = Math.Max(0, lines.Count - bodyHeight);
        var scroll = Math.Clamp(state.TranscriptScroll, 0, maximum);
        var start = Math.Max(0, lines.Count - bodyHeight - scroll);
        UserPromptSpan? prompt = layout.Prompts.LastOrDefault(
            candidate => candidate.Start <= start);
        var contentHeight = bodyHeight;
        if (prompt is not null && bodyHeight > 1)
        {
            contentHeight = bodyHeight - 1;
            start = Math.Max(0, lines.Count - contentHeight - scroll);
            prompt = layout.Prompts.LastOrDefault(candidate => candidate.Start <= start) ?? prompt;
        }
        var visible = lines
            .Skip(start)
            .Take(contentHeight)
            .ToList();
        var toggles = layout.Toggles
            .Skip(start)
            .Take(contentHeight)
            .ToList();
        return new ChatTranscriptWindow(
            prompt is null ? null : RenderStickyPrompt(prompt.Text, width),
            start,
            contentHeight,
            visible,
            toggles);
    }

    private static string RenderStickyPrompt(string text, int width)
    {
        var bounded = text.Length > 512 ? text[..512] : text;
        var normalized = bounded
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        var compact = string.Join(
            ' ',
            normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var line = "› " + (compact.Length == 0 ? "…" : compact);
        return DiffRender.Ansi.UserMessage +
            DisplayText.PadToWidth(DisplayText.Ellipsize(line, width), width) +
            DiffRender.Ansi.Reset;
    }

    private sealed record UserPromptSpan(int Start, string Text);

    private sealed record TranscriptLayout(
        List<string> Lines,
        List<UserPromptSpan> Prompts,
        List<TranscriptToggle?> Toggles);

    private sealed record TranscriptLine(
        string Text,
        TranscriptToggle? Toggle = null);

    private sealed record TranscriptToggle(
        string Key,
        TranscriptToggleKind Kind);

    private enum TranscriptToggleKind
    {
        CodeBlock,
        Diff,
        Tool,
        Reasoning
    }

    private sealed record ChatTranscriptWindow(
        string? Header,
        int Start,
        int VisibleHeight,
        List<string> Visible,
        List<TranscriptToggle?> Toggles);

    private static List<string> RenderHome(ChatTuiState state, int width)
    {
        var lines = new List<string> { string.Empty };
        foreach (var line in ChatTexts.Home(state.Language))
        {
            if (line.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            var split = line.IndexOf(" - ", StringComparison.Ordinal);
            var command = split > 0 && line.StartsWith('/') ? line[..split] : null;
            var wrapped = DisplayText.WrapToWidth(line, Math.Max(1, width - 2));
            for (var index = 0; index < wrapped.Count; index++)
            {
                if (command is not null
                    && index == 0
                    && wrapped[index].StartsWith(command, StringComparison.Ordinal))
                {
                    lines.Add(
                        "  " + DiffRender.Ansi.Bold + command + DiffRender.Ansi.Reset +
                        DiffRender.Ansi.Secondary + wrapped[index][command.Length..] +
                        DiffRender.Ansi.Reset);
                }
                else
                {
                    lines.Add(
                        "  " + DiffRender.Ansi.Secondary + wrapped[index] +
                        DiffRender.Ansi.Reset);
                }
            }
        }
        return lines;
    }

    private static string RenderHeaderTitle()
    {
        var suffix = HeaderText[2..];
        var version = suffix.IndexOf(" (", StringComparison.Ordinal);
        var name = version >= 0 ? suffix[..version] : suffix;
        var versionText = version >= 0 ? suffix[version..] : string.Empty;
        return "  " + DiffRender.Ansi.BoldAccent + ">_" + DiffRender.Ansi.Reset +
            DiffRender.Ansi.Bold + name + DiffRender.Ansi.Reset +
            DiffRender.Ansi.Secondary + versionText + DiffRender.Ansi.Reset;
    }

    private static string RenderHeaderPath(ChatTuiState state, int width)
    {
        var path = DisplayWorkspacePath(state);
        var available = Math.Max(1, width - 5);
        return DiffRender.Ansi.Fit(
            "     " + DiffRender.Ansi.Secondary +
            DisplayText.Ellipsize(path, available) + DiffRender.Ansi.Reset,
            width);
    }

    private static string DisplayWorkspacePath(ChatTuiState state) =>
        DisplayPath(string.IsNullOrWhiteSpace(state.Workspace)
            ? Environment.CurrentDirectory
            : state.Workspace);

    private static string DisplayPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            return path;
        }

        var fullPath = Path.GetFullPath(path);
        var fullHome = Path.GetFullPath(home)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (fullPath.Equals(fullHome, StringComparison.OrdinalIgnoreCase))
        {
            return "~";
        }
        return fullPath.StartsWith(
            fullHome + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)
            ? "~" + fullPath[fullHome.Length..]
            : path;
    }

    private static bool IsHome(ChatTuiState state) =>
        state.Screen == ChatScreen.Chat
        && state.Transcript.Count == 0
        && !state.IsTurnActive
        && !state.HasPendingApproval;

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
        var contentHeight = Math.Max(1, bodyHeight - header.Count);
        var editor = RenderHistoryEditor(state, width, language);
        var editorHeight = editor is null ? 0 : 1;
        List<string> body;

        if (width >= 90)
        {
            const int detailWidth = 38;
            const int gapWidth = 3;
            var leftWidth = width - detailWidth - gapWidth;
            var left = BuildHistoryList(
                view,
                selection,
                leftWidth,
                Math.Max(1, contentHeight - editorHeight),
                language,
                now);
            if (editor is not null)
            {
                left.Insert(0, editor);
            }
            var right = RenderHistoryDetails(selected, detailWidth, language);
            body = ComposeHistoryColumns(
                left,
                right,
                leftWidth,
                detailWidth,
                contentHeight);
        }
        else
        {
            var minimumListHeight = view.Tasks.Count > 0 && width >= 56 ? 5 : 1;
            var details = RenderHistoryDetails(selected, width, language);
            var detailBudget = Math.Max(
                0,
                contentHeight - editorHeight - minimumListHeight - 1);
            if (details.Count > detailBudget)
            {
                details = details.Take(detailBudget).ToList();
            }
            var listHeight = Math.Max(
                minimumListHeight,
                contentHeight - editorHeight - details.Count -
                (details.Count > 0 ? 1 : 0));
            body = BuildHistoryList(
                view,
                selection,
                width,
                listHeight,
                language,
                now);
            if (editor is not null)
            {
                body.Insert(0, editor);
            }
            if (details.Count > 0)
            {
                body.Add(string.Empty);
                body.AddRange(details);
            }
            body = PadBody(body, contentHeight);
        }

        header.AddRange(body);
        return PadBody(header, bodyHeight);
    }

    private static List<string> RenderHistoryHeader(
        ChatTuiState state,
        HistoryView view,
        int width,
        UiLanguage language)
    {
        var lines = new List<string>
        {
            DiffRender.Ansi.Bold +
            ChatTexts.HistoryTitle(language) +
            DiffRender.Ansi.Reset + "  " +
            DiffRender.Ansi.Dim +
            ChatTexts.HistoryGroupBy(language, state.HistoryGrouping) +
            "  g" +
            DiffRender.Ansi.Reset
        };

        var filterHint = width >= 80 && !HasHistoryEditor(state)
            ? RenderHintLine(
                [("Tab/Shift+Tab", ChatTexts.T(language, "switch", "切换"))])
            : string.Empty;
        var filterHintWidth = DiffRender.Ansi.VisibleWidth(filterHint);
        var tabWidth = filterHintWidth > 0
            ? Math.Max(20, width - filterHintWidth - 2)
            : width;
        var tabLines = new List<string>();
        var tabLine = string.Empty;
        foreach (var filter in Enum.GetValues<HistoryFilter>())
        {
            var label = ChatTexts.HistoryFilterLabel(language, filter) + " " +
                view.Count(filter);
            var tab = filter == state.HistoryFilter
                ? DiffRender.Ansi.ActiveTab + " " + label + " " + DiffRender.Ansi.Reset
                : DiffRender.Ansi.Dim + " " + label + " " + DiffRender.Ansi.Reset;
            var candidate = tabLine.Length == 0 ? tab : tabLine + " " + tab;
            if (tabLine.Length > 0 && DiffRender.Ansi.VisibleWidth(candidate) > tabWidth)
            {
                tabLines.Add(tabLine);
                tabLine = tab;
            }
            else
            {
                tabLine = candidate;
            }
        }
        if (tabLine.Length > 0)
        {
            tabLines.Add(tabLine);
        }
        if (filterHintWidth > 0 && tabLines.Count > 0)
        {
            var last = tabLines[^1];
            var padding = Math.Max(
                1,
                width - DiffRender.Ansi.VisibleWidth(last) - filterHintWidth);
            tabLines[^1] = last + new string(' ', padding) + filterHint;
        }
        lines.AddRange(tabLines);

        lines.Add(
            DiffRender.Ansi.Dim +
            new string('─', Math.Max(1, width)) +
            DiffRender.Ansi.Reset);
        return lines;
    }

    private static List<string> BuildHistoryList(
        HistoryView view,
        int selection,
        int width,
        int height,
        UiLanguage language,
        DateTimeOffset now)
    {
        var lines = new List<string>();
        var groupLines = new List<string>();
        var selectedIndex = -1;
        var taskIndex = 0;
        var (statusWidth, timeWidth) = HistoryColumnWidths(
            view,
            language,
            now);

        var firstGroup = true;
        foreach (var group in view.Groups)
        {
            if (!firstGroup)
            {
                groupLines.Add(string.Empty);
            }
            firstGroup = false;
            groupLines.Add(
                DiffRender.Ansi.Dim +
                DiffRender.Ansi.Fit(
                    HistoryGroupLabel(view, group, language) + "  " + group.Tasks.Count,
                    width) +
                DiffRender.Ansi.Reset);
            foreach (var task in group.Tasks)
            {
                if (taskIndex == selection)
                {
                    selectedIndex = groupLines.Count;
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

        var showHeader = height >= 3 && width >= 56;
        var scrollable = groupLines.Count >
            Math.Max(0, height - (showHeader ? 1 : 0));
        var showTopIndicator = !showHeader && scrollable && height >= 3;
        var showBottomIndicator = scrollable && height >= 3;
        var available = Math.Max(
            0,
            height -
            (showHeader ? 1 : 0) -
            (showTopIndicator ? 1 : 0) -
            (showBottomIndicator ? 1 : 0));
        var start = scrollable && selectedIndex >= 0
            ? Math.Clamp(
                selectedIndex - available + 1,
                0,
                Math.Max(0, groupLines.Count - available))
            : 0;

        if (showTopIndicator)
        {
            lines.Add(RenderHistoryScrollIndicator(start > 0 ? "↑" : " ", width));
        }
        if (showHeader)
        {
            lines.Add(RenderHistoryTableHeader(
                width,
                statusWidth,
                timeWidth,
                language,
                start > 0));
        }
        lines.AddRange(groupLines.Skip(start).Take(available));
        if (showBottomIndicator)
        {
            lines.Add(RenderHistoryScrollIndicator(
                start + available < groupLines.Count ? "↓" : " ",
                width));
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
        var (dot, dotColor) = HistoryStatusVisual(task.Status);
        var dotText = selected
            ? dot + " "
            : dotColor + dot + DiffRender.Ansi.Reset + " ";
        var status = DisplayText.PadToWidth(
            ChatTexts.HistoryStatus(language, task.Status),
            statusWidth);
        var time = PadLeftToWidth(
            FormatHistoryTime(task.Session.UpdatedAt, language, now),
            timeWidth);
        var titleWidth = Math.Max(
            1,
            width - DisplayText.Width(marker) - DiffRender.Ansi.VisibleWidth(dotText) -
            statusWidth - timeWidth - 4);
        var title = DisplayText.PadToWidth(
            DisplayText.Ellipsize(TaskTitle(task.Session, language), titleWidth),
            titleWidth);
        var titleText = selected
            ? title
            : TaskTitleColor(task.Session.SessionId) + title + DiffRender.Ansi.Reset;
        var timeText = selected
            ? time
            : DiffRender.Ansi.Dim + time + DiffRender.Ansi.Reset;
        var row = DiffRender.Ansi.Fit(
            marker + dotText + titleText + "  " + status + "  " + timeText,
            width);
        return selected
            ? DiffRender.Ansi.Selection + row + DiffRender.Ansi.Reset
            : row;
    }

    private static (int Status, int Time) HistoryColumnWidths(
        HistoryView view,
        UiLanguage language,
        DateTimeOffset now)
    {
        var statusWidth = Enum.GetValues<HistoryTaskStatus>()
            .Max(status => DisplayText.Width(ChatTexts.HistoryStatus(language, status)));
        var timeWidth = Math.Max(
            6,
            view.Tasks.Count == 0
                ? 0
                : view.Tasks.Max(task => DisplayText.Width(
                    FormatHistoryTime(task.Session.UpdatedAt, language, now))));
        return (statusWidth, timeWidth);
    }

    private static string RenderHistoryTableHeader(
        int width,
        int statusWidth,
        int timeWidth,
        UiLanguage language,
        bool hasMoreAbove)
    {
        var titleWidth = Math.Max(1, width - statusWidth - timeWidth - 8);
        var gutter = hasMoreAbove ? "↑   " : "    ";
        var header = DiffRender.Ansi.Fit(
            gutter +
            DisplayText.PadToWidth(ChatTexts.TasksColumn(language), titleWidth) +
            "  " +
            DisplayText.PadToWidth(ChatTexts.StatusColumn(language), statusWidth) +
            "  " +
            PadLeftToWidth(ChatTexts.UpdatedColumn(language), timeWidth),
            width);
        return DiffRender.Ansi.Dim + header + DiffRender.Ansi.Reset;
    }

    private static string RenderHistoryScrollIndicator(string marker, int width) =>
        DiffRender.Ansi.Fit(
            DiffRender.Ansi.Dim + marker + DiffRender.Ansi.Reset,
            width);

    private static (string Symbol, string Color) HistoryStatusVisual(
        HistoryTaskStatus status) =>
        status switch
        {
            HistoryTaskStatus.NeedsYou => ("●", DiffRender.Ansi.Red),
            HistoryTaskStatus.Working => ("●", DiffRender.Ansi.Green),
            HistoryTaskStatus.Ready => ("○", DiffRender.Ansi.Cyan),
            _ => ("○", DiffRender.Ansi.Dim)
        };

    private static List<string> RenderHistoryDetails(
        HistoryTask? task,
        int width,
        UiLanguage language)
    {
        var lines = new List<string>
        {
            DiffRender.Ansi.Bold + ChatTexts.TaskDetails(language) + DiffRender.Ansi.Reset
        };
        if (task is null)
        {
            lines.Add(string.Empty);
            lines.Add(ChatTexts.NoTaskSelected(language));
            return lines;
        }

        var session = task.Session;
        lines.Add(string.Empty);
        AddWrappedStyled(
            lines,
            TaskTitle(session, language),
            width,
            DiffRender.Ansi.Bold + TaskTitleColor(session.SessionId));
        var (symbol, color) = HistoryStatusVisual(task.Status);
        lines.Add(
            color + symbol + DiffRender.Ansi.Reset + " " +
            ChatTexts.HistoryStatus(language, task.Status));
        lines.Add(string.Empty);
        var model = string.IsNullOrWhiteSpace(session.Provider)
            && string.IsNullOrWhiteSpace(session.Model)
                ? "—"
                : $"{session.Provider}/{session.Model}".Trim('/');
        AddDetailField(lines, ChatTexts.ModelLabel(language), model, width, language);
        AddDetailField(
            lines,
            ChatTexts.ReasoningLabel(language),
            ValueOrDash(session.ReasoningEffort),
            width,
            language);
        lines.Add(string.Empty);
        lines.Add(DiffRender.Ansi.Dim + ChatTexts.ProjectLabel(language) + DiffRender.Ansi.Reset);
        AddWrapped(lines, WorkspaceLabel(session.Workspace), width);
        lines.Add(string.Empty);
        lines.Add(DiffRender.Ansi.Dim + ChatTexts.PromptLabel(language) + DiffRender.Ansi.Reset);
        AddWrapped(lines, ValueOrDash(session.Prompt), width);
        if (!string.IsNullOrWhiteSpace(session.LastMessage))
        {
            lines.Add(string.Empty);
            lines.Add(
                DiffRender.Ansi.Dim +
                ChatTexts.LastMessageLabel(language) +
                DiffRender.Ansi.Reset);
            AddWrapped(lines, session.LastMessage.Trim(), width);
        }
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
        var separator = DiffRender.Ansi.Dim + "│" + DiffRender.Ansi.Reset;
        for (var row = 0; row < height; row++)
        {
            var leftText = row < left.Count ? left[row] : string.Empty;
            var rightText = row < right.Count ? right[row] : string.Empty;
            lines.Add(
                DiffRender.Ansi.Fit(leftText, leftWidth) + " " +
                separator + " " +
                DiffRender.Ansi.Fit(rightText, rightWidth));
        }
        return lines;
    }

    private static bool HasHistoryEditor(ChatTuiState state) =>
        state.HistoryRenaming
        || state.HistorySearching
        || state.HistorySearch.Length > 0;

    private static string? RenderHistoryEditor(
        ChatTuiState state,
        int width,
        UiLanguage language)
    {
        if (state.HistoryRenaming)
        {
            return RenderHistoryRenameLine(state, width, language);
        }
        if (state.HistorySearching || state.HistorySearch.Length > 0)
        {
            return RenderHistorySearchLine(state, width, language);
        }
        return null;
    }
    private static string RenderHistorySearchLine(
        ChatTuiState state,
        int width,
        UiLanguage language) =>
        RenderHistoryEditorLine(
            ChatTexts.HistorySearch(language) + " › ",
            state.HistorySearch,
            state.HistorySearchCursor,
            state.HistorySearching,
            width);

    private static string RenderHistoryRenameLine(
        ChatTuiState state,
        int width,
        UiLanguage language) =>
        RenderHistoryEditorLine(
            ChatTexts.HistoryRename(language) + " › ",
            state.HistoryRenameInput,
            state.HistoryRenameCursor,
            editing: true,
            width);

    private static string RenderHistoryEditorLine(
        string prefix,
        string query,
        int cursorValue,
        bool editing,
        int width)
    {
        var contentWidth = Math.Max(0, width - DisplayText.Width(prefix));
        string content;
        if (contentWidth == 0)
        {
            content = string.Empty;
        }
        else if (!editing)
        {
            content = DiffRender.Ansi.TruncateVisible(query, contentWidth);
        }
        else
        {
            var cursor = Math.Clamp(cursorValue, 0, query.Length);
            if (DisplayText.Width(query) + 1 <= contentWidth)
            {
                content = query.Insert(cursor, "|");
            }
            else
            {
                var available = Math.Max(0, contentWidth - 1);
                var leftWidth = (available + 1) / 2;
                var rightWidth = available - leftWidth;
                var before = query[..cursor];
                var after = query[cursor..];
                content = TailToWidth(before, leftWidth) +
                    "|" +
                    DisplayText.TruncateToWidth(after, rightWidth);
            }
        }

        return DiffRender.Ansi.ActiveTab +
            DiffRender.Ansi.Fit(
                DiffRender.Ansi.BoldCyan +
                prefix +
                DiffRender.Ansi.ResetForeground +
                content,
                width) +
            DiffRender.Ansi.Reset;
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

    private static string TaskTitleColor(string sessionId) =>
        TaskTitleColors[StableColorIndex(sessionId)];

    private static (string ModelColor, string PathColor, string TitleColor) ContextColors(
        ChatTuiState state)
    {
        var random = new Random(state.ContextColorSeed);
        var indices = new List<int>(3);
        while (indices.Count < 3)
        {
            var index = random.Next(TaskTitleColors.Length);
            if (!indices.Contains(index))
            {
                indices.Add(index);
            }
        }
        return (
            TaskTitleColors[indices[0]],
            TaskTitleColors[indices[1]],
            TaskTitleColors[indices[2]]);
    }

    private static string ConversationTitle(ChatTuiState state)
    {
        var current = state.History.FirstOrDefault(
            session => session.SessionId == state.SessionId);
        if (!string.IsNullOrWhiteSpace(current?.Task))
        {
            return current.Task.Trim();
        }

        var firstUser = state.Transcript
            .FirstOrDefault(entry => entry.Kind == TranscriptKind.User)
            ?.Text;
        if (!string.IsNullOrWhiteSpace(firstUser))
        {
            return firstUser.Trim().Split('\n')[0];
        }
        return ChatTexts.T(state.Language, "New chat", "新对话");
    }

    private static int StableColorIndex(string value)
    {
        var hash = 14695981039346656037UL;
        foreach (var character in value)
        {
            hash = (hash ^ (byte)character) * 1099511628211UL;
        }
        return (int)(hash % (ulong)TaskTitleColors.Length);
    }

    private static string WorkspaceLabel(string workspace) =>
        string.IsNullOrWhiteSpace(workspace) ? "—" : DisplayPath(workspace);

    private static string HistoryGroupLabel(
        HistoryView view,
        HistoryGroup group,
        UiLanguage language) =>
        view.Grouping switch
        {
            HistoryGrouping.Status => group.Tasks.Count > 0
                ? ChatTexts.HistoryStatus(language, group.Tasks[0].Status)
                : group.Label,
            HistoryGrouping.Model => group.Label,
            _ => WorkspaceLabel(group.Label)
        };

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
        if (state.ShortcutReturnScreen == ChatScreen.History)
        {
            return RenderHistoryShortcutsBody(state.Language, width, bodyHeight);
        }

        var language = state.Language;
        var groups = ChatTexts.ChatShortcutGroups(language);
        var lines = RenderHome(state, width);
        lines.Add(string.Empty);
        lines.Add(DiffRender.Ansi.Bold + ChatTexts.ShortcutsTitle(language) + DiffRender.Ansi.Reset);
        lines.Add(string.Empty);

        if (width >= 70)
        {
            var columnWidth = Math.Max(18, (width - 4) / groups.Count);
            var keyWidths = groups
                .Select(group => group.Entries.Max(entry => DisplayText.Width(entry.Key)))
                .ToArray();
            lines.Add(DiffRender.Ansi.Fit(
                string.Join(
                    "  ",
                    groups.Select(group =>
                        DiffRender.Ansi.Fit(
                            DiffRender.Ansi.Bold + group.Title + DiffRender.Ansi.Reset,
                            columnWidth))),
                width));
            var rows = groups.Max(group => group.Entries.Count);
            for (var row = 0; row < rows; row++)
            {
                var columns = new List<string>(groups.Count);
                for (var column = 0; column < groups.Count; column++)
                {
                    var entries = groups[column].Entries;
                    if (row >= entries.Count)
                    {
                        columns.Add(DiffRender.Ansi.Fit(string.Empty, columnWidth));
                        continue;
                    }
                    var entry = entries[row];
                    var padding = keyWidths[column] - DisplayText.Width(entry.Key) + 2;
                    columns.Add(DiffRender.Ansi.Fit(
                        DiffRender.Ansi.BoldAccent + entry.Key + DiffRender.Ansi.Reset +
                        new string(' ', padding) + entry.Label,
                        columnWidth));
                }
                lines.Add(DiffRender.Ansi.Fit(string.Join("  ", columns), width));
            }
        }
        else
        {
            foreach (var group in groups)
            {
                lines.Add(DiffRender.Ansi.Bold + group.Title + DiffRender.Ansi.Reset);
                var keyWidth = group.Entries.Max(entry => DisplayText.Width(entry.Key));
                foreach (var entry in group.Entries)
                {
                    var padding = keyWidth - DisplayText.Width(entry.Key) + 2;
                    lines.Add(DiffRender.Ansi.Fit(
                        DiffRender.Ansi.BoldAccent + entry.Key + DiffRender.Ansi.Reset +
                        new string(' ', padding) + entry.Label,
                        width));
                }
                lines.Add(string.Empty);
            }
        }

        lines.Add(string.Empty);
        var settingsLine =
            DiffRender.Ansi.Accent + "/settings" + DiffRender.Ansi.Reset +
            DiffRender.Ansi.Secondary + " " +
            ChatTexts.T(language, "customize", "自定义") +
            DiffRender.Ansi.Reset;
        lines.Add(settingsLine);
        if (lines.Count > bodyHeight)
        {
            var title = DiffRender.Ansi.Bold + ChatTexts.ShortcutsTitle(language) +
                DiffRender.Ansi.Reset;
            var titleIndex = lines.IndexOf(title);
            if (titleIndex > 0 && lines[titleIndex - 1].Length == 0)
            {
                lines.RemoveAt(titleIndex - 1);
                titleIndex--;
            }
            if (titleIndex >= 0
                && titleIndex + 1 < lines.Count
                && lines[titleIndex + 1].Length == 0)
            {
                lines.RemoveAt(titleIndex + 1);
            }
        }
        if (lines.Count > bodyHeight)
        {
            lines = lines.Take(Math.Max(0, bodyHeight - 1)).ToList();
            lines.Add(settingsLine);
        }
        return PadBody(lines, bodyHeight);
    }

    private static List<string> RenderHistoryShortcutsBody(
        UiLanguage language,
        int width,
        int bodyHeight)
    {
        var groups = new (string Title, IReadOnlyList<(string Key, string Label)> Entries)[]
        {
            (ChatTexts.T(language, "Navigate", "导航"),
                ChatTexts.HistoryNavigateShortcuts(language)),
            (ChatTexts.T(language, "Tasks", "任务"),
                ChatTexts.HistoryTaskShortcuts(language)),
            (ChatTexts.T(language, "View", "视图"),
                ChatTexts.HistoryViewShortcuts(language))
        };
        var lines = new List<string>
        {
            DiffRender.Ansi.Bold + ChatTexts.HistoryShortcutsTitle(language) + DiffRender.Ansi.Reset,
            string.Empty
        };

        if (width >= 70)
        {
            var columnWidth = Math.Max(18, (width - 4) / groups.Length);
            var keyWidths = groups
                .Select(group => group.Entries.Max(entry => DisplayText.Width(entry.Key)))
                .ToArray();
            lines.Add(DiffRender.Ansi.Fit(
                string.Join(
                    "  ",
                    groups.Select(group =>
                        DiffRender.Ansi.Fit(
                            DiffRender.Ansi.Bold + group.Title + DiffRender.Ansi.Reset,
                            columnWidth))),
                width));
            var rows = groups.Max(group => group.Entries.Count);
            for (var row = 0; row < rows; row++)
            {
                var columns = new List<string>(groups.Length);
                for (var column = 0; column < groups.Length; column++)
                {
                    var entries = groups[column].Entries;
                    if (row >= entries.Count)
                    {
                        columns.Add(DiffRender.Ansi.Fit(string.Empty, columnWidth));
                        continue;
                    }
                    var entry = entries[row];
                    var padding = keyWidths[column] - DisplayText.Width(entry.Key) + 2;
                    columns.Add(DiffRender.Ansi.Fit(
                        DiffRender.Ansi.BoldAccent + entry.Key + DiffRender.Ansi.Reset +
                        new string(' ', padding) + entry.Label,
                        columnWidth));
                }
                lines.Add(DiffRender.Ansi.Fit(string.Join("  ", columns), width));
            }
        }
        else
        {
            foreach (var group in groups)
            {
                lines.Add(DiffRender.Ansi.Bold + group.Title + DiffRender.Ansi.Reset);
                foreach (var entry in group.Entries)
                {
                    lines.Add(
                        DiffRender.Ansi.BoldAccent + entry.Key + DiffRender.Ansi.Reset +
                        "  " + entry.Label);
                }
                lines.Add(string.Empty);
            }
        }
        return PadBody(lines.Take(bodyHeight).ToList(), bodyHeight);
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
        else if (MentionPicker.IsOpen(state))
        {
            overlay = RenderMentionPicker(state, width, bodyHeight);
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
            var marker = index == selected ? "› " : "  ";
            var name = DisplayText.PadToWidth(command.Name, nameWidth);
            if (index == selected)
            {
                var row = marker + name + "  " + description;
                lines.Add(
                    DiffRender.Ansi.Inverse +
                    DiffRender.Ansi.Fit(row, width) +
                    DiffRender.Ansi.Reset);
            }
            else
            {
                lines.Add(DiffRender.Ansi.Fit(
                    marker + DiffRender.Ansi.Bold + name + DiffRender.Ansi.Reset +
                    "  " + DiffRender.Ansi.Secondary + description +
                    DiffRender.Ansi.Reset,
                    width));
            }
        }
        return lines;
    }

    public static List<string> MentionPickerLines(ChatTuiState state, int width) =>
        RenderMentionPicker(state, Math.Max(20, width), int.MaxValue);

    private static List<string> RenderMentionPicker(
        ChatTuiState state,
        int width,
        int maximumLines)
    {
        if (!MentionPicker.IsOpen(state) || maximumLines <= 0)
        {
            return [];
        }

        var language = state.Language;
        var matches = state.MentionPicker.Matches;
        var query = state.MentionPicker.Query;
        var lines = new List<string>
        {
            DiffRender.Ansi.Bold + ChatTexts.MentionTitle(language) + DiffRender.Ansi.Reset,
            DiffRender.Ansi.Secondary + ChatTexts.MentionDescription(language) + DiffRender.Ansi.Reset,
            string.Empty,
            RenderMentionTabs(state, width, language),
            string.Empty,
            DiffRender.Ansi.Secondary +
                (query.Length == 0 ? ChatTexts.MentionSearchPrompt(language) : query) +
                DiffRender.Ansi.Reset,
            string.Empty
        };

        const int panelChrome = 9;
        var availableRows = Math.Min(
            MentionPicker.MaxVisible,
            Math.Max(0, maximumLines - panelChrome));
        var selected = matches.Count == 0
            ? 0
            : Math.Clamp(state.MentionPicker.Selection, 0, matches.Count - 1);
        var start = matches.Count == 0
            ? 0
            : Math.Clamp(
                selected - availableRows + 1,
                0,
                Math.Max(0, matches.Count - availableRows));
        var count = Math.Min(availableRows, Math.Max(0, matches.Count - start));
        if (matches.Count == 0)
        {
            lines.Add(
                "  " + DiffRender.Ansi.Secondary +
                ChatTexts.NoFilesFound(language) + DiffRender.Ansi.Reset);
        }
        else
        {
            var maxNameWidth = matches
                .Select(MentionName)
                .Max(name => DisplayText.Width(name));
            for (var index = start; index < start + count; index++)
            {
                lines.Add(RenderMentionRow(
                    matches[index],
                    index == selected,
                    width,
                    maxNameWidth,
                    language));
            }
        }

        lines.Add(string.Empty);
        lines.Add(DiffRender.Ansi.Fit(
            RenderInlineHint(
                [
                    ("enter/tab", ChatTexts.T(language, "insert", "插入")),
                    ("esc", ChatTexts.T(language, "close", "关闭")),
                    ("↑/↓", ChatTexts.T(language, "select", "选择")),
                    ("←/→", ChatTexts.T(language, "filter", "过滤"))
                ]),
            width));
        return lines.Take(Math.Max(1, maximumLines)).ToList();
    }

    private static string RenderMentionTabs(
        ChatTuiState state,
        int width,
        UiLanguage language)
    {
        var filters = Enum.GetValues<MentionFilter>();
        var tabs = filters.Select(filter =>
        {
            var label = ChatTexts.MentionFilterLabel(language, filter);
            return filter == state.MentionPicker.Filter
                ? DiffRender.Ansi.ActiveTab + " " + label + " " + DiffRender.Ansi.Reset
                : DiffRender.Ansi.Secondary + "  " + label + "  " + DiffRender.Ansi.Reset;
        });
        return DiffRender.Ansi.Fit(string.Join(" ", tabs), width);
    }

    private static string RenderMentionRow(
        string path,
        bool selected,
        int width,
        int maxNameWidth,
        UiLanguage language)
    {
        var name = MentionName(path);
        var parent = MentionParent(path);
        var type = ChatTexts.MentionFileType(language);
        var typeWidth = Math.Max(4, DisplayText.Width(type));
        var contentWidth = Math.Max(1, width - 2 - typeWidth - 2);
        var nameWidth = Math.Min(
            Math.Max(4, contentWidth / 2),
            Math.Max(4, maxNameWidth));
        var pathWidth = Math.Max(0, contentWidth - nameWidth - 2);
        var nameCell = DisplayText.PadToWidth(
            DisplayText.Ellipsize(name, nameWidth),
            nameWidth);
        var pathCell = pathWidth > 0
            ? DisplayText.PadToWidth(DisplayText.Ellipsize(parent, pathWidth), pathWidth)
            : string.Empty;
        var typeCell = PadLeftToWidth(type, typeWidth);
        var marker = selected ? "› " : "  ";
        if (selected)
        {
            var plain = marker + nameCell +
                (pathWidth > 0 ? "  " + pathCell : string.Empty) +
                "  " + typeCell;
            return DiffRender.Ansi.Selection +
                DiffRender.Ansi.Fit(plain, width) +
                DiffRender.Ansi.Reset;
        }

        return DiffRender.Ansi.Fit(
            marker + DiffRender.Ansi.Cyan + nameCell + DiffRender.Ansi.Reset +
            (pathWidth > 0
                ? "  " + DiffRender.Ansi.Secondary + pathCell + DiffRender.Ansi.Reset
                : string.Empty) +
            "  " + typeCell,
            width);
    }

    private static string MentionName(string path)
    {
        var name = Path.GetFileName(path);
        return name.Length == 0 ? path : name;
    }

    private static string MentionParent(string path)
    {
        var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
        return string.IsNullOrEmpty(parent) ? "./" : parent.Replace('\\', '/') + "/";
    }
    private static List<string> RenderInputLines(
        ChatTuiState state,
        int width,
        int height)
    {
        var (lines, cursorRow) = BuildComposerDisplay(state, width);
        var visible = Math.Min(lines.Count, ComposerTextRowLimit(height));
        var maximum = Math.Max(0, lines.Count - visible);
        var scroll = Math.Clamp(cursorRow - visible + 1, 0, maximum);
        if (cursorRow < scroll)
        {
            scroll = cursorRow;
        }

        var rule = DiffRender.Ansi.Secondary + new string('─', width) + DiffRender.Ansi.Reset;
        var rows = new List<string>(visible + 2) { rule };
        for (var row = 0; row < visible; row++)
        {
            var index = scroll + row;
            var prefix = index == cursorRow
                ? DiffRender.Ansi.Bold + "›" + DiffRender.Ansi.Reset + " "
                : "  ";
            var content = state.Input.Length == 0
                ? DiffRender.Ansi.Secondary + lines[index] + DiffRender.Ansi.Reset
                : lines[index];
            rows.Add(DiffRender.Ansi.Fit(prefix + content, width));
        }
        rows.Add(rule);
        return rows;
    }

    private static (List<string> Lines, int CursorRow) BuildComposerDisplay(
        ChatTuiState state,
        int width)
    {
        var contentWidth = Math.Max(1, width - 5);
        if (state.Input.Length == 0)
        {
            return (new List<string> { ChatTexts.InputPlaceholder(state.Language) }, 0);
        }

        var cursor = Math.Clamp(state.Cursor, 0, state.Input.Length);
        var ranges = WrapComposerRanges(state.Input, contentWidth);
        var (cursorRow, _) = LocateComposerCursor(state.Input, ranges, cursor);
        var lines = new List<string>(ranges.Count);
        for (var index = 0; index < ranges.Count; index++)
        {
            var range = ranges[index];
            var line = state.Input[range.Start..range.End];
            if (index == cursorRow)
            {
                var offset = Math.Clamp(cursor - range.Start, 0, line.Length);
                line = line.Insert(offset, "|");
            }
            lines.Add(line);
        }
        return (lines, cursorRow);
    }

    private static List<(int Start, int End)> WrapComposerRanges(string text, int width)
    {
        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = new List<(int Start, int End)>();
        var position = 0;
        while (true)
        {
            var newline = normalized.IndexOf('\n', position);
            var end = newline >= 0 ? newline : normalized.Length;
            AddComposerRange(normalized, position, end, width, lines);
            if (newline < 0)
            {
                break;
            }
            position = newline + 1;
            if (position == normalized.Length)
            {
                lines.Add((position, position));
                break;
            }
        }
        return lines.Count == 0 ? [(0, 0)] : lines;
    }

    private static void AddComposerRange(
        string text,
        int start,
        int end,
        int width,
        List<(int Start, int End)> lines)
    {
        if (start == end)
        {
            lines.Add((start, end));
            return;
        }

        var lineStart = start;
        var index = start;
        var used = 0;
        while (index < end)
        {
            var element = StringInfo.GetNextTextElement(text, index);
            var elementWidth = DisplayText.Width(element);
            if (used > 0 && used + elementWidth > width)
            {
                lines.Add((lineStart, index));
                lineStart = index;
                used = 0;
            }
            used += elementWidth;
            index += element.Length;
            if (used >= width)
            {
                lines.Add((lineStart, index));
                lineStart = index;
                used = 0;
            }
        }
        if (lineStart < end)
        {
            lines.Add((lineStart, end));
        }
    }

    private static (int Row, int Column) LocateComposerCursor(
        string text,
        IReadOnlyList<(int Start, int End)> ranges,
        int cursor)
    {
        cursor = Math.Clamp(cursor, 0, text.Length);
        var row = 0;
        for (var index = 0; index < ranges.Count; index++)
        {
            if (cursor >= ranges[index].Start && cursor <= ranges[index].End)
            {
                row = index;
            }
        }
        var range = ranges[row];
        return (row, DisplayText.Width(text[range.Start..cursor]));
    }

    private static int IndexAtVisualColumn(string text, int start, int end, int column)
    {
        var index = start;
        var used = 0;
        while (index < end)
        {
            var element = StringInfo.GetNextTextElement(text, index);
            var width = DisplayText.Width(element);
            if (used + width > column)
            {
                break;
            }
            used += width;
            index += element.Length;
            if (used == column)
            {
                break;
            }
        }
        return index;
    }
    private static int ComposerTextRowLimit(int height) =>
        Math.Clamp(height - 8, 1, 6);

    private static string RenderModelLine(ChatTuiState state, int width)
    {
        var model = string.IsNullOrWhiteSpace(state.Provider)
            ? ChatTexts.T(state.Language, "no provider configured", "未配置 Provider")
            : $"{state.Provider}/{state.Model}";
        var effort = string.IsNullOrWhiteSpace(state.ReasoningEffort)
            || state.ReasoningEffort.Equals("default", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : state.ReasoningEffort;
        var path = DisplayWorkspacePath(state);
        var title = ConversationTitle(state);
        var (modelColor, pathColor, titleColor) = ContextColors(state);
        var separator = DiffRender.Ansi.Secondary + " · " + DiffRender.Ansi.Reset;
        var baseLine = "  " + modelColor + DiffRender.Ansi.Bold + model +
            DiffRender.Ansi.Reset;
        if (effort.Length > 0)
        {
            baseLine += " " + modelColor + effort + DiffRender.Ansi.Reset;
        }
        baseLine += separator;
        var remaining = width - DiffRender.Ansi.VisibleWidth(baseLine) -
            DiffRender.Ansi.VisibleWidth(separator);
        if (remaining <= 0)
        {
            return DiffRender.Ansi.Fit(baseLine, width);
        }

        var pathText = DisplayText.Ellipsize(
            path,
            Math.Max(1, remaining / 2));
        var titleBudget = Math.Max(
            1,
            remaining - DisplayText.Width(pathText));
        var titleText = DisplayText.Ellipsize(title, titleBudget);
        return DiffRender.Ansi.Fit(
            baseLine + pathColor + pathText + DiffRender.Ansi.Reset +
            separator + titleColor + titleText + DiffRender.Ansi.Reset,
            width);
    }

    private static string RenderFooter(ChatTuiState state, int width)
    {
        var language = state.Language;
        if (state.Screen == ChatScreen.History)
        {
            return RenderHistoryFooter(state, width);
        }

        if (MentionPicker.IsOpen(state))
        {
            return string.Empty;
        }

        if (state.Screen == ChatScreen.Shortcuts
            && state.ShortcutReturnScreen == ChatScreen.History)
        {
            return RenderHintLine(
                [("Esc", ChatTexts.T(state.Language, "back", "返回"))]);
        }

        if (state.Screen == ChatScreen.Shortcuts)
        {
            return "  " + DiffRender.Ansi.Bold + "?" + DiffRender.Ansi.Reset +
                DiffRender.Ansi.Secondary + " / " + DiffRender.Ansi.Reset +
                DiffRender.Ansi.Bold + "esc" + DiffRender.Ansi.Reset +
                DiffRender.Ansi.Secondary + " close" + DiffRender.Ansi.Reset;
        }

        string hints;
        if (state.ModelPicker.IsOpen)
        {
            hints = ChatTexts.ModelPickerFooter(language);
        }

        else if (ChatCommandPalette.IsOpen(state))
        {
            hints = ChatTexts.PaletteFooter(language);
        }
        else if (state.HasPendingApproval)
        {
            hints = ChatTexts.ApprovalFooter(language);
        }
        else if (state.IsTurnActive)
        {
            hints = ChatTexts.WorkingFooter(language);
        }
        else
        {
            hints = RenderAgentShortcutsHint(language);
        }

        var right = new List<string>();
        if (!string.IsNullOrWhiteSpace(state.StatusMessage))
        {
            right.Add(DiffRender.Ansi.Dim + state.StatusMessage + DiffRender.Ansi.Reset);
        }
        if (state.IsTurnActive)
        {
            right.Add(
                DiffRender.Ansi.Green +
                ChatTexts.T(language, "Working…", "运行中…") +
                DiffRender.Ansi.Reset);
        }
        if (state.WarningCount > 0)
        {
            right.Add(
                DiffRender.Ansi.Warning +
                ChatTexts.WarningNoticeLabel(language, state.WarningCount) +
                DiffRender.Ansi.Reset + DiffRender.Ansi.Secondary + " · " +
                DiffRender.Ansi.Bold + "f2" + DiffRender.Ansi.Reset +
                DiffRender.Ansi.Secondary +
                ChatTexts.T(language, " to view", " 查看") + DiffRender.Ansi.Reset);
        }
        if (right.Count == 0)
        {
            return hints;
        }

        return RenderFooterWithStatus(
            hints,
            string.Join("  ", right),
            width);
    }

    private static string RenderHistoryFooter(ChatTuiState state, int width)
    {
        if (state.HistoryRenaming)
        {
            return RenderHintLine(
                [
                    ("Enter", ChatTexts.T(state.Language, "save", "保存")),
                    ("Esc", ChatTexts.T(state.Language, "cancel", "取消"))
                ]);
        }

        if (state.PendingHistoryDeleteSessionId is not null)
        {
            return RenderHintLine(
                [
                    ("delete", ChatTexts.HistoryDeleteConfirm(state.Language)),
                    ("Esc", ChatTexts.HistoryDeleteCancel(state.Language))
                ]);
        }

        var line = RenderHintLine(
            ChatTexts.HistoryFooterHints(state.Language, state.HistorySearching));
        if (string.IsNullOrWhiteSpace(state.StatusMessage))
        {
            return line;
        }
        var status = DiffRender.Ansi.Dim + state.StatusMessage + DiffRender.Ansi.Reset;
        return RenderFooterWithStatus(line, status, width);
    }

    private static string RenderFooterWithStatus(
        string hints,
        string? status,
        int width)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return hints;
        }

        var statusWidth = DiffRender.Ansi.VisibleWidth(status);
        if (statusWidth >= width)
        {
            return DiffRender.Ansi.Fit(status, width);
        }
        var hintWidth = width - statusWidth - 1;
        if (hintWidth <= 0)
        {
            return new string(' ', width - statusWidth) + status;
        }
        if (DiffRender.Ansi.VisibleWidth(hints) > hintWidth)
        {
            hints = DiffRender.Ansi.TruncateVisible(hints, hintWidth);
        }
        var gap = Math.Max(1, width - DiffRender.Ansi.VisibleWidth(hints) - statusWidth);
        return hints + new string(' ', gap) + status;
    }

    private static string RenderHintLine(
        IEnumerable<(string Key, string Label)> hints) =>
        string.Join(
            "  ",
            hints.Select(hint =>
                DiffRender.Ansi.Bold + hint.Key + DiffRender.Ansi.Reset + " " +
                DiffRender.Ansi.Dim + hint.Label + DiffRender.Ansi.Reset));

    private static string RenderAgentShortcutsHint(UiLanguage language) =>
        "  " + RenderInlineHint(
            [
                ("←", ChatTexts.T(language, "for agents", "代理")),
                ("?", ChatTexts.T(language, "for shortcuts", "快捷键"))
            ]);
    private static string RenderInlineHint(
        IEnumerable<(string Key, string Label)> hints) =>
        string.Join(
            " · ",
            hints.Select(hint =>
                DiffRender.Ansi.Bold + hint.Key + DiffRender.Ansi.Reset + " " +
                DiffRender.Ansi.Dim + hint.Label + DiffRender.Ansi.Reset));
    private static IEnumerable<string> RenderEntry(
        TranscriptEntry entry,
        int width,
        UiLanguage language)
    {
        switch (entry.Kind)
        {
            case TranscriptKind.User:
                return RenderUserMessage(entry.Text, width);

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

    private static List<TranscriptLine> RenderAssistantLines(
        ChatTuiState state,
        int entryIndex,
        string text,
        int width,
        UiLanguage language)
    {
        var result = new List<TranscriptLine>();
        var prose = new List<string>();
        var code = new List<string>();
        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var bulletShown = false;
        var inCode = false;
        var blockIndex = 0;
        var codeLanguage = string.Empty;

        void FlushProse()
        {
            foreach (var sourceLine in prose)
            {
                if (string.IsNullOrWhiteSpace(sourceLine))
                {
                    result.Add(new TranscriptLine(string.Empty));
                    continue;
                }
                var label = bulletShown
                    ? string.Empty
                    : ChatTexts.AssistantLabel(language);
                result.AddRange(RenderAssistantProseLine(sourceLine, width, label));
                bulletShown = true;
            }
            prose.Clear();
        }

        void FlushCode()
        {
            var key = $"code:{entryIndex}:{blockIndex}";
            var expanded = state.IsTranscriptItemExpanded(key);
            var marker = expanded ? "▾" : "▸";
            var count = code.Count == 1
                ? ChatTexts.T(language, "1 line", "1 行")
                : ChatTexts.T(language, $"{code.Count} lines", $"{code.Count} 行");
            var heading = string.IsNullOrEmpty(codeLanguage)
                ? ChatTexts.T(language, $"Code · {count}", $"代码 · {count}")
                : ChatTexts.T(
                    language,
                    $"Code · {count} · {codeLanguage}",
                    $"代码 · {count} · {codeLanguage}");
            var label = bulletShown ? string.Empty : ChatTexts.AssistantLabel(language);
            bulletShown = true;
            result.Add(new TranscriptLine(
                DiffRender.Ansi.Fit(
                    label +
                    DiffRender.Ansi.Accent +
                    marker +
                    DiffRender.Ansi.Reset +
                    DiffRender.Ansi.Secondary +
                    " " +
                    heading +
                    DiffRender.Ansi.Reset,
                    width),
                new TranscriptToggle(key, TranscriptToggleKind.CodeBlock)));
            if (expanded)
            {
                foreach (var codeLine in code)
                {
                    foreach (var wrapped in DisplayText.WrapToWidth("  " + codeLine, width))
                    {
                        result.Add(new TranscriptLine(
                            DiffRender.Ansi.Cyan + wrapped + DiffRender.Ansi.Reset));
                    }
                }
            }
            code.Clear();
        }

        foreach (var line in normalized.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode)
                {
                    FlushCode();
                    inCode = false;
                    blockIndex++;
                }
                else
                {
                    FlushProse();
                    inCode = true;
                    codeLanguage = trimmed[3..].Trim();
                }
                continue;
            }
            if (inCode)
            {
                code.Add(line);
            }
            else
            {
                prose.Add(line);
            }
        }
        if (inCode)
        {
            FlushCode();
        }
        else
        {
            FlushProse();
        }
        return result;
    }
    private static List<TranscriptLine> RenderAssistantProseLine(
        string sourceLine,
        int width,
        string label)
    {
        var raw = sourceLine.TrimEnd();
        var indentLength = raw.Length - raw.TrimStart(' ').Length;
        var indent = raw[..indentLength];
        var body = raw[indentLength..];
        if (body.Length == 0)
        {
            return [new TranscriptLine(string.Empty)];
        }

        var rule = body.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (rule.Length >= 3
            && (rule.All(character => character == '-')
                || rule.All(character => character == '*')
                || rule.All(character => character == '_')))
        {
            return
            [
                new TranscriptLine(
                    DiffRender.Ansi.Dim +
                    new string('─', Math.Max(1, width)) +
                    DiffRender.Ansi.Reset)
            ];
        }

        var heading = false;
        var quote = false;
        var bullet = false;
        var marker = string.Empty;
        var hashes = 0;
        while (hashes < body.Length && body[hashes] == '#')
        {
            hashes++;
        }
        if (hashes is >= 1 and <= 6
            && hashes < body.Length
            && body[hashes] == ' ')
        {
            heading = true;
            body = body[(hashes + 1)..].TrimStart();
        }
        else if (body.StartsWith("> ", StringComparison.Ordinal))
        {
            quote = true;
            marker = "│ ";
            body = body[2..];
        }
        else if (body.Length >= 2
            && body[0] is '-' or '*' or '+'
            && body[1] == ' ')
        {
            bullet = true;
            marker = label.Length > 0 ? string.Empty : "• ";
            body = body[2..];
        }

        var plain = label + indent + marker + body;
        var rendered = new List<TranscriptLine>();
        foreach (var line in WrapPlain(plain, width))
        {
            var text = heading || quote
                ? line
                : StyleInlineMarkdown(line);
            if (heading)
            {
                text = DiffRender.Ansi.Bold + text + DiffRender.Ansi.Reset;
            }
            else if (quote)
            {
                text = DiffRender.Ansi.Secondary + text + DiffRender.Ansi.Reset;
            }
            if (bullet)
            {
                var markerIndex = text.IndexOf('•');
                if (markerIndex >= 0)
                {
                    text = text[..markerIndex] +
                        DiffRender.Ansi.Accent +
                        "•" +
                        DiffRender.Ansi.Reset +
                        text[(markerIndex + 1)..];
                }
            }
            rendered.Add(new TranscriptLine(text));
        }
        return rendered;
    }

    private static string StyleInlineMarkdown(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length + 16);
        for (var index = 0; index < text.Length;)
        {
            if (text[index] == '`')
            {
                var end = text.IndexOf('`', index + 1);
                if (end > index + 1)
                {
                    builder.Append(DiffRender.Ansi.Cyan);
                    builder.Append(text, index + 1, end - index - 1);
                    builder.Append(DiffRender.Ansi.Reset);
                    index = end + 1;
                    continue;
                }
            }
            if ((text[index] == '*' || text[index] == '_')
                && index + 2 < text.Length
                && text[index + 1] == text[index])
            {
                var token = new string(text[index], 2);
                var end = text.IndexOf(token, index + 2, StringComparison.Ordinal);
                if (end > index + 2)
                {
                    builder.Append(DiffRender.Ansi.Bold);
                    builder.Append(text, index + 2, end - index - 2);
                    builder.Append(DiffRender.Ansi.Reset);
                    index = end + 2;
                    continue;
                }
            }
            builder.Append(text[index]);
            index++;
        }
        return builder.ToString();
    }
    private static IEnumerable<string> RenderUserMessage(string text, int width)
    {
        foreach (var line in WrapPlain("> " + text, width))
        {
            yield return DiffRender.Ansi.UserMessage +
                DisplayText.PadToWidth(line, width) +
                DiffRender.Ansi.Reset;
        }
    }
    private static List<TranscriptLine> RenderDiffLines(
        ChatTuiState state,
        int entryIndex,
        string text,
        int width,
        UiLanguage language)
    {
        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var rawLines = normalized.Split('\n');
        var key = $"diff:{entryIndex}";
        var expanded = state.IsTranscriptItemExpanded(key);
        var marker = expanded ? "▾" : "▸";
        var added = rawLines.Count(line =>
            line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal));
        var removed = rawLines.Count(line =>
            line.StartsWith('-') && !line.StartsWith("---", StringComparison.Ordinal));
        var count = rawLines.Length == 1
            ? ChatTexts.T(language, "1 line", "1 行")
            : ChatTexts.T(language, $"{rawLines.Length} lines", $"{rawLines.Length} 行");
        var heading = $"{ChatTexts.DiffTitle(language)} · +{added} -{removed} · {count}";
        var result = new List<TranscriptLine>
        {
            new(
                DiffRender.Ansi.Fit(
                    DiffRender.Ansi.BoldCyan +
                    marker +
                    DiffRender.Ansi.Reset +
                    DiffRender.Ansi.Secondary +
                    " " +
                    heading +
                    DiffRender.Ansi.Reset,
                    width),
                new TranscriptToggle(key, TranscriptToggleKind.Diff))
        };
        if (expanded)
        {
            foreach (var line in RenderDiff(text, width, language).Skip(1))
            {
                result.Add(new TranscriptLine(line));
            }
        }
        return result;
    }
    private static List<TranscriptLine> RenderToolLines(
        ChatTuiState state,
        int entryIndex,
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
        var color = success
            ? DiffRender.Ansi.Green
            : failure
                ? DiffRender.Ansi.Red
                : DiffRender.Ansi.Cyan;
        var key = $"tool:{entryIndex}";
        var expanded = state.IsTranscriptItemExpanded(key);
        var marker = expanded ? "▾" : "▸";
        var summary = lines.Count == 0
            ? string.Empty
            : DiffRender.RedactSensitive(lines[0].Trim());
        var label = ChatTexts.ToolLabel(language).Trim();
        var result = new List<TranscriptLine>
        {
            new(
                DiffRender.Ansi.Fit(
                    color + marker + " " + label + " " + summary + DiffRender.Ansi.Reset,
                    width),
                new TranscriptToggle(key, TranscriptToggleKind.Tool))
        };
        if (!expanded)
        {
            return result;
        }

        for (var index = 1; index < lines.Count; index++)
        {
            var detail = DiffRender.RedactSensitive(lines[index]);
            foreach (var wrapped in WrapPlain("  " + detail, width))
            {
                result.Add(new TranscriptLine(
                    DiffRender.Ansi.Secondary + wrapped + DiffRender.Ansi.Reset));
            }
        }
        return result;
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
        lines.AddRange(ColorLines(
            WrapPlain(ChatTexts.ApprovalTitle(language), width),
            DiffRender.Ansi.Yellow));
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
        var lines = new List<string>
        {
            DiffRender.Ansi.BoldCyan + ChatTexts.DiffTitle(language) + DiffRender.Ansi.Reset
        };
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

    private static void AddWrapped(List<string> lines, string text, int width)
    {
        foreach (var line in DisplayText.WrapToWidth(text, Math.Max(1, width)))
        {
            lines.Add(line);
        }
    }

    private static void AddWrappedStyled(
        List<string> lines,
        string text,
        int width,
        string style)
    {
        foreach (var line in DisplayText.WrapToWidth(text, Math.Max(1, width)))
        {
            lines.Add(style + line + DiffRender.Ansi.Reset);
        }
    }

    private static void AddDetailField(
        List<string> lines,
        string label,
        string value,
        int width,
        UiLanguage language)
    {
        var separator = language == UiLanguage.ChineseSimplified ? "：" : ": ";
        var prefix = label + separator;
        var prefixWidth = DisplayText.Width(prefix);
        var available = Math.Max(1, width - prefixWidth);
        var wrapped = DisplayText.WrapToWidth(value, available);
        lines.Add(
            DiffRender.Ansi.Dim + prefix + DiffRender.Ansi.Reset +
            (wrapped.Count == 0 ? "—" : wrapped[0]));
        for (var index = 1; index < wrapped.Count; index++)
        {
            lines.Add(new string(' ', prefixWidth) + wrapped[index]);
        }
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
