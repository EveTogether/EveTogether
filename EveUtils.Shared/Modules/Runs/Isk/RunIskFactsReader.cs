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
        MiningOreTypes ores, IEnumerable<LocalKillmail> losses)
    {
        IReadOnlyList<LootTallyLine> lostInLosses = LossLines(losses);
        RunParameter[] all = [.. parameters];
        IReadOnlyList<LootTallyLine> loot = LootTally.Count(Tally(run));
        decimal? gained = KnownLootValue(loot, LootKind.Gained, prices);
        decimal? lost = KnownLootValue(loot, LootKind.Lost, prices);
        int? filamentCount = _ParsedInt(all, RunParameterKey.AbyssalFilamentCount);
        decimal? filamentCost = filamentCount is > 0 && FilamentTypeId(all) is { } typeId
            && prices.TryGetValue(typeId, out double price)
            ? (decimal)price * filamentCount.Value
            : null;
        IReadOnlyList<LootTallyLine> spent = Spent(run);
        decimal? spentCost = KnownLootValue(spent, LootKind.Lost, prices);
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
            ShipLossIskCost = KnownLootValue(lostInLosses, LootKind.Lost, prices),
            HasShipLoss = lostInLosses.Count > 0,
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
        IEnumerable<LocalKillmail> losses)
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
            .Concat(LossLines(losses).Select(line => line.ItemTypeId)).Distinct()];
    }

    /// <summary>The own losses linked to these runs (ET-331), items included, by run. Only a client store has them; a
    /// run read back from a server never carries one.</summary>
    public static async Task<ILookup<Guid, LocalKillmail>> LinkedLossesAsync(ClientDbContext db, IReadOnlyCollection<Guid> runIds,
        CancellationToken cancellationToken) =>
        (await db.Set<LocalKillmail>()
            .AsNoTracking()
            .Include(killmail => killmail.Items)
            .Where(killmail => killmail.IsLoss && killmail.RunId != null && runIds.Contains(killmail.RunId.Value))
            .ToListAsync(cancellationToken))
        .ToLookup(killmail => killmail.RunId.GetValueOrDefault());

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

    /// <summary>Priced mining, ore by ore (ET-229): the resolved by-exact-SDE-name type id decides the price
    /// (<see cref="MiningValuation"/>), residue never counts (depleted, never collected, no ISK value), and a
    /// critical cycle's units are not added twice — they are already inside <see cref="RunMiningEntry.Units"/>.
    /// Null only when nothing on the run could be priced, the same "not priced yet, not zero" rule loot follows.</summary>
    public static decimal? MiningValue(IEnumerable<RunMiningEntry> entries, MiningOreTypes ores, IReadOnlyDictionary<int, double> prices)
    {
        decimal[] values = [.. entries
            .Select(entry => (Entry: entry, Price: ores.Of(entry.OreType) is { } ore
                ? MiningValuation.UnitPrice(ore.TypeId, ore.IsMutanite, prices)
                : null))
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
