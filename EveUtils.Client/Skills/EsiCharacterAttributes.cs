using System.Text.Json.Serialization;

namespace EveUtils.Client.Skills;

/// <summary>The ESI <c>GET /characters/{id}/attributes/</c> response: the character's five training attributes,
/// the base allocation without implants. The remap-cooldown fields ESI also returns are not stored — only the five
/// attributes feed the SP/min rate (data-minimalisation).</summary>
public sealed class EsiCharacterAttributes
{
    [JsonPropertyName("charisma")] public int Charisma { get; set; }
    [JsonPropertyName("intelligence")] public int Intelligence { get; set; }
    [JsonPropertyName("memory")] public int Memory { get; set; }
    [JsonPropertyName("perception")] public int Perception { get; set; }
    [JsonPropertyName("willpower")] public int Willpower { get; set; }
}
