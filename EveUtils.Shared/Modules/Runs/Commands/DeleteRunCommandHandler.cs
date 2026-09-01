using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

internal sealed class DeleteRunCommandHandler(IDbContextFactory<ClientDbContext> contextFactory)
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
        return changed == 0
            ? Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound, "The run no longer exists.", "Runs"))
            : Result.Success();
    }
}
