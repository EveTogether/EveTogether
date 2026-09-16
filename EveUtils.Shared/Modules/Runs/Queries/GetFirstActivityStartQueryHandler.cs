using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
internal sealed class GetFirstActivityStartQueryHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : IQueryHandler<GetFirstActivityStartQuery, Result<DateTime?>>
{
    public async Task<Result<DateTime?>> Handle(GetFirstActivityStartQuery query, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        DateTime? first = await db.Set<ActivitySummary>()
            .AsNoTracking()
            .MinAsync(summary => (DateTime?)summary.StartedAtUtc, cancellationToken);
        return Result<DateTime?>.Success(first is { } startedAtUtc ? DateTime.SpecifyKind(startedAtUtc, DateTimeKind.Utc) : null);
    }
}
