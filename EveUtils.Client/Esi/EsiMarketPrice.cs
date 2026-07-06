using System.Text.Json.Serialization;

namespace EveUtils.Client.Esi;

/// <summary>One entry of the public ESI <c>GET /markets/prices/</c> response. Snake_case on the wire, so the
/// names need <see cref="JsonPropertyNameAttribute"/> (the Web defaults only fold case, not underscores).</summary>
public sealed class EsiMarketPrice
{
    [JsonPropertyName("type_id")]
    public int TypeId { get; init; }

    [JsonPropertyName("average_price")]
    public double AveragePrice { get; init; }

    [JsonPropertyName("adjusted_price")]
    public double AdjustedPrice { get; init; }
}
