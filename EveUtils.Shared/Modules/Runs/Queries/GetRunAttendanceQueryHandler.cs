using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
internal sealed class GetRunAttendanceQueryHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : IQueryHandler<GetRunAttendanceQuery, Result<RunAttendanceDecision?>>
{
    public async Task<Result<RunAttendanceDecision?>> Handle(GetRunAttendanceQuery query, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        Run? decided = await db.Set<Run>()
            .AsNoTracking()
            .Include(run => run.AttendanceEntries)
            .Where(run => !run.DeletedAtUtc.HasValue && run.AttendanceSetAtUtc.HasValue
                          && (string.IsNullOrEmpty(query.GroupCode) ? run.Id == query.RunId : run.GroupCode == query.GroupCode))
            .OrderByDescending(run => run.AttendanceSetAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        return Result<RunAttendanceDecision?>.Success(decided is null ? null : RunAttendanceDecision.Of(decided));
    }
}
