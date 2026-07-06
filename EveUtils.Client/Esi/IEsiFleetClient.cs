using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.Modules.Esi.Http;

namespace EveUtils.Client.Esi;

/// <summary>
/// Typed client for the in-game fleet over the metered ESI pivot. Reads plus the boss-only writes:
/// MOTD/free-move, member move/kick and the wing/squad structure; invite arrives separately. Every
/// <c>/fleets/{id}/</c> call runs the <c>fleet_boss_id</c> precheck first so a non-boss never hits the endpoint
/// (its 404 would burn the error-limit budget → ban risk).
/// </summary>
public interface IEsiFleetClient
{
    /// <summary>Reads a character's own fleet status (<c>GET /characters/{id}/fleet/</c>): fleet id, role, wing/squad,
    /// boss. Works for any member with the read scope; the per-member endpoint that drives detect + the precheck.</summary>
    Task<EsiResult<EsiCharacterFleet>> GetCharacterFleetAsync(int characterId, CancellationToken cancellationToken = default);

    /// <summary>Reads the live roster (<c>GET /fleets/{id}/members/</c>) — boss-only. Fails the precheck without a
    /// network call when <paramref name="actingCharacterId"/> is not the fleet boss.</summary>
    Task<EsiResult<EsiFleetMember[]>> GetMembersAsync(long fleetId, int actingCharacterId, CancellationToken cancellationToken = default);

    /// <summary>Reads the wing/squad structure (<c>GET /fleets/{id}/wings/</c>) — boss-only, same precheck.</summary>
    Task<EsiResult<EsiFleetWing[]>> GetWingsAsync(long fleetId, int actingCharacterId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the fleet MOTD and/or free-move (<c>PUT /fleets/{id}/</c>) — boss-only + write scope: the smallest
    /// write, with zero roster risk, that proves the whole write chain. Null fields are left unchanged. Runs the same
    /// <c>fleet_boss_id</c> precheck as the reads so a non-boss write never burns the error-limit budget.
    /// </summary>
    Task<EsiResult> SetFleetSettingsAsync(long fleetId, int actingCharacterId, string? motd, bool? isFreeMove,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a member to a wing/squad with a role (<c>PUT /fleets/{id}/members/{member_id}/</c>) — boss-only + write
    /// scope. ESI keys the member by character id. <paramref name="role"/> is the literal ESI role string; pass
    /// <paramref name="wingId"/>/<paramref name="squadId"/> only for the positions that role requires (null = omitted).
    /// Same <c>fleet_boss_id</c> precheck as the reads.
    /// </summary>
    Task<EsiResult> MoveMemberAsync(long fleetId, int memberCharacterId, string role, long? wingId, long? squadId,
        int actingCharacterId, CancellationToken cancellationToken = default);

    /// <summary>Kicks a member (<c>DELETE /fleets/{id}/members/{member_id}/</c>) — boss-only + write scope,
    /// keyed by character id, same precheck.</summary>
    Task<EsiResult> KickMemberAsync(long fleetId, int memberCharacterId, int actingCharacterId,
        CancellationToken cancellationToken = default);

    /// <summary>Creates an empty wing (<c>POST /fleets/{id}/wings/</c>) and returns its new in-game id — boss-only +
    /// write scope. ESI names it "New Wing"; pair with <see cref="RenameWingAsync"/>.</summary>
    Task<EsiResult<long>> CreateWingAsync(long fleetId, int actingCharacterId, CancellationToken cancellationToken = default);

    /// <summary>Renames a wing (<c>PUT /fleets/{id}/wings/{wing_id}/</c>) — boss-only + write scope.</summary>
    Task<EsiResult> RenameWingAsync(long fleetId, long wingId, string name, int actingCharacterId,
        CancellationToken cancellationToken = default);

    /// <summary>Creates an empty squad under a wing (<c>POST /fleets/{id}/wings/{wing_id}/squads/</c>) and returns its
    /// new in-game id — boss-only + write scope. ESI names it "New Squad"; pair with <see cref="RenameSquadAsync"/>.
    /// Note the route asymmetry: a squad is created nested under its wing but renamed on the flat <c>/squads/{id}/</c>.</summary>
    Task<EsiResult<long>> CreateSquadAsync(long fleetId, long wingId, int actingCharacterId, CancellationToken cancellationToken = default);

    /// <summary>Renames a squad (<c>PUT /fleets/{id}/squads/{squad_id}/</c>) — boss-only + write scope.</summary>
    Task<EsiResult> RenameSquadAsync(long fleetId, long squadId, string name, int actingCharacterId,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a wing (<c>DELETE /fleets/{id}/wings/{wing_id}/</c>) — boss-only + write scope. The
    /// in-game wing must be empty (no squads/members) for ESI to accept it; the caller only deletes emptied units.</summary>
    Task<EsiResult> DeleteWingAsync(long fleetId, long wingId, int actingCharacterId, CancellationToken cancellationToken = default);

    /// <summary>Deletes a squad (<c>DELETE /fleets/{id}/squads/{squad_id}/</c>) — boss-only + write scope,
    /// on the flat <c>/squads/{id}/</c> route (same asymmetry as rename). The squad must be empty of members.</summary>
    Task<EsiResult> DeleteSquadAsync(long fleetId, long squadId, int actingCharacterId, CancellationToken cancellationToken = default);

    /// <summary>Invites a character to the live fleet at a wing/squad/role (<c>POST /fleets/{id}/members/</c>) — boss-only
    /// + write scope. The invite is an in-game invitation the pilot must accept; a CSPA charge on the target makes
    /// ESI reject it. Pass <paramref name="wingId"/>/<paramref name="squadId"/> only for the positions the role requires.
    /// Same <c>fleet_boss_id</c> precheck.</summary>
    Task<EsiResult> InviteMemberAsync(long fleetId, int characterId, string role, long? wingId, long? squadId,
        int actingCharacterId, CancellationToken cancellationToken = default);
}
