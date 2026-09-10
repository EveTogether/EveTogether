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
internal sealed class DiscardRunsInGroupCommandHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus, IDispatcher dispatcher)
    : ICommandHandler<DiscardRunsInGroupCommand, Result<int>>
{
    public async Task<Result<int>> Handle(DiscardRunsInGroupCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.GroupCode))
            return Result<int>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "A discard needs the group it applies to.", "Runs"));

        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        List<Run> runs = await db.Set<Run>()
            .Where(run => run.GroupCode == command.GroupCode && !run.DeletedAtUtc.HasValue)
            .ToListAsync(cancellationToken);

        // Captured before Apply, which is the only thing that may still move State — an already-saved run keeps its
        // state (ET-105 AC-1) and must never end up in here regardless of DeleteAfterDiscard.
        List<Guid> toDelete = [];
        foreach (Run run in runs)
        {
            bool wasAlreadySaved = run.State is RunState.Saved;
            RunDiscard.Apply(run, command.DiscardedAtUtc);
            if (command.DeleteAfterDiscard && !wasAlreadySaved)
                toDelete.Add(run.Id);
        }

        await db.SaveChangesAsync(cancellationToken);

        // The RUNNING band (ET-203) has no other way to hear that these runs' clocks just stopped — one publish
        // covers the whole group, since every subscriber reloads its own running set wholesale rather than reading
        // the id this event carries (ET-220).
        if (runs.Count > 0)
        {
            await eventBus.PublishAsync(new RunRunningStateChangedEvent(runs[0].Id), EventTarget.Local, cancellationToken);
            await eventBus.PublishAsync(new RunsChangedEvent(null, command.GroupCode), EventTarget.Local, cancellationToken);
        }

        // Reuses DeleteRunCommand (ET-214) per affected run rather than a second bulk-update path: this group is
        // small (a fleet or an ET-210 multi-toon pick, never a whole day's history), and a never-saved run's own
        // rebuild is a documented no-op, so looping costs nothing worth a bulk statement over.
        foreach (Guid runId in toDelete)
            await dispatcher.Send(new DeleteRunCommand(runId, command.DiscardedAtUtc), cancellationToken);

        return Result<int>.Success(runs.Count);
    }
}
