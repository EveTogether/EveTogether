using System.Text.Json.Serialization;

namespace EveUtils.Shared.Modules.Esi;

/// <summary>The public <c>GET /alliances/{id}/</c> response (no token): the alliance's name + ticker.</summary>
public sealed class EsiAlliancePublic
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("ticker")] public string Ticker { get; set; } = string.Empty;
}
