using TajpanShowController.Core.Models;
using TajpanShowController.Core.Services;

namespace TajpanShowController.Core.Interfaces;

public enum RemoteConnectionState { Disconnected, Connecting, Connected, Fault }

public interface IRemoteControllerService : IAsyncDisposable
{
    RemoteConnectionState ConnectionState { get; }
    string LastResponse { get; }
    event EventHandler<RemoteButton>? ButtonPressed;
    event EventHandler? StatusChanged;
    Task ConnectAsync(string portName, bool simulation, CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
    void UpdateDisplay(int trackNumber, string trackName, PlaybackState state, TimeSpan position);
}
