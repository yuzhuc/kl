using System.Text.Json;
using Xunit;

namespace Kl.Cli.Tests;

public sealed class ChatTuiTests
{
    [Fact]
    public void RenderShowsHeaderInputAndFooter()
    {
        var state = new ChatTuiState("D:\\work", "openai", "gpt-5", "session-1");
        var render = ChatTuiRenderer.Render(100, 24, state);
        var lines = render.Split('\n');

        Assert.Equal(24, lines.Length);
        Assert.Equal(">_ KL (v0.1.0)", lines[0].TrimEnd());
        Assert.Contains("> Ask KL to do anything", render);
        Assert.Contains("openai/gpt-5", render);
        Assert.Contains("reasoning: default", render);
        Assert.Contains("← history", render);
        Assert.Contains("? shortcuts", render);
    }

    [Fact]
    public void RenderShowsTranscriptEntryKinds()
    {
        var state = new ChatTuiState();
        state.Transcript.Add(new TranscriptEntry(TranscriptKind.User, "fix it"));
        state.Transcript.Add(new TranscriptEntry(TranscriptKind.Assistant, "done"));
        state.Transcript.Add(new TranscriptEntry(TranscriptKind.Tool, "shell cargo test"));
        state.Transcript.Add(new TranscriptEntry(TranscriptKind.Approval, "edit src/lib.rs"));
        state.Transcript.Add(new TranscriptEntry(TranscriptKind.Diff, "-old\n+new"));
        state.Transcript.Add(new TranscriptEntry(TranscriptKind.Error, "boom"));

        var render = ChatTuiRenderer.Render(100, 24, state);

        Assert.Contains("You: fix it", render);
        Assert.Contains("KL: done", render);
        Assert.Contains("[tool] shell cargo test", render);
        Assert.Contains("[approval] edit src/lib.rs", render);
        Assert.Contains("+new", render);
        Assert.Contains("[error] boom", render);
    }

    [Fact]
    public void RenderShowsWorkedDurationAfterTurn()
    {
        var state = new ChatTuiState();
        state.Apply(new SessionEvent(SessionEventKind.TurnStarted));
        state.TurnStartedAt = DateTimeOffset.Now.AddSeconds(-1.2);
        state.Apply(new SessionEvent(
            SessionEventKind.TurnCompleted,
            Status: "completed",
            Text: "done"));

        var render = ChatTuiRenderer.Render(100, 24, state);

        Assert.Contains("KL: done", render);
        Assert.Contains("Worked for 1.2s •", render);
    }

    [Fact]
    public void TranscriptScrollShowsOlderEntries()
    {
        var state = new ChatTuiState();
        for (var index = 0; index < 40; index++)
        {
            state.Transcript.Add(new TranscriptEntry(
                TranscriptKind.Info,
                $"line-{index:00}"));
        }
        state.TranscriptScroll = 100;

        var render = ChatTuiRenderer.Render(80, 12, state);

        Assert.Contains("line-00", render);
        Assert.DoesNotContain("line-39", render);
    }

    [Fact]
    public void HistoryShowsSelectionAndSelectedDetails()
    {
        var state = new ChatTuiState("D:\\b", "p2", "m2", "s2")
        {
            Screen = ChatScreen.History,
            HistorySelection = 1,
            History =
            [
                new HistorySession(
                    "s1",
                    "D:\\a",
                    "p1",
                    "m1",
                    "2026-10-17T10:00:00Z",
                    2,
                    "completed",
                    "task one",
                    "prompt one",
                    "last one"),
                new HistorySession(
                    "s2",
                    "D:\\b",
                    "p2",
                    "m2",
                    "2026-10-17T11:00:00Z",
                    4,
                    "ready",
                    "task two",
                    "prompt two",
                    "last two")
            ]
        };

        var render = ChatTuiRenderer.Render(100, 24, state);

        Assert.Contains("Workspace: D:\\a", render);
        Assert.Contains("> task two", render);
        Assert.Contains("p2/m2", render);
        Assert.Contains("messages: 4", render);
        Assert.Contains("last: last two", render);
        Assert.Contains("prompt: prompt two", render);
        Assert.Contains("? shortcuts", render.Split('\n')[^1]);
    }

    [Fact]
    public void EmptyInputLeftOpensHistory()
    {
        var state = new ChatTuiState();
        var action = ChatKeyMapper.Map(
            Key(ConsoleKey.LeftArrow),
            state);

        Assert.Equal(ChatKeyAction.OpenHistory, action);
    }

    [Fact]
    public void NonEmptyInputLeftMovesCursor()
    {
        var state = new ChatTuiState { Input = "abc", Cursor = 3 };
        var action = ChatKeyMapper.Map(
            Key(ConsoleKey.LeftArrow),
            state);

        Assert.Equal(ChatKeyAction.CursorLeft, action);
    }

    [Fact]
    public void HistoryKeysMapToSelectionActions()
    {
        var state = new ChatTuiState { Screen = ChatScreen.History };

        Assert.Equal(ChatKeyAction.HistoryUp, ChatKeyMapper.Map(Key(ConsoleKey.UpArrow), state));
        Assert.Equal(ChatKeyAction.HistoryDown, ChatKeyMapper.Map(Key(ConsoleKey.DownArrow), state));
        Assert.Equal(ChatKeyAction.HistoryOpen, ChatKeyMapper.Map(Key(ConsoleKey.Enter), state));
        Assert.Equal(ChatKeyAction.HistoryNew, ChatKeyMapper.Map(Key(ConsoleKey.N, 'n'), state));
        Assert.Equal(ChatKeyAction.HistoryBack, ChatKeyMapper.Map(Key(ConsoleKey.Escape), state));
    }

    [Fact]
    public void ControlCIsCancelOrExitEvenWithoutModifierFlags()
    {
        var state = new ChatTuiState();
        var key = new ConsoleKeyInfo('', ConsoleKey.C, shift: false, alt: false, control: false);
        Assert.Equal(ChatKeyAction.CancelOrExit, ChatKeyMapper.Map(key, state));
    }

    [Fact]
    public void ApprovalKeysMapToAllowAndDeny()
    {
        var state = new ChatTuiState { PendingApprovalId = "approval-1" };

        Assert.Equal(
            ChatKeyAction.ApprovalAllow,
            ChatKeyMapper.Map(Key(ConsoleKey.A, 'a'), state));
        Assert.Equal(
            ChatKeyAction.ApprovalDeny,
            ChatKeyMapper.Map(Key(ConsoleKey.D, 'd'), state));
    }

    [Fact]
    public void QuestionMarkOnlyOpensShortcutsWithEmptyInput()
    {
        var state = new ChatTuiState();
        Assert.Equal(
            ChatKeyAction.ToggleShortcuts,
            ChatKeyMapper.Map(Key(ConsoleKey.Oem2, '?'), state));

        state.Input = "why";
        state.Cursor = 3;
        Assert.Equal(
            ChatKeyAction.Insert,
            ChatKeyMapper.Map(Key(ConsoleKey.Oem2, '?'), state));
    }

    [Fact]
    public void ReasoningDeltaAppearsInTranscript()
    {
        var state = new ChatTuiState();
        state.Apply(new SessionEvent(
            SessionEventKind.ReasoningDelta,
            Text: "thinking"));

        Assert.Contains(state.Transcript, entry => entry.Text.Contains("thinking"));
    }
    [Fact]
    public void ToolOutputStartsOnANewLine()
    {
        var state = new ChatTuiState();
        state.Apply(new SessionEvent(
            SessionEventKind.ToolStarted,
            Name: "read_file",
            Args: "{\"path\":\"a.txt\"}"));
        state.Apply(new SessionEvent(
            SessionEventKind.ToolOutput,
            Text: "contents"));

        Assert.Equal("read_file {\"path\":\"a.txt\"}\ncontents", state.Transcript[^1].Text);
    }
    [Fact]
    public void ParsesMissingHistoryFieldsAsEmpty()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "sessions": [
                {
                  "session_id": "s1",
                  "workspace": "D:\\work",
                  "provider": "openai",
                  "model": "gpt",
                  "created_at": "2026-10-17T10:00:00Z",
                  "updated_at": "2026-10-17T11:00:00Z",
                  "message_count": 2
                }
              ]
            }
            """);

        var sessions = ChatTuiState.ParseSessions(document.RootElement);

        var session = Assert.Single(sessions);
        Assert.Null(session.Status);
        Assert.Null(session.Task);
        Assert.Null(session.Prompt);
        Assert.Null(session.LastMessage);
    }

    [Fact]
    public void ParsesHistoryAndReplaysVisibleTranscript()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "messages": [
                {"role": "user", "text": "fix it", "timestamp": "2026-10-17T10:00:00Z", "seq": 2},
                {
                  "role": "tool",
                  "text": "",
                  "timestamp": "2026-10-17T10:00:01Z",
                  "seq": 4,
                  "tool": {"name": "shell", "ok": true, "summary": "exit 0 in 5 ms"}
                },
                {"role": "assistant", "text": "done", "timestamp": "2026-10-17T10:00:02Z", "seq": 5}
              ]
            }
            """);

        var messages = ChatTuiState.ParseHistory(document.RootElement);
        var state = new ChatTuiState();
        state.AddHistory(messages);

        Assert.Equal(3, messages.Count);
        Assert.Equal(2, messages[0].Seq);
        Assert.Equal(
            new[] { TranscriptKind.User, TranscriptKind.Tool, TranscriptKind.Assistant },
            state.Transcript.Select(entry => entry.Kind));
        Assert.Equal("fix it", state.Transcript[0].Text);
        Assert.Equal("[ok] shell: exit 0 in 5 ms", state.Transcript[1].Text);
        Assert.Equal("done", state.Transcript[2].Text);
    }

    [Fact]
    public void MissingHistoryFieldsStayEmpty()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "messages": [
                {"role": "assistant", "text": "hi"}
              ]
            }
            """);

        var message = Assert.Single(ChatTuiState.ParseHistory(document.RootElement));
        Assert.Equal(string.Empty, message.Timestamp);
        Assert.Null(message.Seq);
        Assert.Null(message.ToolName);
        Assert.Null(message.ToolOk);
        Assert.Null(message.ToolSummary);
    }

    [Fact]
    public void LiveAssistantDeltaDoesNotMergeIntoReplayedMessage()
    {
        var state = new ChatTuiState();
        state.AddHistory(
        [
            new HistoryMessage("assistant", "old answer", string.Empty, 1, null, null, null)
        ]);

        state.Apply(new SessionEvent(SessionEventKind.TurnStarted));
        state.Apply(new SessionEvent(SessionEventKind.AssistantDelta, Text: "new answer"));

        Assert.Equal(2, state.Transcript.Count);
        Assert.Equal("old answer", state.Transcript[0].Text);
        Assert.Equal("new answer", state.Transcript[1].Text);
    }

    private static ConsoleKeyInfo Key(
        ConsoleKey key,
        char character = '\0',
        bool control = false) =>
        new(character, key, shift: false, alt: false, control);
}
