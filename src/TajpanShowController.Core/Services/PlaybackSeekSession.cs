namespace TajpanShowController.Core.Services;

public sealed class PlaybackSeekSession
{
    public bool IsSeeking { get; private set; }
    public TimeSpan Position { get; private set; }
    public TimeSpan Duration { get; private set; }
    public bool IsEnabled => Duration > TimeSpan.Zero;

    public void UpdateFromPlayback(TimeSpan position, TimeSpan duration)
    {
        Duration = NormalizeDuration(duration);
        if (!IsSeeking)
            Position = Clamp(position);
    }

    public bool Begin()
    {
        if (!IsEnabled)
            return false;

        IsSeeking = true;
        return true;
    }

    public TimeSpan Preview(TimeSpan position)
    {
        if (IsSeeking)
            Position = Clamp(position);
        return Position;
    }

    public TimeSpan TargetFromFraction(double fraction) =>
        TimeSpan.FromTicks((long)(Duration.Ticks * Math.Clamp(fraction, 0d, 1d)));

    public TimeSpan Complete(TimeSpan position)
    {
        Position = Clamp(position);
        IsSeeking = false;
        return Position;
    }

    public void Cancel(TimeSpan playbackPosition)
    {
        IsSeeking = false;
        Position = Clamp(playbackPosition);
    }

    private TimeSpan Clamp(TimeSpan position) =>
        position < TimeSpan.Zero ? TimeSpan.Zero : position > Duration ? Duration : position;

    private static TimeSpan NormalizeDuration(TimeSpan duration) =>
        duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
}
