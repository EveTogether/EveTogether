using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace EveUtils.Client.Esi;

/// <summary>A wing from <c>GET /fleets/{id}/wings/</c>: id + name + nested squads.</summary>
public sealed class EsiFleetWing
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("squads")] public List<EsiFleetSquadInfo> Squads { get; set; } = [];
}

/// <summary>A squad nested under an <see cref="EsiFleetWing"/> (ESI returns squads inside the wings response).</summary>
public sealed class EsiFleetSquadInfo
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
}
