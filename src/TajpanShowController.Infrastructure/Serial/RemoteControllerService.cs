using System.Diagnostics;
using TajpanShowController.Core.Interfaces;
using TajpanShowController.Core.Models;
using TajpanShowController.Core.Protocol;
using TajpanShowController.Core.Services;

namespace TajpanShowController.Infrastructure.Serial;

public sealed class RemoteControllerService(Func<bool, ISerialTransport> transportFactory) : IRemoteControllerService
{
    private readonly StreamingProtocolParser _parser = new();
    private readonly RemoteButtonEdgeDetector _edges = new();
    private readonly object _displayGate = new();
    private ISerialTransport? _transport;
    private CancellationTokenSource? _workerCts;
    private Task? _worker;
    private DisplaySnapshot _wanted = new(0, "", RemoteDisplayState.Stopped, TimeSpan.Zero);
    private DisplaySnapshot _sent = new(-1, "\0", (RemoteDisplayState)(-1), TimeSpan.MinValue);
    private int _displayCursor;
    private int _controlFailures;

    public const int MaxAttempts = 3;
    public static readonly TimeSpan PollPeriod = TimeSpan.FromMilliseconds(10);
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromMilliseconds(8);
    public RemoteConnectionState ConnectionState { get; private set; }
    public string LastResponse { get; private set; } = "—";
    public event EventHandler<RemoteButton>? ButtonPressed;
    public event EventHandler? StatusChanged;

    public async Task ConnectAsync(string portName, bool simulation, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync(cancellationToken);
        ConnectionState = RemoteConnectionState.Connecting; RaiseStatus();
        _transport = transportFactory(simulation);
        await _transport.OpenAsync(portName, cancellationToken);
        lock (_displayGate) _sent = new(-1, "\0", (RemoteDisplayState)(-1), TimeSpan.MinValue);
        _workerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConnectionState = RemoteConnectionState.Connected; RaiseStatus();
        _worker = RunAsync(_workerCts.Token);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _workerCts?.Cancel();
        if (_worker is not null) try { await _worker.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken); } catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
        if (_transport is not null) { try { await _transport.CloseAsync(cancellationToken); } catch (OperationCanceledException) { } await _transport.DisposeAsync(); }
        _workerCts?.Dispose(); _workerCts = null; _worker = null; _transport = null;
        ConnectionState = RemoteConnectionState.Disconnected; RaiseStatus();
    }

    public void UpdateDisplay(int trackNumber, string trackName, PlaybackState state, TimeSpan position)
    {
        var remoteState = state switch { PlaybackState.Playing => RemoteDisplayState.Playing, PlaybackState.Paused => RemoteDisplayState.Paused, _ => RemoteDisplayState.Stopped };
        lock (_displayGate) _wanted = new(trackNumber, ProtocolCodec.SanitizeTrackName(trackName), remoteState, position);
    }

    private async Task RunAsync(CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var nextPoll = TimeSpan.Zero;
        try
        {
            while (!token.IsCancellationRequested && _transport?.IsOpen == true)
            {
                var wait = nextPoll - clock.Elapsed;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, token);
                nextPoll += PollPeriod;
                await PollOnceAsync(token);
                if (ConnectionState == RemoteConnectionState.Fault) break;
                await SendOnePendingDisplayAsync(token);
                if (clock.Elapsed > nextPoll + PollPeriod) nextPoll = clock.Elapsed;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LastResponse = ex.Message;
            ConnectionState = RemoteConnectionState.Fault; RaiseStatus();
        }
    }

    private async Task PollOnceAsync(CancellationToken token)
    {
        await _transport!.WriteAsync(ProtocolCodec.Poll(), token);
        var response = await ReadFrameAsync(ResponseTimeout, token);
        if (response?.Kind == ProtocolFrameKind.Buttons)
        {
            await _transport.WriteAsync(ProtocolCodec.Ack(), token);
            LastResponse = response.Raw; _controlFailures = 0;
            foreach (var button in _edges.Update(response.GetButtons())) ButtonPressed?.Invoke(this, button);
            RaiseStatus(); return;
        }
        await _transport.WriteAsync(ProtocolCodec.Nack(), token);
        LastResponse = response?.Raw ?? "TIMEOUT";
        if (++_controlFailures >= MaxAttempts) { ConnectionState = RemoteConnectionState.Fault; RaiseStatus(); }
    }

    private async Task SendOnePendingDisplayAsync(CancellationToken token)
    {
        DisplaySnapshot wanted, sent;
        lock (_displayGate) { wanted = _wanted; sent = _sent; }
        for (var i = 0; i < 4; i++)
        {
            var slot = (_displayCursor++ % 4);
            byte[]? command = slot switch
            {
                0 when wanted.TrackNumber != sent.TrackNumber => ProtocolCodec.TrackNumber(wanted.TrackNumber),
                1 when wanted.TrackName != sent.TrackName => ProtocolCodec.TrackName(wanted.TrackName),
                2 when wanted.State != sent.State => ProtocolCodec.State(wanted.State),
                3 when sent.Position == TimeSpan.MinValue || Math.Abs((wanted.Position - sent.Position).TotalMilliseconds) >= 100 => ProtocolCodec.Timecode(wanted.Position),
                _ => null
            };
            if (command is null) continue;
            if (await SendDisplayWithRetryAsync(command, token))
            {
                lock (_displayGate) _sent = slot switch { 0 => _sent with { TrackNumber = wanted.TrackNumber }, 1 => _sent with { TrackName = wanted.TrackName }, 2 => _sent with { State = wanted.State }, _ => _sent with { Position = wanted.Position } };
            }
            return;
        }
    }

    private async Task<bool> SendDisplayWithRetryAsync(byte[] command, CancellationToken token)
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            await _transport!.WriteAsync(command, token);
            var response = await ReadFrameAsync(ResponseTimeout, token);
            if (response?.Kind == ProtocolFrameKind.Ack) return true;
            if (attempt + 1 < MaxAttempts) return false; // retry remains pending for a later polling interval
        }
        return false;
    }

    private async Task<ProtocolFrame?> ReadFrameAsync(TimeSpan timeout, CancellationToken token)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(timeout);
        var buffer = new byte[256];
        try
        {
            while (true)
            {
                var count = await _transport!.ReadAsync(buffer, timeoutCts.Token);
                var frames = _parser.Append(buffer.AsSpan(0, count));
                if (frames.Count > 0) return frames[0];
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }

    private void RaiseStatus() => StatusChanged?.Invoke(this, EventArgs.Empty);
    public async ValueTask DisposeAsync() => await DisconnectAsync();
    private sealed record DisplaySnapshot(int TrackNumber, string TrackName, RemoteDisplayState State, TimeSpan Position);
}
