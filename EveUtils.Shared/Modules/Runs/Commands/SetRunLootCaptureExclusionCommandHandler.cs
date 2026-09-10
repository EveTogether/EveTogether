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
internal sealed class SetRunLootCaptureExclusionCommandHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus, IDispatcher dispatcher)
    : ICommandHandler<SetRunLootCaptureExclusionCommand, Result>
{
    public async Task<Result> Handle(SetRunLootCaptureExclusionCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        RunLootCapture? capture = await db.Set<RunLootCapture>()
            .FirstOrDefaultAsync(c => c.Id == command.CaptureId, cancellationToken);
        if (capture is null)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                "That loot capture no longer exists.", "Runs"));

        Result<Run> opened = await RunLootWrites.OpenForCorrectionAsync(db, capture.RunId, cancellationToken);
        if (!opened.IsSuccess || opened.Value is not { } run)
            return Result.Failure([.. opened.Messages]);
        if (capture.IsExcluded == command.IsExcluded)
            return Result.Success();

        bool isSaved = run.State is RunState.Saved;
        capture.IsExcluded = command.IsExcluded;
        if (isSaved)
            RunLootWrites.MarkCorrected(run);
        await db.SaveChangesAsync(cancellationToken);

        // One rebuild, of this one activity, per correction (ET-215) — never one per run in the group.
        if (isSaved)
            await dispatcher.Send(new RebuildActivitySummariesCommand(run.Id), cancellationToken);
        await eventBus.PublishAsync(new RunLootCapturedEvent(run.Id), EventTarget.Local, cancellationToken);
        if (isSaved)
            await eventBus.PublishAsync(new RunLootCorrectedEvent(run.Id), EventTarget.Local, cancellationToken);
        return Result.Success();
    }
}
