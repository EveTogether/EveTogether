using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Server.Grpc;

/// <summary>
/// Resolves a fleet's live broadcast set, server-authoritatively: the roster members who currently hold a live
/// event-bus connection — membership (DB) ∩ presence (<see cref="ConnectedClients"/>). This replaces the former
/// client-Enter-driven <c>ActiveFleetRegistry</c>: leaving, being kicked, a disband or a disconnect all drop a
/// character automatically because they no longer satisfy "member ∧ connected", so there is no participation state
/// to keep in sync (which previously let a kicked/disbanded member keep receiving the fleet's broadcasts).
/// </summary>
public sealed class FleetBroadcastResolver(IFleetReader repository, ConnectedClients connectedClients)
{
    /// <summary>The roster members of the fleet that currently hold a live connection — the fleet-scoped delivery set.</summary>
    public async Task<IReadOnlyList<int>> ConnectedMembersAsync(long fleetId, CancellationToken cancellationToken = default)
    {
        var members = await repository.ListMembersAsync(fleetId, cancellationToken);
        return members
            .Select(m => m.CharacterId)
            .Where(connectedClients.IsConnected)
            .Distinct()
            .ToList();
    }

    /// <summary>Everyone who shares a roster with the character and holds a live connection, the character itself left
    /// out — who has to hear that it connected or went away (ET-492). Finished fleets do not count: nobody starts those.</summary>
    public async Task<IReadOnlyList<int>> ConnectedFleetMatesAsync(int characterId, CancellationToken cancellationToken = default)
    {
        HashSet<int> mates = [];
        foreach (var fleet in await repository.ListForParticipantAsync(characterId, cancellationToken: cancellationToken))
        {
            var members = await repository.ListMembersAsync(fleet.Id, cancellationToken);
            mates.UnionWith(members
                .Select(m => m.CharacterId)
                .Where(id => id != characterId && connectedClients.IsConnected(id)));
        }

        return [.. mates];
    }

    /// <summary>True if the fleet has at least one connected member — the inactivity signal. Activation-agnostic:
    /// a connected member of a Forming fleet (signed up in advance) still keeps it alive.</summary>
    public async Task<bool> HasConnectedMemberAsync(long fleetId, CancellationToken cancellationToken = default) =>
        (await ConnectedMembersAsync(fleetId, cancellationToken)).Count > 0;

    /// <summary>True if the character is a roster member of the fleet — the send-side authorization check so a
    /// non-member cannot reroute events into a fleet's broadcast stream.</summary>
    public async Task<bool> IsMemberAsync(long fleetId, int characterId, CancellationToken cancellationToken = default)
    {
        var members = await repository.ListMembersAsync(fleetId, cancellationToken);
        return members.Any(m => m.CharacterId == characterId);
    }

    /// <summary>The broadcast set for live metrics: connected members of the fleet, but only once it is ACTIVE,
    /// and only those for whom THIS is the active fleet they were activated in first. A Forming or Concluded fleet
    /// broadcasts nothing — empty set. The activated-first tiebreak enforces "one active fleet per character"
    /// (2026-06-04): a member signed up in advance to a fleet that starts while they are still in an earlier active
    /// fleet stays coupled to that earlier fleet and is excluded here. The entry-guard blocks the common cases, so
    /// the per-member lookup below short-circuits whenever a character is in just this one active fleet.</summary>
    /// <summary>Whether the fleet broadcasts at all: started, not merely formed (ET-440).</summary>
    public async Task<bool> IsStartedAsync(long fleetId, CancellationToken cancellationToken = default) =>
        (await repository.GetAsync(fleetId, cancellationToken))?.Activation == FleetActivation.Active;

    public async Task<IReadOnlyList<int>> ActiveBroadcastMembersAsync(long fleetId, CancellationToken cancellationToken = default)
    {
        var fleet = await repository.GetAsync(fleetId, cancellationToken);
        if (fleet is null || fleet.Activation != FleetActivation.Active)
            return [];

        var connected = await ConnectedMembersAsync(fleetId, cancellationToken);
        var result = new List<int>(connected.Count);
        foreach (var characterId in connected)
        {
            var actives = await repository.ListActiveMembershipsAsync(characterId, cancellationToken);
            // In just this one active fleet (the normal case) → broadcast here.
            if (actives.Count <= 1)
            {
                result.Add(characterId);
                continue;
            }

            // In several active fleets → couple only to the one activated first (null sorts earliest), Id as tiebreak.
            var primary = actives
                .OrderBy(m => m.ActivatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(m => m.FleetId)
                .First();
            if (primary.FleetId == fleetId)
                result.Add(characterId);
        }

        return result;
    }
}
