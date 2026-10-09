using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Sde;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class FillRunPriceSnapshotsCommandHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IMarketPriceRepository marketPrices, ISdeAccessor sde,
    IEventBus eventBus, IDispatcher dispatcher)
    : ICommandHandler<FillRunPriceSnapshotsCommand, Result<int>>
{
    // The price refresh, the SDE import and the log's button can ask at once; one pass at a time, and the second finds
    // every line the first priced already fixed.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<Result<int>> Handle(FillRunPriceSnapshotsCommand command, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            return await _FillAsync(cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<Result<int>> _FillAsync(CancellationToken cancellationToken)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await _PriceMigratedOresAsync(db, cancellationToken);
        IReadOnlySet<Guid> corrected = await _FillOwnLinesAsync(db, cancellationToken);
        IReadOnlySet<Guid> lossesPriced = await _FillLossPricesAsync(db, cancellationToken);
        Guid[] changedIds = [.. corrected.Union(lossesPriced)];
        if (changedIds.Length == 0)
            return Result<int>.Success(0);

        List<Run> changed = await db.Set<Run>()
            .AsNoTracking()
            .Where(run => changedIds.Contains(run.Id))
            .ToListAsync(cancellationToken);
        foreach (Run run in changed)
        {
            if (run.State is RunState.Saved)
            {
                await dispatcher.Send(new RebuildActivitySummariesCommand(run.Id), cancellationToken);
                if (corrected.Contains(run.Id))
                    await eventBus.PublishAsync(new RunLootCorrectedEvent(run.Id), EventTarget.Local, cancellationToken);
            }
            await eventBus.PublishAsync(new RunsChangedEvent(run.Id, run.GroupCode), EventTarget.Local, cancellationToken);
        }

        return Result<int>.Success(changed.Count);
    }

    /// <summary>The own runs' loot, ore and filament lines that had no price yet: a correction to a saved run, which
    /// goes to the server again.</summary>
    private async Task<IReadOnlySet<Guid>> _FillOwnLinesAsync(ClientDbContext db, CancellationToken cancellationToken)
    {
        IQueryable<Guid> withOpenLoot = db.Set<RunLootCapture>()
            .Where(capture => capture.Entries.Any(entry => entry.UnitPriceIsk == null))
            .Select(capture => capture.RunId);
        IQueryable<Guid> withOpenMining = db.Set<RunMiningEntry>()
            .Where(entry => entry.UnitPriceIsk == null)
            .Select(entry => entry.RunId);
        IQueryable<Guid> withOpenFilament = db.Set<RunParameter>()
            .Where(parameter => parameter.ParameterKey == RunParameterKey.AbyssalFilamentTypeId && parameter.UnitPriceIsk == null)
            .Select(parameter => parameter.RunId);
        List<Run> runs = await db.Set<Run>()
            .Where(run => !run.DeletedAtUtc.HasValue
                          && db.Set<LocalCharacter>().Any(character => character.EsiCharacterId == run.CharacterId)
                          && (withOpenLoot.Contains(run.Id) || withOpenMining.Contains(run.Id) || withOpenFilament.Contains(run.Id)))
            .ToListAsync(cancellationToken);
        if (runs.Count == 0)
            return new HashSet<Guid>();

        IReadOnlySet<Guid> priced = await RunPriceSnapshots.FixAsync(db, marketPrices, sde, [.. runs.Select(run => run.Id)],
            PriceSnapshotSource.Backfill, cancellationToken);
        if (priced.Count == 0)
            return priced;

        foreach (Run saved in runs.Where(run => priced.Contains(run.Id) && run.State is RunState.Saved))
            RunLootWrites.MarkCorrected(saved);
        await db.SaveChangesAsync(cancellationToken);
        return priced;
    }

    /// <summary>The linked losses (ET-464): one linked before their prices were fixed is priced as it stands now, once
    /// (<see cref="PriceSnapshotSource.Migrated"/>), and a type the cache had no price for when it was linked is filled
    /// in. Never a correction: a loss is not published, so pricing one must not send its run to the server again.</summary>
    private async Task<IReadOnlySet<Guid>> _FillLossPricesAsync(ClientDbContext db, CancellationToken cancellationToken)
    {
        IQueryable<Guid> liveRunIds = db.Set<Run>().Where(run => !run.DeletedAtUtc.HasValue).Select(run => run.Id);
        Guid[] unpriced = [.. await db.Set<LocalKillmail>()
            .Where(killmail => killmail.IsLoss && killmail.RunId != null && liveRunIds.Contains(killmail.RunId.Value)
                               && !db.Set<RunLossPrice>().Any(price => price.RunId == killmail.RunId
                                                                       && price.CharacterId == killmail.CharacterId
                                                                       && price.KillmailId == killmail.KillmailId))
            .Select(killmail => killmail.RunId.GetValueOrDefault())
            .Distinct()
            .ToListAsync(cancellationToken)];
        HashSet<Guid> priced = [.. await RunLossPriceSnapshots.FixAsync(db, marketPrices, unpriced, PriceSnapshotSource.Migrated,
            cancellationToken)];
        // Saved before the open rows are read from the store, or a row just added would be added twice.
        await db.SaveChangesAsync(cancellationToken);

        Guid[] open = [.. await db.Set<RunLossPrice>()
            .Where(price => price.UnitPriceIsk == null && liveRunIds.Contains(price.RunId))
            .Select(price => price.RunId)
            .Distinct()
            .ToListAsync(cancellationToken)];
        priced.UnionWith(await RunLossPriceSnapshots.FixAsync(db, marketPrices, open, PriceSnapshotSource.Backfill, cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
        return priced;
    }

    /// <summary>The ores the snapshot migration marked (ET-463), on every run in the store: they are the migration's
    /// work, done here only because an ore's type lives in the SDE, so they are never a correction and never move a
    /// published run. Priced once; with no price for an ore yet, the line is filled as any other later.</summary>
    private async Task _PriceMigratedOresAsync(ClientDbContext db, CancellationToken cancellationToken)
    {
        Guid[] runIds = [.. await db.Set<RunMiningEntry>()
            .Where(entry => entry.PriceSource == PriceSnapshotSource.Migrated && entry.UnitPriceIsk == null)
            .Select(entry => entry.RunId)
            .Distinct()
            .ToListAsync(cancellationToken)];
        if (runIds.Length == 0 || !sde.IsAvailable)
            return;

        IReadOnlySet<Guid> priced = await RunPriceSnapshots.FixAsync(db, marketPrices, sde, runIds, PriceSnapshotSource.Migrated,
            cancellationToken);
        if (priced.Count == 0)
            return;

        await db.SaveChangesAsync(cancellationToken);
        // Whatever the cache could not price stays open; from here on it is filled as a correction like any other line.
        await db.Set<RunMiningEntry>()
            .Where(entry => entry.PriceSource == PriceSnapshotSource.Migrated && entry.UnitPriceIsk == null)
            .ExecuteUpdateAsync(properties => properties.SetProperty(entry => entry.PriceSource, (PriceSnapshotSource?)null),
                cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);
    }
}
