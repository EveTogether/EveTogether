using System.Text.Json.Serialization;

namespace EveUtils.Shared.Modules.Fittings.Dtos;

/// <summary>One slot/item in an ESI fitting.</summary>
public sealed record EsiFittingItem(
    [property: JsonPropertyName("type_id")] int TypeId,
    [property: JsonPropertyName("flag")] string Flag,
    [property: JsonPropertyName("quantity")] int Quantity);
