using System.Threading.Channels;
using TajpanShowController.Core.Interfaces;
using TajpanShowController.Core.Protocol;

namespace TajpanShowController.Infrastructure.Serial;

public sealed class SimulatedRemoteTransport : ISerialTransport
{
    private readonly Channel<byte> _responses = Channel.CreateUnbounded<byte>();
    public bool IsOpen { get; private set; }
    public string ButtonBits { get; set; } = "00000000";
    public bool DropResponses { get; set; }
    public bool NackDisplayCommands { get; set; }
    public bool SendMalformedNext { get; set; }
    public List<string> Writes { get; } = [];
    public Task OpenAsync(string portName, CancellationToken cancellationToken) { IsOpen = true; return Task.CompletedTask; }
    public Task CloseAsync(CancellationToken cancellationToken) { IsOpen = false; return Task.CompletedTask; }
    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (!IsOpen || DropResponses) return ValueTask.CompletedTask;
        var line = System.Text.Encoding.ASCII.GetString(data.Span).TrimEnd('\r','\n');
        Writes.Add(line);
        byte[]? response = null;
        if (line == "@S") response = SendMalformedNext ? ProtocolCodec.Bytes("bad") : ProtocolCodec.Bytes("@B" + ButtonBits);
        else if (line.StartsWith("@T") || line.StartsWith("@N") || line.StartsWith("@K") || line.StartsWith("@P"))
            response = NackDisplayCommands ? ProtocolCodec.Nack() : ProtocolCodec.Ack();
        SendMalformedNext = false;
        if (response is not null) foreach (var b in response) _responses.Writer.TryWrite(b);
        return ValueTask.CompletedTask;
    }
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var first = await _responses.Reader.ReadAsync(cancellationToken);
        buffer.Span[0] = first;
        var count = 1;
        while (count < buffer.Length && _responses.Reader.TryRead(out var b)) buffer.Span[count++] = b;
        return count;
    }
    public ValueTask DisposeAsync() { IsOpen = false; _responses.Writer.TryComplete(); return ValueTask.CompletedTask; }
}
