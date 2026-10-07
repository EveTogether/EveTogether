using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Entities;

/// <summary>An item name no SDE type carries yet (a new event item, ET-460). It is not a loot entry: without a type it
/// has no value and nothing downstream can total it, so it waits here — name and amount only, never the pasted text —
/// until a later SDE knows the name. A line that belongs to a capture then moves into that capture as a real entry and
/// stays behind as <see cref="UnrecognisedItemStatus.Resolved"/>, so what was recognised, when, and as what can be read
/// back afterwards.</summary>
public sealed class UnrecognisedLootLine
{
    public Guid Id { get; set; }

    /// <summary>The capture the name was copied into; null for an input that belongs to no run.</summary>
    public Guid? RunLootCaptureId { get; set; }

    /// <summary>The character the loot was copied for, so the log can say whose it was after the run is saved.</summary>
    public long? CharacterId { get; set; }

    public UnrecognisedItemSource Source { get; set; }
    public string Name { get; set; } = string.Empty;
    public long Quantity { get; set; }
    public DateTime FirstSeenAtUtc { get; set; }
    public UnrecognisedItemStatus Status { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }
    public int? ResolvedTypeId { get; set; }

    /// <summary>The cached average price per unit when the name was recognised; null when there was none yet.</summary>
    public decimal? ResolvedUnitPrice { get; set; }

    /// <summary>The entry this line became. Local only — a run read back from a server gets new entry ids.</summary>
    public Guid? ResolvedRunLootEntryId { get; set; }

    /// <summary>For a mission reward, the run it was paid on and the reward row that took the type id. Local only,
    /// and set only once recognised: until then the reward row itself is the open record.</summary>
    public Guid? RunId { get; set; }

    public Guid? ResolvedRunParameterId { get; set; }

    public RunLootCapture? RunLootCapture { get; set; }
}
