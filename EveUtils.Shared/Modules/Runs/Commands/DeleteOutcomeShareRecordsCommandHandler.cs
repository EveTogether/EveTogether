using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class DeleteOutcomeShareRecordsCommandHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : ICommandHandler<DeleteOutcomeShareRecordsCommand, Result>
{
    public async Task<Result> Handle(DeleteOutcomeShareRecordsCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        IQueryable<OutcomeShareRecord> records = db.Set<OutcomeShareRecord>()
            .Where(record => record.ServerAddress == command.ServerAddress);
        if (command.ActivityRefs is { } activityRefs)
        {
            string[] refs = [.. activityRefs];
            records = records.Where(record => refs.Contains(record.ActivityRef));
        }

        await records.ExecuteDeleteAsync(cancellationToken);
        return Result.Success();
    }
}
