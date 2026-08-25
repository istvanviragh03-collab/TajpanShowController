using System.IO.Ports;
using TajpanShowController.Core.Interfaces;

namespace TajpanShowController.Infrastructure.Serial;

public sealed class SerialPortTransport : ISerialTransport
{
    private SerialPort? _port;
    public bool IsOpen => _port?.IsOpen == true;

    public Task OpenAsync(string portName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _port = new SerialPort(portName, 192000, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None, Encoding = System.Text.Encoding.ASCII,
            NewLine = "\r\n", ReadTimeout = 50, WriteTimeout = 50, DtrEnable = false, RtsEnable = false
        };
        _port.Open();
        return Task.CompletedTask;
    }
    public Task CloseAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); _port?.Close(); return Task.CompletedTask; }
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var stream = _port?.BaseStream ?? throw new InvalidOperationException("A soros port nincs nyitva.");
        await stream.WriteAsync(data, cancellationToken); await stream.FlushAsync(cancellationToken);
    }
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        (_port?.BaseStream ?? throw new InvalidOperationException("A soros port nincs nyitva.")).ReadAsync(buffer, cancellationToken);
    public ValueTask DisposeAsync() { _port?.Dispose(); _port = null; return ValueTask.CompletedTask; }
}
