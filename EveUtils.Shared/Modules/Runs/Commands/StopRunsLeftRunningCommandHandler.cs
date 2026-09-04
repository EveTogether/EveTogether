using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class StopRunsLeftRunningCommandHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : ICommandHandler<StopRunsLeftRunningCommand, Result<int>>
{
    public async Task<Result<int>> Handle(StopRunsLeftRunningCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        List<Run> running = await db.Set<Run>()
            .Where(run => run.State == RunState.Running && !run.DeletedAtUtc.HasValue)
            .ToListAsync(cancellationToken);
        if (running.Count == 0)
            return Result<int>.Success(0);

        foreach (Run run in running)
        {
            // When it really ended is not knowable — nothing recorded the pilot walking away, and the process that
            // could have is gone. So it ends when we noticed, which is honest rather than flattering: the run will
            // read long, and CORRECT is already on screen for exactly that.
            run.State = RunState.Stopped;
            run.StoppedAtUtc = command.StoppedAtUtc;
            if (run.SyncState is not RunSyncState.Local)
                run.SyncState = RunSyncState.Pending;
            run.Revision++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result<int>.Success(running.Count);
    }
}
