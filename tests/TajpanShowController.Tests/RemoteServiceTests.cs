using TajpanShowController.Core.Diagnostics;
using TajpanShowController.Core.Interfaces;
using TajpanShowController.Core.Models;
using TajpanShowController.Core.Protocol;
using TajpanShowController.Core.Services;
using TajpanShowController.Infrastructure.Serial;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Xunit;

namespace TajpanShowController.Tests;

public sealed class RemoteServiceTests
{
    [Fact]
    public async Task FiftyBlockingFirstUseCommandsAndDebugFloodDoNotInterruptPolling()
    {
        var transport = new AlternatingButtonTransport();
        var log = new RemoteDebugLogBuffer();
        await using var service = new RemoteControllerService(_ => transport, log);
        var handled = 0;
        var falseDisconnects = 0;
        var wasConnected = false;
        service.StatusChanged += (_, _) =>
        {
            if (service.ConnectionState == RemoteConnectionState.Connected) wasConnected = true;
            else if (wasConnected) Interlocked.Increment(ref falseDisconnects);
        };
        service.ButtonPressed += (_, button) =>
        {
            if (button != RemoteButton.Start) return;
            Thread.Sleep(20); // Represents a slow first-use file/decoder/audio initialization.
            Interlocked.Increment(ref handled);
        };

        var ct = TestContext.Current.CancellationToken;
        await service.ConnectAsync("SIM", true, ct);
        var debugFlood = Task.Run(() =>
        {
            for (var i = 0; i < 50_000; i++)
            {
                log.Write(RemoteDebugLogKind.Event, "stress " + i);
                if ((i & 127) == 0) log.Drain(250);
            }
        }, ct);

        await WaitUntilAsync(() => Volatile.Read(ref handled) >= 50, ct);
        await debugFlood;
        var timing = service.TimingMetrics.Snapshot();

        Assert.Equal(RemoteConnectionState.Connected, service.ConnectionState);
        Assert.Equal(0, Volatile.Read(ref falseDisconnects));
        Assert.Equal(0, timing.TimeoutCount);
        Assert.True(timing.PollCount >= 100);
        Assert.True(transport.AckCount >= 100);
        Console.WriteLine($"50 first-use commands: polls={timing.PollCount}, avg RTT={timing.AveragePollRtt.TotalMilliseconds:F3} ms, max RTT={timing.MaxPollRtt.TotalMilliseconds:F3} ms, RX->parse max={timing.MaxReceiveToParse.TotalMilliseconds:F3} ms, parse->ACK max={timing.MaxParseToAck.TotalMilliseconds:F3} ms, false disconnects={falseDisconnects}");
    }

    [Fact] public async Task SimulatorUsesSameProtocolAndEdgeDetection()
    {
        var sim = new SimulatedRemoteTransport(); await using var service = new RemoteControllerService(_ => sim);
        var count = 0; service.ButtonPressed += (_, b) => { if (b == RemoteButton.Start) count++; };
        var ct = TestContext.Current.CancellationToken; await service.ConnectAsync("SIM", true, ct); await WaitUntilAsync(() => service.ConnectionState == RemoteConnectionState.Connected, ct);
        sim.ButtonBits = "10000000"; await Task.Delay(35, ct); await service.DisconnectAsync(ct); Assert.Equal(1, count);
    }

    [Fact]
    public async Task FirstButtonFrameAfterConnectIsBaselineAndDoesNotRaiseFalseEdge()
    {
        var sim = new SimulatedRemoteTransport { ButtonBits = "10000000" };
        await using var service = new RemoteControllerService(_ => sim);
        var count = 0;
        service.ButtonPressed += (_, _) => count++;

        var ct = TestContext.Current.CancellationToken;
        await service.ConnectAsync("SIM", true, ct);
        await WaitUntilAsync(() => service.ConnectionState == RemoteConnectionState.Connected, ct);
        await Task.Delay(25, ct);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task ReconnectResendsCompleteDisplaySnapshotThroughExistingCoalescing()
    {
        var first = new SimulatedRemoteTransport();
        var second = new SimulatedRemoteTransport();
        var transports = new Queue<SimulatedRemoteTransport>([first, second]);
        await using var service = new RemoteControllerService(_ => transports.Dequeue());
        service.UpdateDisplay(7, "Reconnect track", PlaybackState.Playing, TimeSpan.FromSeconds(42.3));
        var ct = TestContext.Current.CancellationToken;

        await service.ConnectAsync("SIM", true, ct);
        await Task.Delay(150, ct);
        await service.DisconnectAsync(ct);
        await service.ConnectAsync("SIM", true, ct);
        await Task.Delay(150, ct);

        Assert.Contains("@N7", second.Writes);
        Assert.Contains("@KReconnect track", second.Writes);
        Assert.Contains("@PP", second.Writes);
        Assert.Contains("@T00:42.3", second.Writes);
    }

    [Fact]
    public async Task SimulatedConnectionLossAutoReconnectsThroughProductionCoordinator()
    {
        var first = new SimulatedRemoteTransport();
        var second = new SimulatedRemoteTransport();
        var transports = new Queue<SimulatedRemoteTransport>([first, second]);
        await using var service = new RemoteControllerService(_ => transports.Dequeue());
        await using var coordinator = new RemoteConnectionCoordinator(
            service,
            () => new RemoteConnectionOptions(null, true),
            () => service.UpdateDisplay(3, "Simulation reconnect", PlaybackState.Paused, TimeSpan.FromSeconds(9.4)),
            reconnectInterval: TimeSpan.FromMilliseconds(20),
            connectAttemptTimeout: TimeSpan.FromMilliseconds(250));
        coordinator.SetAutoReconnect(true);
        var ct = TestContext.Current.CancellationToken;
        Assert.True(await coordinator.StartAsync(autoConnect: true, ct));

        first.DropResponses = true;
        await WaitUntilAsync(() => transports.Count == 0 && service.ConnectionState == RemoteConnectionState.Connected, ct);
        await WaitUntilAsync(() => second.Writes.Contains("@N3") && second.Writes.Contains("@KSimulation reconnect") && second.Writes.Contains("@PA"), ct);

        Assert.Contains("@N3", second.Writes);
        Assert.Contains("@KSimulation reconnect", second.Writes);
        Assert.Contains("@PA", second.Writes);
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
    public async Task PhysicalUsbLatencyWithinResponseBudgetCompletesHandshake()
    {
        var transport = new DelayedReadTransport(
            TimeSpan.FromMilliseconds(30),
            TimeSpan.FromMilliseconds(30));
        await using var service = new RemoteControllerService(_ => transport);
        var ct = TestContext.Current.CancellationToken;

        await service.ConnectAsync("COM12", false, ct);
        await WaitUntilAsync(() => service.ConnectionState == RemoteConnectionState.Connected, ct);

        Assert.Equal(RemoteConnectionState.Connected, service.ConnectionState);
        Assert.Equal("@B00000000", service.LastResponse);
    }

    [Fact]
    public async Task PhysicalDisplayLatencyWithinResponseBudgetMaintainsConnection()
    {
        var transport = new DelayedReadTransport(
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(10));
        await using var service = new RemoteControllerService(_ => transport);
        service.UpdateDisplay(1, "Hardware test", PlaybackState.Playing, TimeSpan.FromSeconds(12.3));
        var ct = TestContext.Current.CancellationToken;

        await service.ConnectAsync("COM12", false, ct);
        await WaitUntilAsync(() => service.ConnectionState == RemoteConnectionState.Connected, ct);
        await Task.Delay(TimeSpan.FromSeconds(1), ct);

        Assert.Equal(RemoteConnectionState.Connected, service.ConnectionState);
        Assert.Contains("@N1", transport.Writes);
        Assert.Contains("@KHardware test", transport.Writes);
        Assert.Contains("@PP", transport.Writes);
        Assert.Contains("@T00:12.3", transport.Writes);
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

        Assert.True(sim.Writes.Count(frame => frame == "@S") >= idleSeconds * 40);
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
    public async Task CloseFailureStillDisposesOldTransportAndAllowsReconnect()
    {
        var log = new RemoteDebugLogBuffer();
        var failing = new CloseFailingTransport();
        var recovered = new SimulatedRemoteTransport();
        var transports = new Queue<ISerialTransport>([failing, recovered]);
        await using var service = new RemoteControllerService(_ => transports.Dequeue(), log);
        var ct = TestContext.Current.CancellationToken;

        await service.ConnectAsync("SIM", true, ct);
        await WaitUntilAsync(() => service.ConnectionState == RemoteConnectionState.Connected, ct);
        await service.DisconnectAsync(ct);

        Assert.True(failing.WasDisposed);
        Assert.Equal(RemoteConnectionState.Disconnected, service.ConnectionState);
        Assert.Contains(DrainAll(log), entry => entry.Kind == RemoteDebugLogKind.Error && entry.Message.Contains("close failed", StringComparison.Ordinal));

        await service.ConnectAsync("SIM", true, ct);
        await WaitUntilAsync(() => service.ConnectionState == RemoteConnectionState.Connected, ct);
        Assert.Equal(RemoteConnectionState.Connected, service.ConnectionState);
    }

    [Fact]
    public async Task TimedOutOldWorkerCannotCorruptNewConnectionSession()
    {
        var stale = new StubbornReadTransport();
        var current = new SimulatedRemoteTransport();
        var transports = new Queue<ISerialTransport>([stale, current]);
        await using var service = new RemoteControllerService(_ => transports.Dequeue());
        var buttonEvents = 0;
        service.ButtonPressed += (_, _) => Interlocked.Increment(ref buttonEvents);
        var ct = TestContext.Current.CancellationToken;

        await service.ConnectAsync("SIM", true, ct);
        await service.DisconnectAsync(ct); // Exercises the worker-shutdown timeout path.
        await service.ConnectAsync("SIM", true, ct);
        await WaitUntilAsync(() => service.ConnectionState == RemoteConnectionState.Connected, ct);

        stale.Release("@B10000000\r\n");
        await Task.Delay(50, ct);

        Assert.Equal(RemoteConnectionState.Connected, service.ConnectionState);
        Assert.Equal(0, buttonEvents);
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
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10, cancellationToken);
        Assert.True(condition(), "The expected remote state was not reached within three seconds.");
    }

    private sealed class CloseFailingTransport : ISerialTransport
    {
        private readonly SimulatedRemoteTransport _inner = new();
        public bool WasDisposed { get; private set; }
        public bool IsOpen => _inner.IsOpen;
        public Task OpenAsync(string portName, CancellationToken cancellationToken) => _inner.OpenAsync(portName, cancellationToken);
        public Task CloseAsync(CancellationToken cancellationToken) => throw new IOException("Injected close failure");
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) => _inner.WriteAsync(data, cancellationToken);
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => _inner.ReadAsync(buffer, cancellationToken);
        public async ValueTask DisposeAsync() { WasDisposed = true; await _inner.DisposeAsync(); }
    }

    private sealed class DelayedReadTransport(TimeSpan pollReadDelay, TimeSpan displayReadDelay) : ISerialTransport
    {
        private readonly SimulatedRemoteTransport _inner = new();
        private TimeSpan _nextReadDelay;
        public bool IsOpen => _inner.IsOpen;
        public IEnumerable<string> Writes => _inner.Writes;
        public Task OpenAsync(string portName, CancellationToken cancellationToken) => _inner.OpenAsync(portName, cancellationToken);
        public Task CloseAsync(CancellationToken cancellationToken) => _inner.CloseAsync(cancellationToken);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            var frame = System.Text.Encoding.ASCII.GetString(data.Span).TrimEnd('\r', '\n');
            if (frame == "@S") _nextReadDelay = pollReadDelay;
            else if (frame.StartsWith("@T") || frame.StartsWith("@N") || frame.StartsWith("@K") || frame.StartsWith("@P"))
                _nextReadDelay = displayReadDelay;
            return _inner.WriteAsync(data, cancellationToken);
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Delay(_nextReadDelay, cancellationToken);
            return await _inner.ReadAsync(buffer, cancellationToken);
        }
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class StubbornReadTransport : ISerialTransport
    {
        private readonly TaskCompletionSource<byte[]> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IsOpen { get; private set; }
        public Task OpenAsync(string portName, CancellationToken cancellationToken) { IsOpen = true; return Task.CompletedTask; }
        public Task CloseAsync(CancellationToken cancellationToken) { IsOpen = false; return Task.CompletedTask; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var response = await _read.Task; // Deliberately ignores cancellation to emulate a stuck serial driver read.
            response.CopyTo(buffer);
            return response.Length;
        }
        public void Release(string response) => _read.TrySetResult(System.Text.Encoding.ASCII.GetBytes(response));
        public ValueTask DisposeAsync() { IsOpen = false; return ValueTask.CompletedTask; }
    }

    private sealed class AlternatingButtonTransport : ISerialTransport
    {
        private readonly Channel<byte> _responses = Channel.CreateUnbounded<byte>();
        private int _polls;
        public bool IsOpen { get; private set; }
        public int AckCount { get; private set; }
        public Task OpenAsync(string portName, CancellationToken cancellationToken) { IsOpen = true; return Task.CompletedTask; }
        public Task CloseAsync(CancellationToken cancellationToken) { IsOpen = false; return Task.CompletedTask; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            var frame = System.Text.Encoding.ASCII.GetString(data.Span).TrimEnd('\r', '\n');
            if (frame == "@S")
            {
                var pressed = Interlocked.Increment(ref _polls) % 2 == 0;
                foreach (var value in ProtocolCodec.Bytes(pressed ? "@B10000000" : "@B00000000"))
                    _responses.Writer.TryWrite(value);
            }
            else if (frame == "@A") AckCount++;
            return ValueTask.CompletedTask;
        }
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var first = await _responses.Reader.ReadAsync(cancellationToken);
            buffer.Span[0] = first;
            var count = 1;
            while (count < buffer.Length && _responses.Reader.TryRead(out var value)) buffer.Span[count++] = value;
            return count;
        }
        public ValueTask DisposeAsync() { IsOpen = false; _responses.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
