using TajpanShowController.Core.Interfaces;
using TajpanShowController.Core.Models;
using TajpanShowController.Core.Services;
using Xunit;

namespace TajpanShowController.Tests;

public sealed class PlaybackTransportControllerTests
{
    [Fact] public async Task StoppedPlayStartsSelectedFromBeginning() { var f = Fixture.Create(PlaybackState.Stopped, 1); await f.Controller.PlayAsync(TestContext.Current.CancellationToken); Assert.Equal(PlaybackState.Playing, f.Playback.State); Assert.Same(f.Tracks[1], f.Playing); Assert.Equal(TimeSpan.Zero, f.Playback.Position); }
    [Fact] public async Task PausedPlayResumesAtExistingPosition() { var f = Fixture.Create(PlaybackState.Paused, 1, TimeSpan.FromSeconds(17)); await f.Controller.PlayAsync(TestContext.Current.CancellationToken); Assert.Equal(PlaybackState.Playing, f.Playback.State); Assert.Equal(TimeSpan.FromSeconds(17), f.Playback.Position); Assert.Equal(1, f.Playback.ResumeCalls); Assert.Equal(0, f.Playback.LoadCalls); }
    [Fact] public async Task PlayingPlayDoesNotRestartOrReload() { var f = Fixture.Create(PlaybackState.Playing, 1, TimeSpan.FromSeconds(9)); await f.Controller.PlayAsync(TestContext.Current.CancellationToken); Assert.Equal(TimeSpan.FromSeconds(9), f.Playback.Position); Assert.Equal(0, f.Playback.LoadCalls); Assert.Equal(0, f.Playback.RestartCalls); }
    [Fact] public void PlayingPausePauses() { var f = Fixture.Create(PlaybackState.Playing, 1, TimeSpan.FromSeconds(8)); f.Controller.Pause(); Assert.Equal(PlaybackState.Paused, f.Playback.State); Assert.Equal(TimeSpan.FromSeconds(8), f.Playback.Position); }
    [Fact] public void PausedStopStopsAndResetsPosition() { var f = Fixture.Create(PlaybackState.Paused, 1, TimeSpan.FromSeconds(8)); f.Controller.Stop(); Assert.Equal(PlaybackState.Stopped, f.Playback.State); Assert.Equal(TimeSpan.Zero, f.Playback.Position); Assert.Equal(1, f.SelectedIndex); }
    [Fact] public async Task PlayingSeekChangesPositionAndRemainsPlaying() { var f = Fixture.Create(PlaybackState.Playing, 1); await f.Controller.SeekAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken); Assert.Equal(TimeSpan.FromSeconds(90), f.Playback.Position); Assert.Equal(PlaybackState.Playing, f.Playback.State); Assert.Same(f.Tracks[1], f.Playing); }
    [Fact] public async Task PausedSeekChangesPositionAndRemainsPaused() { var f = Fixture.Create(PlaybackState.Paused, 1); await f.Controller.SeekAsync(TimeSpan.FromSeconds(45.67), TestContext.Current.CancellationToken); Assert.Equal(TimeSpan.FromSeconds(45.67), f.Playback.Position); Assert.Equal("00:45.6", PlaybackTimeFormatter.Format(f.Playback.Position)); Assert.Equal(PlaybackState.Paused, f.Playback.State); }
    [Fact] public async Task StoppedSeekPreloadsTrackWithoutStartingIt() { var f = Fixture.Create(PlaybackState.Stopped, 1); await f.Controller.SeekAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken); Assert.Equal(TimeSpan.FromSeconds(60), f.Playback.Position); Assert.Equal(PlaybackState.Stopped, f.Playback.State); Assert.Equal(1, f.Playback.LoadCalls); Assert.Same(f.Tracks[1], f.Playing); }
    [Fact] public async Task StoppedSeekThenPlayContinuesFromSeekPosition() { var f = Fixture.Create(PlaybackState.Stopped, 1); await f.Controller.SeekAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken); await f.Controller.PlayAsync(TestContext.Current.CancellationToken); Assert.Equal(TimeSpan.FromSeconds(60), f.Playback.Position); Assert.Equal(PlaybackState.Playing, f.Playback.State); Assert.Equal(1, f.Playback.LoadCalls); }
    [Fact] public async Task StopAfterSeekResetsPosition() { var f = Fixture.Create(PlaybackState.Playing, 1); await f.Controller.SeekAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken); f.Controller.Stop(); Assert.Equal(TimeSpan.Zero, f.Playback.Position); Assert.Equal("00:00.0", PlaybackTimeFormatter.Format(f.Playback.Position)); Assert.Equal(PlaybackState.Stopped, f.Playback.State); }
    [Fact] public async Task SeekToExactEndDoesNotSynthesizeCompletionOrChangeState() { var f = Fixture.Create(PlaybackState.Paused, 1); var completions = 0; f.Playback.PlaybackCompleted += (_, _) => completions++; await f.Controller.SeekAsync(f.Playback.Duration, TestContext.Current.CancellationToken); Assert.Equal(f.Playback.Duration, f.Playback.Position); Assert.Equal(PlaybackState.Paused, f.Playback.State); Assert.Equal(0, completions); }

    [Fact] public async Task PlayingGuiNextSelectsAndStartsNext() { var f = Fixture.Create(PlaybackState.Playing, 1); await f.Controller.NextAsync(TransportCommandSource.Gui, TestContext.Current.CancellationToken); Assert.Equal(2, f.SelectedIndex); Assert.Same(f.Tracks[2], f.Playing); Assert.Equal(PlaybackState.Playing, f.Playback.State); Assert.Equal(TimeSpan.Zero, f.Playback.Position); Assert.Equal("00:00.0", PlaybackTimeFormatter.Format(f.Playback.Position)); }
    [Fact] public async Task PlayingGuiNextNavigatesFromPlayingTrackNotUnrelatedSelection() { var f = Fixture.Create(PlaybackState.Playing, 1); f.Selected = f.Tracks[0]; await f.Controller.NextAsync(TransportCommandSource.Gui, TestContext.Current.CancellationToken); Assert.Equal(2, f.SelectedIndex); Assert.Same(f.Tracks[2], f.Playing); }
    [Fact] public async Task PausedGuiNextSelectsWithoutPlaying() { var f = Fixture.Create(PlaybackState.Paused, 1); await f.Controller.NextAsync(TransportCommandSource.Gui, TestContext.Current.CancellationToken); Assert.Equal(2, f.SelectedIndex); Assert.Equal(PlaybackState.Paused, f.Playback.State); Assert.Equal(TimeSpan.Zero, f.Playback.Position); }
    [Fact] public async Task StoppedGuiNextSelectsWithoutPlaying() { var f = Fixture.Create(PlaybackState.Stopped, 1); await f.Controller.NextAsync(TransportCommandSource.Gui, TestContext.Current.CancellationToken); Assert.Equal(2, f.SelectedIndex); Assert.Equal(PlaybackState.Stopped, f.Playback.State); Assert.Equal(0, f.Playback.LoadCalls); }
    [Theory] [InlineData(false)] [InlineData(true)] public async Task PlayingRemoteTrackChangeIsIgnored(bool previous) { var f = Fixture.Create(PlaybackState.Playing, 1, TimeSpan.FromSeconds(8)); if (previous) await f.Controller.PreviousAsync(TransportCommandSource.Remote, TestContext.Current.CancellationToken); else await f.Controller.NextAsync(TransportCommandSource.Remote, TestContext.Current.CancellationToken); Assert.Equal(1, f.SelectedIndex); Assert.Same(f.Tracks[1], f.Playing); Assert.Equal(TimeSpan.FromSeconds(8), f.Playback.Position); Assert.Equal(0, f.Playback.LoadCalls); Assert.Equal(0, f.Playback.RestartCalls); }
    [Fact] public async Task PausedRemoteNextChangesTrackAndRemainsPaused() { var f = Fixture.Create(PlaybackState.Paused, 1); await f.Controller.NextAsync(TransportCommandSource.Remote, TestContext.Current.CancellationToken); Assert.Equal(2, f.SelectedIndex); Assert.Equal(PlaybackState.Paused, f.Playback.State); }
    [Fact] public async Task StoppedRemotePreviousChangesTrackAndRemainsStopped() { var f = Fixture.Create(PlaybackState.Stopped, 1); await f.Controller.PreviousAsync(TransportCommandSource.Remote, TestContext.Current.CancellationToken); Assert.Equal(0, f.SelectedIndex); Assert.Equal(PlaybackState.Stopped, f.Playback.State); }
    [Fact] public void NaturalEndStopsWithoutSelectingNext() { var f = Fixture.Create(PlaybackState.Playing, 1); f.Playback.CompleteNaturally(); f.Controller.PlaybackCompleted(); Assert.Equal(PlaybackState.Stopped, f.Playback.State); Assert.Equal(1, f.SelectedIndex); Assert.Same(f.Tracks[1], f.Playing); Assert.Equal(0, f.Playback.LoadCalls); }
    [Fact] public async Task PreviousAfterThresholdRestartsCurrent() { var f = Fixture.Create(PlaybackState.Playing, 1, TimeSpan.FromSeconds(4)); await f.Controller.PreviousAsync(TransportCommandSource.Gui, TestContext.Current.CancellationToken); Assert.Equal(1, f.SelectedIndex); Assert.Equal(1, f.Playback.RestartCalls); Assert.Equal(TimeSpan.Zero, f.Playback.Position); Assert.Equal(PlaybackState.Playing, f.Playback.State); }
    [Fact] public async Task PreviousAtThresholdStartsPrevious() { var f = Fixture.Create(PlaybackState.Playing, 1, PlaybackTransportController.PreviousRestartThreshold); await f.Controller.PreviousAsync(TransportCommandSource.Gui, TestContext.Current.CancellationToken); Assert.Equal(0, f.SelectedIndex); Assert.Same(f.Tracks[0], f.Playing); Assert.Equal(PlaybackState.Playing, f.Playback.State); }
    [Fact] public async Task PreviousOnFirstTrackDoesNotUnderflow() { var f = Fixture.Create(PlaybackState.Stopped, 0); await f.Controller.PreviousAsync(TransportCommandSource.Gui, TestContext.Current.CancellationToken); Assert.Equal(0, f.SelectedIndex); }
    [Fact] public async Task NextOnLastTrackDoesNotOverflow() { var f = Fixture.Create(PlaybackState.Stopped, 2); await f.Controller.NextAsync(TransportCommandSource.Gui, TestContext.Current.CancellationToken); Assert.Equal(2, f.SelectedIndex); }

    private sealed class Fixture
    {
        public required FakePlaybackService Playback { get; init; }
        public required List<PlaylistTrack> Tracks { get; init; }
        public required PlaybackTransportController Controller { get; set; }
        public PlaylistTrack? Selected { get; set; }
        public PlaylistTrack? Playing { get; set; }
        public int SelectedIndex => Selected is null ? -1 : Tracks.IndexOf(Selected);

        public static Fixture Create(PlaybackState state, int selectedIndex, TimeSpan? position = null)
        {
            var playback = new FakePlaybackService(state, position ?? TimeSpan.Zero);
            var fixture = new Fixture { Playback = playback, Tracks = [Track("one"), Track("two"), Track("three")], Controller = null! };
            fixture.Selected = fixture.Tracks[selectedIndex];
            fixture.Playing = fixture.Selected;
            if (state != PlaybackState.Stopped) playback.SetLoadedFile(fixture.Selected.FilePath);
            fixture.Controller = new PlaybackTransportController(playback, fixture.Tracks, () => fixture.Selected, track => fixture.Selected = track, () => fixture.Playing, track => fixture.Playing = track);
            return fixture;
        }

        private static PlaylistTrack Track(string name) => new() { FilePath = name + ".wav", Title = name };
    }

    private sealed class FakePlaybackService(PlaybackState initialState, TimeSpan initialPosition) : IPlaybackService
    {
        public PlaybackState State { get; private set; } = initialState;
        public string? LoadedFilePath { get; private set; }
        public TimeSpan Position { get; private set; } = initialPosition;
        public TimeSpan Duration => TimeSpan.FromMinutes(3);
        public float Volume { get; set; }
        public int OutputDeviceNumber { get; set; }
        public int LoadCalls { get; private set; }
        public int ResumeCalls { get; private set; }
        public int RestartCalls { get; private set; }
        public int SeekCalls { get; private set; }
        public event EventHandler? StateChanged;
        public event EventHandler? PositionChanged;
        public event EventHandler? PlaybackCompleted;
        public event EventHandler<Exception>? PlaybackFailed { add { } remove { } }
        public event EventHandler<PlaybackLoadTiming>? LoadMeasured { add { } remove { } }
        public IReadOnlyList<AudioOutputDevice> GetOutputDevices() => [];
        public Task LoadAsync(string filePath, PlaybackState initialState = PlaybackState.Stopped, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); LoadCalls++; LoadedFilePath = Path.GetFullPath(filePath); Position = TimeSpan.Zero; SetState(initialState); PositionChanged?.Invoke(this, EventArgs.Empty); return Task.CompletedTask; }
        public void Play() => SetState(PlaybackState.Playing);
        public void Pause() { if (State == PlaybackState.Playing) SetState(PlaybackState.Paused); }
        public void Resume() { if (State != PlaybackState.Paused) return; ResumeCalls++; SetState(PlaybackState.Playing); }
        public void Stop() { Position = TimeSpan.Zero; SetState(PlaybackState.Stopped); PositionChanged?.Invoke(this, EventArgs.Empty); }
        public void Restart() { RestartCalls++; Position = TimeSpan.Zero; SetState(PlaybackState.Playing); PositionChanged?.Invoke(this, EventArgs.Empty); }
        public void Seek(TimeSpan position) { SeekCalls++; Position = position < TimeSpan.Zero ? TimeSpan.Zero : position > Duration ? Duration : position; PositionChanged?.Invoke(this, EventArgs.Empty); }
        public void SetLoadedFile(string filePath) => LoadedFilePath = Path.GetFullPath(filePath);
        public void CompleteNaturally() { Position = TimeSpan.Zero; SetState(PlaybackState.Stopped); PlaybackCompleted?.Invoke(this, EventArgs.Empty); }
        private void SetState(PlaybackState state) { if (State == state) return; State = state; StateChanged?.Invoke(this, EventArgs.Empty); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
