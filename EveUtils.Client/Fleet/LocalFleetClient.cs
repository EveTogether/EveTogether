using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;

namespace EveUtils.Client.Fleet;

/// <summary>
/// <see cref="IFleetClient"/> for a client-only fleet: serves the same roster window from the local SQLite
/// via <see cref="ClientFleetService"/> (Shared CQRS handlers) + the client <see cref="IFleetRepository"/>, with no
/// server or gRPC. Anti-splintering: no duplicate roster UI and no duplicate model — only this thin adapter.
///
/// Client-only specifics: there are no remote invites/join-requests (those lists are empty); "connected characters"
/// are the owner's local characters; "inviting to a position" means adding one of the owner's local toons directly
/// onto that position (no round-trip — the owner vouches for their own character).
/// </summary>
public sealed class LocalFleetClient(
    ClientFleetService local, IFleetRepository repository, ICharacterRegistry characters, int ownerCharacterId) : IFleetClient
{
    public async Task<FleetInfo?> GetFleetAsync(long fleetId)
    {
        var fleet = await repository.GetAsync(fleetId);
        return fleet is null
            ? null
            : new FleetInfo(
                fleet.Id, fleet.Name, fleet.Description, fleet.Visibility, fleet.State, fleet.CreatorCharacterId,
                fleet.FromTime, fleet.ToTime, fleet.CreatedAt, fleet.Activation, fleet.FleetCompositionId,
                fleet.EsiFleetId, fleet.EsiFleetBossId, fleet.EsiAutoApplyStructure, fleet.EsiAutoInviteMembers);
    }

    public async Task<IReadOnlyList<FleetMemberInfo>> ListMembersAsync(long fleetId) =>
        (await repository.ListMembersAsync(fleetId))
            .Select(m => new FleetMemberInfo(m.Id, m.CharacterId, m.WingId, m.SquadId, m.Role, m.IsExternal, _FromFit(m.AssignedFit), m.AssignedCompositionEntryId, m.FitSkillVerdict))
            .ToList();

    private static FitReferenceInfo? _FromFit(FitReference? fit) => fit is null ? null : new FitReferenceInfo(
        fit.ShipTypeId, fit.FitName, fit.RawJson, fit.ContentHash, fit.LocalFittingId, fit.ServerSharedFitId);

    // A client-only fleet has no remote invites or join-requests.
    public Task<IReadOnlyList<FleetInviteInfo>> ListPendingFleetInvitesAsync(long fleetId) =>
        Task.FromResult<IReadOnlyList<FleetInviteInfo>>([]);

    public Task<IReadOnlyList<FleetJoinRequestInfo>> ListPendingJoinRequestsAsync(long fleetId) =>
        Task.FromResult<IReadOnlyList<FleetJoinRequestInfo>>([]);

    public async Task<IReadOnlyList<FleetWingInfo>> ListWingsAsync(long fleetId) =>
        (await repository.ListWingsAsync(fleetId))
            .Select(w => new FleetWingInfo(w.Id, w.FleetId, w.Name))
            .ToList();

    public async Task<IReadOnlyList<FleetSquadInfo>> ListSquadsAsync(long wingId) =>
        (await repository.ListSquadsAsync(wingId))
            .Select(s => new FleetSquadInfo(s.Id, s.WingId, s.Name))
            .ToList();

    public async Task<IReadOnlyList<ConnectedCharacterInfo>> ListConnectedCharactersAsync() =>
        (await characters.GetAllAsync())
            .Where(c => c.EsiCharacterId is not null)
            .Select(c => new ConnectedCharacterInfo(c.EsiCharacterId!.Value, c.Name))
            .ToList();

    public async Task<(bool Ok, string Message)> MoveMemberAsync(long memberId, FleetRole role, long wingId, long squadId) =>
        Map(await local.MoveMemberAsync(memberId, role, wingId, squadId, ownerCharacterId));

    public async Task<(bool Ok, string Message)> SwapMembersAsync(long firstMemberId, long secondMemberId) =>
        Map(await local.SwapMembersAsync(firstMemberId, secondMemberId, ownerCharacterId));

    public async Task<(bool Ok, string Message)> AssignMemberFitAsync(long memberId, FitReferenceInfo? fit, long? compositionEntryId) =>
        Map(await local.AssignMemberFitAsync(memberId, _ToFit(fit), compositionEntryId, ownerCharacterId));

    /// <summary>For a client-only fleet this client IS the pilot's client for all its local toons, so the report
    /// acts as the member's own character to satisfy the handler's self-only rule.</summary>
    public async Task<(bool Ok, string Message)> ReportMemberFitVerdictAsync(long memberId, FitSkillVerdict verdict)
    {
        var member = await repository.GetMemberAsync(memberId);
        if (member is null)
            return (false, "Fleet member not found.");
        return Map(await local.ReportMemberFitVerdictAsync(memberId, verdict, member.CharacterId));
    }

    public async Task<(bool Ok, string Message)> ReportMemberInGameFleetAsync(long memberId, bool inFleet)
    {
        var member = await repository.GetMemberAsync(memberId);
        if (member is null)
            return (false, "Fleet member not found.");
        return Map(await local.ReportMemberInGameFleetAsync(memberId, inFleet, member.CharacterId));
    }

    public async Task<(bool Ok, string Message)> SetFleetCompositionAsync(long fleetId, long? compositionId) =>
        Map(await local.SetFleetCompositionAsync(fleetId, compositionId, ownerCharacterId));

    public async Task<(bool Ok, string Message)> CoupleFleetToEsiAsync(long fleetId, long esiFleetId, int esiFleetBossId) =>
        Map(await local.CoupleFleetToEsiAsync(fleetId, esiFleetId, esiFleetBossId, ownerCharacterId));

    public async Task<(bool Ok, string Message)> UncoupleFleetFromEsiAsync(long fleetId) =>
        Map(await local.UncoupleFleetFromEsiAsync(fleetId, ownerCharacterId));

    public async Task<(bool Ok, string Message)> SetFleetEsiAutomationAsync(long fleetId, bool autoApplyStructure, bool autoInviteMembers) =>
        Map(await local.SetFleetEsiAutomationAsync(fleetId, autoApplyStructure, autoInviteMembers, ownerCharacterId));

    private static FitReference? _ToFit(FitReferenceInfo? info) => info is null ? null : new FitReference
    {
        ShipTypeId = info.ShipTypeId,
        FitName = info.FitName,
        RawJson = info.RawJson,
        ContentHash = info.ContentHash,
        LocalFittingId = info.LocalFittingId,
        ServerSharedFitId = info.ServerSharedFitId
    };

    public async Task<(bool Ok, string Message, long Id)> CreateWingAsync(long fleetId, string name) =>
        MapId(await local.AddWingAsync(fleetId, name, ownerCharacterId));

    public async Task<(bool Ok, string Message, long Id)> CreateSquadAsync(long wingId, string name) =>
        MapId(await local.AddSquadAsync(wingId, name, ownerCharacterId));

    public async Task<(bool Ok, string Message)> RenameWingAsync(long wingId, string name) =>
        Map(await local.RenameWingAsync(wingId, name, ownerCharacterId));

    public async Task<(bool Ok, string Message)> RenameSquadAsync(long squadId, string name) =>
        Map(await local.RenameSquadAsync(squadId, name, ownerCharacterId));

    public async Task<(bool Ok, string Message)> DeleteWingAsync(long wingId) =>
        Map(await local.DeleteWingAsync(wingId, ownerCharacterId));

    public async Task<(bool Ok, string Message)> DeleteSquadAsync(long squadId) =>
        Map(await local.DeleteSquadAsync(squadId, ownerCharacterId));

    public async Task<(bool Ok, string Message)> AddExternalMemberAsync(long fleetId, int characterId) =>
        Map(await local.AddExternalAsync(fleetId, characterId, ownerCharacterId));

    /// <summary>"Invite to position" for a client-only fleet = add one of the owner's local toons and place it on the
    /// position (no remote invite). Wing/squad arrive as 0 = unspecified (the roster's NoneIfUnset).</summary>
    public async Task<(bool Ok, string Message, long InviteId)> CreateInviteAsync(
        long fleetId, int inviteeCharacterId, FleetRole role, long wingId, long squadId, string? message)
    {
        var added = await local.AddLocalCharacterAsync(fleetId, inviteeCharacterId, ownerCharacterId);
        if (!added.IsSuccess)
            return (false, FirstMessage(added), 0);

        // Place onto the chosen position when one was given; otherwise leave the auto-placement from AddLocalCharacter.
        if (wingId > 0 || squadId > 0 || role != FleetRole.SquadMember)
        {
            var moved = await local.MoveMemberAsync(
                added.Value, role, wingId <= 0 ? -1 : wingId, squadId <= 0 ? -1 : squadId, ownerCharacterId);
            if (!moved.IsSuccess)
                return (false, FirstMessage(moved), 0);
        }

        return (true, "Local character added.", added.Value);
    }

    public async Task<(bool Ok, string Message)> TransferFleetOwnershipAsync(long fleetId, int newOwnerCharacterId) =>
        Map(await local.TransferOwnershipAsync(fleetId, newOwnerCharacterId, ownerCharacterId));

    public async Task<(bool Ok, string Message)> RemoveFleetMemberAsync(long memberId) =>
        Map(await local.RemoveMemberAsync(memberId, ownerCharacterId));

    // A client-only fleet's roster is always opened by its owner, so the self-leave action is never shown for it; the
    // owner removes characters from the roster instead. Present only to satisfy the seam.
    public Task<(bool Ok, string Message)> LeaveFleetAsync(long fleetId, int characterId) =>
        Task.FromResult((false, "A client-only fleet is managed by its owner; remove the character from the roster instead."));

    public Task<(bool Ok, string Message)> RespondToJoinRequestAsync(long requestId, bool accept) =>
        Task.FromResult((false, "A client-only fleet has no join requests."));

    public async Task<(bool Ok, string Message)> StartFleetAsync(long fleetId) =>
        Map(await local.StartFleetAsync(fleetId, ownerCharacterId));

    public async Task<(bool Ok, string Message)> ConcludeFleetAsync(long fleetId) =>
        Map(await local.ConcludeFleetAsync(fleetId, ownerCharacterId));

    private static (bool Ok, string Message) Map(Result result) => (result.IsSuccess, FirstMessage(result));

    private static (bool Ok, string Message, long Id) MapId(Result<long> result) =>
        (result.IsSuccess, FirstMessage(result), result.IsSuccess ? result.Value : 0);

    private static string FirstMessage(Result result) => result.Messages.FirstOrDefault()?.Text ?? "";
    private static string FirstMessage(Result<long> result) => result.Messages.FirstOrDefault()?.Text ?? "";
}
