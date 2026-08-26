using TajpanShowController.Core.Interfaces;

namespace TajpanShowController.Core.Models;

public sealed record RemoteStatusPresentation(string Text, string Color, string Detail)
{
    public static RemoteStatusPresentation From(RemoteConnectionState state, string? detail = null) => state switch
    {
        RemoteConnectionState.Connected => new("CONNECTED", "#4CDA82", "Communication healthy"),
        RemoteConnectionState.Fault => new("ERROR", "#E45B60", string.IsNullOrWhiteSpace(detail) ? "Remote communication error" : detail),
        RemoteConnectionState.Connecting => new("CONNECTING", "#D9A441", "Waiting for remote response"),
        _ => new("DISCONNECTED", "#75808A", "No active remote connection")
    };
}
