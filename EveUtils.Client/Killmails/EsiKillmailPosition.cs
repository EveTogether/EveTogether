using System.Text.Json.Serialization;

namespace EveUtils.Client.Killmails;

/// <summary>Where the victim died, in metres in the system's own frame (ET-473).</summary>
public sealed class EsiKillmailPosition
{
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("z")] public double Z { get; set; }
}
