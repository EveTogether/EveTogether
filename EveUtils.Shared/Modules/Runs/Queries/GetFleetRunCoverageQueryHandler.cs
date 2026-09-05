using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
internal sealed class GetFleetRunCoverageQueryHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : IQueryHandler<GetFleetRunCoverageQuery, Result<FleetRunCoverageDto>>
{
    public async Task<Result<FleetRunCoverageDto>> Handle(
        GetFleetRunCoverageQuery query, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Mirrors GetActivityOverviewQueryHandler's FleetId branch, counted rather than paged — this is the same
        // question ("which activities carry this fleet's origin"), just answered as a number.
        int completed = await db.Set<ActivitySummary>().AsNoTracking()
            .Where(summary => summary.GroupCode != null && db.Set<RunGroupOrigin>()
                .Any(origin => origin.GroupCode == summary.GroupCode && origin.FleetId == query.FleetId))
            .CountAsync(cancellationToken);

        if (completed > 0)
            return Result<FleetRunCoverageDto>.Success(new FleetRunCoverageDto(completed, IsKnown: true));

        // Nothing found for this fleet. That is a real zero only if the fleet could not possibly have a run from
        // before RunGroupOrigin started recording — i.e. it was created no earlier than the oldest row on file.
        DateTime? oldestRecordedAtUtc = await db.Set<RunGroupOrigin>().AsNoTracking()
            .OrderBy(origin => origin.RecordedAtUtc)
            .Select(origin => (DateTime?)origin.RecordedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        bool isKnownZero = oldestRecordedAtUtc is { } floor && query.FleetCreatedAtUtc >= floor;

        return Result<FleetRunCoverageDto>.Success(new FleetRunCoverageDto(0, isKnownZero));
    }
}
