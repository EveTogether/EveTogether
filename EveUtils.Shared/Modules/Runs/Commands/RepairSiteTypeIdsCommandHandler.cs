using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class RepairSiteTypeIdsCommandHandler(
    IDbContextFactory<ClientDbContext> contextFactory, ISdeAccessor sde, IDispatcher dispatcher)
    : ICommandHandler<RepairSiteTypeIdsCommand, Result<int>>
{
    public async Task<Result<int>> Handle(RepairSiteTypeIdsCommand command, CancellationToken cancellationToken = default)
    {
        if (!sde.IsAvailable)
            return Result<int>.Success(0);

        // Read live rather than hardcoded (ET-228 AC-3: only an exact match repairs a run, never a guess) — every
        // archetype, not just Homefront's 70 (ET-275): the same "no guessing" guard applies to a Combat Site or an
        // Ore Site's dungeon id as much as to a homefront's. The group-by-name guard is defensive: domain/homefronts.md
        // measured 218 of 1014 English names shared by more than one dungeon across the whole catalogue, and a name
        // with more than one dungeon must fall back to "do not touch this name" rather than pick one of them.
        Dictionary<string, int> dungeonIdByName = sde.SearchSites()
            .GroupBy(site => site.Name)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().DungeonId);
        if (dungeonIdByName.Count == 0)
            return Result<int>.Success(0);

        // A HashSet, not the dictionary's own KeyCollection: EF Core translates Contains against a captured
        // collection like this into a SQL IN clause, which a dictionary's key view is not guaranteed to support.
        HashSet<string> catalogueNames = [.. dungeonIdByName.Keys];
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // ET-261: Uncatalogued is not a competing guess, it is Site's own predecessor — a run started before this
        // catalogue existed (or before the SDE build behind it carried the site) recorded "the pilot's clipboard
        // named it, the catalogue just did not have it yet" (SiteTypeSource.cs's own doc on the value). An exact
        // name match is exactly as much proof for one of those runs as for a fresh SiteTypeSource.Site run — so it
        // repairs the same way, and its source is corrected to Site along with its id.
        List<Run> candidates = await db.Set<Run>()
            .Where(run => (run.SiteTypeSource == SiteTypeSource.Site || run.SiteTypeSource == SiteTypeSource.Uncatalogued)
                && run.SiteName != null && catalogueNames.Contains(run.SiteName!))
            .ToListAsync(cancellationToken);

        List<Guid> repairedRunIds = [];
        foreach (Run run in candidates)
        {
            int correctId = dungeonIdByName[run.SiteName!];
            if (run.SiteTypeId == correctId && run.SiteTypeSource == SiteTypeSource.Site)
                continue;

            run.SiteTypeId = correctId;
            run.SiteTypeSource = SiteTypeSource.Site;
            // The same correction rule ET-215 gave a saved run's loot: the revision moves, and a published copy
            // turns Outdated rather than Pending, so the fix reaches the server only when the pilot next publishes.
            RunLootWrites.MarkCorrected(run);
            repairedRunIds.Add(run.Id);
        }

        if (repairedRunIds.Count == 0)
            return Result<int>.Success(0);

        await db.SaveChangesAsync(cancellationToken);
        // ActivitySummary.SiteTypeId is denormalised off Run.SiteTypeId at save/rebuild time (RebuildActivitySummariesCommandHandler),
        // so the detail screen and the runs overview — which read the summary, never Run directly — would keep
        // showing the old TYPE without this. Scoped per repaired run rather than a full rebuild: this is a handful
        // of old activities at most, not the whole store ET-210 measured a full rebuild costing seconds over.
        foreach (Guid runId in repairedRunIds)
            await dispatcher.Send(new RebuildActivitySummariesCommand(runId), cancellationToken);
        return Result<int>.Success(repairedRunIds.Count);
    }
}
