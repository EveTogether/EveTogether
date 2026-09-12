using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class DedupeMissionRewardParametersCommandHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IDispatcher dispatcher)
    : ICommandHandler<DedupeMissionRewardParametersCommand, Result<int>>
{
    public async Task<Result<int>> Handle(DedupeMissionRewardParametersCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Only a grouped Mission run ever carries reward parameters at all (ClipboardMissionOffer is the sole
        // writer), and only a group of more than one run could have received the duplicate in the first place.
        List<Run> groupedMissionRuns = await db.Set<Run>()
            .Where(run => run.GroupCode != null && run.ActivityKind == ActivityKind.Mission
                          && run.State == RunState.Saved && !run.DeletedAtUtc.HasValue)
            .ToListAsync(cancellationToken);
        Dictionary<string, List<Run>> runsByGroup = groupedMissionRuns
            .GroupBy(run => run.GroupCode!)
            .Where(group => group.Count() > 1)
            .ToDictionary(group => group.Key, group => group.ToList());
        if (runsByGroup.Count == 0)
            return Result<int>.Success(0);

        List<Guid> candidateRunIds = [.. runsByGroup.Values.SelectMany(runs => runs.Select(run => run.Id))];
        List<RunParameter> parameters = await db.Set<RunParameter>()
            .Where(parameter => candidateRunIds.Contains(parameter.RunId))
            .ToListAsync(cancellationToken);
        if (parameters.Count == 0)
            return Result<int>.Success(0);
        ILookup<Guid, RunParameter> parametersByRun = parameters.ToLookup(parameter => parameter.RunId);

        List<RunBountyEntry> bountyEntries = await db.Set<RunBountyEntry>()
            .AsNoTracking()
            .Where(entry => candidateRunIds.Contains(entry.RunId))
            .ToListAsync(cancellationToken);
        Dictionary<Guid, decimal> bountyByRun = bountyEntries.GroupBy(entry => entry.RunId)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Isk));

        HashSet<Guid> repairedRunIds = [];
        HashSet<string> repairedGroupCodes = [];
        foreach ((string groupCode, List<Run> runs) in runsByGroup)
        {
            List<Guid> runIdsWithParameters = [.. runs.Select(run => run.Id).Where(id => parametersByRun[id].Any())];
            if (runIdsWithParameters.Count <= 1)
                continue; // nothing duplicated in this group — a solo mission's siblings never got any (ET-210 loot/enemies-only tag-alongs)

            Guid keeper = runIdsWithParameters
                .OrderByDescending(id => bountyByRun.GetValueOrDefault(id))
                .ThenBy(id => id)
                .First();

            foreach (Guid runId in runIdsWithParameters.Where(id => id != keeper))
            {
                foreach (RunParameter parameter in parametersByRun[runId])
                    db.Remove(parameter);
                // The same correction rule ET-215 gave a saved run's loot: the revision moves, and a published copy
                // turns Outdated rather than Pending, so the fix reaches the server only when the pilot next publishes.
                RunLootWrites.MarkCorrected(runs.First(run => run.Id == runId));
                repairedRunIds.Add(runId);
            }
            repairedGroupCodes.Add(groupCode);
        }

        if (repairedRunIds.Count == 0)
            return Result<int>.Success(0);

        await db.SaveChangesAsync(cancellationToken);
        // One rebuild per repaired group is enough — RebuildActivitySummariesCommand rebuilds the whole activity a
        // run belongs to, not just that one run, so a second call for a sibling in the same group would be wasted
        // work (ET-210's own SAVE performance fix made exactly this mistake avoidable).
        foreach (string groupCode in repairedGroupCodes)
            await dispatcher.Send(new RebuildActivitySummariesCommand(runsByGroup[groupCode][0].Id), cancellationToken);
        return Result<int>.Success(repairedRunIds.Count);
    }
}
