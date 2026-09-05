using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
internal sealed class GetIskTodayQueryHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : IQueryHandler<GetIskTodayQuery, Result<decimal>>
{
    public async Task<Result<decimal>> Handle(GetIskTodayQuery query, CancellationToken cancellationToken = default)
    {
        if (query.CharacterIds.Count == 0)
            return Result<decimal>.Success(0m);

        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // Correlated against Run the same way GetActivityOverviewQueryHandler does — ActivitySummary carries no
        // participant list of its own, only GroupCode/RunId to join back through.
        decimal? total = await db.Set<ActivitySummary>()
            .AsNoTracking()
            .Where(summary => summary.StartedAtUtc >= query.SinceUtc)
            .Where(summary => db.Set<Run>().Any(run =>
                query.CharacterIds.Contains(run.CharacterId) && run.State == RunState.Saved && !run.DeletedAtUtc.HasValue
                && ((summary.GroupCode != null && run.GroupCode == summary.GroupCode)
                    || (summary.RunId != null && run.Id == summary.RunId))))
            .SumAsync(summary => (decimal?)summary.BountyIsk, cancellationToken);

        return Result<decimal>.Success(total ?? 0m);
    }
}
