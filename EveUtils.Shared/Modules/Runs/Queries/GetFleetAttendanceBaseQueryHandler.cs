using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
internal sealed class GetFleetAttendanceBaseQueryHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : IQueryHandler<GetFleetAttendanceBaseQuery, Result<RunAttendanceDecision?>>
{
    public async Task<Result<RunAttendanceDecision?>> Handle(GetFleetAttendanceBaseQuery query, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        IQueryable<string> fleetGroups = db.Set<RunGroupOrigin>()
            .Where(origin => origin.FleetId == query.FleetId)
            .Select(origin => origin.GroupCode);

        Run? latest = await db.Set<Run>()
            .AsNoTracking()
            .Include(run => run.AttendanceEntries)
            .Where(run => !run.DeletedAtUtc.HasValue && run.AttendanceSetAtUtc.HasValue
                          && run.GroupCode != null && run.GroupCode != query.ExcludeGroupCode
                          && fleetGroups.Contains(run.GroupCode))
            .OrderByDescending(run => run.AttendanceSetAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        return Result<RunAttendanceDecision?>.Success(latest is null ? null : RunAttendanceDecision.Of(latest));
    }
}
