using TajpanShowController.Core.Services;
using Xunit;

namespace TajpanShowController.Tests;

public sealed class PlaybackTimeFormatterTests
{
    [Theory]
    [InlineData(0, "00:00.0")]
    [InlineData(5.34, "00:05.3")]
    [InlineData(62.78, "01:02.7")]
    [InlineData(754.99, "12:34.9")]
    [InlineData(3663.25, "61:03.2")]
    public void FormatsWithTruncatedTenthsAndUnboundedMinutes(double seconds, string expected) =>
        Assert.Equal(expected, PlaybackTimeFormatter.Format(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void ProtocolAndPlaybackUseTheSameFormatter()
    {
        var position = TimeSpan.FromSeconds(134.69);
        var frame = System.Text.Encoding.ASCII.GetString(TajpanShowController.Core.Protocol.ProtocolCodec.Timecode(position));

        Assert.Equal("02:14.6", PlaybackTimeFormatter.Format(position));
        Assert.Equal("@T02:14.6\r\n", frame);
        Assert.Equal("@T61:03.2\r\n", System.Text.Encoding.ASCII.GetString(
            TajpanShowController.Core.Protocol.ProtocolCodec.Timecode(TimeSpan.FromSeconds(3663.25))));
    }
}
