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
internal sealed class GetUnfinishedRunsQueryHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : IQueryHandler<GetUnfinishedRunsQuery, Result<IReadOnlyList<UnfinishedRunDto>>>
{
    public async Task<Result<IReadOnlyList<UnfinishedRunDto>>> Handle(
        GetUnfinishedRunsQuery query, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        List<UnfinishedRunDto> runs = await db.Set<Run>()
            .AsNoTracking()
            .Where(run => run.State == RunState.Stopped && !run.DeletedAtUtc.HasValue)
            // On the stop where there is one, on the start otherwise: a row without a stop stamp would sort as the
            // oldest thing on screen no matter when it was flown.
            .OrderByDescending(run => run.StoppedAtUtc ?? run.StartedAtUtc)
            .Select(run => new UnfinishedRunDto(
                run.Id, run.CharacterId, run.ActivityKind, run.SiteName, run.StartedAtUtc, run.StoppedAtUtc))
            .ToListAsync(cancellationToken);
        return Result<IReadOnlyList<UnfinishedRunDto>>.Success(runs);
    }
}
