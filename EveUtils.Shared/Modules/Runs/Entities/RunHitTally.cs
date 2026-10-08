using EveUtils.Shared.Modules.Gamelog.Models;

namespace EveUtils.Shared.Modules.Runs.Entities;

/// <summary>
/// Every shot of one run with the same direction, counterparty, weapon and hit quality, added up (ET-467, B8).
/// What a per-target, per-weapon breakdown needs, at a size that grows with the enemies rather than the run's length.
/// </summary>
public sealed class RunHitTally
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public DamageDirection Direction { get; set; }

    /// <summary>
    /// The target of a shot going out, the source of one coming in, as the line names it.
    /// </summary>
    public string Counterparty { get; set; } = string.Empty;

    /// <summary>
    /// Null where the line names none: drones and most NPC fire coming in.
    /// </summary>
    public string? Weapon { get; set; }

    public HitQuality Quality { get; set; }
    public int Count { get; set; }
    public long Sum { get; set; }
    public int Min { get; set; }
    public int Max { get; set; }
}
