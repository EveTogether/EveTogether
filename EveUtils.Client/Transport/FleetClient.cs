using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Fleet;
using EveUtils.Grpc;
using EveUtils.Shared.Modules.Fleet.Entities;
using Grpc.Core;
using GrpcFleets = EveUtils.Grpc.Fleets;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Transport;

namespace EveUtils.Client.Transport;

/// <summary>
/// Calls the server's <c>Fleets</c> RPCs over the TOFU-pinned channel, using a stored server session as the
/// bearer. The server stamps the acting character from the validated token, never the request body. Fleets live per
/// server: the caller supplies the server address.
///
/// Multi-character: each method takes an optional <c>actingCharacterId</c> → the call authenticates as that
/// coupled character's session (default 0 = the most-recent session).
///
/// Token refresh: every RPC runs through <see cref="InvokeAsync{TReply}"/>, which on an
/// <c>Unauthenticated</c> reply (the 1-hour access token expired while the event-bus stream stayed open and never
/// reconnected) refreshes the session via <see cref="ServerSessionRefresher"/> and retries once — so actions don't
/// fail "Not authenticated" while the stream still shows connected.
/// </summary>
public sealed class FleetClient(
    GrpcChannelFactory channelFactory, IClientSessionStore sessionStore, ServerSessionRefresher refresher) : IFleetTransportClient, ISingletonService
{
    public async Task<(bool Ok, string Message, long FleetId)> CreateFleetAsync(
        string serverAddress, string name, string? description, FleetVisibility visibility,
        FleetOfflineBehavior offlineBehavior, DateTimeOffset? fromTime, DateTimeOffset? toTime,
        int actingCharacterId = 0, CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await InvokeAsync(serverAddress, actingCharacterId, (client, headers) =>
                client.CreateFleetAsync(new CreateFleetRequest
                {
                    Name = name,
                    Description = description ?? string.Empty,
                    Visibility = (int)visibility,
                    OfflineBehavior = (int)offlineBehavior,
                    FromTime = FormatTime(fromTime),
                    ToTime = FormatTime(toTime)
                }, headers, cancellationToken: cancellationToken), cancellationToken);
            return (reply.Accepted, reply.Message, reply.FleetId);
        }
        catch (RpcException ex)
        {
            return (false, ex.Status.Detail, 0);
        }
    }

    public Task<(bool Ok, string Message)> SetFleetCompositionAsync(
        string serverAddress, long fleetId, long? compositionId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
        {
            var request = new SetFleetCompositionRequest { FleetId = fleetId };
            if (compositionId is long composition)
                request.CompositionId = composition;
            return client.SetFleetCompositionAsync(request, headers, cancellationToken: cancellationToken);
        }, cancellationToken);

    public Task<(bool Ok, string Message)> CoupleFleetToEsiAsync(
        string serverAddress, long fleetId, long esiFleetId, int esiFleetBossId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.CoupleFleetToEsiAsync(
                new CoupleFleetToEsiRequest { FleetId = fleetId, EsiFleetId = esiFleetId, EsiFleetBossId = esiFleetBossId },
                headers, cancellationToken: cancellationToken), cancellationToken);

    public Task<(bool Ok, string Message)> UncoupleFleetFromEsiAsync(
        string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.UncoupleFleetFromEsiAsync(
                new UncoupleFleetFromEsiRequest { FleetId = fleetId }, headers, cancellationToken: cancellationToken), cancellationToken);

    public Task<(bool Ok, string Message)> SetFleetEsiAutomationAsync(
        string serverAddress, long fleetId, bool autoApplyStructure, bool autoInviteMembers,
        int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.SetFleetEsiAutomationAsync(
                new SetFleetEsiAutomationRequest
                {
                    FleetId = fleetId, AutoApplyStructure = autoApplyStructure, AutoInviteMembers = autoInviteMembers
                }, headers, cancellationToken: cancellationToken), cancellationToken);

    public async Task<(bool Ok, string Message)> EditFleetAsync(
        string serverAddress, long fleetId, string name, string? description, FleetVisibility visibility,
        FleetOfflineBehavior offlineBehavior, DateTimeOffset? fromTime, DateTimeOffset? toTime,
        int actingCharacterId = 0, CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await InvokeAsync(serverAddress, actingCharacterId, (client, headers) =>
                client.EditFleetAsync(new EditFleetRequest
                {
                    FleetId = fleetId,
                    Name = name,
                    Description = description ?? string.Empty,
                    Visibility = (int)visibility,
                    OfflineBehavior = (int)offlineBehavior,
                    FromTime = FormatTime(fromTime),
                    ToTime = FormatTime(toTime)
                }, headers, cancellationToken: cancellationToken), cancellationToken);
            return (reply.Accepted, reply.Message);
        }
        catch (RpcException ex)
        {
            return (false, ex.Status.Detail);
        }
    }

    public Task<(bool Ok, string Message)> DisbandFleetAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.DisbandFleetAsync(new DisbandFleetRequest { FleetId = fleetId }, headers, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Starts the fleet (6.5): flips it Forming → Active and notifies the roster. Creator-only.</summary>
    public Task<(bool Ok, string Message)> StartFleetAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.StartFleetAsync(new StartFleetRequest { FleetId = fleetId }, headers, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Concludes the fleet (2026-06-04): marks it finished (→ Concluded), kept for history. Creator-only.</summary>
    public Task<(bool Ok, string Message)> ConcludeFleetAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.ConcludeFleetAsync(new ConcludeFleetRequest { FleetId = fleetId }, headers, cancellationToken: cancellationToken), cancellationToken);

    public Task<(bool Ok, string Message)> JoinFleetAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.JoinFleetAsync(new JoinFleetRequest { FleetId = fleetId }, headers, cancellationToken: cancellationToken), cancellationToken);

    public Task<(bool Ok, string Message)> EnterFleetAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.EnterFleetAsync(new EnterFleetRequest { FleetId = fleetId }, headers, cancellationToken: cancellationToken), cancellationToken);

    public Task<(bool Ok, string Message)> LeaveFleetAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.LeaveFleetAsync(new LeaveFleetRequest { FleetId = fleetId }, headers, cancellationToken: cancellationToken), cancellationToken);

    public Task<(bool Ok, string Message)> RespondToInviteAsync(string serverAddress, long inviteId, bool accept, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.RespondToFleetInviteAsync(new RespondToFleetInviteRequest { InviteId = inviteId, Accept = accept }, headers, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Answers a queued message — accept/decline. A fleet-invite message joins/declines the
    /// fleet via the server-side responder; mail has no response.</summary>
    public Task<(bool Ok, string Message)> RespondToMessageAsync(string serverAddress, long messageId, bool accept, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.RespondToMessageAsync(new RespondToMessageRequest { MessageId = messageId, Accept = accept }, headers, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Creates an invite. <paramref name="wingId"/>/<paramref name="squadId"/> place the
    /// invitee on accept (0 = unspecified, role-dependent).</summary>
    public async Task<(bool Ok, string Message, long InviteId)> CreateInviteAsync(
        string serverAddress, long fleetId, int inviteeCharacterId, FleetRole role,
        long wingId = 0, long squadId = 0, string? message = null, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        await CreateStructureAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.CreateFleetInviteAsync(new CreateFleetInviteRequest
            {
                FleetId = fleetId,
                InviteeCharacterId = inviteeCharacterId,
                Role = (int)role,
                WingId = wingId,
                SquadId = squadId,
                Message = message ?? string.Empty
            }, headers, cancellationToken: cancellationToken), cancellationToken);

    // The two endpoints that BUILD the Fleets window lists surface a transport failure (QueryOrThrowAsync) instead of
    // mapping it to an empty list, so a transient error doesn't read as "you have no fleets" and blank the view.
    public async Task<FleetInfo?> GetFleetAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await InvokeAsync(serverAddress, actingCharacterId, (client, headers) =>
                client.GetFleetAsync(new GetFleetRequest { FleetId = fleetId }, headers, ListDeadline(), cancellationToken), cancellationToken);
            return reply.Found ? MapFleet(reply.Fleet) : null;
        }
        catch (RpcException)
        {
            return null;
        }
    }

    public Task<IReadOnlyList<FleetInfo>> ListMyFleetsAsync(string serverAddress, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        QueryOrThrowAsync(serverAddress, actingCharacterId,
            (client, headers) => client.ListMyFleetsAsync(new ListMyFleetsRequest(), headers, ListDeadline(), cancellationToken),
            reply => reply.Ok ? reply.Fleets.Select(MapFleet).ToList() : [], cancellationToken);

    public Task<IReadOnlyList<FleetInfo>> ListOpenFleetsAsync(string serverAddress, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        QueryOrThrowAsync(serverAddress, actingCharacterId,
            (client, headers) => client.ListOpenFleetsAsync(new ListOpenFleetsRequest(), headers, ListDeadline(), cancellationToken),
            reply => reply.Ok ? reply.Fleets.Select(MapFleet).ToList() : [], cancellationToken);

    public Task<IReadOnlyList<ConnectedCharacterInfo>> ListConnectedCharactersAsync(string serverAddress, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        QueryAsync(serverAddress, actingCharacterId,
            (client, headers) => client.ListConnectedCharactersAsync(new ListConnectedCharactersRequest(), headers, cancellationToken: cancellationToken),
            reply => reply.Ok ? reply.Characters.Select(c => new ConnectedCharacterInfo(c.CharacterId, c.CharacterName)).ToList() : [], cancellationToken);

    public Task<(bool Ok, string Message)> MoveMemberAsync(
        string serverAddress, long memberId, FleetRole role, long wingId, long squadId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.MoveMemberAsync(new MoveMemberRequest
            {
                MemberId = memberId,
                Role = (int)role,
                WingId = wingId,
                SquadId = squadId
            }, headers, cancellationToken: cancellationToken), cancellationToken);

    public Task<IReadOnlyList<FleetWingInfo>> ListWingsAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        QueryAsync(serverAddress, actingCharacterId,
            (client, headers) => client.ListWingsAsync(new ListWingsRequest { FleetId = fleetId }, headers, cancellationToken: cancellationToken),
            reply => reply.Ok ? reply.Wings.Select(w => new FleetWingInfo(w.Id, w.FleetId, w.Name)).ToList() : [], cancellationToken);

    public Task<IReadOnlyList<FleetSquadInfo>> ListSquadsAsync(string serverAddress, long wingId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        QueryAsync(serverAddress, actingCharacterId,
            (client, headers) => client.ListSquadsAsync(new ListSquadsRequest { WingId = wingId }, headers, cancellationToken: cancellationToken),
            reply => reply.Ok ? reply.Squads.Select(s => new FleetSquadInfo(s.Id, s.WingId, s.Name)).ToList() : [], cancellationToken);

    /// <summary>Adds a wing to a fleet. The server enforces the EVE 5-wing limit.</summary>
    public Task<(bool Ok, string Message, long Id)> CreateWingAsync(string serverAddress, long fleetId, string name, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        CreateStructureAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.CreateWingAsync(new CreateWingRequest { FleetId = fleetId, Name = name }, headers, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Adds a squad to a wing. The server enforces the EVE 5-squad-per-wing limit.</summary>
    public Task<(bool Ok, string Message, long Id)> CreateSquadAsync(string serverAddress, long wingId, string name, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        CreateStructureAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.CreateSquadAsync(new CreateSquadRequest { WingId = wingId, Name = name }, headers, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Renames a wing. Owner-gated; pairs with the in-game ESI rename for a coupled fleet.</summary>
    public Task<(bool Ok, string Message)> RenameWingAsync(string serverAddress, long wingId, string name, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.RenameWingAsync(new RenameWingRequest { WingId = wingId, Name = name }, headers, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Renames a squad. Owner-gated; pairs with the in-game ESI rename for a coupled fleet.</summary>
    public Task<(bool Ok, string Message)> RenameSquadAsync(string serverAddress, long squadId, string name, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.RenameSquadAsync(new RenameSquadRequest { SquadId = squadId, Name = name }, headers, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Deletes a wing. Owner-gated + idempotent; the UI only offers it for an empty wing.</summary>
    public Task<(bool Ok, string Message)> DeleteWingAsync(string serverAddress, long wingId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.DeleteWingAsync(new DeleteWingRequest { WingId = wingId }, headers, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Deletes a squad. Owner-gated + idempotent; the UI only offers it for an empty squad.</summary>
    public Task<(bool Ok, string Message)> DeleteSquadAsync(string serverAddress, long squadId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.DeleteSquadAsync(new DeleteSquadRequest { SquadId = squadId }, headers, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Adds an external (session-less) EVE character directly to a fleet on trust.</summary>
    public Task<(bool Ok, string Message)> AddExternalMemberAsync(
        string serverAddress, long fleetId, int characterId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.AddExternalMemberAsync(new AddExternalMemberRequest
            {
                FleetId = fleetId,
                CharacterId = characterId
            }, headers, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Hands a fleet's ownership to another member. Creator-only; the old owner stays a member.</summary>
    public Task<(bool Ok, string Message)> TransferFleetOwnershipAsync(
        string serverAddress, long fleetId, int newOwnerCharacterId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.TransferFleetOwnershipAsync(new TransferFleetOwnershipRequest
            {
                FleetId = fleetId,
                NewOwnerCharacterId = newOwnerCharacterId
            }, headers, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>Removes a member from a fleet. The owner removes anyone; the creator can't be removed until
    /// ownership is transferred away.</summary>
    public Task<(bool Ok, string Message)> RemoveFleetMemberAsync(
        string serverAddress, long memberId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.RemoveFleetMemberAsync(new RemoveFleetMemberRequest { MemberId = memberId }, headers, cancellationToken: cancellationToken), cancellationToken);

    public Task<(bool Ok, string Message)> SwapMembersAsync(
        string serverAddress, long firstMemberId, long secondMemberId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.SwapMembersAsync(new SwapMembersRequest
            {
                FirstMemberId = firstMemberId,
                SecondMemberId = secondMemberId
            }, headers, cancellationToken: cancellationToken), cancellationToken);

    public Task<(bool Ok, string Message)> AssignMemberFitAsync(
        string serverAddress, long memberId, FitReferenceInfo? fit, long? compositionEntryId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
        {
            var request = new AssignMemberFitRequest { MemberId = memberId };
            if (fit is not null)
                request.Fit = ToFitDto(fit);
            if (compositionEntryId is long entryId)
                request.CompositionEntryId = entryId;
            return client.AssignMemberFitAsync(request, headers, cancellationToken: cancellationToken);
        }, cancellationToken);

    /// <summary>Reports the acting pilot's own can-fly verdict for their assigned fit.
    /// Self-only: the server rejects a report for anyone but the acting character.</summary>
    public Task<(bool Ok, string Message)> ReportMemberFitVerdictAsync(
        string serverAddress, long memberId, FitSkillVerdict verdict, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.ReportMemberFitVerdictAsync(new ReportMemberFitVerdictRequest
            {
                MemberId = memberId,
                Verdict = (int)verdict
            }, headers, cancellationToken: cancellationToken), cancellationToken);

    public Task<(bool Ok, string Message)> ReportMemberInGameFleetAsync(
        string serverAddress, long memberId, bool inFleet, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.ReportMemberInGameFleetAsync(new ReportMemberInGameFleetRequest
            {
                MemberId = memberId,
                InFleet = inFleet
            }, headers, cancellationToken: cancellationToken), cancellationToken);

    public Task<IReadOnlyList<FleetMemberInfo>> ListMembersAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        QueryAsync(serverAddress, actingCharacterId,
            (client, headers) => client.ListMembersAsync(new ListMembersRequest { FleetId = fleetId }, headers, ListDeadline(), cancellationToken),
            reply => reply.Ok
                ? reply.Members.Select(m => new FleetMemberInfo(m.Id, m.CharacterId, m.WingId, m.SquadId, (FleetRole)m.Role, m.IsExternal,
                    m.AssignedFit is null ? null : MapFit(m.AssignedFit),
                    m.HasAssignedCompositionEntryId ? m.AssignedCompositionEntryId : null,
                    (FitSkillVerdict)m.FitSkillVerdict)).ToList()
                : [], cancellationToken);

    public Task<IReadOnlyList<FleetInviteInfo>> ListPendingInvitesAsync(string serverAddress, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        QueryAsync(serverAddress, actingCharacterId,
            (client, headers) => client.ListPendingInvitesAsync(new ListPendingInvitesRequest(), headers, cancellationToken: cancellationToken),
            reply => reply.Ok
                ? reply.Invites.Select(i => new FleetInviteInfo(i.Id, i.FleetId, i.InviterCharacterId, i.InviteeCharacterId, (FleetRole)i.Role, (FleetInviteStatus)i.Status)).ToList()
                : [], cancellationToken);

    /// <summary>A fleet's still-open invites for the roster's pending-invites section.</summary>
    public Task<IReadOnlyList<FleetInviteInfo>> ListPendingFleetInvitesAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        QueryAsync(serverAddress, actingCharacterId,
            (client, headers) => client.ListPendingFleetInvitesAsync(new ListPendingFleetInvitesRequest { FleetId = fleetId }, headers, cancellationToken: cancellationToken),
            reply => reply.Ok
                ? reply.Invites.Select(i => new FleetInviteInfo(i.Id, i.FleetId, i.InviterCharacterId, i.InviteeCharacterId, (FleetRole)i.Role, (FleetInviteStatus)i.Status)).ToList()
                : [], cancellationToken);

    /// <summary>Requests to join an invite-only fleet. The owner is messaged and answers via the inbox;
    /// a public fleet is joined directly with <see cref="JoinFleetAsync"/>.</summary>
    public Task<(bool Ok, string Message)> RequestToJoinAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.RequestToJoinAsync(new RequestToJoinRequest { FleetId = fleetId }, headers, cancellationToken: cancellationToken), cancellationToken);

    /// <summary>The owner accepts/declines a pending join-request directly by its id.</summary>
    public Task<(bool Ok, string Message)> RespondToJoinRequestAsync(string serverAddress, long requestId, bool accept, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.RespondToJoinRequestAsync(new RespondToJoinRequestRequest { RequestId = requestId, Accept = accept }, headers, cancellationToken: cancellationToken), cancellationToken);

    public Task<IReadOnlyList<FleetJoinRequestInfo>> ListPendingJoinRequestsAsync(string serverAddress, long fleetId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        QueryAsync(serverAddress, actingCharacterId,
            (client, headers) => client.ListPendingJoinRequestsAsync(new ListPendingJoinRequestsRequest { FleetId = fleetId }, headers, cancellationToken: cancellationToken),
            reply => reply.Ok ? reply.Requests.Select(r => new FleetJoinRequestInfo(r.Id, r.FleetId, r.RequesterCharacterId)).ToList() : [], cancellationToken);

    // --- Plumbing: every RPC runs through InvokeAsync (load session → call → refresh-on-401 → retry once). ---

    private const string NotPaired = "Not paired with this server — couple a character first.";

    /// <summary>A short client-side deadline for the list reads that build the Fleets window, so an unreachable/slow
    /// server fails fast (DeadlineExceeded → surfaced per server) instead of hanging on the TCP connect timeout — the
    /// concurrent multi-server reload then isn't held up by one bad server.</summary>
    private static DateTime ListDeadline() => DateTime.UtcNow.AddSeconds(5);

    /// <summary>Runs one unary RPC with the acting character's bearer; on an Unauthenticated reply refreshes the
    /// session once and retries. Throws RpcException (Unauthenticated/NotPaired) when there is no session,
    /// so the calling method's catch surfaces it like before.</summary>
    // --- Fleet Compositions. Library-level RPCs; the server stamps the acting character and gates
    // mutations owner-or-manage. Reads map proto → client Info DTOs (a transient failure → empty/null). ---

    public Task<(bool Ok, string Message, long Id)> CreateFleetCompositionAsync(
        string serverAddress, string name, string? description, bool isClientOnly, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        CreateStructureAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.CreateFleetCompositionAsync(new CreateFleetCompositionRequest
            {
                Name = name,
                Description = description ?? string.Empty,
                IsClientOnly = isClientOnly
            }, headers, cancellationToken: cancellationToken), cancellationToken);

    public Task<(bool Ok, string Message)> EditFleetCompositionAsync(
        string serverAddress, long compositionId, string name, string? description, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.EditFleetCompositionAsync(new EditFleetCompositionRequest
            {
                CompositionId = compositionId,
                Name = name,
                Description = description ?? string.Empty
            }, headers, cancellationToken: cancellationToken), cancellationToken);

    public Task<(bool Ok, string Message)> DeleteFleetCompositionAsync(string serverAddress, long compositionId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.DeleteFleetCompositionAsync(new DeleteFleetCompositionRequest { CompositionId = compositionId }, headers, cancellationToken: cancellationToken), cancellationToken);

    public Task<IReadOnlyList<FleetCompositionInfo>> ListMyFleetCompositionsAsync(string serverAddress, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        QueryAsync(serverAddress, actingCharacterId,
            (client, headers) => client.ListMyFleetCompositionsAsync(new ListMyFleetCompositionsRequest(), headers, ListDeadline(), cancellationToken),
            reply => reply.Ok ? reply.Compositions.Select(MapComposition).ToList() : [], cancellationToken);

    public Task<IReadOnlyList<FleetCompositionInfo>> ListAllFleetCompositionsAsync(string serverAddress, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        QueryAsync(serverAddress, actingCharacterId,
            (client, headers) => client.ListAllFleetCompositionsAsync(new ListAllFleetCompositionsRequest(), headers, ListDeadline(), cancellationToken),
            reply => reply.Ok ? reply.Compositions.Select(MapComposition).ToList() : [], cancellationToken);

    public async Task<FleetCompositionDetail?> GetFleetCompositionAsync(string serverAddress, long compositionId, int actingCharacterId = 0, CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await InvokeAsync(serverAddress, actingCharacterId, (client, headers) =>
                client.GetFleetCompositionAsync(new GetFleetCompositionRequest { CompositionId = compositionId }, headers, ListDeadline(), cancellationToken), cancellationToken);
            return reply.Found ? MapDetail(reply.Composition) : null;
        }
        catch (RpcException)
        {
            return null;
        }
    }

    public Task<(bool Ok, string Message, long Id)> AddFleetCompositionRoleAsync(
        string serverAddress, long compositionId, string roleName, int? groupMinCount, int actingCharacterId = 0, CancellationToken cancellationToken = default)
    {
        var request = new AddFleetCompositionRoleRequest { CompositionId = compositionId, RoleName = roleName };
        if (groupMinCount is int min)
            request.GroupMinCount = min;
        return CreateStructureAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.AddFleetCompositionRoleAsync(request, headers, cancellationToken: cancellationToken), cancellationToken);
    }

    public Task<(bool Ok, string Message)> EditFleetCompositionRoleAsync(
        string serverAddress, long roleId, string roleName, int? groupMinCount, int actingCharacterId = 0, CancellationToken cancellationToken = default)
    {
        var request = new EditFleetCompositionRoleRequest { RoleId = roleId, RoleName = roleName };
        if (groupMinCount is int min)
            request.GroupMinCount = min;
        return ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.EditFleetCompositionRoleAsync(request, headers, cancellationToken: cancellationToken), cancellationToken);
    }

    public Task<(bool Ok, string Message)> RemoveFleetCompositionRoleAsync(string serverAddress, long roleId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.RemoveFleetCompositionRoleAsync(new RemoveFleetCompositionRoleRequest { RoleId = roleId }, headers, cancellationToken: cancellationToken), cancellationToken);

    public Task<(bool Ok, string Message)> ReorderFleetCompositionRolesAsync(
        string serverAddress, long compositionId, IReadOnlyList<long> orderedRoleIds, int actingCharacterId = 0, CancellationToken cancellationToken = default)
    {
        var request = new ReorderFleetCompositionRolesRequest { CompositionId = compositionId };
        request.OrderedRoleIds.AddRange(orderedRoleIds);
        return ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.ReorderFleetCompositionRolesAsync(request, headers, cancellationToken: cancellationToken), cancellationToken);
    }

    public Task<(bool Ok, string Message, long Id)> AddFleetCompositionEntryAsync(
        string serverAddress, long roleId, FitReferenceInfo fit, int? entryMinCount, int actingCharacterId = 0, CancellationToken cancellationToken = default)
    {
        var request = new AddFleetCompositionEntryRequest { RoleId = roleId, Fit = ToFitDto(fit) };
        if (entryMinCount is int min)
            request.EntryMinCount = min;
        return CreateStructureAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.AddFleetCompositionEntryAsync(request, headers, cancellationToken: cancellationToken), cancellationToken);
    }

    public Task<(bool Ok, string Message)> EditFleetCompositionEntryAsync(
        string serverAddress, long entryId, int? entryMinCount, int actingCharacterId = 0, CancellationToken cancellationToken = default)
    {
        var request = new EditFleetCompositionEntryRequest { EntryId = entryId };
        if (entryMinCount is int min)
            request.EntryMinCount = min;
        return ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.EditFleetCompositionEntryAsync(request, headers, cancellationToken: cancellationToken), cancellationToken);
    }

    public Task<(bool Ok, string Message)> RemoveFleetCompositionEntryAsync(string serverAddress, long entryId, int actingCharacterId = 0, CancellationToken cancellationToken = default) =>
        ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.RemoveFleetCompositionEntryAsync(new RemoveFleetCompositionEntryRequest { EntryId = entryId }, headers, cancellationToken: cancellationToken), cancellationToken);

    public Task<(bool Ok, string Message)> ReorderFleetCompositionEntriesAsync(
        string serverAddress, long roleId, IReadOnlyList<long> orderedEntryIds, int actingCharacterId = 0, CancellationToken cancellationToken = default)
    {
        var request = new ReorderFleetCompositionEntriesRequest { RoleId = roleId };
        request.OrderedEntryIds.AddRange(orderedEntryIds);
        return ActionAsync(serverAddress, actingCharacterId, (client, headers) =>
            client.ReorderFleetCompositionEntriesAsync(request, headers, cancellationToken: cancellationToken), cancellationToken);
    }

    private static FleetCompositionInfo MapComposition(FleetCompositionDto dto) => new(
        dto.Id,
        dto.Name,
        string.IsNullOrEmpty(dto.Description) ? null : dto.Description,
        dto.OwnerCharacterId,
        ParseTime(dto.CreatedAt) ?? DateTimeOffset.MinValue,
        ParseTime(dto.UpdatedAt) ?? DateTimeOffset.MinValue,
        dto.CanEdit,
        dto.OwnerCharacterName,
        dto.FleetCount);

    private static FleetCompositionDetail MapDetail(FleetCompositionViewDto dto) => new(
        MapComposition(dto.Composition),
        dto.Roles.Select(MapRole).ToList());

    private static FleetCompositionRoleInfo MapRole(FleetCompositionRoleDto dto) => new(
        dto.Id,
        dto.CompositionId,
        dto.RoleName,
        dto.HasGroupMinCount ? dto.GroupMinCount : null,
        dto.SortOrder,
        dto.Entries.Select(MapEntry).ToList());

    private static FleetCompositionEntryInfo MapEntry(FleetCompositionEntryDto dto) => new(
        dto.Id,
        dto.RoleId,
        dto.HasEntryMinCount ? dto.EntryMinCount : null,
        dto.SortOrder,
        MapFit(dto.Fit));

    private static FitReferenceInfo MapFit(FitReferenceDto dto) => new(
        dto.ShipTypeId,
        dto.FitName,
        dto.RawJson,
        dto.ContentHash,
        dto.HasLocalFittingId ? dto.LocalFittingId : null,
        dto.HasServerSharedFitId ? dto.ServerSharedFitId : null);

    private static FitReferenceDto ToFitDto(FitReferenceInfo info)
    {
        var dto = new FitReferenceDto
        {
            ShipTypeId = info.ShipTypeId,
            FitName = info.FitName,
            RawJson = info.RawJson,
            ContentHash = info.ContentHash
        };
        if (info.LocalFittingId is int localFittingId)
            dto.LocalFittingId = localFittingId;
        if (info.ServerSharedFitId is int serverSharedFitId)
            dto.ServerSharedFitId = serverSharedFitId;
        return dto;
    }

    private async Task<TReply> InvokeAsync<TReply>(
        string serverAddress, int actingCharacterId,
        Func<GrpcFleets.FleetsClient, Metadata, AsyncUnaryCall<TReply>> rpc, CancellationToken cancellationToken)
    {
        var session = await LoadSessionAsync(serverAddress, actingCharacterId, cancellationToken);
        if (session is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, NotPaired));

        var channel = channelFactory.CreatePinned(serverAddress);
        var client = new GrpcFleets.FleetsClient(channel);
        try
        {
            return await rpc(client, BearerHeaders(session.AccessToken));
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unauthenticated)
        {
            var refreshed = await refresher.RefreshAsync(serverAddress, actingCharacterId, cancellationToken);
            if (refreshed is null)
                throw;
            return await rpc(client, BearerHeaders(refreshed.AccessToken)); // retry once with the rotated token
        }
    }

    private Task<ClientSessionTokens?> LoadSessionAsync(string serverAddress, int actingCharacterId, CancellationToken cancellationToken) =>
        actingCharacterId != 0
            ? sessionStore.LoadForCharacterAsync(serverAddress, actingCharacterId, cancellationToken)
            : sessionStore.LoadAsync(serverAddress, cancellationToken);

    private static Metadata BearerHeaders(string accessToken) => new() { { "authorization", $"Bearer {accessToken}" } };

    private async Task<(bool Ok, string Message)> ActionAsync(
        string serverAddress, int actingCharacterId,
        Func<GrpcFleets.FleetsClient, Metadata, AsyncUnaryCall<FleetActionReply>> call, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await InvokeAsync(serverAddress, actingCharacterId, call, cancellationToken);
            return (reply.Accepted, reply.Message);
        }
        catch (RpcException ex)
        {
            return (false, ex.Status.Detail);
        }
    }

    private async Task<(bool Ok, string Message, long Id)> CreateStructureAsync(
        string serverAddress, int actingCharacterId,
        Func<GrpcFleets.FleetsClient, Metadata, AsyncUnaryCall<CreateStructureReply>> call, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await InvokeAsync(serverAddress, actingCharacterId, call, cancellationToken);
            return (reply.Accepted, reply.Message, reply.Id);
        }
        catch (RpcException ex)
        {
            return (false, ex.Status.Detail, 0);
        }
    }

    private async Task<IReadOnlyList<TResult>> QueryAsync<TReply, TResult>(
        string serverAddress, int actingCharacterId,
        Func<GrpcFleets.FleetsClient, Metadata, AsyncUnaryCall<TReply>> call,
        Func<TReply, IReadOnlyList<TResult>> map, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await InvokeAsync(serverAddress, actingCharacterId, call, cancellationToken);
            return map(reply);
        }
        catch (RpcException)
        {
            return [];
        }
    }

    /// <summary>Like <see cref="QueryAsync"/> but rethrows a transport failure as a transport-agnostic
    /// <see cref="FleetTransportException"/> instead of mapping it to an empty list — for the list endpoints whose
    /// callers must tell "the server is unreachable" apart from "there are none" (the Fleets window keeps its
    /// current rows on the former rather than blanking them).</summary>
    private async Task<IReadOnlyList<TResult>> QueryOrThrowAsync<TReply, TResult>(
        string serverAddress, int actingCharacterId,
        Func<GrpcFleets.FleetsClient, Metadata, AsyncUnaryCall<TReply>> call,
        Func<TReply, IReadOnlyList<TResult>> map, CancellationToken cancellationToken)
    {
        try
        {
            var reply = await InvokeAsync(serverAddress, actingCharacterId, call, cancellationToken);
            return map(reply);
        }
        catch (RpcException ex)
        {
            throw new FleetTransportException(ex.Status.Detail);
        }
    }

    private static FleetInfo MapFleet(FleetDto dto) => new(
        dto.Id,
        dto.Name,
        string.IsNullOrEmpty(dto.Description) ? null : dto.Description,
        (FleetVisibility)dto.Visibility,
        (FleetState)dto.State,
        dto.CreatorCharacterId,
        ParseTime(dto.FromTime),
        ParseTime(dto.ToTime),
        ParseTime(dto.CreatedAt) ?? DateTimeOffset.MinValue,
        (FleetActivation)dto.Activation,
        dto.HasFleetCompositionId ? dto.FleetCompositionId : null,
        dto.HasEsiFleetId ? dto.EsiFleetId : null,
        dto.HasEsiFleetBossId ? dto.EsiFleetBossId : null,
        dto.EsiAutoApplyStructure,
        dto.EsiAutoInviteMembers);

    private static string FormatTime(DateTimeOffset? time) => time?.ToString("o", CultureInfo.InvariantCulture) ?? string.Empty;

    private static DateTimeOffset? ParseTime(string value) =>
        !string.IsNullOrEmpty(value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}
