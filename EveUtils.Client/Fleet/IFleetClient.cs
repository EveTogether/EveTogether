using System.Collections.Generic;
using System.Threading.Tasks;
using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Client.Fleet;

/// <summary>
/// The roster + metrics surface the per-fleet windows (<c>FleetRosterViewModel</c>/<c>FleetMetricsViewModel</c>) drive,
/// abstracted over the transport so the SAME windows serve both a server-backed fleet and a client-only fleet
/// . Implementations bind their context once (server address + acting character for
/// <see cref="ServerFleetClient"/>; the local repository + owner for <see cref="LocalFleetClient"/>), so the methods
/// take no server/character — the window stays transport-agnostic. Anti-splintering: there is no second roster UI,
/// only a second <see cref="IFleetClient"/> over the same Shared CQRS handlers.
/// </summary>
public interface IFleetClient
{
    /// <summary>Reads the current fleet header (incl. its coupled <c>FleetCompositionId</c>) from the backing store,
    /// so a viewer's roster can reflect a remote couple/unlink pushed as fleet.changed. Null if it's gone.</summary>
    Task<FleetInfo?> GetFleetAsync(long fleetId);

    Task<IReadOnlyList<FleetMemberInfo>> ListMembersAsync(long fleetId);
    Task<IReadOnlyList<FleetInviteInfo>> ListPendingFleetInvitesAsync(long fleetId);
    Task<IReadOnlyList<FleetJoinRequestInfo>> ListPendingJoinRequestsAsync(long fleetId);
    Task<IReadOnlyList<FleetWingInfo>> ListWingsAsync(long fleetId);
    Task<IReadOnlyList<FleetSquadInfo>> ListSquadsAsync(long wingId);

    /// <summary>The characters available to invite + to resolve names with (connected sessions for a server fleet,
    /// the owner's local characters for a client-only fleet).</summary>
    Task<IReadOnlyList<ConnectedCharacterInfo>> ListConnectedCharactersAsync();

    Task<(bool Ok, string Message)> MoveMemberAsync(long memberId, FleetRole role, long wingId, long squadId);

    /// <summary>Exchanges two members' exact roster positions (stream G): dragging a pilot onto an occupied commander
    /// slot swaps the two rather than being rejected by the move endpoint's slot-uniqueness rule.</summary>
    Task<(bool Ok, string Message)> SwapMembersAsync(long firstMemberId, long secondMemberId);

    /// <summary>Assigns (or clears, when <paramref name="fit"/> is null) the fit a roster member flies.</summary>
    Task<(bool Ok, string Message)> AssignMemberFitAsync(long memberId, FitReferenceInfo? fit, long? compositionEntryId);

    /// <summary>Reports this client's can-fly verdict for a member's assigned fit. Self-only:
    /// trained skills never leave the pilot's client, so only their own client may report — and only the verdict
    /// travels, never the skills.</summary>
    Task<(bool Ok, string Message)> ReportMemberFitVerdictAsync(long memberId, FitSkillVerdict verdict);

    /// <summary>Reports the acting pilot's own in-game fleet presence. Self-only: the server
    /// rejects a report for anyone but the acting character.</summary>
    Task<(bool Ok, string Message)> ReportMemberInGameFleetAsync(long memberId, bool inFleet);

    /// <summary>Couples a composition to this fleet, or unlinks it when <paramref name="compositionId"/> is null
    /// . Only allowed while the fleet is forming.</summary>
    Task<(bool Ok, string Message)> SetFleetCompositionAsync(long fleetId, long? compositionId);

    /// <summary>Links this fleet to a detected live in-game fleet. Owner-only.</summary>
    Task<(bool Ok, string Message)> CoupleFleetToEsiAsync(long fleetId, long esiFleetId, int esiFleetBossId);

    /// <summary>Clears this fleet's link to an in-game fleet: the in-game fleet is gone, so the stored
    /// <c>EsiFleetId</c> is dropped. Owner-only; storage-role only (no ESI call).</summary>
    Task<(bool Ok, string Message)> UncoupleFleetFromEsiAsync(long fleetId);

    /// <summary>Persists this fleet's Auto Apply / Auto Invite toggles. Owner-only; storage-role only (no ESI
    /// call) — the flags only drive the boss client's own ESI pushes.</summary>
    Task<(bool Ok, string Message)> SetFleetEsiAutomationAsync(long fleetId, bool autoApplyStructure, bool autoInviteMembers);

    Task<(bool Ok, string Message, long Id)> CreateWingAsync(long fleetId, string name);
    Task<(bool Ok, string Message, long Id)> CreateSquadAsync(long wingId, string name);
    Task<(bool Ok, string Message)> RenameWingAsync(long wingId, string name);
    Task<(bool Ok, string Message)> RenameSquadAsync(long squadId, string name);
    Task<(bool Ok, string Message)> DeleteWingAsync(long wingId);
    Task<(bool Ok, string Message)> DeleteSquadAsync(long squadId);
    Task<(bool Ok, string Message)> AddExternalMemberAsync(long fleetId, int characterId);
    Task<(bool Ok, string Message, long InviteId)> CreateInviteAsync(
        long fleetId, int inviteeCharacterId, FleetRole role, long wingId, long squadId, string? message);
    Task<(bool Ok, string Message)> TransferFleetOwnershipAsync(long fleetId, int newOwnerCharacterId);
    Task<(bool Ok, string Message)> RemoveFleetMemberAsync(long memberId);

    /// <summary>One of my characters leaves this fleet: self-removal from the roster window, distinct from the
    /// owner-only <see cref="RemoveFleetMemberAsync"/>. <paramref name="characterId"/> is the leaving character — I may
    /// have several of my own characters in the fleet (multi-box), each leavable on its own. The owner's own character
    /// leaves by disbanding/transferring instead.</summary>
    Task<(bool Ok, string Message)> LeaveFleetAsync(long fleetId, int characterId);
    Task<(bool Ok, string Message)> RespondToJoinRequestAsync(long requestId, bool accept);
    Task<(bool Ok, string Message)> StartFleetAsync(long fleetId);
    Task<(bool Ok, string Message)> ConcludeFleetAsync(long fleetId);
}
