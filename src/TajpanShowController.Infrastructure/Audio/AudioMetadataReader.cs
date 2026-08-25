using NAudio.Wave;

namespace TajpanShowController.Infrastructure.Audio;

public static class AudioMetadataReader
{
    public static TimeSpan? TryGetDuration(string path)
    {
        try { using var reader = new AudioFileReader(path); return reader.TotalTime; }
        catch { return null; }
    }
}
