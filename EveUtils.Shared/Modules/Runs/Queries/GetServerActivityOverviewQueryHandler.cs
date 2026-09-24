using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Killmails.Entities;
using EveUtils.Shared.Modules.Market.Repositories;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using EveUtils.Shared.Modules.Sde;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
internal sealed class GetServerActivityOverviewQueryHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IMarketPriceRepository marketPrices, ISdeAccessor sde)
    : IQueryHandler<GetServerActivityOverviewQuery, Result<IReadOnlyList<ServerActivityDto>>>
{
    public async Task<Result<IReadOnlyList<ServerActivityDto>>> Handle(
        GetServerActivityOverviewQuery query, CancellationToken cancellationToken = default)
    {
        DateTime nowUtc = DateTime.UtcNow;
        List<Run> runs = [];
        foreach (RunWirePayload payload in query.Runs)
        {
            Run run = payload.Run.ToEntity();
            // Anchored the way the sync applier anchors a pulled run (ET-244): a start read off a server column comes
            // back Unspecified, and taken as local time it would land hours off on any machine outside UTC.
            run.StartedAtUtc = AbyssalSpace.AnchorFromWireUtc(run.StartedAtUtc, payload.SentAtUnixMilliseconds, nowUtc);
            run.StoppedAtUtc = run.StoppedAtUtc is { } stoppedAtUtc
                ? AbyssalSpace.AnchorFromWireUtc(stoppedAtUtc, payload.SentAtUnixMilliseconds, nowUtc)
                : null;
            run.SyncState = RunSyncState.Synced;
            run.SyncServerAddress = query.ServerAddress;
            runs.Add(run);
        }

        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        string[] groupCodes = [.. runs.Select(run => run.GroupCode).OfType<string>().Distinct()];
        Dictionary<string, long> fleetByGroup = await db.Set<RunGroupOrigin>().AsNoTracking()
            .Where(origin => groupCodes.Contains(origin.GroupCode))
            .ToDictionaryAsync(origin => origin.GroupCode, origin => origin.FleetId, cancellationToken);
        if (query.FleetId is { } fleetId)
            runs = [.. runs.Where(run => run.GroupCode is { } groupCode && fleetByGroup.GetValueOrDefault(groupCode) == fleetId)];
        if (runs.Count == 0)
            return Result<IReadOnlyList<ServerActivityDto>>.Success([]);

        ILookup<Guid, RunParameter> parametersByRun = runs.SelectMany(run => run.Parameters).ToLookup(parameter => parameter.RunId);
        MiningOreTypes ores = RunIskFactsReader.OresOf(runs, sde);
        // A loss never travels with a run (ET-331), so a server's copy is added up without one.
        ILookup<Guid, LocalKillmail> noLosses = Array.Empty<LocalKillmail>().ToLookup(loss => loss.RunId.GetValueOrDefault());
        IReadOnlyDictionary<int, double> prices = await marketPrices.GetAveragePricesAsync(
            [.. RunIskFactsReader.PricedTypeIds(runs, parametersByRun.SelectMany(group => group), ores, [])], cancellationToken);
        HashSet<long>? ownCharacterIds = query.OwnCharacterIds is { } own ? [.. own] : null;
        ActivityServerSyncDto[] onThisServer = [new ActivityServerSyncDto(query.ServerAddress, IsPending: false)];

        List<ServerActivityDto> activities = [];
        foreach (IGrouping<string, Run> activity in runs.GroupBy(run => run.GroupCode ?? run.Id.ToString())
                     .OrderByDescending(activity => activity.Min(run => run.StartedAtUtc)))
        {
            Run[] members = [.. activity];
            ActivitySummary summary = ActivitySummaryBuilder.Build(activity.Key, members, parametersByRun, prices, ores, noLosses);
            ActivityOverviewRowDto row = ActivityOverviewRows.ToDto(summary, members.SelectMany(run => run.Parameters),
                members.Select(run => (run.CharacterId, run.CharacterNameSnapshot)),
                members.Any(run => run.AutoSavedAtUtc.HasValue), onThisServer, ownCharacterIds);
            RunAttendanceDecision? attendance = members.Where(run => run.AttendanceSetAtUtc.HasValue)
                .MaxBy(run => run.AttendanceSetAtUtc) is { } decided ? RunAttendanceDecision.Of(decided) : null;
            long? fleet = summary.GroupCode is { } code && fleetByGroup.TryGetValue(code, out long known) ? known : null;
            ActivityDetailDto detail = ActivityDetails.ToDto(summary, members,
                [.. members.SelectMany(run => run.BountyEntries)], [.. members.SelectMany(run => run.EnemyObservations)],
                [.. members.SelectMany(run => run.Parameters)], [.. members.SelectMany(run => run.MiningEntries)],
                attendance, fleet,
                StoredIskBreakdown.ReadByCharacter(summary.IskContributionsByCharacter) ?? new Dictionary<long, IskBreakdown>());
            activities.Add(new ServerActivityDto(row, detail));
        }

        return Result<IReadOnlyList<ServerActivityDto>>.Success(activities);
    }
}
