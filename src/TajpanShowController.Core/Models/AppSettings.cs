namespace TajpanShowController.Core.Models;

public sealed class AppSettings
{
    public string? LastComPort { get; set; }
    public float Volume { get; set; } = 0.75f;
    public List<PlaylistTrack> Playlist { get; set; } = [];
}
