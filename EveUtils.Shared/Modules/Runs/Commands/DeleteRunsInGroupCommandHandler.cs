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
internal sealed class DeleteRunsInGroupCommandHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus, IDispatcher dispatcher)
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
        IQueryable<Run> targets = db.Set<Run>()
            .Where(run => run.GroupCode == command.GroupCode && run.State == RunState.Saved && !run.DeletedAtUtc.HasValue);
        if (command.OnlyRunIds is { } onlyRunIds)
            targets = targets.Where(run => onlyRunIds.Contains(run.Id));

        int changed = await targets.ExecuteUpdateAsync(properties => properties
            .SetProperty(run => run.DeletedAtUtc, command.DeletedAtUtc)
            .SetProperty(run => run.SyncState,
                run => run.SyncState == RunSyncState.Local ? RunSyncState.Local : RunSyncState.Pending)
            .SetProperty(run => run.Revision, run => run.Revision + 1), cancellationToken);
        if (changed == 0)
            return Result<int>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound, "The activity no longer exists.", "Runs"));

        // A representative run of the group: RebuildActivitySummariesCommand resolves the whole group from any one
        // of its runs, deleted or not. OnlyRunIds already names one whenever the caller restricted the delete; the
        // whole-group case (no restriction) still has to ask, since ExecuteUpdateAsync never loaded any row.
        Guid representativeRunId = command.OnlyRunIds?.FirstOrDefault()
            ?? await db.Set<Run>().Where(run => run.GroupCode == command.GroupCode)
                .Select(run => run.Id).FirstAsync(cancellationToken);
        // Rebuilt before the event fires, not after — same reasoning as SaveRunCommandHandler: a screen reacting to
        // RunDeletedEvent by re-reading the overview must never see the summary as it stood before this delete.
        await dispatcher.Send(new RebuildActivitySummariesCommand(representativeRunId), cancellationToken);
        await eventBus.PublishAsync(new RunDeletedEvent(representativeRunId), EventTarget.Local, cancellationToken);
        return Result<int>.Success(changed);
    }
}
