using System.Text.Json;
using TajpanShowController.Core.Interfaces;
using TajpanShowController.Core.Models;

namespace TajpanShowController.Infrastructure.Persistence;

public sealed class JsonSettingsStore(string baseDirectory) : ISettingsStore
{
    private readonly string _path = Path.Combine(baseDirectory, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_path)) return new AppSettings();
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, Options, cancellationToken) ?? new AppSettings();
        }
        catch (JsonException) { return new AppSettings(); }
        catch (IOException) { return new AppSettings(); }
        catch (UnauthorizedAccessException) { return new AppSettings(); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
            await JsonSerializer.SerializeAsync(stream, settings, Options, cancellationToken);
        File.Move(temporary, _path, true);
    }
}
