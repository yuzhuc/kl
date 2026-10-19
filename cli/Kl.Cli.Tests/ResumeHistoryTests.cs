using Xunit;

namespace Kl.Cli.Tests;

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
        var options = new CliOptions
        {
            ConfigPath = config,
            HomePath = Path.Combine(root, "home"),
            Workspace = workspace
        };

        try
        {
            string sessionId;
            await using (var session = await KlSessionClient.StartAsync(
                options,
                new SilentPresenter()))
            {
                sessionId = session.SessionId;
                var outcome = await session.RunTurnAsync("old prompt");
                Assert.Equal("completed", outcome.Status);
            }

            options.Session = sessionId;
            await using var resumed = await KlSessionClient.StartAsync(
                options,
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
            new("assistant", "old answer", "", 5, null, null, null),
            new("user", "second prompt", "", 7, null, null, null),
            new("assistant", "second answer", "", 9, null, null, null)
        };
        var lines = new[]
        {
            """{"seq":2,"event":{"type":"turn.started","data":{"turn_id":"t1"}}}""",
            """{"seq":3,"event":{"type":"turn.reasoning","data":{"turn_id":"t1","text":"Check"}}}""",
            """{"seq":4,"event":{"type":"turn.reasoning","data":{"turn_id":"t1","text":" files."}}}""",
            """{"seq":5,"event":{"type":"turn.delta","data":{"turn_id":"t1","text":"old answer"}}}""",
            """{"seq":6,"event":{"type":"turn.completed","data":{"turn_id":"t1"}}}""",
            """{"seq":7,"event":{"type":"turn.started","data":{"turn_id":"t2"}}}""",
            """{"seq":8,"event":{"type":"turn.delta","data":{"turn_id":"t2","text":"second answer"}}}""",
            """{"seq":9,"event":{"type":"turn.completed","data":{"turn_id":"t2"}}}"""
        };

        var state = new ChatTuiState();
        state.AddHistory(messages, ChatTuiState.ParseReasoningEvents(lines));

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