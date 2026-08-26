namespace TajpanShowController.Core.Diagnostics;

public enum RemoteDebugLogKind
{
    Tx,
    Rx,
    Event,
    Playback,
    State,
    Info,
    Warning,
    Error
}

public sealed record RemoteDebugLogEntry(
    DateTimeOffset Timestamp,
    RemoteDebugLogKind Kind,
    string Message,
    string? ParsedDescription = null)
{
    public string TimestampText => Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
    public string KindText => Kind switch
    {
        RemoteDebugLogKind.Warning => "WARN",
        _ => Kind.ToString().ToUpperInvariant()
    };
}
