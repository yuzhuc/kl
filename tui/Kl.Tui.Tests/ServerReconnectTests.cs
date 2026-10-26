using System.Text.Json;
using Xunit;

namespace Kl.Tui.Tests;

public sealed class ServerReconnectTests
{
    [Fact]
    public async Task SkipInitialReplayStartsAtAttachLastSeq()
    {
        await using var server = new FakeCoreServer();
        server.QueueAttachReplay(FakeCoreServer.Event(
            7,
            "turn.completed",
            new { turn_id = "turn-old", status = "completed", text = "old", input_tokens = 1, output_tokens = 2 }));
        var presenter = new RecordingPresenter();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = await KlSessionClient.StartAsync(
            new KlClientOptions { Endpoint = server.Endpoint },
            new KlSessionOptions
            {
                SessionId = "session-1",
                Workspace = Path.GetTempPath(),
                SkipInitialReplay = true
            },
            presenter,
            timeout.Token);

        Assert.Equal(7, session.LastSeq);
        Assert.DoesNotContain(
            presenter.Events,
            item => item.Kind == SessionEventKind.TurnCompleted && item.Text == "old");

        server.PushEvent(8, "turn.failed", new
        {
            turn_id = "turn-new",
            message = "new"
        });
        await WaitForAsync(() => session.LastSeq >= 8, timeout.Token);
        Assert.Contains(
            presenter.Events,
            item => item.Kind == SessionEventKind.TurnFailed && item.Text == "new");
    }

    [Fact]
    public async Task SecondClientIsReadOnlyObserverAndReceivesEvents()
    {
        await using var server = new FakeCoreServer();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var writer = await KlSessionClient.StartAsync(
            new KlClientOptions { Endpoint = server.Endpoint },
            new KlSessionOptions { SessionId = "session-1", Workspace = Path.GetTempPath() },
            new RecordingPresenter(),
            timeout.Token);
        var observerPresenter = new RecordingPresenter();
        await using var observer = await KlSessionClient.StartAsync(
            new KlClientOptions { Endpoint = server.Endpoint },
            new KlSessionOptions { SessionId = "session-1", Workspace = Path.GetTempPath() },
            observerPresenter,
            timeout.Token);

        Assert.True(writer.IsWriter);
        Assert.False(observer.IsWriter);

        server.PushEvent(5, "turn.failed", new
        {
            turn_id = "turn-observer",
            message = "observed"
        });
        await WaitForAsync(() => observer.LastSeq >= 5, timeout.Token);
        Assert.Contains(
            observerPresenter.Events,
            item => item.Kind == SessionEventKind.TurnFailed && item.Text == "observed");

        await observer.ReconnectAsync(timeout.Token);
        Assert.False(observer.IsWriter);
        server.PushEvent(6, "turn.failed", new
        {
            turn_id = "turn-reconnected",
            message = "after-reconnect"
        });
        await WaitForAsync(() => observer.LastSeq >= 6, timeout.Token);

        var turnRequests = server.RequestMethods.Count(method => method == Protocol.TurnStart);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => observer.RunTurnAsync("must not be sent", timeout.Token));
        Assert.Contains("read-only", error.Message);
        Assert.Equal(
            turnRequests,
            server.RequestMethods.Count(method => method == Protocol.TurnStart));

        var cancelRequests = server.RequestMethods.Count(method => method == Protocol.TurnCancel);
        var cancelError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => observer.CancelAsync(timeout.Token));
        Assert.Contains("read-only", cancelError.Message);
        Assert.Equal(
            cancelRequests,
            server.RequestMethods.Count(method => method == Protocol.TurnCancel));
    }

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
