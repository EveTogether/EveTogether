using System.Globalization;
using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using EveUtils.Shared.Modules.Sde;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>
/// Fixes the unit price on a run's loot, ore and filament lines (ET-463), written once for every way in: a capture as
/// it is stored, the one-time fill of a line that had no price yet, and "Re-value at current prices". The lines are
/// tracked in <paramref name="db"/>; the caller saves, and decides what a change means for the run.
/// </summary>
internal static class RunPriceSnapshots
{
    /// <summary>Which of <paramref name="runIds"/> had a line priced. <see cref="PriceSnapshotSource.Revalued"/>
    /// replaces every price the cache has a newer figure for; <see cref="PriceSnapshotSource.Migrated"/> only prices the
    /// lines the snapshot migration marked and could not price itself; every other source fills a line that has none, and
    /// takes a price the same run already fixed for that type before the cache's, so one type keeps one price per run.
    /// A blueprint is valued by its build appraisal instead of a market price (ET-501) while the user has that on; one
    /// whose build cannot be priced in full stays open rather than take a market price for something it is not.</summary>
    public static async Task<IReadOnlySet<Guid>> FixAsync(ClientDbContext db, IMarketPriceRepository marketPrices,
        IBlueprintAppraisalService blueprints, ISdeAccessor sde, IReadOnlyCollection<Guid> runIds, PriceSnapshotSource source,
        CancellationToken cancellationToken)
    {
        bool replaces = source is PriceSnapshotSource.Revalued;
        bool migrating = source is PriceSnapshotSource.Migrated;
        List<RunLootCapture> captures = await db.Set<RunLootCapture>()
            .Include(capture => capture.Entries)
            .Where(capture => runIds.Contains(capture.RunId))
            .ToListAsync(cancellationToken);
        List<RunMiningEntry> mining = await db.Set<RunMiningEntry>()
            .Where(entry => runIds.Contains(entry.RunId) && (replaces || entry.UnitPriceIsk == null)
                            && (!migrating || entry.PriceSource == PriceSnapshotSource.Migrated))
            .ToListAsync(cancellationToken);
        List<RunParameter> filaments = await db.Set<RunParameter>()
            .Where(parameter => runIds.Contains(parameter.RunId) && parameter.ParameterKey == RunParameterKey.AbyssalFilamentTypeId
                                && (replaces || parameter.UnitPriceIsk == null)
                                && (!migrating || parameter.PriceSource == PriceSnapshotSource.Migrated))
            .ToListAsync(cancellationToken);

        bool IsOpen(RunLootEntry entry) =>
            replaces || (entry.UnitPriceIsk is null && (!migrating || entry.PriceSource is PriceSnapshotSource.Migrated));
        RunLootEntry[] open = [.. captures.SelectMany(capture => capture.Entries).Where(IsOpen)];
        if (open.Length == 0 && mining.Count == 0 && filaments.Count == 0)
            return new HashSet<Guid>();

        MiningOreTypes ores = MiningOreTypes.Resolve(mining.Select(entry => entry.OreType), sde);
        IReadOnlyDictionary<int, decimal?> blueprintValues = await blueprints.GetLootValuesAsync(
            [.. open.Select(entry => entry.ItemTypeId).Distinct()], cancellationToken);
        IReadOnlyDictionary<int, double> live = await marketPrices.GetAveragePricesAsync([.. open
            .Select(entry => entry.ItemTypeId)
            .Concat(ores.TypeIds)
            .Concat(filaments.Select(_FilamentTypeId).OfType<int>())
            .Distinct()], cancellationToken);
        DateTime nowUtc = DateTime.UtcNow;
        HashSet<Guid> priced = [];

        foreach (IGrouping<Guid, RunLootCapture> run in captures.GroupBy(capture => capture.RunId))
        {
            IReadOnlyDictionary<int, RunLootEntry> fixedLines = replaces
                ? new Dictionary<int, RunLootEntry>()
                : _FixedLines(run);
            foreach (RunLootEntry entry in run.SelectMany(capture => capture.Entries).Where(IsOpen))
            {
                (decimal? price, LootPriceBasis basis) = fixedLines.TryGetValue(entry.ItemTypeId, out RunLootEntry? kept)
                    ? (kept.UnitPriceIsk, kept.PriceBasis)
                    : blueprintValues.TryGetValue(entry.ItemTypeId, out decimal? appraised)
                        ? (_Rounded(appraised), LootPriceBasis.BlueprintAppraisal)
                        : (_Rounded(_Live(live, entry.ItemTypeId)), LootPriceBasis.Market);
                if (price is null || (price == entry.UnitPriceIsk && basis == entry.PriceBasis))
                    continue;

                (entry.UnitPriceIsk, entry.PricedAtUtc, entry.PriceSource, entry.PriceBasis) = (price, nowUtc, source, basis);
                priced.Add(run.Key);
            }
        }

        foreach (RunMiningEntry entry in mining)
        {
            decimal? price = ores.Of(entry.OreType) is { } ore ? _Rounded(MiningValuation.UnitPrice(ore.TypeId, ore.IsMutanite, live)) : null;
            if (price is null || price == entry.UnitPriceIsk)
                continue;

            (entry.UnitPriceIsk, entry.PricedAtUtc, entry.PriceSource) = (price, nowUtc, source);
            priced.Add(entry.RunId);
        }

        foreach (RunParameter filament in filaments)
        {
            decimal? price = _FilamentTypeId(filament) is { } typeId ? _Rounded(_Live(live, typeId)) : null;
            if (price is null || price == filament.UnitPriceIsk)
                continue;

            (filament.UnitPriceIsk, filament.PricedAtUtc, filament.PriceSource) = (price, nowUtc, source);
            priced.Add(filament.RunId);
        }

        return priced;
    }

    /// <summary>The price a line is worth as it is stored, saved with it. A run the cache has no price for yet keeps
    /// its lines open, and the hourly refresh fills them (<see cref="FillRunPriceSnapshotsCommand"/>).</summary>
    public static async Task FixOnCaptureAsync(ClientDbContext db, IMarketPriceRepository marketPrices,
        IBlueprintAppraisalService blueprints, ISdeAccessor sde, Guid runId, CancellationToken cancellationToken)
    {
        if ((await FixAsync(db, marketPrices, blueprints, sde, [runId], PriceSnapshotSource.Capture, cancellationToken)).Count > 0)
            await db.SaveChangesAsync(cancellationToken);
    }

    // The line per type whose price the run already fixed, earliest capture first — the order RunPrices.FixedLootPrices
    // values the run by, read with its basis so a later line of the same blueprint says where its price came from too.
    private static Dictionary<int, RunLootEntry> _FixedLines(IEnumerable<RunLootCapture> captures)
    {
        Dictionary<int, RunLootEntry> lines = [];
        foreach (RunLootEntry entry in captures.OrderBy(capture => capture.CapturedAtUtc).SelectMany(capture => capture.Entries))
            if (entry.UnitPriceIsk is not null)
                lines.TryAdd(entry.ItemTypeId, entry);
        return lines;
    }

    private static int? _FilamentTypeId(RunParameter parameter) =>
        int.TryParse(parameter.TypedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int typeId) ? typeId : null;

    private static decimal? _Live(IReadOnlyDictionary<int, double> live, int typeId) =>
        live.TryGetValue(typeId, out double price) ? (decimal)price : null;

    // The columns hold two decimals on every server provider; rounded here, a client's copy and the server's agree.
    private static decimal? _Rounded(decimal? price) => price is { } value ? Math.Round(value, 2, MidpointRounding.AwayFromZero) : null;
}
