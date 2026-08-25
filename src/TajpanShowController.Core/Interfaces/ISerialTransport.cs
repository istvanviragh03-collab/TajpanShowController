namespace TajpanShowController.Core.Interfaces;

public interface ISerialTransport : IAsyncDisposable
{
    bool IsOpen { get; }
    Task OpenAsync(string portName, CancellationToken cancellationToken);
    Task CloseAsync(CancellationToken cancellationToken);
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken);
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}
