using System;
using System.Text.Json.Serialization;

namespace EveUtils.Client.Esi;

/// <summary>
/// One member from <c>GET /fleets/{id}/members/</c>. Mapped onto our
/// <c>FleetMember</c> (member-matching by <see cref="CharacterId"/>). <see cref="WingId"/>/<see cref="SquadId"/>
/// are <c>-1</c> when unassigned.
/// </summary>
public sealed class EsiFleetMember
{
    [JsonPropertyName("character_id")] public int CharacterId { get; set; }
    [JsonPropertyName("ship_type_id")] public int ShipTypeId { get; set; }
    [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
    [JsonPropertyName("role_name")] public string RoleName { get; set; } = string.Empty;
    [JsonPropertyName("join_time")] public DateTimeOffset JoinTime { get; set; }
    [JsonPropertyName("wing_id")] public long WingId { get; set; } = -1;
    [JsonPropertyName("squad_id")] public long SquadId { get; set; } = -1;
    [JsonPropertyName("solar_system_id")] public int SolarSystemId { get; set; }
    [JsonPropertyName("station_id")] public long? StationId { get; set; }
    [JsonPropertyName("takes_fleet_warp")] public bool TakesFleetWarp { get; set; }
}
