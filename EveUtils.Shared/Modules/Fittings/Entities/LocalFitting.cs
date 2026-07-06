using EveUtils.Shared.Data;

namespace EveUtils.Shared.Modules.Fittings.Entities;

/// <summary>
/// A fitting imported from ESI and stored locally on the client. The raw ESI JSON is
/// stored verbatim (<see cref="RawJson"/>); no SDE mapping or internal model for this stage (focus
/// is ESI + permissions + sharing). <see cref="OwnerId"/> = ESI character id.
/// </summary>
public sealed class LocalFitting : IOwnedEntity
{
    public int Id { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public int EsiFittingId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int ShipTypeId { get; set; }
    public string RawJson { get; set; } = "{}";
    public DateTimeOffset ImportedAt { get; set; }

    /// <summary>Optional free-text notes the user adds to a fit (fit-metadata). App-local — never sent to ESI and not
    /// part of the content fingerprint, so editing it never changes the fit's identity.</summary>
    public string? Description { get; set; }

    /// <summary>Optional comma-separated user tags (fit-metadata). App-local; like the description, ignored by the
    /// content hash, so tagging never changes identity.</summary>
    public string? Tags { get; set; }

    /// <summary>Order-independent content fingerprint: the dedup key across import/share/download,
    /// owner- and ESI-id-agnostic. Empty on rows written before the column existed until the startup backfill runs.</summary>
    public string ContentHash { get; set; } = string.Empty;
}
