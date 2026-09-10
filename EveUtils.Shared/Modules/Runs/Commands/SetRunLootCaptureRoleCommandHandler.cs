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
internal sealed class SetRunLootCaptureRoleCommandHandler(IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus)
    : ICommandHandler<SetRunLootCaptureRoleCommand, Result>
{
    public async Task<Result> Handle(SetRunLootCaptureRoleCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        RunLootCapture? capture = await db.Set<RunLootCapture>()
            .FirstOrDefaultAsync(candidate => candidate.Id == command.CaptureId, cancellationToken);
        if (capture is null)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                "That loot capture no longer exists.", "Runs"));

        // Saving is the lock. Everything about the loot stays adjustable until then and nothing after it — including
        // the run ET-179 finished on the pilot's behalf a day after STOP.
        bool isSaved = await db.Set<Run>()
            .AnyAsync(run => run.Id == capture.RunId && run.State == RunState.Saved, cancellationToken);
        if (isSaved)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "This run is saved, so its loot can no longer be changed.", "Runs"));

        await RunLootCaptureRoles.AssignAsync(db, capture, command.Role, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        // The run window shows this run twice since ET-215 — the starting-hold picker and the run's own block in the
        // per-character loot — and the block has to follow a new starting hold as much as a new capture.
        await eventBus.PublishAsync(new RunLootCapturedEvent(capture.RunId), EventTarget.Local, cancellationToken);
        await eventBus.PublishAsync(new RunsChangedEvent(capture.RunId), EventTarget.Local, cancellationToken);
        return Result.Success();
    }
}
