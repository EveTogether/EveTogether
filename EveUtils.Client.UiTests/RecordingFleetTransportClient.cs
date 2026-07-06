using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Fleet;
using EveUtils.Client.Transport;
using EveUtils.Shared.Modules.Fleet.Entities;

namespace EveUtils.Client.UiTests;

/// <summary>
/// In-memory <see cref="IFleetTransportClient"/> double for headless UI tests: no gRPC, no server. Returns empty
/// listings so a <c>FleetsViewModel</c> can initialise/reload, lets a test seed a fleet's members
/// (<see cref="MembersByFleet"/>) and records every <see cref="RequestToJoinAsync"/> call
/// (<see cref="RequestToJoinCalls"/>) so the picked acting character can be asserted.
/// </summary>
public sealed class RecordingFleetTransportClient : IFleetTransportClient
{
    /// <summary>Members returned by <see cref="ListMembersAsync"/> per fleet id (default: none).</summary>
    public Dictionary<long, IReadOnlyList<FleetMemberInfo>> MembersByFleet { get; } = new();

    /// <summary>Every recorded request-to-join, in call order.</summary>
    public List<(string ServerAddress, long FleetId, int ActingCharacterId)> RequestToJoinCalls { get; } = new();

    /// <summary>Every recorded respond-to-message (inbox reply), in call order.</summary>
    public List<(string ServerAddress, long MessageId, bool Accept, int ActingCharacterId)> RespondToMessageCalls { get; } = new();

    /// <summary>Fleets returned by <see cref="ListMyFleetsAsync"/> per server (default: none) — for the multi-server
    /// aggregation/grouping tests.</summary>
    public Dictionary<string, IReadOnlyList<FleetInfo>> MyFleetsByServer { get; } = new();

    /// <summary>Fleets returned by <see cref="ListOpenFleetsAsync"/> per server (default: none).</summary>
    public Dictionary<string, IReadOnlyList<FleetInfo>> OpenFleetsByServer { get; } = new();

    /// <summary>Servers whose <see cref="ListMyFleetsAsync"/> throws a <see cref="FleetTransportException"/> — simulates
    /// an unreachable/down server so a test can assert it is isolated (the other servers still load) and its rows are
    /// kept rather than blanked.</summary>
    public HashSet<string> UnreachableServers { get; } = new();

    public Task<IReadOnlyList<FleetMemberInfo>> ListMembersAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        Task.FromResult(MembersByFleet.TryGetValue(fleetId, out var members) ? members : EmptyList<FleetMemberInfo>());

    public Task<(bool Ok, string Message)> RequestToJoinAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default)
    {
        RequestToJoinCalls.Add((serverAddress, fleetId, actingCharacterId));
        return Accepted();
    }

    public Task<IReadOnlyList<FleetInfo>> ListMyFleetsAsync(string serverAddress, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        UnreachableServers.Contains(serverAddress)
            ? throw new FleetTransportException("server unreachable")
            : Task.FromResult(MyFleetsByServer.TryGetValue(serverAddress, out var fleets) ? fleets : EmptyList<FleetInfo>());

    public Task<IReadOnlyList<FleetInfo>> ListOpenFleetsAsync(string serverAddress, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        Task.FromResult(OpenFleetsByServer.TryGetValue(serverAddress, out var fleets) ? fleets : EmptyList<FleetInfo>());

    // --- The rest of the surface is unused by these tests; provide harmless defaults. ---

    public Task<FleetInfo?> GetFleetAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        Task.FromResult<FleetInfo?>(null);

    public Task<(bool Ok, string Message, long FleetId)> CreateFleetAsync(
        string serverAddress, string name, string? description, FleetVisibility visibility,
        FleetOfflineBehavior offlineBehavior, DateTimeOffset? fromTime, DateTimeOffset? toTime,
        int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        Task.FromResult<(bool Ok, string Message, long FleetId)>((true, string.Empty, 0));

    public Task<(bool Ok, string Message)> EditFleetAsync(
        string serverAddress, long fleetId, string name, string? description, FleetVisibility visibility,
        FleetOfflineBehavior offlineBehavior, DateTimeOffset? fromTime, DateTimeOffset? toTime,
        int actingCharacterId = 0, CancellationToken cancellationToken = default) => Accepted();

    public Task<(bool Ok, string Message)> DisbandFleetAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default) => Accepted();

    /// <summary>The arguments of the last <see cref="AssignMemberFitAsync"/> call, or null if never assigned.</summary>
    public (long MemberId, FitReferenceInfo? Fit, long? EntryId)? LastAssignedFit { get; private set; }

    public Task<(bool Ok, string Message)> AssignMemberFitAsync(string serverAddress, long memberId, FitReferenceInfo? fit, long? compositionEntryId, int actingCharacterId = 0, CancellationToken cancellationToken = default)
    {
        LastAssignedFit = (memberId, fit, compositionEntryId);
        return Accepted();
    }

    public (long FleetId, long? CompositionId)? LastSetComposition { get; private set; }

    public Task<(bool Ok, string Message)> SetFleetCompositionAsync(string serverAddress, long fleetId, long? compositionId, int actingCharacterId = 0, CancellationToken cancellationToken = default)
    {
        LastSetComposition = (fleetId, compositionId);
        return Accepted();
    }

    public Task<(bool Ok, string Message)> CoupleFleetToEsiAsync(string serverAddress, long fleetId, long esiFleetId, int esiFleetBossId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        Accepted();

    /// <summary>Every recorded uncouple call, in call order, so a test can assert the poller cleared a dead link.</summary>
    public List<(string ServerAddress, long FleetId, int ActingCharacterId)> UncoupleCalls { get; } = new();

    public Task<(bool Ok, string Message)> UncoupleFleetFromEsiAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default)
    {
        UncoupleCalls.Add((serverAddress, fleetId, actingCharacterId));
        return Accepted();
    }

    /// <summary>Every recorded ESI-automation save, in call order, so a test can assert the toggle was persisted.</summary>
    public List<(string ServerAddress, long FleetId, bool AutoApplyStructure, bool AutoInviteMembers, int ActingCharacterId)> SetEsiAutomationCalls { get; } = new();

    public Task<(bool Ok, string Message)> SetFleetEsiAutomationAsync(string serverAddress, long fleetId, bool autoApplyStructure, bool autoInviteMembers, int actingCharacterId = 0, CancellationToken cancellationToken = default)
    {
        SetEsiAutomationCalls.Add((serverAddress, fleetId, autoApplyStructure, autoInviteMembers, actingCharacterId));
        return Accepted();
    }

    public Task<(bool Ok, string Message)> StartFleetAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default) => Accepted();

    public Task<(bool Ok, string Message)> ConcludeFleetAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default) => Accepted();

    /// <summary>Result returned by <see cref="JoinFleetAsync"/> — default accepted; a test can set a failure to drive the failed-join path.</summary>
    public (bool Ok, string Message) JoinResult { get; set; } = (true, string.Empty);

    public Task<(bool Ok, string Message)> JoinFleetAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default) => Task.FromResult(JoinResult);

    public Task<(bool Ok, string Message)> EnterFleetAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default) => Accepted();

    /// <summary>Every recorded leave, in call order, so a test can assert which character was pulled from the fleet.</summary>
    public List<(string ServerAddress, long FleetId, int ActingCharacterId)> LeaveCalls { get; } = new();

    public Task<(bool Ok, string Message)> LeaveFleetAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default)
    {
        LeaveCalls.Add((serverAddress, fleetId, actingCharacterId));
        return Accepted();
    }

    public Task<(bool Ok, string Message)> RespondToInviteAsync(string serverAddress, long inviteId, bool accept, int actingCharacterId = 0, CancellationToken cancellationToken = default) => Accepted();

    public Task<(bool Ok, string Message)> RespondToMessageAsync(string serverAddress, long messageId, bool accept, int actingCharacterId = 0, CancellationToken cancellationToken = default)
    {
        RespondToMessageCalls.Add((serverAddress, messageId, accept, actingCharacterId));
        return Accepted();
    }

    public Task<(bool Ok, string Message, long InviteId)> CreateInviteAsync(
        string serverAddress, long fleetId, int inviteeCharacterId, FleetRole role,
        long wingId = 0, long squadId = 0, string? message = null, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        Task.FromResult<(bool Ok, string Message, long InviteId)>((true, string.Empty, 0));

    public Task<IReadOnlyList<ConnectedCharacterInfo>> ListConnectedCharactersAsync(string serverAddress, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        Task.FromResult(EmptyList<ConnectedCharacterInfo>());

    public Task<(bool Ok, string Message)> MoveMemberAsync(
        string serverAddress, long memberId, FleetRole role, long wingId, long squadId, int actingCharacterId = 0, CancellationToken cancellationToken = default) => Accepted();

    /// <summary>The arguments of the last <see cref="SwapMembersAsync"/> call, or null if never swapped (stream G).</summary>
    public (long FirstMemberId, long SecondMemberId, int ActingCharacterId)? LastSwap { get; private set; }

    public Task<(bool Ok, string Message)> SwapMembersAsync(
        string serverAddress, long firstMemberId, long secondMemberId, int actingCharacterId = 0, CancellationToken cancellationToken = default)
    {
        LastSwap = (firstMemberId, secondMemberId, actingCharacterId);
        return Accepted();
    }

    public Task<IReadOnlyList<FleetWingInfo>> ListWingsAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        Task.FromResult(EmptyList<FleetWingInfo>());

    public Task<IReadOnlyList<FleetSquadInfo>> ListSquadsAsync(string serverAddress, long wingId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        Task.FromResult(EmptyList<FleetSquadInfo>());

    public Task<(bool Ok, string Message, long Id)> CreateWingAsync(string serverAddress, long fleetId, string name, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        Task.FromResult<(bool Ok, string Message, long Id)>((true, string.Empty, 0));

    public Task<(bool Ok, string Message, long Id)> CreateSquadAsync(string serverAddress, long wingId, string name, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        Task.FromResult<(bool Ok, string Message, long Id)>((true, string.Empty, 0));

    public Task<(bool Ok, string Message)> RenameWingAsync(string serverAddress, long wingId, string name, int actingCharacterId = 0, CancellationToken cancellationToken = default) => Accepted();

    public Task<(bool Ok, string Message)> RenameSquadAsync(string serverAddress, long squadId, string name, int actingCharacterId = 0, CancellationToken cancellationToken = default) => Accepted();

    public Task<(bool Ok, string Message)> DeleteWingAsync(string serverAddress, long wingId, int actingCharacterId = 0, CancellationToken cancellationToken = default) => Accepted();

    public Task<(bool Ok, string Message)> DeleteSquadAsync(string serverAddress, long squadId, int actingCharacterId = 0, CancellationToken cancellationToken = default) => Accepted();

    public Task<(bool Ok, string Message)> AddExternalMemberAsync(string serverAddress, long fleetId, int characterId, int actingCharacterId = 0, CancellationToken cancellationToken = default) => Accepted();

    public Task<(bool Ok, string Message)> TransferFleetOwnershipAsync(string serverAddress, long fleetId, int newOwnerCharacterId, int actingCharacterId = 0, CancellationToken cancellationToken = default) => Accepted();

    public Task<(bool Ok, string Message)> RemoveFleetMemberAsync(string serverAddress, long memberId, int actingCharacterId = 0, CancellationToken cancellationToken = default) => Accepted();

    /// <summary>The verdicts reported per member, so a test can assert the self-report fired.</summary>
    public List<(long MemberId, FitSkillVerdict Verdict, int ActingCharacterId)> ReportedVerdicts { get; } = [];

    public Task<(bool Ok, string Message)> ReportMemberFitVerdictAsync(string serverAddress, long memberId, FitSkillVerdict verdict, int actingCharacterId = 0, CancellationToken cancellationToken = default)
    {
        ReportedVerdicts.Add((memberId, verdict, actingCharacterId));
        return Accepted();
    }

    public List<(long MemberId, bool InFleet, int ActingCharacterId)> ReportedInGameFleet { get; } = [];

    public Task<(bool Ok, string Message)> ReportMemberInGameFleetAsync(string serverAddress, long memberId, bool inFleet, int actingCharacterId = 0, CancellationToken cancellationToken = default)
    {
        ReportedInGameFleet.Add((memberId, inFleet, actingCharacterId));
        return Accepted();
    }

    public Task<IReadOnlyList<FleetInviteInfo>> ListPendingInvitesAsync(string serverAddress, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        Task.FromResult(EmptyList<FleetInviteInfo>());

    public Task<IReadOnlyList<FleetInviteInfo>> ListPendingFleetInvitesAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        Task.FromResult(EmptyList<FleetInviteInfo>());

    public Task<(bool Ok, string Message)> RespondToJoinRequestAsync(string serverAddress, long requestId, bool accept, int actingCharacterId = 0, CancellationToken cancellationToken = default) => Accepted();

    public Task<IReadOnlyList<FleetJoinRequestInfo>> ListPendingJoinRequestsAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        Task.FromResult(EmptyList<FleetJoinRequestInfo>());

    // --- Fleet Compositions: harmless defaults; these tests don't exercise the composition library. ---

    public Task<(bool Ok, string Message, long Id)> CreateFleetCompositionAsync(string serverAddress, string name, string? description, bool isClientOnly, int actingCharacterId = 0, CancellationToken cancellationToken = default) => Created();

    public Task<(bool Ok, string Message)> EditFleetCompositionAsync(string serverAddress, long compositionId, string name, string? description, int actingCharacterId = 0, CancellationToken cancellationToken = default) => Accepted();

    public Task<(bool Ok, string Message)> DeleteFleetCompositionAsync(string serverAddress, long compositionId, int actingCharacterId = 0, CancellationToken cancellationToken = default) => Accepted();

    public Task<IReadOnlyList<FleetCompositionInfo>> ListMyFleetCompositionsAsync(string serverAddress, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        Task.FromResult(EmptyList<FleetCompositionInfo>());

    public Task<IReadOnlyList<FleetCompositionInfo>> ListAllFleetCompositionsAsync(string serverAddress, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        Task.FromResult(EmptyList<FleetCompositionInfo>());

    /// <summary>Compositions returned by <see cref="GetFleetCompositionAsync"/> per id (default: none → null).</summary>
    public Dictionary<long, FleetCompositionDetail> CompositionsById { get; } = new();

    public Task<FleetCompositionDetail?> GetFleetCompositionAsync(string serverAddress, long compositionId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        Task.FromResult(CompositionsById.TryGetValue(compositionId, out var composition) ? composition : null);

    public Task<(bool Ok, string Message, long Id)> AddFleetCompositionRoleAsync(string serverAddress, long compositionId, string roleName, int? groupMinCount, int actingCharacterId = 0, CancellationToken cancellationToken = default) => Created();

    public Task<(bool Ok, string Message)> EditFleetCompositionRoleAsync(string serverAddress, long roleId, string roleName, int? groupMinCount, int actingCharacterId = 0, CancellationToken cancellationToken = default) => Accepted();

    public Task<(bool Ok, string Message)> RemoveFleetCompositionRoleAsync(string serverAddress, long roleId, int actingCharacterId = 0, CancellationToken cancellationToken = default) => Accepted();

    public Task<(bool Ok, string Message)> ReorderFleetCompositionRolesAsync(string serverAddress, long compositionId, IReadOnlyList<long> orderedRoleIds, int actingCharacterId = 0, CancellationToken cancellationToken = default) => Accepted();

    public Task<(bool Ok, string Message, long Id)> AddFleetCompositionEntryAsync(string serverAddress, long roleId, FitReferenceInfo fit, int? entryMinCount, int actingCharacterId = 0, CancellationToken cancellationToken = default) => Created();

    public Task<(bool Ok, string Message)> EditFleetCompositionEntryAsync(string serverAddress, long entryId, int? entryMinCount, int actingCharacterId = 0, CancellationToken cancellationToken = default) => Accepted();

    public Task<(bool Ok, string Message)> RemoveFleetCompositionEntryAsync(string serverAddress, long entryId, int actingCharacterId = 0, CancellationToken cancellationToken = default) => Accepted();

    public Task<(bool Ok, string Message)> ReorderFleetCompositionEntriesAsync(string serverAddress, long roleId, IReadOnlyList<long> orderedEntryIds, int actingCharacterId = 0, CancellationToken cancellationToken = default) => Accepted();

    private static Task<(bool Ok, string Message, long Id)> Created() => Task.FromResult<(bool Ok, string Message, long Id)>((true, string.Empty, 0));

    private static Task<(bool Ok, string Message)> Accepted() => Task.FromResult<(bool Ok, string Message)>((true, string.Empty));

    private static IReadOnlyList<T> EmptyList<T>() => Array.Empty<T>();
}
