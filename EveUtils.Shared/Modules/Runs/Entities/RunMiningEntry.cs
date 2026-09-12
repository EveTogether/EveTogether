namespace EveUtils.Shared.Modules.Runs.Entities;

/// <summary>One ore, aggregated for one run (ET-229) — not one row per mining cycle, since a single site is on the
/// order of a hundred cycles per character. General for any mining, not only a homefront: the ore's own capacity
/// (a Metaliminal asteroid's 5,000 units) is a fact of the site, never stored here.</summary>
public sealed class RunMiningEntry
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public required string OreType { get; set; }
    public int Units { get; set; }

    /// <summary>Already counted in <see cref="Units"/> — a critical cycle is extra yield on top of what the asteroid
    /// gave up, never additional units to add again.</summary>
    public int CriticalUnits { get; set; }

    /// <summary>Depleted from the asteroid but never collected — no ISK value, kept apart from <see cref="Units"/>
    /// so a valuation never counts it and a capacity check (a homefront's known 5,000 units) can still add it back.</summary>
    public int ResidueUnits { get; set; }

    /// <summary>The line's own timestamp, not when it was read — stored for ET-230's evidence, never shown
    /// (Jithran, 2026-09-11: the activity already runs start to stop).</summary>
    public DateTime FirstObservedAtUtc { get; set; }

    public DateTime LastObservedAtUtc { get; set; }
    public Run? Run { get; set; }
}
