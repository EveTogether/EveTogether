using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

internal sealed class DiscardRunsInGroupCommandHandler(IDbContextFactory<ClientDbContext> contextFactory)
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

        foreach (Run run in runs)
            RunDiscard.Apply(run, command.DiscardedAtUtc);

        await db.SaveChangesAsync(cancellationToken);
        return Result<int>.Success(runs.Count);
    }
}
