using TajpanShowController.Core.Interfaces;
using TajpanShowController.Core.Models;
using TajpanShowController.Core.Services;
using TajpanShowController.Infrastructure.Serial;
using Xunit;

namespace TajpanShowController.Tests;

public sealed class RemoteServiceTests
{
    [Fact] public async Task SimulatorUsesSameProtocolAndEdgeDetection()
    {
        var sim = new SimulatedRemoteTransport { ButtonBits = "10000000" }; await using var service = new RemoteControllerService(_ => sim);
        var count = 0; service.ButtonPressed += (_, b) => { if (b == RemoteButton.Start) count++; };
        var ct = TestContext.Current.CancellationToken; await service.ConnectAsync("SIM", true, ct); await Task.Delay(35, ct); await service.DisconnectAsync(ct); Assert.Equal(1, count);
    }
    [Fact] public async Task LostConnectionBecomesFaultAfterMaximumFailures()
    {
        var sim = new SimulatedRemoteTransport { DropResponses = true }; await using var service = new RemoteControllerService(_ => sim);
        var ct = TestContext.Current.CancellationToken; await service.ConnectAsync("SIM", true, ct);
        for (var i = 0; i < 50 && service.ConnectionState != RemoteConnectionState.Fault; i++) await Task.Delay(10, ct);
        Assert.Equal(RemoteConnectionState.Fault, service.ConnectionState);
    }
    [Fact] public async Task PlaybackStateIsAcceptedByRemoteDisplayLayer()
    {
        var sim = new SimulatedRemoteTransport(); await using var service = new RemoteControllerService(_ => sim);
        service.UpdateDisplay(4, "Fő playback", PlaybackState.Paused, TimeSpan.FromSeconds(12.3));
        var ct = TestContext.Current.CancellationToken; await service.ConnectAsync("SIM", true, ct); await Task.Delay(60, ct); Assert.Equal(RemoteConnectionState.Connected, service.ConnectionState);
    }
}
