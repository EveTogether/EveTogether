using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Fleet;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Server;

/// <summary>
/// Headless proof for auto-squad-on-join (2026-06-04), runnable via <c>--fleet-autoplace-test</c>. A new fleet ships
/// with one default Wing 1 / Squad 1; the first <see cref="FleetStructureLimits.MaxMembersPerSquad"/> joiners fill it,
/// and the next joiner must auto-create "Squad 2" in the same wing and land there (instead of the old -1/-1
/// "unassigned" fallback). Exit 0 = all passed.
/// </summary>
public static class FleetAutoPlaceCheck
{
    private const int Owner = 8100;

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        Console.WriteLine("== EVE-Utils auto-squad-on-join check ==");
        using var scope = services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var repository = scope.ServiceProvider.GetRequiredService<IFleetRepository>();
        var ct = CancellationToken.None;
        var ok = true;
        long fleetId = 0;

        try
        {
            fleetId = (await dispatcher.Send(new CreateFleetCommand(
                "Auto-place Op", null, FleetVisibility.Public, null, null, FleetOfflineBehavior.StayOffline, Owner), ct)).Value;

            var wings = await repository.ListWingsAsync(fleetId, ct);
            ok &= Check("new fleet ships with one default wing", wings.Count == 1);
            var squadsBefore = await repository.ListSquadsAsync(wings[0].Id, ct);
            ok &= Check("the default wing has one default squad", squadsBefore.Count == 1);

            // Fill the default squad to capacity (the creator is FC at -1/-1, so all 10 seats are for joiners).
            var cap = FleetStructureLimits.MaxMembersPerSquad;
            for (var i = 0; i < cap; i++)
                ok &= (await dispatcher.Send(new JoinFleetCommand(fleetId, 8200 + i), ct)).IsSuccess;

            var firstSquad = (await repository.ListSquadsAsync(wings[0].Id, ct))[0];
            var inFirst = (await repository.ListMembersAsync(fleetId, ct)).Count(m => m.SquadId == firstSquad.Id);
            ok &= Check($"the first {cap} joiners fill Squad 1", inFirst == cap);
            ok &= Check("Squad 1 is full but no extra squad has been created yet",
                (await repository.ListSquadsAsync(wings[0].Id, ct)).Count == 1);

            // The next joiner must auto-create a second squad in the same wing and land there.
            ok &= (await dispatcher.Send(new JoinFleetCommand(fleetId, 8300), ct)).IsSuccess;
            var squadsAfter = await repository.ListSquadsAsync(wings[0].Id, ct);
            ok &= Check("the overflow joiner auto-created a second squad", squadsAfter.Count == 2);
            ok &= Check("the new squad is named 'Squad 2'", squadsAfter[1].Name == "Squad 2");
            var overflow = (await repository.ListMembersAsync(fleetId, ct)).First(m => m.CharacterId == 8300);
            ok &= Check("the overflow joiner landed in the new squad (not unassigned)", overflow.SquadId == squadsAfter[1].Id);
        }
        finally
        {
            if (fleetId != 0)
            {
                await using var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<ServerDbContext>>().CreateDbContextAsync(ct);
                var fleet = await db.Set<FleetEntity>().FirstOrDefaultAsync(f => f.Id == fleetId, ct);
                if (fleet is not null)
                    db.Remove(fleet); // cascade removes wings/squads/members
                await db.SaveChangesAsync(ct);
            }
        }

        Console.WriteLine(ok ? "RESULT: PASS ✓" : "RESULT: FAIL ✗");
        return ok ? 0 : 1;
    }

    private static bool Check(string label, bool pass)
    {
        Console.WriteLine($"  {(pass ? "✓" : "✗ FAIL")}  {label}");
        return pass;
    }
}
