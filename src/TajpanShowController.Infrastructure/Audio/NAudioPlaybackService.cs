using NAudio.Wave;
using System.Diagnostics;
using TajpanShowController.Core.Interfaces;
using TajpanShowController.Core.Models;
using AppPlaybackState = TajpanShowController.Core.Models.PlaybackState;

namespace TajpanShowController.Infrastructure.Audio;

public sealed class NAudioPlaybackService : IPlaybackService
{
    private readonly object _gate = new();
    private readonly HashSet<string> _loadedFiles = new(StringComparer.OrdinalIgnoreCase);
    private WaveOutEvent? _output;
    private AudioFileReader? _reader;
    private readonly PeriodicTimer _timer = new(TimeSpan.FromMilliseconds(100));
    private readonly CancellationTokenSource _timerCts = new();
    private float _volume = 0.75f;
    private int _outputDeviceNumber = -1;
    private AppPlaybackState _state;
    private bool _manualStop;
    private string? _loadedFilePath;

    public NAudioPlaybackService() => _ = ReportPositionAsync();
    public AppPlaybackState State => _state;
    public string? LoadedFilePath { get { lock (_gate) return _loadedFilePath; } }
    public TimeSpan Position { get { lock (_gate) return _reader?.CurrentTime ?? TimeSpan.Zero; } }
    public TimeSpan Duration { get { lock (_gate) return _reader?.TotalTime ?? TimeSpan.Zero; } }
    public float Volume { get => _volume; set { _volume = Math.Clamp(value, 0, 1); lock (_gate) if (_reader is not null) _reader.Volume = _volume; } }
    public int OutputDeviceNumber { get => _outputDeviceNumber; set => _outputDeviceNumber = value; }
    public IReadOnlyList<AudioOutputDevice> GetOutputDevices()
    {
        var devices = new List<AudioOutputDevice> { new(-1, "Alapértelmezett Windows audio") };
        for (var i = 0; i < WaveOut.DeviceCount; i++) { try { devices.Add(new(i, WaveOut.GetCapabilities(i).ProductName)); } catch { } }
        return devices;
    }
    public event EventHandler? StateChanged;
    public event EventHandler? PositionChanged;
    public event EventHandler? PlaybackCompleted;
    public event EventHandler<Exception>? PlaybackFailed;
    public event EventHandler<PlaybackLoadTiming>? LoadMeasured;

    public Task LoadAsync(string filePath, AppPlaybackState initialState = AppPlaybackState.Stopped, CancellationToken cancellationToken = default) =>
        Task.Run(() => Load(filePath, initialState, cancellationToken), cancellationToken);

    private void Load(string filePath, AppPlaybackState initialState, CancellationToken cancellationToken)
    {
        var totalStart = Stopwatch.GetTimestamp();
        cancellationToken.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(filePath);
        var probeStart = Stopwatch.GetTimestamp();
        var fileExists = File.Exists(fullPath);
        var fileProbeElapsed = Stopwatch.GetElapsedTime(probeStart);
        if (!fileExists) throw new FileNotFoundException("Az audiofájl nem található.", filePath);
        var firstLoadInSession = false;
        TimeSpan disposeElapsed;
        TimeSpan readerElapsed;
        TimeSpan durationReadElapsed;
        TimeSpan outputCreationElapsed;
        TimeSpan outputInitializationElapsed;
        TimeSpan playbackStartElapsed = TimeSpan.Zero;
        lock (_gate)
        {
            var phaseStart = Stopwatch.GetTimestamp();
            DisposeAudio();
            disposeElapsed = Stopwatch.GetElapsedTime(phaseStart);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                phaseStart = Stopwatch.GetTimestamp();
                _reader = new AudioFileReader(filePath) { Volume = _volume };
                readerElapsed = Stopwatch.GetElapsedTime(phaseStart);
                phaseStart = Stopwatch.GetTimestamp();
                _ = _reader.TotalTime;
                durationReadElapsed = Stopwatch.GetElapsedTime(phaseStart);
                phaseStart = Stopwatch.GetTimestamp();
                _output = new WaveOutEvent { DeviceNumber = _outputDeviceNumber };
                _output.PlaybackStopped += OnPlaybackStopped;
                outputCreationElapsed = Stopwatch.GetElapsedTime(phaseStart);
                phaseStart = Stopwatch.GetTimestamp();
                _output.Init(_reader);
                outputInitializationElapsed = Stopwatch.GetElapsedTime(phaseStart);
                _manualStop = false;
                _loadedFilePath = fullPath;
                if (initialState == AppPlaybackState.Playing)
                {
                    phaseStart = Stopwatch.GetTimestamp();
                    _output.Play();
                    playbackStartElapsed = Stopwatch.GetElapsedTime(phaseStart);
                }
                SetState(initialState);
                firstLoadInSession = _loadedFiles.Add(fullPath);
            }
            catch (Exception ex) { DisposeAudio(); PlaybackFailed?.Invoke(this, ex); throw; }
        }
        var timing = new PlaybackLoadTiming(
            fullPath,
            firstLoadInSession,
            fileProbeElapsed,
            disposeElapsed,
            readerElapsed,
            durationReadElapsed,
            outputCreationElapsed,
            outputInitializationElapsed,
            playbackStartElapsed,
            Stopwatch.GetElapsedTime(totalStart));
        LoadMeasured?.Invoke(this, timing);
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Play() { lock (_gate) { if (_output is null) return; _manualStop = false; _output.Play(); SetState(AppPlaybackState.Playing); } }
    public void Pause() { lock (_gate) { if (_output is null || _state != AppPlaybackState.Playing) return; _output.Pause(); SetState(AppPlaybackState.Paused); } }
    public void Resume() { lock (_gate) { if (_output is null || _state != AppPlaybackState.Paused) return; _output.Play(); SetState(AppPlaybackState.Playing); } }
    public void Stop() { lock (_gate) { if (_output is null) return; _manualStop = true; _output.Stop(); if (_reader is not null) _reader.Position = 0; SetState(AppPlaybackState.Stopped); } }
    public void Restart() { lock (_gate) { if (_output is null || _reader is null) return; _manualStop = false; _reader.Position = 0; _output.Play(); SetState(AppPlaybackState.Playing); PositionChanged?.Invoke(this, EventArgs.Empty); } }
    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            if (_reader is null) return;
            _reader.CurrentTime = position < TimeSpan.Zero
                ? TimeSpan.Zero
                : position > _reader.TotalTime ? _reader.TotalTime : position;
        }
        PositionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        lock (_gate)
        {
            if (e.Exception is not null) PlaybackFailed?.Invoke(this, e.Exception);
            var completed = !_manualStop && _reader is not null && _reader.Position >= _reader.Length;
            if (completed) _reader!.Position = 0;
            SetState(AppPlaybackState.Stopped);
            if (completed) PlaybackCompleted?.Invoke(this, EventArgs.Empty);
        }
    }
    private async Task ReportPositionAsync()
    {
        try { while (await _timer.WaitForNextTickAsync(_timerCts.Token)) PositionChanged?.Invoke(this, EventArgs.Empty); }
        catch (OperationCanceledException) { }
    }
    private void SetState(AppPlaybackState state) { if (_state == state) return; _state = state; StateChanged?.Invoke(this, EventArgs.Empty); }
    private void DisposeAudio() { if (_output is not null) _output.PlaybackStopped -= OnPlaybackStopped; _output?.Dispose(); _reader?.Dispose(); _output = null; _reader = null; _loadedFilePath = null; }
    public ValueTask DisposeAsync() { _timerCts.Cancel(); _timer.Dispose(); lock (_gate) DisposeAudio(); _timerCts.Dispose(); return ValueTask.CompletedTask; }
}
