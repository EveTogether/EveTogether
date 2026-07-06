using EveUtils.Shared.Modules.Fleet.Entities;
// Outside the .Entities namespace the enclosing namespace "Fleet" shadows the entity type of the same
// name, so alias the type to reference it unambiguously (the .Entities files don't need this).
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Shared.Modules.Fleet.Repositories;

/// <summary>
/// A character's membership of a started (Active, non-archived) fleet, with the fleet's activation timestamp.
/// Backs the "one active fleet per character" rule (2026-06-04): the entry-guard rejects joining a second active
/// fleet, and the broadcast tiebreak couples a character to only the active fleet they were activated in first.
/// </summary>
public sealed record ActiveFleetMembership(long FleetId, string FleetName, DateTimeOffset? ActivatedAt);

/// <summary>
/// Server-side persistence for fleets. Covers the fleet-level operations the lifecycle and
/// discovery handlers need; wing/squad/member/invite operations are added alongside their handlers.
/// </summary>
public interface IFleetRepository
{
    Task<long> AddAsync(FleetEntity fleet, CancellationToken cancellationToken = default);

    Task<FleetEntity?> GetAsync(long fleetId, CancellationToken cancellationToken = default);

    /// <summary>Persists changes to an existing fleet (edit/disband). The entity is updated wholesale.</summary>
    Task UpdateAsync(FleetEntity fleet, CancellationToken cancellationToken = default);

    /// <summary>The fleets a character owns.</summary>
    Task<IReadOnlyList<FleetEntity>> ListByCreatorAsync(int creatorCharacterId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The active fleets a character is involved in — owns OR is a roster member of. The "MY FLEETS" list:
    /// a creator manages their own; a member who accepted an invite needs the fleet here so they can enter it.
    /// </summary>
    Task<IReadOnlyList<FleetEntity>> ListForParticipantAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>Active, publicly listable fleets on this server.</summary>
    Task<IReadOnlyList<FleetEntity>> ListOpenAsync(CancellationToken cancellationToken = default);

    // --- Persistence + cleanup. The background sweep lists fleets by state, bumps a fleet's
    // activity timestamp on a member event, and hard-deletes a long-archived fleet (its wings/squads/
    // members/invites cascade via FK). ---

    /// <summary>All fleets in the given state (cleanup sweep: Active → archive candidates, Archived → delete).</summary>
    Task<IReadOnlyList<FleetEntity>> ListByStateAsync(FleetState state, CancellationToken cancellationToken = default);

    /// <summary>Counts how many fleets are coupled to each of the given compositions (via <c>FleetCompositionId</c>),
    /// for the library's "N fleets" pill. Compositions with no coupled fleet are absent from the result.</summary>
    Task<IReadOnlyDictionary<long, int>> CountFleetsByCompositionIdsAsync(IReadOnlyCollection<long> compositionIds, CancellationToken cancellationToken = default);

    /// <summary>Bumps <see cref="FleetEntity.LastActivityAt"/> — the cleanup inactivity signal.</summary>
    Task TouchActivityAsync(long fleetId, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>Hard-deletes a fleet; its wings, squads, members and invites cascade with it (FK).</summary>
    Task DeleteAsync(long fleetId, CancellationToken cancellationToken = default);

    // --- Wing/squad structure. The handlers resolve a wing/squad back to its owning
    // fleet (GetWing → FleetId → GetAsync; GetSquad → WingId → GetWing → FleetId) for the creator check. ---

    Task<long> AddWingAsync(FleetWing wing, CancellationToken cancellationToken = default);

    Task<FleetWing?> GetWingAsync(long wingId, CancellationToken cancellationToken = default);

    Task UpdateWingAsync(FleetWing wing, CancellationToken cancellationToken = default);

    /// <summary>Removes a wing; its squads cascade with it (FK, see FleetSquadConfiguration).</summary>
    Task DeleteWingAsync(long wingId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FleetWing>> ListWingsAsync(long fleetId, CancellationToken cancellationToken = default);

    Task<long> AddSquadAsync(FleetSquad squad, CancellationToken cancellationToken = default);

    Task<FleetSquad?> GetSquadAsync(long squadId, CancellationToken cancellationToken = default);

    Task UpdateSquadAsync(FleetSquad squad, CancellationToken cancellationToken = default);

    Task DeleteSquadAsync(long squadId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FleetSquad>> ListSquadsAsync(long wingId, CancellationToken cancellationToken = default);

    // --- Invites + roster. The invite is the durable source of truth; accepting it
    // adds the invitee to the roster. The (FleetId, CharacterId) roster index is unique (one membership
    // per fleet); the (InviteeCharacterId, Status) invite index backs the on-attach pending sync. ---

    Task<long> AddInviteAsync(FleetInvite invite, CancellationToken cancellationToken = default);

    Task<FleetInvite?> GetInviteAsync(long inviteId, CancellationToken cancellationToken = default);

    Task UpdateInviteAsync(FleetInvite invite, CancellationToken cancellationToken = default);

    /// <summary>A character's still-open invites (Pending), for the on-attach durable sync.</summary>
    Task<IReadOnlyList<FleetInvite>> ListPendingInvitesForInviteeAsync(int inviteeCharacterId, CancellationToken cancellationToken = default);

    /// <summary>A fleet's still-open invites (Pending), for the roster's pending-invites section.</summary>
    Task<IReadOnlyList<FleetInvite>> ListPendingInvitesForFleetAsync(long fleetId, CancellationToken cancellationToken = default);

    /// <summary>True if the invitee already has a Pending invite for this fleet (avoid duplicate invites).</summary>
    Task<bool> HasPendingInviteAsync(long fleetId, int inviteeCharacterId, CancellationToken cancellationToken = default);

    // --- Request-to-join (6.2). The mirror of an invite: a character asks to join an invite-only
    // fleet, the owner accepts (→ roster) or denies. The request is the durable source of truth; its (fleet,
    // status) index backs the owner's pending list, the duplicate-request guard reuses it on (requester). ---

    Task<long> AddJoinRequestAsync(FleetJoinRequest request, CancellationToken cancellationToken = default);

    Task<FleetJoinRequest?> GetJoinRequestAsync(long requestId, CancellationToken cancellationToken = default);

    Task UpdateJoinRequestAsync(FleetJoinRequest request, CancellationToken cancellationToken = default);

    /// <summary>A fleet's still-open join requests (Pending), for the owner's roster pending-section.</summary>
    Task<IReadOnlyList<FleetJoinRequest>> ListPendingJoinRequestsForFleetAsync(long fleetId, CancellationToken cancellationToken = default);

    /// <summary>True if the requester already has a Pending join request for this fleet (avoid duplicates).</summary>
    Task<bool> HasPendingJoinRequestAsync(long fleetId, int requesterCharacterId, CancellationToken cancellationToken = default);

    Task<long> AddMemberAsync(FleetMember member, CancellationToken cancellationToken = default);

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

    /// <summary>Persists a member's wing/squad/role change (member-move).</summary>
    Task UpdateMemberAsync(FleetMember member, CancellationToken cancellationToken = default);

    /// <summary>Persists two members' position changes in a SINGLE transaction (stream G member-swap) — both saved
    /// together so a swap never leaves the roster half-exchanged.</summary>
    Task UpdateMembersAsync(FleetMember first, FleetMember second, CancellationToken cancellationToken = default);

    /// <summary>Removes a single roster member by its primary key. No-op if it is gone.</summary>
    Task RemoveMemberAsync(long memberId, CancellationToken cancellationToken = default);
}
