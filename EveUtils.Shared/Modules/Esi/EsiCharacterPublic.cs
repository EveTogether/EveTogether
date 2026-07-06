using System.Text.Json.Serialization;

namespace EveUtils.Shared.Modules.Esi;

/// <summary>The public <c>GET /characters/{id}/</c> response (no token): the character's name + affiliations.</summary>
public sealed class EsiCharacterPublic
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("corporation_id")] public int CorporationId { get; set; }
    [JsonPropertyName("alliance_id")] public int? AllianceId { get; set; }
}
