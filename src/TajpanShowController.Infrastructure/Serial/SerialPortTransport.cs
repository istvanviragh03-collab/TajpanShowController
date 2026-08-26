using System.IO.Ports;
using TajpanShowController.Core.Interfaces;

namespace TajpanShowController.Infrastructure.Serial;

public sealed class SerialPortTransport : ISerialTransport
{
    private SerialPort? _port;
    public bool IsOpen => _port?.IsOpen == true;

    public async Task OpenAsync(string portName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var port = new SerialPort(portName, RemoteSerialDefaults.BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None, Encoding = System.Text.Encoding.ASCII,
            NewLine = "\r\n", ReadTimeout = 50, WriteTimeout = 50, DtrEnable = true, RtsEnable = false
        };
        _port = port;
        await Task.Run(port.Open).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }
    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var port = _port;
        if (port is not null) await Task.Run(port.Close).ConfigureAwait(false);
    }
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        var stream = _port?.BaseStream ?? throw new InvalidOperationException("A soros port nincs nyitva.");
        await stream.WriteAsync(data, cancellationToken); await stream.FlushAsync(cancellationToken);
    }
    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
        (_port?.BaseStream ?? throw new InvalidOperationException("A soros port nincs nyitva.")).ReadAsync(buffer, cancellationToken);
    public async ValueTask DisposeAsync()
    {
        var port = Interlocked.Exchange(ref _port, null);
        if (port is not null) await Task.Run(port.Dispose).ConfigureAwait(false);
    }
}
