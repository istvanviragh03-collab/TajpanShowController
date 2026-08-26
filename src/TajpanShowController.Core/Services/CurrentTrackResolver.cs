using TajpanShowController.Core.Models;

namespace TajpanShowController.Core.Services;

public static class CurrentTrackResolver
{
    public static PlaylistTrack? Resolve(PlaylistTrack? selected, PlaylistTrack? playing, PlaybackState state) =>
        state is PlaybackState.Playing or PlaybackState.Paused ? playing : selected;
}
