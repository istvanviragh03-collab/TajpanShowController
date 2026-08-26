using TajpanShowController.Core.Models;
using TajpanShowController.Infrastructure.Persistence;
using Xunit;

namespace TajpanShowController.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TajpanShowController.Tests", Guid.NewGuid().ToString("N"));
    [Fact] public async Task SavesAndLoadsPlaylist()
    {
        var store = new JsonSettingsStore(_directory); var expected = new AppSettings { PlaylistName = "Saved show", LastComPort = "COM12", AutoConnect = false, AutoReconnect = false, Volume = .5f, AudioOutputDeviceNumber = 2, AudioOutputDeviceName = "Test output", Playlist = [new PlaylistTrack { FilePath = "x.wav", Title = "X", Duration = TimeSpan.FromSeconds(2) }] };
        var ct = TestContext.Current.CancellationToken; await store.SaveAsync(expected, ct); var loaded = await store.LoadAsync(ct); Assert.Single(loaded.Playlist); Assert.Equal("Saved show", loaded.PlaylistName); Assert.Equal("COM12", loaded.LastComPort); Assert.False(loaded.AutoConnect); Assert.False(loaded.AutoReconnect); Assert.Equal("X", loaded.Playlist[0].Title); Assert.Equal(.5f, loaded.Volume); Assert.Equal(2, loaded.AudioOutputDeviceNumber); Assert.Equal("Test output", loaded.AudioOutputDeviceName);
    }
    [Fact] public async Task MissingFileReturnsDefaults() => Assert.Empty((await new JsonSettingsStore(_directory).LoadAsync(TestContext.Current.CancellationToken)).Playlist);
    [Fact] public async Task CorruptJsonReturnsDefaults() { var ct = TestContext.Current.CancellationToken; Directory.CreateDirectory(_directory); await File.WriteAllTextAsync(Path.Combine(_directory, "settings.json"), "{broken", ct); Assert.Empty((await new JsonSettingsStore(_directory).LoadAsync(ct)).Playlist); }
    [Fact]
    public async Task LegacySettingsWithoutAutoFlagsUseSafeDefaults()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "settings.json"), "{\"LastComPort\":\"COM8\"}", ct);

        var loaded = await new JsonSettingsStore(_directory).LoadAsync(ct);

        Assert.Equal("COM8", loaded.LastComPort);
        Assert.True(loaded.AutoConnect);
        Assert.True(loaded.AutoReconnect);
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
