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

    [Fact]
    public async Task DisplayBurstStressKeepsPollingAndRemoteConnected()
    {
        var port = Environment.GetEnvironmentVariable("TAJPAN_HARDWARE_COM");
        if (string.IsNullOrWhiteSpace(port))
        {
            Assert.Skip("Set TAJPAN_HARDWARE_COM to run the physical remote stress test.");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        await using var service = new RemoteControllerService(_ => new SerialPortTransport());
        await service.ConnectAsync(port, false, ct);
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline && service.ConnectionState != RemoteConnectionState.Connected)
            await Task.Delay(10, ct);
        Assert.Equal(RemoteConnectionState.Connected, service.ConnectionState);

        var falseDisconnects = 0;
        var disconnectAge = TimeSpan.Zero;
        service.StatusChanged += (_, _) =>
        {
            if (service.ConnectionState == RemoteConnectionState.Disconnected)
            {
                Interlocked.Increment(ref falseDisconnects);
                disconnectAge = service.TimeSinceLastValidResponse ?? TimeSpan.Zero;
            }
        };
        var cycles = int.TryParse(Environment.GetEnvironmentVariable("TAJPAN_HARDWARE_CYCLES"), out var configuredCycles)
            ? Math.Clamp(configuredCycles, 1, 50) : 50;
        var sendDisplay = !string.Equals(Environment.GetEnvironmentVariable("TAJPAN_HARDWARE_NO_DISPLAY"), "1", StringComparison.Ordinal);
        var stateOnly = string.Equals(Environment.GetEnvironmentVariable("TAJPAN_HARDWARE_STATE_ONLY"), "1", StringComparison.Ordinal);
        var fieldMode = Environment.GetEnvironmentVariable("TAJPAN_HARDWARE_FIELD")?.ToLowerInvariant();
        if (fieldMode is not null)
        {
            service.UpdateDisplay(1, "BASE", PlaybackState.Stopped, TimeSpan.Zero);
            await Task.Delay(1000, ct);
            if (fieldMode == "sequence")
            {
                service.UpdateDisplay(2, "BASE", PlaybackState.Stopped, TimeSpan.Zero); await Task.Delay(1000, ct);
                service.UpdateDisplay(2, "NAME", PlaybackState.Stopped, TimeSpan.Zero); await Task.Delay(1000, ct);
                service.UpdateDisplay(2, "NAME", PlaybackState.Playing, TimeSpan.Zero); await Task.Delay(1000, ct);
                service.UpdateDisplay(2, "NAME", PlaybackState.Playing, TimeSpan.FromSeconds(1)); await Task.Delay(1000, ct);
            }
        }
        for (var i = 0; i < cycles; i++)
        {
            if (sendDisplay) service.UpdateDisplay(
                fieldMode == "number" ? i + 2 : (stateOnly || fieldMode is not null ? 1 : i + 1),
                fieldMode == "name" ? $"NAME {i:00}" : (stateOnly || fieldMode is not null ? "TEST" : $"PLAY {i:00}"),
                fieldMode == "state" ? PlaybackState.Playing : (stateOnly || fieldMode is not null ? PlaybackState.Stopped : PlaybackState.Playing),
                fieldMode == "time" ? TimeSpan.FromSeconds(i + 1) : (stateOnly || fieldMode is not null ? TimeSpan.Zero : TimeSpan.FromMilliseconds(i * 100)));
            await Task.Delay(100, ct);
            if (sendDisplay) service.UpdateDisplay(
                fieldMode == "number" ? i + 2 : (stateOnly || fieldMode is not null ? 1 : i + 1),
                fieldMode == "name" ? $"STOP {i:00}" : (stateOnly || fieldMode is not null ? "TEST" : $"STOP {i:00}"),
                fieldMode == "state" ? PlaybackState.Stopped : (stateOnly || fieldMode is not null ? PlaybackState.Stopped : PlaybackState.Stopped),
                fieldMode == "time" ? TimeSpan.Zero : TimeSpan.Zero);
            await Task.Delay(100, ct);
        }

        var metrics = service.TimingMetrics.Snapshot();
        var drained = service.DebugLog.Drain(1000);
        var diagnostics = string.Join(" | ", drained
            .Where(e => e.Kind is RemoteDebugLogKind.State or RemoteDebugLogKind.Warning or RemoteDebugLogKind.Error)
            .Select(e => $"{e.Kind}:{e.Message}"));
        var txTrace = string.Join(",", drained
            .Where(e => e.Kind == RemoteDebugLogKind.Tx)
            .Select(e => e.Message));
        Console.WriteLine($"COM12 display stress: max poll gap={metrics.MaxPollGap.TotalMilliseconds:F2} ms, " +
            $"max valid RX gap={metrics.MaxValidResponseGap.TotalMilliseconds:F2} ms, " +
            $"poll RTT avg/max={metrics.AveragePollRtt.TotalMilliseconds:F2}/{metrics.MaxPollRtt.TotalMilliseconds:F2} ms, " +
            $"false disconnects={falseDisconnects}");
        Assert.True(falseDisconnects == 0,
            $"false disconnects={falseDisconnects}, max poll gap={metrics.MaxPollGap.TotalMilliseconds:F2} ms, " +
            $"max valid RX gap={metrics.MaxValidResponseGap.TotalMilliseconds:F2} ms, state={service.ConnectionState}, " +
            $"disconnect age={disconnectAge.TotalMilliseconds:F2} ms, " +
            $"diagnostics={diagnostics}, tx={txTrace}");
        Assert.Equal(RemoteConnectionState.Connected, service.ConnectionState);
        Assert.True(metrics.MaxPollGap < TimeSpan.FromMilliseconds(50));
        Assert.True(metrics.MaxValidResponseGap < TimeSpan.FromMilliseconds(50));
    }
}
