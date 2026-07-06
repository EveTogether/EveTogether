using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Fleet;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Composition;

/// <summary>
/// Headless proof for client-only fleets, runnable via <c>--client-fleet-test</c>. Drives the real
/// client DI: a client-only fleet is created locally through the Shared CQRS handlers over the client-bound
/// <see cref="IFleetRepository"/> (no server, no gRPC). Asserts the default Wing 1 + Squad 1 ship with it, a
/// local toon and an external are added, a member move works, the <see cref="Fleet.IsClientOnly"/> marker is
/// persisted in the client DB, and the active-state marks the fleet client-only so its metrics stay local.
/// Exit 0 = pass, 1 = fail.
/// </summary>
public static class ClientFleetTest
{
    private const int Owner = 95000001;       // a local toon owning the fleet
    private const int SecondLocal = 95000002; // a second local toon
    private const int External = 96000001;    // an external EVE character (no local session)

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        Console.WriteLine("== EVE-Utils client-only fleet check ==");
        var ct = CancellationToken.None;

        var fleetService = services.GetRequiredService<ClientFleetService>();
        var repository = services.GetRequiredService<IFleetRepository>();
        var active = services.GetRequiredService<IActiveFleetState>();

        var ok = true;

        // --- Create a client-only fleet through the Shared CreateFleet handler (local dispatcher). ---
        var created = await fleetService.CreateLocalFleetAsync("My local roam", "client-only test", Owner, ct);
        ok &= Check("create local fleet", created.IsSuccess && created.Value != 0);
        var fleetId = created.Value;

        var fleet = await repository.GetAsync(fleetId, ct);
        ok &= Check("fleet persisted in the client DB", fleet is not null);
        ok &= Check("fleet is marked client-only (IsClientOnly persisted)", fleet is { IsClientOnly: true });
        ok &= Check("owner is the creator", fleet?.CreatorCharacterId == Owner);
        ok &= Check("a client-only fleet is never discoverable (InviteOnly)", fleet?.Visibility == FleetVisibility.InviteOnly);

        // --- Default structure: CreateFleet seeds Wing 1 + Squad 1, reused unchanged. ---
        var wings = await repository.ListWingsAsync(fleetId, ct);
        ok &= Check("default Wing 1 present", wings.Count == 1 && wings[0].Name == "Wing 1");
        var squads = wings.Count == 1 ? await repository.ListSquadsAsync(wings[0].Id, ct) : [];
        ok &= Check("default Squad 1 present", squads.Count == 1 && squads[0].Name == "Squad 1");

        // --- Creator is a default member (FC), added by the Shared handler. ---
        var roster0 = await repository.ListMembersAsync(fleetId, ct);
        ok &= Check("creator is a default member (Fleet Commander)",
            roster0.Any(m => m.CharacterId == Owner && m.Role == FleetRole.FleetCommander && !m.IsExternal));

        // --- Add a second local toon (non-external, dropped into the open squad). ---
        var addedLocal = await fleetService.AddLocalCharacterAsync(fleetId, SecondLocal, Owner, ct);
        ok &= Check("add local character", addedLocal.IsSuccess);
        var localMember = (await repository.ListMembersAsync(fleetId, ct)).FirstOrDefault(m => m.CharacterId == SecondLocal);
        ok &= Check("local toon is a non-external member", localMember is { IsExternal: false });
        ok &= Check("local toon dropped into the default squad", localMember?.SquadId == squads[0].Id);

        // --- Add an external pilot on trust (the Shared AddExternalMember handler). ---
        var addedExternal = await fleetService.AddExternalAsync(fleetId, External, Owner, ct);
        ok &= Check("add external pilot", addedExternal.IsSuccess && addedExternal.Value != 0);
        var externalMember = await repository.GetMemberAsync(addedExternal.Value, ct);
        ok &= Check("external pilot is flagged IsExternal", externalMember is { IsExternal: true });

        // --- Move the local toon to be the squad's commander (Shared MoveMember handler). ---
        var moved = await fleetService.MoveMemberAsync(
            localMember!.Id, FleetRole.SquadCommander, wings[0].Id, squads[0].Id, Owner, ct);
        ok &= Check("move member (local toon → squad commander)", moved.IsSuccess);
        var movedMember = await repository.GetMemberAsync(localMember.Id, ct);
        ok &= Check("member move persisted (role + position)",
            movedMember is { Role: FleetRole.SquadCommander } && movedMember.WingId == wings[0].Id && movedMember.SquadId == squads[0].Id);

        // --- Authorization: a non-owner cannot manage the fleet (creator-only seam holds locally too). ---
        var deniedExternal = await fleetService.AddExternalAsync(fleetId, External + 1, SecondLocal, ct);
        ok &= Check("non-owner is denied management of the fleet", !deniedExternal.IsSuccess);

        // --- Metrics routing: entering a client-only fleet marks the active-state client-only, so the
        //     FleetMetricPublisher keeps samples local (EventTarget.Local) — never over gRPC. ---
        active.Enter(fleetId, Owner, clientOnly: true);
        ok &= Check("active fleet is marked client-only (metrics stay local, no gRPC)", active.IsActiveFleetClientOnly);
        active.Leave();
        ok &= Check("after leaving, no client-only active fleet remains", !active.IsActiveFleetClientOnly);

        // --- Isolation: this fleet must NOT surface in any server-bound discovery path. There is no FleetClient
        //     call here at all — the whole flow ran on the local dispatcher + client DB. ---
        var mine = await repository.ListByCreatorAsync(Owner, ct);
        ok &= Check("client-only fleet is visible to its local owner", mine.Any(f => f.Id == fleetId));

        Console.WriteLine(ok ? "RESULT: PASS ✓" : "RESULT: FAIL ✗");
        return ok ? 0 : 1;
    }

    private static bool Check(string label, bool pass)
    {
        Console.WriteLine($"  {(pass ? "✓" : "✗ FAIL")}  {label}");
        return pass;
    }
}
