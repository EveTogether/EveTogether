using System;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Queries;

namespace EveUtils.Client.Fleet;

/// <summary>
/// The completed-run count the stop dialog prints (ET-185), or null when that count cannot be trusted — shared by
/// every screen that offers a STOP so the roster window and the overview read the same coverage the same way. Null
/// travels all the way to <see cref="EveUtils.Client.Dialogs.StopFleetPrompt"/> unchanged rather than becoming a
/// zero: a fleet older than <c>RunGroupOrigin</c> (ET-182) can look empty for a reason that has nothing to do with
/// what it flew.
/// </summary>
public static class FleetCompletedRuns
{
    public static async Task<int?> CountAsync(
        IDispatcher dispatcher, long fleetId, DateTime fleetCreatedAtUtc, CancellationToken cancellationToken = default)
    {
        Result<FleetRunCoverageDto> coverage = await dispatcher.Query(
            new GetFleetRunCoverageQuery(fleetId, fleetCreatedAtUtc), cancellationToken);
        return coverage.IsSuccess && coverage.Value is { IsKnown: true } known ? known.CompletedCount : null;
    }
}
