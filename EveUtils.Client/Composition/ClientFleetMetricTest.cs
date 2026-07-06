using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Client.Fleet;
using EveUtils.Client.Gamelog;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Settings.Commands;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Composition;

/// <summary>
/// Headless proof for the metric aggregation + scoping, runnable via <c>--fleet-metric-test</c>.
/// Drives the real client DI: feeds DPS into the gamelog service, then asserts the <see cref="FleetMetricPublisher"/>
/// only emits while the client is participating, stamps the active fleet + character onto every sample, marks the
/// event fleet-scoped (so it reroutes), and goes quiet again after leaving. Also checks the metric catalog's
/// semantics/aggregation rules. Exit 0 = pass, 1 = fail.
/// </summary>
public static class ClientFleetMetricTest
{
    private const long FleetId = 4242;
    private const int Character = 90001;
    private const int SecondCharacter = 90002; // a second local toon active in the SAME fleet.

    public static async Task<int> RunAsync(IServiceProvider services)
    {
        Console.WriteLine("== EVE-Utils fleet metric aggregation + scoping check ==");

        var gamelog = services.GetRequiredService<GamelogClientService>();
        var participation = services.GetRequiredService<IFleetParticipation>();
        var publisher = services.GetRequiredService<FleetMetricPublisher>();
        var bus = services.GetRequiredService<IEventBus>();
        var ct = CancellationToken.None;

        var captured = new List<FleetMetricEvent>();
        using var subscription = bus.Subscribe<FleetMetricEvent>(evt => captured.Add(evt));

        var ok = true;

        // --- Not participating: a tick must publish nothing (no leak to a fleet we are not in). ---
        await publisher.PublishTickAsync(Now(), ct);
        ok &= Check("idle (no active fleet) → no metric published", captured.Count == 0);

        // --- Participating: feed combat (both directions), enter the fleet, tick → a dealt + a received sample. ---
        gamelog.SetCharacter("Pilot");
        gamelog.MapCharacter(Character, "Pilot"); // couple the fleet id to the gamelog name (per-character DPS)
        await gamelog.AddHitAsync(DamageDirection.Outgoing, 600, "Target", ct);
        await gamelog.AddHitAsync(DamageDirection.Outgoing, 400, "Target", ct);
        await gamelog.AddHitAsync(DamageDirection.Incoming, 250, "Target", ct);
        participation.Set([new FleetParticipant(Character, FleetId, ClientOnly: true)]);
        captured.Clear();

        await publisher.PublishTickAsync(Now(), ct);
        ok &= Check("active fleet → a dealt + a received DPS sample published", captured.Count == 2);

        var dealt = captured.FirstOrDefault(e => e.Data.Kind == MetricKind.Dps);
        var received = captured.FirstOrDefault(e => e.Data.Kind == MetricKind.DpsIn);
        ok &= Check("a Dps (dealt) sample is published", dealt is not null);
        ok &= Check("a DpsIn (received) sample is published", received is not null);
        ok &= Check("samples scoped to the active fleet", dealt?.Data.FleetId == FleetId && received?.Data.FleetId == FleetId);
        ok &= Check("samples stamped with the active character", dealt?.Data.CharacterId == Character && received?.Data.CharacterId == Character);
        ok &= Check("event carries the source character id", dealt?.CharacterId == Character);
        ok &= Check("non-zero dealt DPS (outgoing combat within window)", dealt is { Data.Value: > 0 });
        ok &= Check("non-zero received DPS (incoming combat within window)", received is { Data.Value: > 0 });
        ok &= Check("event is fleet-scoped so it reroutes", dealt is IFleetScopedEvent { FleetId: FleetId });

        // --- Multi-toon bundling: a second local character active in the SAME fleet must publish its
        //     own per-character samples in the SAME tick — one coordinated flush, each stamped with its own id. ---
        gamelog.MapCharacter(SecondCharacter, "Pilot2");
        await gamelog.AddHitAsync("Pilot2", DamageDirection.Outgoing, 800, "Target", cancellationToken: ct);
        // Both local toons are in the SAME fleet → both publish in one tick.
        participation.Set([
            new FleetParticipant(Character, FleetId, ClientOnly: true),
            new FleetParticipant(SecondCharacter, FleetId, ClientOnly: true),
        ]);
        ok &= Check("both local characters participate in the one fleet",
            participation.Current.Count == 2 && participation.Current.All(p => p.FleetId == FleetId));

        captured.Clear();
        await publisher.PublishTickAsync(Now(), ct); // ONE publish cycle
        var firstSamples = captured.Where(e => e.Data.CharacterId == Character).ToList();
        var secondSamples = captured.Where(e => e.Data.CharacterId == SecondCharacter).ToList();
        ok &= Check("first character still publishes a dealt + received sample in the bundled tick", firstSamples.Count == 2);
        ok &= Check("second character publishes a dealt + received sample in the same tick", secondSamples.Count == 2);
        ok &= Check("every sample is stamped with a known active character (no unstamped sample)",
            captured.All(e => e.Data.CharacterId == Character || e.Data.CharacterId == SecondCharacter));
        ok &= Check("every sample is scoped to the one active fleet", captured.All(e => e.Data.FleetId == FleetId));
        ok &= Check("no duplicate (character, kind) sample in the bundle",
            captured.Select(e => (e.Data.CharacterId, e.Data.Kind)).Distinct().Count() == captured.Count);
        ok &= Check("the second character's DPS is its own, not a merge of the first",
            secondSamples.Any(e => e.Data.Kind == MetricKind.Dps && e.Data.Value > 0));

        // One character leaving keeps the other participating in the fleet (per-character).
        participation.Set([new FleetParticipant(Character, FleetId, ClientOnly: true)]);
        ok &= Check("after one toon leaves, only the other toon still participates",
            participation.Current.Count == 1 && participation.Current[0].CharacterId == Character);
        captured.Clear();
        await publisher.PublishTickAsync(Now(), ct);
        ok &= Check("after one toon leaves, only the remaining toon publishes", captured.All(e => e.Data.CharacterId == Character) && captured.Count == 2);

        // --- Left: a tick after leaving must publish nothing more. ---
        participation.Set([]);
        captured.Clear();
        await publisher.PublishTickAsync(Now(), ct);
        ok &= Check("after leaving → no further metrics", captured.Count == 0);

        // --- Metric catalog semantics. ---
        ok &= Check("Dps is a Rate metric", FleetMetricCatalog.Describe(MetricKind.Dps).Semantics == MetricSemantics.Rate);
        ok &= Check("Dps is aggregatable", FleetMetricCatalog.IsAggregatable(MetricKind.Dps));
        ok &= Check("DpsIn is a Rate metric", FleetMetricCatalog.Describe(MetricKind.DpsIn).Semantics == MetricSemantics.Rate);
        ok &= Check("DpsIn is aggregatable", FleetMetricCatalog.IsAggregatable(MetricKind.DpsIn));
        ok &= Check("Location is a State metric", FleetMetricCatalog.Describe(MetricKind.Location).Semantics == MetricSemantics.State);
        ok &= Check("Location is NOT aggregatable", !FleetMetricCatalog.IsAggregatable(MetricKind.Location));
        ok &= Check("fleet-total for Dps sums members", FleetMetricCatalog.Aggregate(MetricKind.Dps, [100, 250, 50]) == 400);
        ok &= Check("fleet-total for a State kind is null (label, not a rollup)",
            FleetMetricCatalog.Aggregate(MetricKind.Location, [1, 2]) is null);

        // --- New kinds: Neut (cap-warfare, Rate) + MiningLedger (Cumulative haul) travel the bus and roll up. ---
        ok &= Check("Neut is a Rate metric", FleetMetricCatalog.Describe(MetricKind.Neut).Semantics == MetricSemantics.Rate);
        ok &= Check("Neut is aggregatable (counts into the fleet total)", FleetMetricCatalog.IsAggregatable(MetricKind.Neut));
        ok &= Check("Neut has a unit (does not degrade to a bare State)", FleetMetricCatalog.Describe(MetricKind.Neut).Unit.Length > 0);
        ok &= Check("fleet-total for Neut sums members", FleetMetricCatalog.Aggregate(MetricKind.Neut, [30, 20, 10]) == 60);
        ok &= Check("MiningLedger is a Cumulative metric", FleetMetricCatalog.Describe(MetricKind.MiningLedger).Semantics == MetricSemantics.Cumulative);
        ok &= Check("MiningLedger is aggregatable (counts into the fleet haul)", FleetMetricCatalog.IsAggregatable(MetricKind.MiningLedger));
        ok &= Check("MiningLedger has a unit", FleetMetricCatalog.Describe(MetricKind.MiningLedger).Unit.Length > 0);
        ok &= Check("fleet-total for MiningLedger sums members", FleetMetricCatalog.Aggregate(MetricKind.MiningLedger, [1500, 2500]) == 4000);

        // --- Location privacy opt-in: position is shared only when the user opts in. ---
        ok &= await CheckLocationOptInAsync(services, publisher, participation, bus, ct);

        Console.WriteLine(ok ? "RESULT: PASS ✓" : "RESULT: FAIL ✗");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// Asserts the location privacy gate (Option P, 2026-06-04): it is the single publisher-level share decision,
    /// uniform with DPS. On a <b>server</b> fleet location is opt-IN — off (default) publishes no
    /// <see cref="MetricKind.Location"/> sample, on lets it through (other kinds unaffected). On a <b>local-only</b>
    /// fleet the share-gate does not apply, so location is shared even when opted out (it only feeds your own graphs).
    /// </summary>
    private static async Task<bool> CheckLocationOptInAsync(
        IServiceProvider services, FleetMetricPublisher publisher, IFleetParticipation participation, IEventBus bus, CancellationToken ct)
    {
        var ok = true;
        var location = services.GetRequiredService<LocationMetricSource>();
        const long systemId = 30000142; // Jita, a stand-in solar_system_id

        location.SetSystem(Character, systemId); // a position is known — only the share decision should gate it

        var captured = new List<FleetMetricEvent>();
        using (var subscription = bus.Subscribe<FleetMetricEvent>(evt => captured.Add(evt)))
        {
            // --- Server fleet: location is opt-IN (the privacy gate applies). ---
            participation.Set([new FleetParticipant(Character, FleetId, ClientOnly: false)]);

            // Default (opted out): no Location sample leaves, though DPS still does.
            await SetShareLocationAsync(services, false);
            captured.Clear();
            await publisher.PublishTickAsync(Now(), ct);
            ok &= Check("server fleet, opt-out (default) → no Location sample published",
                captured.All(e => e.Data.Kind != MetricKind.Location));
            ok &= Check("server fleet, opt-out leaves the other kinds (DPS) untouched",
                captured.Any(e => e.Data.Kind == MetricKind.Dps));

            // Opted in: exactly the known system id is shared, scoped + stamped like any sample.
            await SetShareLocationAsync(services, true);
            captured.Clear();
            await publisher.PublishTickAsync(Now(), ct);
            var loc = captured.FirstOrDefault(e => e.Data.Kind == MetricKind.Location);
            ok &= Check("server fleet, opt-in → a Location sample is published", loc is not null);
            ok &= Check("Location sample carries the solar-system id", loc is { Data.Value: systemId });
            ok &= Check("Location sample scoped + stamped",
                loc?.Data.FleetId == FleetId && loc?.Data.CharacterId == Character);

            // --- Local-only fleet: the share-gate does not apply — location is shared even when opted OUT
            //     (2026-06-04, Option P: a local-only fleet feeds only your own graphs, nothing to hide). ---
            await SetShareLocationAsync(services, false);
            participation.Set([new FleetParticipant(Character, FleetId, ClientOnly: true)]);
            captured.Clear();
            await publisher.PublishTickAsync(Now(), ct);
            ok &= Check("local-only fleet, opt-out → Location IS still shared (gate bypassed for client-only)",
                captured.Any(e => e.Data.Kind == MetricKind.Location));
        }

        participation.Set([]);
        await SetShareLocationAsync(services, false); // never leave the opt-in on for a real instance
        return ok;
    }

    private static async Task SetShareLocationAsync(IServiceProvider services, bool on)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Send(new SetSettingCommand(LocationMetricSource.ShareLocationSettingKey, on ? "true" : "false"));
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static bool Check(string label, bool pass)
    {
        Console.WriteLine($"  {(pass ? "✓" : "✗ FAIL")}  {label}");
        return pass;
    }
}
