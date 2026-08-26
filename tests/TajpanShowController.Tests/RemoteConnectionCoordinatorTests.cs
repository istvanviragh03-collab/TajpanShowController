using TajpanShowController.Core.Interfaces;
using TajpanShowController.Core.Models;
using TajpanShowController.Core.Services;
using Xunit;

namespace TajpanShowController.Tests;

public sealed class RemoteConnectionCoordinatorTests
{
    private static readonly TimeSpan ShortReconnectInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan ShortConnectTimeout = TimeSpan.FromMilliseconds(80);

    [Fact]
    public async Task AutoConnectWithValidSavedPortStartsConnectionAttempt()
    {
        var remote = new FakeRemoteService(AttemptOutcome.Connected);
        await using var coordinator = Create(remote, autoReconnect: false);

        Assert.True(await coordinator.StartAsync(autoConnect: true, TestContext.Current.CancellationToken));
        Assert.Equal(["COM12"], remote.OpenedPorts);
        Assert.Equal(RemoteConnectionState.Connected, remote.ConnectionState);
    }

    [Fact]
    public async Task DisabledAutoConnectDoesNotStartConnectionAttempt()
    {
        var remote = new FakeRemoteService(AttemptOutcome.Connected);
        await using var coordinator = Create(remote, autoReconnect: true);

        Assert.False(await coordinator.StartAsync(autoConnect: false, TestContext.Current.CancellationToken));
        Assert.Empty(remote.OpenedPorts);
        Assert.Equal(RemoteConnectionState.Disconnected, remote.ConnectionState);
    }

    [Fact]
    public async Task MissingSavedPortDoesNotStartConnectionAttempt()
    {
        var remote = new FakeRemoteService(AttemptOutcome.Connected);
        await using var coordinator = Create(remote, autoReconnect: true, portName: null);

        Assert.False(await coordinator.StartAsync(autoConnect: true, TestContext.Current.CancellationToken));
        Assert.Empty(remote.OpenedPorts);
    }

    [Fact]
    public async Task FailedStartupAttemptDoesNotRemainConnecting()
    {
        var remote = new FakeRemoteService(AttemptOutcome.StayConnecting);
        await using var coordinator = Create(remote, autoReconnect: false);

        Assert.False(await coordinator.StartAsync(autoConnect: true, TestContext.Current.CancellationToken));
        Assert.Equal(RemoteConnectionState.Disconnected, remote.ConnectionState);
    }

    [Fact]
    public async Task ConnectionLossWithAutoReconnectRetriesAndRecovers()
    {
        var remote = new FakeRemoteService(AttemptOutcome.Connected, AttemptOutcome.Fault, AttemptOutcome.Connected);
        await using var coordinator = Create(remote, autoReconnect: true);
        await coordinator.StartAsync(autoConnect: true, TestContext.Current.CancellationToken);

        remote.LoseConnection();
        await WaitUntilAsync(() => remote.ConnectCalls >= 3 && remote.ConnectionState == RemoteConnectionState.Connected);

        Assert.Equal(3, remote.ConnectCalls);
    }

    [Fact]
    public async Task RepeatedReconnectFailuresContinuePeriodically()
    {
        var remote = new FakeRemoteService(AttemptOutcome.Connected, AttemptOutcome.Fault, AttemptOutcome.Fault, AttemptOutcome.Fault);
        await using var coordinator = Create(remote, autoReconnect: true);
        await coordinator.StartAsync(autoConnect: true, TestContext.Current.CancellationToken);

        remote.LoseConnection();
        await WaitUntilAsync(() => remote.ConnectCalls >= 4);

        Assert.True(remote.ConnectCalls >= 4);
        Assert.Equal(1, remote.MaximumConcurrentConnects);
    }

    [Fact]
    public async Task DisabledAutoReconnectLeavesLostConnectionDisconnected()
    {
        var remote = new FakeRemoteService(AttemptOutcome.Connected, AttemptOutcome.Connected);
        await using var coordinator = Create(remote, autoReconnect: false);
        await coordinator.StartAsync(autoConnect: true, TestContext.Current.CancellationToken);

        remote.LoseConnection();
        await Task.Delay(ShortReconnectInterval * 3, TestContext.Current.CancellationToken);

        Assert.Equal(1, remote.ConnectCalls);
        Assert.Equal(RemoteConnectionState.Disconnected, remote.ConnectionState);
    }

    [Fact]
    public async Task ManualDisconnectSuppressesAutoReconnect()
    {
        var remote = new FakeRemoteService(AttemptOutcome.Connected, AttemptOutcome.Connected);
        await using var coordinator = Create(remote, autoReconnect: true);
        await coordinator.StartAsync(autoConnect: true, TestContext.Current.CancellationToken);

        await coordinator.ManualDisconnectAsync(TestContext.Current.CancellationToken);
        await Task.Delay(ShortReconnectInterval * 3, TestContext.Current.CancellationToken);

        Assert.Equal(1, remote.ConnectCalls);
        Assert.Equal(RemoteConnectionState.Disconnected, remote.ConnectionState);
    }

    [Fact]
    public async Task ManualConnectAfterManualDisconnectClearsSuppressionEvenWhenAutomationIsDisabled()
    {
        var remote = new FakeRemoteService(AttemptOutcome.Connected, AttemptOutcome.Connected);
        await using var coordinator = Create(remote, autoReconnect: false);
        await coordinator.StartAsync(autoConnect: true, TestContext.Current.CancellationToken);
        await coordinator.ManualDisconnectAsync(TestContext.Current.CancellationToken);

        Assert.True(await coordinator.ManualConnectAsync(TestContext.Current.CancellationToken));

        Assert.Equal(2, remote.ConnectCalls);
        Assert.Equal(RemoteConnectionState.Connected, remote.ConnectionState);
    }

    [Fact]
    public async Task ManualConnectRunsImmediatelyWithoutWaitingForReconnectTimer()
    {
        var remote = new FakeRemoteService(AttemptOutcome.Connected, AttemptOutcome.Connected);
        await using var coordinator = new RemoteConnectionCoordinator(
            remote,
            () => new RemoteConnectionOptions("COM12", false),
            () => { },
            reconnectInterval: TimeSpan.FromSeconds(5),
            connectAttemptTimeout: ShortConnectTimeout);
        coordinator.SetAutoReconnect(true);
        await coordinator.StartAsync(autoConnect: true, TestContext.Current.CancellationToken);
        remote.LoseConnection();

        Assert.True(await coordinator.ManualConnectAsync(TestContext.Current.CancellationToken));
        Assert.Equal(2, remote.ConnectCalls);
    }

    [Fact]
    public async Task ConcurrentStartupAndManualTriggersNeverOpenInParallel()
    {
        var remote = new FakeRemoteService(AttemptOutcome.Connected, AttemptOutcome.Connected)
        {
            ConnectDelay = TimeSpan.FromMilliseconds(50)
        };
        await using var coordinator = Create(remote, autoReconnect: true);

        var startup = coordinator.StartAsync(autoConnect: true, TestContext.Current.CancellationToken);
        var manual = coordinator.ManualConnectAsync(TestContext.Current.CancellationToken);
        await Task.WhenAll(startup, manual);

        Assert.Equal(1, remote.MaximumConcurrentConnects);
    }

    [Fact]
    public async Task RapidSettingsConnectAndDisconnectChangesDoNotDeadlockOrPoisonNextConnect()
    {
        var remote = new FakeRemoteService(AttemptOutcome.Connected) { ConnectDelay = TimeSpan.FromMilliseconds(25) };
        await using var coordinator = Create(remote, autoReconnect: true);
        var ct = TestContext.Current.CancellationToken;

        var stress = Task.Run(async () =>
        {
            for (var i = 0; i < 20; i++)
            {
                var connect = coordinator.ManualConnectAsync(ct);
                await Task.Delay(1, ct);
                coordinator.SetAutoReconnect(i % 2 == 0);
                var disconnect = coordinator.ManualDisconnectAsync(ct);
                await Task.WhenAll(connect, disconnect);
            }

            coordinator.SetAutoReconnect(false);
            return await coordinator.ManualConnectAsync(ct);
        }, ct);

        Assert.True(await stress.WaitAsync(TimeSpan.FromSeconds(3), ct));
        Assert.Equal(RemoteConnectionState.Connected, remote.ConnectionState);
        Assert.Equal(1, remote.MaximumConcurrentConnects);
    }

    [Fact]
    public async Task DisablingAutoReconnectCancelsPendingRetry()
    {
        var remote = new FakeRemoteService(AttemptOutcome.Connected, AttemptOutcome.Connected);
        await using var coordinator = Create(remote, autoReconnect: true);
        await coordinator.StartAsync(autoConnect: true, TestContext.Current.CancellationToken);
        remote.LoseConnection();

        coordinator.SetAutoReconnect(false);
        await Task.Delay(ShortReconnectInterval * 3, TestContext.Current.CancellationToken);

        Assert.Equal(1, remote.ConnectCalls);
    }

    [Fact]
    public async Task ShutdownCancelsPendingReconnectLoop()
    {
        var remote = new FakeRemoteService(AttemptOutcome.Connected, AttemptOutcome.Connected);
        var coordinator = Create(remote, autoReconnect: true);
        await coordinator.StartAsync(autoConnect: true, TestContext.Current.CancellationToken);
        remote.LoseConnection();

        await coordinator.DisposeAsync();
        var countAtShutdown = remote.ConnectCalls;
        await Task.Delay(ShortReconnectInterval * 3, TestContext.Current.CancellationToken);

        Assert.Equal(countAtShutdown, remote.ConnectCalls);
    }

    [Fact]
    public async Task SuccessfulReconnectRequestsDisplayResync()
    {
        var remote = new FakeRemoteService(AttemptOutcome.Connected, AttemptOutcome.Connected);
        var resyncs = 0;
        await using var coordinator = Create(remote, autoReconnect: true, resyncDisplay: () => resyncs++);
        await coordinator.StartAsync(autoConnect: true, TestContext.Current.CancellationToken);
        remote.LoseConnection();

        await WaitUntilAsync(() => remote.ConnectCalls >= 2 && remote.ConnectionState == RemoteConnectionState.Connected);

        Assert.Equal(2, resyncs);
    }

    private static RemoteConnectionCoordinator Create(
        FakeRemoteService remote,
        bool autoReconnect,
        string? portName = "COM12",
        Action? resyncDisplay = null)
    {
        var coordinator = new RemoteConnectionCoordinator(
            remote,
            () => new RemoteConnectionOptions(portName, false),
            resyncDisplay ?? (() => { }),
            reconnectInterval: ShortReconnectInterval,
            connectAttemptTimeout: ShortConnectTimeout);
        coordinator.SetAutoReconnect(autoReconnect);
        return coordinator;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.True(condition(), "The expected coordinator state was not reached within two seconds.");
    }

    private enum AttemptOutcome { Connected, Fault, StayConnecting }

    private sealed class FakeRemoteService(params AttemptOutcome[] outcomes) : IRemoteControllerService
    {
        private readonly Queue<AttemptOutcome> _outcomes = new(outcomes);
        private int _concurrentConnects;

        public RemoteConnectionState ConnectionState { get; private set; }
        public string LastResponse { get; private set; } = "—";
        public int ConnectCalls { get; private set; }
        public int MaximumConcurrentConnects { get; private set; }
        public TimeSpan ConnectDelay { get; init; }
        public List<string> OpenedPorts { get; } = [];
        public event EventHandler<RemoteButton>? ButtonPressed { add { } remove { } }
        public event EventHandler? StatusChanged;

        public async Task ConnectAsync(string portName, bool simulation, CancellationToken cancellationToken = default)
        {
            ConnectCalls++;
            OpenedPorts.Add(portName);
            var concurrent = Interlocked.Increment(ref _concurrentConnects);
            MaximumConcurrentConnects = Math.Max(MaximumConcurrentConnects, concurrent);
            try
            {
                SetState(RemoteConnectionState.Connecting);
                if (ConnectDelay > TimeSpan.Zero) await Task.Delay(ConnectDelay, cancellationToken);
                var outcome = _outcomes.Count > 0 ? _outcomes.Dequeue() : AttemptOutcome.Fault;
                if (outcome == AttemptOutcome.Connected)
                {
                    LastResponse = "@B00000000";
                    SetState(RemoteConnectionState.Connected);
                }
                else if (outcome == AttemptOutcome.Fault)
                {
                    LastResponse = "Port unavailable";
                    SetState(RemoteConnectionState.Fault);
                }
            }
            finally { Interlocked.Decrement(ref _concurrentConnects); }
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            SetState(RemoteConnectionState.Disconnected);
            return Task.CompletedTask;
        }

        public void LoseConnection()
        {
            LastResponse = "TIMEOUT";
            SetState(RemoteConnectionState.Disconnected);
        }

        public void UpdateDisplay(int trackNumber, string trackName, PlaybackState state, TimeSpan position) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void SetState(RemoteConnectionState state)
        {
            if (ConnectionState == state) return;
            ConnectionState = state;
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
