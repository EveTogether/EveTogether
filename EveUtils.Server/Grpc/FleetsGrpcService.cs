using System.Globalization;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Grpc;
using EveUtils.Server.Auth;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Composition.Commands;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Queries;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Messaging.Commands;
using EveUtils.Shared.Modules.ServerAuth.Repositories;
using Grpc.Core;
using GrpcFleets = EveUtils.Grpc.Fleets;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Server.Grpc;

/// <summary>
/// Fleet lifecycle over gRPC. Auth-gated by the server session token; the acting
/// character is taken from the validated session (never the request body) and stamped onto each command,
/// so the per-fleet creator check in the handlers can't be spoofed. The app-permission gate (fleet.*) runs
/// inside the dispatcher (<c>RequiresPermissionAttribute</c>); replies carry {accepted,message}.
/// </summary>
public sealed class FleetsGrpcService(
    ServerSessionService sessions,
    IDispatcher dispatcher,
    ConnectedClients connectedClients,
    IFleetRepository fleets,
    IFleetCompositionRepository compositions,
    FleetCompositionAuthorizer compositionAuthorizer,
    IServerAuthRepository serverAuth) : GrpcFleets.FleetsBase
{
    private const string NotAuthenticated = "Not authenticated — pair with the server first.";

    public override async Task<CreateFleetReply> CreateFleet(CreateFleetRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new CreateFleetReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new CreateFleetCommand(
            request.Name,
            NullIfEmpty(request.Description),
            (FleetVisibility)request.Visibility,
            ParseTime(request.FromTime),
            ParseTime(request.ToTime),
            (FleetOfflineBehavior)request.OfflineBehavior,
            character.Value), context.CancellationToken);

        return result.IsSuccess
            ? new CreateFleetReply { Accepted = true, Message = "Created.", FleetId = result.Value }
            : new CreateFleetReply { Accepted = false, Message = FirstMessage(result) };
    }

    public override async Task<FleetActionReply> EditFleet(EditFleetRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new EditFleetCommand(
            request.FleetId,
            request.Name,
            NullIfEmpty(request.Description),
            (FleetVisibility)request.Visibility,
            ParseTime(request.FromTime),
            ParseTime(request.ToTime),
            (FleetOfflineBehavior)request.OfflineBehavior,
            character.Value), context.CancellationToken);

        return ToActionReply(result, "Saved.");
    }

    public override async Task<FleetActionReply> SetFleetComposition(SetFleetCompositionRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new SetFleetCompositionCommand(
            request.FleetId, request.HasCompositionId ? request.CompositionId : null, character.Value), context.CancellationToken);
        if (result.IsSuccess)
            await BroadcastFleetChangedAsync(request.FleetId, FleetChangeKind.CompositionChanged, context.CancellationToken);
        return ToActionReply(result, "Composition coupled.");
    }

    public override async Task<FleetActionReply> CoupleFleetToEsi(CoupleFleetToEsiRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new CoupleFleetToEsiCommand(
            request.FleetId, request.EsiFleetId, request.EsiFleetBossId, character.Value), context.CancellationToken);
        if (result.IsSuccess)
            await BroadcastFleetChangedAsync(request.FleetId, FleetChangeKind.RosterChanged, context.CancellationToken);
        return ToActionReply(result, "Coupled to the in-game fleet.");
    }

    public override async Task<FleetActionReply> UncoupleFleetFromEsi(UncoupleFleetFromEsiRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new UncoupleFleetFromEsiCommand(request.FleetId, character.Value), context.CancellationToken);
        if (result.IsSuccess)
            await BroadcastFleetChangedAsync(request.FleetId, FleetChangeKind.RosterChanged, context.CancellationToken);
        return ToActionReply(result, "Uncoupled from the in-game fleet.");
    }

    public override async Task<FleetActionReply> SetFleetEsiAutomation(SetFleetEsiAutomationRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(
            new SetFleetEsiAutomationCommand(request.FleetId, character.Value, request.AutoApplyStructure, request.AutoInviteMembers),
            context.CancellationToken);
        if (result.IsSuccess)
            await BroadcastFleetChangedAsync(request.FleetId, FleetChangeKind.RosterChanged, context.CancellationToken);
        return ToActionReply(result, "ESI automation settings saved.");
    }

    public override async Task<FleetActionReply> DisbandFleet(DisbandFleetRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new DisbandFleetCommand(request.FleetId, character.Value), context.CancellationToken);
        return ToActionReply(result, "Disbanded.");
    }

    public override async Task<FleetActionReply> StartFleet(StartFleetRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new StartFleetCommand(request.FleetId, character.Value), context.CancellationToken);
        if (result.IsSuccess)
            await BroadcastFleetChangedAsync(request.FleetId, FleetChangeKind.Activated, context.CancellationToken);
        return ToActionReply(result, "Started.");
    }

    public override async Task<FleetActionReply> ConcludeFleet(ConcludeFleetRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new ConcludeFleetCommand(request.FleetId, character.Value), context.CancellationToken);
        if (result.IsSuccess)
            await BroadcastFleetChangedAsync(request.FleetId, FleetChangeKind.Concluded, context.CancellationToken);
        return ToActionReply(result, "Concluded.");
    }

    public override async Task<GetFleetReply> GetFleet(GetFleetRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new GetFleetReply { Found = false, Message = NotAuthenticated };

        var fleet = await dispatcher.Query(new GetFleetQuery(request.FleetId), context.CancellationToken);
        return fleet is null
            ? new GetFleetReply { Found = false, Message = "Fleet not found." }
            : new GetFleetReply { Found = true, Fleet = ToDto(fleet) };
    }

    public override async Task<ListFleetsReply> ListMyFleets(ListMyFleetsRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new ListFleetsReply { Ok = false, Message = NotAuthenticated };

        // "My fleets" = fleets I own OR am a member of — a member who accepted an invite must see the fleet
        // to enter it; the per-row edit/disband stays gated on the creator check client-side.
        var fleets = await dispatcher.Query(new ListMyFleetsQuery(character.Value), context.CancellationToken);
        var reply = new ListFleetsReply { Ok = true };
        foreach (var fleet in fleets)
            reply.Fleets.Add(ToDto(fleet));
        return reply;
    }

    // --- Wing/squad structure. Acting character from the session; creator-only in the handlers. ---

    public override async Task<CreateStructureReply> CreateWing(CreateWingRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new CreateStructureReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new CreateWingCommand(request.FleetId, request.Name, character.Value), context.CancellationToken);
        return ToCreateReply(result);
    }

    public override async Task<FleetActionReply> RenameWing(RenameWingRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new RenameWingCommand(request.WingId, request.Name, character.Value), context.CancellationToken);
        return ToActionReply(result, "Saved.");
    }

    public override async Task<FleetActionReply> DeleteWing(DeleteWingRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new DeleteWingCommand(request.WingId, character.Value), context.CancellationToken);
        return ToActionReply(result, "Deleted.");
    }

    public override async Task<CreateStructureReply> CreateSquad(CreateSquadRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new CreateStructureReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new CreateSquadCommand(request.WingId, request.Name, character.Value), context.CancellationToken);
        return ToCreateReply(result);
    }

    public override async Task<FleetActionReply> RenameSquad(RenameSquadRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new RenameSquadCommand(request.SquadId, request.Name, character.Value), context.CancellationToken);
        return ToActionReply(result, "Saved.");
    }

    public override async Task<FleetActionReply> DeleteSquad(DeleteSquadRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new DeleteSquadCommand(request.SquadId, character.Value), context.CancellationToken);
        return ToActionReply(result, "Deleted.");
    }

    public override async Task<ListWingsReply> ListWings(ListWingsRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new ListWingsReply { Ok = false, Message = NotAuthenticated };

        var wings = await dispatcher.Query(new ListWingsQuery(request.FleetId), context.CancellationToken);
        var reply = new ListWingsReply { Ok = true };
        foreach (var wing in wings)
            reply.Wings.Add(new WingDto { Id = wing.Id, FleetId = wing.FleetId, Name = wing.Name });
        return reply;
    }

    public override async Task<ListSquadsReply> ListSquads(ListSquadsRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new ListSquadsReply { Ok = false, Message = NotAuthenticated };

        var squads = await dispatcher.Query(new ListSquadsQuery(request.WingId), context.CancellationToken);
        var reply = new ListSquadsReply { Ok = true };
        foreach (var squad in squads)
            reply.Squads.Add(new SquadDto { Id = squad.Id, WingId = squad.WingId, Name = squad.Name });
        return reply;
    }

    // --- Roster. Acting character from the session; creator-only in the handler. ---

    public override async Task<FleetActionReply> MoveMember(MoveMemberRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new MoveMemberCommand(
            request.MemberId, (FleetRole)request.Role, request.WingId, request.SquadId, character.Value), context.CancellationToken);
        // A move/unassign is a roster mutation — refresh viewers live just like a swap/fit change (the request
        // carries only the member, so resolve its fleet). Without it other open windows kept the old position.
        if (result.IsSuccess && await fleets.GetMemberAsync(request.MemberId, context.CancellationToken) is { } member)
            await BroadcastFleetChangedAsync(member.FleetId, FleetChangeKind.RosterChanged, context.CancellationToken);
        return ToActionReply(result, "Moved.");
    }

    public override async Task<FleetActionReply> SwapMembers(SwapMembersRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new SwapMembersCommand(
            request.FirstMemberId, request.SecondMemberId, character.Value), context.CancellationToken);
        // Stream G: a position swap is a roster mutation — refresh viewers live just like a move/fit change. Reuse the
        // RosterChanged kind (clients reload kind-agnostically). Both members share a fleet (the handler enforces it),
        // so resolve it from either one.
        if (result.IsSuccess && await fleets.GetMemberAsync(request.FirstMemberId, context.CancellationToken) is { } member)
            await BroadcastFleetChangedAsync(member.FleetId, FleetChangeKind.RosterChanged, context.CancellationToken);
        return ToActionReply(result, "Members swapped.");
    }

    public override async Task<FleetActionReply> AssignMemberFit(AssignMemberFitRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new AssignMemberFitCommand(
            request.MemberId, FromFitDto(request.Fit),
            request.HasCompositionEntryId ? request.CompositionEntryId : null, character.Value), context.CancellationToken);
        // B-4: a member-fit change must refresh viewers live — the roster's assigned fit + skill badge and
        // the Fleets tabs' member leaves. Reuse the existing RosterChanged kind (the request carries only the member,
        // so resolve its fleet) rather than a new event type; clients reload kind-agnostically.
        if (result.IsSuccess && await fleets.GetMemberAsync(request.MemberId, context.CancellationToken) is { } member)
            await BroadcastFleetChangedAsync(member.FleetId, FleetChangeKind.RosterChanged, context.CancellationToken);
        return ToActionReply(result, "Fit assigned.");
    }

    public override async Task<FleetActionReply> ReportMemberFitVerdict(ReportMemberFitVerdictRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        // cross-client: self-only enforced in the handler (the pilot's client is the only skill authority).
        // The result's value says whether the stored verdict changed — only then are viewers notified.
        var result = await dispatcher.Send(new ReportMemberFitVerdictCommand(
            request.MemberId, (FitSkillVerdict)request.Verdict, character.Value), context.CancellationToken);
        if (result.IsSuccess && result.Value && await fleets.GetMemberAsync(request.MemberId, context.CancellationToken) is { } member)
            await BroadcastFleetChangedAsync(member.FleetId, FleetChangeKind.RosterChanged, context.CancellationToken);
        return result.IsSuccess
            ? new FleetActionReply { Accepted = true, Message = "Reported." }
            : new FleetActionReply { Accepted = false, Message = FirstMessage(result) };
    }

    public override async Task<FleetActionReply> ReportMemberInGameFleet(ReportMemberInGameFleetRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        // self-report: self-only enforced in the handler (presence comes from the pilot's own /characters/{id}/fleet/).
        // The result's value says whether the stored presence changed — only then are viewers notified.
        var result = await dispatcher.Send(new ReportMemberInGameFleetCommand(
            request.MemberId, request.InFleet, character.Value), context.CancellationToken);
        if (result.IsSuccess && result.Value && await fleets.GetMemberAsync(request.MemberId, context.CancellationToken) is { } member)
            await BroadcastFleetChangedAsync(member.FleetId, FleetChangeKind.RosterChanged, context.CancellationToken);
        return result.IsSuccess
            ? new FleetActionReply { Accepted = true, Message = "Reported." }
            : new FleetActionReply { Accepted = false, Message = FirstMessage(result) };
    }

    public override async Task<ListMembersReply> ListMembers(ListMembersRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new ListMembersReply { Ok = false, Message = NotAuthenticated };

        var members = await dispatcher.Query(new ListMembersQuery(request.FleetId), context.CancellationToken);
        var reply = new ListMembersReply { Ok = true };
        foreach (var member in members)
        {
            var dto = new MemberDto
            {
                Id = member.Id,
                CharacterId = member.CharacterId,
                WingId = member.WingId,
                SquadId = member.SquadId,
                Role = (int)member.Role,
                IsExternal = member.IsExternal,
                AssignedFit = member.AssignedFit is null ? null : ToFitDto(member.AssignedFit),
                FitSkillVerdict = (int)member.FitSkillVerdict
            };
            if (member.AssignedCompositionEntryId is long entryId)
                dto.AssignedCompositionEntryId = entryId;
            reply.Members.Add(dto);
        }
        return reply;
    }

    public override async Task<FleetActionReply> AddExternalMember(AddExternalMemberRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new AddExternalMemberCommand(
            request.FleetId, request.CharacterId, character.Value), context.CancellationToken);
        if (result.IsSuccess)
            await BroadcastFleetChangedAsync(request.FleetId, FleetChangeKind.RosterChanged, context.CancellationToken);
        return ToActionReply(result, "Added.");
    }

    public override async Task<FleetActionReply> TransferFleetOwnership(TransferFleetOwnershipRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new TransferFleetOwnershipCommand(
            request.FleetId, request.NewOwnerCharacterId, character.Value), context.CancellationToken);
        if (result.IsSuccess)
            await BroadcastFleetChangedAsync(request.FleetId, FleetChangeKind.RosterChanged, context.CancellationToken);
        return ToActionReply(result, "Transferred.");
    }

    public override async Task<FleetActionReply> RemoveFleetMember(RemoveFleetMemberRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        // Resolve the fleet BEFORE the removal deletes the member row, so a successful kick can still notify the
        // remaining (and the kicked) members — without it their open windows kept showing the removed member.
        var member = await fleets.GetMemberAsync(request.MemberId, context.CancellationToken);
        var result = await dispatcher.Send(new RemoveFleetMemberCommand(
            request.MemberId, character.Value), context.CancellationToken);
        if (result.IsSuccess && member is not null)
            await BroadcastFleetChangedAsync(member.FleetId, FleetChangeKind.RosterChanged, context.CancellationToken);
        return ToActionReply(result, "Removed.");
    }

    // --- Invites. After persisting, the server pushes the targeted event over the bus. ---

    public override async Task<CreateStructureReply> CreateFleetInvite(CreateFleetInviteRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new CreateStructureReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new CreateFleetInviteCommand(
            request.FleetId,
            request.InviteeCharacterId,
            (FleetRole)request.Role,
            NullIfZero(request.WingId),
            NullIfZero(request.SquadId),
            NullIfEmpty(request.Message),
            character.Value), context.CancellationToken);

        if (!result.IsSuccess)
            return new CreateStructureReply { Accepted = false, Message = FirstMessage(result) };

        var payload = result.Value!;
        // the invite is enqueued by the handler, which raises MessageEnqueuedEvent → the server live-delivers
        // it centrally (single inbox channel). Offline → the on-connect sweep delivers it.
        return new CreateStructureReply { Accepted = true, Message = "Invited.", Id = payload.InviteId };
    }

    public override async Task<FleetActionReply> RespondToFleetInvite(RespondToFleetInviteRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new RespondToFleetInviteCommand(request.InviteId, request.Accept, character.Value), context.CancellationToken);
        if (!result.IsSuccess)
            return new FleetActionReply { Accepted = false, Message = FirstMessage(result) };

        var payload = result.Value!;
        await PushAsync(new FleetInviteRespondedEvent(payload, payload.InviteeCharacterId), context.CancellationToken);
        if (payload.Accepted)
            await BroadcastFleetChangedAsync(payload.FleetId, FleetChangeKind.RosterChanged, context.CancellationToken);
        return new FleetActionReply { Accepted = true, Message = payload.Accepted ? "Joined." : "Declined." };
    }

    public override async Task<FleetActionReply> RespondToMessage(RespondToMessageRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        // generic respond — the message-kind's responder runs the domain action (a fleet invite joins
        // the roster). RespondToFleetInvite stays as the internal fleet path the responder delegates to.
        var result = await dispatcher.Send(new RespondToMessageCommand(request.MessageId, request.Accept, character.Value), context.CancellationToken);
        return result.IsSuccess
            ? new FleetActionReply { Accepted = true, Message = result.Value!.Accepted ? "Accepted." : "Declined." }
            : new FleetActionReply { Accepted = false, Message = FirstMessage(result) };
    }

    public override async Task<ListInvitesReply> ListPendingInvites(ListPendingInvitesRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new ListInvitesReply { Ok = false, Message = NotAuthenticated };

        var invites = await dispatcher.Query(new ListPendingInvitesQuery(character.Value), context.CancellationToken);
        return ToInvitesReply(invites);
    }

    /// <summary>A fleet's open invites for the roster's pending-invites section.</summary>
    public override async Task<ListInvitesReply> ListPendingFleetInvites(ListPendingFleetInvitesRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new ListInvitesReply { Ok = false, Message = NotAuthenticated };

        var invites = await dispatcher.Query(new ListPendingFleetInvitesQuery(request.FleetId), context.CancellationToken);
        return ToInvitesReply(invites);
    }

    private static ListInvitesReply ToInvitesReply(IReadOnlyList<FleetInvite> invites)
    {
        var reply = new ListInvitesReply { Ok = true };
        foreach (var invite in invites)
            reply.Invites.Add(new InviteDto
            {
                Id = invite.Id,
                FleetId = invite.FleetId,
                InviterCharacterId = invite.InviterCharacterId,
                InviteeCharacterId = invite.InviteeCharacterId,
                Role = (int)invite.Role,
                Status = (int)invite.Status,
                CreatedAt = invite.CreatedAt.ToString("o")
            });
        return reply;
    }

    // --- Discovery. ---

    public override async Task<ListFleetsReply> ListOpenFleets(ListOpenFleetsRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new ListFleetsReply { Ok = false, Message = NotAuthenticated };

        var fleets = await dispatcher.Query(new ListOpenFleetsQuery(), context.CancellationToken);
        var reply = new ListFleetsReply { Ok = true };
        foreach (var fleet in fleets)
            reply.Fleets.Add(ToDto(fleet));
        return reply;
    }

    public override async Task<FleetActionReply> JoinFleet(JoinFleetRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new JoinFleetCommand(request.FleetId, character.Value), context.CancellationToken);
        if (result.IsSuccess)
            await BroadcastFleetChangedAsync(request.FleetId, FleetChangeKind.RosterChanged, context.CancellationToken);
        return ToActionReply(result, "Joined.");
    }

    // --- Request-to-join (6.2). An invite-only fleet is not directly joinable; the requester asks and
    // the owner is messaged. The owner answers via the generic RespondToMessage RPC. ---

    public override async Task<FleetActionReply> RequestToJoin(RequestToJoinRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new RequestToJoinCommand(request.FleetId, character.Value), context.CancellationToken);
        if (!result.IsSuccess)
            return new FleetActionReply { Accepted = false, Message = FirstMessage(result) };

        // the request is enqueued for the owner by the handler, which raises MessageEnqueuedEvent → the server
        // live-delivers it centrally (single inbox channel). Offline → the on-connect sweep delivers it.
        return new FleetActionReply { Accepted = true, Message = "Requested." };
    }

    public override async Task<FleetActionReply> RespondToJoinRequest(RespondToJoinRequestRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        // Direct owner accept/decline by request id — the same shared respond-core the message path uses.
        var result = await dispatcher.Send(new RespondToJoinRequestCommand(request.RequestId, request.Accept, character.Value), context.CancellationToken);
        return result.IsSuccess
            ? new FleetActionReply { Accepted = true, Message = request.Accept ? "Accepted." : "Declined." }
            : new FleetActionReply { Accepted = false, Message = FirstMessage(result) };
    }

    public override async Task<ListJoinRequestsReply> ListPendingJoinRequests(ListPendingJoinRequestsRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new ListJoinRequestsReply { Ok = false, Message = NotAuthenticated };

        var requests = await dispatcher.Query(new ListPendingJoinRequestsQuery(request.FleetId), context.CancellationToken);
        var reply = new ListJoinRequestsReply { Ok = true };
        foreach (var joinRequest in requests)
            reply.Requests.Add(new JoinRequestDto
            {
                Id = joinRequest.Id,
                FleetId = joinRequest.FleetId,
                RequesterCharacterId = joinRequest.RequesterCharacterId
            });
        return reply;
    }

    public override async Task<ListConnectedCharactersReply> ListConnectedCharacters(ListConnectedCharactersRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new ListConnectedCharactersReply { Ok = false, Message = NotAuthenticated };

        var reply = new ListConnectedCharactersReply { Ok = true };
        foreach (var connected in connectedClients.ConnectedCharacters())
            reply.Characters.Add(new ConnectedCharacterDto { CharacterId = connected.CharacterId, CharacterName = connected.CharacterName });
        return reply;
    }

    // --- Active participation. Runtime state on the server; entering requires membership + an
    // active fleet, and replaces any previous active fleet. ---

    public override async Task<FleetActionReply> EnterFleet(EnterFleetRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var fleet = await dispatcher.Query(new GetFleetQuery(request.FleetId), context.CancellationToken);
        if (fleet is null || fleet.State != FleetState.Active)
            return new FleetActionReply { Accepted = false, Message = "Fleet not found or no longer active." };

        // Participation requires roster membership — except the creator, who may always enter their own fleet
        // even without a roster row.
        var isCreator = fleet.CreatorCharacterId == character.Value;
        if (!isCreator && !await dispatcher.Query(new IsFleetMemberQuery(request.FleetId, character.Value), context.CancellationToken))
            return new FleetActionReply { Accepted = false, Message = "You must be a fleet member to participate." };

        // Participation is now derived server-side: a connected roster member is automatically in the fleet's
        // broadcast set, so there is no separate "enter" state to set — this just stamps activity.
        await fleets.TouchActivityAsync(request.FleetId, DateTimeOffset.UtcNow, context.CancellationToken);
        return new FleetActionReply { Accepted = true, Message = "Active." };
    }

    public override async Task<FleetActionReply> LeaveFleet(LeaveFleetRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        // Leaving means leaving the roster: once the membership is gone the character drops out of the fleet's
        // broadcast set (members ∩ connected). Leave the SPECIFIC fleet (a character may be a member of several —
        // e.g. signed up in advance to more than one). RemoveFleetMember enforces self-only + the creator-must-transfer
        // rule.
        var member = (await fleets.ListMembersAsync(request.FleetId, context.CancellationToken))
            .FirstOrDefault(m => m.CharacterId == character.Value);
        if (member is null)
            return new FleetActionReply { Accepted = true, Message = "Left." }; // not a member → nothing to do

        var result = await dispatcher.Send(new RemoveFleetMemberCommand(member.Id, character.Value), context.CancellationToken);
        if (result.IsSuccess)
            await BroadcastFleetChangedAsync(request.FleetId, FleetChangeKind.RosterChanged, context.CancellationToken);
        return ToActionReply(result, "Left.");
    }

    /// <summary>Pushes a targeted event to its recipient's connections (no-op if they are offline; the durable
    /// invite is the source of truth and is re-sent on attach).</summary>
    private Task PushAsync(ITargetedEvent integrationEvent, CancellationToken cancellationToken) =>
        connectedClients.SendToCharacterAsync(integrationEvent.TargetCharacterId, WireEnvelopeFactory.ToEnvelope(integrationEvent), cancellationToken);

    /// <summary>Notifies a fleet's currently connected members that its lifecycle or roster changed, so their open
    /// fleet list, roster window and metrics participation refresh live instead of only on a reconnect/restart. The
    /// remaining roster is resolved fresh, so a leave/remove reaches everyone still in the fleet.</summary>
    // --- Fleet Compositions. Acting character from the session; mutations gated owner-or-manage in
    // the handlers. Reads go straight to the composition repository (trivial pass-throughs, no query handler). ---

    public override async Task<CreateStructureReply> CreateFleetComposition(CreateFleetCompositionRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new CreateStructureReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new CreateFleetCompositionCommand(
            request.Name, NullIfEmpty(request.Description), request.IsClientOnly, character.Value), context.CancellationToken);
        return ToCreateReply(result);
    }

    public override async Task<FleetActionReply> EditFleetComposition(EditFleetCompositionRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new EditFleetCompositionCommand(
            request.CompositionId, request.Name, NullIfEmpty(request.Description), character.Value), context.CancellationToken);
        return ToActionReply(result, "Saved.");
    }

    public override async Task<FleetActionReply> DeleteFleetComposition(DeleteFleetCompositionRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new DeleteFleetCompositionCommand(request.CompositionId, character.Value), context.CancellationToken);
        return ToActionReply(result, "Deleted.");
    }

    public override async Task<ListFleetCompositionsReply> ListMyFleetCompositions(ListMyFleetCompositionsRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new ListFleetCompositionsReply { Ok = false, Message = NotAuthenticated };

        var list = await compositions.ListByOwnerAsync(character.Value, context.CancellationToken);
        var names = await OwnerNamesAsync(context.CancellationToken);
        var fleetCounts = await fleets.CountFleetsByCompositionIdsAsync(list.Select(c => c.Id).ToList(), context.CancellationToken);
        var reply = new ListFleetCompositionsReply { Ok = true };
        foreach (var composition in list)
            reply.Compositions.Add(ToCompositionDto(composition, canEdit: true,
                ownerName: OwnerName(names, composition.OwnerCharacterId), fleetCount: FleetCountOf(fleetCounts, composition.Id)));
        return reply;
    }

    public override async Task<ListFleetCompositionsReply> ListAllFleetCompositions(ListAllFleetCompositionsRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new ListFleetCompositionsReply { Ok = false, Message = NotAuthenticated };

        var list = await compositions.ListAllAsync(context.CancellationToken);
        var names = await OwnerNamesAsync(context.CancellationToken);
        var fleetCounts = await fleets.CountFleetsByCompositionIdsAsync(list.Select(c => c.Id).ToList(), context.CancellationToken);
        var reply = new ListFleetCompositionsReply { Ok = true };
        foreach (var composition in list)
        {
            var canEdit = await compositionAuthorizer.CanManageAsync(composition, character.Value, context.CancellationToken);
            reply.Compositions.Add(ToCompositionDto(composition, canEdit,
                OwnerName(names, composition.OwnerCharacterId), FleetCountOf(fleetCounts, composition.Id)));
        }
        return reply;
    }

    private async Task<Dictionary<int, string>> OwnerNamesAsync(CancellationToken cancellationToken)
    {
        var synced = await serverAuth.ListSyncedAsync(cancellationToken);
        return synced
            .GroupBy(s => s.EsiCharacterId)
            .ToDictionary(g => g.Key, g => g.First().CharacterName);
    }

    private static string OwnerName(IReadOnlyDictionary<int, string> names, int characterId) =>
        names.TryGetValue(characterId, out var name) && !string.IsNullOrWhiteSpace(name) ? name : $"Char {characterId}";

    public override async Task<GetFleetCompositionReply> GetFleetComposition(GetFleetCompositionRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new GetFleetCompositionReply { Found = false, Message = NotAuthenticated };

        var graph = await compositions.GetGraphAsync(request.CompositionId, context.CancellationToken);
        if (graph is null)
            return new GetFleetCompositionReply { Found = false, Message = "Composition not found." };

        // Resolve the owner name like the list paths do, so the detail view carries it too (it defaulted to empty).
        var ownerName = OwnerName(await OwnerNamesAsync(context.CancellationToken), graph.Composition.OwnerCharacterId);
        return new GetFleetCompositionReply { Found = true, Composition = ToViewDto(graph, ownerName) };
    }

    public override async Task<CreateStructureReply> AddFleetCompositionRole(AddFleetCompositionRoleRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new CreateStructureReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new AddFleetCompositionRoleCommand(
            request.CompositionId, request.RoleName, request.HasGroupMinCount ? request.GroupMinCount : null, character.Value), context.CancellationToken);
        return ToCreateReply(result);
    }

    public override async Task<FleetActionReply> EditFleetCompositionRole(EditFleetCompositionRoleRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new EditFleetCompositionRoleCommand(
            request.RoleId, request.RoleName, request.HasGroupMinCount ? request.GroupMinCount : null, character.Value), context.CancellationToken);
        return ToActionReply(result, "Saved.");
    }

    public override async Task<FleetActionReply> RemoveFleetCompositionRole(RemoveFleetCompositionRoleRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new RemoveFleetCompositionRoleCommand(request.RoleId, character.Value), context.CancellationToken);
        return ToActionReply(result, "Removed.");
    }

    public override async Task<FleetActionReply> ReorderFleetCompositionRoles(ReorderFleetCompositionRolesRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new ReorderFleetCompositionRolesCommand(
            request.CompositionId, request.OrderedRoleIds.ToList(), character.Value), context.CancellationToken);
        return ToActionReply(result, "Reordered.");
    }

    public override async Task<CreateStructureReply> AddFleetCompositionEntry(AddFleetCompositionEntryRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new CreateStructureReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new AddFleetCompositionEntryCommand(
            request.RoleId, FromFitDto(request.Fit), request.HasEntryMinCount ? request.EntryMinCount : null, character.Value), context.CancellationToken);
        return ToCreateReply(result);
    }

    public override async Task<FleetActionReply> EditFleetCompositionEntry(EditFleetCompositionEntryRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new EditFleetCompositionEntryCommand(
            request.EntryId, request.HasEntryMinCount ? request.EntryMinCount : null, character.Value), context.CancellationToken);
        return ToActionReply(result, "Saved.");
    }

    public override async Task<FleetActionReply> RemoveFleetCompositionEntry(RemoveFleetCompositionEntryRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new RemoveFleetCompositionEntryCommand(request.EntryId, character.Value), context.CancellationToken);
        return ToActionReply(result, "Removed.");
    }

    public override async Task<FleetActionReply> ReorderFleetCompositionEntries(ReorderFleetCompositionEntriesRequest request, ServerCallContext context)
    {
        var character = await AuthenticateAsync(context);
        if (character is null)
            return new FleetActionReply { Accepted = false, Message = NotAuthenticated };

        var result = await dispatcher.Send(new ReorderFleetCompositionEntriesCommand(
            request.RoleId, request.OrderedEntryIds.ToList(), character.Value), context.CancellationToken);
        return ToActionReply(result, "Reordered.");
    }

    private static FleetCompositionDto ToCompositionDto(FleetComposition c, bool canEdit = false, string ownerName = "", int fleetCount = 0) => new()
    {
        Id = c.Id,
        Name = c.Name,
        Description = c.Description ?? "",
        OwnerCharacterId = c.OwnerCharacterId,
        CreatedAt = c.CreatedAt.ToString("o"),
        UpdatedAt = c.UpdatedAt.ToString("o"),
        CanEdit = canEdit,
        OwnerCharacterName = ownerName,
        FleetCount = fleetCount
    };

    private static int FleetCountOf(IReadOnlyDictionary<long, int> counts, long compositionId) =>
        counts.TryGetValue(compositionId, out var count) ? count : 0;

    private static FleetCompositionViewDto ToViewDto(FleetCompositionGraph graph, string ownerName = "")
    {
        var dto = new FleetCompositionViewDto { Composition = ToCompositionDto(graph.Composition, ownerName: ownerName) };
        foreach (var roleGraph in graph.Roles)
        {
            var roleDto = new FleetCompositionRoleDto
            {
                Id = roleGraph.Role.Id,
                CompositionId = roleGraph.Role.CompositionId,
                RoleName = roleGraph.Role.RoleName,
                SortOrder = roleGraph.Role.SortOrder
            };
            if (roleGraph.Role.GroupMinCount is int groupMin)
                roleDto.GroupMinCount = groupMin;

            foreach (var entry in roleGraph.Entries)
            {
                var entryDto = new FleetCompositionEntryDto
                {
                    Id = entry.Id,
                    RoleId = entry.RoleId,
                    SortOrder = entry.SortOrder,
                    Fit = ToFitDto(entry.Fit)
                };
                if (entry.EntryMinCount is int entryMin)
                    entryDto.EntryMinCount = entryMin;
                roleDto.Entries.Add(entryDto);
            }

            dto.Roles.Add(roleDto);
        }

        return dto;
    }

    private static FitReferenceDto ToFitDto(FitReference fit)
    {
        var dto = new FitReferenceDto
        {
            ShipTypeId = fit.ShipTypeId,
            FitName = fit.FitName,
            RawJson = fit.RawJson,
            ContentHash = fit.ContentHash
        };
        if (fit.LocalFittingId is int localFittingId)
            dto.LocalFittingId = localFittingId;
        if (fit.ServerSharedFitId is int serverSharedFitId)
            dto.ServerSharedFitId = serverSharedFitId;
        return dto;
    }

    private static FitReference FromFitDto(FitReferenceDto? dto)
    {
        if (dto is null)
            return new FitReference(); // empty → the handler rejects it (a fit with a ship + name is required).

        return new FitReference
        {
            ShipTypeId = dto.ShipTypeId,
            FitName = dto.FitName,
            RawJson = dto.RawJson,
            ContentHash = dto.ContentHash,
            LocalFittingId = dto.HasLocalFittingId ? dto.LocalFittingId : null,
            ServerSharedFitId = dto.HasServerSharedFitId ? dto.ServerSharedFitId : null
        };
    }

    private async Task BroadcastFleetChangedAsync(long fleetId, FleetChangeKind kind, CancellationToken cancellationToken)
    {
        var members = await fleets.ListMembersAsync(fleetId, cancellationToken);
        var envelope = WireEnvelopeFactory.ToEnvelope(new FleetChangedEvent(new FleetChangePayload(fleetId, kind)));
        await connectedClients.SendToCharactersAsync(members.Select(m => m.CharacterId), envelope, cancellationToken);
    }

    private static long? NullIfZero(long value) => value > 0 ? value : null;

    private async Task<int?> AuthenticateAsync(ServerCallContext context)
    {
        var token = ExtractBearer(context);
        var session = token is null ? null : await sessions.ValidateAsync(token, context.CancellationToken);
        return session?.SyncedCharacter?.EsiCharacterId;
    }

    private static FleetActionReply ToActionReply(Result result, string okMessage) =>
        result.IsSuccess
            ? new FleetActionReply { Accepted = true, Message = okMessage }
            : new FleetActionReply { Accepted = false, Message = FirstMessage(result) };

    private static CreateStructureReply ToCreateReply(Result<long> result) =>
        result.IsSuccess
            ? new CreateStructureReply { Accepted = true, Message = "Created.", Id = result.Value }
            : new CreateStructureReply { Accepted = false, Message = FirstMessage(result) };

    private static FleetDto ToDto(FleetEntity f)
    {
        var dto = new FleetDto
        {
            Id = f.Id,
            Name = f.Name,
            Description = f.Description ?? "",
            Visibility = (int)f.Visibility,
            OfflineBehavior = (int)f.OfflineBehavior,
            FromTime = f.FromTime?.ToString("o") ?? "",
            ToTime = f.ToTime?.ToString("o") ?? "",
            CreatorCharacterId = f.CreatorCharacterId,
            State = (int)f.State,
            CreatedAt = f.CreatedAt.ToString("o"),
            LastActivityAt = f.LastActivityAt.ToString("o"),
            Activation = (int)f.Activation,
            EsiAutoApplyStructure = f.EsiAutoApplyStructure,
            EsiAutoInviteMembers = f.EsiAutoInviteMembers
        };
        if (f.FleetCompositionId is long compositionId)
            dto.FleetCompositionId = compositionId;
        if (f.EsiFleetId is long esiFleetId)
            dto.EsiFleetId = esiFleetId;
        if (f.EsiFleetBossId is int esiFleetBossId)
            dto.EsiFleetBossId = esiFleetBossId;
        return dto;
    }

    private static string FirstMessage(Result result) =>
        result.Messages.Count > 0 ? result.Messages[0].Text : "Failed.";

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static DateTimeOffset? ParseTime(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt) ? dt : null;

    private static string? ExtractBearer(ServerCallContext context)
    {
        var authorization = context.RequestHeaders.GetValue("authorization");
        return authorization is not null && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization["Bearer ".Length..]
            : null;
    }
}
