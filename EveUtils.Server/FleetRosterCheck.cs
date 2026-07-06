using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Server;

/// <summary>
/// Headless proof for roster management, runnable via <c>--fleet-roster-test</c>. Drives
/// the real DI container + dispatcher through: creator-as-member, the EVE wing/squad limits, and member-move
/// (ESI position rules, squad capacity, command-slot uniqueness, creator-gate). Exit 0 = pass, 1 = fail.
/// </summary>
public static class FleetRosterCheck
{
    private const int Creator = 1001;
    private const int Mover = 2002;
    private const int Other = 3003;
    private const int Filler = 6006;

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        Console.WriteLine("== EVE-Utils fleet-roster check (creator-member, EVE limits, member-move) ==");

        using var scope = services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var repository = scope.ServiceProvider.GetRequiredService<IFleetRepository>();
        var ct = CancellationToken.None;
        var ok = true;

        // 0. Create a public fleet; the creator must be a default-accepted FleetCommander on the roster.
        var created = await dispatcher.Send(new CreateFleetCommand(
            "Roster Fleet", null, FleetVisibility.Public, null, null, FleetOfflineBehavior.StayOffline, Creator), ct);
        ok &= Check("create fleet", created.IsSuccess && created.Value > 0);
        var fleetId = created.Value;

        var roster0 = await repository.ListMembersAsync(fleetId, ct);
        ok &= Check("creator is a default FleetCommander member",
            roster0.Any(m => m.CharacterId == Creator && m.Role == FleetRole.FleetCommander && m.WingId == -1 && m.SquadId == -1));

        // a new fleet ships with one default wing holding one default squad.
        var seedWings = await repository.ListWingsAsync(fleetId, ct);
        ok &= Check("new fleet has a default Wing 1", seedWings.Count == 1 && seedWings[0].Name == "Wing 1");
        var defaultWingId = seedWings.Count == 1 ? seedWings[0].Id : 0;
        var seedSquads = await repository.ListSquadsAsync(defaultWingId, ct);
        ok &= Check("the default wing holds a default Squad 1", seedSquads.Count == 1 && seedSquads[0].Name == "Squad 1");
        var defaultSquadId = seedSquads.Count == 1 ? seedSquads[0].Id : 0;

        // 1. Wing limit (counting the default Wing 1): 4 more reach the cap of 5, the next is rejected.
        long firstWingId = 0;
        for (var i = 2; i <= FleetStructureLimits.MaxWingsPerFleet; i++)
        {
            var w = await dispatcher.Send(new CreateWingCommand(fleetId, $"Wing {i}", Creator), ct);
            ok &= Check($"create wing {i}/{FleetStructureLimits.MaxWingsPerFleet}", w.IsSuccess);
            if (i == 2) firstWingId = w.Value;
        }
        var overWing = await dispatcher.Send(new CreateWingCommand(fleetId, "Wing 6", Creator), ct);
        ok &= Check("wing past the cap rejected (VALIDATION_FAILED)",
            !overWing.IsSuccess && overWing.Messages.Any(m => m.Code == MessageCodes.ValidationFailed));

        // 2. Squad limit: 5 squads in a wing succeed, the 6th is rejected.
        long firstSquadId = 0;
        for (var i = 1; i <= FleetStructureLimits.MaxSquadsPerWing; i++)
        {
            var s = await dispatcher.Send(new CreateSquadCommand(firstWingId, $"Squad {i}", Creator), ct);
            ok &= Check($"create squad {i}/{FleetStructureLimits.MaxSquadsPerWing}", s.IsSuccess);
            if (i == 1) firstSquadId = s.Value;
        }
        var overSquad = await dispatcher.Send(new CreateSquadCommand(firstWingId, "Squad 6", Creator), ct);
        ok &= Check("6th squad rejected (VALIDATION_FAILED)",
            !overSquad.IsSuccess && overSquad.Messages.Any(m => m.Code == MessageCodes.ValidationFailed));

        // 3. A member to move: self-join (public) → on the roster, auto-placed into the first open squad
        //    (the default Squad 1, which has room), not the -1/-1 sentinel.
        ok &= Check("mover joins the fleet", (await dispatcher.Send(new JoinFleetCommand(fleetId, Mover), ct)).IsSuccess);
        var rosterAfterJoin = await repository.ListMembersAsync(fleetId, ct);
        var mover = rosterAfterJoin.FirstOrDefault(m => m.CharacterId == Mover);
        ok &= Check("mover is on the roster", mover is not null);
        ok &= Check("mover auto-placed into the default Squad 1 (not -1/-1)",
            mover is not null && mover.WingId == defaultWingId && mover.SquadId == defaultSquadId);
        var moverId = mover!.Id;

        // 4. Valid move: into wing1/squad1 as SquadMember.
        var move = await dispatcher.Send(new MoveMemberCommand(moverId, FleetRole.SquadMember, firstWingId, firstSquadId, Creator), ct);
        ok &= Check("move mover → wing1/squad1 as SquadMember", move.IsSuccess);
        var movedBack = await repository.GetMemberAsync(moverId, ct);
        ok &= Check("move persisted (wing+squad+role)",
            movedBack is { WingId: var w1, SquadId: var s1, Role: FleetRole.SquadMember } && w1 == firstWingId && s1 == firstSquadId);

        // 5. Invalid position: SquadMember without a squad is rejected.
        var badPos = await dispatcher.Send(new MoveMemberCommand(moverId, FleetRole.SquadMember, firstWingId, -1, Creator), ct);
        ok &= Check("SquadMember without squad rejected (VALIDATION_FAILED)",
            !badPos.IsSuccess && badPos.Messages.Any(m => m.Code == MessageCodes.ValidationFailed));

        // 6. Referential integrity: an unknown wing is rejected.
        var ghostWing = await dispatcher.Send(new MoveMemberCommand(moverId, FleetRole.WingCommander, 999_999_999, -1, Creator), ct);
        ok &= Check("move into unknown wing rejected (VALIDATION_FAILED)",
            !ghostWing.IsSuccess && ghostWing.Messages.Any(m => m.Code == MessageCodes.ValidationFailed));

        // 7. Command-slot uniqueness: a second FleetCommander is rejected (the creator already holds it).
        var secondFc = await dispatcher.Send(new MoveMemberCommand(moverId, FleetRole.FleetCommander, -1, -1, Creator), ct);
        ok &= Check("second FleetCommander rejected (slot filled)",
            !secondFc.IsSuccess && secondFc.Messages.Any(m => m.Code == MessageCodes.ValidationFailed));

        // 8. Creator-gate: a non-creator cannot move members.
        var foreign = await dispatcher.Send(new MoveMemberCommand(moverId, FleetRole.SquadMember, firstWingId, firstSquadId, Other), ct);
        ok &= Check("non-creator move rejected (PERMISSION_DENIED)",
            !foreign.IsSuccess && foreign.Messages.Any(m => m.Code == MessageCodes.PermissionDenied));

        // 9. Squad capacity: fill squad1 to 10, then an 11th move in is rejected.
        var now = DateTimeOffset.UtcNow;
        var seeded = await repository.ListMembersAsync(fleetId, ct);
        var inSquad = seeded.Count(m => m.SquadId == firstSquadId); // mover is already there (1)
        for (var i = inSquad; i < FleetStructureLimits.MaxMembersPerSquad; i++)
        {
            await repository.AddMemberAsync(new FleetMember
            {
                FleetId = fleetId,
                CharacterId = 9000 + i,
                Role = FleetRole.SquadMember,
                WingId = firstWingId,
                SquadId = firstSquadId,
                JoinTime = now
            }, ct);
        }
        ok &= Check("filler joins the fleet", (await dispatcher.Send(new JoinFleetCommand(fleetId, Filler), ct)).IsSuccess);
        var fillerMember = (await repository.ListMembersAsync(fleetId, ct)).First(m => m.CharacterId == Filler);
        var overCapacity = await dispatcher.Send(new MoveMemberCommand(fillerMember.Id, FleetRole.SquadMember, firstWingId, firstSquadId, Creator), ct);
        ok &= Check("11th member into a full squad rejected (capacity)",
            !overCapacity.IsSuccess && overCapacity.Messages.Any(m => m.Code == MessageCodes.ValidationFailed));

        // 10. Unknown member → NOT_FOUND.
        var ghostMember = await dispatcher.Send(new MoveMemberCommand(999_999_999, FleetRole.SquadMember, firstWingId, firstSquadId, Creator), ct);
        ok &= Check("move unknown member → NOT_FOUND",
            !ghostMember.IsSuccess && ghostMember.Messages.Any(m => m.Code == MessageCodes.NotFound));

        // 11. Unassign (R3-5): move a member to fleet level with no position. "Remove from composition" keeps the
        //     member in the fleet (still on the roster), just out of the wing/squad structure.
        var unassign = await dispatcher.Send(new MoveMemberCommand(moverId, FleetRole.Unassigned, -1, -1, Creator), ct);
        ok &= Check("unassign member to fleet level (Unassigned, -1/-1)", unassign.IsSuccess);
        var unassigned = await repository.GetMemberAsync(moverId, ct);
        ok &= Check("unassigned member stays in the fleet at -1/-1",
            unassigned is { Role: FleetRole.Unassigned, WingId: -1, SquadId: -1 });

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
