using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class DeleteRunsInGroupCommandHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : ICommandHandler<DeleteRunsInGroupCommand, Result<int>>
{
    public async Task<Result<int>> Handle(DeleteRunsInGroupCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.GroupCode))
            return Result<int>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "A delete needs the group it applies to.", "Runs"));

        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // Saved only: a running or stopped-unfinished sibling that happens to share this group code is not part of
        // the activity this row shows — it belongs to DISCARD, not to deleting a saved activity.
        int changed = await db.Set<Run>()
            .Where(run => run.GroupCode == command.GroupCode && run.State == RunState.Saved && !run.DeletedAtUtc.HasValue)
            .ExecuteUpdateAsync(properties => properties
                .SetProperty(run => run.DeletedAtUtc, command.DeletedAtUtc)
                .SetProperty(run => run.SyncState,
                    run => run.SyncState == RunSyncState.Local ? RunSyncState.Local : RunSyncState.Pending)
                .SetProperty(run => run.Revision, run => run.Revision + 1), cancellationToken);
        return changed == 0
            ? Result<int>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound, "The activity no longer exists.", "Runs"))
            : Result<int>.Success(changed);
    }
}
