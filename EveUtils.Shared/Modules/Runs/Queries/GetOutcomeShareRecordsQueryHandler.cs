using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
internal sealed class GetOutcomeShareRecordsQueryHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : IQueryHandler<GetOutcomeShareRecordsQuery, Result<IReadOnlyList<OutcomeShareRecordDto>>>
{
    public async Task<Result<IReadOnlyList<OutcomeShareRecordDto>>> Handle(GetOutcomeShareRecordsQuery query,
        CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        IQueryable<OutcomeShareRecord> records = db.Set<OutcomeShareRecord>().AsNoTracking();
        if (query.ServerAddress is { } serverAddress)
            records = records.Where(record => record.ServerAddress == serverAddress);

        return Result<IReadOnlyList<OutcomeShareRecordDto>>.Success(
            [.. (await records.ToListAsync(cancellationToken)).Select(OutcomeShareRecordDto.FromEntity)]);
    }
}
