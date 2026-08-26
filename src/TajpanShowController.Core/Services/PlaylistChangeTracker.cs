namespace TajpanShowController.Core.Services;

public sealed class PlaylistChangeTracker
{
    public bool IsModified { get; private set; }
    public void MarkModified() => IsModified = true;
    public void MarkSaved() => IsModified = false;
}
