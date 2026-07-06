using System.Collections.Concurrent;
using EveUtils.Grpc;
using EveUtils.Server.Grpc;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Queries;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Server;

/// <summary>
/// Headless proof for the invite round-trip, runnable via <c>--fleet-invite-test</c>. Drives
/// the real DI container + dispatcher through invite → targeted-delivery → accept/deny → roster, plus the
/// pending-invite vangnet. The targeted delivery is asserted against a real <see cref="ConnectedClients"/> with
/// fake stream writers (reusing <see cref="WireEnvelopeFactory"/>), so the same routing the gRPC push uses is
/// exercised. Exit code 0 = all checks passed, 1 = a check failed.
/// </summary>
public static class FleetInviteCheck
{
    private const int Inviter = 1001;   // fleet creator
    private const int Invitee = 2002;
    private const int Stranger = 3003;
    private const int Invitee2 = 5005;

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        Console.WriteLine("== EVE-Utils invite round-trip check ==");

        using var scope = services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var repository = scope.ServiceProvider.GetRequiredService<EveUtils.Shared.Modules.Fleet.Repositories.IFleetRepository>();
        var ct = CancellationToken.None;
        var ok = true;

        // 0. A fleet to invite into.
        var created = await dispatcher.Send(new CreateFleetCommand(
            "Recruit Fleet", null, FleetVisibility.InviteOnly, null, null, FleetOfflineBehavior.StayOffline, Inviter), ct);
        ok &= Check("create fleet", created.IsSuccess);
        var fleetId = created.Value;

        // 1. Creator invites a character.
        var invite = await dispatcher.Send(new CreateFleetInviteCommand(
            fleetId, Invitee, FleetRole.SquadMember, null, null, null, Inviter), ct);
        ok &= Check("creator invites → Pending invite created", invite.IsSuccess && invite.Value!.InviteId > 0);
        ok &= Check("invite payload targets the invitee + carries fleet name",
            invite.Value!.InviteeCharacterId == Invitee && invite.Value!.FleetName == "Recruit Fleet");
        var inviteId = invite.Value!.InviteId;

        // 2. A non-creator cannot invite.
        var foreign = await dispatcher.Send(new CreateFleetInviteCommand(fleetId, 4004, FleetRole.SquadMember, null, null, null, Stranger), ct);
        ok &= Check("non-creator invite rejected (PERMISSION_DENIED)",
            !foreign.IsSuccess && foreign.Messages.Any(m => m.Code == MessageCodes.PermissionDenied));

        // 3. Self-invite + duplicate invite are refused.
        var self = await dispatcher.Send(new CreateFleetInviteCommand(fleetId, Inviter, FleetRole.SquadMember, null, null, null, Inviter), ct);
        ok &= Check("self-invite rejected (VALIDATION_FAILED)",
            !self.IsSuccess && self.Messages.Any(m => m.Code == MessageCodes.ValidationFailed));
        var dup = await dispatcher.Send(new CreateFleetInviteCommand(fleetId, Invitee, FleetRole.SquadMember, null, null, null, Inviter), ct);
        ok &= Check("duplicate pending invite rejected", !dup.IsSuccess);

        // 4. The invite shows up in the invitee's pending list (the durable vangnet).
        var pending = await dispatcher.Query(new ListPendingInvitesQuery(Invitee), ct);
        ok &= Check("invitee's pending list contains the invite", pending.Any(i => i.Id == inviteId));

        // 5. Targeted delivery: the FleetInviteEvent reaches ONLY the invitee's connection.
        ok &= await CheckTargetedDeliveryAsync(invite.Value!, ct);

        // 6. Only the invitee may respond.
        var wrongResponder = await dispatcher.Send(new RespondToFleetInviteCommand(inviteId, true, Stranger), ct);
        ok &= Check("non-invitee response rejected (PERMISSION_DENIED)",
            !wrongResponder.IsSuccess && wrongResponder.Messages.Any(m => m.Code == MessageCodes.PermissionDenied));

        // 7. Accept → invitee joins the roster, with the invited role.
        var accept = await dispatcher.Send(new RespondToFleetInviteCommand(inviteId, true, Invitee), ct);
        ok &= Check("invitee accepts", accept.IsSuccess && accept.Value!.Accepted);
        var members = await repository.ListMembersAsync(fleetId, ct);
        ok &= Check("accepted invitee is on the roster with the invited role",
            members.Any(m => m.CharacterId == Invitee && m.Role == FleetRole.SquadMember));

        // 8. A second response is refused; the invite no longer appears as pending.
        var again = await dispatcher.Send(new RespondToFleetInviteCommand(inviteId, false, Invitee), ct);
        ok &= Check("double-response rejected (VALIDATION_FAILED)",
            !again.IsSuccess && again.Messages.Any(m => m.Code == MessageCodes.ValidationFailed));
        var pendingAfter = await dispatcher.Query(new ListPendingInvitesQuery(Invitee), ct);
        ok &= Check("accepted invite no longer pending", pendingAfter.All(i => i.Id != inviteId));

        // 9. Deny path: a denied invitee does NOT join.
        var invite2 = await dispatcher.Send(new CreateFleetInviteCommand(fleetId, Invitee2, FleetRole.SquadMember, null, null, null, Inviter), ct);
        ok &= Check("second invite created", invite2.IsSuccess);
        var deny = await dispatcher.Send(new RespondToFleetInviteCommand(invite2.Value!.InviteId, false, Invitee2), ct);
        ok &= Check("invitee denies", deny.IsSuccess && !deny.Value!.Accepted);
        var membersAfterDeny = await repository.ListMembersAsync(fleetId, ct);
        ok &= Check("denied invitee did NOT join", membersAfterDeny.All(m => m.CharacterId != Invitee2));

        // 10. Responding to an unknown invite → NOT_FOUND.
        var ghost = await dispatcher.Send(new RespondToFleetInviteCommand(999_999_999, true, Invitee), ct);
        ok &= Check("respond to unknown invite → NOT_FOUND",
            !ghost.IsSuccess && ghost.Messages.Any(m => m.Code == MessageCodes.NotFound));

        await CleanupAsync(scope.ServiceProvider, fleetId, ct);

        Console.WriteLine(ok ? "RESULT: PASS ✓" : "RESULT: FAIL ✗");
        return ok ? 0 : 1;
    }

    private static async Task<bool> CheckTargetedDeliveryAsync(FleetInvitePayload payload, CancellationToken ct)
    {
        var clients = new ConnectedClients();
        var inviteeWriter = new RecordingWriter();
        var inviterWriter = new RecordingWriter();
        var strangerWriter = new RecordingWriter();
        clients.Add(new ConnectedClient("invitee", Invitee, "Invitee", inviteeWriter));
        clients.Add(new ConnectedClient("inviter", Inviter, "Inviter", inviterWriter));
        clients.Add(new ConnectedClient("stranger", Stranger, "Stranger", strangerWriter));

        var evt = new FleetInviteEvent(payload, payload.InviterCharacterId);
        await clients.SendToCharacterAsync(evt.TargetCharacterId, WireEnvelopeFactory.ToEnvelope(evt), ct);

        var ok = Check("targeted invite → invitee received", inviteeWriter.Count == 1);
        ok &= Check("targeted invite → inviter NOT received", inviterWriter.Count == 0);
        ok &= Check("targeted invite → stranger NOT received", strangerWriter.Count == 0);
        return ok;
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
