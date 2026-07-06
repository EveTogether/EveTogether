using System.Text.Json.Serialization;

namespace EveUtils.Shared.Modules.Fittings.Dtos;

/// <summary>
/// ESI fitting as returned by <c>GET /characters/{id}/fittings/</c>.
/// Stored verbatim as JSON; SDE name-resolution and internal model come later.
/// </summary>
public sealed record EsiFitting(
    [property: JsonPropertyName("fitting_id")] int FittingId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("ship_type_id")] int ShipTypeId,
    [property: JsonPropertyName("items")] IReadOnlyList<EsiFittingItem> Items);
