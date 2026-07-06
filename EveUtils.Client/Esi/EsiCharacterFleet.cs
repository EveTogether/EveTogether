using System.Text.Json.Serialization;

namespace EveUtils.Client.Esi;

/// <summary>
/// Response of <c>GET /characters/{id}/fleet/</c>. The only per-member fleet endpoint: each member reads its
/// OWN fleet id, role, wing/squad and the <see cref="FleetBossId"/> — never the roster. <see cref="WingId"/>/
/// <see cref="SquadId"/> are <c>-1</c> when not assigned (ESI sentinel, matches our <c>FleetMember</c>). Cached 60s.
/// </summary>
public sealed class EsiCharacterFleet
{
    [JsonPropertyName("fleet_id")] public long FleetId { get; set; }
    [JsonPropertyName("fleet_boss_id")] public int FleetBossId { get; set; }
    [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
    [JsonPropertyName("wing_id")] public long WingId { get; set; } = -1;
    [JsonPropertyName("squad_id")] public long SquadId { get; set; } = -1;

    /// <summary>Boss detection: the boss is the character whose id equals <see cref="FleetBossId"/>.</summary>
    public bool IsBoss(int characterId) => FleetBossId == characterId;
}
