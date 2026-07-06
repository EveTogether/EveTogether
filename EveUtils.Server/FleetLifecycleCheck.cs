using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Data;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;

namespace EveUtils.Server;

/// <summary>
/// Headless proof for the fleet lifecycle, runnable via <c>--fleet-test</c>. Drives the
/// real DI container (so it also proves the Scrutor auto-registration resolves) and the dispatcher through a
/// create → read → edit → reject-non-creator → list → disband scenario against the dev DB. Exit code 0 = all
/// checks passed, 1 = a check failed.
/// </summary>
public static class FleetLifecycleCheck
{
    private const int Creator = 1001;
    private const int Other = 2002;

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        Console.WriteLine("== EVE-Utils fleet-lifecycle check (CQRS + permission) ==");

        using var scope = services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var ct = CancellationToken.None;
        var ok = true;

        // 1. Create (as Creator).
        var created = await dispatcher.Send(new CreateFleetCommand(
            "Alpha Fleet", "first", FleetVisibility.InviteOnly, null, null, FleetOfflineBehavior.StayOffline, Creator), ct);
        ok &= Check("create succeeds", created.IsSuccess && created.Value > 0);
        var id = created.Value;

        // 2. Read back.
        var fleet = await dispatcher.Query(new GetFleetQuery(id), ct);
        ok &= Check("read back: owned by creator + Active", fleet is { CreatorCharacterId: Creator, State: FleetState.Active });

        // 3. Edit by creator.
        var edit = await dispatcher.Send(new EditFleetCommand(
            id, "Alpha Reformed", "renamed", FleetVisibility.Public, null, null, FleetOfflineBehavior.AutoLeave, Creator), ct);
        ok &= Check("creator can edit", edit.IsSuccess);
        var afterEdit = await dispatcher.Query(new GetFleetQuery(id), ct);
        ok &= Check("edit persisted (name + visibility)", afterEdit is { Name: "Alpha Reformed", Visibility: FleetVisibility.Public });

        // 4. Edit by a non-creator is rejected.
        var foreignEdit = await dispatcher.Send(new EditFleetCommand(
            id, "Hijacked", null, FleetVisibility.Public, null, null, FleetOfflineBehavior.StayOffline, Other), ct);
        ok &= Check("non-creator edit rejected (PERMISSION_DENIED)",
            !foreignEdit.IsSuccess && foreignEdit.Messages.Any(m => m.Code == MessageCodes.PermissionDenied));

        // 5. List the creator's fleets.
        var mine = await dispatcher.Query(new ListFleetsByCreatorQuery(Creator), ct);
        ok &= Check("list-by-creator contains the fleet", mine.Any(f => f.Id == id));

        // 6. Disband by a non-creator is rejected.
        var foreignDisband = await dispatcher.Send(new DisbandFleetCommand(id, Other), ct);
        ok &= Check("non-creator disband rejected", !foreignDisband.IsSuccess);

        // 7. Disband by creator → Archived.
        var disband = await dispatcher.Send(new DisbandFleetCommand(id, Creator), ct);
        ok &= Check("creator can disband", disband.IsSuccess);
        var afterDisband = await dispatcher.Query(new GetFleetQuery(id), ct);
        ok &= Check("disband soft-deletes (Archived)", afterDisband is { State: FleetState.Archived });

        // 8. Missing fleet → NOT_FOUND.
        var missing = await dispatcher.Send(new EditFleetCommand(
            999_999_999, "ghost", null, FleetVisibility.Public, null, null, FleetOfflineBehavior.StayOffline, Creator), ct);
        ok &= Check("unknown fleet → NOT_FOUND",
            !missing.IsSuccess && missing.Messages.Any(m => m.Code == MessageCodes.NotFound));

        await CleanupAsync(scope.ServiceProvider, id, ct);

        Console.WriteLine(ok ? "RESULT: PASS ✓" : "RESULT: FAIL ✗");
        return ok ? 0 : 1;
    }

    private static async Task CleanupAsync(IServiceProvider provider, long fleetId, CancellationToken ct)
    {
        await using var db = await provider.GetRequiredService<IDbContextFactory<ServerDbContext>>().CreateDbContextAsync(ct);
        var fleet = await db.Set<FleetEntity>().FirstOrDefaultAsync(f => f.Id == fleetId, ct);
        if (fleet is not null)
        {
            db.Remove(fleet);
            await db.SaveChangesAsync(ct);
        }
    }

    private static bool Check(string label, bool pass)
    {
        Console.WriteLine($"  {(pass ? "✓" : "✗ FAIL")}  {label}");
        return pass;
    }
}
