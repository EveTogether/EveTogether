using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
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
            return Result<int>.Success(0);

        IReadOnlySet<Guid> priced = await RunPriceSnapshots.FixAsync(db, marketPrices, sde, [.. runs.Select(run => run.Id)],
            PriceSnapshotSource.Backfill, cancellationToken);
        if (priced.Count == 0)
            return Result<int>.Success(0);

        Run[] changed = [.. runs.Where(run => priced.Contains(run.Id))];
        foreach (Run saved in changed.Where(run => run.State is RunState.Saved))
            RunLootWrites.MarkCorrected(saved);
        await db.SaveChangesAsync(cancellationToken);

        foreach (Run run in changed)
        {
            bool isSaved = run.State is RunState.Saved;
            if (isSaved)
            {
                await dispatcher.Send(new RebuildActivitySummariesCommand(run.Id), cancellationToken);
                await eventBus.PublishAsync(new RunLootCorrectedEvent(run.Id), EventTarget.Local, cancellationToken);
            }
            await eventBus.PublishAsync(new RunsChangedEvent(run.Id, run.GroupCode), EventTarget.Local, cancellationToken);
        }

        return Result<int>.Success(changed.Length);
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
