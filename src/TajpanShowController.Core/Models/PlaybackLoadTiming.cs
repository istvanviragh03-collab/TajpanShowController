namespace TajpanShowController.Core.Models;

public sealed record PlaybackLoadTiming(
    string FilePath,
    bool FirstLoadInSession,
    TimeSpan FileProbe,
    TimeSpan PreviousAudioDispose,
    TimeSpan ReaderCreation,
    TimeSpan DurationRead,
    TimeSpan OutputCreation,
    TimeSpan OutputInitialization,
    TimeSpan PlaybackStart,
    TimeSpan Total);
