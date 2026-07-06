using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Server;

/// <summary>
/// Headless proof for ownership-transfer + member-removal, runnable via <c>--fleet-ownership-test</c>.
/// Drives the real DI container + dispatcher through: a new fleet's default wing/squad, a joiner's
/// auto-placement, creator-only ownership transfer (and its rejections), member removal, and the
/// creator-leave block before/after a transfer. Exit 0 = pass, 1 = fail.
/// </summary>
public static class FleetOwnershipCheck
{
    private const int Creator = 1001;
    private const int Member = 2002;
    private const int Outsider = 3003;

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        Console.WriteLine("== EVE-Utils ownership-transfer + member-removal check ==");

        using var scope = services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var repository = scope.ServiceProvider.GetRequiredService<IFleetRepository>();
        var ct = CancellationToken.None;
        var ok = true;

        // 0. Create a public fleet — it ships with a default Wing 1 + Squad 1.
        var created = await dispatcher.Send(new CreateFleetCommand(
            "Ownership Fleet", null, FleetVisibility.Public, null, null, FleetOfflineBehavior.StayOffline, Creator), ct);
        ok &= Check("create fleet", created.IsSuccess && created.Value > 0);
        var fleetId = created.Value;

        var wings = await repository.ListWingsAsync(fleetId, ct);
        ok &= Check("new fleet has a default Wing 1", wings.Count == 1 && wings[0].Name == "Wing 1");
        var defaultWingId = wings.Count == 1 ? wings[0].Id : 0;
        var squads = await repository.ListSquadsAsync(defaultWingId, ct);
        ok &= Check("the default wing holds a default Squad 1", squads.Count == 1 && squads[0].Name == "Squad 1");
        var defaultSquadId = squads.Count == 1 ? squads[0].Id : 0;

        // 1. A member joins → auto-placed into the first open squad, not the -1/-1 sentinel.
        ok &= Check("member joins the fleet", (await dispatcher.Send(new JoinFleetCommand(fleetId, Member), ct)).IsSuccess);
        var roster = await repository.ListMembersAsync(fleetId, ct);
        var member = roster.FirstOrDefault(m => m.CharacterId == Member);
        ok &= Check("joiner is on the roster", member is not null);
        ok &= Check("joiner auto-placed into the default Squad 1 (not -1/-1)",
            member is not null && member.WingId == defaultWingId && member.SquadId == defaultSquadId);

        // 2. Transfer rejections BEFORE a valid transfer.
        var foreignTransfer = await dispatcher.Send(new TransferFleetOwnershipCommand(fleetId, Member, Outsider), ct);
        ok &= Check("non-creator transfer rejected (PERMISSION_DENIED)",
            !foreignTransfer.IsSuccess && foreignTransfer.Messages.Any(m => m.Code == MessageCodes.PermissionDenied));

        var nonMemberTransfer = await dispatcher.Send(new TransferFleetOwnershipCommand(fleetId, Outsider, Creator), ct);
        ok &= Check("transfer to a non-member rejected (VALIDATION_FAILED)",
            !nonMemberTransfer.IsSuccess && nonMemberTransfer.Messages.Any(m => m.Code == MessageCodes.ValidationFailed));

        // 3. Creator can't be removed while still the owner — by the creator themselves or by anyone.
        var creatorMember = roster.First(m => m.CharacterId == Creator);
        var blockedLeave = await dispatcher.Send(new RemoveFleetMemberCommand(creatorMember.Id, Creator), ct);
        ok &= Check("creator-leave blocked before transfer (VALIDATION_FAILED)",
            !blockedLeave.IsSuccess && blockedLeave.Messages.Any(m => m.Code == MessageCodes.ValidationFailed));
        ok &= Check("the block message names the transfer requirement",
            blockedLeave.Messages.Any(m => m.Text.Contains("Transfer ownership")));

        // 4. A plain member may remove themselves; an outsider may not remove someone else.
        var foreignRemove = await dispatcher.Send(new RemoveFleetMemberCommand(member!.Id, Outsider), ct);
        ok &= Check("outsider removing a member rejected (PERMISSION_DENIED)",
            !foreignRemove.IsSuccess && foreignRemove.Messages.Any(m => m.Code == MessageCodes.PermissionDenied));

        // 5. Valid transfer: ownership moves to the member; the old owner stays on the roster.
        var transfer = await dispatcher.Send(new TransferFleetOwnershipCommand(fleetId, Member, Creator), ct);
        ok &= Check("creator transfers ownership to the member", transfer.IsSuccess);
        var afterTransfer = await repository.GetAsync(fleetId, ct);
        ok &= Check("CreatorCharacterId moved to the new owner", afterTransfer?.CreatorCharacterId == Member);
        ok &= Check("the old owner is still a roster member",
            (await repository.ListMembersAsync(fleetId, ct)).Any(m => m.CharacterId == Creator));

        // 6. After the transfer, the former owner (no longer creator) may leave; the new owner removes them.
        var oldOwner = (await repository.ListMembersAsync(fleetId, ct)).First(m => m.CharacterId == Creator);
        var ownerRemovesOld = await dispatcher.Send(new RemoveFleetMemberCommand(oldOwner.Id, Member), ct);
        ok &= Check("new owner removes the former owner (now a plain member)", ownerRemovesOld.IsSuccess);
        ok &= Check("the former owner is off the roster",
            (await repository.ListMembersAsync(fleetId, ct)).All(m => m.CharacterId != Creator));

        // 7. Unknown member → NOT_FOUND.
        var ghost = await dispatcher.Send(new RemoveFleetMemberCommand(999_999_999, Member), ct);
        ok &= Check("remove unknown member → NOT_FOUND",
            !ghost.IsSuccess && ghost.Messages.Any(m => m.Code == MessageCodes.NotFound));

        await CleanupAsync(scope.ServiceProvider, fleetId, ct);

        Console.WriteLine(ok ? "RESULT: PASS ✓" : "RESULT: FAIL ✗");
        return ok ? 0 : 1;
    }

    private static async Task CleanupAsync(IServiceProvider provider, long fleetId, CancellationToken ct)
    {
        await using var db = await provider.GetRequiredService<IDbContextFactory<ServerDbContext>>().CreateDbContextAsync(ct);
        var fleet = await db.Set<FleetEntity>().FirstOrDefaultAsync(f => f.Id == fleetId, ct);
        if (fleet is not null)
        {
            db.Remove(fleet); // cascade removes wings/squads/members
            await db.SaveChangesAsync(ct);
        }
    }

    private static bool Check(string label, bool pass)
    {
        Console.WriteLine($"  {(pass ? "✓" : "✗ FAIL")}  {label}");
        return pass;
    }
}
