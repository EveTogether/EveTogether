using System.Globalization;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Gamelog.Entities;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
internal sealed class GetRunStoredHitsCombatQueryHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : IQueryHandler<GetRunStoredHitsCombatQuery, Result<RunCombatTimelineDto?>>
{
    public async Task<Result<RunCombatTimelineDto?>> Handle(GetRunStoredHitsCombatQuery query,
        CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        Run? run = await db.Set<Run>().AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == query.RunId, cancellationToken);
        if (run?.StoppedAtUtc is not { } stoppedAtUtc)
        {
            return Result<RunCombatTimelineDto?>.Success(null);
        }

        int characterId = (int)run.CharacterId;
        // SQLite cannot compare DateTimeOffset: the text column starts with the clock value, so ±14 h covers any offset label.
        string from = run.StartedAtUtc.AddHours(-14).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        string to = stoppedAtUtc.AddHours(14).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        List<CombatSample> samples = await db.Set<CombatSample>().FromSqlInterpolated(
                $"SELECT * FROM CombatSample WHERE CharacterId = {characterId} AND Timestamp >= {from} AND Timestamp <= {to}")
            .AsNoTracking().ToListAsync(cancellationToken);

        // The stored offset is a label, not the clock (ET-470): the wall-clock value is the game log's EVE time.
        GameLogEvent[] hits = [.. samples
            // A miss is stored with no damage (CharacterMetrics reads it the same way), so it must not count as a hit.
            .Select(sample => new CombatEvent(sample.Timestamp.DateTime, sample.Direction, sample.Amount, sample.Target, null,
                sample.Amount <= 0 ? HitQuality.Misses : HitQuality.Hits))
            .Where(hit => hit.Timestamp >= run.StartedAtUtc && hit.Timestamp <= stoppedAtUtc)];
        if (hits.Length == 0)
        {
            return Result<RunCombatTimelineDto?>.Success(null);
        }

        return Result<RunCombatTimelineDto?>.Success(
            RunCombatTelemetry.ToDto(RunCombatTelemetry.Build(run.Id, hits, run.StartedAtUtc, stoppedAtUtc)));
    }
}
