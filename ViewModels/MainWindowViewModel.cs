using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private readonly ISettingsStore _settings;
    private readonly RemoteControllerService _remote;
    private readonly SimulatedRemoteTransport _simulator = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private AppSettings _loadedSettings = new();

    [ObservableProperty] private PlaylistTrack? selectedTrack;
    [ObservableProperty] private PlaylistTrack? playingTrack;
    [ObservableProperty] private string currentTime = "00:00";
    [ObservableProperty] private string totalTime = "00:00";
    [ObservableProperty] private double progress;
    [ObservableProperty] private double volume = 75;
    [ObservableProperty] private string systemClock = DateTime.Now.ToString("yyyy. MM. dd.  HH:mm:ss");
    [ObservableProperty] private string connectionLabel = "Nincs csatlakoztatva";
    [ObservableProperty] private string connectionDetail = "—   192000 baud";
    [ObservableProperty] private string lastResponse = "—";
    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private bool isSimulation;
    [ObservableProperty] private string? selectedPort;
    [ObservableProperty] private string statusMessage = "Készen áll";
    [ObservableProperty] private bool settingsVisible;

    public ObservableCollection<PlaylistTrack> Playlist { get; } = [];
    public ObservableCollection<string> Ports { get; } = [];
    public ObservableCollection<string> Diagnostics { get; } = [];
    public string SelectedTitle => SelectedTrack?.Title ?? "Nincs kiválasztott szám";
    public string SelectedPosition => SelectedTrack is null ? "0 / 0" : $"{Playlist.IndexOf(SelectedTrack) + 1} / {Playlist.Count}";
    public string PlaybackGlyph => _playback.State == PlaybackState.Playing ? "Ⅱ" : "▶";
    public string LcdLine1 => SelectedTrack is null ? "00 NINCS KIJEL." : $"{Playlist.IndexOf(SelectedTrack) + 1:00} {Protocol.ProtocolTextForLcd(SelectedTrack.Title, 13)}";
    public string LcdLine2 => $"{StateShort,-6}{CurrentTime,8}";
    public string StateShort => _playback.State switch { PlaybackState.Playing => "PLAY", PlaybackState.Paused => "PAUSE", _ => "STOP" };
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
        _remote = new RemoteControllerService(sim => sim ? _simulator : new SerialPortTransport());
        PlayCommand = new AsyncRelayCommand(PlaySelectedAsync);
        StopCommand = new RelayCommand(Stop);
        PauseCommand = new RelayCommand(PauseOrResume);
        PreviousCommand = new RelayCommand(Previous);
        NextCommand = new RelayCommand(Next);
        RemoveCommand = new RelayCommand(RemoveSelected);
        MoveUpCommand = new RelayCommand(() => MoveSelected(-1));
        MoveDownCommand = new RelayCommand(() => MoveSelected(1));
        ConnectCommand = new AsyncRelayCommand(ConnectAsync);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync);
        _playback.StateChanged += PlaybackChanged;
        _playback.PositionChanged += PlaybackPositionChanged;
        _playback.PlaybackFailed += (_, ex) => OnUi(() => StatusMessage = "Lejátszási hiba: " + ex.Message);
        _playback.PlaybackCompleted += (_, _) => OnUi(Next);
        _remote.ButtonPressed += RemoteButtonPressed;
        _remote.StatusChanged += (_, _) => OnUi(UpdateRemoteStatus);
        _clock.Tick += (_, _) => SystemClock = DateTime.Now.ToString("yyyy. MM. dd.  HH:mm:ss");
        _clock.Start();
    }

    public async Task InitializeAsync()
    {
        _loadedSettings = await _settings.LoadAsync();
        foreach (var track in _loadedSettings.Playlist.Where(t => !string.IsNullOrWhiteSpace(t.FilePath))) Playlist.Add(track);
        Volume = Math.Clamp(_loadedSettings.Volume * 100, 0, 100); _playback.Volume = (float)(Volume / 100);
        SelectedPort = _loadedSettings.LastComPort; RefreshPorts();
        SelectedTrack = Playlist.FirstOrDefault(); RaiseTrackProperties();
    }

    partial void OnSelectedTrackChanged(PlaylistTrack? value) { RaiseTrackProperties(); UpdateRemoteDisplay(); }
    partial void OnVolumeChanged(double value) { _playback.Volume = (float)Math.Clamp(value / 100, 0, 1); }

    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(File.Exists))
        {
            if (Playlist.Any(t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase))) continue;
            Playlist.Add(new PlaylistTrack { FilePath = path, Title = Path.GetFileNameWithoutExtension(path), Duration = AudioMetadataReader.TryGetDuration(path) });
        }
        SelectedTrack ??= Playlist.FirstOrDefault(); await SaveAsync(); RaiseTrackProperties();
    }
    public async Task ClearAsync() { Stop(); Playlist.Clear(); SelectedTrack = null; PlayingTrack = null; await SaveAsync(); RaiseTrackProperties(); }
    public void RefreshPorts() { var selected = SelectedPort; Ports.Clear(); foreach (var port in SerialPort.GetPortNames().OrderBy(x => x)) Ports.Add(port); if (selected is not null && Ports.Contains(selected)) SelectedPort = selected; else SelectedPort = Ports.FirstOrDefault(); }
    public async Task SimulateButtonAsync(string bits)
    {
        if (!IsSimulation || !IsConnected) { StatusMessage = "Előbb csatlakozz szimulációs módban."; return; }
        _simulator.ButtonBits = bits; await Task.Delay(35); _simulator.ButtonBits = "00000000";
    }
    public void SimulateMalformed() => _simulator.SendMalformedNext = true;
    public void SimulateNack() => _simulator.NackDisplayCommands = !_simulator.NackDisplayCommands;
    public void SimulateConnectionLoss() => _simulator.DropResponses = true;

    private async Task PlaySelectedAsync()
    {
        if (SelectedTrack is null) return;
        try
        {
            if (PlayingTrack?.Id == SelectedTrack.Id && _playback.State == PlaybackState.Playing) { _playback.Pause(); return; }
            if (PlayingTrack?.Id == SelectedTrack.Id && _playback.State == PlaybackState.Paused) { _playback.Resume(); return; }
            if (PlayingTrack?.Id != SelectedTrack.Id) { await _playback.LoadAsync(SelectedTrack.FilePath); PlayingTrack = SelectedTrack; }
            _playback.Play(); StatusMessage = "Lejátszás";
        }
        catch (Exception ex) { StatusMessage = "Nem játszható le: " + ex.Message; }
    }
    private void Stop() { _playback.Stop(); StatusMessage = "Leállítva"; }
    private void PauseOrResume() { if (_playback.State == PlaybackState.Playing) _playback.Pause(); else if (_playback.State == PlaybackState.Paused) _playback.Resume(); }
    private void Previous() { if (Playlist.Count == 0) return; SelectedTrack = Playlist[Math.Max(0, Playlist.IndexOf(SelectedTrack!) - 1)]; }
    private void Next() { if (Playlist.Count == 0) return; SelectedTrack = Playlist[Math.Min(Playlist.Count - 1, Math.Max(0, Playlist.IndexOf(SelectedTrack!)) + 1)]; }
    private void RemoveSelected() { if (SelectedTrack is null) return; if (PlayingTrack?.Id == SelectedTrack.Id) Stop(); var index = Playlist.IndexOf(SelectedTrack); Playlist.Remove(SelectedTrack); SelectedTrack = Playlist.Count == 0 ? null : Playlist[Math.Min(index, Playlist.Count - 1)]; _ = SaveAsync(); }
    private void MoveSelected(int direction) { if (SelectedTrack is null) return; var old = Playlist.IndexOf(SelectedTrack); var next = old + direction; if (next < 0 || next >= Playlist.Count) return; Playlist.Move(old, next); _ = SaveAsync(); RaiseTrackProperties(); }
    private async Task ConnectAsync()
    {
        if (!IsSimulation && string.IsNullOrWhiteSpace(SelectedPort)) { StatusMessage = "Válassz COM-portot."; return; }
        if (IsSimulation) { _simulator.DropResponses = false; _simulator.NackDisplayCommands = false; _simulator.SendMalformedNext = false; _simulator.ButtonBits = "00000000"; }
        try { await _remote.ConnectAsync(SelectedPort ?? "SIM", IsSimulation); _loadedSettings.LastComPort = SelectedPort; await SaveAsync(); UpdateRemoteDisplay(); }
        catch (Exception ex) { StatusMessage = "Kapcsolódási hiba: " + ex.Message; }
    }
    private Task DisconnectAsync() => _remote.DisconnectAsync();
    private void RemoteButtonPressed(object? sender, RemoteButton button) => OnUi(() => { switch (button) { case RemoteButton.Start: _ = PlaySelectedAsync(); break; case RemoteButton.Stop: Stop(); break; case RemoteButton.Pause: PauseOrResume(); break; case RemoteButton.Previous: Previous(); break; case RemoteButton.Next: Next(); break; } });
    private void PlaybackChanged(object? sender, EventArgs e) => OnUi(() => { OnPropertyChanged(nameof(PlaybackGlyph)); OnPropertyChanged(nameof(StateShort)); OnPropertyChanged(nameof(LcdLine2)); UpdateRemoteDisplay(); });
    private void PlaybackPositionChanged(object? sender, EventArgs e) => OnUi(() => { CurrentTime = Format(_playback.Position); TotalTime = Format(_playback.Duration); Progress = _playback.Duration.TotalMilliseconds <= 0 ? 0 : _playback.Position.TotalMilliseconds / _playback.Duration.TotalMilliseconds * 100; OnPropertyChanged(nameof(LcdLine2)); UpdateRemoteDisplay(); });
    private void UpdateRemoteDisplay() => _remote.UpdateDisplay(SelectedTrack is null ? 0 : Playlist.IndexOf(SelectedTrack) + 1, SelectedTrack?.Title ?? "", _playback.State, _playback.Position);
    private void UpdateRemoteStatus() { IsConnected = _remote.ConnectionState == RemoteConnectionState.Connected; ConnectionLabel = _remote.ConnectionState switch { RemoteConnectionState.Connected => "Csatlakoztatva", RemoteConnectionState.Fault => "REMOTE COMM ERROR", RemoteConnectionState.Connecting => "Kapcsolódás…", _ => "Nincs csatlakoztatva" }; ConnectionDetail = $"{(IsSimulation ? "SIM" : SelectedPort ?? "—")}   192000 baud"; LastResponse = _remote.LastResponse; AddDiagnostic($"{DateTime.Now:HH:mm:ss}  {ConnectionLabel}  {LastResponse}"); }
    private void AddDiagnostic(string text) { Diagnostics.Add(text); while (Diagnostics.Count > 100) Diagnostics.RemoveAt(0); }
    private void RaiseTrackProperties() { OnPropertyChanged(nameof(SelectedTitle)); OnPropertyChanged(nameof(SelectedPosition)); OnPropertyChanged(nameof(LcdLine1)); }
    private static string Format(TimeSpan time) => $"{Math.Clamp((int)time.TotalMinutes, 0, 99):00}:{time.Seconds:00}";
    private static void OnUi(Action action) { var dispatcher = Application.Current?.Dispatcher; if (dispatcher is null || dispatcher.CheckAccess()) action(); else dispatcher.BeginInvoke(action); }
    private Task SaveAsync() => _settings.SaveAsync(new AppSettings { LastComPort = SelectedPort, Volume = (float)(Volume / 100), Playlist = Playlist.ToList() });
    public async ValueTask DisposeAsync() { _clock.Stop(); await SaveAsync(); await _remote.DisposeAsync(); await _playback.DisposeAsync(); }
}

internal static class Protocol
{
    public static string ProtocolTextForLcd(string text, int max) { var clean = global::TajpanShowController.Core.Protocol.ProtocolCodec.SanitizeTrackName(text).ToUpperInvariant(); return clean.Length <= max ? clean : clean[..max]; }
}
