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
/// Verifies the per-metric share gate on SERVER fleets: combat metrics are broadcast by default (opt-OUT) and can be
/// turned off, while location and bounty stay private by default (opt-IN) and only go out once enabled. The gate
/// decides what LEAVES this machine — a sample always reaches the local bus, which is what draws your own row and the
/// fleet totals (ET-41). Exercises both the pure <see cref="MetricShareSnapshot"/> defaults and the gate as applied by
/// <see cref="FleetMetricPublisher"/>. A local-only fleet never reaches the transport at all — covered by
/// <see cref="Publisher_LocalOnlyFleet_NeverLeavesTheMachine"/>.
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
    public async Task Publisher_UnsharedMetric_StillFeedsYourOwnRow_ButIsNotBroadcast()
    {
        // ET-41: bounty is opt-IN, so it is not broadcast — but it is YOUR bounty, so your own row and the fleet
        // totals must still get it. The gate is a broadcast boundary, not a blindfold on your own client.
        var cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var capture = Capture(out var bus);

        await ServerFleetPublisher(instance, bus, MetricKind.Bounty).PublishTickAsync(1, cancellationToken);
        Assert.Contains(MetricKind.Bounty, capture.Local);
        Assert.DoesNotContain(MetricKind.Bounty, capture.Broadcast);

        await SetSettingAsync(instance, MetricShareSnapshot.KeyFor(MetricKind.Bounty), "true", cancellationToken);
        capture.Clear();
        await ServerFleetPublisher(instance, bus, MetricKind.Bounty).PublishTickAsync(2, cancellationToken);
        Assert.Contains(MetricKind.Bounty, capture.Broadcast);
    }

    [Fact]
    public async Task Publisher_BroadcastsDpsByDefault_ButNotAfterOptOut()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var capture = Capture(out var bus);
        var publisher = ServerFleetPublisher(instance, bus, MetricKind.Dps);

        await publisher.PublishTickAsync(unixMs: 1, cancellationToken);
        Assert.Contains(MetricKind.Dps, capture.Broadcast);

        await SetSettingAsync(instance, MetricShareSnapshot.KeyFor(MetricKind.Dps), "false", cancellationToken);
        capture.Clear();
        await publisher.PublishTickAsync(unixMs: 2, cancellationToken);
        Assert.DoesNotContain(MetricKind.Dps, capture.Broadcast);
        Assert.Contains(MetricKind.Dps, capture.Local); // still your own graph
    }

    [Fact]
    public async Task Publisher_DoesNotBroadcastLocationUntilOptedIn()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var capture = Capture(out var bus);
        var publisher = ServerFleetPublisher(instance, bus, MetricKind.Location);

        await publisher.PublishTickAsync(unixMs: 1, cancellationToken);
        Assert.DoesNotContain(MetricKind.Location, capture.Broadcast);

        await SetSettingAsync(instance, MetricShareSnapshot.KeyFor(MetricKind.Location), "true", cancellationToken);
        capture.Clear();
        await publisher.PublishTickAsync(unixMs: 2, cancellationToken);
        Assert.Contains(MetricKind.Location, capture.Broadcast);
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
        var capture = Capture(out var bus);

        // Location is globally off, but an override shares it for THIS fleet+character.
        await SetSettingAsync(instance, MetricShareSnapshot.OverrideKeyFor(FleetId, Owner, MetricKind.Location), "true", cancellationToken);
        await ServerFleetPublisher(instance, bus, MetricKind.Location).PublishTickAsync(1, cancellationToken);
        Assert.Contains(MetricKind.Location, capture.Broadcast);

        // DPS is globally on, but an override hides it for THIS fleet+character.
        await SetSettingAsync(instance, MetricShareSnapshot.OverrideKeyFor(FleetId, Owner, MetricKind.Dps), "false", cancellationToken);
        capture.Clear();
        await ServerFleetPublisher(instance, bus, MetricKind.Dps).PublishTickAsync(2, cancellationToken);
        Assert.DoesNotContain(MetricKind.Dps, capture.Broadcast);
    }

    [Fact]
    public async Task Publisher_LocalOnlyFleet_NeverLeavesTheMachine()
    {
        // 2026-06-04 (Option P): a local-only fleet is purely local — its samples only ever feed your own
        // graphs, so nothing is broadcast whatever the gate says, and everything still reaches your own row.
        var cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var capture = Capture(out var bus);

        await SetSettingAsync(instance, MetricShareSnapshot.KeyFor(MetricKind.Dps), "false", cancellationToken);

        var participation = new FleetParticipation();
        participation.Set([new FleetParticipant(Owner, FleetId, ClientOnly: true)]);
        var share = instance.Services.GetRequiredService<IMetricShareSettings>();

        await new FleetMetricPublisher(participation, [new FixedMetricSource(MetricKind.Dps)], bus, share)
            .PublishTickAsync(1, cancellationToken);
        Assert.Contains(MetricKind.Dps, capture.Local); // opted OUT, but it is your own graph

        await new FleetMetricPublisher(participation, [new FixedMetricSource(MetricKind.Location)], bus, share)
            .PublishTickAsync(2, cancellationToken);
        Assert.Contains(MetricKind.Location, capture.Local); // opt-IN default is off, but local-only still draws it

        Assert.Empty(capture.Broadcast); // and nothing left the machine
    }

    /// <summary>A bus wired to a recording transport, so a test can tell "drawn on my own client" from "broadcast".</summary>
    private static MetricCapture Capture(out IEventBus bus)
    {
        var capture = new MetricCapture();
        var inProcess = new InProcessEventBus(new RecordingTransport(capture.Broadcast));
        // Subscription lives for the test's duration; the bus is local to the test.
        inProcess.Subscribe<FleetMetricEvent>((e, _) =>
        {
            capture.Local.Add(e.Data.Kind);
            return Task.CompletedTask;
        });
        bus = inProcess;
        return capture;
    }

    private sealed class MetricCapture
    {
        public List<MetricKind> Local { get; } = [];
        public List<MetricKind> Broadcast { get; } = [];

        public void Clear()
        {
            Local.Clear();
            Broadcast.Clear();
        }
    }

    private sealed class RecordingTransport(List<MetricKind> broadcast) : IRemoteEventTransport
    {
        public Task SendAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
        {
            if (integrationEvent is FleetMetricEvent metric)
                broadcast.Add(metric.Data.Kind);
            return Task.CompletedTask;
        }
    }

    // A SERVER-backed participant: the per-metric share gate is a privacy boundary for what you broadcast to other
    // members on a server, so it only applies here (a local-only fleet never reaches the transport at all).
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
