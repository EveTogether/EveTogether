using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Fleet;
using EveUtils.Client.Gamelog;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Gamelog.Events;
using EveUtils.Shared.Modules.Gamelog.Repositories;
using EveUtils.Shared.Modules.Settings.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Composition;

/// <summary>
/// Headless proof that real gamelog tailing feeds DPS coupled to the right character, runnable
/// via <c>--gamelog-test</c>. Points the watcher at a temp directory (via the setting), writes a gamelog with a
/// known <c>Listener:</c> after the watcher starts, appends combat, and asserts the DPS lands under the mapped
/// character id — and only that one. A second character's log is tracked separately, never blended in.
///
/// NB: this persists combat rows + the gamelog.directory setting in the client DB, so run it on a throwaway
/// instance: <c>EVEUTILS_INSTANCE=gamelogtest dotnet run --project EveUtils.Client -- --gamelog-test</c>.
/// The setting is reset to empty at the end so it never leaks to a real instance. Exit 0 = pass, 1 = fail.
/// </summary>
public static class ClientGamelogTest
{
    private const long FleetId = 1;
    private const int KnownId = 90001;     // a "registered ESI character" (TestPilot)
    private const int UnmappedId = 90002;  // never mapped → must yield no fleet DPS
    private const int OtherId = 90003;     // a second character (OtherPilot)

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        Console.WriteLine("== EVE Together gamelog → real DPS coupling check ==");
        var ok = true;

        var dir = Path.Combine(Path.GetTempPath(), "evetogether-gamelogtest");
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);

        // Idempotency: this test accumulates bounty + mined into the persisted CharacterMetricState, which
        // survives across runs in the same instance. Reset the test characters' persisted state up front (mirroring
        // the temp-dir wipe above) so a second `make test-gamelog` without `make clean-test-data` doesn't double the
        // totals and fail the exact-total assertions (bounty 4,875 / mined 1,500).
        var metricState = services.GetRequiredService<ICharacterMetricStateRepository>();
        await metricState.UpsertAsync("TestPilot", 0, 0, "{}");
        await metricState.UpsertAsync("OtherPilot", 0, 0, "{}");

        await SetGamelogDirAsync(services, dir);

        var gamelog = services.GetRequiredService<GamelogClientService>();
        gamelog.MapCharacter(KnownId, "TestPilot"); // simulate the registry's name↔id coupling

        var combatCount = 0;
        var bus = services.GetRequiredService<IEventBus>();
        using var subscription = bus.Subscribe<CombatLoggedEvent>(_ => Interlocked.Increment(ref combatCount));

        var watcher = services.GetRequiredService<GamelogWatcherService>();
        try
        {
            await watcher.StartAsync();

            // Write the header AFTER the watcher started (so it tails new growth, not history), then append combat.
            var logPath = Path.Combine(dir, "20260528_123316_1.txt");
            await File.WriteAllTextAsync(logPath, Header("TestPilot"));
            await Task.Delay(900); // let the watcher baseline + detect the character
            await File.AppendAllTextAsync(logPath, CombatLine(250, "Guristas Frigate") + "\n" + CombatLine(300, "Guristas Frigate") + "\n");

            var dealtKnown = await PollFleetDpsAsync(gamelog, KnownId);
            // Poll for the bus events too: the tracker is fed before the (async) publish, so a single check could
            // race the pipeline.
            var published = await PollAsync(() => Volatile.Read(ref combatCount), c => c > 0);
            ok &= Check("combat events parsed + published on the bus", published > 0);
            ok &= Check("DPS is coupled to the right character (mapped id > 0)", dealtKnown > 0);
            ok &= Check("an unmapped character id yields no fleet DPS", FleetDps(gamelog, UnmappedId) == 0);

            // --- Metrics: bounty + location + notify + miss + mining + remote-rep land in the snapshot. ---
            // TestPilot is participating in the fleet before the bounty lands → the fleet meter counts it as run bounty.
            var participation = services.GetRequiredService<IFleetParticipation>();
            participation.Set([new FleetParticipant(KnownId, FleetId, ClientOnly: false)]);
            await File.AppendAllTextAsync(logPath,
                "[ 2026.05.28 12:33:20 ] (bounty) 4,875 ISK added to next bounty payout\n" +
                "[ 2026.05.28 12:33:21 ] (None) Jumping from Eba to Fora\n" +
                "[ 2026.05.28 12:33:22 ] (notify) Interference from Guristas Scout's warp prevents your sensors from locking the target\n" +
                "[ 2026.05.28 12:33:23 ] (combat) Your group of Small Pulse misses Guristas Frigate completely - Small Pulse\n" +
                "[ 2026.05.28 12:33:24 ] (mining) You mined 1500 units of Veldspar\n" +
                "[ 2026.05.28 12:33:25 ] (combat) 250 remote armor repaired to Fleetmate - Medium Remote Armor Repairer II\n" +
                // Incoming energy neut — direction comes from the lead colour (0xffe57f7f = incoming), validated against
                // real gamelogs. And a remote-capacitor-transmitted line that must NOT count as a rep (cap warfare ≠ heal).
                "[ 2026.05.28 12:33:26 ] (combat) <color=0xffe57f7f><b>54 GJ</b><color=0x77ffffff><font size=10> energy neutralized </font><b><color=0xffffffff>Corpum Priest</b><color=0x77ffffff><font size=10> - Corpum Priest</font>\n" +
                "[ 2026.05.28 12:33:27 ] (combat) <color=0xff7fffff><b>40 GJ</b><color=0x77ffffff><font size=10> energy neutralized </font><b><color=0xffffffff>Guristas Scout</b><color=0x77ffffff><font size=10> - Medium Energy Neutralizer II</font>\n" +
                "[ 2026.05.28 12:33:28 ] (combat) <color=0xffccff66><b>236</b><color=0x77ffffff><font size=10> remote capacitor transmitted by </font><b><color=0xffffffff>Catbank</b><color=0x77ffffff><font size=10> - Corpum C-Type Medium Remote Capacitor Transmitter</font>\n");

            var m = await PollAsync(() => gamelog.Snapshot("TestPilot"),
                s => s.BountyTotal > 0 && s.Location is not null && s.TotalMinedUnits > 0 && s.RepairedOut > 0 && s.NeutIn > 0 && s.NeutOut > 0);
            ok &= Check("bounty total accumulated (4,875)", m.BountyTotal == 4875);
            ok &= Check("kills counted from bounty", m.Kills == 1);
            ok &= Check("location tracked from gamelog jump (Fora)", m.Location == "Fora");
            ok &= Check("enemies encountered includes the target", m.Enemies.Any(e => e.Target == "Guristas Frigate"));
            ok &= Check("hits + misses counted (>=2 hits, >=1 miss)", m.Hits >= 2 && m.Misses >= 1);
            ok &= Check("notable notify event captured", m.RecentEvents.Count > 0);
            ok &= Check("mined units accumulated (1500 Veldspar)", m.TotalMinedUnits == 1500);
            ok &= Check("remote rep (outgoing) accumulated (250)", m.RepairedOut == 250);
            ok &= Check("incoming neut accumulated (54 GJ, colour 0xffe57f7f)", m.NeutIn == 54);
            ok &= Check("outgoing neut accumulated (40 GJ, colour 0xff7fffff)", m.NeutOut == 40);
            ok &= Check("remote capacitor transmitted is NOT counted as a rep", m.RepairedIn == 0);
            ok &= Check("fleet sample emits a live neut rate (> 0)", FleetMetric(gamelog, KnownId, MetricKind.Neut) > 0);
            ok &= Check("fleet sample emits a live cap rate (> 0)", FleetMetric(gamelog, KnownId, MetricKind.Cap) > 0);
            ok &= Check("fleet bounty = run bounty since joining (4,875)", FleetMetric(gamelog, KnownId, MetricKind.Bounty) == 4875);

            // Per-RUN scope: bounty earned BEFORE joining a fleet is not in the fleet meter; only what's earned during the run.
            const int runPilotId = 95001;
            const long runFleet = 77;
            gamelog.MapCharacter(runPilotId, "RunPilot");
            await gamelog.AddBountyAsync("RunPilot", 1_000_000); // earned before participating
            ok &= Check("bounty before joining the fleet is not in the fleet meter",
                FleetMetric(gamelog, runPilotId, MetricKind.Bounty, runFleet) == 0);
            participation.Set([new FleetParticipant(runPilotId, runFleet, ClientOnly: false)]);
            await gamelog.AddBountyAsync("RunPilot", 2_500_000); // earned during the run
            ok &= Check("only bounty earned during the run counts (2.5M)",
                FleetMetric(gamelog, runPilotId, MetricKind.Bounty, runFleet) == 2_500_000);

            // --- Persistence roundtrip: a fresh service restores bounty + mined, but NOT DPS/enemies. ---
            var fresh = new GamelogClientService(services, services.GetRequiredService<IEventBus>());
            await fresh.EnsureSeededAsync("TestPilot");
            var restored = fresh.Snapshot("TestPilot");
            ok &= Check("bounty persisted across a fresh service", restored.BountyTotal == 4875);
            ok &= Check("mined persisted across a fresh service", restored.TotalMinedUnits == 1500);
            ok &= Check("DPS + enemies are session-only (not persisted)", restored.TotalDealt == 0 && restored.Enemies.Count == 0);

            // A second character's gamelog must be tracked separately — no cross-contamination.
            var otherPath = Path.Combine(dir, "20260528_123317_2.txt");
            await File.WriteAllTextAsync(otherPath, Header("OtherPilot"));
            await Task.Delay(900);
            await File.AppendAllTextAsync(otherPath, CombatLine(900, "Serpentis Cruiser") + "\n");
            gamelog.MapCharacter(OtherId, "OtherPilot");

            var dealtOther = await PollFleetDpsAsync(gamelog, OtherId);
            ok &= Check("a second character's combat is tracked under its own id", dealtOther > 0);
            ok &= Check("the first character's DPS is unaffected by the second", FleetDps(gamelog, KnownId) > 0);
        }
        finally
        {
            watcher.Stop();
            await SetGamelogDirAsync(services, ""); // never leave the temp dir as a real instance's setting
        }

        Console.WriteLine(ok ? "RESULT: PASS ✓" : "RESULT: FAIL ✗");
        return ok ? 0 : 1;
    }

    private static async Task SetGamelogDirAsync(IServiceProvider services, string dir)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Send(new SetSettingCommand(GamelogWatcherService.GamelogDirectorySettingKey, dir));
    }

    private static async Task<T> PollAsync<T>(Func<T> get, Func<T, bool> ready)
    {
        var value = get();
        for (var i = 0; i < 40 && !ready(value); i++)
        {
            await Task.Delay(100);
            value = get();
        }
        return value;
    }

    private static async Task<double> PollFleetDpsAsync(GamelogClientService gamelog, int characterId)
    {
        for (var i = 0; i < 40; i++)
        {
            var dps = FleetDps(gamelog, characterId);
            if (dps > 0)
                return dps;
            await Task.Delay(100);
        }
        return 0;
    }

    private static double FleetDps(GamelogClientService gamelog, int characterId) =>
        gamelog.Sample(FleetId, characterId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).First().Value;

    private static double FleetMetric(GamelogClientService gamelog, int characterId, MetricKind kind) =>
        FleetMetric(gamelog, characterId, kind, FleetId);

    private static double FleetMetric(GamelogClientService gamelog, int characterId, MetricKind kind, long fleetId) =>
        gamelog.Sample(fleetId, characterId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).First(s => s.Kind == kind).Value;

    private static string Header(string characterName) =>
        "------------------------------------------------------------\n" +
        "  Gamelog\n" +
        $"  Listener: {characterName}\n" +
        "  Session Started: 2026.05.28 12:33:16\n" +
        "------------------------------------------------------------\n";

    // Tags are stripped by the parser, so a plain line is enough: "<amount> to <target> - <quality>".
    private static string CombatLine(int amount, string target) =>
        $"[ 2026.05.28 12:33:16 ] (combat) {amount} to {target} - Hits";

    private static bool Check(string label, bool pass)
    {
        Console.WriteLine($"  {(pass ? "✓" : "✗ FAIL")}  {label}");
        return pass;
    }
}
