using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Events;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class QueueRunForServerSyncCommandHandler(IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus)
    : ICommandHandler<QueueRunForServerSyncCommand, Result>
{
    public async Task<Result> Handle(QueueRunForServerSyncCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        int changed = await db.Set<Run>().Where(run => run.Id == command.RunId)
            .ExecuteUpdateAsync(properties => properties
                .SetProperty(run => run.SyncState, RunSyncState.Pending)
                .SetProperty(run => run.SyncServerAddress, command.ServerAddress), cancellationToken);
        if (changed == 0)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound, "The run no longer exists.", "Runs"));

        await eventBus.PublishAsync(new RunsChangedEvent(command.RunId), EventTarget.Local, cancellationToken);
        return Result.Success();
    }
}
