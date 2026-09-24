using EveUtils.Shared.Modules.Fleet.Entities;
// Outside the .Entities namespace the enclosing namespace "Fleet" shadows the entity type of the same
// name, so alias the type to reference it unambiguously (the .Entities files don't need this).
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Shared.Modules.Fleet.Repositories;

/// <summary>
/// The read half of <see cref="IFleetRepository"/> (ET-383). Everything outside the fleet command handlers takes this
/// one: a fleet write outside a handler publishes no <c>FleetChangedEvent</c>, so no screen or client hears it.
/// </summary>
public interface IFleetReader
{
    Task<FleetEntity?> GetAsync(long fleetId, CancellationToken cancellationToken = default);

    /// <summary>The fleets a character owns.</summary>
    Task<IReadOnlyList<FleetEntity>> ListByCreatorAsync(int creatorCharacterId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The active fleets a character is involved in — owns OR is a roster member of. The "MY FLEETS" list:
    /// a creator manages their own; a member who accepted an invite needs the fleet here so they can enter it.
    /// A concluded fleet is finished and hidden by default; <paramref name="includeConcluded"/> lets the one screen
    /// that keeps a record of them (the overview's FINISHED band, ET-170) ask for them.
    /// </summary>
    Task<IReadOnlyList<FleetEntity>> ListForParticipantAsync(int characterId, bool includeConcluded = false, CancellationToken cancellationToken = default);

    /// <summary>Active, publicly listable fleets on this server.</summary>
    Task<IReadOnlyList<FleetEntity>> ListOpenAsync(CancellationToken cancellationToken = default);

    /// <summary>True if the fleet is one <see cref="ListOpenAsync"/> would list — the same predicate, so "what a
    /// non-member can discover" has one definition.</summary>
    Task<bool> IsOpenAsync(long fleetId, CancellationToken cancellationToken = default);

    /// <summary>All fleets in the given state (cleanup sweep: Active → archive candidates, Archived → delete).</summary>
    Task<IReadOnlyList<FleetEntity>> ListByStateAsync(FleetState state, CancellationToken cancellationToken = default);

    /// <summary>Counts how many fleets are coupled to each of the given compositions (via <c>FleetCompositionId</c>),
    /// for the library's "N fleets" pill. Compositions with no coupled fleet are absent from the result.</summary>
    Task<IReadOnlyDictionary<long, int>> CountFleetsByCompositionIdsAsync(IReadOnlyCollection<long> compositionIds, CancellationToken cancellationToken = default);

    Task<FleetWing?> GetWingAsync(long wingId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FleetWing>> ListWingsAsync(long fleetId, CancellationToken cancellationToken = default);

    Task<FleetSquad?> GetSquadAsync(long squadId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FleetSquad>> ListSquadsAsync(long wingId, CancellationToken cancellationToken = default);

    Task<FleetInvite?> GetInviteAsync(long inviteId, CancellationToken cancellationToken = default);

    /// <summary>A character's still-open invites (Pending), for the on-attach durable sync.</summary>
    Task<IReadOnlyList<FleetInvite>> ListPendingInvitesForInviteeAsync(int inviteeCharacterId, CancellationToken cancellationToken = default);

    /// <summary>A fleet's still-open invites (Pending), for the roster's pending-invites section.</summary>
    Task<IReadOnlyList<FleetInvite>> ListPendingInvitesForFleetAsync(long fleetId, CancellationToken cancellationToken = default);

    /// <summary>True if the invitee already has a Pending invite for this fleet (avoid duplicate invites).</summary>
    Task<bool> HasPendingInviteAsync(long fleetId, int inviteeCharacterId, CancellationToken cancellationToken = default);

    Task<FleetJoinRequest?> GetJoinRequestAsync(long requestId, CancellationToken cancellationToken = default);

    /// <summary>A fleet's still-open join requests (Pending), for the owner's roster pending-section.</summary>
    Task<IReadOnlyList<FleetJoinRequest>> ListPendingJoinRequestsForFleetAsync(long fleetId, CancellationToken cancellationToken = default);

    /// <summary>True if the requester already has a Pending join request for this fleet (avoid duplicates).</summary>
    Task<bool> HasPendingJoinRequestAsync(long fleetId, int requesterCharacterId, CancellationToken cancellationToken = default);

    Task<bool> IsMemberAsync(long fleetId, int characterId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The started (Active, non-archived) fleets the character is a roster member of, each with its activation
    /// timestamp. Empty when the character is in no active fleet. Drives the one-active-fleet entry-guard and the
    /// broadcast tiebreak (2026-06-04). Concluded and Forming fleets are excluded — they do not broadcast.
    /// </summary>
    Task<IReadOnlyList<ActiveFleetMembership>> ListActiveMembershipsAsync(int characterId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FleetMember>> ListMembersAsync(long fleetId, CancellationToken cancellationToken = default);

    /// <summary>A single roster member by its primary key (member-move, roster management).</summary>
    Task<FleetMember?> GetMemberAsync(long memberId, CancellationToken cancellationToken = default);
}
