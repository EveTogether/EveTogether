using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Commands;

[ClientOnly]
internal sealed class FillRunSolarSystemsCommandHandler(IDbContextFactory<ClientDbContext> contextFactory, IDispatcher dispatcher)
    : ICommandHandler<FillRunSolarSystemsCommand, Result<int>>
{
    public async Task<Result<int>> Handle(FillRunSolarSystemsCommand command, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        List<Run> withoutSystem = await db.Set<Run>()
            .Where(run => run.State == RunState.Saved && run.DeletedAtUtc == null
                && run.SolarSystemId == null && run.GroupCode != null)
            .ToListAsync(cancellationToken);
        if (withoutSystem.Count == 0)
            return Result<int>.Success(0);

        List<string> groupCodes = [.. withoutSystem.Select(run => run.GroupCode!).Distinct()];
        Dictionary<string, List<int>> systemsByGroup = (await db.Set<Run>()
                .AsNoTracking()
                .Where(run => run.DeletedAtUtc == null && run.SolarSystemId != null && groupCodes.Contains(run.GroupCode!))
                .Select(run => new { run.GroupCode, SolarSystemId = run.SolarSystemId!.Value })
                .Distinct()
                .ToListAsync(cancellationToken))
            .GroupBy(row => row.GroupCode!)
            .ToDictionary(group => group.Key, group => group.Select(row => row.SolarSystemId).ToList());

        List<Guid> filledRunIds = [];
        foreach (Run run in withoutSystem)
        {
            if (!systemsByGroup.TryGetValue(run.GroupCode!, out List<int>? systems) || systems is not [int only])
                continue;

            run.SolarSystemId = only;
            RunLootWrites.MarkCorrected(run);
            filledRunIds.Add(run.Id);
        }

        if (filledRunIds.Count == 0)
            return Result<int>.Success(0);

        await db.SaveChangesAsync(cancellationToken);
        foreach (Guid runId in filledRunIds)
            await dispatcher.Send(new RebuildActivitySummariesCommand(runId), cancellationToken);
        return Result<int>.Success(filledRunIds.Count);
    }
}
