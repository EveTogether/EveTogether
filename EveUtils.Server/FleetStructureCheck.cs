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
/// Headless proof for the wing/squad structure, runnable via <c>--fleet-structure-test</c>.
/// Drives the real DI container + dispatcher through a create-fleet → wings/squads CRUD → cascade-delete →
/// reject scenario against the dev DB. Exit code 0 = all checks passed, 1 = a check failed.
/// </summary>
public static class FleetStructureCheck
{
    private const int Creator = 1001;
    private const int Other = 2002;

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        Console.WriteLine("== EVE-Utils fleet-structure check (wings/squads CRUD, creator-gated) ==");

        using var scope = services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var ct = CancellationToken.None;
        var ok = true;

        // 0. A fleet to hang structure on.
        var created = await dispatcher.Send(new CreateFleetCommand(
            "Structure Fleet", null, FleetVisibility.InviteOnly, null, null, FleetOfflineBehavior.StayOffline, Creator), ct);
        ok &= Check("create fleet", created.IsSuccess && created.Value > 0);
        var fleetId = created.Value;

        // 1. Create a wing.
        var wing = await dispatcher.Send(new CreateWingCommand(fleetId, "Wing A", Creator), ct);
        ok &= Check("creator creates wing", wing.IsSuccess && wing.Value > 0);
        var wingId = wing.Value;

        // 2. Rename the wing + read back.
        var renameWing = await dispatcher.Send(new RenameWingCommand(wingId, "Wing Alpha", Creator), ct);
        ok &= Check("creator renames wing", renameWing.IsSuccess);
        var wings = await dispatcher.Query(new ListWingsQuery(fleetId), ct);
        ok &= Check("list-wings shows the rename", wings.Any(w => w.Id == wingId && w.Name == "Wing Alpha"));

        // 3. Create + rename a squad in that wing.
        var squad = await dispatcher.Send(new CreateSquadCommand(wingId, "Squad 1", Creator), ct);
        ok &= Check("creator creates squad", squad.IsSuccess && squad.Value > 0);
        var squadId = squad.Value;
        var renameSquad = await dispatcher.Send(new RenameSquadCommand(squadId, "Squad One", Creator), ct);
        ok &= Check("creator renames squad", renameSquad.IsSuccess);
        var squads = await dispatcher.Query(new ListSquadsQuery(wingId), ct);
        ok &= Check("list-squads shows the squad", squads.Any(s => s.Id == squadId && s.Name == "Squad One"));

        // 4. A non-creator cannot touch the structure.
        var foreignWing = await dispatcher.Send(new CreateWingCommand(fleetId, "Hostile Wing", Other), ct);
        ok &= Check("non-creator create-wing rejected (PERMISSION_DENIED)",
            !foreignWing.IsSuccess && foreignWing.Messages.Any(m => m.Code == MessageCodes.PermissionDenied));
        var foreignRename = await dispatcher.Send(new RenameSquadCommand(squadId, "Hijacked", Other), ct);
        ok &= Check("non-creator rename-squad rejected", !foreignRename.IsSuccess);

        // 5. Validation: empty name is rejected.
        var blankWing = await dispatcher.Send(new CreateWingCommand(fleetId, "  ", Creator), ct);
        ok &= Check("blank wing name rejected (VALIDATION_FAILED)",
            !blankWing.IsSuccess && blankWing.Messages.Any(m => m.Code == MessageCodes.ValidationFailed));

        // 6. Unknown targets resolve to NOT_FOUND.
        var ghostWing = await dispatcher.Send(new RenameWingCommand(999_999_999, "ghost", Creator), ct);
        ok &= Check("rename unknown wing → NOT_FOUND",
            !ghostWing.IsSuccess && ghostWing.Messages.Any(m => m.Code == MessageCodes.NotFound));
        var ghostSquadParent = await dispatcher.Send(new CreateSquadCommand(999_999_999, "x", Creator), ct);
        ok &= Check("create squad under unknown wing → NOT_FOUND",
            !ghostSquadParent.IsSuccess && ghostSquadParent.Messages.Any(m => m.Code == MessageCodes.NotFound));

        // 7. Deleting the wing cascades its squads away.
        var deleteWing = await dispatcher.Send(new DeleteWingCommand(wingId, Creator), ct);
        ok &= Check("creator deletes wing", deleteWing.IsSuccess);
        var wingsAfter = await dispatcher.Query(new ListWingsQuery(fleetId), ct);
        ok &= Check("wing gone after delete", wingsAfter.All(w => w.Id != wingId));
        var squadsAfter = await dispatcher.Query(new ListSquadsQuery(wingId), ct);
        ok &= Check("squads cascade-deleted with the wing", squadsAfter.Count == 0);

        // 8. Once the fleet is archived, structure ops are refused.
        var disband = await dispatcher.Send(new DisbandFleetCommand(fleetId, Creator), ct);
        ok &= Check("disband fleet", disband.IsSuccess);
        var onArchived = await dispatcher.Send(new CreateWingCommand(fleetId, "Too Late", Creator), ct);
        ok &= Check("structure op on archived fleet rejected (VALIDATION_FAILED)",
            !onArchived.IsSuccess && onArchived.Messages.Any(m => m.Code == MessageCodes.ValidationFailed));

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
            db.Remove(fleet); // cascade removes any remaining wings/squads
            await db.SaveChangesAsync(ct);
        }
    }

    private static bool Check(string label, bool pass)
    {
        Console.WriteLine($"  {(pass ? "✓" : "✗ FAIL")}  {label}");
        return pass;
    }
}
