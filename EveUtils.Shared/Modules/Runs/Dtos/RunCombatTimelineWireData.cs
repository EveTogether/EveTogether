using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Dtos;

/// <summary>A run's stored combat travelling with the run (ET-472), blobs as stored so a fleet mate reads them back byte-equal.</summary>
public sealed class RunCombatTimelineWireData
{
    public required int Seconds { get; init; }
    public int MaxHitOut { get; init; }
    public string? MaxHitOutTarget { get; init; }
    public int MaxHitIn { get; init; }
    public string? MaxHitInSource { get; init; }
    public int HitsOut { get; init; }
    public int MissesOut { get; init; }
    public int HitsIn { get; init; }
    public int MissesIn { get; init; }
    public IReadOnlyList<SeriesWire> Series { get; init; } = [];
    public IReadOnlyList<HitTallyWire> HitTallies { get; init; } = [];

    public sealed record SeriesWire(CombatSeriesKind Kind, long Total, byte[] Samples);

    public sealed record HitTallyWire(DamageDirection Direction, string Counterparty, string? Weapon, HitQuality Quality,
        int Count, long Sum, int Min, int Max);

    public static RunCombatTimelineWireData FromEntity(RunCombatTimeline timeline) => new()
    {
        Seconds = timeline.Seconds,
        MaxHitOut = timeline.MaxHitOut,
        MaxHitOutTarget = timeline.MaxHitOutTarget,
        MaxHitIn = timeline.MaxHitIn,
        MaxHitInSource = timeline.MaxHitInSource,
        HitsOut = timeline.HitsOut,
        MissesOut = timeline.MissesOut,
        HitsIn = timeline.HitsIn,
        MissesIn = timeline.MissesIn,
        Series = [.. timeline.Series.OrderBy(series => series.Kind)
            .Select(series => new SeriesWire(series.Kind, series.Total, series.Samples))],
        HitTallies = [.. timeline.HitTallies
            .OrderBy(tally => tally.Direction).ThenBy(tally => tally.Counterparty, StringComparer.Ordinal)
            .ThenBy(tally => tally.Weapon, StringComparer.Ordinal).ThenBy(tally => tally.Quality)
            .Select(tally => new HitTallyWire(tally.Direction, tally.Counterparty,
                tally.Weapon, tally.Quality, tally.Count, tally.Sum, tally.Min, tally.Max))]
    };

    public RunCombatTimeline ToEntity(Guid runId) => new()
    {
        RunId = runId,
        Seconds = Seconds,
        MaxHitOut = MaxHitOut,
        MaxHitOutTarget = MaxHitOutTarget,
        MaxHitIn = MaxHitIn,
        MaxHitInSource = MaxHitInSource,
        HitsOut = HitsOut,
        MissesOut = MissesOut,
        HitsIn = HitsIn,
        MissesIn = MissesIn,
        Series = [.. Series.Select(series => new RunCombatSeries
        {
            Id = Guid.CreateVersion7(), RunId = runId, Kind = series.Kind, Total = series.Total, Samples = series.Samples
        })],
        HitTallies = [.. HitTallies.Select(tally => new RunHitTally
        {
            Id = Guid.CreateVersion7(), RunId = runId, Direction = tally.Direction, Counterparty = tally.Counterparty,
            Weapon = tally.Weapon, Quality = tally.Quality, Count = tally.Count, Sum = tally.Sum, Min = tally.Min,
            Max = tally.Max
        })]
    };
}
