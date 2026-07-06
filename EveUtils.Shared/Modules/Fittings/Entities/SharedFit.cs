namespace EveUtils.Shared.Modules.Fittings.Entities;

/// <summary>
/// A fitting shared to the server by a client. Stored server-side; visibility = server-wide
/// . The raw ESI JSON is the canonical representation for this stage.
/// </summary>
public sealed class SharedFit
{
    public int Id { get; set; }
    public int EsiFittingId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int ShipTypeId { get; set; }
    public string RawJson { get; set; } = "{}";
    public string SharedByCharacterName { get; set; } = string.Empty;
    public int SharedByCharacterId { get; set; }
    public DateTimeOffset SharedAt { get; set; }

    /// <summary>Order-independent content fingerprint: the dedup key for the server library,
    /// owner- and ESI-id-agnostic. Empty on rows written before the column existed until the startup backfill runs.</summary>
    public string ContentHash { get; set; } = string.Empty;
}
