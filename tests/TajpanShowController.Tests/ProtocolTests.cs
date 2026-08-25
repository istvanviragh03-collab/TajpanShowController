using System.Text;
using TajpanShowController.Core.Models;
using TajpanShowController.Core.Protocol;
using Xunit;

namespace TajpanShowController.Tests;

public sealed class ProtocolTests
{
    private static string Text(byte[] bytes) => Encoding.ASCII.GetString(bytes);

    [Fact] public void FormatsAllOutgoingCommands()
    {
        Assert.Equal("@S\r\n", Text(ProtocolCodec.Poll())); Assert.Equal("@A\r\n", Text(ProtocolCodec.Ack())); Assert.Equal("@X\r\n", Text(ProtocolCodec.Nack()));
        Assert.Equal("@B10101010\r\n", Text(ProtocolCodec.Buttons(new(true,false,true,false,true,false,true,false))));
        Assert.Equal("@T02:03.4\r\n", Text(ProtocolCodec.Timecode(TimeSpan.FromMilliseconds(123400))));
        Assert.Equal("@N12\r\n", Text(ProtocolCodec.TrackNumber(12))); Assert.Equal("@KTrack\r\n", Text(ProtocolCodec.TrackName("Track")));
        Assert.Equal("@PQ\r\n", Text(ProtocolCodec.State(RemoteDisplayState.Queued))); Assert.Equal("@PP\r\n", Text(ProtocolCodec.State(RemoteDisplayState.Playing)));
        Assert.Equal("@PA\r\n", Text(ProtocolCodec.State(RemoteDisplayState.Paused))); Assert.Equal("@PS\r\n", Text(ProtocolCodec.State(RemoteDisplayState.Stopped)));
    }

    [Fact] public void ParsesButtonMappingAndReservedBits()
    {
        var state = StreamingProtocolParser.ParseLine("@B10101101").GetButtons();
        Assert.True(state.Start); Assert.False(state.Stop); Assert.True(state.Pause); Assert.False(state.Previous); Assert.True(state.Next);
        Assert.True(state.Reserved1); Assert.False(state.Reserved2); Assert.True(state.Reserved3);
    }
    [Fact] public void HandlesFragmentedLine()
    {
        var p = new StreamingProtocolParser(); Assert.Empty(p.Append("@B1000"u8)); var result = p.Append("0000\r\n"u8);
        Assert.Single(result); Assert.Equal(ProtocolFrameKind.Buttons, result[0].Kind);
    }
    [Fact] public void HandlesMultipleCrLfLinesInOneRead()
    {
        var result = new StreamingProtocolParser().Append("@A\r\n@X\r\n@B00000000\r\n"u8);
        Assert.Equal(3, result.Count); Assert.Equal([ProtocolFrameKind.Ack, ProtocolFrameKind.Nack, ProtocolFrameKind.Buttons], result.Select(x => x.Kind));
    }
    [Theory]
    [InlineData("B00000000", ProtocolFrameKind.Unknown)] [InlineData("@B0000000", ProtocolFrameKind.Unknown)] [InlineData("@B000A0000", ProtocolFrameKind.Unknown)] [InlineData("@Z", ProtocolFrameKind.Unknown)]
    public void RejectsInvalidFrames(string line, ProtocolFrameKind expected) => Assert.Equal(expected, StreamingProtocolParser.ParseLine(line).Kind);
    [Fact] public void ParsesAckAndNack() { Assert.Equal(ProtocolFrameKind.Ack, StreamingProtocolParser.ParseLine("@A").Kind); Assert.Equal(ProtocolFrameKind.Nack, StreamingProtocolParser.ParseLine("@X").Kind); }
    [Fact] public void SanitizesTrackName() => Assert.Equal("rvztr", ProtocolCodec.SanitizeTrackName("Árvíz@tűrő\r\n"));
}
