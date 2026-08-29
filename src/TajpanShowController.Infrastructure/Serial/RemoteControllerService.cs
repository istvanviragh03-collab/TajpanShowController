using System.Diagnostics;
using System.Text;
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
    private ISerialTransport? _transport;
    private CancellationTokenSource? _workerCts;
    private Task? _worker;
    private DisplaySnapshot _wanted = new(0, "", RemoteDisplayState.Stopped, TimeSpan.Zero);
    private DisplaySnapshot _sent = new(-1, "\0", (RemoteDisplayState)(-1), TimeSpan.MinValue);
    private RemoteButtonState? _lastLoggedButtons;
    private long _lastDisplaySendTimestamp;
    private long _displaySnapshotStartedTimestamp;
    private int _connectionGeneration;
    private bool _buttonBaselinePending;
    private long _lastValidResponseTimestamp;

    public const int MaxAttempts = 3;
    public static readonly TimeSpan PollPeriod = TimeSpan.FromMilliseconds(20);
    public static readonly TimeSpan RemoteDisconnectTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan WatchdogPeriod = TimeSpan.FromMilliseconds(5);
    private static readonly TimeSpan DisplayDeadlineGuard = TimeSpan.FromMilliseconds(2);
    private static readonly TimeSpan MinimumDisplayTransactionBudget = TimeSpan.FromMilliseconds(8);
    private static readonly TimeSpan DisplayTransactionTimeout = TimeSpan.FromMilliseconds(30);
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
            Volatile.Write(ref _displaySnapshotStartedTimestamp, Stopwatch.GetTimestamp());
            Volatile.Write(ref _lastDisplaySendTimestamp, 0);
            _lastLoggedButtons = null;
            _buttonBaselinePending = true;
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
        var wanted = new DisplaySnapshot(trackNumber, ProtocolCodec.SanitizeTrackName(trackName), remoteState, position);
        lock (_displayGate)
        {
            if (_wanted != wanted) Volatile.Write(ref _displaySnapshotStartedTimestamp, Stopwatch.GetTimestamp());
            _wanted = wanted;
        }
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
        var receivedFrames = Channel.CreateUnbounded<ReceivedFrame>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });
        var receiver = Task.Run(() => ReceiveFramesAsync(
            transport, parser, generation, receivedFrames.Writer, receiverCts.Token), receiverCts.Token);
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
                var pollTx = await WriteFrameAsync(
                    transport, ProtocolCodec.Poll(), token, publishImmediately: false).ConfigureAwait(false);
                TimingMetrics.RecordPollSent(pollSentTimestamp);
                var pollResponse = await ReadTransactionResponseAsync(
                    receivedFrames.Reader, ProtocolFrameKind.Buttons, pollResponseTimeout, token).ConfigureAwait(false);
                await CompletePollTransactionAsync(
                    transport, generation, pollTx, pollSentTimestamp, pollResponse, pollResponseTimeout, token).ConfigureAwait(false);
                if (token.IsCancellationRequested || !IsCurrentGeneration(generation)) return;
                if (ConnectionState == RemoteConnectionState.Fault) break;

                var displayBudget = nextPoll - clock.Elapsed - DisplayDeadlineGuard;
                if (displayBudget >= MinimumDisplayTransactionBudget && TryGetPendingDisplay(out var pendingDisplay))
                {
                    var displaySent = Stopwatch.GetTimestamp();
                    await WriteFrameAsync(transport, pendingDisplay.Command, token).ConfigureAwait(false);
                    var displayResponse = await ReadTransactionResponseAsync(
                        receivedFrames.Reader, ProtocolFrameKind.Ack, DisplayTransactionTimeout, token).ConfigureAwait(false);
                    if (displayResponse?.Frame.Kind == ProtocolFrameKind.Ack)
                    {
                        TimingMetrics.RecordDisplay(displaySent, displayResponse.Value.ParsedTimestamp);
                        MarkDisplaySent(pendingDisplay);
                        Volatile.Write(ref _lastDisplaySendTimestamp, displaySent);
                    }
                    else if (displayResponse?.Frame.Kind == ProtocolFrameKind.Nack)
                    {
                        _debugLog.Write(RemoteDebugLogKind.Warning, $"Display NACK: {FrameText(pendingDisplay.Command)}");
                    }
                    else
                    {
                        _debugLog.Write(RemoteDebugLogKind.Warning, $"Display response timeout: {FrameText(pendingDisplay.Command)}");
                    }
                }
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
            receivedFrames.Writer.TryComplete();
            watchdogCts.Cancel();
            try { await receiver.ConfigureAwait(false); }
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
        ChannelWriter<ReceivedFrame> framesWriter,
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
                    await framesWriter.WriteAsync(
                        new ReceivedFrame(frame, DateTimeOffset.Now, bytesTimestamp, parsedTimestamp), token).ConfigureAwait(false);
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
        finally { framesWriter.TryComplete(); }
    }

    private async Task<ReceivedFrame?> ReadTransactionResponseAsync(
        ChannelReader<ReceivedFrame> frames,
        ProtocolFrameKind expectedKind,
        TimeSpan timeout,
        CancellationToken token)
    {
        if (timeout <= TimeSpan.Zero) return null;
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(timeout);
        try
        {
            while (await frames.WaitToReadAsync(timeoutCts.Token).ConfigureAwait(false))
            {
                while (frames.TryRead(out var received))
                {
                    if (received.Frame.Kind == expectedKind ||
                        expectedKind == ProtocolFrameKind.Ack && received.Frame.Kind == ProtocolFrameKind.Nack)
                        return received;
                    if (received.Frame.Kind == ProtocolFrameKind.Unknown)
                        _debugLog.Write(RemoteDebugLogKind.Error, "Invalid frame: " + DisplayRaw(received.Frame.Raw));
                    else
                        _debugLog.Write(RemoteDebugLogKind.Rx, received.Frame.Raw,
                            $"Unexpected {received.Frame.Kind} while awaiting {expectedKind}");
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        return null;
    }

    private async Task CompletePollTransactionAsync(
        ISerialTransport transport,
        int generation,
        RemoteDebugLogEntry pollTx,
        long pollSentTimestamp,
        ReceivedFrame? received,
        TimeSpan responseTimeout,
        CancellationToken token)
    {
        var completedTimestamp = Stopwatch.GetTimestamp();
        if (received?.Frame.Kind != ProtocolFrameKind.Buttons)
        {
            TimingMetrics.RecordPoll(pollSentTimestamp, 0, 0, 0, completedTimestamp, true, responseTimeout);
            LastResponse = "TIMEOUT";
            _debugLog.Write(RemoteDebugLogKind.Warning, "Poll response timeout");
            return;
        }

        var valid = received.Value;
        Volatile.Write(ref _lastValidResponseTimestamp, valid.ParsedTimestamp);
        TimingMetrics.RecordValidResponse(valid.ParsedTimestamp);
        LastResponse = valid.Frame.Raw;
        var buttons = valid.Frame.GetButtons();
        var changed = !_lastLoggedButtons.HasValue || _lastLoggedButtons.Value != buttons;
        var entries = new List<RemoteDebugLogEntry>
        {
            pollTx,
            new(valid.Timestamp, RemoteDebugLogKind.Rx, valid.Frame.Raw, $"Buttons={valid.Frame.Payload}")
        };
        var ack = await WriteFrameAsync(transport, ProtocolCodec.Ack(), token, publishImmediately: false).ConfigureAwait(false);
        var ackTimestamp = Stopwatch.GetTimestamp();
        TimingMetrics.RecordPoll(
            pollSentTimestamp, valid.BytesReceivedTimestamp, valid.ParsedTimestamp,
            ackTimestamp, ackTimestamp, false, responseTimeout);
        entries.Add(ack);
        if (changed && !_buttonBaselinePending)
            AddButtonChanges(entries, _lastLoggedButtons ?? default, buttons, valid.Timestamp);
        if (changed) _debugLog.WriteRange(entries);
        SetConnectionState(RemoteConnectionState.Connected, generation);
        _lastLoggedButtons = buttons;
        if (_buttonBaselinePending)
        {
            _edges.Synchronize(buttons);
            _buttonBaselinePending = false;
        }
        else
        {
            foreach (var button in _edges.Update(buttons))
                _buttonCommands.Writer.TryWrite(new ButtonCommand(generation, button, valid.ParsedTimestamp));
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
                foreach (var button in _edges.Update(buttons))
                    _buttonCommands.Writer.TryWrite(new ButtonCommand(generation, button, read.ParsedTimestamp));
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

    private bool TryGetPendingDisplay(out PendingDisplay pending)
    {
        pending = default;
        if (ConnectionState != RemoteConnectionState.Connected) return false;
        DisplaySnapshot wanted, sent;
        lock (_displayGate) { wanted = _wanted; sent = _sent; }
        if (wanted.State != sent.State)
        {
            pending = new PendingDisplay(2, ProtocolCodec.State(wanted.State), wanted);
            return true;
        }
        if (wanted.TrackNumber != sent.TrackNumber)
        {
            pending = new PendingDisplay(0, ProtocolCodec.TrackNumber(wanted.TrackNumber), wanted);
            return true;
        }
        if (wanted.TrackName != sent.TrackName)
        {
            pending = new PendingDisplay(1, ProtocolCodec.TrackName(wanted.TrackName), wanted);
            return true;
        }
        if (sent.Position == TimeSpan.MinValue ||
            Math.Abs((wanted.Position - sent.Position).TotalMilliseconds) >= 100)
        {
            pending = new PendingDisplay(3, ProtocolCodec.Timecode(wanted.Position), wanted);
            return true;
        }
        return false;
    }

    private void MarkDisplaySent(PendingDisplay pending)
    {
        lock (_displayGate)
        {
            _sent = pending.Slot switch
            {
                0 => _sent with { TrackNumber = pending.Snapshot.TrackNumber },
                1 => _sent with { TrackName = pending.Snapshot.TrackName },
                2 => _sent with { State = pending.Snapshot.State },
                _ => _sent with { Position = pending.Snapshot.Position }
            };
            if (_sent == _wanted)
            {
                var started = Volatile.Read(ref _displaySnapshotStartedTimestamp);
                if (started > 0) TimingMetrics.RecordDisplaySnapshotSettled(Stopwatch.GetElapsedTime(started));
            }
        }
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
            TimingMetrics.RecordButtonDispatch(command.ParsedTimestamp, Stopwatch.GetTimestamp());
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
    private readonly record struct PendingDisplay(int Slot, byte[] Command, DisplaySnapshot Snapshot);
    private readonly record struct ReceivedFrame(
        ProtocolFrame Frame,
        DateTimeOffset Timestamp,
        long BytesReceivedTimestamp,
        long ParsedTimestamp);
    private sealed record ButtonCommand(int Generation, RemoteButton Button, long ParsedTimestamp);
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
