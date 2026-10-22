using System.Text.Json;
using Xunit;

namespace Kl.Tui.Tests;

public sealed class ServerReconnectTests
{
    [Fact]
    public async Task ReplaysAttachEventsOnceAndReconnectsAfterLastSeq()
    {
        await using var server = new FakeCoreServer();
        server.QueueAttachReplay(FakeCoreServer.Event(
            1,
            "turn.completed",
            new { turn_id = "turn-1", status = "completed", text = "old", input_tokens = 1, output_tokens = 2 }));
        var presenter = new RecordingPresenter();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = await KlSessionClient.StartAsync(
            new KlClientOptions { Endpoint = server.Endpoint },
            new KlSessionOptions
            {
                SessionId = "session-1",
                Workspace = Path.GetTempPath()
            },
            presenter,
            timeout.Token);

        await WaitForAsync(() => session.LastSeq >= 1, timeout.Token);
        Assert.Contains(
            presenter.Events,
            item => item.Kind == SessionEventKind.TurnCompleted && item.Text == "old");

        server.PushEvent(1, "turn.completed", new
        {
            turn_id = "turn-1",
            status = "completed",
            text = "duplicate",
            input_tokens = 0,
            output_tokens = 0
        });
        server.PushEvent(2, "turn.failed", new
        {
            turn_id = "turn-2",
            message = "new"
        });
        await WaitForAsync(() => session.LastSeq >= 2, timeout.Token);
        Assert.DoesNotContain(
            presenter.Events,
            item => item.Kind == SessionEventKind.TurnCompleted && item.Text == "duplicate");
        Assert.Contains(
            presenter.Events,
            item => item.Kind == SessionEventKind.TurnFailed && item.Text == "new");

        server.QueueAttachReplay(FakeCoreServer.Event(
            2,
            "turn.failed",
            new { turn_id = "turn-2", message = "new" }));
        await session.ReconnectAsync(timeout.Token);
        Assert.Equal(2, session.LastSeq);
        Assert.Contains(2L, server.AttachAfterSeqs);

        server.PushEvent(3, "turn.failed", new
        {
            turn_id = "turn-3",
            message = "after-reconnect"
        });
        await WaitForAsync(() => session.LastSeq >= 3, timeout.Token);
        Assert.Contains(
            presenter.Events,
            item => item.Kind == SessionEventKind.TurnFailed && item.Text == "after-reconnect");
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(25, cancellationToken);
        }
    }

    private sealed class RecordingPresenter : ISessionPresenter
    {
        public List<SessionEvent> Events { get; } = [];

        public ValueTask OnEventAsync(
            SessionEvent sessionEvent,
            CancellationToken cancellationToken)
        {
            lock (Events)
            {
                Events.Add(sessionEvent);
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> RequestApprovalAsync(
            ApprovalRequestEvent request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);
    }
}
