using System.Text.Json;
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
            var messages = SessionHistory.ParseHistory(history);

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
    public void HistoryParsesToolOutputAndDiff()
    {
        using var document = JsonDocument.Parse(
            """
            {"messages":[
              {"role":"tool","text":"","seq":5,
               "tool":{"name":"shell","ok":true,"summary":"exit 0","output":"line1\nline2\n","diff":"+new"}}
            ]}
            """);
        var message = Assert.Single(SessionHistory.ParseHistory(document.RootElement));

        Assert.Equal("tool", message.Role);
        Assert.Equal(5, message.Seq);
        Assert.Equal("[ok] shell: exit 0", message.ToolDisplay());
        Assert.Equal("line1\nline2\n", message.ToolOutput);
        Assert.Equal("+new", message.ToolDiff);
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
