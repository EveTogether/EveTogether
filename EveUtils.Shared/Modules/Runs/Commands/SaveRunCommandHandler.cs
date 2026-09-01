using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

internal sealed class SaveRunCommandHandler(IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus)
    : ICommandHandler<SaveRunCommand, Result>
{
    public async Task<Result> Handle(SaveRunCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        Run? run = await db.Set<Run>()
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == command.RunId, cancellationToken);
        if (run is null)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                "The run no longer exists.", "Runs"));
        if (run.State is RunState.Saved)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "A saved run cannot be saved again.", "Runs"));

        foreach (RunLootCaptureInput capture in command.LootCaptures)
        {
            var entity = new RunLootCapture
            {
                Id = Guid.CreateVersion7(),
                RunId = run.Id,
                CapturedAtUtc = capture.CapturedAtUtc,
                Source = capture.Source,
                ContentHash = capture.ContentHash
            };
            foreach (RunLootEntryInput entry in capture.Entries)
            {
                entity.Entries.Add(new RunLootEntry
                {
                    Id = Guid.CreateVersion7(),
                    ItemTypeId = entry.ItemTypeId,
                    Name = entry.Name,
                    Quantity = entry.Quantity,
                    Volume = entry.Volume,
                    ClipboardPrice = entry.ClipboardPrice,
                    LootKind = entry.LootKind
                });
            }
            db.Set<RunLootCapture>().Add(entity);
        }
        foreach (RunBountyEntryInput bounty in command.BountyEntries)
            db.Set<RunBountyEntry>().Add(new RunBountyEntry { Id = Guid.CreateVersion7(), RunId = run.Id, OccurredAtUtc = bounty.OccurredAtUtc, Isk = bounty.Isk });
        foreach (RunEnemyObservationInput observation in command.EnemyObservations.Where(observation => observation.Count > 0))
            db.Set<RunEnemyObservation>().Add(new RunEnemyObservation
            {
                Id = Guid.CreateVersion7(),
                RunId = run.Id,
                EnemyTypeId = observation.EnemyTypeId,
                EnemyName = observation.EnemyName,
                Direction = observation.Direction,
                FirstObservedAtUtc = observation.FirstObservedAtUtc,
                LastObservedAtUtc = observation.LastObservedAtUtc
            });
        foreach (RunParameterInput parameter in command.Parameters)
            db.Set<RunParameter>().Add(new RunParameter
            {
                Id = Guid.CreateVersion7(),
                RunId = run.Id,
                ParameterKey = parameter.ParameterKey,
                TypedValue = parameter.TypedValue,
                ObservedAtUtc = parameter.ObservedAtUtc
            });

        await db.SaveChangesAsync(cancellationToken);
        int savedRuns = await db.Set<Run>()
            .Where(candidate => candidate.Id == command.RunId && candidate.State != RunState.Saved)
            .ExecuteUpdateAsync(properties => properties
                .SetProperty(candidate => candidate.State, RunState.Saved)
                .SetProperty(candidate => candidate.StoppedAtUtc, command.StoppedAtUtc)
                .SetProperty(candidate => candidate.SavedAtUtc, command.SavedAtUtc)
                .SetProperty(candidate => candidate.SyncState, RunSyncState.Pending)
                .SetProperty(candidate => candidate.Revision, candidate => candidate.Revision + 1), cancellationToken);
        if (savedRuns == 0)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "A saved run cannot be saved again.", "Runs"));
        await eventBus.PublishAsync(new RunSavedEvent(command.RunId), EventTarget.Local, cancellationToken);
        return Result.Success();
    }
}
