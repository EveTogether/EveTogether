using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Fleet;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Client.UiTests;

/// <summary>Public-ESI name lookup double: returns a name for the seeded ids, "unknown" otherwise.</summary>
internal sealed class FakeExternalLookup : Dictionary<int, string>, IExternalCharacterLookup
{
    public Task<ExternalCharacterInfo> LookupAsync(int characterId, CancellationToken cancellationToken = default) =>
        Task.FromResult(TryGetValue(characterId, out var name)
            ? new ExternalCharacterInfo(characterId, name, null, null, true)
            : ExternalCharacterInfo.Unknown(characterId));
}

/// <summary>Minimal <see cref="IFleetClient"/> double for the per-fleet windows: returns the seeded roster/connected
/// set, harmless defaults elsewhere. Shared by the roster + metrics name-resolution tests.</summary>
internal sealed class FakeFleetClient : IFleetClient
{
    public IReadOnlyList<FleetMemberInfo> Members { get; set; } = [];
    public IReadOnlyList<FleetInviteInfo> Invites { get; set; } = [];
    public IReadOnlyList<ConnectedCharacterInfo> Connected { get; set; } = [];
    public FleetInfo? Fleet { get; set; }

    public Task<FleetInfo?> GetFleetAsync(long fleetId) => Task.FromResult(Fleet);

    public Task<IReadOnlyList<FleetMemberInfo>> ListMembersAsync(long fleetId) => Task.FromResult(Members);
    public Task<IReadOnlyList<FleetInviteInfo>> ListPendingFleetInvitesAsync(long fleetId) => Task.FromResult(Invites);
    public Task<IReadOnlyList<FleetJoinRequestInfo>> ListPendingJoinRequestsAsync(long fleetId) => Empty<FleetJoinRequestInfo>();
    /// <summary>The fleet's structure. Seed it when a test needs members actually PLACED in the tree — an unplaced
    /// member lives in the roster's left list only, so a structure-less fleet quietly halves what a roster test
    /// renders.</summary>
    public IReadOnlyList<FleetWingInfo> Wings { get; set; } = [];
    public IReadOnlyList<FleetSquadInfo> Squads { get; set; } = [];

    public Task<IReadOnlyList<FleetWingInfo>> ListWingsAsync(long fleetId) => Task.FromResult(Wings);

    public Task<IReadOnlyList<FleetSquadInfo>> ListSquadsAsync(long wingId) =>
        Task.FromResult<IReadOnlyList<FleetSquadInfo>>([.. Squads.Where(s => s.WingId == wingId)]);
    public Task<IReadOnlyList<ConnectedCharacterInfo>> ListConnectedCharactersAsync() => Task.FromResult(Connected);

    public Task<(bool Ok, string Message)> MoveMemberAsync(long memberId, FleetRole role, long wingId, long squadId) => Ok();
    public Task<(bool Ok, string Message)> SwapMembersAsync(long firstMemberId, long secondMemberId) => Ok();
    public Task<(bool Ok, string Message)> AssignMemberFitAsync(long memberId, FitReferenceInfo? fit, long? compositionEntryId) => Ok();

    /// <summary>The verdicts the window reported, so a test can assert the self-report fired.</summary>
    public List<(long MemberId, FitSkillVerdict Verdict)> ReportedVerdicts { get; } = [];

    public Task<(bool Ok, string Message)> ReportMemberFitVerdictAsync(long memberId, FitSkillVerdict verdict)
    {
        ReportedVerdicts.Add((memberId, verdict));
        return Ok();
    }

    /// <summary>The in-game presence reports the window fired, so a test can assert it.</summary>
    public List<(long MemberId, bool InFleet)> ReportedInGameFleet { get; } = [];

    public Task<(bool Ok, string Message)> ReportMemberInGameFleetAsync(long memberId, bool inFleet)
    {
        ReportedInGameFleet.Add((memberId, inFleet));
        return Ok();
    }

    public Task<(bool Ok, string Message)> SetFleetCompositionAsync(long fleetId, long? compositionId) => Ok();
    public Task<(bool Ok, string Message)> CoupleFleetToEsiAsync(long fleetId, long esiFleetId, int esiFleetBossId) => Ok();
    public Task<(bool Ok, string Message)> UncoupleFleetFromEsiAsync(long fleetId) => Ok();
    public Task<(bool Ok, string Message)> SetFleetEsiAutomationAsync(long fleetId, bool autoApplyStructure, bool autoInviteMembers) => Ok();
    public Task<(bool Ok, string Message, long Id)> CreateWingAsync(long fleetId, string name) => OkId();
    public Task<(bool Ok, string Message, long Id)> CreateSquadAsync(long wingId, string name) => OkId();
    public Task<(bool Ok, string Message)> RenameWingAsync(long wingId, string name) => Ok();
    public Task<(bool Ok, string Message)> RenameSquadAsync(long squadId, string name) => Ok();
    public Task<(bool Ok, string Message)> DeleteWingAsync(long wingId) => Ok();
    public Task<(bool Ok, string Message)> DeleteSquadAsync(long squadId) => Ok();
    public Task<(bool Ok, string Message)> AddExternalMemberAsync(long fleetId, int characterId) => Ok();
    public Task<(bool Ok, string Message, long InviteId)> CreateInviteAsync(long fleetId, int inviteeCharacterId, FleetRole role, long wingId, long squadId, string? message) => OkId();
    public Task<(bool Ok, string Message)> TransferFleetOwnershipAsync(long fleetId, int newOwnerCharacterId) => Ok();

    /// <summary>The member ids a screen asked to remove, so a test can assert the removal reached the transport.</summary>
    public List<long> RemovedMemberIds { get; } = [];

    /// <summary>Set to make the removal fail, standing in for the server refusing it (a non-owner, the creator).</summary>
    public string? RemoveFailure { get; set; }

    public Task<(bool Ok, string Message)> RemoveFleetMemberAsync(long memberId)
    {
        if (RemoveFailure is { } failure)
            return Task.FromResult((false, failure));
        RemovedMemberIds.Add(memberId);

        // A removal that leaves the member on the roster is not a removal: the next ListMembersAsync has to agree with
        // it, or a screen that re-reads the roster looks correct here while it re-adds the pilot in production.
        Members = [.. Members.Where(m => m.Id != memberId)];
        return Ok();
    }

    /// <summary>The (fleet, character) pairs the window asked to leave, so a test can assert which of my
    /// characters left.</summary>
    public List<(long FleetId, int CharacterId)> LeaveFleetCalls { get; } = [];
    public Task<(bool Ok, string Message)> LeaveFleetAsync(long fleetId, int characterId)
    {
        LeaveFleetCalls.Add((fleetId, characterId));
        return Ok();
    }

    public Task<(bool Ok, string Message)> RespondToJoinRequestAsync(long requestId, bool accept) => Ok();

    /// <summary>What this store reports as the start collision (ET-168) — the members counting for another started
    /// fleet. Empty unless a test stages one.</summary>
    public List<FleetMemberElsewhereInfo> MembersActiveElsewhere { get; } = [];

    public Task<IReadOnlyList<FleetMemberElsewhereInfo>> ListMembersActiveElsewhereAsync(long fleetId) =>
        Task.FromResult<IReadOnlyList<FleetMemberElsewhereInfo>>(MembersActiveElsewhere);

    /// <summary>Every ask, as (fleet, member) — 0 for everyone who is elsewhere (ET-168).</summary>
    public List<(long FleetId, int OnlyCharacterId)> SwitchRequestCalls { get; } = [];

    /// <summary>What the ask answers with; a test can set a refusal to drive the failed-ask path.</summary>
    public (bool Ok, string Message, int Asked) SwitchRequestResult { get; set; } = (true, string.Empty, 0);

    public Task<(bool Ok, string Message, int Asked)> RequestFleetSwitchAsync(long fleetId, int onlyCharacterId = 0)
    {
        SwitchRequestCalls.Add((fleetId, onlyCharacterId));
        return Task.FromResult(SwitchRequestResult);
    }

    public Task<(bool Ok, string Message)> StartFleetAsync(long fleetId) => Ok();

    /// <summary>The fleet ids a screen asked to stop / to conclude, so a test can assert which of the two exits the
    /// stop dialog's choice actually took — the whole point of ET-166 is that they are not the same call.</summary>
    public List<long> StoppedFleetIds { get; } = [];

    public List<long> ConcludedFleetIds { get; } = [];

    public Task<(bool Ok, string Message)> StopFleetAsync(long fleetId)
    {
        StoppedFleetIds.Add(fleetId);
        return Ok();
    }

    public Task<(bool Ok, string Message)> ConcludeFleetAsync(long fleetId)
    {
        ConcludedFleetIds.Add(fleetId);
        return Ok();
    }

    private static Task<IReadOnlyList<T>> Empty<T>() => Task.FromResult<IReadOnlyList<T>>([]);
    private static Task<(bool Ok, string Message)> Ok() => Task.FromResult((true, string.Empty));
    private static Task<(bool Ok, string Message, long Id)> OkId() => Task.FromResult((true, string.Empty, 0L));
}
