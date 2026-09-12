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
internal sealed class GetRunningRunsQueryHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : IQueryHandler<GetRunningRunsQuery, Result<IReadOnlyList<RunningRunDto>>>
{
    public async Task<Result<IReadOnlyList<RunningRunDto>>> Handle(
        GetRunningRunsQuery query, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        List<RunningRunDto> runs = await db.Set<Run>()
            .AsNoTracking()
            .Where(run => run.State == RunState.Running && !run.DeletedAtUtc.HasValue)
            .Select(run => new RunningRunDto(
                run.Id, run.CharacterId, run.ActivityKind, run.StartedAtUtc, run.GroupCode, run.SiteName, run.Signature,
                run.SignatureGroupSnapshot, run.SiteTypeId))
            .ToListAsync(cancellationToken);
        return Result<IReadOnlyList<RunningRunDto>>.Success(runs);
    }
}
