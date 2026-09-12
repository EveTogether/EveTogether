using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Events;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class RestoreRunsInGroupCommandHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus, IDispatcher dispatcher)
    : ICommandHandler<RestoreRunsInGroupCommand, Result<int>>
{
    public async Task<Result<int>> Handle(RestoreRunsInGroupCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.GroupCode))
            return Result<int>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "A restore needs the group it applies to.", "Runs"));

        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        List<Run> group = await db.Set<Run>().AsNoTracking()
            .Where(run => run.GroupCode == command.GroupCode)
            .ToListAsync(cancellationToken);
        // One run per character comes back (I7, ET-274): the one deleted last, which is the one the activity's delete
        // took — a copy folded into it earlier was deleted before that and stays so — and none for a character that
        // has a run in the group again.
        HashSet<long> live = [.. group.Where(run => run.DeletedAtUtc is null).Select(run => run.CharacterId)];
        Guid[] restored = [.. group
            .Where(run => run.DeletedAtUtc is not null && !live.Contains(run.CharacterId))
            .GroupBy(run => run.CharacterId)
            .Select(character => character.OrderByDescending(run => run.DeletedAtUtc).ThenBy(run => run.Id).First().Id)];
        int changed = await db.Set<Run>()
            .Where(run => restored.Contains(run.Id))
            .ExecuteUpdateAsync(properties => properties
                .SetProperty(run => run.DeletedAtUtc, (DateTime?)null)
                .SetProperty(run => run.Revision, run => run.Revision + 1), cancellationToken);
        if (changed == 0)
            return Result<int>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound, "The activity could not be restored.", "Runs"));

        Guid representativeRunId = await db.Set<Run>().Where(run => run.GroupCode == command.GroupCode)
            .Select(run => run.Id).FirstAsync(cancellationToken);
        await dispatcher.Send(new RebuildActivitySummariesCommand(representativeRunId), cancellationToken);
        await eventBus.PublishAsync(new RunRestoredEvent(representativeRunId), EventTarget.Local, cancellationToken);
        await eventBus.PublishAsync(new RunsChangedEvent(representativeRunId, command.GroupCode), EventTarget.Local, cancellationToken);
        return Result<int>.Success(changed);
    }
}
