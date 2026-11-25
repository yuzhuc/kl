using System.Threading.Channels;
using Kl.Client;
using Kl.Gui.Services;

namespace Kl.Gui.Tests;

/// <summary>
/// The presenter is the only place where core callbacks cross into the GUI.
/// These tests pin the queueing and the approval handshake contract.
/// </summary>
public sealed class CoreSessionPresenterTests
{
    private static ApprovalRequestEvent Approval(string approvalId = "approval-1", string name = "shell") =>
        new(approvalId, name, "run", @"D:\kl", "需要执行命令", """{"cmd":"ls"}""");

    [Fact]
    public async Task OnEventAsyncQueuesEventsForTheConsumer()
    {
        var presenter = new CoreSessionPresenter();

        await presenter.OnEventAsync(new SessionEvent(SessionEventKind.TurnStarted), CancellationToken.None);
        await presenter.OnEventAsync(
            new SessionEvent(SessionEventKind.AssistantDelta, Text: "你好", Seq: 3),
            CancellationToken.None);

        var started = await presenter.Events.ReadAsync();
        var delta = await presenter.Events.ReadAsync();

        Assert.Equal(SessionEventKind.TurnStarted, started.Kind);
        Assert.Equal(SessionEventKind.AssistantDelta, delta.Kind);
        Assert.Equal("你好", delta.Text);
        Assert.Equal(3, delta.Seq);
    }

    [Fact]
    public async Task RequestApprovalDecisionEmitsApprovalRequestedAndWaitsForResolution()
    {
        var presenter = new CoreSessionPresenter();

        var decisionTask = presenter.RequestApprovalDecisionAsync(Approval(), CancellationToken.None).AsTask();
        var requested = await presenter.Events.ReadAsync();

        Assert.Equal(SessionEventKind.ApprovalRequested, requested.Kind);
        Assert.Equal("approval-1", requested.ApprovalId);
        Assert.Equal("shell", requested.Name);
        Assert.Equal("run", requested.Action);
        Assert.Equal(@"D:\kl", requested.Resource);
        Assert.Equal("需要执行命令", requested.Reason);

        Assert.True(presenter.ResolveApprovalDecision("approval-1", "allow"));
        Assert.Equal("allow", await decisionTask);
    }

    [Theory]
    [InlineData("allow", true)]
    [InlineData("allow_session", true)]
    [InlineData("deny", false)]
    public async Task RequestApprovalAsyncMapsDecisionsToBool(string decision, bool expected)
    {
        var presenter = new CoreSessionPresenter();

        var approvalTask = presenter.RequestApprovalAsync(Approval(), CancellationToken.None).AsTask();
        await presenter.Events.ReadAsync();
        Assert.True(presenter.ResolveApprovalDecision("approval-1", decision));

        Assert.Equal(expected, await approvalTask);
    }

    [Fact]
    public async Task CancellationReturnsDenyAndEmitsApprovalResolved()
    {
        var presenter = new CoreSessionPresenter();
        using var cancellation = new CancellationTokenSource();

        var decisionTask = presenter.RequestApprovalDecisionAsync(Approval(), cancellation.Token).AsTask();
        var requested = await presenter.Events.ReadAsync();
        Assert.Equal(SessionEventKind.ApprovalRequested, requested.Kind);

        cancellation.Cancel();

        Assert.Equal("deny", await decisionTask);
        var resolved = await presenter.Events.ReadAsync();
        Assert.Equal(SessionEventKind.ApprovalResolved, resolved.Kind);
        Assert.Equal("approval-1", resolved.ApprovalId);
        Assert.Equal("deny", resolved.Decision);
    }

    [Fact]
    public void ResolveApprovalDecisionReturnsFalseForUnknownId()
    {
        var presenter = new CoreSessionPresenter();

        Assert.False(presenter.ResolveApprovalDecision("missing", "allow"));
    }

    [Fact]
    public async Task ResolveApprovalDecisionReturnsFalseWhenAlreadyResolved()
    {
        var presenter = new CoreSessionPresenter();
        var decisionTask = presenter.RequestApprovalDecisionAsync(Approval(), CancellationToken.None).AsTask();

        Assert.True(presenter.ResolveApprovalDecision("approval-1", "allow"));
        Assert.False(presenter.ResolveApprovalDecision("approval-1", "deny"));
        Assert.Equal("allow", await decisionTask);
    }

    [Fact]
    public async Task CompleteClosesTheEventChannel()
    {
        var presenter = new CoreSessionPresenter();

        presenter.Complete();

        await Assert.ThrowsAsync<ChannelClosedException>(
            async () => await presenter.Events.ReadAsync());
    }
}
