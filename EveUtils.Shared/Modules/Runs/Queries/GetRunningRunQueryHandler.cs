using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Runs.Queries;

internal sealed class GetRunningRunQueryHandler(IDbContextFactory<ClientDbContext> contextFactory)
    : IQueryHandler<GetRunningRunQuery, Result<RunningRunDto>>
{
    public async Task<Result<RunningRunDto>> Handle(GetRunningRunQuery query, CancellationToken cancellationToken = default)
    {
        await using ClientDbContext db = await contextFactory.CreateDbContextAsync(cancellationToken);
        (Run? run, int runningCount) = await RunningRunLookup.FindAsync(db, cancellationToken);
        if (run is null)
            return Result<RunningRunDto>.Failure(runningCount == 0
                ? new ResultMessage(MessageSeverity.Error, MessageCodes.NotFound, "No run is running.", "Runs")
                : new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                    $"{runningCount} runs are running, so which one this window is showing is ambiguous.", "Runs"));

        return Result<RunningRunDto>.Success(new RunningRunDto(
            run.Id, run.CharacterId, run.ActivityKind, run.StartedAtUtc, run.GroupCode, run.SiteName));
    }
}
