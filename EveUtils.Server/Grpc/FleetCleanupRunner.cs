using EveUtils.Shared.Modules.Fleet.Cleanup;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Server.Grpc;

/// <summary>
/// One cleanup sweep over the fleets: archive Active fleets that have gone inactive (no connected member
/// per <see cref="FleetBroadcastResolver"/> and no member event past the grace, end-time accelerating) and
/// hard-delete fleets that have been Archived past the keep-window. Pulled out of the background service so a
/// headless check can run a deterministic sweep against a supplied "now". The decision itself is the pure
/// <see cref="FleetCleanupPolicy"/>; this only loads, applies and persists.
/// </summary>
public sealed class FleetCleanupRunner(IFleetRepository repository, FleetBroadcastResolver broadcast)
{
    public async Task<SweepResult> SweepAsync(DateTimeOffset now, FleetCleanupOptions options, CancellationToken cancellationToken = default)
    {
        var archived = 0;
        var deleted = 0;

        foreach (var fleet in await repository.ListByStateAsync(FleetState.Active, cancellationToken))
        {
            var hasActive = await broadcast.HasConnectedMemberAsync(fleet.Id, cancellationToken);
            if (FleetCleanupPolicy.Evaluate(FleetState.Active, fleet.Activation, fleet.ToTime, fleet.LastActivityAt, hasActive, now, options)
                != FleetCleanupAction.Archive)
                continue;

            fleet.State = FleetState.Archived;
            fleet.LastActivityAt = now; // doubles as the archived-at clock for the hard-delete window
            await repository.UpdateAsync(fleet, cancellationToken);
            archived++;
        }

        foreach (var fleet in await repository.ListByStateAsync(FleetState.Archived, cancellationToken))
        {
            if (FleetCleanupPolicy.Evaluate(FleetState.Archived, fleet.Activation, fleet.ToTime, fleet.LastActivityAt, hasActiveParticipants: false, now, options)
                != FleetCleanupAction.Delete)
                continue;

            await repository.DeleteAsync(fleet.Id, cancellationToken);
            deleted++;
        }

        return new SweepResult(archived, deleted);
    }

    public readonly record struct SweepResult(int Archived, int Deleted);
}
