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
internal sealed class GetOpenEscalationsQueryHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : IQueryHandler<GetOpenEscalationsQuery, Result<IReadOnlyList<OpenEscalationDto>>>
{
    private static readonly RunParameterKey[] EscalationKeys =
    [
        RunParameterKey.Escalation, RunParameterKey.EscalationDungeonId, RunParameterKey.EscalationSystem,
        RunParameterKey.EscalationSolarSystemId, RunParameterKey.EscalationExpiresAtUtc,
        RunParameterKey.EscalationOutcome, RunParameterKey.EscalationCompletedByRunId
    ];

    public async Task<Result<IReadOnlyList<OpenEscalationDto>>> Handle(
        GetOpenEscalationsQuery query, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        IQueryable<Run> sources = db.Set<Run>().AsNoTracking()
            .Where(run => !run.DeletedAtUtc.HasValue
                && run.Parameters.Any(parameter => parameter.ParameterKey == RunParameterKey.Escalation));
        if (query.CharacterIds is { } characterIds)
        {
            List<long> ids = [.. characterIds];
            sources = sources.Where(run => ids.Contains(run.CharacterId));
        }
        List<Run> runs = await sources.ToListAsync(cancellationToken);
        if (runs.Count == 0)
            return Result<IReadOnlyList<OpenEscalationDto>>.Success([]);

        List<Guid> runIds = [.. runs.Select(run => run.Id)];
        ILookup<Guid, RunParameterDto> parametersByRun = (await db.Set<RunParameter>().AsNoTracking()
                .Where(parameter => runIds.Contains(parameter.RunId) && EscalationKeys.Contains(parameter.ParameterKey))
                .Select(parameter => new RunParameterDto(parameter.RunId, parameter.ParameterKey, parameter.TypedValue,
                    parameter.Amount, parameter.ItemTypeId, parameter.BonusWindowSeconds, parameter.ObservedAtUtc,
                    parameter.EntryId))
                .ToListAsync(cancellationToken))
            .ToLookup(parameter => parameter.RunId);

        // An escalation run started and not saved yet still leaves the escalation open — but a second start for it
        // would be a mistake, so the row carries the run already flying it.
        List<RunParameterDto> unsavedLinks = await db.Set<RunParameter>().AsNoTracking()
            .Where(parameter => parameter.ParameterKey == RunParameterKey.EscalationSourceRunId
                && parameter.Run != null && parameter.Run.State != RunState.Saved && !parameter.Run.DeletedAtUtc.HasValue)
            .Select(parameter => new RunParameterDto(parameter.RunId, parameter.ParameterKey, parameter.TypedValue,
                parameter.Amount, parameter.ItemTypeId, parameter.BonusWindowSeconds, parameter.ObservedAtUtc,
                parameter.EntryId))
            .ToListAsync(cancellationToken);
        Dictionary<(Guid SourceRunId, Guid? EntryId), Guid> inProgress = [];
        foreach (RunParameterDto link in unsavedLinks)
            if (RunEscalations.SourceOf([link]) is { } source)
                inProgress.TryAdd(source, link.RunId);

        DateTime nowUtc = DateTime.UtcNow;
        List<OpenEscalationDto> open = [.. runs
            .SelectMany(run => RunEscalations.Read(parametersByRun[run.Id])
                .Where(escalation => escalation.IsOpenAt(nowUtc))
                .Select(escalation => new OpenEscalationDto(
                    run.Id, run.CharacterId, run.CharacterNameSnapshot, run.SiteName, run.StartedAtUtc, escalation,
                    inProgress.TryGetValue((run.Id, escalation.EntryId), out Guid inProgressRunId)
                        ? inProgressRunId
                        : null)))
            .OrderBy(row => row.Escalation.ExpiresAtUtc ?? DateTime.MaxValue)
            .ThenBy(row => row.Escalation.RegisteredAtUtc)];
        return Result<IReadOnlyList<OpenEscalationDto>>.Success(open);
    }
}
