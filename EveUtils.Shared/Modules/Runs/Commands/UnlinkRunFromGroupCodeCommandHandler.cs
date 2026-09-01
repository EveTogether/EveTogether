using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

internal sealed class UnlinkRunFromGroupCodeCommandHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : ICommandHandler<UnlinkRunFromGroupCodeCommand, Result>
{
    public async Task<Result> Handle(UnlinkRunFromGroupCodeCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        Run? run = await db.Set<Run>().FirstOrDefaultAsync(candidate => candidate.Id == command.RunId, cancellationToken);
        if (run is null)
            return Result.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound,
                "The run no longer exists.", "Runs"));

        run.GroupCode = null;
        run.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
