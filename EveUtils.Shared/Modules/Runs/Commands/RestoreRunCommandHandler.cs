using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Events;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class RestoreRunCommandHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus, IDispatcher dispatcher)
    : ICommandHandler<RestoreRunCommand, Result>
{
    public async Task<Result> Handle(RestoreRunCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        int changed = await db.Set<Run>().Where(run => run.Id == command.RunId && run.DeletedAtUtc.HasValue)
            .ExecuteUpdateAsync(properties => properties
                .SetProperty(run => run.DeletedAtUtc, (DateTime?)null)
                .SetProperty(run => run.Revision, run => run.Revision + 1), cancellationToken);
        if (changed == 0)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound, "The run could not be restored.", "Runs"));

        await dispatcher.Send(new RebuildActivitySummariesCommand(command.RunId), cancellationToken);
        await eventBus.PublishAsync(new RunRestoredEvent(command.RunId), EventTarget.Local, cancellationToken);
        await eventBus.PublishAsync(new RunsChangedEvent(command.RunId), EventTarget.Local, cancellationToken);
        return Result.Success();
    }
}
