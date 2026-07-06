using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Fleet;
using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Client.Transport;

/// <summary>
/// The client's transport surface for the server's <c>Fleets</c> RPCs, abstracted over the concrete gRPC
/// <see cref="FleetClient"/>. Fleets live per server, so every call takes the server address; multi-character
/// calls take an optional acting character id (0 = the most-recent session). Extracted so view-models and
/// the <see cref="ServerFleetClient"/> roster wrapper depend on the seam rather than the concrete client, which
/// also lets headless UI tests substitute a fake without a running server.
/// </summary>
public interface IFleetTransportClient
{
    Task<(bool Ok, string Message, long FleetId)> CreateFleetAsync(
        string serverAddress, string name, string? description, FleetVisibility visibility,
        FleetOfflineBehavior offlineBehavior, DateTimeOffset? fromTime, DateTimeOffset? toTime,
        int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> EditFleetAsync(
        string serverAddress, long fleetId, string name, string? description, FleetVisibility visibility,
        FleetOfflineBehavior offlineBehavior, DateTimeOffset? fromTime, DateTimeOffset? toTime,
        int actingCharacterId = 0, CancellationToken cancellationToken = default);

    /// <summary>Couples a composition to a fleet, or unlinks it when <paramref name="compositionId"/> is null.</summary>
    Task<(bool Ok, string Message)> SetFleetCompositionAsync(
        string serverAddress, long fleetId, long? compositionId, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    /// <summary>Links a fleet to a detected live in-game fleet.</summary>
    Task<(bool Ok, string Message)> CoupleFleetToEsiAsync(
        string serverAddress, long fleetId, long esiFleetId, int esiFleetBossId, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    /// <summary>Clears a fleet's stored in-game link: the in-game fleet is gone, so the server drops the
    /// <c>EsiFleetId</c> it relays. Storage-role only — no ESI call.</summary>
    Task<(bool Ok, string Message)> UncoupleFleetFromEsiAsync(
        string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    /// <summary>Persists the Auto Apply / Auto Invite toggles on a server fleet. Storage-role only — the
    /// server stores and relays the flags, it never acts on ESI.</summary>
    Task<(bool Ok, string Message)> SetFleetEsiAutomationAsync(
        string serverAddress, long fleetId, bool autoApplyStructure, bool autoInviteMembers,
        int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> DisbandFleetAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> StartFleetAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> ConcludeFleetAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> JoinFleetAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> EnterFleetAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> LeaveFleetAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> RespondToInviteAsync(string serverAddress, long inviteId, bool accept, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> RespondToMessageAsync(string serverAddress, long messageId, bool accept, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message, long InviteId)> CreateInviteAsync(
        string serverAddress, long fleetId, int inviteeCharacterId, FleetRole role,
        long wingId = 0, long squadId = 0, string? message = null, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    /// <summary>Reads one fleet header (incl. its coupled composition id) — null if not found/unreachable. Lets a
    /// viewer's open roster pick up a remote couple/unlink pushed as fleet.changed.</summary>
    Task<FleetInfo?> GetFleetAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FleetInfo>> ListMyFleetsAsync(string serverAddress, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FleetInfo>> ListOpenFleetsAsync(string serverAddress, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConnectedCharacterInfo>> ListConnectedCharactersAsync(string serverAddress, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> MoveMemberAsync(
        string serverAddress, long memberId, FleetRole role, long wingId, long squadId, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    /// <summary>Exchanges two members' exact roster positions (stream G drag-and-drop onto an occupied commander slot).</summary>
    Task<(bool Ok, string Message)> SwapMembersAsync(
        string serverAddress, long firstMemberId, long secondMemberId, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    /// <summary>Assigns (or clears, when <paramref name="fit"/> is null) the fit a roster member flies.</summary>
    Task<(bool Ok, string Message)> AssignMemberFitAsync(
        string serverAddress, long memberId, FitReferenceInfo? fit, long? compositionEntryId, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FleetWingInfo>> ListWingsAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FleetSquadInfo>> ListSquadsAsync(string serverAddress, long wingId, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message, long Id)> CreateWingAsync(string serverAddress, long fleetId, string name, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message, long Id)> CreateSquadAsync(string serverAddress, long wingId, string name, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> RenameWingAsync(string serverAddress, long wingId, string name, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> RenameSquadAsync(string serverAddress, long squadId, string name, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> DeleteWingAsync(string serverAddress, long wingId, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> DeleteSquadAsync(string serverAddress, long squadId, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> AddExternalMemberAsync(string serverAddress, long fleetId, int characterId, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> TransferFleetOwnershipAsync(string serverAddress, long fleetId, int newOwnerCharacterId, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> RemoveFleetMemberAsync(string serverAddress, long memberId, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    /// <summary>Reports the acting pilot's own can-fly verdict for their assigned fit.</summary>
    Task<(bool Ok, string Message)> ReportMemberFitVerdictAsync(string serverAddress, long memberId, FitSkillVerdict verdict, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    /// <summary>Reports the acting pilot's own in-game fleet presence.</summary>
    Task<(bool Ok, string Message)> ReportMemberInGameFleetAsync(string serverAddress, long memberId, bool inFleet, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FleetMemberInfo>> ListMembersAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FleetInviteInfo>> ListPendingInvitesAsync(string serverAddress, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FleetInviteInfo>> ListPendingFleetInvitesAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> RequestToJoinAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> RespondToJoinRequestAsync(string serverAddress, long requestId, bool accept, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FleetJoinRequestInfo>> ListPendingJoinRequestsAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    // --- Fleet Compositions. Library-level (not per-fleet); the server stamps the acting character
    // from the session and gates mutations owner-or-fleet-composition.manage. ---

    Task<(bool Ok, string Message, long Id)> CreateFleetCompositionAsync(
        string serverAddress, string name, string? description, bool isClientOnly, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> EditFleetCompositionAsync(
        string serverAddress, long compositionId, string name, string? description, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> DeleteFleetCompositionAsync(string serverAddress, long compositionId, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FleetCompositionInfo>> ListMyFleetCompositionsAsync(string serverAddress, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    /// <summary>Every composition on the server, with each carrying the acting character's owner-or-manage edit-state
    /// and the resolved owner name.</summary>
    Task<IReadOnlyList<FleetCompositionInfo>> ListAllFleetCompositionsAsync(string serverAddress, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<FleetCompositionDetail?> GetFleetCompositionAsync(string serverAddress, long compositionId, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message, long Id)> AddFleetCompositionRoleAsync(
        string serverAddress, long compositionId, string roleName, int? groupMinCount, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> EditFleetCompositionRoleAsync(
        string serverAddress, long roleId, string roleName, int? groupMinCount, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> RemoveFleetCompositionRoleAsync(string serverAddress, long roleId, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> ReorderFleetCompositionRolesAsync(
        string serverAddress, long compositionId, IReadOnlyList<long> orderedRoleIds, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message, long Id)> AddFleetCompositionEntryAsync(
        string serverAddress, long roleId, FitReferenceInfo fit, int? entryMinCount, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> EditFleetCompositionEntryAsync(
        string serverAddress, long entryId, int? entryMinCount, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> RemoveFleetCompositionEntryAsync(string serverAddress, long entryId, int actingCharacterId = 0, CancellationToken cancellationToken = default);

    Task<(bool Ok, string Message)> ReorderFleetCompositionEntriesAsync(
        string serverAddress, long roleId, IReadOnlyList<long> orderedEntryIds, int actingCharacterId = 0, CancellationToken cancellationToken = default);
}
