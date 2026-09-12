using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class TouchRunAliveCommandHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : ICommandHandler<TouchRunAliveCommand, Result>
{
    public async Task<Result> Handle(TouchRunAliveCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        Run? run = await db.Set<Run>()
            .FirstOrDefaultAsync(candidate => candidate.Id == command.RunId && !candidate.DeletedAtUtc.HasValue,
                cancellationToken);
        // Stopped or saved under the writer's feet (the pilot pressed STOP in the half-second before this tick's
        // write went out) — the heartbeat is moot either way, and overwriting a real StoppedAtUtc with "still
        // running a moment ago" would be exactly the dishonest stop time this ticket exists to stop writing.
        if (run is null || run.State is not RunState.Running)
            return Result.Success();

        run.LastAliveAtUtc = command.AtUtc;
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
