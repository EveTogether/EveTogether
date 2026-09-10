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
internal sealed class DiscardRunCommandHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus, IDispatcher dispatcher)
    : ICommandHandler<DiscardRunCommand, Result>
{
    public async Task<Result> Handle(DiscardRunCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        Run? run = await db.Set<Run>()
            .FirstOrDefaultAsync(candidate => candidate.Id == command.RunId && !candidate.DeletedAtUtc.HasValue,
                cancellationToken);
        if (run is null)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                "The run no longer exists.", "Runs"));

        bool wasAlreadySaved = run.State is RunState.Saved;
        RunDiscard.Apply(run, command.StoppedAtUtc);
        await db.SaveChangesAsync(cancellationToken);
        // The RUNNING band (ET-203) has no other way to hear that this run's clock just stopped, the same gap
        // SetRunStoppedCommandHandler already closes for STOP — a discard changes the same running/not-running state
        // and was the one caller that never said so (ET-220).
        await eventBus.PublishAsync(new RunRunningStateChangedEvent(run.Id), EventTarget.Local, cancellationToken);
        await eventBus.PublishAsync(new RunsChangedEvent(run.Id, run.GroupCode), EventTarget.Local, cancellationToken);

        // Reuses DeleteRunCommand (ET-214) rather than a second place that writes DeletedAtUtc: a run that was
        // already saved is never in here (ET-105 AC-1 — RunDiscard.Apply leaves a saved run's state untouched, and
        // this flag is never set true for the fanout of someone else's discard in the first place), so the guard is
        // belt-and-braces, not the only thing standing between this and deleting a member's saved history.
        if (command.DeleteAfterDiscard && !wasAlreadySaved)
            await dispatcher.Send(new DeleteRunCommand(run.Id, command.StoppedAtUtc), cancellationToken);

        return Result.Success();
    }
}
