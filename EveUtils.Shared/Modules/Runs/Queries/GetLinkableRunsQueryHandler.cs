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
internal sealed class GetLinkableRunsQueryHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : IQueryHandler<GetLinkableRunsQuery, Result<IReadOnlyList<LinkableRunDto>>>
{
    public async Task<Result<IReadOnlyList<LinkableRunDto>>> Handle(
        GetLinkableRunsQuery query, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        Run? source = await db.Set<Run>().AsNoTracking().Include(run => run.Parameters)
            .FirstOrDefaultAsync(run => run.Id == query.SourceRunId && !run.DeletedAtUtc.HasValue, cancellationToken);
        RunEscalationDto? escalation = source is null
            ? null
            : RunEscalations.Read(source.Parameters.Select(parameter => new RunParameterDto(parameter.RunId,
                    parameter.ParameterKey, parameter.TypedValue, parameter.Amount, parameter.ItemTypeId,
                    parameter.BonusWindowSeconds, parameter.ObservedAtUtc, parameter.EntryId)))
                .FirstOrDefault(entry => entry.EntryId == query.EntryId);
        if (source is null || escalation is not { Outcome: null })
            return Result<IReadOnlyList<LinkableRunDto>>.Success([]);

        DateTime lastFlyableAtUtc = RunEscalations.LastFlyableAtUtc(escalation);
        List<Run> candidates = await db.Set<Run>().AsNoTracking()
            .Where(run => run.Id != source.Id && !run.DeletedAtUtc.HasValue && run.State == RunState.Saved
                && run.ActivityKind == ActivityKind.Site && run.StartedAtUtc > source.StartedAtUtc
                && run.StartedAtUtc <= lastFlyableAtUtc)
            .ToListAsync(cancellationToken);
        HashSet<Guid> alreadyTied = await _RunsTiedToAnEscalationAsync(db, cancellationToken);

        return Result<IReadOnlyList<LinkableRunDto>>.Success(
        [
            .. candidates
                .Where(run => !alreadyTied.Contains(run.Id) && RunEscalations.IsSiteOf(escalation, run.SiteTypeId, run.SiteName))
                .Select(run => new LinkableRunDto(run.Id, run.CharacterId, run.CharacterNameSnapshot, run.SiteName,
                    run.SolarSystemId, run.StartedAtUtc, run.CharacterId == source.CharacterId,
                    escalation.SolarSystemId is not null && run.SolarSystemId == escalation.SolarSystemId))
                .OrderByDescending(row => row.IsSameCharacter)
                .ThenByDescending(row => row.IsSameSystem)
                .ThenBy(row => row.StartedAtUtc)
        ]);
    }

    // An escalation run started from START, or a run another escalation already names as its flown run: either one is
    // spoken for, so offering it again would tie a run to two escalations.
    private static async Task<HashSet<Guid>> _RunsTiedToAnEscalationAsync(ClientDbContext db, CancellationToken cancellationToken)
    {
        List<RunParameter> links = await db.Set<RunParameter>().AsNoTracking()
            .Where(parameter => parameter.ParameterKey == RunParameterKey.EscalationSourceRunId
                || parameter.ParameterKey == RunParameterKey.EscalationCompletedByRunId)
            .ToListAsync(cancellationToken);
        return
        [
            .. links.Select(link => link.ParameterKey == RunParameterKey.EscalationSourceRunId
                ? link.RunId
                : Guid.TryParse(link.TypedValue, out Guid completedBy) ? completedBy : Guid.Empty)
        ];
    }
}
