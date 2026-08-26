using System.Globalization;

namespace TajpanShowController.Core.Services;

public static class PlaybackTimeFormatter
{
    public static string Format(TimeSpan value)
    {
        var nonNegativeTicks = Math.Max(0, value.Ticks);
        var totalTenths = nonNegativeTicks / (TimeSpan.TicksPerSecond / 10);
        var totalMinutes = totalTenths / 600;
        var seconds = totalTenths / 10 % 60;
        var tenths = totalTenths % 10;
        return string.Create(CultureInfo.InvariantCulture, $"{totalMinutes:00}:{seconds:00}.{tenths}");
    }
}
