using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

/// <summary>
/// Fixes the unit price of every hull and item of the losses linked to a run (ET-464), the way
/// <see cref="RunPriceSnapshots"/> does for its loot, ore and filament. Kept apart from it: a loss never goes to a
/// server, so a loss priced anew is never a correction to publish. The rows are tracked in <paramref name="db"/>; the
/// caller saves.
/// </summary>
internal static class RunLossPriceSnapshots
{
    /// <summary>Which of <paramref name="runIds"/> had a loss price added, dropped or changed. The rows follow the links
    /// as they stand: a loss no longer linked to the run loses its rows, and a linked type without one gets one — at the
    /// cache's price, or none yet. <see cref="PriceSnapshotSource.Backfill"/> also fills a row that has no price yet,
    /// <see cref="PriceSnapshotSource.Revalued"/> replaces every price the cache has a figure for, and every other
    /// source leaves the rows already there as they are.</summary>
    public static async Task<IReadOnlySet<Guid>> FixAsync(ClientDbContext db, IMarketPriceRepository marketPrices,
        IReadOnlyCollection<Guid> runIds, PriceSnapshotSource source, CancellationToken cancellationToken)
    {
        bool replaces = source is PriceSnapshotSource.Revalued;
        bool fillsOpen = replaces || source is PriceSnapshotSource.Backfill;
        List<LocalKillmail> losses = await RunIskFactsReader.LinkedKillmailsAsync(db, runIds, cancellationToken);
        List<RunLossPrice> rows = await db.Set<RunLossPrice>()
            .Where(price => runIds.Contains(price.RunId))
            .ToListAsync(cancellationToken);

        HashSet<(Guid RunId, int CharacterId, int KillmailId, int TypeId)> linked = [.. losses.SelectMany(loss =>
            LinkedLoss.TypeIds(loss).Select(typeId => (loss.RunId.GetValueOrDefault(), loss.CharacterId, loss.KillmailId, typeId)))];
        HashSet<Guid> changed = [];
        foreach (RunLossPrice stale in rows.Where(row => !linked.Contains(_Key(row))))
        {
            db.Set<RunLossPrice>().Remove(stale);
            changed.Add(stale.RunId);
        }

        HashSet<(Guid RunId, int CharacterId, int KillmailId, int TypeId)> kept = [.. rows.Select(_Key)];
        (Guid RunId, int CharacterId, int KillmailId, int TypeId)[] missing = [.. linked.Where(key => !kept.Contains(key))];
        RunLossPrice[] open = [.. rows.Where(row => linked.Contains(_Key(row)) && (replaces || (fillsOpen && row.UnitPriceIsk is null)))];
        if (missing.Length == 0 && open.Length == 0)
            return changed;

        IReadOnlyDictionary<int, double> live = await marketPrices.GetAveragePricesAsync(
            [.. missing.Select(key => key.TypeId).Concat(open.Select(row => row.TypeId)).Distinct()], cancellationToken);
        DateTime nowUtc = DateTime.UtcNow;

        foreach ((Guid runId, int characterId, int killmailId, int typeId) in missing)
        {
            decimal? price = _Rounded(live, typeId);
            db.Set<RunLossPrice>().Add(new RunLossPrice
            {
                RunId = runId,
                CharacterId = characterId,
                KillmailId = killmailId,
                TypeId = typeId,
                UnitPriceIsk = price,
                PricedAtUtc = price is null ? null : nowUtc,
                PriceSource = price is null ? null : source
            });
            changed.Add(runId);
        }

        foreach (RunLossPrice row in open)
        {
            decimal? price = _Rounded(live, row.TypeId);
            if (price is null || price == row.UnitPriceIsk)
                continue;

            (row.UnitPriceIsk, row.PricedAtUtc, row.PriceSource) = (price, nowUtc, source);
            changed.Add(row.RunId);
        }

        return changed;
    }

    private static (Guid RunId, int CharacterId, int KillmailId, int TypeId) _Key(RunLossPrice row) =>
        (row.RunId, row.CharacterId, row.KillmailId, row.TypeId);

    // Two decimals, as the loot lines keep theirs (RunPriceSnapshots).
    private static decimal? _Rounded(IReadOnlyDictionary<int, double> live, int typeId) =>
        live.TryGetValue(typeId, out double price) ? Math.Round((decimal)price, 2, MidpointRounding.AwayFromZero) : null;
}
