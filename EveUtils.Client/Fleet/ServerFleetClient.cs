using System.Collections.Generic;
using System.Threading.Tasks;
using EveUtils.Client.Transport;
using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Client.Fleet;

/// <summary>
/// <see cref="IFleetClient"/> over the gRPC transport (<see cref="IFleetTransportClient"/>) for a server-backed
/// fleet: binds the server address + the acting character (the fleet's owner for owner actions) once, then delegates
/// every roster call to the wrapped client. All multi-character routing is already in the transport client;
/// this just supplies the bound context so the window doesn't pass server/character on every call.
/// </summary>
public sealed class ServerFleetClient(IFleetTransportClient fleets, string serverAddress, int actingCharacterId) : IFleetClient
{
    public Task<FleetInfo?> GetFleetAsync(long fleetId) =>
        fleets.GetFleetAsync(serverAddress, fleetId, actingCharacterId);

    public Task<IReadOnlyList<FleetMemberInfo>> ListMembersAsync(long fleetId) =>
        fleets.ListMembersAsync(serverAddress, fleetId, actingCharacterId);

    public Task<IReadOnlyList<FleetInviteInfo>> ListPendingFleetInvitesAsync(long fleetId) =>
        fleets.ListPendingFleetInvitesAsync(serverAddress, fleetId, actingCharacterId);

    public Task<IReadOnlyList<FleetJoinRequestInfo>> ListPendingJoinRequestsAsync(long fleetId) =>
        fleets.ListPendingJoinRequestsAsync(serverAddress, fleetId, actingCharacterId);

    public Task<IReadOnlyList<FleetWingInfo>> ListWingsAsync(long fleetId) =>
        fleets.ListWingsAsync(serverAddress, fleetId, actingCharacterId);

    public Task<IReadOnlyList<FleetSquadInfo>> ListSquadsAsync(long wingId) =>
        fleets.ListSquadsAsync(serverAddress, wingId, actingCharacterId);

    public Task<IReadOnlyList<ConnectedCharacterInfo>> ListConnectedCharactersAsync() =>
        fleets.ListConnectedCharactersAsync(serverAddress, actingCharacterId);

    public Task<(bool Ok, string Message)> MoveMemberAsync(long memberId, FleetRole role, long wingId, long squadId) =>
        fleets.MoveMemberAsync(serverAddress, memberId, role, wingId, squadId, actingCharacterId);

    public Task<(bool Ok, string Message)> SwapMembersAsync(long firstMemberId, long secondMemberId) =>
        fleets.SwapMembersAsync(serverAddress, firstMemberId, secondMemberId, actingCharacterId);

    public Task<(bool Ok, string Message)> AssignMemberFitAsync(long memberId, FitReferenceInfo? fit, long? compositionEntryId) =>
        fleets.AssignMemberFitAsync(serverAddress, memberId, fit, compositionEntryId, actingCharacterId);

    public Task<(bool Ok, string Message)> ReportMemberFitVerdictAsync(long memberId, FitSkillVerdict verdict) =>
        fleets.ReportMemberFitVerdictAsync(serverAddress, memberId, verdict, actingCharacterId);

    public Task<(bool Ok, string Message)> ReportMemberInGameFleetAsync(long memberId, bool inFleet) =>
        fleets.ReportMemberInGameFleetAsync(serverAddress, memberId, inFleet, actingCharacterId);

    public Task<(bool Ok, string Message)> SetFleetCompositionAsync(long fleetId, long? compositionId) =>
        fleets.SetFleetCompositionAsync(serverAddress, fleetId, compositionId, actingCharacterId);

    public Task<(bool Ok, string Message)> CoupleFleetToEsiAsync(long fleetId, long esiFleetId, int esiFleetBossId) =>
        fleets.CoupleFleetToEsiAsync(serverAddress, fleetId, esiFleetId, esiFleetBossId, actingCharacterId);

    public Task<(bool Ok, string Message)> UncoupleFleetFromEsiAsync(long fleetId) =>
        fleets.UncoupleFleetFromEsiAsync(serverAddress, fleetId, actingCharacterId);

    public Task<(bool Ok, string Message)> SetFleetEsiAutomationAsync(long fleetId, bool autoApplyStructure, bool autoInviteMembers) =>
        fleets.SetFleetEsiAutomationAsync(serverAddress, fleetId, autoApplyStructure, autoInviteMembers, actingCharacterId);

    public Task<(bool Ok, string Message, long Id)> CreateWingAsync(long fleetId, string name) =>
        fleets.CreateWingAsync(serverAddress, fleetId, name, actingCharacterId);

    public Task<(bool Ok, string Message, long Id)> CreateSquadAsync(long wingId, string name) =>
        fleets.CreateSquadAsync(serverAddress, wingId, name, actingCharacterId);

    public Task<(bool Ok, string Message)> RenameWingAsync(long wingId, string name) =>
        fleets.RenameWingAsync(serverAddress, wingId, name, actingCharacterId);

    public Task<(bool Ok, string Message)> RenameSquadAsync(long squadId, string name) =>
        fleets.RenameSquadAsync(serverAddress, squadId, name, actingCharacterId);

    public Task<(bool Ok, string Message)> DeleteWingAsync(long wingId) =>
        fleets.DeleteWingAsync(serverAddress, wingId, actingCharacterId);

    public Task<(bool Ok, string Message)> DeleteSquadAsync(long squadId) =>
        fleets.DeleteSquadAsync(serverAddress, squadId, actingCharacterId);

    public Task<(bool Ok, string Message)> AddExternalMemberAsync(long fleetId, int characterId) =>
        fleets.AddExternalMemberAsync(serverAddress, fleetId, characterId, actingCharacterId);

    public Task<(bool Ok, string Message, long InviteId)> CreateInviteAsync(
        long fleetId, int inviteeCharacterId, FleetRole role, long wingId, long squadId, string? message) =>
        fleets.CreateInviteAsync(serverAddress, fleetId, inviteeCharacterId, role, wingId, squadId, message, actingCharacterId);

    public Task<(bool Ok, string Message)> TransferFleetOwnershipAsync(long fleetId, int newOwnerCharacterId) =>
        fleets.TransferFleetOwnershipAsync(serverAddress, fleetId, newOwnerCharacterId, actingCharacterId);

    public Task<(bool Ok, string Message)> RemoveFleetMemberAsync(long memberId) =>
        fleets.RemoveFleetMemberAsync(serverAddress, memberId, actingCharacterId);

    public Task<(bool Ok, string Message)> LeaveFleetAsync(long fleetId, int characterId) =>
        fleets.LeaveFleetAsync(serverAddress, fleetId, characterId);

    public Task<(bool Ok, string Message)> RespondToJoinRequestAsync(long requestId, bool accept) =>
        fleets.RespondToJoinRequestAsync(serverAddress, requestId, accept, actingCharacterId);

    public Task<(bool Ok, string Message)> StartFleetAsync(long fleetId) =>
        fleets.StartFleetAsync(serverAddress, fleetId, actingCharacterId);

    public Task<(bool Ok, string Message)> ConcludeFleetAsync(long fleetId) =>
        fleets.ConcludeFleetAsync(serverAddress, fleetId, actingCharacterId);
}
