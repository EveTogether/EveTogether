using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
internal sealed class GetRunCombatTimelineQueryHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : IQueryHandler<GetRunCombatTimelineQuery, Result<RunCombatTimelineDto?>>
{
    public async Task<Result<RunCombatTimelineDto?>> Handle(GetRunCombatTimelineQuery query,
        CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        RunCombatTimeline? timeline = await db.Set<RunCombatTimeline>()
            .AsNoTracking()
            .Include(candidate => candidate.Series)
            .Include(candidate => candidate.HitTallies)
            .FirstOrDefaultAsync(candidate => candidate.RunId == query.RunId, cancellationToken);
        if (timeline is null)
        {
            return Result<RunCombatTimelineDto?>.Success(null);
        }

        return Result<RunCombatTimelineDto?>.Success(new RunCombatTimelineDto(
            timeline.Seconds,
            timeline.Series.ToDictionary(series => series.Kind,
                series => RunCombatTelemetry.Decode(series.Samples, timeline.Seconds)),
            timeline.MaxHitOut,
            timeline.MaxHitOutTarget,
            timeline.MaxHitIn,
            timeline.MaxHitInSource,
            timeline.HitsOut,
            timeline.MissesOut,
            timeline.HitsIn,
            timeline.MissesIn,
            [.. timeline.HitTallies.Select(tally => new RunHitTallyDto(
                tally.Direction, tally.Counterparty, tally.Weapon, tally.Quality, tally.Count, tally.Sum, tally.Min, tally.Max))]));
    }
}
