using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Tally;
using EveUtils.Shared.Modules.Sde;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Isk;

/// <summary>A stored run's facts, read the same way for a saved activity (<c>RebuildActivitySummariesCommandHandler</c>)
/// and an unfinished run (<c>GetUnfinishedRunsQueryHandler</c>). The run must come with its loot captures and their
/// entries, its bounty entries and its mining entries loaded; its parameters and linked losses are handed in, since a
/// caller reading many runs at once reads those apart.</summary>
internal static class RunIskFactsReader
{
    public static RunIskFacts From(Run run, IEnumerable<RunParameter> parameters, IReadOnlyDictionary<int, double> prices,
        MiningOreTypes ores, IEnumerable<LinkedLoss> losses, Func<int, bool> isCharge)
    {
        LinkedLoss[] linked = [.. losses];
        RunParameter[] all = [.. parameters];
        RunPrices runPrices = RunPrices.Of(run, all, prices);
        LootTallyCount counted = LootTally.Count(Tally(run), isCharge, SpentFilament(all));
        IReadOnlyList<LootTallyLine> loot = counted.Loot;
        decimal? gained = KnownLootValue(loot, LootKind.Gained, runPrices.Loot);
        decimal? lost = KnownLootValue(loot, LootKind.Lost, runPrices.Loot);
        int? filamentCount = _ParsedInt(all, RunParameterKey.AbyssalFilamentCount);
        decimal? filamentCost = filamentCount is > 0 && FilamentTypeId(all) is { } typeId
            && runPrices.Filament(typeId) is { } price
            ? price * filamentCount.Value
            : null;
        // The charges fired (ET-471) leave LOOT and land here, at the same fixed price, so TOTAL ISK stays as it was.
        IReadOnlyList<LootTallyLine> spent = [.. Spent(run), .. counted.SpentCharges];
        decimal? spentCost = KnownLootValue(spent, LootKind.Lost, runPrices.Loot);
        decimal? consumableCost = filamentCost is null && spentCost is null
            ? null
            : filamentCost.GetValueOrDefault() + spentCost.GetValueOrDefault();
        return new RunIskFacts
        {
            CharacterId = run.CharacterId,
            BountyIsk = run.BountyEntries.Sum(entry => entry.Isk),
            LootIskNet = gained is null && lost is null ? null : gained.GetValueOrDefault() - lost.GetValueOrDefault(),
            HasLoot = loot.Count > 0,
            ConsumableIskCost = consumableCost,
            HasConsumables = filamentCount is > 0 || spent.Count > 0,
            ShipLossIskCost = LossValue(linked, prices),
            HasShipLoss = linked.Length > 0,
            MiningIskValue = MiningValue(run.MiningEntries, ores, prices),
            HasMining = run.MiningEntries.Count > 0,
            Parameters = [.. all.Select(parameter => new RunIskParameter(
                parameter.ParameterKey, parameter.Amount, parameter.BonusWindowSeconds, parameter.ObservedAtUtc))],
            StoppedAtUtc = run.StoppedAtUtc,
            HomefrontExpectedPayoutIsk = HomefrontExpectedPayout(run)
        };
    }

    /// <summary>Every type a set of runs needs a price for — loot kept in the tally, each run's resolved filament
    /// (ET-249) and each ore resolved by its exact SDE name, never guessed (ET-229) — so one price read serves them
    /// all, and the stored summary and the detail screen's per-character figures price the same way. Linked losses add
    /// their hulls and items (ET-331).</summary>
    public static IReadOnlyList<int> PricedTypeIds(IEnumerable<Run> runs, IEnumerable<RunParameter> parameters, MiningOreTypes ores,
        IEnumerable<LinkedLoss> losses)
    {
        IEnumerable<int> loot = runs
            .SelectMany(run => run.LootCaptures)
            .Where(capture => !capture.IsExcluded)
            .SelectMany(capture => capture.Entries)
            .Select(entry => entry.ItemTypeId);
        IEnumerable<int> filaments = parameters
            .GroupBy(parameter => parameter.RunId)
            .Select(FilamentTypeId)
            .OfType<int>();
        return [.. loot.Concat(filaments).Concat(ores.TypeIds)
            .Concat(LossLines(losses.Select(loss => loss.Killmail)).Select(line => line.ItemTypeId)).Distinct()];
    }

    /// <summary>The own losses linked to these runs (ET-331), items included, with the prices fixed when each was linked
    /// (ET-464), by run. Only a client store has them; a run read back from a server never carries one.</summary>
    public static async Task<ILookup<Guid, LinkedLoss>> LinkedLossesAsync(ClientDbContext db, IReadOnlyCollection<Guid> runIds,
        CancellationToken cancellationToken)
    {
        List<LocalKillmail> losses = await LinkedKillmailsAsync(db, runIds, cancellationToken);
        ILookup<Guid, RunLossPrice> pricesByRun = (await db.Set<RunLossPrice>()
                .AsNoTracking()
                .Where(price => runIds.Contains(price.RunId))
                .ToListAsync(cancellationToken))
            .ToLookup(price => price.RunId);
        return losses
            .Select(killmail => (RunId: killmail.RunId.GetValueOrDefault(), Killmail: killmail))
            .ToLookup(loss => loss.RunId, loss => LinkedLoss.Of(loss.Killmail, pricesByRun[loss.RunId]));
    }

    public static Task<List<LocalKillmail>> LinkedKillmailsAsync(ClientDbContext db, IReadOnlyCollection<Guid> runIds,
        CancellationToken cancellationToken) =>
        db.Set<LocalKillmail>()
            .AsNoTracking()
            .Include(killmail => killmail.Items)
            .Where(killmail => killmail.IsLoss && killmail.RunId != null && runIds.Contains(killmail.RunId.Value))
            .ToListAsync(cancellationToken);

    /// <summary>Everything a loss cost, as lost lines priced like loot: the hull once, and every item whether it was
    /// destroyed or dropped, since a drop in the abyss is gone as well.</summary>
    public static IReadOnlyList<LootTallyLine> LossLines(IEnumerable<LocalKillmail> losses) =>
        [.. losses.SelectMany(loss => loss.Items
            .Select(item => new LootTallyLine(item.TypeId, item.QuantityDestroyed + item.QuantityDropped, null, LootKind.Lost))
            .Prepend(new LootTallyLine(loss.VictimShipTypeId, 1, null, LootKind.Lost)))];

    /// <summary>Every ore the runs mined, each looked up in the SDE once for the whole read.</summary>
    public static MiningOreTypes OresOf(IEnumerable<Run> runs, ISdeAccessor sde) =>
        MiningOreTypes.Resolve(runs.SelectMany(run => run.MiningEntries).Select(entry => entry.OreType), sde);

    /// <summary>What the curve owes this run's own character right now (ET-231), read the same way for a saved
    /// activity and the open run window (<c>ActivityWindowViewModel</c> reads the identical fact off its own
    /// participant rows, kept in step with the run's own columns).</summary>
    public static decimal? HomefrontExpectedPayout(Run run) =>
        HomefrontCatalogue.KindByDungeonId.TryGetValue(run.SiteTypeId, out string? kind)
            && HomefrontPayoutTable.TryGetExpected(kind, run.InSiteAtCompletion, run.AttendanceCount,
                run.HomefrontOutcome, run.HomefrontCompletedWaveCount, run.StoppedAtUtc ?? DateTime.UtcNow) is { } expected
            ? expected.Amount
            : null;

    /// <summary>Priced mining, ore by ore (ET-229): the price fixed on the entry (ET-463), else the resolved
    /// by-exact-SDE-name type id decides it (<see cref="MiningValuation"/>), residue never counts (depleted, never collected, no ISK value), and a
    /// critical cycle's units are not added twice — they are already inside <see cref="RunMiningEntry.Units"/>.
    /// Null only when nothing on the run could be priced, the same "not priced yet, not zero" rule loot follows.</summary>
    public static decimal? MiningValue(IEnumerable<RunMiningEntry> entries, MiningOreTypes ores, IReadOnlyDictionary<int, double> prices)
    {
        decimal[] values = [.. entries
            .Select(entry => (Entry: entry, Price: entry.UnitPriceIsk ?? (ores.Of(entry.OreType) is { } ore
                ? MiningValuation.UnitPrice(ore.TypeId, ore.IsMutanite, prices)
                : null)))
            .Where(resolved => resolved.Price is not null)
            .Select(resolved => resolved.Price!.Value * resolved.Entry.Units)];
        return values.Length == 0 ? null : values.Sum();
    }

    public static decimal? MiningValue(IEnumerable<RunMiningEntry> entries, ISdeAccessor sde, IReadOnlyDictionary<int, double> prices)
    {
        RunMiningEntry[] all = [.. entries];
        return MiningValue(all, MiningOreTypes.Resolve(all.Select(entry => entry.OreType), sde), prices);
    }

    /// <summary>The resolved filament type a run's CONSUMABLES was saved against (ET-249), so a caller pricing many
    /// runs at once can collect it into the same type-id set loot pricing already builds.</summary>
    public static int? FilamentTypeId(IEnumerable<RunParameter> parameters) =>
        _ParsedInt(parameters, RunParameterKey.AbyssalFilamentTypeId);

    /// <summary>The filament CONSUMABLES counts for a run, for <see cref="LootTally"/> to take out of LOOT (ET-483).</summary>
    public static (int TypeId, int Count)? SpentFilament(IEnumerable<RunParameter> parameters) =>
        _ParsedInt(parameters, RunParameterKey.AbyssalFilamentCount) is > 0 and var count && FilamentTypeId(parameters) is { } typeId
            ? (typeId, count)
            : null;

    private static int? _ParsedInt(IEnumerable<RunParameter> parameters, RunParameterKey key) =>
        parameters.FirstOrDefault(parameter => parameter.ParameterKey == key)?.TypedValue is { } value
        && int.TryParse(value, out int parsed)
            ? parsed
            : null;

    /// <summary>What the run's pilot wrote out as spent besides the filament (ET-334), priced like the filament and
    /// the loot by type id and never counted as loot.</summary>
    public static IReadOnlyList<LootTallyLine> Spent(Run run) =>
        [.. run.LootCaptures
            .Where(capture => capture.Role is LootCaptureRole.Consumed && !capture.IsExcluded)
            .SelectMany(capture => capture.Entries)
            .Select(entry => new LootTallyLine(entry.ItemTypeId, entry.Quantity, entry.Volume, LootKind.Lost))];

    // Counted per run, not per activity: a starting cargo hold belongs to the run it was pasted on, and two grouped
    // runs each have their own. The rule itself is LootTally's, shared with the open window.
    public static IReadOnlyList<LootTallyCapture> Tally(Run run) =>
        [.. run.LootCaptures
            .OrderBy(capture => capture.CapturedAtUtc)
            .Select(capture => new LootTallyCapture(capture.Role, capture.IsExcluded,
                [.. capture.Entries.Select(entry =>
                    new LootTallyLine(entry.ItemTypeId, entry.Quantity, entry.Volume, entry.LootKind))]))];

    /// <summary>What the linked losses cost, each line at the price fixed when its loss was linked (ET-464), else at
    /// the live one; null when nothing could be priced.</summary>
    public static decimal? LossValue(IEnumerable<LinkedLoss> losses, IReadOnlyDictionary<int, double> live)
    {
        decimal?[] values = [.. losses.Select(loss =>
            KnownLootValue(LossLines([loss.Killmail]), LootKind.Lost, typeId => loss.UnitPrice(typeId, live)))];
        return values.Any(value => value is not null) ? values.Sum() : null;
    }

    // Valuation goes through ET's own type-id lookup, never the clipboard's own ISK column, and a missing price counts
    // as nothing rather than as a wrong figure. GetValueOrDefault(), not ?? 1: a missing quantity counts as zero
    // pieces in a summary's item count, so it must value as zero here too.
    public static decimal? KnownLootValue(
        IEnumerable<LootTallyLine> loot, LootKind lootKind, IReadOnlyDictionary<int, double> prices) =>
        KnownLootValue(loot, lootKind, typeId => prices.TryGetValue(typeId, out double price) ? (decimal)price : null);

    public static decimal? KnownLootValue(IEnumerable<LootTallyLine> loot, LootKind lootKind, Func<int, decimal?> unitPrice)
    {
        decimal[] values = [.. loot
            .Where(line => line.LootKind == lootKind)
            .Select(line => unitPrice(line.ItemTypeId) * line.Quantity.GetValueOrDefault())
            .OfType<decimal>()];
        return values.Length == 0 ? null : values.Sum();
    }

    /// <summary>Whether any part of the run is still valued at the live cache price (ET-463): a loot, ore, filament or
    /// linked-loss line with no fixed price yet (ET-464). Only such a run can come out differently when it is added up
    /// again after a price refresh.</summary>
    public static bool HasLiveValue(Run run, IEnumerable<RunParameter> parameters, IEnumerable<LinkedLoss> losses) =>
        run.LootCaptures.Where(capture => !capture.IsExcluded).SelectMany(capture => capture.Entries).Any(entry => entry.UnitPriceIsk is null)
        || run.MiningEntries.Any(entry => entry.UnitPriceIsk is null)
        || RunPrices.FilamentRow(parameters) is { UnitPriceIsk: null }
        || losses.Any(loss => loss.HasLivePrice);
}
