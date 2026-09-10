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
internal sealed class DeleteRunCommandHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus, IDispatcher dispatcher)
    : ICommandHandler<DeleteRunCommand, Result>
{
    public async Task<Result> Handle(DeleteRunCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        int changed = await db.Set<Run>().Where(run => run.Id == command.RunId && !run.DeletedAtUtc.HasValue)
            .ExecuteUpdateAsync(properties => properties
                .SetProperty(run => run.DeletedAtUtc, command.DeletedAtUtc)
                .SetProperty(run => run.SyncState,
                    run => run.SyncState == RunSyncState.Local ? RunSyncState.Local : RunSyncState.Pending)
                .SetProperty(run => run.Revision, run => run.Revision + 1), cancellationToken);
        if (changed == 0)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound, "The run no longer exists.", "Runs"));

        // A never-saved run (e.g. thrown away from the UNFINISHED band) has no ActivitySummary to begin with, so
        // this is a harmless no-op for that path — the rebuild only ever touches Saved rows.
        await dispatcher.Send(new RebuildActivitySummariesCommand(command.RunId), cancellationToken);
        await eventBus.PublishAsync(new RunDeletedEvent(command.RunId), EventTarget.Local, cancellationToken);
        await eventBus.PublishAsync(new RunsChangedEvent(command.RunId), EventTarget.Local, cancellationToken);
        return Result.Success();
    }
}
