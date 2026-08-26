using System.Globalization;
using System.Text;
using TajpanShowController.Core.Models;
using TajpanShowController.Core.Services;

namespace TajpanShowController.Core.Protocol;

public static class ProtocolCodec
{
    public const string Terminator = "\r\n";
    public static byte[] Poll() => Bytes("@S");
    public static byte[] Ack() => Bytes("@A");
    public static byte[] Nack() => Bytes("@X");
    public static byte[] Buttons(RemoteButtonState s) => Bytes("@B" +
        string.Concat(s.Start?'1':'0', s.Stop?'1':'0', s.Pause?'1':'0', s.Previous?'1':'0',
            s.Next?'1':'0', s.Reserved1?'1':'0', s.Reserved2?'1':'0', s.Reserved3?'1':'0'));
    public static byte[] Timecode(TimeSpan value)
        => Bytes("@T" + PlaybackTimeFormatter.Format(value));
    public static byte[] TrackNumber(int number) => number < 0
        ? throw new ArgumentOutOfRangeException(nameof(number)) : Bytes("@N" + number.ToString(CultureInfo.InvariantCulture));
    public static byte[] TrackName(string name) => Bytes("@K" + SanitizeTrackName(name));
    public static byte[] State(RemoteDisplayState state) => Bytes(state switch
    {
        RemoteDisplayState.Queued => "@PQ", RemoteDisplayState.Playing => "@PP",
        RemoteDisplayState.Paused => "@PA", RemoteDisplayState.Stopped => "@PS",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    });
    public static string SanitizeTrackName(string? name) =>
        new((name ?? string.Empty).Where(c => c is not ('\r' or '\n' or '@') && c <= 0x7f && !char.IsControl(c)).ToArray());
    public static byte[] Bytes(string frame) => Encoding.ASCII.GetBytes(frame + Terminator);
}
