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
internal sealed class GetActivityRunsForPublishQueryHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : IQueryHandler<GetActivityRunsForPublishQuery, Result<IReadOnlyList<ActivityRunForPublishDto>>>
{
    public async Task<Result<IReadOnlyList<ActivityRunForPublishDto>>> Handle(
        GetActivityRunsForPublishQuery query, CancellationToken cancellationToken = default)
    {
        if (query.SummaryIds.Count == 0)
            return Result<IReadOnlyList<ActivityRunForPublishDto>>.Success([]);

        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        List<ActivitySummary> summaries = await db.Set<ActivitySummary>()
            .AsNoTracking()
            .Where(summary => query.SummaryIds.Contains(summary.Id))
            .ToListAsync(cancellationToken);

        // The same two ways GetActivityDetailQueryHandler finds a summary's runs, batched: a group by its code, a
        // solo activity by the one run it was built from.
        string[] groupCodes = [.. summaries.Where(summary => summary.GroupCode != null).Select(summary => summary.GroupCode!)];
        Guid[] soloRunIds = [.. summaries.Where(summary => summary.GroupCode == null && summary.RunId.HasValue)
            .Select(summary => summary.RunId!.Value)];

        List<Run> runs = await db.Set<Run>()
            .AsNoTracking()
            .Where(run => run.State == RunState.Saved && !run.DeletedAtUtc.HasValue
                && ((run.GroupCode != null && groupCodes.Contains(run.GroupCode)) || soloRunIds.Contains(run.Id)))
            .ToListAsync(cancellationToken);

        Dictionary<string, Guid> summaryIdByGroupCode = summaries
            .Where(summary => summary.GroupCode != null)
            .ToDictionary(summary => summary.GroupCode!, summary => summary.Id);
        Dictionary<Guid, Guid> summaryIdByRunId = summaries
            .Where(summary => summary.GroupCode == null && summary.RunId.HasValue)
            .ToDictionary(summary => summary.RunId!.Value, summary => summary.Id);

        List<ActivityRunForPublishDto> result = [];
        foreach (Run run in runs)
        {
            Guid? summaryId = run.GroupCode is { } groupCode && summaryIdByGroupCode.TryGetValue(groupCode, out Guid byGroup)
                ? byGroup
                : summaryIdByRunId.TryGetValue(run.Id, out Guid byRun) ? byRun : null;
            if (summaryId is { } id)
                result.Add(new ActivityRunForPublishDto(id, run.Id, run.CharacterId, run.SyncState));
        }

        return Result<IReadOnlyList<ActivityRunForPublishDto>>.Success(result);
    }
}
