using System.Diagnostics;
using System.Text;
using System.Collections.Concurrent;
using System.Threading.Channels;
using TajpanShowController.Core.Diagnostics;
using TajpanShowController.Core.Interfaces;
using TajpanShowController.Core.Models;
using TajpanShowController.Core.Protocol;
using TajpanShowController.Core.Services;

namespace TajpanShowController.Infrastructure.Serial;

public sealed class RemoteControllerService : IRemoteControllerService
{
    private readonly Func<bool, ISerialTransport> _transportFactory;
    private readonly RemoteButtonEdgeDetector _edges = new();
    private readonly RemoteDebugLogBuffer _debugLog;
    private readonly Channel<ButtonCommand> _buttonCommands = Channel.CreateUnbounded<ButtonCommand>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
    private readonly Task _buttonCommandWorker;
    private readonly object _displayGate = new();
    private readonly SemaphoreSlim _txGate = new(1, 1);
    private readonly ConcurrentQueue<long> _pollTimestamps = new();
    private ISerialTransport? _transport;
    private CancellationTokenSource? _workerCts;
    private Task? _worker;
    private DisplaySnapshot _wanted = new(0, "", RemoteDisplayState.Stopped, TimeSpan.Zero);
    private DisplaySnapshot _sent = new(-1, "\0", (RemoteDisplayState)(-1), TimeSpan.MinValue);
    private RemoteButtonState? _lastLoggedButtons;
    private int _displayCursor;
    private long _lastDisplaySendTimestamp;
    private int _connectionGeneration;
    private bool _buttonBaselinePending;
    private long _lastValidResponseTimestamp;

    public const int MaxAttempts = 3;
    public static readonly TimeSpan PollPeriod = TimeSpan.FromMilliseconds(20);
    public static readonly TimeSpan RemoteDisconnectTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan WatchdogPeriod = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan DisplayHousekeepingPeriod = TimeSpan.FromMilliseconds(40);
    // Keep separate response budgets for state polling and display housekeeping.
    public static readonly TimeSpan PollResponseTimeout = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan DisplayResponseTimeout = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan SimulationResponseTimeout = TimeSpan.FromMilliseconds(8);
    public RemoteCommunicationMetrics TimingMetrics { get; } = new();
    public RemoteConnectionState ConnectionState { get; private set; }
    public string LastResponse { get; private set; } = "—";
    public TimeSpan? TimeSinceLastValidResponse
    {
        get
        {
            var timestamp = Volatile.Read(ref _lastValidResponseTimestamp);
            return timestamp == 0 ? null : Stopwatch.GetElapsedTime(timestamp);
        }
    }
    public RemoteDebugLogBuffer DebugLog => _debugLog;
    public event EventHandler<RemoteButton>? ButtonPressed;
    public event EventHandler? StatusChanged;

    public RemoteControllerService(
        Func<bool, ISerialTransport> transportFactory,
        RemoteDebugLogBuffer? debugLog = null)
    {
        _transportFactory = transportFactory;
        _debugLog = debugLog ?? new RemoteDebugLogBuffer();
        _buttonCommandWorker = Task.Run(DispatchButtonCommandsAsync);
    }

    public async Task ConnectAsync(string portName, bool simulation, CancellationToken cancellationToken = default)
    {
        await DisconnectAsync(cancellationToken).ConfigureAwait(false);
        SetConnectionState(RemoteConnectionState.Connecting);
        try
        {
            var transport = _transportFactory(simulation);
            _transport = transport;
            await transport.OpenAsync(portName, cancellationToken).ConfigureAwait(false);
            lock (_displayGate) _sent = new(-1, "\0", (RemoteDisplayState)(-1), TimeSpan.MinValue);
            _displayCursor = 0;
            Volatile.Write(ref _lastDisplaySendTimestamp, 0);
            _lastLoggedButtons = null;
            _buttonBaselinePending = true;
            while (_pollTimestamps.TryDequeue(out _)) { }
            // The watchdog also covers the initial handshake: until the first
            // valid frame arrives, the connection age is the RX age baseline.
            Volatile.Write(ref _lastValidResponseTimestamp, Stopwatch.GetTimestamp());
            _workerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var generation = Interlocked.Increment(ref _connectionGeneration);
            var pollResponseTimeout = simulation ? SimulationResponseTimeout : PollResponseTimeout;
            _worker = Task.Run(() => RunAsync(
                transport, new StreamingProtocolParser(), generation,
                pollResponseTimeout, _workerCts.Token));
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
        Interlocked.Increment(ref _connectionGeneration); // Immediately makes late callbacks from the old worker stale.
        SetConnectionState(RemoteConnectionState.Disconnected);
        var workerCts = _workerCts;
        var worker = _worker;
        var transport = _transport;
        workerCts?.Cancel();
        if (worker is not null)
        {
            try { await worker.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { _debugLog.Write(RemoteDebugLogKind.Warning, "Remote worker shutdown timeout"); }
        }
        if (transport is not null)
        {
            try { await transport.CloseAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _debugLog.Write(RemoteDebugLogKind.Error, "SerialPort close failed: " + ex.Message); }
            try { await transport.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _debugLog.Write(RemoteDebugLogKind.Error, "SerialPort dispose failed: " + ex.Message); }
        }
        workerCts?.Dispose();
        if (ReferenceEquals(_workerCts, workerCts)) _workerCts = null;
        if (ReferenceEquals(_worker, worker)) _worker = null;
        if (ReferenceEquals(_transport, transport)) _transport = null;
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

    private async Task RunAsync(
        ISerialTransport transport,
        StreamingProtocolParser parser,
        int generation,
        TimeSpan pollResponseTimeout,
        CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var nextPoll = TimeSpan.Zero;
        using var receiverCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var displaySignals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        var receiver = Task.Run(() => ReceiveFramesAsync(
            transport, parser, generation, pollResponseTimeout, displaySignals.Writer, receiverCts.Token), receiverCts.Token);
        var displayWorker = Task.Run(() => RunDisplayWorkerAsync(
            transport, generation, displaySignals.Reader, receiverCts.Token), receiverCts.Token);
        using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var watchdog = Task.Run(() => RunConnectionWatchdogAsync(generation, watchdogCts.Token), watchdogCts.Token);
        try
        {
            while (!token.IsCancellationRequested && transport.IsOpen)
            {
                var wait = nextPoll - clock.Elapsed;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
                else TimingMetrics.RecordScheduleDelay(-wait);
                nextPoll += PollPeriod;
                var pollSentTimestamp = Stopwatch.GetTimestamp();
                _pollTimestamps.Enqueue(pollSentTimestamp);
                await WriteFrameAsync(transport, ProtocolCodec.Poll(), token, publishImmediately: false).ConfigureAwait(false);
                TimingMetrics.RecordPollSent(pollSentTimestamp);
                if (token.IsCancellationRequested || !IsCurrentGeneration(generation)) return;
                if (ConnectionState == RemoteConnectionState.Fault) break;
                if (clock.Elapsed > nextPoll + PollPeriod) nextPoll = clock.Elapsed;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsCurrentGeneration(generation)) return;
            LastResponse = ex.Message;
            _debugLog.Write(RemoteDebugLogKind.Error, "SerialPort exception: " + ex.Message);
            SetConnectionState(RemoteConnectionState.Fault, generation);
        }
        finally
        {
            receiverCts.Cancel();
            displaySignals.Writer.TryComplete();
            watchdogCts.Cancel();
            try { await receiver.ConfigureAwait(false); }
            catch (OperationCanceledException) when (receiverCts.IsCancellationRequested) { }
            try { await displayWorker.ConfigureAwait(false); }
            catch (OperationCanceledException) when (receiverCts.IsCancellationRequested) { }
            try { await watchdog.ConfigureAwait(false); }
            catch (OperationCanceledException) when (watchdogCts.IsCancellationRequested) { }
            if (!token.IsCancellationRequested && ConnectionState != RemoteConnectionState.Fault)
                SetConnectionState(RemoteConnectionState.Disconnected, generation);
        }
    }

    private async Task ReceiveFramesAsync(
        ISerialTransport transport,
        StreamingProtocolParser parser,
        int generation,
        TimeSpan responseTimeout,
        ChannelWriter<bool> displaySignals,
        CancellationToken token)
    {
        var buffer = new byte[256];
        try
        {
            while (!token.IsCancellationRequested && transport.IsOpen)
            {
                var count = await transport.ReadAsync(buffer, token).ConfigureAwait(false);
                if (count <= 0) continue;
                var bytesTimestamp = Stopwatch.GetTimestamp();
                var frames = parser.Append(buffer.AsSpan(0, count));
                if (frames.Count == 0) continue;
                var parsedTimestamp = Stopwatch.GetTimestamp();
                foreach (var frame in frames)
                {
                    if (!IsCurrentGeneration(generation)) return;
                    if (frame.Kind == ProtocolFrameKind.Buttons)
                    {
                        var pollSent = _pollTimestamps.TryDequeue(out var queued) ? queued : parsedTimestamp;
                        Volatile.Write(ref _lastValidResponseTimestamp, parsedTimestamp);
                        TimingMetrics.RecordValidResponse(parsedTimestamp);
                        TimingMetrics.RecordPoll(pollSent, bytesTimestamp, parsedTimestamp, 0, parsedTimestamp, false, responseTimeout);
                        LastResponse = frame.Raw;
                        var buttons = frame.GetButtons();
                        var changed = !_lastLoggedButtons.HasValue || _lastLoggedButtons.Value != buttons;
                        var entries = new List<RemoteDebugLogEntry> { new(DateTimeOffset.Now, RemoteDebugLogKind.Rx, frame.Raw, $"Buttons={frame.Payload}") };
                        var ack = await WriteFrameAsync(transport, ProtocolCodec.Ack(), token, publishImmediately: false).ConfigureAwait(false);
                        entries.Add(ack);
                        if (changed && !_buttonBaselinePending) AddButtonChanges(entries, _lastLoggedButtons ?? default, buttons, DateTimeOffset.Now);
                        if (changed) _debugLog.WriteRange(entries);
                        SetConnectionState(RemoteConnectionState.Connected, generation);
                        displaySignals.TryWrite(true);
                        _lastLoggedButtons = buttons;
                        if (_buttonBaselinePending) { _edges.Synchronize(buttons); _buttonBaselinePending = false; }
                        else foreach (var button in _edges.Update(buttons)) _buttonCommands.Writer.TryWrite(new ButtonCommand(generation, button));
                    }
                    else if (frame.Kind == ProtocolFrameKind.Unknown)
                    {
                        _debugLog.Write(RemoteDebugLogKind.Error, "Invalid frame: " + DisplayRaw(frame.Raw));
                    }
                    else
                    {
                        _debugLog.Write(RemoteDebugLogKind.Rx, frame.Raw, $"Housekeeping {frame.Kind} response");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsCurrentGeneration(generation)) return;
            LastResponse = ex.Message;
            _debugLog.Write(RemoteDebugLogKind.Error, "SerialPort exception: " + ex.Message);
            SetConnectionState(RemoteConnectionState.Fault, generation);
        }
    }

    private async Task RunDisplayWorkerAsync(
        ISerialTransport transport,
        int generation,
        ChannelReader<bool> signals,
        CancellationToken token)
    {
        await foreach (var _ in signals.ReadAllAsync(token).ConfigureAwait(false))
        {
            if (!IsCurrentGeneration(generation)) return;
            await SendOnePendingDisplayAsync(transport, generation, token).ConfigureAwait(false);
        }
    }

    private async Task RunConnectionWatchdogAsync(int generation, CancellationToken token)
    {
        using var timer = new PeriodicTimer(WatchdogPeriod);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (!IsCurrentGeneration(generation)) return;
                var timestamp = Volatile.Read(ref _lastValidResponseTimestamp);
                if (timestamp == 0) continue;
                if (Stopwatch.GetElapsedTime(timestamp) >= RemoteDisconnectTimeout &&
                    ConnectionState != RemoteConnectionState.Disconnected)
                {
                    TimingMetrics.RecordTimeout();
                    _debugLog.Write(RemoteDebugLogKind.Warning, "Poll timeout");
                    SetConnectionState(RemoteConnectionState.Disconnected, generation);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task PollOnceAsync(
        ISerialTransport transport,
        StreamingProtocolParser parser,
        int generation,
        TimeSpan responseTimeout,
        CancellationToken token)
    {
        var pollTx = await WriteFrameAsync(transport, ProtocolCodec.Poll(), token, publishImmediately: false).ConfigureAwait(false);
        var pollSentTimestamp = Stopwatch.GetTimestamp();
        TimingMetrics.RecordPollSent(pollSentTimestamp);
        var read = await ReadFrameAsync(transport, parser, ProtocolFrameKind.Buttons, responseTimeout, token).ConfigureAwait(false);
        var responseFinishedTimestamp = Stopwatch.GetTimestamp();
        if (!IsCurrentGeneration(generation)) return;
        if (read?.Selected.Kind == ProtocolFrameKind.Buttons)
        {
            var buttons = read.Selected.GetButtons();
            var changed = !_lastLoggedButtons.HasValue || _lastLoggedButtons.Value != buttons;
            var hasUnexpectedFrames = read.Frames.Count > 1;

            // Communication health is committed as soon as a valid button frame is parsed.
            // It must not wait for logging, UI dispatch, or playback triggered by the button.
            Volatile.Write(ref _lastValidResponseTimestamp, read.ParsedTimestamp);
            TimingMetrics.RecordValidResponse(read.ParsedTimestamp);
            LastResponse = read.Selected.Raw;
            var ackTx = await WriteFrameAsync(transport, ProtocolCodec.Ack(), token, publishImmediately: false).ConfigureAwait(false);
            var ackSentTimestamp = Stopwatch.GetTimestamp();
            TimingMetrics.RecordPoll(
                pollSentTimestamp,
                read.BytesReceivedTimestamp,
                read.ParsedTimestamp,
                ackSentTimestamp,
                ackSentTimestamp,
                timedOut: false,
                responseTimeout);
            SetConnectionState(RemoteConnectionState.Connected, generation);

            if (changed || hasUnexpectedFrames)
            {
                var entries = new List<RemoteDebugLogEntry> { pollTx };
                AddReceivedFrames(entries, read, $"Buttons={read.Selected.Payload}");
                if (changed && !_buttonBaselinePending) AddButtonChanges(entries, _lastLoggedButtons ?? default, buttons, read.Timestamp);
                entries.Add(ackTx);
                _debugLog.WriteRange(entries);
            }

            _lastLoggedButtons = buttons;
            if (_buttonBaselinePending)
            {
                _edges.Synchronize(buttons);
                _buttonBaselinePending = false;
            }
            else
            {
                foreach (var button in _edges.Update(buttons)) _buttonCommands.Writer.TryWrite(new ButtonCommand(generation, button));
            }
            return;
        }

        var nackTx = await WriteFrameAsync(transport, ProtocolCodec.Nack(), token, publishImmediately: false).ConfigureAwait(false);
        TimingMetrics.RecordPoll(
            pollSentTimestamp,
            read?.BytesReceivedTimestamp ?? 0,
            read?.ParsedTimestamp ?? 0,
            0,
            responseFinishedTimestamp,
            timedOut: read is null,
            responseTimeout);
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
    }

    private async Task SendOnePendingDisplayAsync(
        ISerialTransport transport,
        int generation,
        CancellationToken token)
    {
        var lastDisplay = Volatile.Read(ref _lastDisplaySendTimestamp);
        if (lastDisplay != 0)
        {
            var elapsed = Stopwatch.GetElapsedTime(lastDisplay);
            if (elapsed < DisplayHousekeepingPeriod)
                await Task.Delay(DisplayHousekeepingPeriod - elapsed, token).ConfigureAwait(false);
        }
        if (!IsCurrentGeneration(generation) || ConnectionState != RemoteConnectionState.Connected) return;
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
            if (await SendDisplayAsync(transport, command, token).ConfigureAwait(false))
            {
                Volatile.Write(ref _lastDisplaySendTimestamp, Stopwatch.GetTimestamp());
                if (!IsCurrentGeneration(generation)) return;
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

    private async Task<bool> SendDisplayAsync(
        ISerialTransport transport,
        byte[] command,
        CancellationToken token)
    {
        // Display updates are housekeeping. Write them without synchronously
        // waiting for an ACK so a slow display response cannot postpone the
        // next 20 ms state poll. ACK/NACK frames are consumed by the polling
        // reader and do not affect the transport connection state.
        await WriteFrameAsync(transport, command, token).ConfigureAwait(false);
        return true;
    }

    private async ValueTask<RemoteDebugLogEntry> WriteFrameAsync(
        ISerialTransport transport,
        ReadOnlyMemory<byte> data,
        CancellationToken token,
        bool publishImmediately = true)
    {
        await _txGate.WaitAsync(token).ConfigureAwait(false);
        try { await transport.WriteAsync(data, token).ConfigureAwait(false); }
        finally { _txGate.Release(); }
        var entry = new RemoteDebugLogEntry(DateTimeOffset.Now, RemoteDebugLogKind.Tx, FrameText(data.Span));
        if (publishImmediately) _debugLog.Write(entry);
        return entry;
    }

    private async Task<FrameReadResult?> ReadFrameAsync(
        ISerialTransport transport,
        StreamingProtocolParser parser,
        ProtocolFrameKind expectedKind,
        TimeSpan timeout,
        CancellationToken token)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(timeout);
        var buffer = new byte[256];
        var firstBytesTimestamp = 0L;
        try
        {
            while (true)
            {
                var count = await transport.ReadAsync(buffer, timeoutCts.Token).ConfigureAwait(false);
                if (count <= 0) continue;
                if (firstBytesTimestamp == 0) firstBytesTimestamp = Stopwatch.GetTimestamp();
                var frames = parser.Append(buffer.AsSpan(0, count));
                if (frames.Count == 0) continue;
                var parsedTimestamp = Stopwatch.GetTimestamp();
                var selectedIndex = FindExpectedFrame(frames, expectedKind);
                if (frames.Any(frame => frame.Kind == expectedKind ||
                    expectedKind == ProtocolFrameKind.Ack && frame.Kind == ProtocolFrameKind.Nack ||
                    frame.Kind == ProtocolFrameKind.Unknown))
                    return new FrameReadResult(frames, selectedIndex, DateTimeOffset.Now, firstBytesTimestamp, parsedTimestamp);
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

    private bool IsCurrentGeneration(int generation) => generation == Volatile.Read(ref _connectionGeneration);

    private async Task DispatchButtonCommandsAsync()
    {
        await foreach (var command in _buttonCommands.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (!IsCurrentGeneration(command.Generation)) continue;
            try { ButtonPressed?.Invoke(this, command.Button); }
            catch (Exception ex) { _debugLog.Write(RemoteDebugLogKind.Error, "Remote button handler failed: " + ex.Message); }
        }
    }

    private void SetConnectionState(RemoteConnectionState state, int? generation = null)
    {
        if (generation.HasValue && !IsCurrentGeneration(generation.Value)) return;
        if (ConnectionState == state) return;
        var previous = ConnectionState;
        ConnectionState = state;
        _debugLog.Write(RemoteDebugLogKind.State, $"{previous} -> {state}");
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string FrameText(ReadOnlySpan<byte> data) => Encoding.ASCII.GetString(data).TrimEnd('\r', '\n');
    private static string DisplayRaw(string raw) => string.IsNullOrEmpty(raw) ? "<empty>" : raw;

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _buttonCommands.Writer.TryComplete();
        await _buttonCommandWorker.ConfigureAwait(false);
        _txGate.Dispose();
    }

    private sealed record DisplaySnapshot(int TrackNumber, string TrackName, RemoteDisplayState State, TimeSpan Position);
    private sealed record ButtonCommand(int Generation, RemoteButton Button);
    private sealed record FrameReadResult(
        IReadOnlyList<ProtocolFrame> Frames,
        int SelectedIndex,
        DateTimeOffset Timestamp,
        long BytesReceivedTimestamp,
        long ParsedTimestamp)
    {
        public ProtocolFrame Selected => Frames[SelectedIndex];
    }
}
