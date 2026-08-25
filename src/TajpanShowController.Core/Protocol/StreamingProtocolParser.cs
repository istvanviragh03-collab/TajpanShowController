using System.Text;

namespace TajpanShowController.Core.Protocol;

public sealed class StreamingProtocolParser
{
    private readonly StringBuilder _buffer = new();
    private const int MaxBuffer = 4096;

    public IReadOnlyList<ProtocolFrame> Append(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes) _buffer.Append((char)b);
        var frames = new List<ProtocolFrame>();
        while (true)
        {
            var text = _buffer.ToString();
            var end = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (end < 0) break;
            var line = text[..end];
            _buffer.Remove(0, end + 2);
            frames.Add(ParseLine(line));
        }
        if (_buffer.Length > MaxBuffer) _buffer.Clear();
        return frames;
    }

    public static ProtocolFrame ParseLine(string line)
    {
        if (string.IsNullOrEmpty(line) || line[0] != '@') return new(ProtocolFrameKind.Unknown, string.Empty, line);
        return line.Length switch
        {
            2 when line == "@S" => new(ProtocolFrameKind.Poll, "", line),
            2 when line == "@A" => new(ProtocolFrameKind.Ack, "", line),
            2 when line == "@X" => new(ProtocolFrameKind.Nack, "", line),
            10 when line.StartsWith("@B", StringComparison.Ordinal) && line.AsSpan(2).IndexOfAnyExcept('0','1') < 0 => new(ProtocolFrameKind.Buttons, line[2..], line),
            _ when line.StartsWith("@B", StringComparison.Ordinal) => new(ProtocolFrameKind.Unknown, line[2..], line),
            _ when line.StartsWith("@T", StringComparison.Ordinal) => new(ProtocolFrameKind.Timecode, line[2..], line),
            _ when line.StartsWith("@N", StringComparison.Ordinal) => new(ProtocolFrameKind.TrackNumber, line[2..], line),
            _ when line.StartsWith("@K", StringComparison.Ordinal) => new(ProtocolFrameKind.TrackName, line[2..], line),
            3 when line.StartsWith("@P", StringComparison.Ordinal) && "QPAS".Contains(line[2]) => new(ProtocolFrameKind.PlaybackState, line[2..], line),
            _ => new(ProtocolFrameKind.Unknown, line.Length > 2 ? line[2..] : string.Empty, line)
        };
    }
}
