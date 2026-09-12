using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Isk;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
internal sealed class GetActivityOverviewQueryHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : IQueryHandler<GetActivityOverviewQuery, Result<IReadOnlyList<ActivityOverviewRowDto>>>
{
    public async Task<Result<IReadOnlyList<ActivityOverviewRowDto>>> Handle(
        GetActivityOverviewQuery query, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        IQueryable<ActivitySummary> summaries = db.Set<ActivitySummary>().AsNoTracking();
        if (query.FromUtc is { } fromUtc)
            summaries = summaries.Where(summary => summary.StartedAtUtc >= fromUtc);
        if (query.ToUtc is { } toUtc)
            summaries = summaries.Where(summary => summary.StartedAtUtc < toUtc);
        if (query.CharacterId is { } characterId)
            // Correlated against Run rather than a stored participant list — ActivitySummary carries none — using
            // the same Saved/non-deleted filter RebuildActivitySummariesCommandHandler built the summary from.
            summaries = summaries.Where(summary => db.Set<Run>().Any(run =>
                run.CharacterId == characterId && run.State == RunState.Saved && !run.DeletedAtUtc.HasValue
                && ((summary.GroupCode != null && run.GroupCode == summary.GroupCode)
                    || (summary.RunId != null && run.Id == summary.RunId))));
        if (query.FleetId is { } fleetId)
            // A fleet has no field of its own on Run or ActivitySummary — only the group code does, and only via
            // RunGroupOrigin (ET-182). A solo activity's GroupCode is null and never matches.
            summaries = summaries.Where(summary => summary.GroupCode != null && db.Set<RunGroupOrigin>()
                .Any(origin => origin.GroupCode == summary.GroupCode && origin.FleetId == fleetId));

        List<ActivitySummary> matched = await summaries
            .OrderByDescending(summary => summary.StartedAtUtc)
            .ToListAsync(cancellationToken);
        if (matched.Count == 0)
            return Result<IReadOnlyList<ActivityOverviewRowDto>>.Success([]);

        List<string> groupCodes = [.. matched.Where(summary => summary.GroupCode != null).Select(summary => summary.GroupCode!)];
        List<Guid> runIds = [.. matched.Where(summary => summary.RunId != null).Select(summary => summary.RunId!.Value)];
        // The runs behind these activities, resolved once so RunParameter can be filtered by a plain RunId
        // IN-list rather than joined through the Run navigation.
        var memberRuns = await db.Set<Run>()
            .AsNoTracking()
            .Where(run => run.State == RunState.Saved && !run.DeletedAtUtc.HasValue
                          && ((run.GroupCode != null && groupCodes.Contains(run.GroupCode)) || runIds.Contains(run.Id)))
            .Select(run => new
            {
                run.Id, run.GroupCode, run.CharacterId, run.CharacterNameSnapshot,
                run.AutoSavedAtUtc, run.SyncServerAddress, run.SyncState
            })
            .ToListAsync(cancellationToken);
        Dictionary<Guid, string> activityKeyByRunId = memberRuns.ToDictionary(run => run.Id, run => run.GroupCode ?? run.Id.ToString());

        // The reward per kind comes from RunParameter grouped by ParameterKey, never summed into one ISK figure —
        // the enum keeps growing and some of its members (LP, Evermarks) have no ISK rate to convert against.
        List<Guid> memberRunIds = [.. activityKeyByRunId.Keys];
        List<RunParameter> parameters = await db.Set<RunParameter>()
            .AsNoTracking()
            .Where(parameter => memberRunIds.Contains(parameter.RunId))
            .ToListAsync(cancellationToken);
        ILookup<string, RunParameter> rewardsByActivity = parameters.ToLookup(parameter => activityKeyByRunId[parameter.RunId]);
        // Character id + the run's own name snapshot (ET-212), so the crew line can name a fleet mate this machine
        // never linked (ET-247) — the same precedence the expanded row already uses.
        ILookup<string, (long CharacterId, string? CharacterNameSnapshot)> crewByActivity = memberRuns.ToLookup(
            run => run.GroupCode ?? run.Id.ToString(), run => (run.CharacterId, run.CharacterNameSnapshot));
        HashSet<string> autoSavedActivities = [.. memberRuns
            .Where(run => run.AutoSavedAtUtc.HasValue)
            .Select(run => run.GroupCode ?? run.Id.ToString())];
        // One entry per (activity, server), pending when any of that activity's runs is still queued for it. The
        // address is matched out rather than tested, so an unpublished run drops out with nothing left to unwrap.
        ILookup<string, ActivityServerSyncDto> syncByActivity = memberRuns
            .SelectMany(run => run.SyncServerAddress is { } address
                ? new[] { (Activity: run.GroupCode ?? run.Id.ToString(), Address: address, run.SyncState) }
                : [])
            .GroupBy(entry => (entry.Activity, entry.Address))
            .Select(group => (group.Key.Activity, Sync: new ActivityServerSyncDto(
                group.Key.Address, group.Any(entry => entry.SyncState == RunSyncState.Pending),
                group.Any(entry => entry.SyncState == RunSyncState.Outdated))))
            .ToLookup(entry => entry.Activity, entry => entry.Sync);

        return Result<IReadOnlyList<ActivityOverviewRowDto>>.Success(
            [.. matched.Select(summary =>
            {
                string activity = summary.GroupCode ?? summary.RunId!.Value.ToString();
                return _ToDto(summary, rewardsByActivity[activity], crewByActivity[activity],
                    autoSavedActivities.Contains(activity), syncByActivity[activity]);
            })]);
    }

    private static ActivityOverviewRowDto _ToDto(
        ActivitySummary summary, IEnumerable<RunParameter> rewardRows,
        IEnumerable<(long CharacterId, string? CharacterNameSnapshot)> crew, bool hasAutoSavedRun,
        IEnumerable<ActivityServerSyncDto> serverSyncStates)
    {
        RunParameter[] all = [.. rewardRows];
        // AbyssalFilament is the pocket's own tier and weather (ET-241), never a reward the pilot earned — read
        // separately for the row's own name, and kept out of the reward-chip list below on purpose. Its resolved
        // type id and count (ET-249) are CONSUMABLES' own cost, not a reward either.
        string? abyssalFilamentText =
            all.FirstOrDefault(parameter => parameter.ParameterKey == RunParameterKey.AbyssalFilament)?.TypedValue;
        RunParameter[] rewards = [.. all.Where(parameter => parameter.ParameterKey is not (
            RunParameterKey.AbyssalFilament or RunParameterKey.AbyssalFilamentTypeId or RunParameterKey.AbyssalFilamentCount))];
        return new ActivityOverviewRowDto(
            summary.Id, summary.GroupCode, summary.RunId, summary.ActivityKind, summary.SiteName,
            summary.SignatureGroupSnapshot, summary.SolarSystemId,
            summary.StartedAtUtc, summary.DurationSeconds, summary.RunsIncluded, summary.ParticipantCount,
            [.. crew.GroupBy(member => member.CharacterId)
                .Select(group => new ActivityCrewMemberDto(
                    group.Key,
                    group.Select(member => member.CharacterNameSnapshot).FirstOrDefault(name => !string.IsNullOrEmpty(name))))
                .OrderBy(member => member.CharacterId)],
            [.. rewards.GroupBy(reward => reward.ParameterKey)
                .Select(group => new ActivityRewardDto(group.Key, _SumOrNull(group.Select(reward => reward.Amount))))],
            summary.BountyIsk, summary.LootIskNet, summary.EnemyTypeCount,
            rewards.Any(reward => reward.ParameterKey == RunParameterKey.Escalation),
            hasAutoSavedRun,
            [.. serverSyncStates],
            StoredIskBreakdown.Read(summary.IskContributions),
            abyssalFilamentText);
    }

    private static decimal? _SumOrNull(IEnumerable<decimal?> amounts)
    {
        decimal[] known = [.. amounts.Where(amount => amount.HasValue).Select(amount => amount!.Value)];
        return known.Length == 0 ? null : known.Sum();
    }
}
