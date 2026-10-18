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
    Worked
}

public enum ChatKeyAction
{
    None,
    Insert,
    Backspace,
    Delete,
    CursorLeft,
    CursorRight,
    CursorHome,
    CursorEnd,
    Send,
    CancelOrExit,
    OpenHistory,
    HistoryUp,
    HistoryDown,
    HistoryOpen,
    HistoryNew,
    HistoryBack,
    ToggleShortcuts,
    ScrollUp,
    ScrollDown,
    ApprovalAllow,
    ApprovalDeny
}

public sealed record TranscriptEntry(TranscriptKind Kind, string Text);

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
    string? LastMessage);

public sealed class ChatTuiState
{
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
    public string SessionId { get; init; }
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
    public SettingsTuiState Settings { get; } = new();

    public bool HasPendingApproval => PendingApprovalId is not null;

    public void Insert(char character)
    {
        Input = Input.Insert(Math.Clamp(Cursor, 0, Input.Length), character.ToString());
        Cursor = Math.Clamp(Cursor + 1, 0, Input.Length);
    }

    public void Backspace()
    {
        if (Cursor <= 0)
        {
            return;
        }
        Input = Input.Remove(Cursor - 1, 1);
        Cursor--;
    }

    public void Delete()
    {
        if (Cursor >= Input.Length)
        {
            return;
        }
        Input = Input.Remove(Cursor, 1);
    }

    public void MoveCursorLeft() => Cursor = Math.Max(0, Cursor - 1);

    public void MoveCursorRight() => Cursor = Math.Min(Input.Length, Cursor + 1);

    public void MoveCursorHome() => Cursor = 0;

    public void MoveCursorEnd() => Cursor = Input.Length;

    public void AddUserMessage(string text)
    {
        Transcript.Add(new TranscriptEntry(TranscriptKind.User, text));
        TranscriptScroll = 0;
        Input = string.Empty;
        Cursor = 0;
        IsTurnActive = true;
        TurnStartedAt = DateTimeOffset.Now;
        AssistantStarted = false;
        StatusMessage = null;
    }

    public void AddInfo(string text) =>
        Transcript.Add(new TranscriptEntry(TranscriptKind.Info, text));

    public void AddError(string text) =>
        Transcript.Add(new TranscriptEntry(TranscriptKind.Error, text));

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
                AssistantStarted = true;
                if (Transcript.Count > 0
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
                break;
            case SessionEventKind.ReasoningDelta:
                if (string.IsNullOrEmpty(sessionEvent.Text))
                {
                    break;
                }
                if (Transcript.Count > 0
                    && Transcript[^1].Kind == TranscriptKind.Info
                    && Transcript[^1].Text.StartsWith("[reasoning] ", StringComparison.Ordinal))
                {
                    Transcript[^1] = Transcript[^1] with
                    {
                        Text = Transcript[^1].Text + sessionEvent.Text
                    };
                }
                else
                {
                    Transcript.Add(new TranscriptEntry(
                        TranscriptKind.Info,
                        "[reasoning] " + sessionEvent.Text));
                }
                break;
            case SessionEventKind.ToolStarted:
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
                PendingApprovalId = sessionEvent.ApprovalId;
                var approval = $"{sessionEvent.Name} ({sessionEvent.Action}) {sessionEvent.Resource}";
                if (!string.IsNullOrWhiteSpace(sessionEvent.Reason))
                {
                    approval += $" — {sessionEvent.Reason}";
                }
                Transcript.Add(new TranscriptEntry(TranscriptKind.Approval, approval));
                break;
            case SessionEventKind.ApprovalResolved:
                PendingApprovalId = null;
                Transcript.Add(new TranscriptEntry(
                    TranscriptKind.Info,
                    $"[approval] {sessionEvent.Decision}"));
                break;
            case SessionEventKind.ToolCompleted:
                Transcript.Add(new TranscriptEntry(
                    TranscriptKind.Tool,
                    $"{(sessionEvent.Ok ? "[ok]" : "[fail]")} " +
                    $"{sessionEvent.Name}: {sessionEvent.Summary}"));
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
            var seconds = Math.Max(0.1, Math.Round(
                (completed - started).TotalSeconds,
                1));
            Transcript.Add(new TranscriptEntry(
                TranscriptKind.Worked,
                $"Worked for {seconds:0.0}s • {completed:HH:mm}"));
        }

        IsTurnActive = false;
        PendingApprovalId = null;
        TurnStartedAt = null;
        AssistantStarted = false;
    }

    public void ScrollTranscript(int delta, int width, int height)
    {
        var maximum = ChatTuiRenderer.MaxTranscriptScroll(this, width, height);
        TranscriptScroll = Math.Clamp(TranscriptScroll + delta, 0, maximum);
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
                GetOptionalString(value, "last_message")));
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
        if (key.KeyChar == '' || (key.Key == ConsoleKey.C && control))
        {
            return ChatKeyAction.CancelOrExit;
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

        if (state.Screen == ChatScreen.History)
        {
            return key.Key switch
            {
                ConsoleKey.UpArrow => ChatKeyAction.HistoryUp,
                ConsoleKey.DownArrow => ChatKeyAction.HistoryDown,
                ConsoleKey.Enter => ChatKeyAction.HistoryOpen,
                ConsoleKey.Escape => ChatKeyAction.HistoryBack,
                _ when key.KeyChar is 'n' or 'N' => ChatKeyAction.HistoryNew,
                _ when key.KeyChar == '?' => ChatKeyAction.ToggleShortcuts,
                _ => ChatKeyAction.None
            };
        }

        if (state.Screen == ChatScreen.Shortcuts)
        {
            return key.Key == ConsoleKey.Escape || key.KeyChar == '?'
                ? ChatKeyAction.ToggleShortcuts
                : ChatKeyAction.None;
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
