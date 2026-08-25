using TajpanShowController.Core.Models;
using TajpanShowController.Core.Services;
using Xunit;

namespace TajpanShowController.Tests;

public sealed class StateAndRetryTests
{
    [Fact] public void HeldButtonRaisesOnlyOnceUntilReleased()
    {
        var d = new RemoteButtonEdgeDetector(); var pressed = new RemoteButtonState(true,false,false,false,false,false,false,false); var released = default(RemoteButtonState);
        Assert.Equal([RemoteButton.Start], d.Update(pressed)); Assert.Empty(d.Update(pressed)); Assert.Empty(d.Update(released)); Assert.Equal([RemoteButton.Start], d.Update(pressed));
    }
    [Fact] public async Task AckStopsRetryImmediately()
    {
        var calls = 0; var ok = await new RetryPolicy(3).ExecuteAsync((_, _) => Task.FromResult(++calls == 1), TestContext.Current.CancellationToken); Assert.True(ok); Assert.Equal(1, calls);
    }
    [Fact] public async Task NackRetriesAndCanRecover()
    {
        var calls = 0; var ok = await new RetryPolicy(3).ExecuteAsync((_, _) => Task.FromResult(++calls == 3), TestContext.Current.CancellationToken); Assert.True(ok); Assert.Equal(3, calls);
    }
    [Fact] public async Task TimeoutStopsAtMaximumRetry()
    {
        var calls = 0; var ok = await new RetryPolicy(3).ExecuteAsync((_, _) => { calls++; return Task.FromResult(false); }, TestContext.Current.CancellationToken); Assert.False(ok); Assert.Equal(3, calls);
    }
}
