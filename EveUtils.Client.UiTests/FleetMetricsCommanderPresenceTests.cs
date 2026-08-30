using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
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
///
/// ET-63 took the members nobody has a location for out of the denominator and named them beside it: one silent
/// pilot used to hold the badge off green however plainly the rest of the fleet stood with the FC.
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
        await PublishAsync(bus, characterId, system);

        // Samples are routed onto the UI thread, so a published sample is not yet a rendered one.
        for (var i = 0; i < 100 && vm.Members.All(m => m.Location != system); i++)
            await Task.Delay(20);
    }

    // Publish only. Where several members move to the same system, "somebody is in Jita" stops being a signal that
    // this member's sample has landed, so those tests settle on the presence figure itself instead.
    private static Task PublishAsync(IEventBus bus, int characterId, string system) =>
        bus.PublishAsync(new FleetMetricEvent(new MetricSample(characterId, FleetId, MetricKind.Location, 0, 0, system)));

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
        Assert.Equal(3, presence.Known);
        Assert.Equal(3, presence.Total);
        Assert.Equal(0, presence.UnknownLocations);
        Assert.Equal("Jita", presence.CommanderSystem);
        Assert.Equal(FleetCommanderPresenceLevel.Partial, presence.Level);

        // Nobody is unknown here, so the badge says nothing about unknowns — no trailing "(0 unknown)".
        Assert.Equal("◉ 2/3 WITH FC", presence.BadgeText);
    }

    [AvaloniaFact]
    public async Task Badge_ReadsComplete_WhenEveryMemberIsInTheCommanderSystem()
    {
        using var instance = CreateInstance();
        var (vm, bus) = await BuildViewModelAsync(instance, RosterWithCommander(Member));

        await PublishLocationAsync(bus, vm, Commander, "Jita");
        await PublishLocationAsync(bus, vm, Member, "Jita");

        // Both members known, and both with the FC. Waiting on IsComplete alone would settle the moment the
        // commander is the only member we have a location for: one of one reads complete too.
        var presence = await WaitForPresenceAsync(vm, p => p.Known == 2 && p.IsComplete);
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
        await WaitForPresenceAsync(vm, p => p.Known == 2 && p.IsComplete);

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

        var presence = await WaitForPresenceAsync(vm, p => p.CommanderSystem == "Perimeter" && p.Known == 2);
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
        await WaitForPresenceAsync(vm, p => p.Known == 2 && p.IsComplete);
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

    // ---- ET-63: the members nobody has a location for are counted apart, not against the ratio -------------------

    /// <summary>
    /// The operator's example, end to end. Ten in the fleet: five stand with the FC (the FC among them), three are
    /// somewhere else, and two share no location at all. It used to read 5/10 and could never reach green while
    /// those two sat there; now they are named beside the ratio instead of inside it, and the three coming in is
    /// enough.
    /// </summary>
    [AvaloniaFact]
    public async Task Badge_LeavesUnknownLocationsOutOfTheDenominator_AndNamesThemBeside()
    {
        using var instance = CreateInstance();
        var (vm, bus) = await BuildViewModelAsync(instance, RosterOfTen());

        await PublishAsync(bus, Commander, "Jita");
        foreach (var present in WithFc)          // four more in the commander's system: five in all
            await PublishAsync(bus, present, "Jita");
        foreach (var away in Elsewhere)          // three in another system
            await PublishAsync(bus, away, "Amarr");
        // The two in Silent publish nothing at all — location sharing is opt-in, and nothing else has named them.

        var presence = await WaitForPresenceAsync(vm, p => p.InSystem == 5 && p.Known == 8);
        Assert.Equal(5, presence.InSystem);
        Assert.Equal(8, presence.Known);
        Assert.Equal(2, presence.UnknownLocations);
        Assert.Equal(10, presence.Total);
        Assert.Equal(FleetCommanderPresenceLevel.Partial, presence.Level);
        Assert.Equal("◉ 5/8 WITH FC (2 unknown)", presence.BadgeText);
        Assert.Equal(
            "5 of 8 fleet members with a known location are in Jita with the fleet commander. " +
            "2 more share no location and are left out of the count.",
            presence.Tooltip);

        // The three arrive. Every member we have a location for now stands with the FC, so the badge goes green —
        // which is the whole point: the two unknowns no longer veto it.
        foreach (var late in Elsewhere)
            await PublishAsync(bus, late, "Jita");

        presence = await WaitForPresenceAsync(vm, p => p.Known == 8 && p.IsComplete);
        Assert.Equal(FleetCommanderPresenceLevel.Complete, presence.Level);
        Assert.Equal("◉ 8/8 WITH FC (2 unknown)", presence.BadgeText);
        Assert.Equal(2, presence.UnknownLocations);
    }

    /// <summary>
    /// The easiest case to lose. A commander system with no member locations at all counts 0 of 0, and "every known
    /// member stands with the FC" is trivially true of an empty set — so without an explicit guard the badge would
    /// go green over a fleet nobody has been seen in. It reads as the neutral unknown state instead, exactly as a
    /// commander who shares nothing already did.
    ///
    /// Straight against <see cref="FleetCommanderPresence.From"/>: the screen itself cannot reach this, because it
    /// takes the commander's system from a member row that is in the very list it counts. That is what makes the
    /// case easy to leave unguarded, not a reason to leave it unguarded.
    /// </summary>
    [Fact]
    public void Badge_ReadsUnknown_WhenNoMemberLocationIsKnownAtAll()
    {
        var presence = FleetCommanderPresence.From("Jita", [null, null, "  "]);

        Assert.Equal(0, presence.InSystem);
        Assert.Equal(0, presence.Known);
        Assert.Equal(3, presence.UnknownLocations);
        Assert.Equal(FleetCommanderPresenceLevel.Unknown, presence.Level);
        Assert.False(presence.IsComplete);
        Assert.True(presence.IsUnknown);
        Assert.Equal("◉ — WITH FC", presence.BadgeText);
    }

    /// <summary>
    /// The ratio and the green rows are one verdict and cannot drift apart: the members the badge counts are
    /// exactly the members <see cref="FleetCommanderPresence.IsWith"/> agrees with, and a member with no location
    /// is neither counted nor coloured.
    /// </summary>
    [AvaloniaFact]
    public async Task AMemberWithNoLocation_IsNeitherCounted_NorColouredGreen()
    {
        using var instance = CreateInstance();
        var (vm, bus) = await BuildViewModelAsync(instance, RosterWithCommander(Member, Straggler));

        await PublishLocationAsync(bus, vm, Commander, "Jita");
        await PublishLocationAsync(bus, vm, Member, "Jita");
        // Straggler shares nothing.

        var presence = await WaitForPresenceAsync(vm, p => p.Known == 2 && p.IsComplete);
        Assert.Equal("◉ 2/2 WITH FC (1 unknown)", presence.BadgeText);

        // Names land one lookup after the row itself, so wait for the one being asserted on rather than the count.
        for (var i = 0; i < 100 && vm.Members.All(m => m.Character != "Tarek"); i++)
            await Task.Delay(20);

        var silent = vm.Members.Single(m => m.Character == "Tarek");
        Assert.Null(silent.Location);
        Assert.False(presence.IsWith(silent.Location));
        Assert.False(silent.IsWithCommander);

        // Counted and coloured are the same members, off the same verdict.
        Assert.Equal(presence.InSystem, vm.Members.Count(m => m.IsWithCommander));
        Assert.Equal(presence.Known, vm.Members.Count(m => !string.IsNullOrWhiteSpace(m.Location)));
    }

    /// <summary>
    /// Rendered, in every layout ET-30 offers and in both shells this screen reaches a user through. The badge is
    /// header-level and ought to be layout-proof, but "the tests were green and the operator saw something else"
    /// has happened here often enough that the text is read off the visual tree rather than off the view-model.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(FleetMetricsLayout.List, false)]
    [InlineData(FleetMetricsLayout.List, true)]
    [InlineData(FleetMetricsLayout.Grid, false)]
    [InlineData(FleetMetricsLayout.Grid, true)]
    [InlineData(FleetMetricsLayout.Compact, false)]
    [InlineData(FleetMetricsLayout.Compact, true)]
    public async Task Badge_RendersTheUnknownCount_AndGoesGreenWithoutThem(FleetMetricsLayout layout, bool docked)
    {
        using var instance = CreateInstance();
        var (vm, bus) = await BuildViewModelAsync(instance, RosterWithCommander(Member, Straggler));

        await PublishLocationAsync(bus, vm, Commander, "Jita");
        await PublishLocationAsync(bus, vm, Member, "Amarr");
        // Straggler shares nothing.
        await WaitForPresenceAsync(vm, p => p.Known == 2);

        vm.SetLayoutCommand.Execute(layout);
        Control root = Show(vm, docked);

        var badge = Assert.Single(root.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("chip"));
        var text = Assert.IsType<TextBlock>(badge.Child);
        Assert.Equal("◉ 1/2 WITH FC (1 unknown)", text.Text);
        Assert.DoesNotContain("good", badge.Classes);
        Assert.DoesNotContain("dim", badge.Classes);

        // The straggler stays unknown, and the badge still turns green on the members we do know about.
        await PublishLocationAsync(bus, vm, Member, "Jita");
        await WaitForPresenceAsync(vm, p => p.Known == 2 && p.IsComplete);
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();

        Assert.Equal("◉ 2/2 WITH FC (1 unknown)", text.Text);
        Assert.Contains("good", badge.Classes);
    }

    /// <summary>Nothing known about anybody keeps the neutral grey badge — rendered, in both shells.</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Badge_RendersNeutral_WhenEveryLocationIsUnknown(bool docked)
    {
        using var instance = CreateInstance();
        var (vm, _) = await BuildViewModelAsync(instance, RosterWithCommander(Member, Straggler));

        Control root = Show(vm, docked);

        var badge = Assert.Single(root.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("chip"));
        var text = Assert.IsType<TextBlock>(badge.Child);
        Assert.Equal("◉ — WITH FC", text.Text);
        Assert.Contains("dim", badge.Classes);
        Assert.DoesNotContain("good", badge.Classes);
    }

    // ---- harness ------------------------------------------------------------------------------------------------

    private static readonly int[] WithFc = [90250181, 90250182, 90250183, 90250184];
    private static readonly int[] Elsewhere = [90250185, 90250186, 90250187];
    private static readonly int[] Silent = [90250188, 90250189];

    private static FakeFleetClient RosterOfTen() => RosterWithCommander([.. WithFc, .. Elsewhere, .. Silent]);

    // Both ways this screen reaches a user. Docked is the default and the one that has bitten twice: the module host
    // does not show the module's own window, it lifts window.Content out and reparents it into a tab.
    private static Control Show(FleetMetricsViewModel vm, bool docked)
    {
        var window = new FleetMetricsWindow(vm) { Width = 900, Height = 620 };
        Window root = window;

        if (docked)
        {
            var display = new FakeDisplay { IsFloating = false };
            var host = new ModuleHostService();
            host.SetOwner(new Window());
            host.SetHost(display);
            host.Open(window, "FLEET METRICS", "fleet");
            root = new Window { Width = 900, Height = 620, Content = Assert.Single(display.HostTabs).Content };
        }

        root.Show();
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
        return root;
    }

    private sealed class FakeDisplay : IModuleHostDisplay
    {
        public bool IsFloating { get; set; }
        public ObservableCollection<HostTab> HostTabs { get; } = new();
        public HostTab? SelectedHostTab { get; set; }
    }
}
