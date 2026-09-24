using System.Text.Json.Serialization;

namespace EveUtils.Client.Killmails;

/// <summary>The victim block of a killmail: who died in what, and the items the ship carried.</summary>
public sealed class EsiKillmailVictim
{
    [JsonPropertyName("character_id")] public int? CharacterId { get; set; }
    [JsonPropertyName("corporation_id")] public int? CorporationId { get; set; }
    [JsonPropertyName("alliance_id")] public int? AllianceId { get; set; }
    [JsonPropertyName("ship_type_id")] public int ShipTypeId { get; set; }
    [JsonPropertyName("damage_taken")] public int DamageTaken { get; set; }
    [JsonPropertyName("items")] public EsiKillmailItem[] Items { get; set; } = [];
}
