using System.Text.Json.Serialization;

namespace EveUtils.Client.Esi;

/// <summary>One character's active ship from <c>GET /characters/{id}/ship/</c>.</summary>
public sealed class EsiCharacterShip
{
    [JsonPropertyName("ship_type_id")] public int ShipTypeId { get; set; }
    [JsonPropertyName("ship_item_id")] public long ShipItemId { get; set; }
    [JsonPropertyName("ship_name")] public string ShipName { get; set; } = string.Empty;
}
