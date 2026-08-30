using WaveFileWriter = NAudio.Wave.WaveFileWriter;
using WaveFormat = NAudio.Wave.WaveFormat;
using TajpanShowController.Core.Diagnostics;
using TajpanShowController.Core.Interfaces;
using TajpanShowController.Core.Models;
using TajpanShowController.Core.Services;
using TajpanShowController.Infrastructure.Audio;
using TajpanShowController.Infrastructure.Serial;
using Xunit;

namespace TajpanShowController.Tests;

[Collection(WindowsAudioIntegrationCollection.Name)]
public sealed class HardwareIntegrationTests
{
    [Fact]
    public async Task ConfiguredComPortStaysConnectedDuringFirstUsePlaybackTransport()
    {
        var port = Environment.GetEnvironmentVariable("TAJPAN_HARDWARE_COM");
        if (string.IsNullOrWhiteSpace(port))
        {
            Assert.Skip("Set TAJPAN_HARDWARE_COM to run the physical first-use playback test.");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        var testDirectory = Path.Combine(Path.GetTempPath(), "TajpanHardwarePlayback", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        var files = CreateSilentWaveFiles(testDirectory, 2);
        var playlist = files.Select(path => new PlaylistTrack
        {
            FilePath = path,
            Title = Path.GetFileNameWithoutExtension(path),
            Duration = TimeSpan.FromSeconds(2)
        }).ToList();
        PlaylistTrack? selected = playlist[0];
        PlaylistTrack? playing = null;
        var falseDisconnects = 0;
        var wasConnected = false;

        await using var remote = new RemoteControllerService(_ => new SerialPortTransport());
        var playback = new NAudioPlaybackService();
        void SynchronizeDisplay()
        {
            var displayed = CurrentTrackResolver.Resolve(selected, playing, playback.State);
            remote.UpdateDisplay(
                displayed is null ? 0 : playlist.IndexOf(displayed) + 1,
                displayed?.Title ?? string.Empty,
                playback.State,
                playback.Position);
        }
        var controller = new PlaybackTransportController(
            playback,
            playlist,
            () => selected,
            value => { selected = value; SynchronizeDisplay(); },
            () => playing,
            value => { playing = value; SynchronizeDisplay(); });
        playback.StateChanged += (_, _) => SynchronizeDisplay();
        playback.PositionChanged += (_, _) => SynchronizeDisplay();
        remote.StatusChanged += (_, _) =>
        {
            if (remote.ConnectionState == RemoteConnectionState.Connected) wasConnected = true;
            else if (wasConnected) Interlocked.Increment(ref falseDisconnects);
        };

        try
        {
            SynchronizeDisplay();
            await remote.ConnectAsync(port, false, ct);
            await WaitForConnectionAsync(remote, ct);
            var timeoutBaseline = remote.TimingMetrics.Snapshot().TimeoutCount;

            playback.Volume = 0.1f;
            Assert.Equal(0.1f, playback.Volume);
            await controller.PlayAsync(ct);
            Assert.Equal(PlaybackState.Playing, playback.State);
            await Task.Delay(350, ct);
            controller.Pause();
            Assert.Equal(PlaybackState.Paused, playback.State);
            await controller.PlayAsync(ct);
            Assert.Equal(PlaybackState.Playing, playback.State);
            await controller.SeekAsync(TimeSpan.FromSeconds(0.5), ct);
            Assert.True(playback.Position >= TimeSpan.FromMilliseconds(450));
            controller.Stop();
            Assert.Equal(PlaybackState.Stopped, playback.State);

            await controller.NextAsync(TransportCommandSource.Gui, ct);
            Assert.Same(playlist[1], selected);
            Assert.Equal(PlaybackState.Stopped, playback.State);
            await controller.PlayAsync(ct);
            Assert.Equal(PlaybackState.Playing, playback.State);
            await Task.Delay(350, ct);
            controller.Stop();
            await controller.PreviousAsync(TransportCommandSource.Gui, ct);
            Assert.Same(playlist[0], selected);
            await Task.Delay(350, ct);

            var metrics = remote.TimingMetrics.Snapshot();
            Assert.Equal(RemoteConnectionState.Connected, remote.ConnectionState);
            Assert.Equal(0, falseDisconnects);
            Assert.Equal(timeoutBaseline, metrics.TimeoutCount);
            Assert.True(metrics.AveragePollGap > TimeSpan.Zero);
            Assert.True(metrics.MaxValidResponseGap < RemoteControllerService.RemoteDisconnectTimeout);
        }
        finally
        {
            await playback.DisposeAsync();
            if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ConfiguredComPortValidatesFourPhysicalControlsAndCombinedPlayPause()
    {
        var port = Environment.GetEnvironmentVariable("TAJPAN_HARDWARE_COM");
        var enabled = string.Equals(
            Environment.GetEnvironmentVariable("TAJPAN_HARDWARE_BUTTON"), "1", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(port) || !enabled)
        {
            Assert.Skip("Set TAJPAN_HARDWARE_COM and TAJPAN_HARDWARE_BUTTON=1 to run the physical button acceptance test.");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        await using var service = new RemoteControllerService(_ => new SerialPortTransport());
        var stoppedControls = new[] { RemoteButton.Start, RemoteButton.Stop, RemoteButton.Previous, RemoteButton.Next };
        var received = new HashSet<RemoteButton>();
        var stoppedControlsPressed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pausePressed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var playingPhase = false;
        service.ButtonPressed += (_, button) =>
        {
            lock (received)
            {
                received.Add(button);
                if (stoppedControls.All(received.Contains)) stoppedControlsPressed.TrySetResult();
                if (playingPhase && button == RemoteButton.Pause) pausePressed.TrySetResult();
            }
        };
        service.UpdateDisplay(1, "BUTTON TEST", PlaybackState.Stopped, TimeSpan.Zero);
        await service.ConnectAsync(port, false, ct);
        var connectDeadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < connectDeadline && service.ConnectionState != RemoteConnectionState.Connected)
            await Task.Delay(10, ct);
        Assert.Equal(RemoteConnectionState.Connected, service.ConnectionState);

        try
        {
            await stoppedControlsPressed.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
            lock (received) playingPhase = true;
            service.UpdateDisplay(1, "BUTTON TEST", PlaybackState.Playing, TimeSpan.Zero);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            await pausePressed.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
        catch (TimeoutException)
        {
            RemoteButton[] observed;
            lock (received) observed = received.OrderBy(value => value).ToArray();
            Assert.Fail("The four physical controls or the PLAYING-state PAUSE mapping was not observed. Received: " + string.Join(", ", observed));
        }
        var metrics = service.TimingMetrics.Snapshot();
        var reportPath = Environment.GetEnvironmentVariable("TAJPAN_BUTTON_REPORT");
        if (!string.IsNullOrWhiteSpace(reportPath))
            await File.WriteAllTextAsync(reportPath, string.Join(Environment.NewLine,
                "physical_controls=PlayPause,Stop,Previous,Next",
                $"protocol_edges={string.Join(',', received.OrderBy(value => value))}",
                $"button_dispatch_avg_ms={metrics.AverageButtonDispatchLatency.TotalMilliseconds:F3}",
                $"button_dispatch_max_ms={metrics.MaxButtonDispatchLatency.TotalMilliseconds:F3}"), ct);
        lock (received) Assert.Equal(Enum.GetValues<RemoteButton>().OrderBy(value => value), received.OrderBy(value => value));
        Assert.True(metrics.MaxButtonDispatchLatency > TimeSpan.Zero);
    }

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
        Console.WriteLine($"{port} display stress: max poll gap={metrics.MaxPollGap.TotalMilliseconds:F2} ms, " +
            $"max valid RX gap={metrics.MaxValidResponseGap.TotalMilliseconds:F2} ms, " +
            $"poll RTT avg/max={metrics.AveragePollRtt.TotalMilliseconds:F2}/{metrics.MaxPollRtt.TotalMilliseconds:F2} ms, " +
            $"false disconnects={falseDisconnects}");
        var reportPath = Environment.GetEnvironmentVariable("TAJPAN_HARDWARE_REPORT");
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            var lastRxAge = service.TimeSinceLastValidResponse ?? TimeSpan.Zero;
            var report = string.Join(Environment.NewLine,
                $"port={port}",
                $"connection_state={service.ConnectionState}",
                $"cycles={cycles}",
                $"false_disconnects={falseDisconnects}",
                $"poll_count={metrics.PollCount}",
                $"timeout_count={metrics.TimeoutCount}",
                $"poll_hz={(metrics.AveragePollGap > TimeSpan.Zero ? 1 / metrics.AveragePollGap.TotalSeconds : 0):F3}",
                $"poll_interval_avg_ms={metrics.AveragePollGap.TotalMilliseconds:F3}",
                $"poll_interval_max_ms={metrics.MaxPollGap.TotalMilliseconds:F3}",
                $"poll_rtt_avg_ms={metrics.AveragePollRtt.TotalMilliseconds:F3}",
                $"poll_rtt_median_ms={metrics.MedianPollRtt.TotalMilliseconds:F3}",
                $"poll_rtt_p95_ms={metrics.P95PollRtt.TotalMilliseconds:F3}",
                $"poll_rtt_max_ms={metrics.MaxPollRtt.TotalMilliseconds:F3}",
                $"valid_rx_gap_avg_ms={metrics.AverageValidResponseGap.TotalMilliseconds:F3}",
                $"valid_rx_gap_max_ms={metrics.MaxValidResponseGap.TotalMilliseconds:F3}",
                $"last_rx_age_ms={lastRxAge.TotalMilliseconds:F3}",
                $"display_transaction_count={metrics.DisplayTransactionCount}",
                $"display_rtt_avg_ms={metrics.AverageDisplayRtt.TotalMilliseconds:F3}",
                $"display_rtt_max_ms={metrics.MaxDisplayRtt.TotalMilliseconds:F3}",
                $"display_snapshot_settling_max_ms={metrics.MaxDisplaySnapshotSettling.TotalMilliseconds:F3}",
                $"button_dispatch_avg_ms={metrics.AverageButtonDispatchLatency.TotalMilliseconds:F3}",
                $"button_dispatch_max_ms={metrics.MaxButtonDispatchLatency.TotalMilliseconds:F3}");
            await File.WriteAllTextAsync(reportPath, report, ct);
        }
        Assert.True(falseDisconnects == 0,
            $"false disconnects={falseDisconnects}, max poll gap={metrics.MaxPollGap.TotalMilliseconds:F2} ms, " +
            $"max valid RX gap={metrics.MaxValidResponseGap.TotalMilliseconds:F2} ms, state={service.ConnectionState}, " +
            $"disconnect age={disconnectAge.TotalMilliseconds:F2} ms, " +
            $"diagnostics={diagnostics}, tx={txTrace}");
        Assert.Equal(RemoteConnectionState.Connected, service.ConnectionState);
        Assert.True(metrics.MaxPollGap < TimeSpan.FromMilliseconds(50));
        Assert.True(metrics.MaxValidResponseGap < TimeSpan.FromMilliseconds(50));
    }

    private static async Task WaitForConnectionAsync(RemoteControllerService service, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline && service.ConnectionState != RemoteConnectionState.Connected)
            await Task.Delay(10, cancellationToken);
        Assert.Equal(RemoteConnectionState.Connected, service.ConnectionState);
    }

    private static List<string> CreateSilentWaveFiles(string directory, int count)
    {
        var result = new List<string>(count);
        var format = new WaveFormat(8_000, 16, 1);
        for (var index = 0; index < count; index++)
        {
            var path = Path.Combine(directory, $"hardware-first-use-{index:00}.wav");
            using var writer = new WaveFileWriter(path, format);
            writer.Write(new byte[format.AverageBytesPerSecond * 2]);
            result.Add(path);
        }
        return result;
    }
}
