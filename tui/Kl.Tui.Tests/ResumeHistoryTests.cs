using Xunit;

namespace Kl.Tui.Tests;

public sealed class ResumeHistoryTests
{
    [Fact]
    public async Task ResumeReplaysOldMessagesBeforeLiveEvents()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "kl-resume-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(
            Path.Combine(root, "script.json"),
            """[{"text":"old answer"}]""");
        var config = Path.Combine(root, "kl.toml");
        await File.WriteAllTextAsync(
            config,
            """
            [agent]
            provider = "scripted"
            model = "scripted"
            scripted_path = "script.json"
            """);
        var options = new TuiOptions
        {
            ConfigPath = config,
            HomePath = Path.Combine(root, "home"),
            Workspace = workspace
        };

        try
        {
            string sessionId;
            await using (var session = await KlSessionClient.StartAsync(
                options.ToClientOptions(),
                options.ToSessionOptions(),
                new SilentPresenter()))
            {
                sessionId = session.SessionId;
                var outcome = await session.RunTurnAsync("old prompt");
                Assert.Equal("completed", outcome.Status);
            }

            options.Session = sessionId;
            await using var resumed = await KlSessionClient.StartAsync(
                options.ToClientOptions(),
                options.ToSessionOptions(),
                new SilentPresenter());
            var history = await resumed.HistoryAsync(20);
            var messages = ChatTuiState.ParseHistory(history);

            Assert.Collection(
                messages,
                message =>
                {
                    Assert.Equal("user", message.Role);
                    Assert.Equal("old prompt", message.Text);
                },
                message =>
                {
                    Assert.Equal("assistant", message.Role);
                    Assert.Equal("old answer", message.Text);
                });

            var state = new ChatTuiState();
            state.AddHistory(messages);
            state.Apply(new SessionEvent(SessionEventKind.TurnStarted));
            state.Apply(new SessionEvent(SessionEventKind.AssistantDelta, Text: "new answer"));

            Assert.Contains(state.Transcript, entry => entry.Text == "old prompt");
            Assert.Contains(state.Transcript, entry => entry.Text == "old answer");
            Assert.Equal(3, state.Transcript.Count);
            Assert.Equal("new answer", state.Transcript[^1].Text);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void PersistedReasoningIsRestoredWithItsTurn()
    {
        var messages = new List<HistoryMessage>
        {
            new("user", "old prompt", "", 2, null, null, null),
            new(
                "assistant",
                "old answer",
                "",
                5,
                null,
                null,
                null,
                Reasoning: "Check files."),
            new("user", "second prompt", "", 7, null, null, null),
            new("assistant", "second answer", "", 9, null, null, null)
        };
        var events = ChatTuiState.BuildHistoryEvents(messages);

        var state = new ChatTuiState();
        state.AddHistory(messages, events.Reasoning, events.Tools);

        Assert.Collection(
            state.Transcript,
            entry => Assert.Equal(TranscriptKind.User, entry.Kind),
            entry =>
            {
                Assert.Equal(TranscriptKind.Reasoning, entry.Kind);
                Assert.Equal("Check files.", entry.Text);
                Assert.True(entry.IsComplete);
            },
            entry => Assert.Equal(TranscriptKind.Assistant, entry.Kind),
            entry => Assert.Equal(TranscriptKind.User, entry.Kind),
            entry => Assert.Equal(TranscriptKind.Assistant, entry.Kind));

        var plain = DiffRender.Ansi.Strip(ChatTuiRenderer.Render(100, 24, state));
        Assert.Contains("▸ Done thinking", plain);
        Assert.DoesNotContain("Ctrl+R to show", plain);
    }

    [Fact]
    public void PersistedToolCallsAreRestoredWithOutputAndDiff()
    {
        var messages = new List<HistoryMessage>
        {
            new("user", "run it", "", 2, null, null, null),
            new(
                "tool",
                "",
                "",
                5,
                "shell",
                true,
                "exit 0",
                ToolOutput: "line1\nline2\n",
                ToolDiff: "+new"),
            new("assistant", "done", "", 6, null, null, null)
        };
        var events = ChatTuiState.BuildHistoryEvents(messages);
        var state = new ChatTuiState();
        state.AddHistory(messages, events.Reasoning, events.Tools);

        var tool = Assert.Single(
            state.Transcript,
            entry => entry.Kind == TranscriptKind.Tool);
        Assert.Contains("[ok] shell: exit 0", tool.Text);
        Assert.Contains("line2", tool.Text);
        var diff = Assert.Single(
            state.Transcript,
            entry => entry.Kind == TranscriptKind.Diff);
        Assert.Equal("+new", diff.Text);
        Assert.Contains(
            state.Transcript,
            entry => entry.Kind == TranscriptKind.Assistant && entry.Text == "done");
    }

    private sealed class SilentPresenter : ISessionPresenter
    {
        public ValueTask OnEventAsync(
            SessionEvent sessionEvent,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<bool> RequestApprovalAsync(
            ApprovalRequestEvent request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);
    }
}