using System.Collections.Concurrent;
using System.Diagnostics;
using NAudio;
using NAudio.Wave;
using TajpanShowController.Core.Models;
using TajpanShowController.Core.Services;
using TajpanShowController.Infrastructure.Audio;
using TajpanShowController.Infrastructure.Serial;
using Xunit;
using AppPlaybackState = TajpanShowController.Core.Models.PlaybackState;
using RemoteConnectionState = TajpanShowController.Core.Interfaces.RemoteConnectionState;

namespace TajpanShowController.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WindowsAudioIntegrationCollection
{
    public const string Name = "Windows audio integration";
}

[Collection(WindowsAudioIntegrationCollection.Name)]
public sealed class FirstUsePlaybackRemoteStressTests
{
    [Fact]
    public async Task FiftyUniqueFirstStartsAndTheirRepeatsKeepRemoteConnected()
    {
        const int fileCount = 50;
        var testDirectory = Path.Combine(Path.GetTempPath(), "TajpanFirstUseStress", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        var files = CreateSilentWaveFiles(testDirectory, fileCount);
        var timings = new ConcurrentQueue<PlaybackLoadTiming>();
        var transport = new SimulatedRemoteTransport();
        await using var remote = new RemoteControllerService(_ => transport);
        var playback = new NAudioPlaybackService();
        playback.LoadMeasured += (_, timing) => timings.Enqueue(timing);
        var handled = 0;
        var falseDisconnects = 0;
        var wasConnected = false;
        Exception? playbackFailure = null;
        remote.StatusChanged += (_, _) =>
        {
            if (remote.ConnectionState == RemoteConnectionState.Connected) wasConnected = true;
            else if (wasConnected) Interlocked.Increment(ref falseDisconnects);
        };
        remote.ButtonPressed += (_, button) =>
        {
            if (button != RemoteButton.Start) return;
            var index = Volatile.Read(ref handled);
            if (index >= fileCount * 2) return;
            try
            {
                playback.LoadAsync(files[index % files.Count], AppPlaybackState.Playing).GetAwaiter().GetResult();
                playback.Stop();
            }
            catch (Exception ex) { playbackFailure = ex; }
            finally { Interlocked.Increment(ref handled); }
        };

        try
        {
            var ct = TestContext.Current.CancellationToken;
            await remote.ConnectAsync("SIM", true, ct);
            await WaitUntilAsync(() => remote.ConnectionState == RemoteConnectionState.Connected, TimeSpan.FromSeconds(2), ct);

            for (var start = 1; start <= fileCount * 2; start++)
            {
                transport.ButtonBits = "10000000";
                await WaitUntilAsync(() => Volatile.Read(ref handled) >= start, TimeSpan.FromSeconds(5), ct);
                var pollsBeforeRelease = transport.Writes.Count(frame => frame == "@S");
                transport.ButtonBits = "00000000";
                await WaitUntilAsync(
                    () => transport.Writes.Count(frame => frame == "@S") >= pollsBeforeRelease + 2,
                    TimeSpan.FromSeconds(2),
                    ct);
                if (playbackFailure is not null) break;
            }

            if (playbackFailure is MmException or InvalidOperationException)
            {
                Assert.Skip("No usable Windows audio output is available for the NAudio first-use stress test: " + playbackFailure.Message);
                return;
            }
            Assert.Null(playbackFailure);
            Assert.Equal(fileCount * 2, handled);
            Assert.Equal(fileCount * 2, timings.Count);
            Assert.Equal(0, falseDisconnects);
            Assert.Equal(RemoteConnectionState.Connected, remote.ConnectionState);

            var all = timings.ToArray();
            var first = all.Take(fileCount).ToArray();
            var repeat = all.Skip(fileCount).Take(fileCount).ToArray();
            Assert.All(first, timing => Assert.True(timing.FirstLoadInSession));
            Assert.All(repeat, timing => Assert.False(timing.FirstLoadInSession));
            var communication = remote.TimingMetrics.Snapshot();
            Assert.Equal(0, communication.TimeoutCount);
            var report = string.Join(Environment.NewLine,
                Measurement("First start file probe", first, x => x.FileProbe),
                Measurement("First start reader creation", first, x => x.ReaderCreation),
                Measurement("First start duration read", first, x => x.DurationRead),
                Measurement("First start output initialization", first, x => x.OutputInitialization),
                Measurement("First start audio total", first, x => x.Total),
                Measurement("Repeat start file probe", repeat, x => x.FileProbe),
                Measurement("Repeat start reader creation", repeat, x => x.ReaderCreation),
                Measurement("Repeat start duration read", repeat, x => x.DurationRead),
                Measurement("Repeat start output initialization", repeat, x => x.OutputInitialization),
                Measurement("Repeat start audio total", repeat, x => x.Total),
                $"Poll RTT avg/max: {communication.AveragePollRtt.TotalMilliseconds:F3}/{communication.MaxPollRtt.TotalMilliseconds:F3} ms",
                $"RX -> parsed max: {communication.MaxReceiveToParse.TotalMilliseconds:F3} ms",
                $"Parsed -> ACK max: {communication.MaxParseToAck.TotalMilliseconds:F3} ms",
                $"Poll schedule delay max: {communication.MaxScheduleDelay.TotalMilliseconds:F3} ms",
                $"Timeouts/false disconnects: {communication.TimeoutCount}/{falseDisconnects}");
            Console.WriteLine(report);
        }
        finally
        {
            await playback.DisposeAsync();
            if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static string Measurement(
        string label,
        IReadOnlyCollection<PlaybackLoadTiming> samples,
        Func<PlaybackLoadTiming, TimeSpan> select)
        => $"{label} avg/max: {samples.Average(x => select(x).TotalMilliseconds):F2}/{samples.Max(x => select(x).TotalMilliseconds):F2} ms";

    private static List<string> CreateSilentWaveFiles(string directory, int count)
    {
        var result = new List<string>(count);
        var format = new WaveFormat(8_000, 16, 1);
        for (var index = 0; index < count; index++)
        {
            var path = Path.Combine(directory, $"first-use-{index:00}.wav");
            using var writer = new WaveFileWriter(path, format);
            writer.Write(new byte[format.AverageBytesPerSecond * 2]);
            result.Add(path);
        }
        return result;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition() && deadline.Elapsed < timeout) await Task.Delay(5, cancellationToken);
        Assert.True(condition(), "The expected first-use stress condition did not occur before the deadline.");
    }
}
