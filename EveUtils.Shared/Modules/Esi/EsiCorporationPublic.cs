using System.Text.Json.Serialization;

namespace EveUtils.Shared.Modules.Esi;

/// <summary>
/// The public <c>GET /corporations/{id}/</c> response (no token): the corporation's name + ticker. Other public
/// corp fields (member_count, ceo_id, date_founded, …) live in the same response and can be added when a view
/// needs them.
/// </summary>
public sealed class EsiCorporationPublic
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("ticker")] public string Ticker { get; set; } = string.Empty;
}
