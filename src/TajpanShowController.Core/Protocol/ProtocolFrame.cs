using TajpanShowController.Core.Models;

namespace TajpanShowController.Core.Protocol;

public enum ProtocolFrameKind { Poll, Buttons, Ack, Nack, Timecode, TrackNumber, TrackName, PlaybackState, Unknown }

public sealed record ProtocolFrame(ProtocolFrameKind Kind, string Payload, string Raw)
{
    public RemoteButtonState GetButtons() => RemoteButtonState.FromBits(Payload);
}
