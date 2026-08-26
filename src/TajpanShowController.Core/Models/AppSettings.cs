namespace TajpanShowController.Core.Models;

public sealed class AppSettings
{
    public string PlaylistName { get; set; } = "Untitled Show";
    public string? LastComPort { get; set; }
    public bool AutoConnect { get; set; } = true;
    public bool AutoReconnect { get; set; } = true;
    public float Volume { get; set; } = 0.75f;
    public int AudioOutputDeviceNumber { get; set; } = -1;
    public string AudioOutputDeviceName { get; set; } = "Alapértelmezett Windows audio";
    public List<PlaylistTrack> Playlist { get; set; } = [];
}
