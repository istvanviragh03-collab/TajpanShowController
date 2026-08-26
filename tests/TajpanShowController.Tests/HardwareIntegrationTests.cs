using TajpanShowController.Core.Interfaces;
using TajpanShowController.Infrastructure.Serial;
using Xunit;

namespace TajpanShowController.Tests;

public sealed class HardwareIntegrationTests
{
    [Fact]
    public async Task ConfiguredComPortCompletesProductionHandshake()
    {
        var port = Environment.GetEnvironmentVariable("TAJPAN_HARDWARE_COM");
        if (string.IsNullOrWhiteSpace(port))
        {
            Assert.Skip("Set TAJPAN_HARDWARE_COM to run the physical remote smoke test.");
            return;
        }

        var ct = TestContext.Current.CancellationToken;
        await using var service = new RemoteControllerService(_ => new SerialPortTransport());
        await service.ConnectAsync(port, false, ct);

        var deadline = DateTime.UtcNow.AddSeconds(4);
        while (DateTime.UtcNow < deadline && service.ConnectionState == RemoteConnectionState.Connecting)
            await Task.Delay(25, ct);

        Assert.Equal(RemoteConnectionState.Connected, service.ConnectionState);
        Assert.Equal("@B00000000", service.LastResponse);
    }
}
