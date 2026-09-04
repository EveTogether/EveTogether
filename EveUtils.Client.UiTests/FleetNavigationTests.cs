using System;
using System.Collections.Generic;
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
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-171: every fleet screen has a way out of it.
///
/// The complaint was "from metrics I cannot get to the fleet manager", and it was literally true rather than merely
/// awkward: <c>FleetMetricsWindow</c> carried no button at all besides the pop-outs and the density switcher, and the
/// roster had no way back to the overview either. Closing the screen was the only exit, and where that left you
/// depended on whether the modules happened to be docked (a tab strip to reach for) or floating (nothing).
///
/// The rule from screen 8 of the mockup: the overview is home, every other fleet screen leads back to it, and the
/// roster and metrics know each other in BOTH directions. So four routes, of which one already existed:
///
///   roster  → metrics    already there (the header's METRICS button, now in the navigation bar)
///   metrics → roster     new
///   metrics → overview   new
///   roster  → overview   new
///
/// These tests drive the buttons that are actually on the screen, in both shells the app presents them through — an
/// own window and a docked tab — because that pair is where fleet-screen fixes have quietly stopped applying before
/// (ET-30, ET-42, ET-43).
/// </summary>
public class FleetNavigationTests
{
    private const int Owner = 95200001;
    private const int Alt = 95200002;

    private sealed class FakeDisplay : IModuleHostDisplay
    {
        public bool IsFloating { get; set; }
        public ObservableCollection<HostTab> HostTabs { get; } = new();
        public HostTab? SelectedHostTab { get; set; }
    }

    /// <summary>Both shells a fleet screen reaches the pilot through. The nav bar has to work in each.</summary>
    public enum Shell { OwnWindow, DockedTab }

    private static TestClientInstance CreateInstance(RecordingDialogService dialogs) =>
        TestClientInstance.Create(services =>
        {
            services.AddSingleton<IExternalCharacterLookup>(new FakeExternalLookup
            {
                [Owner] = "Jithran",
                [Alt] = "Abnoba Auscent",
            });
            services.AddSingleton<IDialogService>(dialogs);
        });

    private static async Task<bool> WaitForAsync(Func<bool> condition, int tries = 200)
    {
        for (var i = 0; i < tries; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition())
                return true;
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    /// <summary>A client-only fleet with the owner and one alt on it — enough to open both screens for.</summary>
    private static async Task<long> SeedFleetAsync(TestClientInstance instance)
    {
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        await registry.AddOrUpdateAsync(new Character("Jithran", Owner));
        await registry.AddOrUpdateAsync(new Character("Abnoba Auscent", Alt));

        var fleets = instance.Services.GetRequiredService<ClientFleetService>();
        var created = await fleets.CreateLocalFleetAsync("Wednesday Homefronts", null, Owner);
        Assert.True(created.IsSuccess);
        Assert.True((await fleets.AddLocalCharacterAsync(created.Value, Alt, Owner)).IsSuccess);
        return created.Value;
    }

    private static IFleetClient LocalClient(TestClientInstance instance) => new LocalFleetClient(
        instance.Services.GetRequiredService<ClientFleetService>(),
        instance.Services.GetRequiredService<IFleetRepository>(),
        instance.Services.GetRequiredService<ICharacterRegistry>(),
        Owner);

    private static async Task<FleetMetricsViewModel> MetricsAsync(TestClientInstance instance, long fleetId)
    {
        var info = await LocalClient(instance).GetFleetAsync(fleetId);
        var vm = new FleetMetricsViewModel(instance.Services, LocalClient(instance), info!, Owner);
        Assert.True(await WaitForAsync(() => vm.Members.Count == 2), "the metrics roster pre-fill never landed");
        return vm;
    }

    private static async Task<FleetRosterViewModel> RosterAsync(TestClientInstance instance, long fleetId)
    {
        var info = await LocalClient(instance).GetFleetAsync(fleetId);
        var vm = new FleetRosterViewModel(instance.Services, LocalClient(instance), info!, isOwner: true, Owner);
        Assert.True(await WaitForAsync(() => vm.Entries.Count == 2), "the roster window never loaded");
        return vm;
    }

    /// <summary>
    /// Presents a screen the way the pilot has it. The docked path goes through the REAL module host, so the content
    /// is reparented out of the window exactly as it is in the app — which is what makes this a test of the styling
    /// rule as well as of the wiring (ET-42: a style left on the window is dropped on the way into a tab).
    /// </summary>
    private static Control Present(Window window, string title, string moduleId, Shell shell)
    {
        window.Width = 900;
        window.Height = 620;
        if (shell is Shell.OwnWindow)
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            return window;
        }

        var host = new ModuleHostService();
        host.SetOwner(new Window());
        var display = new FakeDisplay { IsFloating = false };
        host.SetHost(display);
        host.Open(window, title, "fleet", moduleId);

        var root = new Window { Width = 900, Height = 620, Content = Assert.Single(display.HostTabs).Content };
        root.Show();
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
        return root;
    }

    private static Button ButtonNamed(Control root, string name) =>
        Assert.Single(root.GetVisualDescendants().OfType<Button>(), b => b.Name == name && b.IsVisible);

    /// <summary>Every text painted on the screen, so "the trail says which fleet this is" is read rather than assumed.</summary>
    private static IReadOnlyList<string> Painted(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text))
            .Select(t => t.Text!.Trim())
            .ToList();

    // ── metrics: the screen the complaint was about ──────────────────────────────────────────────────────────────

    [AvaloniaTheory]
    [InlineData(Shell.OwnWindow)]
    [InlineData(Shell.DockedTab)]
    public async Task Metrics_reaches_the_roster_for_the_same_fleet(Shell shell)
    {
        var dialogs = new RecordingDialogService();
        using var instance = CreateInstance(dialogs);
        var fleetId = await SeedFleetAsync(instance);
        var vm = await MetricsAsync(instance, fleetId);
        var root = Present(new FleetMetricsWindow(vm), $"FLEET METRICS · {vm.FleetName}", $"fleet-metrics:{fleetId}", shell);

        ButtonNamed(root, "OpenRosterButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var opened = Assert.Single(dialogs.OpenedRosters);
        Assert.Equal("Wednesday Homefronts", opened.FleetName);
        // The same fleet, not "a" fleet: with several fleets open this is the mistake that reads as working.
        Assert.Equal(fleetId, opened.FleetId);
        opened.Dispose();
        vm.Dispose();
    }

    [AvaloniaTheory]
    [InlineData(Shell.OwnWindow)]
    [InlineData(Shell.DockedTab)]
    public async Task Metrics_reaches_the_overview(Shell shell)
    {
        var dialogs = new RecordingDialogService();
        using var instance = CreateInstance(dialogs);
        var fleetId = await SeedFleetAsync(instance);
        var vm = await MetricsAsync(instance, fleetId);
        var root = Present(new FleetMetricsWindow(vm), $"FLEET METRICS · {vm.FleetName}", $"fleet-metrics:{fleetId}", shell);

        ButtonNamed(root, "BackToFleetsButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var overview = Assert.Single(dialogs.OpenedFleetOverviews);
        overview.Dispose();
        vm.Dispose();
    }

    /// <summary>
    /// The roster reached from metrics is the WHOLE roster. Its doctrine section hangs off a composition client the
    /// overview passes in but metrics was never handed — so without the fleet client answering for it, the same
    /// screen would quietly show less depending on which door you came through.
    /// </summary>
    [AvaloniaFact]
    public async Task Roster_opened_from_metrics_keeps_its_doctrine()
    {
        var dialogs = new RecordingDialogService();
        using var instance = CreateInstance(dialogs);
        var fleetId = await SeedFleetAsync(instance);
        var vm = await MetricsAsync(instance, fleetId);
        var root = Present(new FleetMetricsWindow(vm), $"FLEET METRICS · {vm.FleetName}", $"fleet-metrics:{fleetId}", Shell.OwnWindow);

        ButtonNamed(root, "OpenRosterButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var opened = Assert.Single(dialogs.OpenedRosters);
        Assert.True(await WaitForAsync(() => opened.CanCoupleComposition),
            "the roster reached from metrics has no doctrine client, so its composition section is gone");
        opened.Dispose();
        vm.Dispose();
    }

    // ── roster ───────────────────────────────────────────────────────────────────────────────────────────────────

    [AvaloniaTheory]
    [InlineData(Shell.OwnWindow)]
    [InlineData(Shell.DockedTab)]
    public async Task Roster_reaches_the_overview(Shell shell)
    {
        var dialogs = new RecordingDialogService();
        using var instance = CreateInstance(dialogs);
        var fleetId = await SeedFleetAsync(instance);
        var vm = await RosterAsync(instance, fleetId);
        var root = Present(new FleetRosterWindow(vm), $"FLEET ROSTER · {vm.FleetName}", $"fleet-roster:{fleetId}", shell);

        ButtonNamed(root, "BackToFleetsButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var overview = Assert.Single(dialogs.OpenedFleetOverviews);
        overview.Dispose();
        vm.Dispose();
    }

    /// <summary>METRICS moved out of the header into the navigation bar; it still has to be there and still work.</summary>
    [AvaloniaTheory]
    [InlineData(Shell.OwnWindow)]
    [InlineData(Shell.DockedTab)]
    public async Task Roster_reaches_metrics_for_the_same_fleet(Shell shell)
    {
        var dialogs = new RecordingDialogService();
        using var instance = CreateInstance(dialogs);
        var fleetId = await SeedFleetAsync(instance);
        var vm = await RosterAsync(instance, fleetId);
        var root = Present(new FleetRosterWindow(vm), $"FLEET ROSTER · {vm.FleetName}", $"fleet-roster:{fleetId}", shell);

        ButtonNamed(root, "NavMetricsButton").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        var opened = Assert.Single(dialogs.OpenedFleetMetrics);
        Assert.Equal(fleetId, opened.FleetId);
        opened.Dispose();
        vm.Dispose();
    }

    // ── which fleet am I in? ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// With three tabs open for one fleet, all titled "FLEET…", the screen itself has to say which fleet it is
    /// about. In a floating window there is no tab strip to read at all. Both screens carry the trail.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(Shell.OwnWindow)]
    [InlineData(Shell.DockedTab)]
    public async Task Both_screens_name_their_fleet_in_the_trail(Shell shell)
    {
        var dialogs = new RecordingDialogService();
        using var instance = CreateInstance(dialogs);
        var fleetId = await SeedFleetAsync(instance);

        var metrics = await MetricsAsync(instance, fleetId);
        var metricsRoot = Present(new FleetMetricsWindow(metrics), "FLEET METRICS", $"fleet-metrics:{fleetId}", shell);
        var metricsCrumbs = metricsRoot.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("crumblink")).Select(b => b.Content as string).ToList();
        Assert.Contains("FLEETS", metricsCrumbs);
        Assert.Contains("Wednesday Homefronts", metricsCrumbs);
        Assert.Contains("METRICS", Painted(metricsRoot));
        metrics.Dispose();

        var roster = await RosterAsync(instance, fleetId);
        var rosterRoot = Present(new FleetRosterWindow(roster), "FLEET ROSTER", $"fleet-roster:{fleetId}", shell);
        var painted = Painted(rosterRoot);
        Assert.Contains("Wednesday Homefronts", painted);
        Assert.Contains("ROSTER", painted);
        roster.Dispose();
    }
}
