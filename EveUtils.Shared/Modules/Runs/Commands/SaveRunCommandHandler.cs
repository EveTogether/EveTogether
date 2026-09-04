using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class SaveRunCommandHandler(IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus, IDispatcher dispatcher)
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

        // A corrected start is typed by a human (ET-98), so the pair is checked here too rather than trusted from
        // whichever screen sent it — a run that ends before it begins is not a duration to store and quietly fix.
        DateTime startedAtUtc = command.StartedAtUtc ?? run.StartedAtUtc;
        if (command.StoppedAtUtc < startedAtUtc)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "A run cannot end before it started.", "Runs"));

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
                Count = observation.Count,
                EnemyTypeId = observation.EnemyTypeId,
                EnemyName = observation.EnemyName,
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
                Amount = parameter.Amount,
                ItemTypeId = parameter.ItemTypeId,
                BonusWindowSeconds = parameter.BonusWindowSeconds,
                ObservedAtUtc = parameter.ObservedAtUtc
            });

        await db.SaveChangesAsync(cancellationToken);
        int savedRuns = await db.Set<Run>()
            .Where(candidate => candidate.Id == command.RunId && candidate.State != RunState.Saved)
            .ExecuteUpdateAsync(properties => properties
                .SetProperty(candidate => candidate.State, RunState.Saved)
                .SetProperty(candidate => candidate.StartedAtUtc, startedAtUtc)
                .SetProperty(candidate => candidate.StoppedAtUtc, command.StoppedAtUtc)
                // The corrected times overwrite the measured ones, so this stamp is all that is left to tell the
                // two apart afterwards — and it cannot be added back later for runs already saved without it.
                .SetProperty(candidate => candidate.TimesCorrectedAtUtc, command.TimesCorrectedAtUtc)
                .SetProperty(candidate => candidate.AutoSavedAtUtc, command.AutoSavedAtUtc)
                // Null leaves the row's own answer alone: the chip already wrote it, and the app's own save of an
                // unfinished run (ET-179) carries none and must not wipe it.
                .SetProperty(candidate => candidate.LootStrategy,
                    candidate => command.LootStrategy ?? candidate.LootStrategy)
                .SetProperty(candidate => candidate.SavedAtUtc, command.SavedAtUtc)
                .SetProperty(candidate => candidate.SyncState,
                    candidate => candidate.SyncState == RunSyncState.Local ? RunSyncState.Local : RunSyncState.Pending)
                .SetProperty(candidate => candidate.Revision, candidate => candidate.Revision + 1), cancellationToken);
        if (savedRuns == 0)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "A saved run cannot be saved again.", "Runs"));
        await eventBus.PublishAsync(new RunSavedEvent(command.RunId), EventTarget.Local, cancellationToken);
        // Local-first: a run must show up in the summary the moment it is saved, not only after the next server
        // sync — RunSynchronizationApplier triggers the same rebuild for the pulled-run path.
        await dispatcher.Send(new RebuildActivitySummariesCommand(), cancellationToken);
        return Result.Success();
    }
}
