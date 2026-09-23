using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
internal sealed class GetLocalActivityIdsQueryHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : IQueryHandler<GetLocalActivityIdsQuery, Result<IReadOnlyList<Guid>>>
{
    public async Task<Result<IReadOnlyList<Guid>>> Handle(GetLocalActivityIdsQuery query, CancellationToken cancellationToken = default)
    {
        if (query.CharacterIds.Count == 0)
            return Result<IReadOnlyList<Guid>>.Success([]);

        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        IQueryable<Run> saved = db.Set<Run>().Where(run => run.State == RunState.Saved && !run.DeletedAtUtc.HasValue);
        // The same membership GetActivityOverviewQueryHandler reads a row's runs by, and the same "local": no member
        // run carries a server address.
        List<Guid> ids = await db.Set<ActivitySummary>()
            .AsNoTracking()
            .Where(summary => saved.Any(run => query.CharacterIds.Contains(run.CharacterId)
                                               && ((summary.GroupCode != null && run.GroupCode == summary.GroupCode)
                                                   || (summary.RunId != null && run.Id == summary.RunId))))
            .Where(summary => !saved.Any(run => run.SyncServerAddress != null
                                                && ((summary.GroupCode != null && run.GroupCode == summary.GroupCode)
                                                    || (summary.RunId != null && run.Id == summary.RunId))))
            .Select(summary => summary.Id)
            .ToListAsync(cancellationToken);

        return Result<IReadOnlyList<Guid>>.Success(ids);
    }
}
