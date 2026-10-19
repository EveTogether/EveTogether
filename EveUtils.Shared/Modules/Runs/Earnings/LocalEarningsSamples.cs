using System.Globalization;
using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using EveUtils.Shared.Modules.Runs.Tally;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Earnings;

/// <summary>
/// The earnings samples of the own saved activities (ET-513): one per activity (<c>GroupCode ?? Id</c>), the whole
/// activity's take, so two own characters in one fleet group are one sample and never two (ET-274). The same reading
/// is what a publisher shares and a loot table counts (ET-515, ET-519).
/// </summary>
internal static class LocalEarningsSamples
{
    /// <summary>Every activity with a key that completed since <paramref name="sinceUtc"/>.</summary>
    public static async Task<IReadOnlyList<ActivityEarningsSample>> ReadAsync(ClientDbContext db, ISdeAccessor sde,
        DateTime sinceUtc, DateTime nowUtc, CancellationToken cancellationToken)
    {
        IQueryable<Run> saved = db.Set<Run>()
            .AsNoTracking()
            .Where(run => run.State == RunState.Saved && !run.DeletedAtUtc.HasValue
                          && run.ActivityKind != ActivityKind.Mining && run.ActivityKind != ActivityKind.Free);
        IQueryable<Run> recent = saved.Where(run => (run.StoppedAtUtc ?? run.StartedAtUtc) >= sinceUtc);
        // Whole activities: a group's run that stopped just before the window still belongs to the take of the group.
        List<Run> runs = await saved
            .Where(run => recent.Any(member => member.Id == run.Id || (run.GroupCode != null && member.GroupCode == run.GroupCode)))
            .Include(run => run.LootCaptures)
                .ThenInclude(capture => capture.Entries)
            .Include(run => run.BountyEntries)
            .Include(run => run.MiningEntries)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);
        if (runs.Count == 0)
            return [];

        List<Guid> runIds = [.. runs.Select(run => run.Id)];
        ILookup<Guid, RunParameter> parametersByRun = (await db.Set<RunParameter>()
            .AsNoTracking()
            .Where(parameter => runIds.Contains(parameter.RunId))
            .ToListAsync(cancellationToken)).ToLookup(parameter => parameter.RunId);
        HashSet<Guid> captureIds = [.. runs.SelectMany(run => run.LootCaptures).Select(capture => capture.Id)];
        Dictionary<Guid, int> openUnrecognisedByCapture = (await db.Set<UnrecognisedLootLine>()
                .AsNoTracking()
                .Where(line => line.Status == UnrecognisedItemStatus.Open && line.RunLootCaptureId != null)
                .Select(line => line.RunLootCaptureId.GetValueOrDefault())
                .ToListAsync(cancellationToken))
            .Where(captureIds.Contains)
            .GroupBy(captureId => captureId)
            .ToDictionary(capture => capture.Key, capture => capture.Count());

        Func<int, bool> isCharge = ChargeTypes.Of(sde);
        MiningOreTypes ores = RunIskFactsReader.OresOf(runs, sde);
        Dictionary<string, IReadOnlyList<SdeSite>> sitesByName = new(StringComparer.Ordinal);
        IReadOnlyList<SdeSite> SitesNamed(string name) =>
            sitesByName.TryGetValue(name, out IReadOnlyList<SdeSite>? sites)
                ? sites
                : sitesByName[name] = sde.IsAvailable ? sde.FindSitesByExactName(name) : [];

        List<ActivityEarningsSample> samples = [];
        foreach (IGrouping<string, Run> activity in runs.GroupBy(run => run.GroupCode ?? run.Id.ToString()))
        {
            Run[] ordered = [.. activity.OrderBy(run => run.StartedAtUtc).ThenBy(run => run.Id)];
            Run source = ordered[0];
            if (ActivityStatsKeys.For(source, parametersByRun[source.Id], SitesNamed) is not { } key)
                continue;

            samples.Add(new ActivityEarningsSample(key, source.SiteName,
                _Sample(key, ordered, parametersByRun, ores, isCharge, openUnrecognisedByCapture, nowUtc)));
        }

        return samples;
    }

    private static EarningsSample _Sample(ActivityStatsKey key, IReadOnlyList<Run> runs, ILookup<Guid, RunParameter> parametersByRun,
        MiningOreTypes ores, Func<int, bool> isCharge, IReadOnlyDictionary<Guid, int> openUnrecognisedByCapture, DateTime nowUtc)
    {
        // No live prices: loot is valued later at today's price, and an item reward counts at the price fixed on it
        // (ET-509) — one the hourly fill has not reached yet counts nothing, as it does in TOTAL ISK.
        IskBreakdown isk = IskContributors.Breakdown(
            [.. runs.Select(run => RunIskFactsReader.From(run, parametersByRun[run.Id], new Dictionary<int, double>(), ores, [],
                isCharge))],
            nowUtc);
        LootTallyLine[] loot = [.. runs.SelectMany(run => LootTally.Count(RunIskFactsReader.Tally(run), isCharge,
            RunIskFactsReader.SpentFilament(parametersByRun[run.Id])).Loot)];
        bool stopped = runs.All(run => run.StoppedAtUtc is not null);
        DateTime startedAtUtc = runs.Min(run => run.StartedAtUtc);
        DateTime? stoppedAtUtc = stopped ? runs.Max(run => run.StoppedAtUtc) : null;
        return new EarningsSample
        {
            PilotBucket = runs[0].CharacterId.ToString(CultureInfo.InvariantCulture),
            CompletedAtUtc = stoppedAtUtc ?? runs.Max(run => run.StoppedAtUtc ?? run.StartedAtUtc),
            // The activity's own span, as its summary measures it (ActivitySummaryBuilder).
            DurationSeconds = stoppedAtUtc is { } end ? Math.Max(0, (int)(end - startedAtUtc).TotalSeconds) : null,
            FixedIsk = new[] { IskSource.Bounty, IskSource.Rewards, IskSource.HomefrontPayout }
                .Sum(source => isk.Of(source)?.Amount ?? 0m),
            LootGained = _Merged(loot, LootKind.Gained),
            LootLost = _Merged(loot, LootKind.Lost),
            Mined = key.Kind is ActivityStatsKind.Homefront ? _Mined(runs, ores) : [],
            LootBasis = runs.Min(_LootBasis),
            OpenUnrecognisedLootCount = runs
                .SelectMany(run => run.LootCaptures)
                .Sum(capture => openUnrecognisedByCapture.GetValueOrDefault(capture.Id)),
            FleetSize = runs.Max(run => run.FleetSizeAtStop) ?? runs.Select(run => run.CharacterId).Distinct().Count()
        };
    }

    // Without a starting hold the tally hands back every paste's lines one after another (LootTally.Count).
    // Units only: residue was depleted but never collected (RunMiningEntry). An ore the SDE cannot name has no type to
    // price and adds nothing, the "not priced yet, not zero" rule MiningValue follows.
    private static IReadOnlyList<EarningsLootLine> _Mined(IEnumerable<Run> runs, MiningOreTypes ores) =>
        [.. runs
            .SelectMany(run => run.MiningEntries)
            .SelectMany(entry => ores.Of(entry.OreType) is { } ore && entry.Units > 0
                ? [new EarningsLootLine(ore.TypeId, entry.Units)]
                : Array.Empty<EarningsLootLine>())
            .GroupBy(line => line.TypeId)
            .Select(type => new EarningsLootLine(type.Key, type.Sum(line => line.Quantity)))];

    private static IReadOnlyList<EarningsLootLine> _Merged(IEnumerable<LootTallyLine> loot, LootKind kind) =>
        [.. loot
            .Where(line => line.LootKind == kind)
            .GroupBy(line => line.ItemTypeId)
            .Select(type => new EarningsLootLine(type.Key, type.Sum(line => line.Quantity ?? 1)))];

    private static LootBasis _LootBasis(Run run)
    {
        IReadOnlyList<LootTallyCapture> captures = RunIskFactsReader.Tally(run);
        (int before, int after) = LootTally.Ends(captures);
        if (before >= 0)
            return after >= 0 ? LootBasis.CargoDifference : LootBasis.None;

        return captures.Any(capture => !capture.IsExcluded && capture.Role is not LootCaptureRole.Consumed)
            ? LootBasis.Captures
            : LootBasis.None;
    }
}
