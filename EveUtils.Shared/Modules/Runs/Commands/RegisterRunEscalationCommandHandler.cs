using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class RegisterRunEscalationCommandHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus)
    : ICommandHandler<RegisterRunEscalationCommand, Result>
{
    public async Task<Result> Handle(RegisterRunEscalationCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.SiteName))
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "An escalation needs a site.", "Runs"));

        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        Run? run = await db.Set<Run>()
            .FirstOrDefaultAsync(candidate => candidate.Id == command.RunId && !candidate.DeletedAtUtc.HasValue,
                cancellationToken);
        if (run is null)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                "The run no longer exists.", "Runs"));

        // A run still in its window registers through the window, which writes the rows at SAVE.
        if (run.State is not RunState.Saved)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "Register the escalation in the run window before saving it.", "Runs"));

        DateTime nowUtc = DateTime.UtcNow;
        db.Set<RunParameter>().AddRange(RunEscalations
            .Rows(Guid.CreateVersion7(), command.SiteName.Trim(), command.DungeonId, command.DestinationSystem,
                command.DestinationSolarSystemId, command.ExpiresAtUtc, nowUtc)
            .Select(row => new RunParameter
            {
                Id = Guid.CreateVersion7(),
                RunId = run.Id,
                ParameterKey = row.ParameterKey,
                TypedValue = row.TypedValue,
                EntryId = row.EntryId,
                ObservedAtUtc = row.ObservedAtUtc
            }));

        // ET-215's rule for a change after the fact, the same one SetEscalationOutcomeCommandHandler follows.
        run.Revision++;
        if (run.SyncState is RunSyncState.Synced)
            run.SyncState = RunSyncState.Outdated;

        await db.SaveChangesAsync(cancellationToken);
        await eventBus.PublishAsync(new RunsChangedEvent(run.Id, run.GroupCode), EventTarget.Local, cancellationToken);
        return Result.Success();
    }
}
