using EveUtils.Client.Fleet;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Verifies the publisher is membership-driven: it shares for every (character, fleet) in the participation set,
/// not for a single "entered" fleet, and shares nothing when the set is empty.
/// </summary>
public class FleetParticipationPublishTests
{
    [Fact]
    public async Task PublishTick_PublishesForEveryParticipant()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var bus = instance.Services.GetRequiredService<IEventBus>();
        var share = instance.Services.GetRequiredService<IMetricShareSettings>();

        var captured = new List<(int Character, long Fleet)>();
        bus.Subscribe<FleetMetricEvent>((e, _) =>
        {
            captured.Add((e.Data.CharacterId, e.Data.FleetId));
            return Task.CompletedTask;
        });

        var participation = new FleetParticipation();
        participation.Set([
            new FleetParticipant(95000001, 11, ClientOnly: true),
            new FleetParticipant(95000002, 22, ClientOnly: true),
        ]);
        var publisher = new FleetMetricPublisher(participation, [new FixedMetricSource(MetricKind.Dps)], bus, share);

        await publisher.PublishTickAsync(unixMs: 1, cancellationToken);

        Assert.Contains((95000001, 11L), captured);
        Assert.Contains((95000002, 22L), captured);
    }

    [Fact]
    public async Task PublishTick_PublishesNothingWhenNotInAnyFleet()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var instance = TestClientInstance.Create();
        var bus = instance.Services.GetRequiredService<IEventBus>();
        var share = instance.Services.GetRequiredService<IMetricShareSettings>();

        var count = 0;
        bus.Subscribe<FleetMetricEvent>((_, _) => { count++; return Task.CompletedTask; });

        var publisher = new FleetMetricPublisher(new FleetParticipation(), [new FixedMetricSource(MetricKind.Dps)], bus, share);
        await publisher.PublishTickAsync(unixMs: 1, cancellationToken);

        Assert.Equal(0, count);
    }
}
