namespace TajpanShowController.Core.Diagnostics;

/// <summary>
/// Small, non-UI, thread-safe hand-off buffer for remote diagnostics.
/// Producers never call the WPF dispatcher; the UI drains entries on its own timer.
/// </summary>
public sealed class RemoteDebugLogBuffer(int capacity = 6000)
{
    private readonly object _gate = new();
    private readonly Queue<RemoteDebugLogEntry> _entries = new();

    public int Capacity { get; } = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));

    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    public void Write(RemoteDebugLogKind kind, string message, string? parsedDescription = null) =>
        Write(new RemoteDebugLogEntry(DateTimeOffset.Now, kind, message, parsedDescription));

    public void Write(RemoteDebugLogEntry entry)
    {
        lock (_gate)
        {
            _entries.Enqueue(entry);
            TrimToCapacity();
        }
    }

    public void WriteRange(IEnumerable<RemoteDebugLogEntry> entries)
    {
        lock (_gate)
        {
            foreach (var entry in entries) _entries.Enqueue(entry);
            TrimToCapacity();
        }
    }

    public IReadOnlyList<RemoteDebugLogEntry> Drain(int maximumCount)
    {
        if (maximumCount <= 0) return [];
        lock (_gate)
        {
            var result = new List<RemoteDebugLogEntry>(Math.Min(maximumCount, _entries.Count));
            while (result.Count < maximumCount && _entries.Count > 0) result.Add(_entries.Dequeue());
            return result;
        }
    }

    private void TrimToCapacity()
    {
        while (_entries.Count > Capacity) _entries.Dequeue();
    }
}
