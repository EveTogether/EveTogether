using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
internal sealed class FindActivitySummaryIdQueryHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : IQueryHandler<FindActivitySummaryIdQuery, Result<Guid?>>
{
    public async Task<Result<Guid?>> Handle(FindActivitySummaryIdQuery query, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        IQueryable<ActivitySummary> summaries = db.Set<ActivitySummary>().AsNoTracking();
        Guid? id = await (query.GroupCode is { } groupCode
                ? summaries.Where(summary => summary.GroupCode == groupCode)
                : summaries.Where(summary => summary.RunId == query.RunId))
            .Select(summary => (Guid?)summary.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return Result<Guid?>.Success(id);
    }
}
