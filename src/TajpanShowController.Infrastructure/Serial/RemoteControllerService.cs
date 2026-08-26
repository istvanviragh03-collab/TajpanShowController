using System.Diagnostics;
using System.Text;
using TajpanShowController.Core.Diagnostics;
using TajpanShowController.Core.Interfaces;
using TajpanShowController.Core.Models;
using TajpanShowController.Core.Protocol;
using TajpanShowController.Core.Services;

namespace TajpanShowController.Infrastructure.Serial;

public sealed class RemoteControllerService(
    Func<bool, ISerialTransport> transportFactory,
    RemoteDebugLogBuffer? debugLog = null) : IRemoteControllerService
{
    private readonly StreamingProtocolParser _parser = new();
    private readonly RemoteButtonEdgeDetector _edges = new();
    private readonly RemoteDebugLogBuffer _debugLog = debugLog ?? new RemoteDebugLogBuffer();
    private readonly object _displayGate = new();
    private ISerialTransport? _transport;
    private CancellationTokenSource? _workerCts;
    private Task? _worker;
    private DisplaySnapshot _wanted = new(0, "", RemoteDisplayState.Stopped, TimeSpan.Zero);
    private DisplaySnapshot _sent = new(-1, "\0", (RemoteDisplayState)(-1), TimeSpan.MinValue);
    private RemoteButtonState? _lastLoggedButtons;
    private int _displayCursor;
    private int _controlFailures;

    public const int MaxAttempts = 3;
    public static readonly TimeSpan PollPeriod = TimeSpan.FromMilliseconds(10);
    public static readonly TimeSpan ResponseTimeout = TimeSpan.FromMilliseconds(8);
    public RemoteConnectionState ConnectionState { get; private set; }
    public string LastResponse { get; private set; } = "—";
    public RemoteDebugLogBuffer DebugLog => _debugLog;
    public event EventHandler<RemoteButton>? ButtonPressed;
    public event EventHandler? StatusChanged;

    public async Task ConnectAsync(string portName, bool simulation, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync(cancellationToken);
        SetConnectionState(RemoteConnectionState.Connecting);
        try
        {
            _transport = transportFactory(simulation);
            await _transport.OpenAsync(portName, cancellationToken);
            lock (_displayGate) _sent = new(-1, "\0", (RemoteDisplayState)(-1), TimeSpan.MinValue);
            _lastLoggedButtons = null;
            _controlFailures = 0;
            _workerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _worker = RunAsync(_workerCts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastResponse = ex.Message;
            _debugLog.Write(RemoteDebugLogKind.Error, "SerialPort open failed: " + ex.Message);
            SetConnectionState(RemoteConnectionState.Fault);
            throw;
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _workerCts?.Cancel();
        if (_worker is not null)
        {
            try { await _worker.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { _debugLog.Write(RemoteDebugLogKind.Warning, "Remote worker shutdown timeout"); }
        }
        if (_transport is not null)
        {
            try { await _transport.CloseAsync(cancellationToken); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _debugLog.Write(RemoteDebugLogKind.Error, "SerialPort close failed: " + ex.Message); throw; }
            try { await _transport.DisposeAsync(); }
            catch (Exception ex) { _debugLog.Write(RemoteDebugLogKind.Error, "SerialPort dispose failed: " + ex.Message); throw; }
        }
        _workerCts?.Dispose();
        _workerCts = null;
        _worker = null;
        _transport = null;
        SetConnectionState(RemoteConnectionState.Disconnected);
    }

    public void UpdateDisplay(int trackNumber, string trackName, PlaybackState state, TimeSpan position)
    {
        var remoteState = state switch
        {
            PlaybackState.Playing => RemoteDisplayState.Playing,
            PlaybackState.Paused => RemoteDisplayState.Paused,
            _ => RemoteDisplayState.Stopped
        };
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
                if (ConnectionState is RemoteConnectionState.Fault or RemoteConnectionState.Disconnected) break;
                await SendOnePendingDisplayAsync(token);
                if (clock.Elapsed > nextPoll + PollPeriod) nextPoll = clock.Elapsed;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            LastResponse = ex.Message;
            _debugLog.Write(RemoteDebugLogKind.Error, "SerialPort exception: " + ex.Message);
            SetConnectionState(RemoteConnectionState.Fault);
        }
        finally
        {
            if (!token.IsCancellationRequested && ConnectionState != RemoteConnectionState.Fault)
                SetConnectionState(RemoteConnectionState.Disconnected);
        }
    }

    private async Task PollOnceAsync(CancellationToken token)
    {
        var pollTx = await WriteFrameAsync(ProtocolCodec.Poll(), token, publishImmediately: false);
        var read = await ReadFrameAsync(ProtocolFrameKind.Buttons, ResponseTimeout, token);
        if (read?.Selected.Kind == ProtocolFrameKind.Buttons)
        {
            var buttons = read.Selected.GetButtons();
            var changed = !_lastLoggedButtons.HasValue || _lastLoggedButtons.Value != buttons;
            var hasUnexpectedFrames = read.Frames.Count > 1;
            var ackTx = await WriteFrameAsync(ProtocolCodec.Ack(), token, publishImmediately: false);

            if (changed || hasUnexpectedFrames)
            {
                var entries = new List<RemoteDebugLogEntry> { pollTx };
                AddReceivedFrames(entries, read, $"Buttons={read.Selected.Payload}");
                if (changed) AddButtonChanges(entries, _lastLoggedButtons ?? default, buttons, read.Timestamp);
                entries.Add(ackTx);
                _debugLog.WriteRange(entries);
            }

            _lastLoggedButtons = buttons;
            LastResponse = read.Selected.Raw;
            _controlFailures = 0;
            SetConnectionState(RemoteConnectionState.Connected);
            foreach (var button in _edges.Update(buttons)) ButtonPressed?.Invoke(this, button);
            return;
        }

        var nackTx = await WriteFrameAsync(ProtocolCodec.Nack(), token, publishImmediately: false);
        var failedEntries = new List<RemoteDebugLogEntry> { pollTx };
        if (read is null)
        {
            failedEntries.Add(new RemoteDebugLogEntry(DateTimeOffset.Now, RemoteDebugLogKind.Warning, "Poll timeout"));
            LastResponse = "TIMEOUT";
        }
        else
        {
            AddReceivedFrames(failedEntries, read);
            failedEntries.Add(new RemoteDebugLogEntry(DateTimeOffset.Now, RemoteDebugLogKind.Warning,
                $"Unexpected {read.Selected.Kind} response to poll"));
            LastResponse = read.Selected.Raw;
        }
        failedEntries.Add(nackTx);
        _debugLog.WriteRange(failedEntries);
        if (++_controlFailures >= MaxAttempts) SetConnectionState(RemoteConnectionState.Disconnected);
    }

    private async Task SendOnePendingDisplayAsync(CancellationToken token)
    {
        DisplaySnapshot wanted, sent;
        lock (_displayGate) { wanted = _wanted; sent = _sent; }
        for (var i = 0; i < 4; i++)
        {
            var slot = _displayCursor++ % 4;
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
                lock (_displayGate)
                {
                    _sent = slot switch
                    {
                        0 => _sent with { TrackNumber = wanted.TrackNumber },
                        1 => _sent with { TrackName = wanted.TrackName },
                        2 => _sent with { State = wanted.State },
                        _ => _sent with { Position = wanted.Position }
                    };
                }
            }
            return;
        }
    }

    private async Task<bool> SendDisplayWithRetryAsync(byte[] command, CancellationToken token)
    {
        var commandText = FrameText(command);
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            await WriteFrameAsync(command, token);
            var read = await ReadFrameAsync(ProtocolFrameKind.Ack, ResponseTimeout, token);
            if (read is null)
            {
                _debugLog.Write(RemoteDebugLogKind.Warning, $"Timeout waiting for ACK to {commandText}");
            }
            else
            {
                var receivedEntries = new List<RemoteDebugLogEntry>();
                AddReceivedFrames(receivedEntries, read);
                if (read.Selected.Kind == ProtocolFrameKind.Nack)
                    receivedEntries.Add(new RemoteDebugLogEntry(DateTimeOffset.Now, RemoteDebugLogKind.Warning, $"NACK for {commandText}"));
                else if (read.Selected.Kind != ProtocolFrameKind.Ack)
                    receivedEntries.Add(new RemoteDebugLogEntry(DateTimeOffset.Now, RemoteDebugLogKind.Warning,
                        $"Unexpected {read.Selected.Kind} response to {commandText}"));
                _debugLog.WriteRange(receivedEntries);
                if (read.Selected.Kind == ProtocolFrameKind.Ack) return true;
            }
            if (attempt + 1 < MaxAttempts) return false; // retry remains pending for a later polling interval
        }
        return false;
    }

    private async ValueTask<RemoteDebugLogEntry> WriteFrameAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken token,
        bool publishImmediately = true)
    {
        await _transport!.WriteAsync(data, token);
        var entry = new RemoteDebugLogEntry(DateTimeOffset.Now, RemoteDebugLogKind.Tx, FrameText(data.Span));
        if (publishImmediately) _debugLog.Write(entry);
        return entry;
    }

    private async Task<FrameReadResult?> ReadFrameAsync(
        ProtocolFrameKind expectedKind,
        TimeSpan timeout,
        CancellationToken token)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(timeout);
        var buffer = new byte[256];
        try
        {
            while (true)
            {
                var count = await _transport!.ReadAsync(buffer, timeoutCts.Token);
                if (count <= 0) continue;
                var frames = _parser.Append(buffer.AsSpan(0, count));
                if (frames.Count == 0) continue;
                var selectedIndex = FindExpectedFrame(frames, expectedKind);
                return new FrameReadResult(frames, selectedIndex, DateTimeOffset.Now);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }

    private static int FindExpectedFrame(IReadOnlyList<ProtocolFrame> frames, ProtocolFrameKind expectedKind)
    {
        for (var i = 0; i < frames.Count; i++)
            if (frames[i].Kind == expectedKind) return i;
        if (expectedKind == ProtocolFrameKind.Ack)
            for (var i = 0; i < frames.Count; i++)
                if (frames[i].Kind == ProtocolFrameKind.Nack) return i;
        return 0;
    }

    private static void AddReceivedFrames(
        ICollection<RemoteDebugLogEntry> entries,
        FrameReadResult read,
        string? selectedDescription = null)
    {
        for (var i = 0; i < read.Frames.Count; i++)
        {
            var frame = read.Frames[i];
            if (frame.Kind == ProtocolFrameKind.Unknown)
            {
                entries.Add(new RemoteDebugLogEntry(read.Timestamp, RemoteDebugLogKind.Error,
                    "Invalid frame: " + DisplayRaw(frame.Raw)));
            }
            else
            {
                entries.Add(new RemoteDebugLogEntry(read.Timestamp, RemoteDebugLogKind.Rx, frame.Raw,
                    i == read.SelectedIndex ? selectedDescription : $"Unexpected {frame.Kind} frame"));
            }
        }
    }

    private static void AddButtonChanges(
        ICollection<RemoteDebugLogEntry> entries,
        RemoteButtonState previous,
        RemoteButtonState current,
        DateTimeOffset timestamp)
    {
        AddButtonChange(entries, timestamp, "START", previous.Start, current.Start);
        AddButtonChange(entries, timestamp, "STOP", previous.Stop, current.Stop);
        AddButtonChange(entries, timestamp, "PAUSE", previous.Pause, current.Pause);
        AddButtonChange(entries, timestamp, "PREVIOUS", previous.Previous, current.Previous);
        AddButtonChange(entries, timestamp, "NEXT", previous.Next, current.Next);
        AddButtonChange(entries, timestamp, "RESERVED1", previous.Reserved1, current.Reserved1);
        AddButtonChange(entries, timestamp, "RESERVED2", previous.Reserved2, current.Reserved2);
        AddButtonChange(entries, timestamp, "RESERVED3", previous.Reserved3, current.Reserved3);
    }

    private static void AddButtonChange(
        ICollection<RemoteDebugLogEntry> entries,
        DateTimeOffset timestamp,
        string name,
        bool previous,
        bool current)
    {
        if (previous == current) return;
        entries.Add(new RemoteDebugLogEntry(timestamp, RemoteDebugLogKind.Event,
            $"Remote {name} {(current ? "pressed" : "released")}"));
    }

    private void SetConnectionState(RemoteConnectionState state)
    {
        if (ConnectionState == state) return;
        var previous = ConnectionState;
        ConnectionState = state;
        _debugLog.Write(RemoteDebugLogKind.State, $"{previous} -> {state}");
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string FrameText(ReadOnlySpan<byte> data) => Encoding.ASCII.GetString(data).TrimEnd('\r', '\n');
    private static string DisplayRaw(string raw) => string.IsNullOrEmpty(raw) ? "<empty>" : raw;

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    private sealed record DisplaySnapshot(int TrackNumber, string TrackName, RemoteDisplayState State, TimeSpan Position);
    private sealed record FrameReadResult(IReadOnlyList<ProtocolFrame> Frames, int SelectedIndex, DateTimeOffset Timestamp)
    {
        public ProtocolFrame Selected => Frames[SelectedIndex];
    }
}
