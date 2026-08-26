using System.Text.Json;
using TajpanShowController.Core.Models;

namespace TajpanShowController.Infrastructure.Persistence;

public sealed class PlaylistFileStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public async Task<PlaylistDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<PlaylistDocument>(stream, Options, cancellationToken) ?? new PlaylistDocument();
        document.PlaylistName = ResolvePlaylistName(document, path);
        return document;
    }

    public async Task SaveAsync(string path, PlaylistDocument document, CancellationToken cancellationToken = default)
    {
        document.LegacyTitle = null;
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, document, Options, cancellationToken);
    }

    public static string ResolvePlaylistName(PlaylistDocument document, string path)
    {
        if (!string.IsNullOrWhiteSpace(document.PlaylistName)) return document.PlaylistName;
        if (!string.IsNullOrWhiteSpace(document.LegacyTitle)) return document.LegacyTitle;
        return Path.GetFileNameWithoutExtension(path);
    }
}
