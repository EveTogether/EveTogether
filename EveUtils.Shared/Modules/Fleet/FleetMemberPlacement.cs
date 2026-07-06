using System.Linq;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Shared.Modules.Fleet;

/// <summary>
/// EVE-parity member placement: resolves the wing + squad a joining member lands in,
/// shared by every auto-place join path (public join, accepted join-request, invite-accept without an explicit
/// position). Drops the member into the first squad with a free seat (wings/squads in Id order); when every squad is
/// full it auto-creates the next squad — and a new wing once the wings are full of squads — within the EVE structure
/// limits, mirroring how an in-game fleet grows. Only a structurally full fleet (5 wings × 5 squads × 10) yields the
/// ESI <c>-1/-1</c> "unassigned" sentinel, leaving the owner to place the member manually.
/// </summary>
internal static class FleetMemberPlacement
{
    public static async Task<(long WingId, long SquadId)> ResolveOrCreateSquadAsync(
        IFleetRepository repository, long fleetId, CancellationToken cancellationToken)
    {
        var roster = await repository.ListMembersAsync(fleetId, cancellationToken);
        var wings = (await repository.ListWingsAsync(fleetId, cancellationToken)).ToList(); // Id-ordered

        // 1. First existing squad with a free seat.
        foreach (var wing in wings)
        {
            var squads = await repository.ListSquadsAsync(wing.Id, cancellationToken); // Id-ordered
            foreach (var squad in squads)
                if (roster.Count(m => m.SquadId == squad.Id) < FleetStructureLimits.MaxMembersPerSquad)
                    return (wing.Id, squad.Id);
        }

        // 2. No open squad → add the next squad to the first wing that still has room for one.
        foreach (var wing in wings)
        {
            var squadCount = (await repository.ListSquadsAsync(wing.Id, cancellationToken)).Count;
            if (squadCount < FleetStructureLimits.MaxSquadsPerWing)
            {
                var squadId = await repository.AddSquadAsync(
                    new FleetSquad { WingId = wing.Id, Name = $"Squad {squadCount + 1}" }, cancellationToken);
                return (wing.Id, squadId);
            }
        }

        // 3. Every wing is full of squads → add a new wing with its first squad, if the fleet has room for a wing.
        if (wings.Count < FleetStructureLimits.MaxWingsPerFleet)
        {
            var wingId = await repository.AddWingAsync(
                new FleetWing { FleetId = fleetId, Name = $"Wing {wings.Count + 1}" }, cancellationToken);
            var squadId = await repository.AddSquadAsync(
                new FleetSquad { WingId = wingId, Name = "Squad 1" }, cancellationToken);
            return (wingId, squadId);
        }

        // 4. Structurally full — leave unassigned (the fleet-size cap rejects further joins upstream).
        return (-1, -1);
    }
}
