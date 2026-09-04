using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class SetRunLootStrategyCommandHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : ICommandHandler<SetRunLootStrategyCommand, Result>
{
    public async Task<Result> Handle(SetRunLootStrategyCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        Run? run = await db.Set<Run>()
            .FirstOrDefaultAsync(candidate => candidate.Id == command.RunId && !candidate.DeletedAtUtc.HasValue,
                cancellationToken);
        if (run is null)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                "The run no longer exists.", "Runs"));

        run.LootStrategy = command.LootStrategy;
        if (run.SyncState is not RunSyncState.Local)
            run.SyncState = RunSyncState.Pending;
        run.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
