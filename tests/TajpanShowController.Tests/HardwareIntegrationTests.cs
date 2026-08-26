using TajpanShowController.Core.Diagnostics;
using TajpanShowController.Core.Interfaces;
using TajpanShowController.Core.Models;
using TajpanShowController.Infrastructure.Serial;
using Xunit;

namespace TajpanShowController.Tests;

public sealed class HardwareIntegrationTests
{
    [Fact]
    public async Task ConfiguredComPortCompletesProductionHandshake()
    {
        var port = Environment.GetEnvironmentVariable("TAJPAN_HARDWARE_COM");
        if (string.IsNullOrWhiteSpace(port))
        {
            Assert.Skip("Set TAJPAN_HARDWARE_COM to run the physical remote smoke test.");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        await using var service = new RemoteControllerService(_ => new SerialPortTransport());
        string? handshakeResponse = null;
        service.StatusChanged += (_, _) =>
        {
            if (service.ConnectionState == RemoteConnectionState.Connected) handshakeResponse ??= service.LastResponse;
        };
        await service.ConnectAsync(port, false, ct);

        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline && service.ConnectionState == RemoteConnectionState.Connecting)
            await Task.Delay(25, ct);

        Assert.Equal(RemoteConnectionState.Connected, service.ConnectionState);
        Assert.Equal("@B00000000", handshakeResponse);
    }

    [Fact]
    public async Task ConfiguredComPortMaintainsConnectionWhileSynchronizingDisplay()
    {
        var port = Environment.GetEnvironmentVariable("TAJPAN_HARDWARE_COM");
        if (string.IsNullOrWhiteSpace(port))
        {
            Assert.Skip("Set TAJPAN_HARDWARE_COM to run the physical remote display-sync test.");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        await using var service = new RemoteControllerService(_ => new SerialPortTransport());
        service.UpdateDisplay(1, "Hardware test", PlaybackState.Playing, TimeSpan.FromSeconds(12.3));
        await service.ConnectAsync(port, false, ct);

        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline && service.ConnectionState == RemoteConnectionState.Connecting)
            await Task.Delay(25, ct);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);

        Assert.Equal(RemoteConnectionState.Connected, service.ConnectionState);
        Assert.NotNull(service.TimeSinceLastValidResponse);
        Assert.True(service.TimeSinceLastValidResponse < TimeSpan.FromMilliseconds(250));
        Assert.DoesNotContain(service.DebugLog.Drain(1000),
            entry => entry.Kind is RemoteDebugLogKind.Warning or RemoteDebugLogKind.Error);
    }

    [Fact]
    public async Task RapidCanceledConnectDisconnectCyclesRecoverOnConfiguredComPort()
    {
        var port = Environment.GetEnvironmentVariable("TAJPAN_HARDWARE_COM");
        if (string.IsNullOrWhiteSpace(port))
        {
            Assert.Skip("Set TAJPAN_HARDWARE_COM to run the physical remote stress test.");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        await using var service = new RemoteControllerService(_ => new SerialPortTransport());
        await using var coordinator = new TajpanShowController.Core.Services.RemoteConnectionCoordinator(
            service,
            () => new TajpanShowController.Core.Services.RemoteConnectionOptions(port, false),
            () => { },
            reconnectInterval: TimeSpan.FromMilliseconds(50),
            connectAttemptTimeout: TimeSpan.FromSeconds(2));
        coordinator.SetAutoReconnect(false);

        for (var i = 0; i < 8; i++)
        {
            var connect = coordinator.ManualConnectAsync(ct);
            await Task.Delay(1, ct);
            await coordinator.ManualDisconnectAsync(ct);
            await connect;
        }

        Assert.True(await coordinator.ManualConnectAsync(ct));
        Assert.Equal(RemoteConnectionState.Connected, service.ConnectionState);
    }
}
