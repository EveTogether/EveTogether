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
        if (run is null)
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
                && _IsSameSystem(row.Escalation, run) && RunEscalations.IsSiteOf(row.Escalation, run.SiteTypeId, run.SiteName))
        ]);
    }

    // A run saved without a system (a manual start) says nothing against the escalation: only a system known on both
    // sides and different rules it out.
    private static bool _IsSameSystem(RunEscalationDto escalation, Run run) =>
        escalation.SolarSystemId is not { } destination || run.SolarSystemId is not { } flown || destination == flown;
}
