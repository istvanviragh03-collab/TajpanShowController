using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TajpanShowController.Core.Diagnostics;
using TajpanShowController.Core.Interfaces;
using TajpanShowController.Core.Models;
using TajpanShowController.Core.Services;
using TajpanShowController.Infrastructure.Audio;
using TajpanShowController.Infrastructure.Persistence;
using TajpanShowController.Infrastructure.Serial;

namespace TajpanShowController.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IPlaybackService _playback = new NAudioPlaybackService();
    private readonly PlaybackTransportController _transport;
    private readonly PlaybackTimelineController _timeline;
    private readonly ISettingsStore _settings;
    private readonly RemoteControllerService _remote;
    private readonly RemoteConnectionCoordinator _connectionCoordinator;
    private SimulatedRemoteTransport _simulator = new();
    private readonly RemoteDebugLogBuffer _debugLog = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _playingBlink = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _debugUiTimer = new() { Interval = TimeSpan.FromMilliseconds(75) };
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly PlaylistChangeTracker _playlistChanges = new();
    private AppSettings _loadedSettings = new();
    private PlaybackState _lastLoggedPlaybackState = PlaybackState.Stopped;
    private bool _isInitializing;

    public const int MaximumDiagnosticEntries = 3000;
    private const int MaximumDiagnosticBatchSize = 250;

    [ObservableProperty] private PlaylistTrack? selectedTrack;
    [ObservableProperty] private PlaylistTrack? playingTrack;
    [ObservableProperty] private string currentTime = "00:00.0";
    [ObservableProperty] private string totalTime = "00:00.0";
    [ObservableProperty] private double volume = 75;
    [ObservableProperty] private string systemClock = DateTime.Now.ToString("yyyy. MM. dd.  HH:mm:ss");
    [ObservableProperty] private string connectionLabel = "DISCONNECTED";
    [ObservableProperty] private string connectionDetail = "—   200000 baud";
    [ObservableProperty] private string remoteStatusColor = "#75808A";
    [ObservableProperty] private string remoteStatusDetail = "No active remote connection";
    [ObservableProperty] private string lastResponse = "—";
    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private bool isSimulation;
    [ObservableProperty] private string? selectedPort;
    [ObservableProperty] private bool autoConnect = true;
    [ObservableProperty] private bool autoReconnect = true;
    [ObservableProperty] private string statusMessage = "Készen áll";
    [ObservableProperty] private bool settingsVisible;
    [ObservableProperty] private bool playingIndicatorVisible;
    [ObservableProperty] private AudioOutputDevice? selectedAudioDevice;
    [ObservableProperty] private string playlistName = "Untitled Show";
    [ObservableProperty] private string playlistPath = "Nincs megnyitott playlist fájl";
    [ObservableProperty] private bool isPlaylistModified;

    public ObservableCollection<PlaylistTrack> Playlist { get; } = [];
    public ObservableCollection<string> Ports { get; } = [];
    public ObservableCollection<RemoteDebugLogEntry> Diagnostics { get; } = [];
    public ObservableCollection<AudioOutputDevice> AudioDevices { get; } = [];
    public event EventHandler? DiagnosticsFlushed;
    public PlaylistTrack? DisplayedTrack => CurrentTrackResolver.Resolve(SelectedTrack, PlayingTrack, _playback.State);
    public string VolumePercent => $"{Math.Round(Math.Clamp(Volume, 0, 100)):0}%";
    public string AudioStatusLabel => SelectedAudioDevice is null ? "NO DEVICE" : "READY";
    public string AudioStatusColor => SelectedAudioDevice is null ? "#E45B60" : "#4CDA82";
    public string NowPlayingFilename => DisplayedTrack is null ? "Nincs kiválasztott szám" : Path.GetFileName(DisplayedTrack.FilePath);
    public string SelectedTitle => SelectedTrack is null ? "Nincs kiválasztott szám" : Path.GetFileName(SelectedTrack.FilePath);
    public string SelectedPosition => SelectedTrack is null ? "0 / 0" : $"{Playlist.IndexOf(SelectedTrack) + 1} / {Playlist.Count}";
    public string PlaybackGlyph => "PLAY";
    public string LcdLine1 => DisplayedTrack is null ? "00 NINCS KIJEL." : $"{Playlist.IndexOf(DisplayedTrack) + 1:00} {Protocol.ProtocolTextForLcd(Path.GetFileNameWithoutExtension(DisplayedTrack.FilePath), 13)}";
    public string LcdLine2 => $"{StateShort,-6}{CurrentTime,8}";
    public string StateShort => _playback.State switch { PlaybackState.Playing => "PLAY", PlaybackState.Paused => "PAUSE", _ => "STOP" };
    public double SeekPositionSeconds { get => _timeline.PositionSeconds; set => _timeline.Preview(value); }
    public double SeekDurationSeconds => _timeline.DurationSeconds;
    public bool IsSeeking => _timeline.IsSeeking;
    public bool IsSeekEnabled => DisplayedTrack is not null && _timeline.IsEnabled;
    public ICommand PlayCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }

    public MainWindowViewModel()
    {
        var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TajpanShowController");
        _settings = new JsonSettingsStore(appData);
        _remote = new RemoteControllerService(CreateRemoteTransport, _debugLog);
        _connectionCoordinator = new RemoteConnectionCoordinator(
            _remote,
            () => new RemoteConnectionOptions(SelectedPort, IsSimulation),
            () => OnUi(UpdateRemoteDisplay),
            _debugLog);
        _transport = new PlaybackTransportController(_playback, Playlist, () => SelectedTrack, track => SelectedTrack = track, () => PlayingTrack, track => PlayingTrack = track);
        _timeline = new PlaybackTimelineController(_transport.SeekAsync);
        _timeline.Changed += (_, _) => ApplyTimelineState();
        PlayCommand = new AsyncRelayCommand(() => ExecuteTransportAsync(_transport.PlayAsync));
        StopCommand = new RelayCommand(Stop);
        PauseCommand = new RelayCommand(Pause);
        PreviousCommand = new AsyncRelayCommand(() => ExecuteTransportAsync(ct => _transport.PreviousAsync(TransportCommandSource.Gui, ct)));
        NextCommand = new AsyncRelayCommand(() => ExecuteTransportAsync(ct => _transport.NextAsync(TransportCommandSource.Gui, ct)));
        RemoveCommand = new RelayCommand(RemoveSelected);
        MoveUpCommand = new RelayCommand(() => MoveSelected(-1));
        MoveDownCommand = new RelayCommand(() => MoveSelected(1));
        ConnectCommand = new AsyncRelayCommand(ConnectAsync);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync);
        _playback.StateChanged += PlaybackChanged;
        _playback.PositionChanged += PlaybackPositionChanged;
        _playback.PlaybackFailed += (_, ex) =>
        {
            _debugLog.Write(RemoteDebugLogKind.Error, "Playback error: " + ex.Message);
            OnUi(() => StatusMessage = "Lejátszási hiba: " + ex.Message);
        };
        _playback.PlaybackCompleted += (_, _) => OnUi(_transport.PlaybackCompleted);
        _playback.LoadMeasured += (_, timing) =>
        {
            _debugLog.Write(
                RemoteDebugLogKind.Playback,
                $"Audio load {(timing.FirstLoadInSession ? "FIRST" : "REPEAT")} {Path.GetFileName(timing.FilePath)}: " +
                $"total={timing.Total.TotalMilliseconds:F1} ms, probe={timing.FileProbe.TotalMilliseconds:F1} ms, " +
                $"reader={timing.ReaderCreation.TotalMilliseconds:F1} ms, duration={timing.DurationRead.TotalMilliseconds:F1} ms, " +
                $"init={timing.OutputInitialization.TotalMilliseconds:F1} ms, " +
                $"start={timing.PlaybackStart.TotalMilliseconds:F1} ms");
            var remoteTiming = _remote.TimingMetrics.Snapshot();
            _debugLog.Write(
                RemoteDebugLogKind.Info,
                $"Remote timing: polls={remoteTiming.PollCount}, timeouts={remoteTiming.TimeoutCount}, " +
                $"RTT avg/max={remoteTiming.AveragePollRtt.TotalMilliseconds:F2}/{remoteTiming.MaxPollRtt.TotalMilliseconds:F2} ms, " +
                $"RX-parse max={remoteTiming.MaxReceiveToParse.TotalMilliseconds:F2} ms, " +
                $"parse-ACK max={remoteTiming.MaxParseToAck.TotalMilliseconds:F2} ms");
        };
        _remote.ButtonPressed += RemoteButtonPressed;
        _remote.StatusChanged += (_, _) => OnUi(UpdateRemoteStatus);
        _clock.Tick += (_, _) => SystemClock = DateTime.Now.ToString("yyyy. MM. dd.  HH:mm:ss");
        _playingBlink.Tick += (_, _) => PlayingIndicatorVisible = _playback.State == PlaybackState.Playing ? !PlayingIndicatorVisible : _playback.State == PlaybackState.Paused;
        _debugUiTimer.Tick += (_, _) => FlushDiagnostics();
        _clock.Start();
        _playingBlink.Start();
        _debugUiTimer.Start();
    }

    public async Task InitializeAsync()
    {
        _isInitializing = true;
        _loadedSettings = await _settings.LoadAsync();
        AutoConnect = _loadedSettings.AutoConnect;
        AutoReconnect = _loadedSettings.AutoReconnect;
        PlaylistName = string.IsNullOrWhiteSpace(_loadedSettings.PlaylistName) ? "Untitled Show" : _loadedSettings.PlaylistName;
        foreach (var track in _loadedSettings.Playlist.Where(t => !string.IsNullOrWhiteSpace(t.FilePath))) Playlist.Add(track);
        Volume = Math.Clamp(_loadedSettings.Volume * 100, 0, 100); _playback.Volume = (float)(Volume / 100);
        foreach (var device in _playback.GetOutputDevices()) AudioDevices.Add(device);
        SelectedAudioDevice = AudioDevices.FirstOrDefault(d => d.DeviceNumber == _loadedSettings.AudioOutputDeviceNumber) ?? AudioDevices.FirstOrDefault();
        SelectedPort = _loadedSettings.LastComPort; RefreshPorts();
        SelectedTrack = Playlist.FirstOrDefault(); RaiseTrackProperties();
        SetPlaylistModified(false);
        _isInitializing = false;
        _connectionCoordinator.SetAutoReconnect(AutoReconnect);
        UpdateRemoteDisplay();
        UpdateRemoteStatus();
        await _connectionCoordinator.StartAsync(AutoConnect);
    }

    partial void OnSelectedTrackChanged(PlaylistTrack? value)
    {
        RaiseTrackProperties();
        if (_playback.State != PlaybackState.Stopped) return;
        ResetTimelineForDisplayedTrack();
        UpdateRemoteDisplay();
    }
    partial void OnPlayingTrackChanged(PlaylistTrack? value) { RaiseTrackProperties(); RefreshTimelineFromPlayback(); }
    partial void OnVolumeChanged(double value) { _playback.Volume = (float)Math.Clamp(value / 100, 0, 1); OnPropertyChanged(nameof(VolumePercent)); }
    partial void OnSelectedAudioDeviceChanged(AudioOutputDevice? value) { _playback.OutputDeviceNumber = value?.DeviceNumber ?? -1; OnPropertyChanged(nameof(AudioStatusLabel)); OnPropertyChanged(nameof(AudioStatusColor)); if (!_isInitializing) _ = SaveAsync(); }
    partial void OnPlaylistNameChanged(string value) { if (_isInitializing) return; SetPlaylistModified(true); _ = SaveAsync(); }
    partial void OnSelectedPortChanged(string? value) { if (!_isInitializing) _ = SaveAsync(); UpdateRemoteStatus(); }
    partial void OnIsSimulationChanged(bool value) => UpdateRemoteStatus();
    partial void OnAutoConnectChanged(bool value) { if (!_isInitializing) _ = SaveAsync(); }
    partial void OnAutoReconnectChanged(bool value) { if (_isInitializing) return; _connectionCoordinator.SetAutoReconnect(value); _ = SaveAsync(); }

    public void SetPlaylistNameFromStorage(string value) { _isInitializing = true; PlaylistName = value; _isInitializing = false; SetPlaylistModified(false); _ = SaveAsync(); }
    public void MarkPlaylistSaved() => SetPlaylistModified(false);
    public void MarkPlaylistModified() { SetPlaylistModified(true); _ = SaveAsync(); }

    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(File.Exists))
        {
            if (Playlist.Any(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase))) continue;
            Playlist.Add(new PlaylistTrack { FilePath = path, Title = Path.GetFileNameWithoutExtension(path), Duration = AudioMetadataReader.TryGetDuration(path) });
        }
        SelectedTrack ??= Playlist.FirstOrDefault(); SetPlaylistModified(true); await SaveAsync(); RaiseTrackProperties();
    }
    public async Task ClearAsync() { Stop(); Playlist.Clear(); SelectedTrack = null; PlayingTrack = null; SetPlaylistModified(true); await SaveAsync(); RaiseTrackProperties(); }
    public void RefreshPorts()
    {
        var selected = SelectedPort;
        var ports = SerialPort.GetPortNames().ToList();
        if (!string.IsNullOrWhiteSpace(selected) && !ports.Contains(selected, StringComparer.OrdinalIgnoreCase)) ports.Add(selected);
        Ports.Clear();
        foreach (var port in ports.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) Ports.Add(port);
        SelectedPort = selected;
    }
    public async Task SimulateButtonAsync(string bits)
    {
        if (!IsSimulation || !IsConnected) { StatusMessage = "Előbb csatlakozz szimulációs módban."; return; }
        _simulator.ButtonBits = bits; await Task.Delay(35); _simulator.ButtonBits = "00000000";
    }
    public void SimulateMalformed() => _simulator.SendMalformedNext = true;
    public void SimulateNack() => _simulator.NackDisplayCommands = !_simulator.NackDisplayCommands;
    public void SimulateConnectionLoss() => _simulator.DropResponses = true;

    public bool BeginSeek() => _timeline.BeginSeek();
    public void PreviewSeek(double positionSeconds) => _timeline.Preview(positionSeconds);
    public Task CompleteSeekAsync() => ExecuteSeekAsync(_timeline.CompleteSeekAsync);
    public Task SeekToFractionAsync(double fraction) => ExecuteSeekAsync(ct => _timeline.SeekToFractionAsync(fraction, ct));
    private async Task ExecuteSeekAsync(Func<CancellationToken, Task> operation)
    {
        try { await operation(CancellationToken.None); }
        catch (Exception ex)
        {
            _debugLog.Write(RemoteDebugLogKind.Error, "Playback seek failed: " + ex.Message);
            StatusMessage = "Nem állítható a lejátszási pozíció: " + ex.Message;
        }
        finally
        {
            RefreshTimelineFromPlayback();
            UpdateRemoteDisplay();
        }
    }

    private async Task ExecuteTransportAsync(Func<CancellationToken, Task> operation)
    {
        try { await operation(CancellationToken.None); }
        catch (Exception ex) { _debugLog.Write(RemoteDebugLogKind.Error, "Playback command failed: " + ex.Message); StatusMessage = "Nem játszható le: " + ex.Message; }
    }
    private void Stop() { _transport.Stop(); RefreshTimelineFromPlayback(); UpdateRemoteDisplay(); StatusMessage = "Leállítva"; }
    private void Pause() => _transport.Pause();
    private void RemoveSelected() { if (SelectedTrack is null) return; if (PlayingTrack?.Id == SelectedTrack.Id) Stop(); var index = Playlist.IndexOf(SelectedTrack); Playlist.Remove(SelectedTrack); SelectedTrack = Playlist.Count == 0 ? null : Playlist[Math.Min(index, Playlist.Count - 1)]; SetPlaylistModified(true); _ = SaveAsync(); }
    private void MoveSelected(int direction) { if (SelectedTrack is null) return; var old = Playlist.IndexOf(SelectedTrack); var next = old + direction; if (next < 0 || next >= Playlist.Count) return; Playlist.Move(old, next); SetPlaylistModified(true); _ = SaveAsync(); RaiseTrackProperties(); }
    private async Task ConnectAsync()
    {
        if (!IsSimulation && string.IsNullOrWhiteSpace(SelectedPort)) { StatusMessage = "Válassz COM-portot."; return; }
        await SaveAsync();
        var connected = await _connectionCoordinator.ManualConnectAsync();
        if (!connected) StatusMessage = "Kapcsolódási hiba: " + _remote.LastResponse;
    }
    private Task DisconnectAsync() => _connectionCoordinator.ManualDisconnectAsync();
    private ISerialTransport CreateRemoteTransport(bool simulation)
    {
        if (!simulation) return new SerialPortTransport();
        _simulator = new SimulatedRemoteTransport();
        return _simulator;
    }
    private void RemoteButtonPressed(object? sender, RemoteButton button) => OnUi(() =>
    {
        switch (button)
        {
            case RemoteButton.Start: _ = ExecuteTransportAsync(_transport.PlayAsync); break;
            case RemoteButton.Stop: Stop(); break;
            case RemoteButton.Pause: Pause(); break;
            case RemoteButton.Previous: _ = ExecuteTransportAsync(ct => _transport.PreviousAsync(TransportCommandSource.Remote, ct)); break;
            case RemoteButton.Next: _ = ExecuteTransportAsync(ct => _transport.NextAsync(TransportCommandSource.Remote, ct)); break;
        }
    });
    private void PlaybackChanged(object? sender, EventArgs e) => OnUi(() =>
    {
        var current = _playback.State;
        if (_lastLoggedPlaybackState != current)
        {
            _debugLog.Write(RemoteDebugLogKind.Playback, $"{_lastLoggedPlaybackState} -> {current}");
            _lastLoggedPlaybackState = current;
        }
        PlayingIndicatorVisible = current != PlaybackState.Stopped;
        OnPropertyChanged(nameof(PlaybackGlyph));
        OnPropertyChanged(nameof(StateShort));
        RaiseTrackProperties();
        RefreshTimelineFromPlayback();
        UpdateRemoteDisplay();
    });
    private void PlaybackPositionChanged(object? sender, EventArgs e) => OnUi(() => { RefreshTimelineFromPlayback(); UpdateRemoteDisplay(); });
    private void UpdateRemoteDisplay() { var track = DisplayedTrack; _remote.UpdateDisplay(track is null ? 0 : Playlist.IndexOf(track) + 1, track is null ? "" : Path.GetFileNameWithoutExtension(track.FilePath), _playback.State, TimeSpan.FromSeconds(_timeline.PositionSeconds)); }
    private void UpdateRemoteStatus() { IsConnected = _remote.ConnectionState == RemoteConnectionState.Connected; var status = RemoteStatusPresentation.From(_remote.ConnectionState, _remote.LastResponse); ConnectionLabel = status.Text; RemoteStatusColor = status.Color; RemoteStatusDetail = status.Detail; ConnectionDetail = $"{(IsSimulation ? "SIM" : SelectedPort ?? "—")}   {RemoteSerialDefaults.BaudRate} baud"; LastResponse = _remote.LastResponse; }
    private void FlushDiagnostics()
    {
        var batch = _debugLog.Drain(MaximumDiagnosticBatchSize);
        if (batch.Count == 0) return;
        var overflow = Diagnostics.Count + batch.Count - MaximumDiagnosticEntries;
        for (var i = 0; i < overflow; i++) Diagnostics.RemoveAt(0);
        foreach (var entry in batch)
        {
            Diagnostics.Add(entry);
            if (entry.Kind == RemoteDebugLogKind.Rx) LastResponse = entry.Message;
        }
        DiagnosticsFlushed?.Invoke(this, EventArgs.Empty);
    }
    private void SetPlaylistModified(bool modified) { if (modified) _playlistChanges.MarkModified(); else _playlistChanges.MarkSaved(); IsPlaylistModified = _playlistChanges.IsModified; }
    private void RaiseTrackProperties() { OnPropertyChanged(nameof(DisplayedTrack)); OnPropertyChanged(nameof(NowPlayingFilename)); OnPropertyChanged(nameof(SelectedTitle)); OnPropertyChanged(nameof(SelectedPosition)); OnPropertyChanged(nameof(LcdLine1)); OnPropertyChanged(nameof(LcdLine2)); OnPropertyChanged(nameof(IsSeekEnabled)); }
    private void ResetTimelineForDisplayedTrack() => _timeline.Reset(DisplayedTrack?.Duration ?? TimeSpan.Zero);
    private void RefreshTimelineFromPlayback()
    {
        var track = DisplayedTrack;
        if (track is null) { _timeline.Reset(TimeSpan.Zero); return; }
        var isDisplayedTrackLoaded = !string.IsNullOrWhiteSpace(_playback.LoadedFilePath) &&
            string.Equals(Path.GetFullPath(_playback.LoadedFilePath), Path.GetFullPath(track.FilePath), StringComparison.OrdinalIgnoreCase);
        var duration = isDisplayedTrackLoaded && _playback.Duration > TimeSpan.Zero
            ? _playback.Duration
            : track.Duration ?? TimeSpan.Zero;
        var position = isDisplayedTrackLoaded ? _playback.Position : TimeSpan.Zero;
        _timeline.Synchronize(position, duration);
    }
    private void ApplyTimelineState()
    {
        CurrentTime = Format(TimeSpan.FromSeconds(_timeline.PositionSeconds));
        TotalTime = Format(TimeSpan.FromSeconds(_timeline.DurationSeconds));
        OnPropertyChanged(nameof(SeekPositionSeconds));
        OnPropertyChanged(nameof(SeekDurationSeconds));
        OnPropertyChanged(nameof(IsSeeking));
        OnPropertyChanged(nameof(IsSeekEnabled));
        OnPropertyChanged(nameof(LcdLine2));
    }
    private static string Format(TimeSpan time) => PlaybackTimeFormatter.Format(time);
    private static void OnUi(Action action) { var dispatcher = Application.Current?.Dispatcher; if (dispatcher is null || dispatcher.CheckAccess()) action(); else dispatcher.BeginInvoke(action); }
    private async Task SaveAsync()
    {
        var snapshot = new AppSettings { PlaylistName = PlaylistName, LastComPort = SelectedPort, AutoConnect = AutoConnect, AutoReconnect = AutoReconnect, Volume = (float)(Volume / 100), AudioOutputDeviceNumber = SelectedAudioDevice?.DeviceNumber ?? -1, AudioOutputDeviceName = SelectedAudioDevice?.Name ?? "Alapértelmezett Windows audio", Playlist = Playlist.ToList() };
        await _saveGate.WaitAsync();
        try { await _settings.SaveAsync(snapshot); }
        finally { _saveGate.Release(); }
    }
    public async ValueTask DisposeAsync() { _clock.Stop(); _playingBlink.Stop(); _debugUiTimer.Stop(); await _connectionCoordinator.DisposeAsync(); await SaveAsync(); await _remote.DisposeAsync(); await _playback.DisposeAsync(); _saveGate.Dispose(); }
}

internal static class Protocol
{
    public static string ProtocolTextForLcd(string text, int max) { var clean = global::TajpanShowController.Core.Protocol.ProtocolCodec.SanitizeTrackName(text).ToUpperInvariant(); return clean.Length <= max ? clean : clean[..max]; }
}
