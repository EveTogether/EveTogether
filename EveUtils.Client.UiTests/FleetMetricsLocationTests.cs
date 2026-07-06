using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// A shared <see cref="MetricKind.Location"/> sample carries the member's solar system in <see cref="MetricSample.Text"/>
/// (the gamelog reports the name; the SDE store has no universe data to resolve an id). The fleet-metrics row must show
/// it, and a Location sample must land on the same row as the DPS samples regardless of which arrives first.
/// </summary>
public class FleetMetricsLocationTests
{
    private const int Member = 90250177;

    private static FleetMetricsViewModel BuildViewModel(TestClientInstance instance, out IEventBus bus)
    {
        var fleet = new FleetInfo(100, "Op", null, FleetVisibility.Public, FleetState.Active, 1,
            null, null, DateTimeOffset.UnixEpoch, FleetActivation.Active);
        bus = instance.Services.GetRequiredService<IEventBus>();
        return new FleetMetricsViewModel(instance.Services, new FakeFleetClient(), fleet);
    }

    private static async Task<DpsViewModel?> WaitForMemberAsync(FleetMetricsViewModel vm, Func<DpsViewModel, bool> ready)
    {
        for (var i = 0; i < 100; i++)
        {
            var member = vm.Members.FirstOrDefault();
            if (member is not null && ready(member))
                return member;
            await Task.Delay(20);
        }
        return vm.Members.FirstOrDefault();
    }

    [AvaloniaFact]
    public async Task LocationSample_SetsMemberLocation()
    {
        var lookup = new FakeExternalLookup { [Member] = "RaymondKrah" };
        using var instance = TestClientInstance.Create(services =>
            services.AddSingleton<IExternalCharacterLookup>(lookup));
        var vm = BuildViewModel(instance, out var bus);

        await bus.PublishAsync(new FleetMetricEvent(new MetricSample(Member, 100, MetricKind.Dps, 50, 0)));
        await bus.PublishAsync(new FleetMetricEvent(new MetricSample(Member, 100, MetricKind.Location, 0, 0, "Jita")));

        var member = await WaitForMemberAsync(vm, m => m.Location == "Jita");
        Assert.NotNull(member);
        Assert.Equal("Jita", member!.Location);
    }

    [AvaloniaFact]
    public async Task RosterMembers_ArePrefilled_WithoutAnySample()
    {
        var lookup = new FakeExternalLookup { [101] = "RaymondKrah", [102] = "Lionear" };
        using var instance = TestClientInstance.Create(services =>
            services.AddSingleton<IExternalCharacterLookup>(lookup));

        // The fleet has two roster members; the window must show both straight away — not wait for each to publish.
        var fleets = new FakeFleetClient
        {
            Members =
            [
                new FleetMemberInfo(1, 101, -1, -1, FleetRole.SquadMember, false),
                new FleetMemberInfo(2, 102, -1, -1, FleetRole.SquadMember, false),
            ],
        };
        var fleet = new FleetInfo(100, "Op", null, FleetVisibility.Public, FleetState.Active, 1,
            null, null, DateTimeOffset.UnixEpoch, FleetActivation.Active);
        var vm = new FleetMetricsViewModel(instance.Services, fleets, fleet);

        // No FleetMetricEvent published at all — the roster pre-fill alone must surface both members.
        for (var i = 0; i < 100 && vm.Members.Count < 2; i++)
            await Task.Delay(20);

        Assert.Equal(2, vm.Members.Count);
        Assert.Contains(vm.Members, m => m.Character == "RaymondKrah");
        Assert.Contains(vm.Members, m => m.Character == "Lionear");
    }

    [AvaloniaFact]
    public async Task LocationBeforeDps_LandsOnTheSameRow()
    {
        var lookup = new FakeExternalLookup { [Member] = "RaymondKrah" };
        using var instance = TestClientInstance.Create(services =>
            services.AddSingleton<IExternalCharacterLookup>(lookup));
        var vm = BuildViewModel(instance, out var bus);

        // Location arrives first — it must create the row, not be dropped — then DPS reuses that same row.
        await bus.PublishAsync(new FleetMetricEvent(new MetricSample(Member, 100, MetricKind.Location, 0, 0, "Amarr")));
        await bus.PublishAsync(new FleetMetricEvent(new MetricSample(Member, 100, MetricKind.Dps, 200, 0)));

        var member = await WaitForMemberAsync(vm, m => m.Location == "Amarr");
        Assert.NotNull(member);
        Assert.Equal("Amarr", member!.Location);
        Assert.Single(vm.Members);
    }
}
