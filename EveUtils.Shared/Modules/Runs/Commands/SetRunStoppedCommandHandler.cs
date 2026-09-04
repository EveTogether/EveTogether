using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class SetRunStoppedCommandHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : ICommandHandler<SetRunStoppedCommand, Result>
{
    public async Task<Result> Handle(SetRunStoppedCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        Run? run = await db.Set<Run>()
            .FirstOrDefaultAsync(candidate => candidate.Id == command.RunId && !candidate.DeletedAtUtc.HasValue,
                cancellationToken);
        if (run is null)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                "The run no longer exists.", "Runs"));

        // A committed run is history; neither stopping nor resuming may take it back (the rule SAVE already holds).
        if (run.State is RunState.Saved)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "This run is already saved, so its clock cannot be moved.", "Runs"));

        run.State = command.StoppedAtUtc is null ? RunState.Running : RunState.Stopped;
        run.StoppedAtUtc = command.StoppedAtUtc;
        if (run.SyncState is not RunSyncState.Local)
            run.SyncState = RunSyncState.Pending;
        run.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
