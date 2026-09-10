using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Events;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class AddRunBountyEntryCommandHandler(IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus)
    : ICommandHandler<AddRunBountyEntryCommand, Result>
{
    public async Task<Result> Handle(AddRunBountyEntryCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // Same lookup AddRunLootCaptureCommandHandler uses, so two of the same pilot's characters each running their
        // own site never contend over whose run a payout belongs to. includeStopped: true for the same reason loot
        // needs it — the last kill's payout line can land a beat after STOP closes the window, and it still belongs
        // to the run that earned it.
        (Run? run, int runningCount) = await RunningRunLookup.FindAsync(db, cancellationToken, includeStopped: true,
            characterId: command.CharacterId);
        if (run is null)
            return Result.Failure(runningCount == 0
                ? new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                    "No run is running, so this bounty was not recorded.", "Runs")
                : new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                    $"{runningCount} runs are running, so this bounty was not recorded against any of them.", "Runs"));

        db.Set<RunBountyEntry>().Add(new RunBountyEntry
        {
            Id = Guid.CreateVersion7(),
            RunId = run.Id,
            OccurredAtUtc = command.OccurredAtUtc,
            Isk = command.Isk
        });
        await db.SaveChangesAsync(cancellationToken);
        await eventBus.PublishAsync(new RunsChangedEvent(run.Id, run.GroupCode), EventTarget.Local, cancellationToken);
        return Result.Success();
    }
}
