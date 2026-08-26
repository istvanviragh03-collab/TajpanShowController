using TajpanShowController.Core.Diagnostics;
using TajpanShowController.Core.Interfaces;

namespace TajpanShowController.Core.Services;

public sealed record RemoteConnectionOptions(string? PortName, bool Simulation)
{
    public bool IsValid => Simulation || !string.IsNullOrWhiteSpace(PortName);
    public string EffectivePortName => Simulation ? "SIM" : PortName!;
}

public sealed class RemoteConnectionCoordinator : IAsyncDisposable
{
    private enum ConnectReason { Startup, Manual, Reconnect }

    private readonly IRemoteControllerService _remote;
    private readonly Func<RemoteConnectionOptions> _optionsProvider;
    private readonly Action _resyncDisplay;
    private readonly RemoteDebugLogBuffer _debugLog;
    private readonly TimeSpan _reconnectInterval;
    private readonly TimeSpan _connectAttemptTimeout;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly object _lifecycleGate = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private CancellationTokenSource? _reconnectCts;
    private CancellationTokenSource? _activeAttemptCts;
    private Task? _reconnectTask;
    private RemoteConnectionState _lastObservedState;
    private bool _connectAttemptActive;
    private bool _manualDisconnectRequested;
    private bool _isShuttingDown;
    private bool _autoReconnectEnabled;
    private int _reconnectAttempt;

    public static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan ConnectAttemptTimeout = TimeSpan.FromSeconds(2);

    public RemoteConnectionCoordinator(
        IRemoteControllerService remote,
        Func<RemoteConnectionOptions> optionsProvider,
        Action resyncDisplay,
        RemoteDebugLogBuffer? debugLog = null,
        TimeSpan? reconnectInterval = null,
        TimeSpan? connectAttemptTimeout = null)
    {
        _remote = remote;
        _optionsProvider = optionsProvider;
        _resyncDisplay = resyncDisplay;
        _debugLog = debugLog ?? new RemoteDebugLogBuffer();
        _reconnectInterval = reconnectInterval ?? ReconnectInterval;
        _connectAttemptTimeout = connectAttemptTimeout ?? ConnectAttemptTimeout;
        _lastObservedState = remote.ConnectionState;
        _remote.StatusChanged += RemoteStatusChanged;
    }

    public void SetAutoReconnect(bool enabled)
    {
        lock (_lifecycleGate) _autoReconnectEnabled = enabled;
        if (!enabled) CancelReconnect();
    }

    public async Task<bool> StartAsync(bool autoConnect, CancellationToken cancellationToken = default)
    {
        lock (_lifecycleGate) _manualDisconnectRequested = false;
        if (!autoConnect) return false;

        var options = _optionsProvider();
        if (!options.IsValid) return false;

        _debugLog.Write(RemoteDebugLogKind.Info,
            $"Auto connect: {options.EffectivePortName} @ {RemoteSerialDefaults.BaudRate}");
        var connected = await ConnectCoreAsync(options, ConnectReason.Startup, cancellationToken);
        if (!connected) ScheduleReconnect();
        return connected;
    }

    public async Task<bool> ManualConnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleGate) _manualDisconnectRequested = false;
        CancelActiveAttempt();
        await StopReconnectAsync();

        var options = _optionsProvider();
        if (!options.IsValid) return false;

        var connected = await ConnectCoreAsync(options, ConnectReason.Manual, cancellationToken);
        if (!connected) ScheduleReconnect();
        return connected;
    }

    public async Task ManualDisconnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_lifecycleGate) _manualDisconnectRequested = true;
        CancelActiveAttempt();
        await StopReconnectAsync();
        await _connectGate.WaitAsync(cancellationToken);
        try { await _remote.DisconnectAsync(cancellationToken); }
        finally { _connectGate.Release(); }
    }

    private async Task<bool> ConnectCoreAsync(
        RemoteConnectionOptions options,
        ConnectReason reason,
        CancellationToken cancellationToken)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCts.Token);
        var token = attemptCts.Token;
        await _connectGate.WaitAsync(token);
        try
        {
            if (_remote.ConnectionState == RemoteConnectionState.Connected) return true;
            lock (_lifecycleGate)
            {
                if (_isShuttingDown || (reason == ConnectReason.Reconnect && _manualDisconnectRequested)) return false;
                _connectAttemptActive = true;
                _activeAttemptCts = attemptCts;
            }

            await _remote.ConnectAsync(options.EffectivePortName, options.Simulation, token);
            var deadline = DateTime.UtcNow + _connectAttemptTimeout;
            while (_remote.ConnectionState == RemoteConnectionState.Connecting && DateTime.UtcNow < deadline)
                await Task.Delay(TimeSpan.FromMilliseconds(10), token);

            if (_remote.ConnectionState == RemoteConnectionState.Connecting)
            {
                _debugLog.Write(RemoteDebugLogKind.Warning, "Remote connection attempt timed out");
                await _remote.DisconnectAsync(CancellationToken.None);
            }

            if (_remote.ConnectionState != RemoteConnectionState.Connected) return false;
            Interlocked.Exchange(ref _reconnectAttempt, 0);
            _resyncDisplay();
            if (reason == ConnectReason.Reconnect)
                _debugLog.Write(RemoteDebugLogKind.Info, "Remote reconnected");
            return true;
        }
        catch (OperationCanceledException) when (attemptCts.IsCancellationRequested)
        {
            try { await _remote.DisconnectAsync(CancellationToken.None); }
            catch (Exception ex) { _debugLog.Write(RemoteDebugLogKind.Error, "Remote disconnect after cancellation failed: " + ex.Message); }
            return false;
        }
        catch (Exception)
        {
            if (_remote.ConnectionState == RemoteConnectionState.Connecting)
            {
                try { await _remote.DisconnectAsync(CancellationToken.None); }
                catch (Exception ex) { _debugLog.Write(RemoteDebugLogKind.Error, "Remote disconnect after failed connect failed: " + ex.Message); }
            }
            return false; // The Remote service records the transport exception and exposes Fault.
        }
        finally
        {
            lock (_lifecycleGate)
            {
                _connectAttemptActive = false;
                if (ReferenceEquals(_activeAttemptCts, attemptCts)) _activeAttemptCts = null;
            }
            _connectGate.Release();
        }
    }

    private void RemoteStatusChanged(object? sender, EventArgs e)
    {
        var state = _remote.ConnectionState;
        RemoteConnectionState previous;
        bool attemptActive;
        bool manualDisconnectRequested;
        lock (_lifecycleGate)
        {
            previous = _lastObservedState;
            _lastObservedState = state;
            attemptActive = _connectAttemptActive;
            manualDisconnectRequested = _manualDisconnectRequested;
        }

        if (!manualDisconnectRequested && previous == RemoteConnectionState.Connected &&
            state is RemoteConnectionState.Disconnected or RemoteConnectionState.Fault)
            _debugLog.Write(RemoteDebugLogKind.Warning, "Remote connection lost");

        if (!attemptActive && state is RemoteConnectionState.Disconnected or RemoteConnectionState.Fault)
            ScheduleReconnect();
    }

    private void ScheduleReconnect()
    {
        CancellationTokenSource cts;
        lock (_lifecycleGate)
        {
            if (!_autoReconnectEnabled || _manualDisconnectRequested || _isShuttingDown ||
                !_optionsProvider().IsValid || _remote.ConnectionState == RemoteConnectionState.Connected ||
                _reconnectTask is { IsCompleted: false }) return;

            cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
            _reconnectCts = cts;
            _reconnectTask = RunReconnectLoopAsync(cts);
        }
    }

    private async Task RunReconnectLoopAsync(CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(_reconnectInterval, token);
                RemoteConnectionOptions options;
                lock (_lifecycleGate)
                {
                    if (!_autoReconnectEnabled || _manualDisconnectRequested || _isShuttingDown) return;
                    options = _optionsProvider();
                }
                if (!options.IsValid) return;

                var attempt = Interlocked.Increment(ref _reconnectAttempt);
                _debugLog.Write(RemoteDebugLogKind.Info, $"Auto reconnect attempt {attempt}");
                if (await ConnectCoreAsync(options, ConnectReason.Reconnect, token)) return;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            lock (_lifecycleGate)
            {
                if (ReferenceEquals(_reconnectCts, cts))
                {
                    _reconnectCts = null;
                    _reconnectTask = null;
                }
            }
            cts.Dispose();
        }
    }

    private void CancelActiveAttempt()
    {
        lock (_lifecycleGate)
        {
            try { _activeAttemptCts?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private void CancelReconnect()
    {
        lock (_lifecycleGate)
        {
            try { _reconnectCts?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task StopReconnectAsync()
    {
        Task? task;
        lock (_lifecycleGate)
        {
            try { _reconnectCts?.Cancel(); }
            catch (ObjectDisposedException) { }
            task = _reconnectTask;
        }
        if (task is not null)
        {
            try { await task; }
            catch (OperationCanceledException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_lifecycleGate)
        {
            if (_isShuttingDown) return;
            _isShuttingDown = true;
            _manualDisconnectRequested = true;
        }
        _remote.StatusChanged -= RemoteStatusChanged;
        _lifetimeCts.Cancel();
        CancelActiveAttempt();
        await StopReconnectAsync();
        await _connectGate.WaitAsync();
        try { await _remote.DisconnectAsync(); }
        finally { _connectGate.Release(); }
        _lifetimeCts.Dispose();
        _connectGate.Dispose();
    }
}
