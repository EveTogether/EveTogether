using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Market.Services;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Sde;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class RevalueRunsCommandHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IMarketPriceRepository marketPrices, IBlueprintAppraisalService blueprints, ISdeAccessor sde,
    IEventBus eventBus, IDispatcher dispatcher)
    : ICommandHandler<RevalueRunsCommand, Result<int>>
{
    public async Task<Result<int>> Handle(RevalueRunsCommand command, CancellationToken cancellationToken = default)
    {
        if (command.RunIds.Count == 0)
            return Result<int>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "No run was named to re-value.", "Runs"));

        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        Guid[] runIds = [.. command.RunIds.Distinct()];
        List<Run> runs = await db.Set<Run>()
            .Where(run => runIds.Contains(run.Id) && !run.DeletedAtUtc.HasValue)
            .ToListAsync(cancellationToken);
        if (runs.Count != runIds.Length)
            return Result<int>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                "The run no longer exists.", "Runs"));

        long[] characterIds = [.. runs.Select(run => run.CharacterId).Distinct()];
        int ownCount = await db.Set<LocalCharacter>()
            .CountAsync(character => characterIds.Contains(character.EsiCharacterId), cancellationToken);
        if (ownCount != characterIds.Length)
            return Result<int>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "Only your own runs can be re-valued; a fleetmate's run is theirs to correct.", "Runs"));

        IReadOnlySet<Guid> revalued = await RunPriceSnapshots.FixAsync(db, marketPrices, blueprints, sde, runIds,
            PriceSnapshotSource.Revalued, cancellationToken);
        // Losses are re-valued too (ET-464), but never published, so a run whose losses alone moved is no correction.
        IReadOnlySet<Guid> lossesRevalued = await RunLossPriceSnapshots.FixAsync(db, marketPrices, runIds,
            PriceSnapshotSource.Revalued, cancellationToken);
        Run[] changed = [.. runs.Where(run => revalued.Contains(run.Id) || lossesRevalued.Contains(run.Id))];
        if (changed.Length == 0)
            return Result<int>.Success(0);

        foreach (Run saved in changed.Where(run => run.State is RunState.Saved && revalued.Contains(run.Id)))
            RunLootWrites.MarkCorrected(saved);
        await db.SaveChangesAsync(cancellationToken);

        // Once per activity: the runs re-valued together are as a rule one group, and its summary covers them all.
        foreach (Run saved in changed.Where(run => run.State is RunState.Saved).DistinctBy(run => run.GroupCode ?? run.Id.ToString()))
            await dispatcher.Send(new RebuildActivitySummariesCommand(saved.Id), cancellationToken);
        foreach (Run run in changed)
        {
            if (run.State is RunState.Saved && revalued.Contains(run.Id))
                await eventBus.PublishAsync(new RunLootCorrectedEvent(run.Id), EventTarget.Local, cancellationToken);
            await eventBus.PublishAsync(new RunsChangedEvent(run.Id, run.GroupCode), EventTarget.Local, cancellationToken);
        }

        return Result<int>.Success(changed.Length);
    }
}
