using EveUtils.Client.Fleet;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Settings.Commands;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Verifies the per-metric share gate on SERVER fleets: combat metrics are shared by default (opt-OUT) and can be
/// turned off, while location stays private by default (opt-IN) and only shares once enabled. Exercises both the pure
/// <see cref="MetricShareSnapshot"/> defaults and the gate as applied by <see cref="FleetMetricPublisher"/>. A
/// local-only fleet bypasses the gate entirely (2026-06-04, Option P) — covered by
/// <see cref="Publisher_LocalOnlyFleet_SharesEverything_RegardlessOfTheGate"/>.
/// </summary>
public class MetricShareGateTests
{
    private const int Owner = 95000001;
    private const long FleetId = 4242;

    [Fact]
    public void Snapshot_Defaults_ShareCombat_ButNotLocation()
    {
        var snapshot = new MetricShareSnapshot(new Dictionary<string, string>());

        Assert.True(snapshot.IsShared(MetricKind.Dps));
        Assert.True(snapshot.IsShared(MetricKind.DpsIn));
        Assert.False(snapshot.IsShared(MetricKind.Location));
    }

    [Fact]
    public void Snapshot_HonoursExplicitToggles()
    {
        var snapshot = new MetricShareSnapshot(new Dictionary<string, string>
        {
            [MetricShareSnapshot.KeyFor(MetricKind.Dps)] = "false",
            [MetricShareSnapshot.KeyFor(MetricKind.Location)] = "true",
        });

        Assert.False(snapshot.IsShared(MetricKind.Dps));
        Assert.True(snapshot.IsShared(MetricKind.Location));
    }

    [Fact]
    public async Task Publisher_SharesDpsByDefault_ButNotAfterOptOut()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var captured = SubscribeKinds(instance, out var bus);
        var publisher = ServerFleetPublisher(instance, bus, MetricKind.Dps);

        await publisher.PublishTickAsync(unixMs: 1, cancellationToken);
        Assert.Contains(MetricKind.Dps, captured);

        await SetSettingAsync(instance, MetricShareSnapshot.KeyFor(MetricKind.Dps), "false", cancellationToken);
        captured.Clear();
        await publisher.PublishTickAsync(unixMs: 2, cancellationToken);
        Assert.DoesNotContain(MetricKind.Dps, captured);
    }

    [Fact]
    public async Task Publisher_DoesNotShareLocationUntilOptedIn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var captured = SubscribeKinds(instance, out var bus);
        var publisher = ServerFleetPublisher(instance, bus, MetricKind.Location);

        await publisher.PublishTickAsync(unixMs: 1, cancellationToken);
        Assert.DoesNotContain(MetricKind.Location, captured);

        await SetSettingAsync(instance, MetricShareSnapshot.KeyFor(MetricKind.Location), "true", cancellationToken);
        captured.Clear();
        await publisher.PublishTickAsync(unixMs: 2, cancellationToken);
        Assert.Contains(MetricKind.Location, captured);
    }

    [Fact]
    public void Snapshot_PerFleetOverride_BeatsGlobalDefault()
    {
        var snapshot = new MetricShareSnapshot(new Dictionary<string, string>
        {
            [MetricShareSnapshot.OverrideKeyFor(7, 42, MetricKind.Location)] = "true",  // override on (global off)
            [MetricShareSnapshot.OverrideKeyFor(7, 42, MetricKind.Dps)] = "false",       // override off (global on)
        });

        Assert.True(snapshot.IsShared(7, 42, MetricKind.Location));
        Assert.False(snapshot.IsShared(7, 42, MetricKind.Dps));
        // A different fleet (no override) follows the global default.
        Assert.False(snapshot.IsShared(99, 42, MetricKind.Location));
        Assert.True(snapshot.IsShared(99, 42, MetricKind.Dps));
    }

    [Fact]
    public async Task Publisher_HonoursPerFleetOverride()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var captured = SubscribeKinds(instance, out var bus);

        // Location is globally off, but an override shares it for THIS fleet+character.
        await SetSettingAsync(instance, MetricShareSnapshot.OverrideKeyFor(FleetId, Owner, MetricKind.Location), "true", cancellationToken);
        await ServerFleetPublisher(instance, bus, MetricKind.Location).PublishTickAsync(1, cancellationToken);
        Assert.Contains(MetricKind.Location, captured);

        // DPS is globally on, but an override hides it for THIS fleet+character.
        await SetSettingAsync(instance, MetricShareSnapshot.OverrideKeyFor(FleetId, Owner, MetricKind.Dps), "false", cancellationToken);
        captured.Clear();
        await ServerFleetPublisher(instance, bus, MetricKind.Dps).PublishTickAsync(2, cancellationToken);
        Assert.DoesNotContain(MetricKind.Dps, captured);
    }

    [Fact]
    public async Task Publisher_LocalOnlyFleet_SharesEverything_RegardlessOfTheGate()
    {
        // 2026-06-04 (Option P): a local-only fleet is purely local — its samples only ever feed your own
        // graphs, so the privacy gate does not apply. Even an explicit opt-OUT (DPS) and the opt-IN default (Location)
        // still get shared for a client-only participant.
        var cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var captured = SubscribeKinds(instance, out var bus);

        await SetSettingAsync(instance, MetricShareSnapshot.KeyFor(MetricKind.Dps), "false", cancellationToken);

        var participation = new FleetParticipation();
        participation.Set([new FleetParticipant(Owner, FleetId, ClientOnly: true)]);
        var share = instance.Services.GetRequiredService<IMetricShareSettings>();

        await new FleetMetricPublisher(participation, [new FixedMetricSource(MetricKind.Dps)], bus, share)
            .PublishTickAsync(1, cancellationToken);
        Assert.Contains(MetricKind.Dps, captured); // opted OUT, but a local-only fleet shares it anyway

        captured.Clear();
        await new FleetMetricPublisher(participation, [new FixedMetricSource(MetricKind.Location)], bus, share)
            .PublishTickAsync(2, cancellationToken);
        Assert.Contains(MetricKind.Location, captured); // opt-IN default is off, but local-only shares it anyway
    }

    private static List<MetricKind> SubscribeKinds(TestClientInstance instance, out IEventBus bus)
    {
        bus = instance.Services.GetRequiredService<IEventBus>();
        var captured = new List<MetricKind>();
        // Subscription lives for the test's duration; the bus is torn down with the instance.
        bus.Subscribe<FleetMetricEvent>((e, _) =>
        {
            captured.Add(e.Data.Kind);
            return Task.CompletedTask;
        });
        return captured;
    }

    // A SERVER-backed participant: the per-metric share gate is a privacy boundary for what you broadcast to other
    // members on a server, so it only applies here (a local-only fleet bypasses it — see Publisher_LocalOnlyFleet_*).
    private static FleetMetricPublisher ServerFleetPublisher(TestClientInstance instance, IEventBus bus, MetricKind kind)
    {
        var participation = new FleetParticipation();
        participation.Set([new FleetParticipant(Owner, FleetId, ClientOnly: false)]);
        var share = instance.Services.GetRequiredService<IMetricShareSettings>();
        return new FleetMetricPublisher(participation, [new FixedMetricSource(kind)], bus, share);
    }

    private static async Task SetSettingAsync(TestClientInstance instance, string key, string value, CancellationToken cancellationToken)
    {
        using var scope = instance.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDispatcher>()
            .Send(new SetSettingCommand(key, value), cancellationToken);
    }
}
