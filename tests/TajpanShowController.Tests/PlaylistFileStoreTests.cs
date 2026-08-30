using TajpanShowController.Core.Models;
using TajpanShowController.Infrastructure.Persistence;
using Xunit;

namespace TajpanShowController.Tests;

public sealed class PlaylistFileStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "TajpanShowController.PlaylistTests", Guid.NewGuid().ToString("N"));
    private readonly PlaylistFileStore _store = new();

    [Fact]
    public async Task EditedNameSurvivesSaveLoadAndSaveAs()
    {
        Directory.CreateDirectory(_directory);
        var ct = TestContext.Current.CancellationToken;
        var first = Path.Combine(_directory, "technical-file-name.json");
        var second = Path.Combine(_directory, "renamed-file.json");
        var document = new PlaylistDocument { PlaylistName = "Szombat esti nagykoncert", Tracks = ["track.wav"] };

        await _store.SaveAsync(first, document, ct);
        var loaded = await _store.LoadAsync(first, ct);
        await _store.SaveAsync(second, loaded, ct);
        var savedAs = await _store.LoadAsync(second, ct);

        Assert.Equal("Szombat esti nagykoncert", loaded.PlaylistName);
        Assert.Equal("Szombat esti nagykoncert", savedAs.PlaylistName);
    }

    [Fact]
    public async Task FileRenameOrMoveDoesNotReplaceStoredPlaylistName()
    {
        Directory.CreateDirectory(_directory);
        var ct = TestContext.Current.CancellationToken;
        var original = Path.Combine(_directory, "original.json");
        var movedFolder = Path.Combine(_directory, "moved");
        Directory.CreateDirectory(movedFolder);
        var moved = Path.Combine(movedFolder, "different-name.json");
        await _store.SaveAsync(original, new PlaylistDocument { PlaylistName = "Tour Show" }, ct);
        File.Move(original, moved);

        Assert.Equal("Tour Show", (await _store.LoadAsync(moved, ct)).PlaylistName);
    }

    [Fact]
    public async Task LegacyTitleIsUsedOnceAsCompatibilityName()
    {
        Directory.CreateDirectory(_directory);
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_directory, "legacy-file.json");
        await File.WriteAllTextAsync(path, "{\"Title\":\"Legacy concert\",\"Tracks\":[]}", ct);

        var document = await _store.LoadAsync(path, ct);

        Assert.Equal("Legacy concert", document.PlaylistName);
    }

    [Fact]
    public async Task NamelessLegacyFileFallsBackToFileName()
    {
        Directory.CreateDirectory(_directory);
        var ct = TestContext.Current.CancellationToken;
        var path = Path.Combine(_directory, "fallback-show.json");
        await File.WriteAllTextAsync(path, "{\"Tracks\":[]}", ct);

        Assert.Equal("fallback-show", (await _store.LoadAsync(path, ct)).PlaylistName);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
