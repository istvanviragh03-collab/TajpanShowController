using TajpanShowController.Core.Interfaces;
using TajpanShowController.Core.Models;

namespace TajpanShowController.Core.Services;

public enum TransportCommandSource
{
    Gui,
    Remote
}

public sealed class PlaybackTransportController
{
    public static readonly TimeSpan PreviousRestartThreshold = TimeSpan.FromSeconds(3);

    private readonly IPlaybackService _playback;
    private readonly IList<PlaylistTrack> _playlist;
    private readonly Func<PlaylistTrack?> _getSelectedTrack;
    private readonly Action<PlaylistTrack?> _setSelectedTrack;
    private readonly Func<PlaylistTrack?> _getPlayingTrack;
    private readonly Action<PlaylistTrack?> _setPlayingTrack;

    public PlaybackTransportController(
        IPlaybackService playback,
        IList<PlaylistTrack> playlist,
        Func<PlaylistTrack?> getSelectedTrack,
        Action<PlaylistTrack?> setSelectedTrack,
        Func<PlaylistTrack?> getPlayingTrack,
        Action<PlaylistTrack?> setPlayingTrack)
    {
        _playback = playback;
        _playlist = playlist;
        _getSelectedTrack = getSelectedTrack;
        _setSelectedTrack = setSelectedTrack;
        _getPlayingTrack = getPlayingTrack;
        _setPlayingTrack = setPlayingTrack;
    }

    public async Task PlayAsync(CancellationToken cancellationToken = default)
    {
        if (_playback.State == PlaybackState.Playing)
            return;

        if (_playback.State == PlaybackState.Paused)
        {
            _playback.Resume();
            return;
        }

        var selected = _getSelectedTrack();
        if (selected is null)
            return;

        if (PathsEqual(_playback.LoadedFilePath, selected.FilePath))
        {
            _playback.Play();
            _setPlayingTrack(selected);
            return;
        }

        await _playback.LoadAsync(selected.FilePath, PlaybackState.Playing, cancellationToken);
        _setPlayingTrack(selected);
    }

    public async Task<bool> SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        if (_playback.State == PlaybackState.Stopped)
        {
            var selected = _getSelectedTrack();
            if (selected is null)
                return false;

            if (!PathsEqual(_playback.LoadedFilePath, selected.FilePath))
                await _playback.LoadAsync(selected.FilePath, PlaybackState.Stopped, cancellationToken);
        }

        if (_playback.LoadedFilePath is null)
            return false;

        _playback.Seek(position);
        return true;
    }

    public void Pause()
    {
        if (_playback.State == PlaybackState.Playing)
            _playback.Pause();
    }

    public void Stop() => _playback.Stop();

    public async Task NextAsync(TransportCommandSource source, CancellationToken cancellationToken = default)
    {
        if (source == TransportCommandSource.Remote && _playback.State == PlaybackState.Playing)
            return;

        var currentIndex = NavigationIndex(_playback.State);
        if (currentIndex < 0 || currentIndex >= _playlist.Count - 1)
            return;

        await ChangeTrackAsync(_playlist[currentIndex + 1], _playback.State, cancellationToken);
    }

    public async Task PreviousAsync(TransportCommandSource source, CancellationToken cancellationToken = default)
    {
        if (source == TransportCommandSource.Remote && _playback.State == PlaybackState.Playing)
            return;

        var state = _playback.State;
        if (state == PlaybackState.Playing && _playback.Position > PreviousRestartThreshold)
        {
            _playback.Restart();
            return;
        }

        var currentIndex = NavigationIndex(state);
        if (currentIndex < 0)
            return;

        if (currentIndex == 0)
        {
            if (state == PlaybackState.Playing)
                _playback.Restart();
            return;
        }

        await ChangeTrackAsync(_playlist[currentIndex - 1], state, cancellationToken);
    }

    public void PlaybackCompleted()
    {
        var finishedTrack = _getPlayingTrack();
        if (finishedTrack is not null)
            _setSelectedTrack(finishedTrack);
    }

    private async Task ChangeTrackAsync(PlaylistTrack track, PlaybackState previousState, CancellationToken cancellationToken)
    {
        _setSelectedTrack(track);

        if (previousState == PlaybackState.Stopped)
        {
            _playback.Stop();
            return;
        }

        await _playback.LoadAsync(track.FilePath, previousState, cancellationToken);
        _setPlayingTrack(track);
    }

    private int SelectedIndex()
    {
        var selected = _getSelectedTrack();
        return selected is null ? -1 : _playlist.IndexOf(selected);
    }

    private int NavigationIndex(PlaybackState state)
    {
        if (state != PlaybackState.Stopped)
        {
            var playing = _getPlayingTrack();
            if (playing is not null)
                return _playlist.IndexOf(playing);
        }

        return SelectedIndex();
    }

    private static bool PathsEqual(string? first, string second)
    {
        if (string.IsNullOrWhiteSpace(first))
            return false;

        try
        {
            return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }
    }
}
