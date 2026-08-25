using TajpanShowController.Core.Models;

namespace TajpanShowController.Core.Interfaces;

public interface IPlaybackService : IAsyncDisposable
{
    PlaybackState State { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    float Volume { get; set; }
    event EventHandler? StateChanged;
    event EventHandler? PositionChanged;
    event EventHandler? PlaybackCompleted;
    event EventHandler<Exception>? PlaybackFailed;
    Task LoadAsync(string filePath, CancellationToken cancellationToken = default);
    void Play();
    void Pause();
    void Resume();
    void Stop();
}
