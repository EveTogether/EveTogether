using System.Collections.Concurrent;
using EveUtils.Grpc;
using EveUtils.Server.Messaging;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Messaging.Commands;
using EveUtils.Shared.Modules.Messaging.Entities;
using EveUtils.Shared.Modules.Messaging.Queries;
using EveUtils.Shared.Modules.Messaging.Repositories;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;
using FleetRepo = EveUtils.Shared.Modules.Fleet.Repositories.IFleetRepository;

namespace EveUtils.Server;

/// <summary>
/// Headless proof for the internal message queue, runnable via <c>--message-test</c>. Drives the real
/// DI container through enqueue → deliver-on-connect → per-kind retention → the generic respond (delegating to
/// the fleet invite) → 30-day expiry cleanup. Delivery is asserted against the real
/// <see cref="MessageDeliveryService"/> with a fake stream writer. Exit 0 = all passed, 1 = a check failed.
/// </summary>
public static class MessageQueueCheck
{
    private const int MailRecipient = 7007;
    private const int Inviter = 6001;
    private const int Invitee = 6002;
    private const int Stranger = 6003;

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        Console.WriteLine("== EVE-Utils message-queue check ==");

        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var dispatcher = sp.GetRequiredService<IDispatcher>();
        var messages = sp.GetRequiredService<IMessageRepository>();
        var fleets = sp.GetRequiredService<FleetRepo>();
        var delivery = sp.GetRequiredService<MessageDeliveryService>();
        var ct = CancellationToken.None;
        var ok = true;

        // 1. Mail = fire-and-forget: enqueue → deliver-on-connect → dropped from the queue.
        var mail = await dispatcher.Send(new EnqueueMessageCommand(
            MailRecipient, Inviter, MessageKind.Mail, "Welcome", "First mail.", null, null), ct);
        ok &= Check("enqueue mail", mail.IsSuccess && mail.Value > 0);
        var mailPending = await dispatcher.Query(new ListPendingMessagesQuery(MailRecipient), ct);
        ok &= Check("mail is pending before delivery", mailPending.Any(m => m.Id == mail.Value));

        var mailWriter = new RecordingWriter();
        var deliveredCount = await delivery.DeliverPendingAsync(mailWriter, MailRecipient, ct);
        ok &= Check("deliver-on-connect pushes the mail", deliveredCount == 1 && mailWriter.Count == 1);
        var mailAfter = await dispatcher.Query(new ListPendingMessagesQuery(MailRecipient), ct);
        ok &= Check("mail dropped from the queue after delivery (fire-and-forget)", mailAfter.All(m => m.Id != mail.Value));

        // 2. Invite rides the queue: create fleet + invite → a FleetInvite-kind message queued for the invitee.
        var fleet = await dispatcher.Send(new CreateFleetCommand(
            "Message Fleet", null, FleetVisibility.InviteOnly, null, null, FleetOfflineBehavior.StayOffline, Inviter), ct);
        ok &= Check("create fleet", fleet.IsSuccess);
        var fleetId = fleet.Value;

        var invite = await dispatcher.Send(new CreateFleetInviteCommand(
            fleetId, Invitee, FleetRole.SquadMember, null, null, null, Inviter), ct);
        ok &= Check("create invite", invite.IsSuccess);
        var inviteId = invite.Value!.InviteId;

        var invitePending = await dispatcher.Query(new ListPendingMessagesQuery(Invitee), ct);
        var inviteMsg = invitePending.FirstOrDefault(m => m.Kind == MessageKind.FleetInvite && m.RefId == inviteId);
        ok &= Check("invite enqueued as a FleetInvite message linked via RefId", inviteMsg is not null);

        // 3. The invite is delivered exactly once: after the first push it is marked Delivered (no longer pending),
        //    and a second deliver-on-connect pushes nothing — no re-delivery on reconnect. It stays answerable (step 5).
        var inviteWriter = new RecordingWriter();
        await delivery.DeliverPendingAsync(inviteWriter, Invitee, ct);
        ok &= Check("deliver-on-connect pushes the invite", inviteWriter.Count >= 1);
        var afterFirstDelivery = await dispatcher.Query(new ListPendingMessagesQuery(Invitee), ct);
        ok &= Check("invite no longer pending after delivery (delivered once)",
            afterFirstDelivery.All(m => m.Id != inviteMsg!.Id));
        var reconnectWriter = new RecordingWriter();
        var reDelivered = await delivery.DeliverPendingAsync(reconnectWriter, Invitee, ct);
        ok &= Check("invite is not re-delivered on reconnect", reDelivered == 0 && reconnectWriter.Count == 0);

        // 4. Authorization on the generic respond.
        var wrongResponder = await dispatcher.Send(new RespondToMessageCommand(inviteMsg!.Id, true, Stranger), ct);
        ok &= Check("non-recipient respond rejected (PERMISSION_DENIED)",
            !wrongResponder.IsSuccess && wrongResponder.Messages.Any(m => m.Code == MessageCodes.PermissionDenied));
        var ghost = await dispatcher.Send(new RespondToMessageCommand(999_999_999, true, Invitee), ct);
        ok &= Check("respond to unknown message → NOT_FOUND",
            !ghost.IsSuccess && ghost.Messages.Any(m => m.Code == MessageCodes.NotFound));

        // 5. The generic respond delegates to the fleet invite: accept → invitee joins the roster.
        var accept = await dispatcher.Send(new RespondToMessageCommand(inviteMsg!.Id, true, Invitee), ct);
        ok &= Check("recipient accepts via RespondToMessage", accept.IsSuccess && accept.Value!.Accepted);
        var members = await fleets.ListMembersAsync(fleetId, ct);
        ok &= Check("accept delegated to the fleet → invitee on the roster with the invited role",
            members.Any(m => m.CharacterId == Invitee && m.Role == FleetRole.SquadMember));
        var respondedPending = await dispatcher.Query(new ListPendingMessagesQuery(Invitee), ct);
        ok &= Check("answered invite no longer pending", respondedPending.All(m => m.Id != inviteMsg!.Id));

        // 6. Double-respond is refused.
        var again = await dispatcher.Send(new RespondToMessageCommand(inviteMsg!.Id, false, Invitee), ct);
        ok &= Check("double-response rejected (VALIDATION_FAILED)",
            !again.IsSuccess && again.Messages.Any(m => m.Code == MessageCodes.ValidationFailed));

        // 7. Mail carries no responder → it cannot be responded to.
        var notice = await dispatcher.Send(new EnqueueMessageCommand(
            Invitee, null, MessageKind.Mail, "Notice", null, null, null), ct);
        var mailRespond = await dispatcher.Send(new RespondToMessageCommand(notice.Value, true, Invitee), ct);
        ok &= Check("respond to mail rejected (no responder for the kind)",
            !mailRespond.IsSuccess && mailRespond.Messages.Any(m => m.Code == MessageCodes.ValidationFailed));

        // 8. Retention sweep: a past-cap message is purged, a fresh one survives.
        var expiredId = await messages.AddAsync(new QueuedMessage
        {
            RecipientCharacterId = MailRecipient,
            Kind = MessageKind.Mail,
            Title = "Old",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-40),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-10),
            Status = MessageStatus.Pending
        }, ct);
        var freshId = (await dispatcher.Send(new EnqueueMessageCommand(
            MailRecipient, null, MessageKind.Mail, "Fresh", null, null, null), ct)).Value;
        var purged = await messages.DeleteExpiredAsync(DateTimeOffset.UtcNow, ct);
        ok &= Check("expiry sweep purges the past-cap message", purged >= 1 && await messages.GetAsync(expiredId, ct) is null);
        ok &= Check("expiry sweep keeps a fresh (+30d) message", await messages.GetAsync(freshId, ct) is not null);

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

        int[] recipients = [MailRecipient, Inviter, Invitee, Stranger];
        await db.Set<QueuedMessage>().Where(m => recipients.Contains(m.RecipientCharacterId)).ExecuteDeleteAsync(ct);
    }

    private static bool Check(string label, bool pass)
    {
        Console.WriteLine($"  {(pass ? "✓" : "✗ FAIL")}  {label}");
        return pass;
    }

    private sealed class RecordingWriter : IServerStreamWriter<ServerEnvelope>
    {
        private readonly ConcurrentBag<ServerEnvelope> _received = [];
        public int Count => _received.Count;
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(ServerEnvelope message)
        {
            _received.Add(message);
            return Task.CompletedTask;
        }

        public Task WriteAsync(ServerEnvelope message, CancellationToken cancellationToken)
        {
            _received.Add(message);
            return Task.CompletedTask;
        }
    }
}
