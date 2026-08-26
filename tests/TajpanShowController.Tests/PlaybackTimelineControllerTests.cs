using TajpanShowController.Core.Services;
using Xunit;

namespace TajpanShowController.Tests;

public sealed class PlaybackTimelineControllerTests
{
    [Fact]
    public async Task ClickAtFiftyPercentSeeksToHalfDuration()
    {
        var seek = new SeekRecorder();
        var timeline = new PlaybackTimelineController(seek.InvokeAsync);
        timeline.Reset(TimeSpan.FromSeconds(268));

        await timeline.SeekToFractionAsync(.5, TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(134), seek.LastPosition);
        Assert.Equal(134, timeline.PositionSeconds);
        Assert.False(timeline.IsSeeking);
    }

    [Fact]
    public async Task PlaybackTimerCannotOverwriteDragPreview()
    {
        var seek = new SeekRecorder();
        var timeline = new PlaybackTimelineController(seek.InvokeAsync);
        timeline.Synchronize(TimeSpan.FromSeconds(42), TimeSpan.FromSeconds(268));
        Assert.True(timeline.BeginSeek());
        timeline.Preview(118);

        timeline.Synchronize(TimeSpan.FromSeconds(43), TimeSpan.FromSeconds(268));

        Assert.Equal(118, timeline.PositionSeconds);
        Assert.Equal(0, seek.Calls);
        await timeline.CompleteSeekAsync(TestContext.Current.CancellationToken);
        Assert.Equal(TimeSpan.FromSeconds(118), seek.LastPosition);
        Assert.Equal(1, seek.Calls);
    }

    [Fact]
    public void ProgrammaticPositionUpdateNeverCallsSeek()
    {
        var seek = new SeekRecorder();
        var timeline = new PlaybackTimelineController(seek.InvokeAsync);

        timeline.Synchronize(TimeSpan.FromSeconds(17), TimeSpan.FromSeconds(180));
        timeline.Synchronize(TimeSpan.FromSeconds(18), TimeSpan.FromSeconds(180));

        Assert.Equal(18, timeline.PositionSeconds);
        Assert.Equal(0, seek.Calls);
    }

    [Fact]
    public void MissingOrZeroDurationDisablesSeeking()
    {
        var timeline = new PlaybackTimelineController((_, _) => Task.CompletedTask);

        timeline.Reset(TimeSpan.Zero);

        Assert.False(timeline.IsEnabled);
        Assert.False(timeline.BeginSeek());
        Assert.Equal(0, timeline.PositionSeconds);
        Assert.Equal(0, timeline.DurationSeconds);
    }

    [Fact]
    public void DurationChangeUpdatesMaximumAndClampsPosition()
    {
        var timeline = new PlaybackTimelineController((_, _) => Task.CompletedTask);
        timeline.Synchronize(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(180));

        timeline.Synchronize(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(60));

        Assert.Equal(60, timeline.DurationSeconds);
        Assert.Equal(60, timeline.PositionSeconds);
    }

    [Fact]
    public async Task ClickFractionIsClampedAtTrackEndAndCommitsOnlyOnce()
    {
        var seek = new SeekRecorder();
        var timeline = new PlaybackTimelineController(seek.InvokeAsync);
        timeline.Reset(TimeSpan.FromSeconds(180));

        await timeline.SeekToFractionAsync(1.5, TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(180), seek.LastPosition);
        Assert.Equal(1, seek.Calls);
    }

    private sealed class SeekRecorder
    {
        public int Calls { get; private set; }
        public TimeSpan LastPosition { get; private set; }
        public Task InvokeAsync(TimeSpan position, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastPosition = position;
            return Task.CompletedTask;
        }
    }
}
