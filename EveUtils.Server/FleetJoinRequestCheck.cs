using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Queries;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Messaging.Commands;
using EveUtils.Shared.Modules.Messaging.Entities;
using EveUtils.Shared.Modules.Messaging.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Server;

/// <summary>
/// Headless proof for the request-to-join round-trip, runnable via <c>--fleet-join-request-test</c>.
/// Drives the real DI container + dispatcher through request → owner-message → accept/decline (via the generic
/// RespondToMessage, delegating to the FleetJoinRequestResponder) → roster + requester notification, plus the
/// invite-only/public/duplicate/member/owner guards. Exit 0 = all checks passed, 1 = a check failed.
/// </summary>
public static class FleetJoinRequestCheck
{
    private const int Owner = 8001;       // fleet creator
    private const int Requester = 8002;
    private const int Requester2 = 8003;
    private const int Stranger = 8004;
    private const int ExistingMember = 8005;
    private const int Requester3 = 8006;  // direct-command accept path
    private const int Requester4 = 8007;  // direct-command decline path

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        Console.WriteLine("== EVE-Utils request-to-join round-trip check ==");

        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var dispatcher = sp.GetRequiredService<IDispatcher>();
        var fleets = sp.GetRequiredService<IFleetRepository>();
        var ct = CancellationToken.None;
        var ok = true;

        // 0. An invite-only fleet to request into, plus a public one to assert the "join directly" guard.
        var inviteOnly = await dispatcher.Send(new CreateFleetCommand(
            "Recruit HQ", null, FleetVisibility.InviteOnly, null, null, FleetOfflineBehavior.StayOffline, Owner), ct);
        ok &= Check("create invite-only fleet", inviteOnly.IsSuccess);
        var fleetId = inviteOnly.Value;

        var publicFleet = await dispatcher.Send(new CreateFleetCommand(
            "Open Roam", null, FleetVisibility.Public, null, null, FleetOfflineBehavior.StayOffline, Owner), ct);
        ok &= Check("create public fleet", publicFleet.IsSuccess);
        var publicFleetId = publicFleet.Value;

        // Seed an existing member to assert the already-a-member guard.
        await fleets.AddMemberAsync(new FleetMember
        {
            FleetId = fleetId,
            CharacterId = ExistingMember,
            Role = FleetRole.SquadMember,
            JoinTime = DateTimeOffset.UtcNow
        }, ct);

        // 1. Request to join the invite-only fleet → Pending request + a message for the owner.
        var request = await dispatcher.Send(new RequestToJoinCommand(fleetId, Requester), ct);
        ok &= Check("request to invite-only fleet → Pending request created",
            request.IsSuccess && request.Value!.RequestId > 0 && request.Value!.FleetName == "Recruit HQ");
        var requestId = request.Value!.RequestId;

        var pending = await dispatcher.Query(new ListPendingJoinRequestsQuery(fleetId), ct);
        ok &= Check("request shows up in the fleet's pending list", pending.Any(r => r.Id == requestId));

        var ownerInbox = await dispatcher.Query(new ListPendingMessagesQuery(Owner), ct);
        var ownerMessage = ownerInbox.FirstOrDefault(m => m.Kind == MessageKind.FleetJoinRequest && m.RefId == requestId);
        ok &= Check("a FleetJoinRequest message is queued for the owner (linked via RefId)", ownerMessage is not null);

        // 2. Request to a public fleet → rejected ("join directly", VALIDATION_FAILED).
        var publicRequest = await dispatcher.Send(new RequestToJoinCommand(publicFleetId, Requester), ct);
        ok &= Check("request to public fleet rejected (VALIDATION_FAILED)",
            !publicRequest.IsSuccess && publicRequest.Messages.Any(m => m.Code == MessageCodes.ValidationFailed));

        // 3. Duplicate pending request → rejected.
        var duplicate = await dispatcher.Send(new RequestToJoinCommand(fleetId, Requester), ct);
        ok &= Check("duplicate pending request rejected (VALIDATION_FAILED)",
            !duplicate.IsSuccess && duplicate.Messages.Any(m => m.Code == MessageCodes.ValidationFailed));

        // 4. Request from an existing member → rejected.
        var fromMember = await dispatcher.Send(new RequestToJoinCommand(fleetId, ExistingMember), ct);
        ok &= Check("request from an existing member rejected (VALIDATION_FAILED)",
            !fromMember.IsSuccess && fromMember.Messages.Any(m => m.Code == MessageCodes.ValidationFailed));

        // 5. Request to an unknown fleet → NOT_FOUND.
        var ghost = await dispatcher.Send(new RequestToJoinCommand(999_999_999, Requester), ct);
        ok &= Check("request to unknown fleet → NOT_FOUND",
            !ghost.IsSuccess && ghost.Messages.Any(m => m.Code == MessageCodes.NotFound));

        // 6. A non-owner answering the owner's message → rejected (the message is the owner's; only they may answer).
        var wrongResponder = await dispatcher.Send(new RespondToMessageCommand(ownerMessage!.Id, true, Stranger), ct);
        ok &= Check("non-owner respond rejected (PERMISSION_DENIED)",
            !wrongResponder.IsSuccess && wrongResponder.Messages.Any(m => m.Code == MessageCodes.PermissionDenied));

        // 7. Owner accepts via the generic RespondToMessage → requester joins the roster + request Accepted +
        //    a notification message lands in the requester's inbox.
        var accept = await dispatcher.Send(new RespondToMessageCommand(ownerMessage!.Id, true, Owner), ct);
        ok &= Check("owner accepts via RespondToMessage", accept.IsSuccess && accept.Value!.Accepted);
        var members = await fleets.ListMembersAsync(fleetId, ct);
        ok &= Check("accepted requester is on the roster", members.Any(m => m.CharacterId == Requester));
        var acceptedRequest = await fleets.GetJoinRequestAsync(requestId, ct);
        ok &= Check("request is now Accepted", acceptedRequest is { Status: FleetJoinRequestStatus.Accepted, RespondedAt: not null });
        var requesterInbox = await dispatcher.Query(new ListPendingMessagesQuery(Requester), ct);
        ok &= Check("an acceptance notification is queued for the requester",
            requesterInbox.Any(m => m.Kind == MessageKind.Mail && m.SenderCharacterId == Owner));
        var pendingAfter = await dispatcher.Query(new ListPendingJoinRequestsQuery(fleetId), ct);
        ok &= Check("answered request no longer pending", pendingAfter.All(r => r.Id != requestId));

        // 8. Decline path: a second requester is denied → NOT on the roster, request Denied, notified.
        var request2 = await dispatcher.Send(new RequestToJoinCommand(fleetId, Requester2), ct);
        ok &= Check("second request created", request2.IsSuccess);
        var request2Id = request2.Value!.RequestId;
        var owner2 = await dispatcher.Query(new ListPendingMessagesQuery(Owner), ct);
        var ownerMessage2 = owner2.First(m => m.Kind == MessageKind.FleetJoinRequest && m.RefId == request2Id);

        var decline = await dispatcher.Send(new RespondToMessageCommand(ownerMessage2.Id, false, Owner), ct);
        ok &= Check("owner declines via RespondToMessage", decline.IsSuccess && !decline.Value!.Accepted);
        var membersAfterDecline = await fleets.ListMembersAsync(fleetId, ct);
        ok &= Check("declined requester did NOT join", membersAfterDecline.All(m => m.CharacterId != Requester2));
        var deniedRequest = await fleets.GetJoinRequestAsync(request2Id, ct);
        ok &= Check("request is now Denied", deniedRequest is { Status: FleetJoinRequestStatus.Denied, RespondedAt: not null });
        var requester2Inbox = await dispatcher.Query(new ListPendingMessagesQuery(Requester2), ct);
        ok &= Check("a decline notification is queued for the second requester",
            requester2Inbox.Any(m => m.Kind == MessageKind.Mail && m.SenderCharacterId == Owner));

        // 9. Direct command path: the owner accepts/declines a request straight by its id — no inbox
        //    round-trip — through the same shared respond-core. A non-owner is rejected (PERMISSION_DENIED).
        var request3 = await dispatcher.Send(new RequestToJoinCommand(fleetId, Requester3), ct);
        ok &= Check("third request created (direct-command accept path)", request3.IsSuccess);
        var request3Id = request3.Value!.RequestId;

        var directWrongResponder = await dispatcher.Send(new RespondToJoinRequestCommand(request3Id, true, Stranger), ct);
        ok &= Check("non-owner direct command rejected (PERMISSION_DENIED)",
            !directWrongResponder.IsSuccess && directWrongResponder.Messages.Any(m => m.Code == MessageCodes.PermissionDenied));

        var directAccept = await dispatcher.Send(new RespondToJoinRequestCommand(request3Id, true, Owner), ct);
        ok &= Check("owner accepts via direct command", directAccept.IsSuccess);
        var membersAfterDirectAccept = await fleets.ListMembersAsync(fleetId, ct);
        ok &= Check("direct-accepted requester is on the roster", membersAfterDirectAccept.Any(m => m.CharacterId == Requester3));
        var directAccepted = await fleets.GetJoinRequestAsync(request3Id, ct);
        ok &= Check("direct-accepted request is now Accepted", directAccepted is { Status: FleetJoinRequestStatus.Accepted, RespondedAt: not null });
        var requester3Inbox = await dispatcher.Query(new ListPendingMessagesQuery(Requester3), ct);
        ok &= Check("an acceptance notification is queued for the direct-accepted requester",
            requester3Inbox.Any(m => m.Kind == MessageKind.Mail && m.SenderCharacterId == Owner));

        var request4 = await dispatcher.Send(new RequestToJoinCommand(fleetId, Requester4), ct);
        ok &= Check("fourth request created (direct-command decline path)", request4.IsSuccess);
        var request4Id = request4.Value!.RequestId;

        var directDecline = await dispatcher.Send(new RespondToJoinRequestCommand(request4Id, false, Owner), ct);
        ok &= Check("owner declines via direct command", directDecline.IsSuccess);
        var membersAfterDirectDecline = await fleets.ListMembersAsync(fleetId, ct);
        ok &= Check("direct-declined requester did NOT join", membersAfterDirectDecline.All(m => m.CharacterId != Requester4));
        var directDenied = await fleets.GetJoinRequestAsync(request4Id, ct);
        ok &= Check("direct-declined request is now Denied", directDenied is { Status: FleetJoinRequestStatus.Denied, RespondedAt: not null });
        var requester4Inbox = await dispatcher.Query(new ListPendingMessagesQuery(Requester4), ct);
        ok &= Check("a decline notification is queued for the direct-declined requester",
            requester4Inbox.Any(m => m.Kind == MessageKind.Mail && m.SenderCharacterId == Owner));

        await CleanupAsync(sp, fleetId, publicFleetId, ct);

        Console.WriteLine(ok ? "RESULT: PASS ✓" : "RESULT: FAIL ✗");
        return ok ? 0 : 1;
    }

    private static async Task CleanupAsync(IServiceProvider provider, long fleetId, long publicFleetId, CancellationToken ct)
    {
        await using var db = await provider.GetRequiredService<IDbContextFactory<ServerDbContext>>().CreateDbContextAsync(ct);
        foreach (var id in new[] { fleetId, publicFleetId })
        {
            var fleet = await db.Set<FleetEntity>().FirstOrDefaultAsync(f => f.Id == id, ct);
            if (fleet is not null)
                db.Remove(fleet); // cascade removes members + join requests
        }
        await db.SaveChangesAsync(ct);

        int[] recipients = [Owner, Requester, Requester2, Stranger, ExistingMember, Requester3, Requester4];
        await db.Set<QueuedMessage>().Where(m => recipients.Contains(m.RecipientCharacterId)).ExecuteDeleteAsync(ct);
    }

    private static bool Check(string label, bool pass)
    {
        Console.WriteLine($"  {(pass ? "✓" : "✗ FAIL")}  {label}");
        return pass;
    }
}
