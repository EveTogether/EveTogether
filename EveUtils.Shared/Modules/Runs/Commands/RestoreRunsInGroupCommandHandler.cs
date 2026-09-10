using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class RestoreRunsInGroupCommandHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : ICommandHandler<RestoreRunsInGroupCommand, Result<int>>
{
    public async Task<Result<int>> Handle(RestoreRunsInGroupCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.GroupCode))
            return Result<int>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "A restore needs the group it applies to.", "Runs"));

        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        int changed = await db.Set<Run>()
            .Where(run => run.GroupCode == command.GroupCode && run.DeletedAtUtc.HasValue)
            .ExecuteUpdateAsync(properties => properties
                .SetProperty(run => run.DeletedAtUtc, (DateTime?)null)
                .SetProperty(run => run.Revision, run => run.Revision + 1), cancellationToken);
        return changed == 0
            ? Result<int>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound, "The activity could not be restored.", "Runs"))
            : Result<int>.Success(changed);
    }
}
