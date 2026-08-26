using System.Text.Json.Serialization;

namespace TajpanShowController.Core.Models;

public sealed class PlaylistDocument
{
    public string? PlaylistName { get; set; }

    [JsonPropertyName("Title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyTitle { get; set; }

    public List<string> Tracks { get; set; } = [];
}
