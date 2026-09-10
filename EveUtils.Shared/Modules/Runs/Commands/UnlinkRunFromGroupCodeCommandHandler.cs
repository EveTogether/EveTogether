using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Events;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class UnlinkRunFromGroupCodeCommandHandler(IDbContextFactory<ClientDbContext> contextFactory, IEventBus eventBus)
    : ICommandHandler<UnlinkRunFromGroupCodeCommand, Result>
{
    public async Task<Result> Handle(UnlinkRunFromGroupCodeCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        Run? run = await db.Set<Run>().FirstOrDefaultAsync(candidate => candidate.Id == command.RunId, cancellationToken);
        if (run is null)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                "The run no longer exists.", "Runs"));

        // The group it leaves is the one whose screens have to hear about it, and after this the run no longer names it.
        string? formerGroupCode = run.GroupCode;
        run.UnlinkFromGroup(command.RecordFormerGroup);
        run.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        await eventBus.PublishAsync(new RunsChangedEvent(run.Id, formerGroupCode), EventTarget.Local, cancellationToken);
        return Result.Success();
    }
}
