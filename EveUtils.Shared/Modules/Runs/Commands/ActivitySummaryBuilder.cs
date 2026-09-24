using System.Security.Cryptography;
using System.Text;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using EveUtils.Shared.Modules.Runs.Tally;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>One activity's <see cref="ActivitySummary"/> out of its saved runs — the stored read model the runs
/// overview and the dashboard read, and (ET-311) what a server tab builds unstored from the runs a server hands
/// back, so both tabs add an activity up the same way.</summary>
internal static class ActivitySummaryBuilder
{
    /// <summary>An id derived from the key the activity is grouped on rather than drawn fresh. A row that is still
    /// standing keeps whatever id it already has (ET-215); this is for the row built anew — above all an activity
    /// deleted whole, whose row went with it, and then restored: it comes back under the id a screen may still be
    /// holding for it, instead of under a new one nobody knows (ET-222).</summary>
    public static Guid IdFor(string activityKey) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"activity-summary:{activityKey}")).AsSpan(0, 16));

    public static ActivitySummary Build(string activityKey, IReadOnlyList<Run> runs,
        ILookup<Guid, RunParameter> parametersByRun, IReadOnlyDictionary<int, double> prices, MiningOreTypes ores,
        ILookup<Guid, LocalKillmail> lossesByRun)
    {
        Run source = runs.OrderBy(run => run.StartedAtUtc).ThenBy(run => run.Id).First();
        DateTime startedAtUtc = runs.Min(run => run.StartedAtUtc);
        DateTime? stoppedAtUtc = runs.All(run => run.StoppedAtUtc is not null)
            ? runs.Max(run => run.StoppedAtUtc)
            : null;
        List<LootTallyLine> loot = [.. runs.SelectMany(run => LootTally.Count(RunIskFactsReader.Tally(run)))];
        decimal? gained = RunIskFactsReader.KnownLootValue(loot, LootKind.Gained, prices);
        decimal? lost = RunIskFactsReader.KnownLootValue(loot, LootKind.Lost, prices);
        // Per run, then added up over the activity by each contributor — the same breakdown the open run window and
        // UNFINISHED make, stored so every screen reads this one and none of them adds figures of its own (ET-256).
        // Earliest run first: the order decides which character a reward line copied onto several runs is handed to
        // in the per-character split below (ET-296).
        DateTime nowUtc = DateTime.UtcNow;
        RunIskFacts[] facts = [.. runs
            .OrderBy(run => run.StartedAtUtc).ThenBy(run => run.Id)
            .Select(run => RunIskFactsReader.From(run, parametersByRun[run.Id], prices, ores, lossesByRun[run.Id]))];
        IskBreakdown isk = IskContributors.Breakdown(facts, nowUtc);
        // The same facts once more, split by character rather than summed — no extra query, and per source it adds
        // up to the activity's own breakdown (ET-296).
        IReadOnlyDictionary<long, IskBreakdown> iskByCharacter = IskContributors.BreakdownByCharacter(facts, nowUtc);
        // Runs, for the PayoutEligibleCount column: how many eligible runs the activity holds.
        int payoutEligibleCount = runs.Count(run => run.IsPayoutEligible);
        // Distinct characters, for the expected payout: they differ because ET-130 lets one character hold more than
        // one eligible run in the same activity, and dividing by runs there would shrink everybody's share.
        int payoutEligibleCharacterCount = runs.Where(run => run.IsPayoutEligible)
            .Select(run => run.CharacterId).Distinct().Count();

        return new ActivitySummary
        {
            Id = IdFor(activityKey),
            GroupCode = source.GroupCode,
            RunId = source.GroupCode is null ? source.Id : null,
            ActivityKind = source.ActivityKind,
            SiteTypeId = source.SiteTypeId,
            SiteName = source.SiteName,
            SignatureGroupSnapshot = source.SignatureGroupSnapshot,
            SolarSystemId = source.SolarSystemId,
            StartedAtUtc = startedAtUtc,
            StoppedAtUtc = stoppedAtUtc,
            DurationSeconds = stoppedAtUtc is null ? 0 : Math.Max(0, (int)(stoppedAtUtc.Value - startedAtUtc).TotalSeconds),
            RunsIncluded = runs.Count,
            ParticipantCount = runs.Select(run => run.CharacterId).Distinct().Count(),
            PayoutEligibleCount = payoutEligibleCount,
            LootIskGained = gained,
            LootIskLost = lost,
            LootIskNet = gained is null && lost is null ? null : gained.GetValueOrDefault() - lost.GetValueOrDefault(),
            LootEntriesWithoutPrice = loot.Count(line => !prices.ContainsKey(line.ItemTypeId)),
            LootItemCount = checked((int)loot.Sum(line => line.Quantity.GetValueOrDefault())),
            // The volume column of an EVE inventory is already the volume of the whole stack (measured: 2 filaments = 0,20 m3).
            LootVolume = loot.Sum(line => line.Volume.GetValueOrDefault()),
            BountyIsk = runs.SelectMany(run => run.BountyEntries).Sum(entry => entry.Isk),
            // Same equal split RunPayoutSplit.Apply makes over the running run's participants — over eligible
            // characters here, since this read model has no per-participant rows to divide onto.
            ExpectedPayoutIsk = payoutEligibleCharacterCount > 0 && gained is { } total ? total / payoutEligibleCharacterCount : 0m,
            TotalIsk = isk.HasFigure ? isk.Total : null,
            IskContributions = StoredIskBreakdown.Write(isk),
            IskContributionsByCharacter = StoredIskBreakdown.WriteByCharacter(iskByCharacter),
            IskSources = IskContributors.Signature,
            EnemyTypeCount = runs.SelectMany(run => run.EnemyObservations).Select(observation => observation.EnemyTypeId).Distinct().Count(),
            CompletenessUnknown = source.GroupCode is not null,
            ComputedAtUtc = DateTime.UtcNow,
            SourceRevisionSum = checked(runs.Sum(run => run.Revision))
        };
    }
}
