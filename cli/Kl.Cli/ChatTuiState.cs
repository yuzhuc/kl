using System.Globalization;
using System.Text.Json;

namespace Kl.Cli;

public enum ChatScreen
{
    Chat,
    History,
    Shortcuts,
    Settings
}

public enum TranscriptKind
{
    User,
    Assistant,
    Tool,
    Approval,
    Diff,
    Error,
    Info,
    Worked,
    Reasoning
}

public enum ChatKeyAction
{
    None,
    Insert,
    InsertNewline,
    Backspace,
    Delete,
    CursorLeft,
    CursorRight,
    CursorHome,
    CursorEnd,
    Send,
    CommandPaletteUp,
    CommandPaletteDown,
    CommandPaletteAccept,
    CommandPaletteClose,
    MentionPickerUp,
    MentionPickerDown,
    MentionPickerPreviousFilter,
    MentionPickerNextFilter,
    MentionPickerAccept,
    MentionPickerClose,
    CancelOrExit,
    OpenHistory,
    HistoryUp,
    HistoryDown,
    HistoryOpen,
    HistoryNew,
    HistoryDelete,
    HistoryNextGrouping,
    HistoryPageUp,
    HistoryPageDown,
    HistoryHome,
    HistoryEnd,
    HistoryRename,
    HistoryRenameAccept,
    HistoryRenameCancel,
    HistoryStop,
    HistoryNextFilter,
    HistoryPreviousFilter,
    HistorySearch,
    HistorySearchAccept,
    HistorySearchClose,
    HistoryBack,
    ToggleShortcuts,
    ScrollUp,
    ScrollDown,
    ApprovalAllow,
    ApprovalDeny,
    SelectUp,
    SelectDown,
    CopyLastAssistant,
    ClearScreen,
    Exit,
    ToggleReasoning,
    ModelPickerUp,
    ModelPickerDown,
    ModelPickerAccept,
    ModelPickerClose
}

public sealed record TranscriptEntry(TranscriptKind Kind, string Text, bool IsComplete = false);

public sealed record HistorySession(
    string SessionId,
    string Workspace,
    string Provider,
    string Model,
    string UpdatedAt,
    int MessageCount,
    string? Status,
    string? Task,
    string? Prompt,
    string? LastMessage,
    string? ReasoningEffort = null);

public sealed record HistoryMessage(
    string Role,
    string Text,
    string Timestamp,
    long? Seq,
    string? ToolName,
    bool? ToolOk,
    string? ToolSummary)
{
    public string ToolDisplay()
    {
        var label = string.Join(
            ": ",
            new[] { ToolName, ToolSummary }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        if (string.IsNullOrEmpty(label))
        {
            return Text;
        }
        var marker = ToolOk switch
        {
            true => "[ok] ",
            false => "[fail] ",
            _ => string.Empty
        };
        return marker + label;
    }
}

public sealed class ChatTuiState
{
    public const char FieldSeparator = '\u001f';

    public ChatTuiState(
        string workspace = "",
        string provider = "",
        string model = "",
        string sessionId = "",
        bool verbose = false)
    {
        Workspace = workspace;
        Provider = provider;
        Model = model;
        SessionId = sessionId;
        Verbose = verbose;
    }

    public ChatScreen Screen { get; set; } = ChatScreen.Chat;
    public ChatScreen ShortcutReturnScreen { get; set; } = ChatScreen.Chat;
    public string Workspace { get; init; }
    public string Provider { get; set; }
    public string Model { get; set; }
    public string ReasoningEffort { get; set; } = string.Empty;
    public int WarningCount { get; set; }
    public string SessionId { get; set; }
    public int ContextColorSeed { get; } = Random.Shared.Next();
    public bool Verbose { get; init; }
    public string Input { get; set; } = string.Empty;
    public int Cursor { get; set; }
    public List<TranscriptEntry> Transcript { get; } = [];
    public int TranscriptScroll { get; set; }
    public bool IsTurnActive { get; set; }
    public string? PendingApprovalId { get; set; }
    public string? StatusMessage { get; set; }
    public DateTimeOffset? TurnStartedAt { get; set; }
    public bool AssistantStarted { get; set; }
    public List<HistorySession> History { get; set; } = [];
    public int HistorySelection { get; set; }
    public HistoryFilter HistoryFilter { get; set; } = Kl.Cli.HistoryFilter.All;
    public HistoryGrouping HistoryGrouping { get; set; } = HistoryGrouping.Workspace;
    public string HistorySearch { get; set; } = string.Empty;
    public int HistorySearchCursor { get; set; }
    public bool HistorySearching { get; set; }
    public string? PendingHistoryDeleteSessionId { get; set; }
    public bool HistoryRenaming { get; set; }
    public string HistoryRenameSessionId { get; set; } = string.Empty;
    public string HistoryRenameInput { get; set; } = string.Empty;
    public int HistoryRenameCursor { get; set; }
    public int CommandPaletteSelection { get; set; }
    public int CommandPaletteScroll { get; set; }
    public bool CommandPaletteDismissed { get; set; }
    public bool ReasoningVisible { get; set; }
    public HashSet<string> ExpandedTranscriptItems { get; } = [];
    public ChatModelPickerState ModelPicker { get; } = new();
    public MentionPickerState MentionPicker { get; } = new();
    public SettingsTuiState Settings { get; } = new();

    public int? SelectionAnchor { get; private set; }
    public int? SelectionCursor { get; private set; }
    public MouseSelection MouseSelection { get; } = new();

    public bool HasPendingApproval => PendingApprovalId is not null;

    public bool HasSelection =>
        MouseSelection.HasSelection
        || (SelectionAnchor is not null && SelectionCursor is not null);

    public UiLanguage Language => Settings.Language;

    public void Insert(char character)
    {
        Input = Input.Insert(Math.Clamp(Cursor, 0, Input.Length), character.ToString());
        Cursor = Math.Clamp(Cursor + 1, 0, Input.Length);
        ResetCommandPalette();
        Kl.Cli.MentionPicker.Update(this);
    }

    public void Backspace()
    {
        if (Cursor <= 0)
        {
            return;
        }
        Input = Input.Remove(Cursor - 1, 1);
        Cursor--;
        ResetCommandPalette();
        Kl.Cli.MentionPicker.Update(this);
    }

    public void Delete()
    {
        if (Cursor >= Input.Length)
        {
            return;
        }
        Input = Input.Remove(Cursor, 1);
        ResetCommandPalette();
        Kl.Cli.MentionPicker.Update(this);
    }

    public void MoveCursorLeft()
    {
        Cursor = Math.Max(0, Cursor - 1);
        Kl.Cli.MentionPicker.Update(this);
    }

    public void MoveCursorRight()
    {
        Cursor = Math.Min(Input.Length, Cursor + 1);
        Kl.Cli.MentionPicker.Update(this);
    }

    public void MoveCursorHome()
    {
        Cursor = 0;
        Kl.Cli.MentionPicker.Update(this);
    }

    public void MoveCursorEnd()
    {
        Cursor = Input.Length;
        Kl.Cli.MentionPicker.Update(this);
    }

    public void DismissCommandPalette() => CommandPaletteDismissed = true;

    public void ClearInput()
    {
        Input = string.Empty;
        Cursor = 0;
        ResetCommandPalette();
        Kl.Cli.MentionPicker.Update(this);
    }

    public void CompleteReasoning()
    {
        for (var index = 0; index < Transcript.Count; index++)
        {
            if (Transcript[index].Kind == TranscriptKind.Reasoning
                && !Transcript[index].IsComplete)
            {
                Transcript[index] = Transcript[index] with { IsComplete = true };
            }
        }
    }
    public void ToggleReasoning() => ReasoningVisible = !ReasoningVisible;

    public bool IsTranscriptItemExpanded(string key) =>
        ExpandedTranscriptItems.Contains(key);

    public void ToggleTranscriptItem(string key)
    {
        if (!ExpandedTranscriptItems.Remove(key))
        {
            ExpandedTranscriptItems.Add(key);
        }
    }

    public HistoryView GetHistoryView() =>
        HistoryView.Build(
            History,
            HistoryFilter,
            HistorySearch,
            SessionId,
            HasPendingApproval,
            IsTurnActive,
            HistoryGrouping);

    public void ResetHistoryView()
    {
        HistoryFilter = Kl.Cli.HistoryFilter.All;
        HistorySearch = string.Empty;
        HistorySearchCursor = 0;
        HistorySearching = false;
        HistorySelection = 0;
        CancelHistoryRename();
    }

    public void SetHistoryFilter(HistoryFilter filter)
    {
        HistoryFilter = filter;
        HistorySelection = 0;
    }

    public void SetHistoryGrouping(HistoryGrouping grouping)
    {
        HistoryGrouping = grouping;
        HistorySelection = 0;
    }

    public void StartHistorySearch()
    {
        HistorySearching = true;
        HistorySearchCursor = HistorySearch.Length;
    }

    public void CloseHistorySearch(bool clear)
    {
        if (clear)
        {
            HistorySearch = string.Empty;
            HistorySearchCursor = 0;
            HistorySelection = 0;
        }
        HistorySearching = false;
    }

    public void StartHistoryRename(HistorySession session)
    {
        HistoryRenaming = true;
        HistoryRenameSessionId = session.SessionId;
        HistoryRenameInput = session.Task ?? string.Empty;
        HistoryRenameCursor = HistoryRenameInput.Length;
        HistorySearching = false;
    }

    public void CancelHistoryRename()
    {
        HistoryRenaming = false;
        HistoryRenameSessionId = string.Empty;
        HistoryRenameInput = string.Empty;
        HistoryRenameCursor = 0;
    }

    public void InsertHistoryRename(char character)
    {
        if (HistoryRenameInput.Length >= 160)
        {
            return;
        }
        HistoryRenameInput = HistoryRenameInput.Insert(
            Math.Clamp(HistoryRenameCursor, 0, HistoryRenameInput.Length),
            character.ToString());
        HistoryRenameCursor = Math.Clamp(
            HistoryRenameCursor + 1,
            0,
            HistoryRenameInput.Length);
    }

    public void BackspaceHistoryRename()
    {
        if (HistoryRenameCursor <= 0)
        {
            return;
        }
        HistoryRenameInput = HistoryRenameInput.Remove(HistoryRenameCursor - 1, 1);
        HistoryRenameCursor--;
    }

    public void DeleteHistoryRename()
    {
        if (HistoryRenameCursor >= HistoryRenameInput.Length)
        {
            return;
        }
        HistoryRenameInput = HistoryRenameInput.Remove(HistoryRenameCursor, 1);
    }

    public void MoveHistoryRenameLeft() =>
        HistoryRenameCursor = Math.Max(0, HistoryRenameCursor - 1);

    public void MoveHistoryRenameRight() =>
        HistoryRenameCursor = Math.Min(HistoryRenameInput.Length, HistoryRenameCursor + 1);

    public void MoveHistoryRenameHome() => HistoryRenameCursor = 0;

    public void MoveHistoryRenameEnd() => HistoryRenameCursor = HistoryRenameInput.Length;

    public void InsertHistorySearch(char character)
    {
        HistorySearch = HistorySearch.Insert(
            Math.Clamp(HistorySearchCursor, 0, HistorySearch.Length),
            character.ToString());
        HistorySearchCursor = Math.Clamp(
            HistorySearchCursor + 1,
            0,
            HistorySearch.Length);
        HistorySelection = 0;
    }

    public void BackspaceHistorySearch()
    {
        if (HistorySearchCursor <= 0)
        {
            return;
        }
        HistorySearch = HistorySearch.Remove(HistorySearchCursor - 1, 1);
        HistorySearchCursor--;
        HistorySelection = 0;
    }

    public void DeleteHistorySearch()
    {
        if (HistorySearchCursor >= HistorySearch.Length)
        {
            return;
        }
        HistorySearch = HistorySearch.Remove(HistorySearchCursor, 1);
        HistorySelection = 0;
    }

    public void MoveHistorySearchLeft() =>
        HistorySearchCursor = Math.Max(0, HistorySearchCursor - 1);

    public void MoveHistorySearchRight() =>
        HistorySearchCursor = Math.Min(HistorySearch.Length, HistorySearchCursor + 1);

    public void MoveHistorySearchHome() => HistorySearchCursor = 0;

    public void MoveHistorySearchEnd() => HistorySearchCursor = HistorySearch.Length;

    public void ClearSelection()
    {
        SelectionAnchor = null;
        SelectionCursor = null;
        MouseSelection.Clear();
    }

    public void BeginMouseSelection(int row, int column)
    {
        SelectionAnchor = null;
        SelectionCursor = null;
        MouseSelection.Begin(row, column);
    }

    public void ExtendMouseSelection(int row, int column) =>
        MouseSelection.Extend(row, column);

    public void EndMouseSelection(int row, int column) =>
        MouseSelection.End(row, column);

    public void ExtendSelection(int delta, int totalLines)
    {
        MouseSelection.Clear();
        if (totalLines <= 0)
        {
            ClearSelection();
            return;
        }

        if (SelectionAnchor is null || SelectionCursor is null)
        {
            SelectionAnchor = totalLines - 1;
            SelectionCursor = Math.Clamp(totalLines - 1 + delta, 0, totalLines - 1);
            return;
        }

        SelectionCursor = Math.Clamp(SelectionCursor.Value + delta, 0, totalLines - 1);
    }

    public string? SelectedText(int width)
    {
        var lines = ChatTuiRenderer.TranscriptLines(this, width);
        if (MouseSelection.HasSelection)
        {
            var selected = MouseSelection.Text(lines);
            return selected.Length == 0 ? null : selected;
        }

        if (SelectionAnchor is not { } anchor || SelectionCursor is not { } cursor)
        {
            return null;
        }
        if (lines.Count == 0)
        {
            return null;
        }

        var start = Math.Clamp(Math.Min(anchor, cursor), 0, lines.Count - 1);
        var end = Math.Clamp(Math.Max(anchor, cursor), 0, lines.Count - 1);
        return string.Join(
            "\n",
            lines.Skip(start)
                .Take(end - start + 1)
                .Select(line => DiffRender.Ansi.Strip(line).TrimEnd()));
    }

    public string? LastAssistantText() =>
        Transcript.LastOrDefault(entry => entry.Kind == TranscriptKind.Assistant)?.Text;

    private static bool ToolEntryMatches(string text, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }
        var first = text.Split('\n', 2)[0].TrimStart();
        return first.Equals(name, StringComparison.OrdinalIgnoreCase)
            || first.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase);
    }
    public void ClearTranscript()
    {
        Transcript.Clear();
        TranscriptScroll = 0;
        ExpandedTranscriptItems.Clear();
        PendingApprovalId = null;
        StatusMessage = null;
        IsTurnActive = false;
        TurnStartedAt = null;
        AssistantStarted = false;
        ClearSelection();
        CommandPaletteSelection = 0;
        CommandPaletteScroll = 0;
        CommandPaletteDismissed = false;
    }

    public void AddUserMessage(string text)
    {
        Transcript.Add(new TranscriptEntry(TranscriptKind.User, text));
        TranscriptScroll = 0;
        ClearInput();
        ClearSelection();
        IsTurnActive = true;
        TurnStartedAt = DateTimeOffset.Now;
        AssistantStarted = false;
        StatusMessage = null;
    }

    private void ResetCommandPalette()
    {
        CommandPaletteSelection = 0;
        CommandPaletteScroll = 0;
        CommandPaletteDismissed = false;
    }

    public void AddInfo(string text) =>
        Transcript.Add(new TranscriptEntry(TranscriptKind.Info, text));

    public void AddError(string text) =>
        Transcript.Add(new TranscriptEntry(TranscriptKind.Error, text));

    public void AddHistory(IEnumerable<HistoryMessage> messages)
    {
        foreach (var message in messages)
        {
            switch (message.Role)
            {
                case "user" when !string.IsNullOrWhiteSpace(message.Text):
                    Transcript.Add(new TranscriptEntry(TranscriptKind.User, message.Text));
                    break;
                case "assistant" when !string.IsNullOrWhiteSpace(message.Text):
                    Transcript.Add(new TranscriptEntry(TranscriptKind.Assistant, message.Text));
                    break;
                case "tool":
                    var tool = message.ToolDisplay();
                    if (!string.IsNullOrWhiteSpace(tool))
                    {
                        Transcript.Add(new TranscriptEntry(TranscriptKind.Tool, tool));
                    }
                    break;
            }
        }
        TranscriptScroll = 0;
    }

    public void Apply(SessionEvent sessionEvent)
    {
        switch (sessionEvent.Kind)
        {
            case SessionEventKind.TurnStarted:
                IsTurnActive = true;
                TurnStartedAt ??= DateTimeOffset.Now;
                AssistantStarted = false;
                StatusMessage = null;
                break;
            case SessionEventKind.AssistantDelta:
                if (string.IsNullOrEmpty(sessionEvent.Text))
                {
                    break;
                }
                if (!AssistantStarted
                    || (Transcript.Count > 0
                        && Transcript[^1].Kind == TranscriptKind.Reasoning))
                {
                    CompleteReasoning();
                }
                if (AssistantStarted
                    && Transcript.Count > 0
                    && Transcript[^1].Kind == TranscriptKind.Assistant)
                {
                    Transcript[^1] = Transcript[^1] with
                    {
                        Text = Transcript[^1].Text + sessionEvent.Text
                    };
                }
                else
                {
                    Transcript.Add(new TranscriptEntry(
                        TranscriptKind.Assistant,
                        sessionEvent.Text));
                }
                AssistantStarted = true;
                break;
            case SessionEventKind.ReasoningDelta:
                if (string.IsNullOrEmpty(sessionEvent.Text))
                {
                    break;
                }
                if (Transcript.Count > 0
                    && Transcript[^1].Kind == TranscriptKind.Reasoning)
                {
                    Transcript[^1] = Transcript[^1] with
                    {
                        Text = Transcript[^1].Text + sessionEvent.Text,
                        IsComplete = false
                    };
                }
                else
                {
                    Transcript.Add(new TranscriptEntry(
                        TranscriptKind.Reasoning,
                        sessionEvent.Text));
                }
                break;
            case SessionEventKind.ToolStarted:
                CompleteReasoning();
                Transcript.Add(new TranscriptEntry(
                    TranscriptKind.Tool,
                    $"{sessionEvent.Name} {sessionEvent.Args}".Trim()));
                break;
            case SessionEventKind.ToolOutput:
                if (string.IsNullOrEmpty(sessionEvent.Text))
                {
                    break;
                }
                if (Transcript.Count > 0 && Transcript[^1].Kind == TranscriptKind.Tool)
                {
                    var previous = Transcript[^1].Text;
                    var separator = previous.EndsWith('\n')
                        || sessionEvent.Text.StartsWith('\n')
                        ? string.Empty
                        : "\n";
                    Transcript[^1] = Transcript[^1] with
                    {
                        Text = previous + separator + sessionEvent.Text
                    };
                }
                else
                {
                    Transcript.Add(new TranscriptEntry(
                        TranscriptKind.Tool,
                        sessionEvent.Text));
                }
                break;
            case SessionEventKind.ApprovalRequested:
                CompleteReasoning();
                PendingApprovalId = sessionEvent.ApprovalId;
                Transcript.Add(new TranscriptEntry(
                    TranscriptKind.Approval,
                    string.Join(
                        FieldSeparator,
                        sessionEvent.Name ?? string.Empty,
                        sessionEvent.Action ?? string.Empty,
                        sessionEvent.Resource ?? string.Empty,
                        sessionEvent.Reason ?? string.Empty)));
                break;
            case SessionEventKind.ApprovalResolved:
                PendingApprovalId = null;
                Transcript.Add(new TranscriptEntry(
                    TranscriptKind.Info,
                    $"[approval] {sessionEvent.Decision}"));
                break;
            case SessionEventKind.ToolCompleted:
                var completion =
                    $"{(sessionEvent.Ok ? "[ok]" : "[fail]")} " +
                    $"{sessionEvent.Name}: {sessionEvent.Summary}";
                if (Transcript.Count > 0
                    && Transcript[^1].Kind == TranscriptKind.Tool
                    && ToolEntryMatches(Transcript[^1].Text, sessionEvent.Name))
                {
                    Transcript[^1] = Transcript[^1] with
                    {
                        Text = completion + "\n" + Transcript[^1].Text
                    };
                }
                else
                {
                    Transcript.Add(new TranscriptEntry(TranscriptKind.Tool, completion));
                }
                if (!string.IsNullOrWhiteSpace(sessionEvent.Diff))
                {
                    Transcript.Add(new TranscriptEntry(
                        TranscriptKind.Diff,
                        sessionEvent.Diff));
                }
                break;
            case SessionEventKind.AuditRecorded when Verbose:
                Transcript.Add(new TranscriptEntry(
                    TranscriptKind.Info,
                    $"[audit] {sessionEvent.Text}"));
                break;
            case SessionEventKind.TurnCompleted:
                FinishTurn(sessionEvent.Status, sessionEvent.Text, failed: false);
                break;
            case SessionEventKind.TurnFailed:
                FinishTurn("failed", sessionEvent.Text, failed: true);
                break;
            case SessionEventKind.Error:
                Transcript.Add(new TranscriptEntry(
                    TranscriptKind.Error,
                    sessionEvent.Text));
                break;
        }
    }

    private void FinishTurn(string? status, string? text, bool failed)
    {
        CompleteReasoning();
        if (failed)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                Transcript.Add(new TranscriptEntry(TranscriptKind.Error, text));
            }
        }
        else if (status == "cancelled")
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                Transcript.Add(new TranscriptEntry(TranscriptKind.Info, text));
            }
        }
        else if (!AssistantStarted && !string.IsNullOrWhiteSpace(text))
        {
            Transcript.Add(new TranscriptEntry(TranscriptKind.Assistant, text));
        }

        if (TurnStartedAt is { } started)
        {
            var completed = DateTimeOffset.Now;
            var seconds = Math.Max(
                0.1,
                Math.Round((completed - started).TotalSeconds, 1));
            Transcript.Add(new TranscriptEntry(
                TranscriptKind.Worked,
                seconds.ToString("0.0", CultureInfo.InvariantCulture)
                    + FieldSeparator
                    + completed.ToString("HH:mm", CultureInfo.InvariantCulture)));
        }

        IsTurnActive = false;
        PendingApprovalId = null;
        TurnStartedAt = null;
        AssistantStarted = false;
    }

    public void ScrollTranscript(int delta, int width, int height)
    {
        ClearSelection();
        var maximum = ChatTuiRenderer.MaxTranscriptScroll(this, width, height);
        TranscriptScroll = Math.Clamp(TranscriptScroll + delta, 0, maximum);
    }

    public static List<HistoryMessage> ParseHistory(JsonElement result)
    {
        var messages = new List<HistoryMessage>();
        if (!result.TryGetProperty("messages", out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return messages;
        }

        foreach (var value in values.EnumerateArray())
        {
            string? toolName = null;
            bool? toolOk = null;
            string? toolSummary = null;
            if (value.TryGetProperty("tool", out var tool)
                && tool.ValueKind == JsonValueKind.Object)
            {
                toolName = GetOptionalString(tool, "name");
                toolOk = tool.TryGetProperty("ok", out var ok)
                    && ok.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? ok.GetBoolean()
                    : null;
                toolSummary = GetOptionalString(tool, "summary");
            }

            messages.Add(new HistoryMessage(
                GetString(value, "role"),
                GetString(value, "text"),
                GetString(value, "timestamp"),
                value.TryGetProperty("seq", out var seq)
                    && seq.TryGetInt64(out var parsedSeq)
                    ? parsedSeq
                    : null,
                toolName,
                toolOk,
                toolSummary));
        }
        return messages;
    }

    public static List<HistorySession> ParseSessions(JsonElement result)
    {
        var sessions = new List<HistorySession>();
        if (!result.TryGetProperty("sessions", out var values)
            || values.ValueKind != JsonValueKind.Array)
        {
            return sessions;
        }

        foreach (var value in values.EnumerateArray())
        {
            sessions.Add(new HistorySession(
                GetString(value, "session_id"),
                GetString(value, "workspace"),
                GetString(value, "provider"),
                GetString(value, "model"),
                GetString(value, "updated_at"),
                value.TryGetProperty("message_count", out var count)
                    && count.TryGetInt32(out var messageCount)
                    ? messageCount
                    : 0,
                GetOptionalString(value, "status"),
                GetOptionalString(value, "task"),
                GetOptionalString(value, "prompt"),
                GetOptionalString(value, "last_message"),
                GetOptionalString(value, "reasoning_effort")));
        }
        return sessions;
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string? GetOptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public static class ChatCommand
{
    // Legacy plain-text chat path. The TUI command palette only accepts
    // /settings; this compatibility check keeps the non-TUI fallback working.
    public static bool IsSettings(string input)
    {
        var command = input.Trim();
        return command.Equals("/settings", StringComparison.OrdinalIgnoreCase)
            || command.Equals("/设置", StringComparison.Ordinal);
    }
}

public static class ChatKeyMapper
{
    public static ChatKeyAction Map(ConsoleKeyInfo key, ChatTuiState state)
    {
        var control = (key.Modifiers & ConsoleModifiers.Control) != 0;
        var shift = (key.Modifiers & ConsoleModifiers.Shift) != 0;

        if (state.ModelPicker.IsOpen)
        {
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    return ChatKeyAction.ModelPickerUp;
                case ConsoleKey.DownArrow:
                    return ChatKeyAction.ModelPickerDown;
                case ConsoleKey.Enter:
                    return ChatKeyAction.ModelPickerAccept;
                case ConsoleKey.Escape:
                    return ChatKeyAction.ModelPickerClose;
            }
            if (control && key.Key is ConsoleKey.P)
            {
                return ChatKeyAction.ModelPickerUp;
            }
            if (control && key.Key is ConsoleKey.N)
            {
                return ChatKeyAction.ModelPickerDown;
            }
        }

        if (control && shift && key.Key == ConsoleKey.C)
        {
            return ChatKeyAction.CopyLastAssistant;
        }

        if (key.KeyChar == '\u0003' || (key.Key == ConsoleKey.C && control))
        {
            return ChatKeyAction.CancelOrExit;
        }

        if (control && key.Key == ConsoleKey.L)
        {
            return ChatKeyAction.ClearScreen;
        }

        if (control && key.Key == ConsoleKey.D)
        {
            return ChatKeyAction.Exit;
        }

        if (control && key.Key == ConsoleKey.R)
        {
            return ChatKeyAction.ToggleReasoning;
        }

        if (state.Screen == ChatScreen.History)
        {
            if (state.HistoryRenaming)
            {
                return key.Key switch
                {
                    ConsoleKey.Enter => ChatKeyAction.HistoryRenameAccept,
                    ConsoleKey.Escape => ChatKeyAction.HistoryRenameCancel,
                    ConsoleKey.Backspace => ChatKeyAction.Backspace,
                    ConsoleKey.Delete => ChatKeyAction.Delete,
                    ConsoleKey.LeftArrow => ChatKeyAction.CursorLeft,
                    ConsoleKey.RightArrow => ChatKeyAction.CursorRight,
                    ConsoleKey.Home => ChatKeyAction.CursorHome,
                    ConsoleKey.End => ChatKeyAction.CursorEnd,
                    _ when key.KeyChar >= ' '
                        && key.KeyChar != '\u007f'
                        && !control
                        && (key.Modifiers & ConsoleModifiers.Alt) == 0 =>
                        ChatKeyAction.Insert,
                    _ => ChatKeyAction.None
                };
            }

            if (state.HistorySearching)
            {
                return key.Key switch
                {
                    ConsoleKey.UpArrow => ChatKeyAction.HistoryUp,
                    ConsoleKey.DownArrow => ChatKeyAction.HistoryDown,
                    ConsoleKey.Tab when shift => ChatKeyAction.HistoryPreviousFilter,
                    ConsoleKey.Tab => ChatKeyAction.HistoryNextFilter,
                    ConsoleKey.Enter => ChatKeyAction.HistorySearchAccept,
                    ConsoleKey.Escape => ChatKeyAction.HistorySearchClose,
                    ConsoleKey.Backspace => ChatKeyAction.Backspace,
                    ConsoleKey.Delete => ChatKeyAction.Delete,
                    ConsoleKey.LeftArrow => ChatKeyAction.CursorLeft,
                    ConsoleKey.RightArrow => ChatKeyAction.CursorRight,
                    ConsoleKey.Home => ChatKeyAction.CursorHome,
                    ConsoleKey.End => ChatKeyAction.CursorEnd,
                    _ when key.KeyChar >= ' '
                        && key.KeyChar != '\u007f'
                        && !control
                        && (key.Modifiers & ConsoleModifiers.Alt) == 0 =>
                        ChatKeyAction.Insert,
                    _ => ChatKeyAction.None
                };
            }

            return key.Key switch
            {
                ConsoleKey.UpArrow => ChatKeyAction.HistoryUp,
                ConsoleKey.DownArrow => ChatKeyAction.HistoryDown,
                ConsoleKey.Tab when shift => ChatKeyAction.HistoryPreviousFilter,
                ConsoleKey.Tab => ChatKeyAction.HistoryNextFilter,
                ConsoleKey.Enter => ChatKeyAction.HistoryOpen,
                ConsoleKey.Escape => ChatKeyAction.HistoryBack,
                ConsoleKey.PageUp => ChatKeyAction.HistoryPageUp,
                ConsoleKey.PageDown => ChatKeyAction.HistoryPageDown,
                ConsoleKey.Home => ChatKeyAction.HistoryHome,
                ConsoleKey.End => ChatKeyAction.HistoryEnd,
                ConsoleKey.Delete => ChatKeyAction.HistoryDelete,
                ConsoleKey.Backspace => ChatKeyAction.HistoryDelete,
                ConsoleKey.F3 => ChatKeyAction.HistorySearch,
                _ when key.KeyChar is 'g' or 'G' => ChatKeyAction.HistoryNextGrouping,
                _ when key.KeyChar is 'j' or 'J' => ChatKeyAction.HistoryDown,
                _ when key.KeyChar is 'k' or 'K' => ChatKeyAction.HistoryUp,
                _ when key.KeyChar is 'o' or 'O' => ChatKeyAction.HistoryOpen,
                _ when key.KeyChar is 'r' or 'R' => ChatKeyAction.HistoryRename,
                _ when key.KeyChar is 'x' or 'X' => ChatKeyAction.HistoryStop,
                _ when key.KeyChar == '/' => ChatKeyAction.HistorySearch,
                _ when key.KeyChar is 'n' or 'N' => ChatKeyAction.HistoryNew,
                _ when key.KeyChar is '?' or '？' => ChatKeyAction.ToggleShortcuts,
                _ => ChatKeyAction.None
            };
        }

        if (state.PendingApprovalId is not null)
        {
            return key.KeyChar switch
            {
                'a' or 'A' or 'y' or 'Y' => ChatKeyAction.ApprovalAllow,
                'd' or 'D' or 'n' or 'N' => ChatKeyAction.ApprovalDeny,
                _ => ChatKeyAction.None
            };
        }

        if (MentionPicker.IsOpen(state))
        {
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    return ChatKeyAction.MentionPickerUp;
                case ConsoleKey.DownArrow:
                    return ChatKeyAction.MentionPickerDown;
                case ConsoleKey.LeftArrow:
                    return ChatKeyAction.MentionPickerPreviousFilter;
                case ConsoleKey.RightArrow:
                    return ChatKeyAction.MentionPickerNextFilter;
                case ConsoleKey.Enter:
                case ConsoleKey.Tab:
                    return ChatKeyAction.MentionPickerAccept;
                case ConsoleKey.Escape:
                    return ChatKeyAction.MentionPickerClose;
            }
        }

        if (ChatCommandPalette.IsOpen(state))
        {
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    return ChatKeyAction.CommandPaletteUp;
                case ConsoleKey.DownArrow:
                    return ChatKeyAction.CommandPaletteDown;
                case ConsoleKey.Enter:
                    return ChatKeyAction.CommandPaletteAccept;
                case ConsoleKey.Escape:
                    return ChatKeyAction.CommandPaletteClose;
            }
            if (control && key.Key is ConsoleKey.P)
            {
                return ChatKeyAction.CommandPaletteUp;
            }
            if (control && key.Key is ConsoleKey.N)
            {
                return ChatKeyAction.CommandPaletteDown;
            }
        }

        if (state.Screen == ChatScreen.Shortcuts)
        {
            return key.Key == ConsoleKey.Escape || key.KeyChar == '?'
                ? ChatKeyAction.ToggleShortcuts
                : ChatKeyAction.None;
        }

        if ((shift && key.Key == ConsoleKey.Enter)
            || (control && key.Key == ConsoleKey.J))
        {
            return ChatKeyAction.InsertNewline;
        }

        if (shift && key.Key == ConsoleKey.UpArrow)
        {
            return ChatKeyAction.SelectUp;
        }
        if (shift && key.Key == ConsoleKey.DownArrow)
        {
            return ChatKeyAction.SelectDown;
        }

        return key.Key switch
        {
            ConsoleKey.Enter when !state.IsTurnActive
                && !string.IsNullOrWhiteSpace(state.Input) => ChatKeyAction.Send,
            ConsoleKey.LeftArrow => state.Input.Length == 0
                ? ChatKeyAction.OpenHistory
                : ChatKeyAction.CursorLeft,
            ConsoleKey.RightArrow => ChatKeyAction.CursorRight,
            ConsoleKey.UpArrow => ChatKeyAction.ScrollUp,
            ConsoleKey.DownArrow => ChatKeyAction.ScrollDown,
            ConsoleKey.Backspace => ChatKeyAction.Backspace,
            ConsoleKey.Delete => ChatKeyAction.Delete,
            ConsoleKey.Home => ChatKeyAction.CursorHome,
            ConsoleKey.End => ChatKeyAction.CursorEnd,
            _ when key.KeyChar == '?' && state.Input.Length == 0 =>
                ChatKeyAction.ToggleShortcuts,
            _ when key.KeyChar >= ' '
                && key.KeyChar != '\u007f'
                && !control
                && (key.Modifiers & ConsoleModifiers.Alt) == 0 =>
                ChatKeyAction.Insert,
            _ => ChatKeyAction.None
        };
    }
}
