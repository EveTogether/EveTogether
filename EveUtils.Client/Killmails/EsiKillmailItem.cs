using System.Text.Json.Serialization;

namespace EveUtils.Client.Killmails;

/// <summary>One item stack on the victim; a container lists its contents one level down in <see cref="Items"/>.</summary>
public sealed class EsiKillmailItem
{
    [JsonPropertyName("item_type_id")] public int ItemTypeId { get; set; }
    [JsonPropertyName("flag")] public int Flag { get; set; }
    [JsonPropertyName("quantity_destroyed")] public long? QuantityDestroyed { get; set; }
    [JsonPropertyName("quantity_dropped")] public long? QuantityDropped { get; set; }
    [JsonPropertyName("items")] public EsiKillmailItem[] Items { get; set; } = [];
}
