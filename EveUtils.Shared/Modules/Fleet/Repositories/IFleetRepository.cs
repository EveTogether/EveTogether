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
/// Persistence for fleets: <see cref="IFleetReader"/> plus the writes. Only the fleet command handlers take this one
/// (ET-383, held by the write-repository guard test), so every fleet write publishes the fleet signal; everything else
/// reads through <see cref="IFleetReader"/>.
/// </summary>
public interface IFleetRepository : IFleetReader
{
    Task<long> AddAsync(FleetEntity fleet, CancellationToken cancellationToken = default);

    /// <summary>Persists changes to an existing fleet (edit/disband). The entity is updated wholesale.</summary>
    Task UpdateAsync(FleetEntity fleet, CancellationToken cancellationToken = default);

    /// <summary>Bumps <see cref="FleetEntity.LastActivityAt"/> — the cleanup inactivity signal.</summary>
    Task TouchActivityAsync(long fleetId, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>Bumps one member's <see cref="FleetMember.LastSeenAt"/> — the per-member presence signal (ET-70).
    /// A character who is not on this fleet's roster is silently ignored.</summary>
    Task TouchMemberSeenAsync(long fleetId, int characterId, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>Hard-deletes a fleet; its wings, squads, members and invites cascade with it (FK).</summary>
    Task DeleteAsync(long fleetId, CancellationToken cancellationToken = default);

    // --- Wing/squad structure. The handlers resolve a wing/squad back to its owning
    // fleet (GetWing → FleetId → GetAsync; GetSquad → WingId → GetWing → FleetId) for the creator check. ---

    Task<long> AddWingAsync(FleetWing wing, CancellationToken cancellationToken = default);

    Task UpdateWingAsync(FleetWing wing, CancellationToken cancellationToken = default);

    /// <summary>Removes a wing; its squads cascade with it (FK, see FleetSquadConfiguration).</summary>
    Task DeleteWingAsync(long wingId, CancellationToken cancellationToken = default);

    Task<long> AddSquadAsync(FleetSquad squad, CancellationToken cancellationToken = default);

    Task UpdateSquadAsync(FleetSquad squad, CancellationToken cancellationToken = default);

    Task DeleteSquadAsync(long squadId, CancellationToken cancellationToken = default);

    // --- Invites + roster. The invite is the durable source of truth; accepting it
    // adds the invitee to the roster. The (FleetId, CharacterId) roster index is unique (one membership
    // per fleet); the (InviteeCharacterId, Status) invite index backs the on-attach pending sync. ---

    Task<long> AddInviteAsync(FleetInvite invite, CancellationToken cancellationToken = default);

    Task UpdateInviteAsync(FleetInvite invite, CancellationToken cancellationToken = default);

    // --- Request-to-join (6.2). The mirror of an invite: a character asks to join an invite-only
    // fleet, the owner accepts (→ roster) or denies. The request is the durable source of truth; its (fleet,
    // status) index backs the owner's pending list, the duplicate-request guard reuses it on (requester). ---

    Task<long> AddJoinRequestAsync(FleetJoinRequest request, CancellationToken cancellationToken = default);

    Task UpdateJoinRequestAsync(FleetJoinRequest request, CancellationToken cancellationToken = default);

    Task<long> AddMemberAsync(FleetMember member, CancellationToken cancellationToken = default);

    /// <summary>Persists a member's wing/squad/role change (member-move).</summary>
    Task UpdateMemberAsync(FleetMember member, CancellationToken cancellationToken = default);

    /// <summary>Persists two members' position changes in a SINGLE transaction (stream G member-swap) — both saved
    /// together so a swap never leaves the roster half-exchanged.</summary>
    Task UpdateMembersAsync(FleetMember first, FleetMember second, CancellationToken cancellationToken = default);

    /// <summary>Removes a single roster member by its primary key. No-op if it is gone.</summary>
    Task RemoveMemberAsync(long memberId, CancellationToken cancellationToken = default);
}
