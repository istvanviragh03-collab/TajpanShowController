using TajpanShowController.Core.Diagnostics;
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
        for (var i = 0; i < 50 && service.ConnectionState == RemoteConnectionState.Connecting; i++) await Task.Delay(10, ct);
        Assert.Equal(RemoteConnectionState.Disconnected, service.ConnectionState);
    }
    [Fact] public async Task PlaybackStateIsAcceptedByRemoteDisplayLayer()
    {
        var sim = new SimulatedRemoteTransport(); await using var service = new RemoteControllerService(_ => sim);
        service.UpdateDisplay(4, "Fő playback", PlaybackState.Paused, TimeSpan.FromSeconds(12.3));
        var ct = TestContext.Current.CancellationToken; await service.ConnectAsync("SIM", true, ct); await Task.Delay(60, ct); Assert.Equal(RemoteConnectionState.Connected, service.ConnectionState);
    }
    [Fact] public async Task AllReleasedHeartbeatConnectsAndIsAcknowledged()
    {
        var sim = new SimulatedRemoteTransport { ButtonBits = "00000000" }; await using var service = new RemoteControllerService(_ => sim);
        var ct = TestContext.Current.CancellationToken; await service.ConnectAsync("SIM", true, ct); await Task.Delay(40, ct);
        Assert.Equal(RemoteConnectionState.Connected, service.ConnectionState);
        Assert.Contains("@S", sim.Writes);
        Assert.Contains("@A", sim.Writes);
    }

    [Fact]
    public async Task IdlePollingIsCoalescedToTheInitialTransaction()
    {
        var log = new RemoteDebugLogBuffer();
        var sim = new SimulatedRemoteTransport { ButtonBits = "00000000" };
        await using var service = new RemoteControllerService(_ => sim, log);
        var ct = TestContext.Current.CancellationToken;

        await service.ConnectAsync("SIM", true, ct);
        var idleSeconds = int.TryParse(Environment.GetEnvironmentVariable("TAJPAN_LONG_IDLE_SECONDS"), out var configuredSeconds)
            ? Math.Max(1, configuredSeconds)
            : 1;
        await Task.Delay(TimeSpan.FromSeconds(idleSeconds) + TimeSpan.FromMilliseconds(200), ct);
        await service.DisconnectAsync(ct);
        var entries = DrainAll(log);

        Assert.True(sim.Writes.Count(frame => frame == "@S") >= 100);
        Assert.Single(entries, entry => entry.Kind == RemoteDebugLogKind.Tx && entry.Message == "@S");
        Assert.Single(entries, entry => entry.Kind == RemoteDebugLogKind.Rx && entry.Message == "@B00000000");
        Assert.Single(entries, entry => entry.Kind == RemoteDebugLogKind.Tx && entry.Message == "@A");
    }

    [Fact]
    public async Task DisplayFramesAreLoggedAfterTheirSuccessfulTransportWrites()
    {
        var log = new RemoteDebugLogBuffer();
        var sim = new SimulatedRemoteTransport();
        await using var service = new RemoteControllerService(_ => sim, log);
        service.UpdateDisplay(4, "Main playback", PlaybackState.Paused, TimeSpan.FromSeconds(12.3));
        var ct = TestContext.Current.CancellationToken;

        await service.ConnectAsync("SIM", true, ct);
        await Task.Delay(150, ct);
        await service.DisconnectAsync(ct);
        var entries = DrainAll(log);

        Assert.Contains(entries, entry => entry.Kind == RemoteDebugLogKind.Tx && entry.Message == "@N4");
        Assert.Contains(entries, entry => entry.Kind == RemoteDebugLogKind.Tx && entry.Message == "@KMain playback");
        Assert.Contains(entries, entry => entry.Kind == RemoteDebugLogKind.Tx && entry.Message == "@PA");
        Assert.Contains(entries, entry => entry.Kind == RemoteDebugLogKind.Tx && entry.Message == "@T00:12.3");
    }

    [Fact]
    public async Task ButtonPressAndReleaseLogFramesEventsAndActualAcks()
    {
        var log = new RemoteDebugLogBuffer();
        var sim = new SimulatedRemoteTransport();
        await using var service = new RemoteControllerService(_ => sim, log);
        var ct = TestContext.Current.CancellationToken;
        await service.ConnectAsync("SIM", true, ct);
        await WaitUntilAsync(() => service.ConnectionState == RemoteConnectionState.Connected, ct);
        DrainAll(log);

        sim.ButtonBits = "10000000";
        var pressed = await CollectUntilAsync(log,
            entries => entries.Any(entry => entry.Kind == RemoteDebugLogKind.Event && entry.Message == "Remote START pressed"), ct);
        sim.ButtonBits = "00000000";
        var released = await CollectUntilAsync(log,
            entries => entries.Any(entry => entry.Kind == RemoteDebugLogKind.Event && entry.Message == "Remote START released"), ct);

        Assert.Contains(pressed, entry => entry.Kind == RemoteDebugLogKind.Rx && entry.Message == "@B10000000");
        Assert.Contains(pressed, entry => entry.Kind == RemoteDebugLogKind.Tx && entry.Message == "@A");
        Assert.Contains(released, entry => entry.Kind == RemoteDebugLogKind.Rx && entry.Message == "@B00000000");
        Assert.Contains(released, entry => entry.Kind == RemoteDebugLogKind.Tx && entry.Message == "@A");
    }

    [Fact]
    public async Task ConnectionTransitionsAreLogged()
    {
        var log = new RemoteDebugLogBuffer();
        await using var service = new RemoteControllerService(_ => new SimulatedRemoteTransport(), log);
        var ct = TestContext.Current.CancellationToken;

        await service.ConnectAsync("SIM", true, ct);
        await WaitUntilAsync(() => service.ConnectionState == RemoteConnectionState.Connected, ct);
        var entries = DrainAll(log);

        Assert.Contains(entries, entry => entry.Kind == RemoteDebugLogKind.State && entry.Message == "Disconnected -> Connecting");
        Assert.Contains(entries, entry => entry.Kind == RemoteDebugLogKind.State && entry.Message == "Connecting -> Connected");
    }

    [Fact]
    public async Task PollTimeoutIsAlwaysLoggedAndEndsInDisconnectedState()
    {
        var log = new RemoteDebugLogBuffer();
        var sim = new SimulatedRemoteTransport();
        await using var service = new RemoteControllerService(_ => sim, log);
        var ct = TestContext.Current.CancellationToken;
        await service.ConnectAsync("SIM", true, ct);
        await WaitUntilAsync(() => service.ConnectionState == RemoteConnectionState.Connected, ct);
        DrainAll(log);

        sim.DropResponses = true;
        await WaitUntilAsync(() => service.ConnectionState == RemoteConnectionState.Disconnected, ct);
        var entries = DrainAll(log);

        Assert.Contains(entries, entry => entry.Kind == RemoteDebugLogKind.Warning && entry.Message == "Poll timeout");
        Assert.Contains(entries, entry => entry.Kind == RemoteDebugLogKind.State && entry.Message == "Connected -> Disconnected");
    }

    [Fact]
    public async Task InvalidFrameIsAlwaysLoggedAsError()
    {
        var log = new RemoteDebugLogBuffer();
        var sim = new SimulatedRemoteTransport();
        await using var service = new RemoteControllerService(_ => sim, log);
        var ct = TestContext.Current.CancellationToken;
        await service.ConnectAsync("SIM", true, ct);
        await WaitUntilAsync(() => service.ConnectionState == RemoteConnectionState.Connected, ct);
        DrainAll(log);

        sim.SendMalformedNext = true;
        var entries = await CollectUntilAsync(log,
            current => current.Any(entry => entry.Kind == RemoteDebugLogKind.Error && entry.Message.Contains("Invalid frame", StringComparison.Ordinal)), ct);

        Assert.Contains(entries, entry => entry.Kind == RemoteDebugLogKind.Error && entry.Message == "Invalid frame: bad");
    }

    [Fact]
    public void DebugBufferIsThreadSafeAndBounded()
    {
        var log = new RemoteDebugLogBuffer(50);
        Parallel.For(0, 1000, i => log.Write(RemoteDebugLogKind.Event, i.ToString()));

        Assert.Equal(50, log.Count);
        Assert.Equal(50, log.Drain(1000).Count);
    }

    private static List<RemoteDebugLogEntry> DrainAll(RemoteDebugLogBuffer log)
    {
        var result = new List<RemoteDebugLogEntry>();
        IReadOnlyList<RemoteDebugLogEntry> batch;
        do
        {
            batch = log.Drain(1000);
            result.AddRange(batch);
        } while (batch.Count > 0);
        return result;
    }

    private static async Task<List<RemoteDebugLogEntry>> CollectUntilAsync(
        RemoteDebugLogBuffer log,
        Func<IReadOnlyList<RemoteDebugLogEntry>, bool> predicate,
        CancellationToken cancellationToken)
    {
        var result = new List<RemoteDebugLogEntry>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            result.AddRange(log.Drain(1000));
            if (predicate(result)) return result;
            await Task.Delay(10, cancellationToken);
        }
        Assert.Fail("The expected remote debug entry was not produced within two seconds.");
        return result;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10, cancellationToken);
        Assert.True(condition(), "The expected remote state was not reached within two seconds.");
    }
}
