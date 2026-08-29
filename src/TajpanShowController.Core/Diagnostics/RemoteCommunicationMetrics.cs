using System.Diagnostics;

namespace TajpanShowController.Core.Diagnostics;

public sealed record RemoteCommunicationMetricsSnapshot(
    long PollCount,
    long TimeoutCount,
    TimeSpan AveragePollRtt,
    TimeSpan MaxPollRtt,
    TimeSpan MaxReceiveToParse,
    TimeSpan MaxParseToAck,
    TimeSpan MaxScheduleDelay,
    TimeSpan MaxTimeoutOvershoot,
    TimeSpan AveragePollGap,
    TimeSpan MaxPollGap,
    TimeSpan AverageValidResponseGap,
    TimeSpan MaxValidResponseGap,
    TimeSpan MedianPollRtt,
    TimeSpan P95PollRtt,
    long DisplayTransactionCount,
    TimeSpan AverageDisplayRtt,
    TimeSpan MaxDisplayRtt,
    TimeSpan MaxDisplaySnapshotSettling,
    TimeSpan AverageButtonDispatchLatency,
    TimeSpan MaxButtonDispatchLatency);

/// <summary>Lock-free, monotonic timing aggregation for the serial critical path.</summary>
public sealed class RemoteCommunicationMetrics
{
    private long _pollCount;
    private long _timeoutCount;
    private long _totalPollTicks;
    private long _maxPollTicks;
    private long _maxReceiveToParseTicks;
    private long _maxParseToAckTicks;
    private long _maxScheduleDelayTicks;
    private long _maxTimeoutOvershootTicks;
    private long _lastPollTimestamp;
    private long _maxPollGapTicks;
    private long _totalPollGapTicks;
    private long _pollGapCount;
    private long _lastValidResponseTimestamp;
    private long _maxValidResponseGapTicks;
    private long _totalValidResponseGapTicks;
    private long _validResponseGapCount;
    private readonly long[] _pollRttSamples = new long[256];
    private long _pollRttSampleIndex;
    private long _displayCount;
    private long _totalDisplayTicks;
    private long _maxDisplayTicks;
    private long _maxDisplaySnapshotSettlingTicks;
    private long _buttonDispatchCount;
    private long _totalButtonDispatchTicks;
    private long _maxButtonDispatchTicks;

    public void RecordScheduleDelay(TimeSpan delay) => UpdateMax(ref _maxScheduleDelayTicks, ToStopwatchTicks(delay));
    public void RecordTimeout() => Interlocked.Increment(ref _timeoutCount);

    public void RecordPollSent(long timestamp)
    {
        var previous = Interlocked.Exchange(ref _lastPollTimestamp, timestamp);
        if (previous > 0)
        {
            var gap = timestamp - previous;
            Interlocked.Add(ref _totalPollGapTicks, gap);
            Interlocked.Increment(ref _pollGapCount);
            UpdateMax(ref _maxPollGapTicks, gap);
        }
    }

    public void RecordValidResponse(long timestamp)
    {
        var previous = Interlocked.Exchange(ref _lastValidResponseTimestamp, timestamp);
        if (previous > 0)
        {
            var gap = timestamp - previous;
            Interlocked.Add(ref _totalValidResponseGapTicks, gap);
            Interlocked.Increment(ref _validResponseGapCount);
            UpdateMax(ref _maxValidResponseGapTicks, gap);
        }
    }

    public void RecordDisplay(long sentTimestamp, long completedTimestamp)
    {
        var ticks = Math.Max(0, completedTimestamp - sentTimestamp);
        Interlocked.Increment(ref _displayCount);
        Interlocked.Add(ref _totalDisplayTicks, ticks);
        UpdateMax(ref _maxDisplayTicks, ticks);
    }

    public void RecordDisplaySnapshotSettled(TimeSpan elapsed) =>
        UpdateMax(ref _maxDisplaySnapshotSettlingTicks, ToStopwatchTicks(elapsed));

    public void RecordButtonDispatch(long parsedTimestamp, long dispatchedTimestamp)
    {
        var ticks = Math.Max(0, dispatchedTimestamp - parsedTimestamp);
        Interlocked.Increment(ref _buttonDispatchCount);
        Interlocked.Add(ref _totalButtonDispatchTicks, ticks);
        UpdateMax(ref _maxButtonDispatchTicks, ticks);
    }

    public void RecordPoll(
        long pollSentTimestamp,
        long bytesReceivedTimestamp,
        long frameParsedTimestamp,
        long ackSentTimestamp,
        long completedTimestamp,
        bool timedOut,
        TimeSpan timeout)
    {
        var pollTicks = Math.Max(0, completedTimestamp - pollSentTimestamp);
        Interlocked.Increment(ref _pollCount);
        Interlocked.Add(ref _totalPollTicks, pollTicks);
        var sampleIndex = Interlocked.Increment(ref _pollRttSampleIndex) - 1;
        Volatile.Write(ref _pollRttSamples[sampleIndex % _pollRttSamples.Length], pollTicks);
        UpdateMax(ref _maxPollTicks, pollTicks);
        if (timedOut)
        {
            Interlocked.Increment(ref _timeoutCount);
            UpdateMax(ref _maxTimeoutOvershootTicks, Math.Max(0, pollTicks - ToStopwatchTicks(timeout)));
        }
        if (bytesReceivedTimestamp > 0 && frameParsedTimestamp >= bytesReceivedTimestamp)
            UpdateMax(ref _maxReceiveToParseTicks, frameParsedTimestamp - bytesReceivedTimestamp);
        if (frameParsedTimestamp > 0 && ackSentTimestamp >= frameParsedTimestamp)
            UpdateMax(ref _maxParseToAckTicks, ackSentTimestamp - frameParsedTimestamp);
    }

    public RemoteCommunicationMetricsSnapshot Snapshot()
    {
        var count = Volatile.Read(ref _pollCount);
        var total = Volatile.Read(ref _totalPollTicks);
        var rttSamples = _pollRttSamples.Where(value => value > 0).ToArray();
        Array.Sort(rttSamples);
        var validGapCount = Volatile.Read(ref _validResponseGapCount);
        var displayCount = Volatile.Read(ref _displayCount);
        var buttonCount = Volatile.Read(ref _buttonDispatchCount);
        return new RemoteCommunicationMetricsSnapshot(
            count,
            Volatile.Read(ref _timeoutCount),
            FromStopwatchTicks(count == 0 ? 0 : total / count),
            FromStopwatchTicks(Volatile.Read(ref _maxPollTicks)),
            FromStopwatchTicks(Volatile.Read(ref _maxReceiveToParseTicks)),
            FromStopwatchTicks(Volatile.Read(ref _maxParseToAckTicks)),
            FromStopwatchTicks(Volatile.Read(ref _maxScheduleDelayTicks)),
            FromStopwatchTicks(Volatile.Read(ref _maxTimeoutOvershootTicks)),
            FromStopwatchTicks(Volatile.Read(ref _pollGapCount) == 0 ? 0 : Volatile.Read(ref _totalPollGapTicks) / Volatile.Read(ref _pollGapCount)),
            FromStopwatchTicks(Volatile.Read(ref _maxPollGapTicks)),
            FromStopwatchTicks(validGapCount == 0 ? 0 : Volatile.Read(ref _totalValidResponseGapTicks) / validGapCount),
            FromStopwatchTicks(Volatile.Read(ref _maxValidResponseGapTicks)),
            Percentile(rttSamples, 0.50),
            Percentile(rttSamples, 0.95),
            displayCount,
            FromStopwatchTicks(displayCount == 0 ? 0 : Volatile.Read(ref _totalDisplayTicks) / displayCount),
            FromStopwatchTicks(Volatile.Read(ref _maxDisplayTicks)),
            FromStopwatchTicks(Volatile.Read(ref _maxDisplaySnapshotSettlingTicks)),
            FromStopwatchTicks(buttonCount == 0 ? 0 : Volatile.Read(ref _totalButtonDispatchTicks) / buttonCount),
            FromStopwatchTicks(Volatile.Read(ref _maxButtonDispatchTicks)));
    }

    private static TimeSpan Percentile(long[] sorted, double percentile) => sorted.Length == 0
        ? TimeSpan.Zero
        : FromStopwatchTicks(sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)]);

    private static void UpdateMax(ref long target, long value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) return;
            current = observed;
        }
    }

    private static long ToStopwatchTicks(TimeSpan value) =>
        (long)(Math.Max(0, value.TotalSeconds) * Stopwatch.Frequency);

    private static TimeSpan FromStopwatchTicks(long ticks) =>
        TimeSpan.FromSeconds((double)Math.Max(0, ticks) / Stopwatch.Frequency);
}
