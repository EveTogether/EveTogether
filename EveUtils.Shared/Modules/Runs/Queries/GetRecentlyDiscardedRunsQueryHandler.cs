using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
internal sealed class GetRecentlyDiscardedRunsQueryHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : IQueryHandler<GetRecentlyDiscardedRunsQuery, Result<IReadOnlyList<RecentlyDiscardedRunDto>>>
{
    public async Task<Result<IReadOnlyList<RecentlyDiscardedRunDto>>> Handle(
        GetRecentlyDiscardedRunsQuery query, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // Stopped only: a saved run that was deleted is a different, deliberate act and has its own history.
        List<Run> runs = await db.Set<Run>()
            .AsNoTracking()
            .Where(run => run.State == RunState.Stopped && run.DeletedAtUtc >= query.SinceUtc)
            .OrderByDescending(run => run.DeletedAtUtc)
            .ToListAsync(cancellationToken);

        return Result<IReadOnlyList<RecentlyDiscardedRunDto>>.Success([.. runs.Select(run => new RecentlyDiscardedRunDto
        {
            RunId = run.Id,
            CharacterId = run.CharacterId,
            ActivityKind = run.ActivityKind,
            SiteName = run.SiteName,
            SignatureGroupSnapshot = run.SignatureGroupSnapshot,
            SiteTypeId = run.SiteTypeId,
            StartedAtUtc = run.StartedAtUtc,
            DiscardedAtUtc = run.DeletedAtUtc ?? run.StartedAtUtc
        })]);
    }
}
