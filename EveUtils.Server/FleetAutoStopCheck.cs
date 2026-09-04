using EveUtils.Server.Grpc;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Fleet.Cleanup;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;

namespace EveUtils.Server;

/// <summary>
/// Headless proof for the automatic stop (ET-167), runnable via <c>--fleet-autostop-test</c> against a real server
/// process and its real database — the way this change can be verified locally without deploying anything. Two
/// layers, as <see cref="FleetCleanupCheck"/> has: the pure <see cref="FleetAutoStopPolicy"/> and
/// <see cref="FleetAutoStopBrake"/> decision tables, and an integration sweep through the real
/// <see cref="FleetAutoStopRunner"/> + dispatcher + DB that stands an emptied and a gone-quiet fleet down while
/// leaving a busy one, a just-started one and a fleet in the daily downtime window alone. Exit 0 = pass, 1 = fail.
/// </summary>
public static class FleetAutoStopCheck
{
    private const int Creator = 7101;
    private const int Flying = 8102;
    private const int Departed = 8103;

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        Console.WriteLine("== EVE-Utils fleet auto-stop check ==");
        var ok = EvaluateBrake();
        ok &= EvaluatePolicy();
        ok &= await EvaluateSweepAsync(services);
        Console.WriteLine(ok ? "RESULT: PASS ✓" : "RESULT: FAIL ✗");
        return ok ? 0 : 1;
    }

    private static bool EvaluateBrake()
    {
        var grace = FleetCleanupOptions.Default.ReconnectGrace;
        DateTimeOffset At(int hour, int minute) => new(2026, 9, 4, hour, minute, 0, TimeSpan.Zero);
        bool Engaged(DateTimeOffset now, bool usable = true, DateTimeOffset? lastDown = null) =>
            FleetAutoStopBrake.IsEngaged(now, usable, lastDown, grace);

        var ok = Check("10:59 UTC → brake off (nothing is wrong yet)", !Engaged(At(10, 59)));
        ok &= Check("11:01 UTC → brake on (the daily window)", Engaged(At(11, 1)));
        ok &= Check("11:04 UTC → brake still on (pilots are coming back)", Engaged(At(11, 4)));
        ok &= Check("11:05 UTC → brake off (reconnect grace spent)", !Engaged(At(11, 5)));
        ok &= Check("ESI unreachable → brake on without the calendar", Engaged(At(20, 0), usable: false));
        ok &= Check("just recovered → brake still on for the reconnect grace",
            Engaged(At(20, 1), lastDown: At(20, 0)));
        ok &= Check("recovered a while ago → brake off",
            !Engaged(At(20, 0) + grace, lastDown: At(20, 0)));
        return ok;
    }

    private static bool EvaluatePolicy()
    {
        var opts = FleetCleanupOptions.Default;
        var now = DateTimeOffset.UnixEpoch.AddYears(56);
        var recent = now - TimeSpan.FromMinutes(1);
        var stale = now - TimeSpan.FromHours(2);

        FleetStopTrigger? Evaluate(FleetPresenceCensus census, DateTimeOffset lastActivity, bool brake = false,
            FleetActivation activation = FleetActivation.Active) =>
            FleetAutoStopPolicy.Evaluate(FleetState.Active, activation, census, lastActivity, now, brake, opts);

        var ok = Check("empty roster + settled → stood down as RosterEmpty",
            Evaluate(new FleetPresenceCensus(0, 0, 0), stale) == FleetStopTrigger.RosterEmpty);
        ok &= Check("empty roster + brake on → STILL stood down (whoever left stayed gone)",
            Evaluate(new FleetPresenceCensus(0, 0, 0), stale, brake: true) == FleetStopTrigger.RosterEmpty);
        ok &= Check("empty roster + just started → left alone (its invitations are still out)",
            Evaluate(new FleetPresenceCensus(0, 0, 0), recent) is null);
        ok &= Check("everyone quiet → stood down as AllMembersOffline",
            Evaluate(new FleetPresenceCensus(3, 0, 3), stale) == FleetStopTrigger.AllMembersOffline);
        ok &= Check("everyone quiet + brake on → withheld",
            Evaluate(new FleetPresenceCensus(3, 0, 3), stale, brake: true) is null);
        ok &= Check("one member still there → left alone",
            Evaluate(new FleetPresenceCensus(3, 1, 3), stale) is null);
        ok &= Check("nobody ever heard from → left alone (silence with no contact before it is not evidence)",
            Evaluate(new FleetPresenceCensus(3, 0, 0), stale) is null);
        ok &= Check("a forming fleet is never stopped",
            Evaluate(new FleetPresenceCensus(0, 0, 0), stale, activation: FleetActivation.Forming) is null);
        ok &= Check("a concluded fleet is never stopped",
            Evaluate(new FleetPresenceCensus(0, 0, 0), stale, activation: FleetActivation.Concluded) is null);
        return ok;
    }

    private static async Task<bool> EvaluateSweepAsync(IServiceProvider services)
    {
        var opts = FleetCleanupOptions.Default;
        using var scope = services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
        var repo = scope.ServiceProvider.GetRequiredService<IFleetRepository>();
        var runner = scope.ServiceProvider.GetRequiredService<FleetAutoStopRunner>();
        var ct = CancellationToken.None;
        var now = DateTimeOffset.UtcNow;
        var silent = now - FleetMemberPresence.SilentAfter - TimeSpan.FromMinutes(5);
        var ok = true;
        var created = new List<long>();

        try
        {
            // A started fleet whose two members are both long silent: the server stands it down by itself.
            var quiet = await StartedFleetAsync(dispatcher, repo, "Auto-stop quiet", created, ct, Flying, Departed);
            await repo.TouchMemberSeenAsync(quiet, Flying, silent, ct);
            await repo.TouchMemberSeenAsync(quiet, Departed, silent, ct);
            var swept = await runner.SweepAsync(now, opts, brakeEngaged: false, ct);
            ok &= Check("a fleet whose members have all gone quiet is stood down",
                (await repo.GetAsync(quiet, ct))?.Activation == FleetActivation.Forming);
            ok &= Check("the sweep reports it as an all-offline stop", swept.AllOffline >= 1);
            var keptRoster = (await repo.ListMembersAsync(quiet, ct)).Select(m => m.CharacterId).ToHashSet();
            ok &= Check("its roster is still on it (stopped, not concluded or emptied)",
                keptRoster.Contains(Flying) && keptRoster.Contains(Departed));
            ok &= Check("and it can be started again",
                (await dispatcher.Send(new StartFleetCommand(quiet, Creator), ct)).IsSuccess);

            // The same fleet, the same silence, swept at 11:01 UTC — the false positive this whole brake is for.
            await repo.TouchMemberSeenAsync(quiet, Flying, silent, ct);
            await repo.TouchMemberSeenAsync(quiet, Departed, silent, ct);
            var downtime = new DateTimeOffset(now.UtcDateTime.Date.AddHours(11).AddMinutes(1), TimeSpan.Zero);
            var brake = FleetAutoStopBrake.IsEngaged(downtime, esiUsable: true, null, opts.ReconnectGrace);
            ok &= Check("11:01 UTC engages the brake", brake);
            await runner.SweepAsync(downtime, opts, brake, ct);
            ok &= Check("nothing is stood down during the daily downtime window",
                (await repo.GetAsync(quiet, ct))?.Activation == FleetActivation.Active);

            // One pilot still publishing keeps the whole fleet running.
            await repo.TouchMemberSeenAsync(quiet, Flying, now, ct);
            await runner.SweepAsync(now, opts, brakeEngaged: false, ct);
            ok &= Check("one member still there keeps the fleet running",
                (await repo.GetAsync(quiet, ct))?.Activation == FleetActivation.Active);

            // A started fleet nobody has published into yet — the FC who starts ahead of their pilots.
            var awaited = await StartedFleetAsync(dispatcher, repo, "Auto-stop awaited", created, ct, Flying);
            await runner.SweepAsync(now, opts, brakeEngaged: false, ct);
            ok &= Check("a fleet whose pilots have not arrived yet keeps running",
                (await repo.GetAsync(awaited, ct))?.Activation == FleetActivation.Active);

            // An emptied roster, settled: stood down on the other ground, brake or no brake. CreateFleetCommand
            // seats the creator as FC, so "everyone left" has to actually be made to happen.
            var emptied = await StartedFleetAsync(dispatcher, repo, "Auto-stop emptied", created, ct, Flying);
            foreach (var member in await repo.ListMembersAsync(emptied, ct))
                await repo.RemoveMemberAsync(member.Id, ct);
            await repo.TouchActivityAsync(emptied, now - TimeSpan.FromHours(2), ct);
            var emptySweep = await runner.SweepAsync(now, opts, brakeEngaged: true, ct);
            ok &= Check("an emptied roster is stood down even with the brake on",
                (await repo.GetAsync(emptied, ct))?.Activation == FleetActivation.Forming);
            ok &= Check("the sweep reports it as an empty-roster stop", emptySweep.RosterEmpty >= 1);

            // And an emptied roster that only just emptied is left alone.
            await dispatcher.Send(new StartFleetCommand(emptied, Creator), ct);
            await repo.TouchActivityAsync(emptied, now, ct);
            await runner.SweepAsync(now, opts, brakeEngaged: false, ct);
            ok &= Check("a fleet started moments ago is not stood down while its invitations are out",
                (await repo.GetAsync(emptied, ct))?.Activation == FleetActivation.Active);

            ok &= Check("the ESI availability the brake reads is driven on this host too",
                scope.ServiceProvider.GetService<IEsiAvailabilityState>() is not null);
        }
        finally
        {
            foreach (var id in created)
                await repo.DeleteAsync(id, ct);
        }

        return ok;
    }

    private static async Task<long> StartedFleetAsync(
        IDispatcher dispatcher, IFleetRepository repo, string name, List<long> created, CancellationToken ct,
        params int[] members)
    {
        var fleetId = (await dispatcher.Send(new CreateFleetCommand(
            name, null, FleetVisibility.Public, null, null, FleetOfflineBehavior.StayOffline, Creator), ct)).Value;
        created.Add(fleetId);

        foreach (var characterId in members)
            await dispatcher.Send(new JoinFleetCommand(fleetId, characterId), ct);

        // Straight to Active without the Start notifications: the sweep reads the phase, and a mailbox per member is
        // noise in a check that is about the phase.
        var fleet = await repo.GetAsync(fleetId, ct);
        fleet!.Activation = FleetActivation.Active;
        fleet.ActivatedAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(3);
        fleet.LastActivityAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(2);
        await repo.UpdateAsync(fleet, ct);
        return fleetId;
    }

    private static bool Check(string label, bool pass)
    {
        Console.WriteLine($"  {(pass ? "✓" : "✗ FAIL")}  {label}");
        return pass;
    }
}
