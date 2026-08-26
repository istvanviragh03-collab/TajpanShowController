using TajpanShowController.Core.Models;

namespace TajpanShowController.Core.Interfaces;

public interface IPlaybackService : IAsyncDisposable
{
    PlaybackState State { get; }
    string? LoadedFilePath { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    float Volume { get; set; }
    int OutputDeviceNumber { get; set; }
    IReadOnlyList<AudioOutputDevice> GetOutputDevices();
    event EventHandler? StateChanged;
    event EventHandler? PositionChanged;
    event EventHandler? PlaybackCompleted;
    event EventHandler<Exception>? PlaybackFailed;
    event EventHandler<PlaybackLoadTiming>? LoadMeasured;
    Task LoadAsync(string filePath, PlaybackState initialState = PlaybackState.Stopped, CancellationToken cancellationToken = default);
    void Play();
    void Pause();
    void Resume();
    void Stop();
    void Restart();
    void Seek(TimeSpan position);
}
