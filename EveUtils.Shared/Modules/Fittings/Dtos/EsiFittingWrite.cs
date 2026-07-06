using System.Text.Json.Serialization;

namespace EveUtils.Shared.Modules.Fittings.Dtos;

/// <summary>Payload for <c>POST /characters/{id}/fittings/</c> — ESI assigns the fitting_id.</summary>
public sealed record EsiFittingWrite(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("ship_type_id")] int ShipTypeId,
    [property: JsonPropertyName("items")] IReadOnlyList<EsiFittingItem> Items);
