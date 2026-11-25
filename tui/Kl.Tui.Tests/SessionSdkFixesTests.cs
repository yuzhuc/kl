using Xunit;

namespace Kl.Tui.Tests;

/// <summary>
/// Regression coverage for the Kl.Client fixes: teardown after the core
/// connection dies (BUG-1), reconnect loading the session into a restarted core
/// (BUG-2), and the presenter correlation fields (DEF-4).
/// </summary>
public sealed class SessionSdkFixesTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(20);

    private static KlSessionOptions SessionOptions() => new()
    {
        SessionId = "session-1",
        Workspace = Path.GetTempPath(),
        SkipInitialReplay = true
    };

    [Fact]
    public async Task DisposeAfterCoreConnectionLossDoesNotThrow()
    {
        var presenter = new RecordingPresenter();
        var server = new FakeCoreServer();
        using var timeout = new CancellationTokenSource(TestTimeout);
        KlSessionClient? session = null;
        try
        {
            session = await KlSessionClient.StartAsync(
                new KlClientOptions { Endpoint = server.Endpoint },
                SessionOptions(),
                presenter,
                timeout.Token);

            // Kill the core side of the transport while the client still holds
            // the session. The pump faults with an IOException; DisposeAsync
            // used to surface it (BUG-1).
            await server.DisposeAsync();

            await WaitForAsync(
                () => presenter.Events.Any(item =>
                    item.Kind == SessionEventKind.Error
                    && item.Text.Contains("core connection closed", StringComparison.Ordinal)),
                timeout.Token);

            await session.DisposeAsync();
            session = null;
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReconnectAfterCoreRestartResumesSession()
    {
        var presenter = new RecordingPresenter();
        var server = new FakeCoreServer();
        using var timeout = new CancellationTokenSource(TestTimeout);
        KlSessionClient? session = null;
        try
        {
            session = await KlSessionClient.StartAsync(
                new KlClientOptions { Endpoint = server.Endpoint },
                SessionOptions(),
                presenter,
                timeout.Token);
            server.PushEvent(
                1,
                "turn.failed",
                new { turn_id = "turn-before", message = "before" });
            await WaitForAsync(() => session.LastSeq >= 1, timeout.Token);

            // Core process exits: the connection drops and pump faults.
            await server.DisposeAsync();
            await WaitForAsync(
                () => presenter.Events.Any(item => item.Kind == SessionEventKind.Error),
                timeout.Token);

            // A new core starts on the same endpoint with the same home, so the
            // session only exists on disk until the client reconnects.
            await using var restarted = new FakeCoreServer(endpoint: server.Endpoint);
            await restarted.Ready.WaitAsync(timeout.Token);

            // Before the fix this threw IOException: kl-core connection closed
            // from StopPumpAsync and never sent session.resume (BUG-1/BUG-2).
            await session.ReconnectAsync(timeout.Token);

            Assert.True(session.IsWriter);
            Assert.Equal(1, session.LastSeq);

            var methods = restarted.RequestMethods.ToList();
            var resumeIndex = methods.IndexOf(Protocol.SessionResume);
            var attachIndex = methods.IndexOf(Protocol.SessionAttach);
            Assert.True(resumeIndex >= 0, "reconnect must send session.resume");
            Assert.True(
                attachIndex > resumeIndex,
                "session.resume must run before session.attach");
            Assert.Contains((long?)1, restarted.AttachAfterSeqs);

            restarted.PushEvent(
                2,
                "turn.failed",
                new { turn_id = "turn-after", message = "after" });
            await WaitForAsync(() => session.LastSeq >= 2, timeout.Token);
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task PresenterReceivesSequenceAndCorrelationIds()
    {
        var presenter = new RecordingPresenter();
        await using var server = new FakeCoreServer();
        using var timeout = new CancellationTokenSource(TestTimeout);
        await using var session = await KlSessionClient.StartAsync(
            new KlClientOptions { Endpoint = server.Endpoint },
            SessionOptions(),
            presenter,
            timeout.Token);

        server.PushEvent(1, "turn.started", new { turn_id = "turn-1" });
        server.PushEvent(2, "tool.started", new
        {
            turn_id = "turn-1",
            call_id = "call-1",
            name = "write_file",
            args = new { path = "out.txt" }
        });
        server.PushEvent(3, "tool.output", new
        {
            turn_id = "turn-1",
            call_id = "call-1",
            stream = "combined",
            chunk = "wrote"
        });
        server.PushEvent(4, "tool.completed", new
        {
            turn_id = "turn-1",
            call_id = "call-1",
            name = "write_file",
            ok = true,
            summary = "wrote 8 bytes",
            output = "wrote 8 bytes to out.txt"
        });
        server.PushEvent(5, "tool.approval.resolved", new
        {
            turn_id = "turn-1",
            call_id = "call-1",
            approval_id = "approval-1",
            decision = "allow"
        });
        server.PushEvent(6, "turn.completed", new
        {
            turn_id = "turn-1",
            status = "completed",
            text = "done",
            input_tokens = 1,
            output_tokens = 2
        });

        await WaitForAsync(
            () => presenter.Events.Any(item => item.Kind == SessionEventKind.TurnCompleted),
            timeout.Token);

        var events = presenter.Events;
        var started = Assert.Single(events, item => item.Kind == SessionEventKind.TurnStarted);
        Assert.Equal(1, started.Seq);
        Assert.Equal("turn-1", started.TurnId);

        var toolStarted = Assert.Single(events, item => item.Kind == SessionEventKind.ToolStarted);
        Assert.Equal(2, toolStarted.Seq);
        Assert.Equal("turn-1", toolStarted.TurnId);
        Assert.Equal("call-1", toolStarted.CallId);
        Assert.Equal("write_file", toolStarted.Name);

        var toolOutput = Assert.Single(events, item => item.Kind == SessionEventKind.ToolOutput);
        Assert.Equal(3, toolOutput.Seq);
        Assert.Equal("call-1", toolOutput.CallId);
        Assert.Equal("wrote", toolOutput.Text);

        var toolCompleted = Assert.Single(events, item => item.Kind == SessionEventKind.ToolCompleted);
        Assert.Equal(4, toolCompleted.Seq);
        Assert.Equal("call-1", toolCompleted.CallId);
        Assert.Equal("wrote 8 bytes to out.txt", toolCompleted.Output);
        Assert.Equal("wrote 8 bytes", toolCompleted.Summary);

        var approval = Assert.Single(events, item => item.Kind == SessionEventKind.ApprovalResolved);
        Assert.Equal(5, approval.Seq);
        Assert.Equal("approval-1", approval.ApprovalId);
        Assert.Equal("call-1", approval.CallId);
        Assert.Equal("allow", approval.Decision);

        var completed = Assert.Single(events, item => item.Kind == SessionEventKind.TurnCompleted);
        Assert.Equal(6, completed.Seq);
        Assert.Equal("turn-1", completed.TurnId);
        Assert.Equal("done", completed.Text);
    }

    private static async Task WaitForAsync(
        Func<bool> condition,
        CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(25, cancellationToken);
        }
    }

    private sealed class RecordingPresenter : ISessionPresenter
    {
        private readonly List<SessionEvent> _events = [];

        /// <summary>Thread-safe snapshot of the events received so far.</summary>
        public IReadOnlyList<SessionEvent> Events
        {
            get
            {
                lock (_events)
                {
                    return _events.ToList();
                }
            }
        }

        public ValueTask OnEventAsync(
            SessionEvent sessionEvent,
            CancellationToken cancellationToken)
        {
            lock (_events)
            {
                _events.Add(sessionEvent);
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> RequestApprovalAsync(
            ApprovalRequestEvent request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(false);
    }
}
