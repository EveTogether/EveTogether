using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The fleet-metrics header badge counts the members standing in the fleet commander's solar system, off the same
/// <see cref="MetricKind.Location"/> samples the member rows already show. The commander counts in both halves of
/// the ratio; a fleet without a commander, or one whose commander shares no location, reads as unknown instead of
/// as a ratio nobody can act on.
/// </summary>
public class FleetMetricsCommanderPresenceTests
{
    private const int Commander = 90250177;
    private const int Member = 90250178;
    private const int Straggler = 90250179;
    private const long FleetId = 100;

    private static readonly FleetInfo Op = new(FleetId, "Op", null, FleetVisibility.Public, FleetState.Active, 1,
        null, null, DateTimeOffset.UnixEpoch, FleetActivation.Active);

    private static TestClientInstance CreateInstance() => TestClientInstance.Create(services =>
        services.AddSingleton<IExternalCharacterLookup>(new FakeExternalLookup
        {
            [Commander] = "RaymondKrah",
            [Member] = "Lionear",
            [Straggler] = "Tarek",
        }));

    private static FakeFleetClient RosterWithCommander(params int[] squadMembers) => new()
    {
        Members =
        [
            new FleetMemberInfo(1, Commander, -1, -1, FleetRole.FleetCommander, false),
            .. squadMembers.Select((characterId, index) =>
                new FleetMemberInfo(index + 2, characterId, 1, 1, FleetRole.SquadMember, false)),
        ],
    };

    // The roster pre-fill is async and the badge's denominator only means anything once it has landed, so every
    // test starts from a window that already shows the whole roster.
    private static async Task<(FleetMetricsViewModel Vm, IEventBus Bus)> BuildViewModelAsync(
        TestClientInstance instance, FakeFleetClient fleets)
    {
        var vm = new FleetMetricsViewModel(instance.Services, fleets, Op);

        for (var i = 0; i < 100 && vm.Members.Count < fleets.Members.Count; i++)
            await Task.Delay(20);
        Assert.Equal(fleets.Members.Count, vm.Members.Count);
        return (vm, instance.Services.GetRequiredService<IEventBus>());
    }

    private static async Task<FleetCommanderPresence> WaitForPresenceAsync(
        FleetMetricsViewModel vm, Func<FleetCommanderPresence, bool> ready)
    {
        for (var i = 0; i < 100 && !ready(vm.CommanderPresence); i++)
            await Task.Delay(20);
        return vm.CommanderPresence;
    }

    private static async Task PublishLocationAsync(IEventBus bus, FleetMetricsViewModel vm, int characterId, string system)
    {
        await bus.PublishAsync(new FleetMetricEvent(new MetricSample(characterId, FleetId, MetricKind.Location, 0, 0, system)));

        // Samples are routed onto the UI thread, so a published sample is not yet a rendered one.
        for (var i = 0; i < 100 && !vm.Members.Any(m => m.Location == system); i++)
            await Task.Delay(20);
    }

    [AvaloniaFact]
    public async Task Badge_CountsMembersInTheCommanderSystem_CommanderIncluded()
    {
        using var instance = CreateInstance();
        var (vm, bus) = await BuildViewModelAsync(instance, RosterWithCommander(Member, Straggler));

        await PublishLocationAsync(bus, vm, Commander, "Jita");
        await PublishLocationAsync(bus, vm, Member, "Jita");
        await PublishLocationAsync(bus, vm, Straggler, "Amarr");

        var presence = await WaitForPresenceAsync(vm, p => p.InSystem == 2);
        Assert.Equal(2, presence.InSystem);
        Assert.Equal(3, presence.Total);
        Assert.Equal("Jita", presence.CommanderSystem);
        Assert.Equal(FleetCommanderPresenceLevel.Partial, presence.Level);
        Assert.Equal("◉ 2/3 WITH FC", presence.BadgeText);
    }

    [AvaloniaFact]
    public async Task Badge_ReadsComplete_WhenEveryMemberIsInTheCommanderSystem()
    {
        using var instance = CreateInstance();
        var (vm, bus) = await BuildViewModelAsync(instance, RosterWithCommander(Member));

        await PublishLocationAsync(bus, vm, Commander, "Jita");
        await PublishLocationAsync(bus, vm, Member, "Jita");

        var presence = await WaitForPresenceAsync(vm, p => p.IsComplete);
        Assert.Equal(FleetCommanderPresenceLevel.Complete, presence.Level);
        Assert.Equal("◉ 2/2 WITH FC", presence.BadgeText);
    }

    [AvaloniaFact]
    public async Task Badge_FallsBackToPartial_WhenAMemberLeavesTheCommanderSystem()
    {
        using var instance = CreateInstance();
        var (vm, bus) = await BuildViewModelAsync(instance, RosterWithCommander(Member));

        await PublishLocationAsync(bus, vm, Commander, "Jita");
        await PublishLocationAsync(bus, vm, Member, "Jita");
        await WaitForPresenceAsync(vm, p => p.IsComplete);

        await PublishLocationAsync(bus, vm, Member, "Perimeter");

        var presence = await WaitForPresenceAsync(vm, p => !p.IsComplete);
        Assert.Equal(FleetCommanderPresenceLevel.Partial, presence.Level);
        Assert.Equal("◉ 1/2 WITH FC", presence.BadgeText);
    }

    [AvaloniaFact]
    public async Task Badge_MovesWithTheCommander_WhenTheCommanderJumps()
    {
        using var instance = CreateInstance();
        var (vm, bus) = await BuildViewModelAsync(instance, RosterWithCommander(Member));

        await PublishLocationAsync(bus, vm, Commander, "Jita");
        await PublishLocationAsync(bus, vm, Member, "Perimeter");
        await WaitForPresenceAsync(vm, p => p.CommanderSystem == "Jita");

        await PublishLocationAsync(bus, vm, Commander, "Perimeter");

        var presence = await WaitForPresenceAsync(vm, p => p.CommanderSystem == "Perimeter");
        Assert.Equal("Perimeter", presence.CommanderSystem);
        Assert.Equal(FleetCommanderPresenceLevel.Complete, presence.Level);
    }

    [AvaloniaFact]
    public async Task Badge_ReadsUnknown_WhenTheCommanderSharesNoLocation()
    {
        using var instance = CreateInstance();
        var (vm, bus) = await BuildViewModelAsync(instance, RosterWithCommander(Member));

        // Location sharing is opt-in: the member reports, the commander does not.
        await PublishLocationAsync(bus, vm, Member, "Jita");

        Assert.True(vm.CommanderPresence.IsUnknown);
        Assert.Equal("◉ — WITH FC", vm.CommanderPresence.BadgeText);
    }

    [AvaloniaFact]
    public async Task Badge_RendersInTheWindowHeader_AndTurnsGreenWhenComplete()
    {
        using var instance = CreateInstance();
        var (vm, bus) = await BuildViewModelAsync(instance, RosterWithCommander(Member));
        var window = new FleetMetricsWindow(vm) { Width = 720, Height = 560 };
        window.Show();

        await PublishLocationAsync(bus, vm, Commander, "Jita");
        await PublishLocationAsync(bus, vm, Member, "Jita");
        await WaitForPresenceAsync(vm, p => p.IsComplete);
        Dispatcher.UIThread.RunJobs();

        var badge = Assert.Single(window.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("chip"));
        var text = Assert.IsType<TextBlock>(badge.Child);
        Assert.Equal("◉ 2/2 WITH FC", text.Text);
        Assert.Contains("good", badge.Classes);
        Assert.DoesNotContain("dim", badge.Classes);
    }

    [AvaloniaFact]
    public async Task Badge_ReadsUnknown_WhenTheFleetHasNoCommander()
    {
        using var instance = CreateInstance();
        var fleets = new FakeFleetClient
        {
            Members = [new FleetMemberInfo(1, Member, 1, 1, FleetRole.SquadMember, false)],
        };
        var (vm, bus) = await BuildViewModelAsync(instance, fleets);

        await PublishLocationAsync(bus, vm, Member, "Jita");

        Assert.True(vm.CommanderPresence.IsUnknown);
        Assert.Equal(FleetCommanderPresenceLevel.Unknown, vm.CommanderPresence.Level);
    }
}
