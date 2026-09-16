using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
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

        HashSet<long> characterIds = [.. query.CharacterIds];
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // Correlated against Run the same way GetActivityOverviewQueryHandler does — ActivitySummary carries no
        // participant list of its own, only GroupCode/RunId to join back through.
        var today = await db.Set<ActivitySummary>()
            .AsNoTracking()
            .Where(summary => summary.StartedAtUtc >= query.SinceUtc)
            .Where(summary => db.Set<Run>().Any(run =>
                query.CharacterIds.Contains(run.CharacterId) && run.State == RunState.Saved && !run.DeletedAtUtc.HasValue
                && ((summary.GroupCode != null && run.GroupCode == summary.GroupCode)
                    || (summary.RunId != null && run.Id == summary.RunId))))
            // The same TOTAL ISK the runs overview and the detail screen show for each activity (ET-256), not its
            // bounty alone — a mission's reward and an evening's loot are ISK earned today too.
            .Select(summary => new { summary.TotalIsk, summary.IskContributionsByCharacter })
            .ToListAsync(cancellationToken);

        // These characters' own share, not the group's (ET-296) — the very figure the runs overview's day total adds
        // up, so "ISK today" and that total can never disagree (RunIskTotalTests). Summed here rather than in the
        // database because the split is JSON; it is one day's activities, not a month's.
        decimal total = today.Sum(summary =>
            StoredIskBreakdown.ReadByCharacter(summary.IskContributionsByCharacter) is { } byCharacter
                ? byCharacter.Where(character => characterIds.Contains(character.Key)).Sum(character => character.Value.Total)
                : summary.TotalIsk ?? 0m);

        return Result<decimal>.Success(total);
    }
}
