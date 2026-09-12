using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Tally;

namespace EveUtils.Shared.Modules.Runs.Isk;

/// <summary>A stored run's facts, read the same way for a saved activity (<c>RebuildActivitySummariesCommandHandler</c>)
/// and an unfinished run (<c>GetUnfinishedRunsQueryHandler</c>). The run must come with its loot captures and their
/// entries and its bounty entries loaded; its parameters are handed in, since a caller reading many runs at once reads
/// those apart.</summary>
internal static class RunIskFactsReader
{
    public static RunIskFacts From(Run run, IEnumerable<RunParameter> parameters, IReadOnlyDictionary<int, double> prices)
    {
        IReadOnlyList<LootTallyLine> loot = LootTally.Count(Tally(run));
        decimal? gained = KnownLootValue(loot, LootKind.Gained, prices);
        decimal? lost = KnownLootValue(loot, LootKind.Lost, prices);
        return new RunIskFacts
        {
            BountyIsk = run.BountyEntries.Sum(entry => entry.Isk),
            LootIskNet = gained is null && lost is null ? null : gained.GetValueOrDefault() - lost.GetValueOrDefault(),
            HasLoot = loot.Count > 0,
            Parameters = [.. parameters.Select(parameter => new RunIskParameter(
                parameter.ParameterKey, parameter.Amount, parameter.BonusWindowSeconds, parameter.ObservedAtUtc))],
            StoppedAtUtc = run.StoppedAtUtc
        };
    }

    // Counted per run, not per activity: a starting cargo hold belongs to the run it was pasted on, and two grouped
    // runs each have their own. The rule itself is LootTally's, shared with the open window.
    public static IReadOnlyList<LootTallyCapture> Tally(Run run) =>
        [.. run.LootCaptures
            .OrderBy(capture => capture.CapturedAtUtc)
            .Select(capture => new LootTallyCapture(capture.Role, capture.IsExcluded,
                [.. capture.Entries.Select(entry =>
                    new LootTallyLine(entry.ItemTypeId, entry.Quantity, entry.Volume, entry.LootKind))]))];

    // Valuation goes through ET's own type-id lookup, never the clipboard's own ISK column, and a missing price counts
    // as nothing rather than as a wrong figure. GetValueOrDefault(), not ?? 1: a missing quantity counts as zero
    // pieces in a summary's item count, so it must value as zero here too.
    public static decimal? KnownLootValue(
        IEnumerable<LootTallyLine> loot, LootKind lootKind, IReadOnlyDictionary<int, double> prices)
    {
        decimal[] values = [.. loot
            .Where(line => line.LootKind == lootKind && prices.ContainsKey(line.ItemTypeId))
            .Select(line => (decimal)prices[line.ItemTypeId] * line.Quantity.GetValueOrDefault())];
        return values.Length == 0 ? null : values.Sum();
    }
}
