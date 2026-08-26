using TajpanShowController.Core.Interfaces;
using TajpanShowController.Core.Models;
using Xunit;

namespace TajpanShowController.Tests;

public sealed class RemoteStatusPresentationTests
{
    [Theory]
    [InlineData(RemoteConnectionState.Disconnected, "DISCONNECTED", "#75808A")]
    [InlineData(RemoteConnectionState.Connected, "CONNECTED", "#4CDA82")]
    [InlineData(RemoteConnectionState.Fault, "ERROR", "#E45B60")]
    public void AllUiLocationsCanUseTheSameStatePresentation(RemoteConnectionState state, string text, string color)
    {
        var presentation = RemoteStatusPresentation.From(state, "port error");
        Assert.Equal(text, presentation.Text);
        Assert.Equal(color, presentation.Color);
    }
}
