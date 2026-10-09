namespace EveUtils.Shared.Modules.Runs.Entities;

/// <summary>
/// One pilot's combat over one run, kept at SAVE (ET-467): the per-second series, the biggest hits, and how every
/// shot landed per counterparty and weapon. A run saved before this existed simply has no row.
/// </summary>
public sealed class RunCombatTimeline
{
    public Guid RunId { get; set; }

    /// <summary>
    /// The length of every series: one value per second from the run's start up to and including its stop.
    /// </summary>
    public int Seconds { get; set; }

    public int MaxHitOut { get; set; }
    public string? MaxHitOutTarget { get; set; }
    public int MaxHitIn { get; set; }
    public string? MaxHitInSource { get; set; }
    public int HitsOut { get; set; }
    public int MissesOut { get; set; }
    public int HitsIn { get; set; }
    public int MissesIn { get; set; }
    public ICollection<RunCombatSeries> Series { get; set; } = [];
    public ICollection<RunHitTally> HitTallies { get; set; } = [];
}
