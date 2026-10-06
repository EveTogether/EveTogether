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
internal sealed class FindMatchingEscalationsQueryHandler(
    IDbContextFactory<ClientDbContext> contextFactory, IDispatcher dispatcher)
    : IQueryHandler<FindMatchingEscalationsQuery, Result<IReadOnlyList<OpenEscalationDto>>>
{
    public async Task<Result<IReadOnlyList<OpenEscalationDto>>> Handle(
        FindMatchingEscalationsQuery query, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        Run? run = await db.Set<Run>().AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == query.RunId && !candidate.DeletedAtUtc.HasValue
                && candidate.State == RunState.Saved && candidate.ActivityKind == ActivityKind.Site, cancellationToken);
        if (run?.SolarSystemId is not { } solarSystemId)
            return Result<IReadOnlyList<OpenEscalationDto>>.Success([]);

        bool isEscalationRun = await db.Set<RunParameter>().AsNoTracking().AnyAsync(parameter =>
            parameter.RunId == run.Id && parameter.ParameterKey == RunParameterKey.EscalationSourceRunId, cancellationToken);
        if (isEscalationRun)
            return Result<IReadOnlyList<OpenEscalationDto>>.Success([]);

        Result<IReadOnlyList<OpenEscalationDto>> open =
            await dispatcher.Query(new GetOpenEscalationsQuery(new HashSet<long> { run.CharacterId }), cancellationToken);
        return Result<IReadOnlyList<OpenEscalationDto>>.Success(
        [
            .. (open.Value ?? []).Where(row => row.InProgressRunId is null && row.SourceRunId != run.Id
                && row.Escalation.SolarSystemId == solarSystemId && _IsSameSite(row.Escalation, run))
        ]);
    }

    // The dungeon id when both sides have one; a run that never resolved to a single dungeon (SiteTypeId 0) falls back
    // to the name the pilot saw.
    private static bool _IsSameSite(RunEscalationDto escalation, Run run) =>
        escalation.DungeonId is > 0 && run.SiteTypeId > 0
            ? escalation.DungeonId == run.SiteTypeId
            : string.Equals(escalation.SiteName, run.SiteName, StringComparison.OrdinalIgnoreCase);
}
