using System.Collections.Concurrent;
using EveUtils.Grpc;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Queries;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Messaging;
using EveUtils.Shared.Modules.Messaging.Commands;
using EveUtils.Shared.Modules.Messaging.Entities;
using EveUtils.Shared.Modules.Messaging.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Server;

/// <summary>
/// Headless proof for the invite extension, runnable via <c>--fleet-invite-ext-test</c>. Drives the
/// real DI container through: an invite carrying a free-text note → the invitee's queued message body carries it;
/// answering through the generic message responder → the inviter is notified by mail; the fleet's pending-invite
/// list shows the open invite before accept and not after; and the fallback where a fleet disbanded between
/// invite and accept fails the accept with "no longer available" and clears the now-orphaned invite from pending.
/// Exit code 0 = all checks passed, 1 = a check failed.
/// </summary>
public static class FleetInviteExtCheck
{
    private const int Inviter = 8001;   // fleet creator / inviter
    private const int Invitee = 8002;
    private const int Invitee2 = 8003;

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        Console.WriteLine("== EVE-Utils invite-extension check ==");

        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var dispatcher = sp.GetRequiredService<IDispatcher>();
        var repository = sp.GetRequiredService<IFleetRepository>();
        var ct = CancellationToken.None;
        var ok = true;

        // 0. A fleet to invite into — with a scheduled window so the enriched invite body can surface it.
        var fromTime = new DateTimeOffset(2026, 6, 4, 20, 0, 0, TimeSpan.Zero);
        var toTime = new DateTimeOffset(2026, 6, 4, 22, 0, 0, TimeSpan.Zero);
        var created = await dispatcher.Send(new CreateFleetCommand(
            "Wormhole Krew", null, FleetVisibility.InviteOnly, fromTime, toTime, FleetOfflineBehavior.StayOffline, Inviter), ct);
        ok &= Check("create fleet", created.IsSuccess);
        var fleetId = created.Value;

        // 1. Invite with a free-text message → the invitee's queued FleetInvite message carries it as the body.
        const string note = "Come fly with us — we need a Logi pilot tonight.";
        var invite = await dispatcher.Send(new CreateFleetInviteCommand(
            fleetId, Invitee, FleetRole.SquadMember, null, null, note, Inviter), ct);
        ok &= Check("creator invites with a message", invite.IsSuccess && invite.Value!.InviteId > 0);
        var inviteId = invite.Value!.InviteId;

        var persisted = await repository.GetInviteAsync(inviteId, ct);
        ok &= Check("invite persists the free-text message", persisted?.Message == note);

        var inviteePending = await dispatcher.Query(new ListPendingMessagesQuery(Invitee), ct);
        var inviteMsg = inviteePending.FirstOrDefault(m => m.Kind == MessageKind.FleetInvite && m.RefId == inviteId);
        ok &= Check("invite enqueued as a FleetInvite message for the invitee", inviteMsg is not null);
        // the enriched body carries fleet name + offered role + scheduled window + the inviter's note.
        ok &= Check("the invitee's message body carries the offered role", inviteMsg?.Body?.Contains("Squad Member") == true);
        ok &= Check("the invitee's message body carries the scheduled window", inviteMsg?.Body?.Contains("Scheduled") == true);
        ok &= Check("the invitee's message body still carries the inviter's note", inviteMsg?.Body?.Contains(note) == true);

        // 2. Pending-by-fleet shows the open invite BEFORE the response.
        var pendingBefore = await dispatcher.Query(new ListPendingFleetInvitesQuery(fleetId), ct);
        ok &= Check("pending-by-fleet lists the open invite before accept", pendingBefore.Any(i => i.Id == inviteId));

        // 3. Accept through the generic message responder → roster join AND the inviter is notified by mail.
        var accept = await dispatcher.Send(new RespondToMessageCommand(inviteMsg!.Id, true, Invitee), ct);
        ok &= Check("invitee accepts via the message responder", accept.IsSuccess && accept.Value!.Accepted);
        var members = await repository.ListMembersAsync(fleetId, ct);
        ok &= Check("accepted invitee joined the roster", members.Any(m => m.CharacterId == Invitee));

        var inviterMail = await dispatcher.Query(new ListPendingMessagesQuery(Inviter), ct);
        var notification = inviterMail.FirstOrDefault(m => m.Kind == MessageKind.Mail && m.Title.Contains("accepted"));
        ok &= Check("inviter is notified by mail that the invite was accepted", notification is not null);

        // 4. Pending-by-fleet no longer lists the answered invite.
        var pendingAfter = await dispatcher.Query(new ListPendingFleetInvitesQuery(fleetId), ct);
        ok &= Check("pending-by-fleet drops the invite after accept", pendingAfter.All(i => i.Id != inviteId));

        // 5. Fallback: an invite whose fleet is disbanded before the answer → accept fails with a clear message
        //    and the now-orphaned invite is no longer pending.
        var invite2 = await dispatcher.Send(new CreateFleetInviteCommand(
            fleetId, Invitee2, FleetRole.SquadMember, null, null, null, Inviter), ct);
        ok &= Check("second invite created", invite2.IsSuccess);
        var invite2Id = invite2.Value!.InviteId;

        var disband = await dispatcher.Send(new DisbandFleetCommand(fleetId, Inviter), ct);
        ok &= Check("fleet disbanded", disband.IsSuccess);

        var lateAccept = await dispatcher.Send(new RespondToFleetInviteCommand(invite2Id, true, Invitee2), ct);
        ok &= Check("accept into a disbanded fleet fails (VALIDATION_FAILED)",
            !lateAccept.IsSuccess && lateAccept.Messages.Any(m => m.Code == MessageCodes.ValidationFailed));
        ok &= Check("the failure says the fleet is no longer available",
            lateAccept.Messages.Any(m => m.Text.Contains("no longer available")));
        ok &= Check("the orphaned invitee did NOT join",
            (await repository.ListMembersAsync(fleetId, ct)).All(m => m.CharacterId != Invitee2));

        var orphanPending = await dispatcher.Query(new ListPendingFleetInvitesQuery(fleetId), ct);
        ok &= Check("the orphaned invite is no longer pending", orphanPending.All(i => i.Id != invite2Id));

        // 6. Declining stays valid even after disband (the invitee just clears the inbox).
        // (Re-use a fresh invite-less assertion is unnecessary; the deny path is covered by --fleet-invite-test.)

        await CleanupAsync(sp, fleetId, ct);

        Console.WriteLine(ok ? "RESULT: PASS ✓" : "RESULT: FAIL ✗");
        return ok ? 0 : 1;
    }

    private static async Task CleanupAsync(IServiceProvider provider, long fleetId, CancellationToken ct)
    {
        await using var db = await provider.GetRequiredService<IDbContextFactory<ServerDbContext>>().CreateDbContextAsync(ct);
        var fleet = await db.Set<FleetEntity>().FirstOrDefaultAsync(f => f.Id == fleetId, ct);
        if (fleet is not null)
        {
            db.Remove(fleet); // cascade removes members + invites
            await db.SaveChangesAsync(ct);
        }

        int[] recipients = [Inviter, Invitee, Invitee2];
        await db.Set<QueuedMessage>().Where(m => recipients.Contains(m.RecipientCharacterId)).ExecuteDeleteAsync(ct);
    }

    private static bool Check(string label, bool pass)
    {
        Console.WriteLine($"  {(pass ? "✓" : "✗ FAIL")}  {label}");
        return pass;
    }
}
