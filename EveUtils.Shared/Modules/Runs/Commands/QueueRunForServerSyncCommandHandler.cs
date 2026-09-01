using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

internal sealed class QueueRunForServerSyncCommandHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : ICommandHandler<QueueRunForServerSyncCommand, Result>
{
    public async Task<Result> Handle(QueueRunForServerSyncCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        int changed = await db.Set<Run>().Where(run => run.Id == command.RunId)
            .ExecuteUpdateAsync(properties => properties.SetProperty(run => run.SyncState, RunSyncState.Pending), cancellationToken);
        return changed == 0
            ? Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound, "The run no longer exists.", "Runs"))
            : Result.Success();
    }
}
