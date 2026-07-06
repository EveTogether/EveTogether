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
/// Headless proof for the external-member flow, runnable via <c>--fleet-external-test</c>. Drives the
/// real DI container + dispatcher through the command/roster path of adding a session-less character on trust:
/// it lands as an external SquadMember (-1/-1), the creator-gate and idempotency hold, and archived / unknown
/// fleets are rejected cleanly. The public-ESI lookup needs the network, so it is exercised separately, not here.
/// Exit 0 = pass, 1 = fail.
/// </summary>
public static class FleetExternalCheck
{
    private const int Creator = 1001;
    private const int Outsider = 2002;
    private const int External = 7007;
    private const int SecondExternal = 8008;

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        Console.WriteLine("== EVE-Utils fleet-external check (add session-less member on trust) ==");

        using var scope = services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var repository = scope.ServiceProvider.GetRequiredService<IFleetRepository>();
        var ct = CancellationToken.None;
        var ok = true;

        // 0. A fleet to add externals into.
        var created = await dispatcher.Send(new CreateFleetCommand(
            "External Fleet", null, FleetVisibility.InviteOnly, null, null, FleetOfflineBehavior.StayOffline, Creator), ct);
        ok &= Check("create fleet", created.IsSuccess && created.Value > 0);
        var fleetId = created.Value;

        // 1. Owner adds an external character → success, returns a member id.
        var add = await dispatcher.Send(new AddExternalMemberCommand(fleetId, External, Creator), ct);
        ok &= Check("owner adds external member (success)", add.IsSuccess && add.Value > 0);

        // 2. It is on the roster as an external SquadMember, unassigned (-1/-1).
        var roster = await repository.ListMembersAsync(fleetId, ct);
        var ext = roster.FirstOrDefault(m => m.CharacterId == External);
        ok &= Check("external on roster: IsExternal=true, SquadMember, -1/-1",
            ext is { IsExternal: true, Role: FleetRole.SquadMember, WingId: -1, SquadId: -1 });

        // 3. Idempotency: adding the same character again is rejected (already a member, VALIDATION_FAILED).
        var dup = await dispatcher.Send(new AddExternalMemberCommand(fleetId, External, Creator), ct);
        ok &= Check("re-adding the same character rejected (VALIDATION_FAILED)",
            !dup.IsSuccess && dup.Messages.Any(m => m.Code == MessageCodes.ValidationFailed));
        var rosterAfterDup = await repository.ListMembersAsync(fleetId, ct);
        ok &= Check("no duplicate roster row", rosterAfterDup.Count(m => m.CharacterId == External) == 1);

        // 4. Creator-gate: a non-creator cannot add externals (PERMISSION_DENIED).
        var foreign = await dispatcher.Send(new AddExternalMemberCommand(fleetId, SecondExternal, Outsider), ct);
        ok &= Check("non-creator add rejected (PERMISSION_DENIED)",
            !foreign.IsSuccess && foreign.Messages.Any(m => m.Code == MessageCodes.PermissionDenied));
        ok &= Check("rejected add did not touch the roster",
            (await repository.ListMembersAsync(fleetId, ct)).All(m => m.CharacterId != SecondExternal));

        // 5. Unknown fleet → NOT_FOUND.
        var ghost = await dispatcher.Send(new AddExternalMemberCommand(999_999_999, External, Creator), ct);
        ok &= Check("add into unknown fleet → NOT_FOUND",
            !ghost.IsSuccess && ghost.Messages.Any(m => m.Code == MessageCodes.NotFound));

        // 6. Archived fleet → rejected (VALIDATION_FAILED, the guard's archived check).
        ok &= Check("disband the fleet", (await dispatcher.Send(new DisbandFleetCommand(fleetId, Creator), ct)).IsSuccess);
        var archived = await dispatcher.Send(new AddExternalMemberCommand(fleetId, SecondExternal, Creator), ct);
        ok &= Check("add into archived fleet rejected (VALIDATION_FAILED)",
            !archived.IsSuccess && archived.Messages.Any(m => m.Code == MessageCodes.ValidationFailed));

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
            db.Remove(fleet); // cascade removes members
            await db.SaveChangesAsync(ct);
        }
    }

    private static bool Check(string label, bool pass)
    {
        Console.WriteLine($"  {(pass ? "✓" : "✗ FAIL")}  {label}");
        return pass;
    }
}
