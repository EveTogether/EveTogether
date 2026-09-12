using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Events;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class AddRunMiningEntryCommandHandler(IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus)
    : ICommandHandler<AddRunMiningEntryCommand, Result>
{
    public async Task<Result> Handle(AddRunMiningEntryCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // Same lookup AddRunBountyEntryCommandHandler uses (ET-219): two of the same pilot's characters each running
        // their own site never contend over whose run a cycle belongs to. includeStopped: true for the same reason —
        // a cycle can land a beat after STOP closes the window, and it still belongs to the run that mined it.
        (Run? run, int runningCount) = await RunningRunLookup.FindAsync(db, cancellationToken, includeStopped: true,
            characterId: command.CharacterId);
        if (run is null)
            return Result.Failure(runningCount == 0
                ? new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                    "No run is running, so this mining was not recorded.", "Runs")
                : new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                    $"{runningCount} runs are running, so this mining was not recorded against any of them.", "Runs"));

        // Aggregated per ore on the run, not one row per cycle (ET-229) — a site is on the order of a hundred cycles
        // per character.
        RunMiningEntry? entry = await db.Set<RunMiningEntry>()
            .FirstOrDefaultAsync(candidate => candidate.RunId == run.Id && candidate.OreType == command.OreType, cancellationToken);
        if (entry is null)
        {
            entry = new RunMiningEntry
            {
                Id = Guid.CreateVersion7(),
                RunId = run.Id,
                OreType = command.OreType,
                FirstObservedAtUtc = command.OccurredAtUtc,
                LastObservedAtUtc = command.OccurredAtUtc
            };
            db.Set<RunMiningEntry>().Add(entry);
        }

        entry.Units += command.Units;
        if (command.IsCritical)
            entry.CriticalUnits += command.Units;
        entry.ResidueUnits += command.ResidueUnits;
        if (command.OccurredAtUtc < entry.FirstObservedAtUtc)
            entry.FirstObservedAtUtc = command.OccurredAtUtc;
        if (command.OccurredAtUtc > entry.LastObservedAtUtc)
            entry.LastObservedAtUtc = command.OccurredAtUtc;

        await db.SaveChangesAsync(cancellationToken);
        await eventBus.PublishAsync(new RunsChangedEvent(run.Id, run.GroupCode), EventTarget.Local, cancellationToken);
        return Result.Success();
    }
}
