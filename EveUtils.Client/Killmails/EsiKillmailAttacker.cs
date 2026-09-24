using System.Text.Json.Serialization;

namespace EveUtils.Client.Killmails;

/// <summary>One attacker on a killmail; an NPC has no character id.</summary>
public sealed class EsiKillmailAttacker
{
    [JsonPropertyName("character_id")] public int? CharacterId { get; set; }
    [JsonPropertyName("corporation_id")] public int? CorporationId { get; set; }
    [JsonPropertyName("alliance_id")] public int? AllianceId { get; set; }
    [JsonPropertyName("faction_id")] public int? FactionId { get; set; }
    [JsonPropertyName("ship_type_id")] public int? ShipTypeId { get; set; }
    [JsonPropertyName("weapon_type_id")] public int? WeaponTypeId { get; set; }
    [JsonPropertyName("damage_done")] public int DamageDone { get; set; }
    [JsonPropertyName("final_blow")] public bool FinalBlow { get; set; }
}
