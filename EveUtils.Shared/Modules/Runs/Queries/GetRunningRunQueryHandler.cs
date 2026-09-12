using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

[ClientOnly]
internal sealed class GetRunningRunQueryHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : IQueryHandler<GetRunningRunQuery, Result<RunningRunDto>>
{
    public async Task<Result<RunningRunDto>> Handle(GetRunningRunQuery query, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // A named RunId (ET-254) is answered outright, Stopped included, with no ambiguity to report even if other
        // runs are running elsewhere — RunningRunLookup already has exactly this shape for the open window's own
        // preferred run, reused here for a window resuming a run it does not have open yet.
        (Run? run, int runningCount) = await RunningRunLookup.FindAsync(db, cancellationToken,
            includeStopped: query.RunId is not null, preferredRunId: query.RunId, characterId: query.CharacterId);
        if (run is null)
            return Result<RunningRunDto>.Failure(runningCount == 0
                ? new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound, "No run is running.", "Runs")
                : new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed, query.CharacterId is null
                    ? $"{runningCount} runs are running, so which one this window is showing is ambiguous."
                    : $"{runningCount} runs are running for this character, so which one this window is showing is ambiguous.",
                    "Runs"));

        // ET-252: a window adopting this row is not the one that started it, so the mission facts it needs — agent,
        // level, system and the reward lines already on the run — travel with it here rather than being left for
        // MISSION to show as unstated. Empty for a site or an abyssal, the same as AgentId/MissionLevel already are.
        List<RunParameterDto> parameters = await db.Set<RunParameter>().AsNoTracking()
            .Where(parameter => parameter.RunId == run.Id)
            .Select(parameter => new RunParameterDto(parameter.RunId, parameter.ParameterKey, parameter.TypedValue,
                parameter.Amount, parameter.ItemTypeId, parameter.BonusWindowSeconds, parameter.ObservedAtUtc))
            .ToListAsync(cancellationToken);

        return Result<RunningRunDto>.Success(new RunningRunDto(
            run.Id, run.CharacterId, run.ActivityKind, run.StartedAtUtc, run.GroupCode, run.SiteName, run.Signature,
            run.SignatureGroupSnapshot, run.SiteTypeId, run.AgentId, run.MissionLevel, run.SolarSystemId, parameters,
            run.StoppedAtUtc));
    }
}
