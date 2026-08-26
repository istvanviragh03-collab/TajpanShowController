using System.Threading.Channels;

namespace TajpanShowController.Core.Diagnostics;

/// <summary>
/// Small, non-UI, thread-safe hand-off buffer for remote diagnostics.
/// Producers never call the WPF dispatcher; the UI drains entries on its own timer.
/// </summary>
public sealed class RemoteDebugLogBuffer
{
    private readonly Channel<RemoteDebugLogEntry> _entries;

    public RemoteDebugLogBuffer(int capacity = 6000)
    {
        Capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
        _entries = Channel.CreateBounded<RemoteDebugLogEntry>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public int Capacity { get; }

    public int Count => _entries.Reader.Count;

    public void Write(RemoteDebugLogKind kind, string message, string? parsedDescription = null) =>
        Write(new RemoteDebugLogEntry(DateTimeOffset.Now, kind, message, parsedDescription));

    public void Write(RemoteDebugLogEntry entry)
    {
        Enqueue(entry);
    }

    public void WriteRange(IEnumerable<RemoteDebugLogEntry> entries)
    {
        foreach (var entry in entries)
            Enqueue(entry);
    }

    public IReadOnlyList<RemoteDebugLogEntry> Drain(int maximumCount)
    {
        if (maximumCount <= 0) return [];
        var result = new List<RemoteDebugLogEntry>(Math.Min(maximumCount, Count));
        while (result.Count < maximumCount && _entries.Reader.TryRead(out var entry)) result.Add(entry);
        return result;
    }

    private void Enqueue(RemoteDebugLogEntry entry) => _entries.Writer.TryWrite(entry);
}
