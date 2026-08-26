namespace TajpanShowController.Core.Services;

/// <summary>
/// Separates explicit pointer interaction from periodic playback position updates.
/// Only CompleteSeekAsync and SeekToFractionAsync invoke the audio seek callback.
/// </summary>
public sealed class PlaybackTimelineController(Func<TimeSpan, CancellationToken, Task> seekAsync)
{
    public double PositionSeconds { get; private set; }
    public double DurationSeconds { get; private set; }
    public bool IsSeeking { get; private set; }
    public bool IsEnabled => DurationSeconds > 0;
    public event EventHandler? Changed;

    public void Reset(TimeSpan duration)
    {
        IsSeeking = false;
        DurationSeconds = Math.Max(0, duration.TotalSeconds);
        PositionSeconds = 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Synchronize(TimeSpan position, TimeSpan duration)
    {
        if (IsSeeking) return;
        DurationSeconds = Math.Max(0, duration.TotalSeconds);
        PositionSeconds = Clamp(position.TotalSeconds);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool BeginSeek()
    {
        if (!IsEnabled) return false;
        IsSeeking = true;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void Preview(double positionSeconds)
    {
        if (!IsSeeking) return;
        PositionSeconds = Clamp(positionSeconds);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task CompleteSeekAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSeeking) return;
        var target = TimeSpan.FromSeconds(Clamp(PositionSeconds));
        try
        {
            await seekAsync(target, cancellationToken);
        }
        finally
        {
            IsSeeking = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task SeekToFractionAsync(double fraction, CancellationToken cancellationToken = default)
    {
        if (!BeginSeek()) return;
        Preview(DurationSeconds * Math.Clamp(fraction, 0, 1));
        await CompleteSeekAsync(cancellationToken);
    }

    private double Clamp(double seconds) => Math.Clamp(seconds, 0, DurationSeconds);
}
