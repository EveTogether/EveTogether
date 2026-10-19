using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class SaveOutcomeShareRecordsCommandHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : ICommandHandler<SaveOutcomeShareRecordsCommand, Result>
{
    public async Task<Result> Handle(SaveOutcomeShareRecordsCommand command, CancellationToken cancellationToken = default)
    {
        if (command.Records.Count == 0)
            return Result.Success();

        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        foreach (OutcomeShareRecordDto record in command.Records)
        {
            OutcomeShareRecord? stored = await db.Set<OutcomeShareRecord>().SingleOrDefaultAsync(
                candidate => candidate.ServerAddress == record.ServerAddress && candidate.ActivityRef == record.ActivityRef,
                cancellationToken);
            if (stored is null)
            {
                stored = new OutcomeShareRecord();
                record.ToEntity(stored);
                db.Set<OutcomeShareRecord>().Add(stored);
            }
            else
                record.ToEntity(stored);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
