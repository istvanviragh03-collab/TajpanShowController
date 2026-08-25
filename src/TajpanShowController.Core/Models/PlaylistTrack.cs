namespace TajpanShowController.Core.Models;

public sealed class PlaylistTrack
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FilePath { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public TimeSpan? Duration { get; set; }
}
