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
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-52: an action on a fleet member updates every open screen that shows them, not only the one it was performed
/// in. The operator removed a pilot in fleet metrics with the fleet browser's tab standing open beside it: metrics
/// lost the row (ET-49) and the browser card kept the pilot as if nothing had happened.
///
/// This is the third round of the same theme — ET-46 was a screen handing back a stale snapshot, ET-49 a removed
/// pilot who stayed in metrics — so it is fixed as ONE mechanism rather than a third private hand-off between two
/// view-models: <see cref="IFleetRosterWatch"/>, which every screen showing a fleet roster subscribes to and every
/// screen changing one announces on. The tests below therefore drive each PAIR of screens in both directions.
///
/// The fleet under test is deliberately CLIENT-ONLY. A local fleet pushes no <c>fleet.changed</c> and never will, so
/// any fix leaning on that signal solves nothing here — and it is exactly the fleet the operator was looking at.
/// </summary>
public class FleetRosterFanOutTests
{
    private const int Owner = 95100001;      // the fleet's creator: its FC, and never removable
    private const int Alt = 95100002;        // a second character coupled on this client
    private const int External = 96100001;   // a pilot with no session here — a row on the browser card and nowhere else

    private sealed class FakeDisplay : IModuleHostDisplay
    {
        public bool IsFloating { get; set; }
        public ObservableCollection<HostTab> HostTabs { get; } = new();
        public HostTab? SelectedHostTab { get; set; }
    }

    /// <summary>Every shell the metrics screen reaches a user through — the dock↔float migration has twice been where
    /// a fix silently stopped applying (ET-30, ET-43).</summary>
    public enum Shell { OwnWindow, DockedTab, MigratedToFloating }

    private static TestClientInstance CreateInstance(RecordingDialogService? dialogs = null) =>
        TestClientInstance.Create(services =>
        {
            services.AddSingleton<IExternalCharacterLookup>(new FakeExternalLookup
            {
                [Owner] = "Jithran",
                [Alt] = "Abnoba Auscent",
                [External] = "Nomad Pilot",
            });
            if (dialogs is not null)
                services.AddSingleton<IDialogService>(dialogs);
        });

    private static RecordingDialogService AlwaysConfirms() => new() { OnConfirm = (_, _) => Task.FromResult(true) };

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

    // Lets everything queued settle, so "this did NOT happen" is a verdict rather than an early look.
    private static async Task SettleAsync()
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The operator's fleet: a client-only fleet holding the owner, one of their alts and an external pilot.</summary>
    private static async Task<long> SeedFleetAsync(TestClientInstance instance)
    {
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        await registry.AddOrUpdateAsync(new Character("Jithran", Owner));
        await registry.AddOrUpdateAsync(new Character("Abnoba Auscent", Alt));

        var fleets = instance.Services.GetRequiredService<ClientFleetService>();
        var created = await fleets.CreateLocalFleetAsync("Home Fleet", null, Owner);
        Assert.True(created.IsSuccess);
        Assert.True((await fleets.AddLocalCharacterAsync(created.Value, Alt, Owner)).IsSuccess);
        Assert.True((await fleets.AddExternalAsync(created.Value, External, Owner)).IsSuccess);
        return created.Value;
    }

    private static IFleetClient LocalClient(TestClientInstance instance) => new LocalFleetClient(
        instance.Services.GetRequiredService<ClientFleetService>(),
        instance.Services.GetRequiredService<IFleetRepository>(),
        instance.Services.GetRequiredService<ICharacterRegistry>(),
        Owner);

    private static async Task<FleetsViewModel> BrowserAsync(TestClientInstance instance)
    {
        var vm = new FleetsViewModel(instance.Services);
        Assert.True(await WaitForAsync(() => vm.LocalFleets.Count == 1 && vm.LocalFleets[0].Members.Count == 3),
            "the fleet browser never loaded the local fleet's card");
        return vm;
    }

    private static async Task<FleetMetricsViewModel> MetricsAsync(TestClientInstance instance, long fleetId)
    {
        var info = await LocalClient(instance).GetFleetAsync(fleetId);
        var vm = new FleetMetricsViewModel(instance.Services, LocalClient(instance), info!, Owner);
        Assert.True(await WaitForAsync(() => vm.Members.Count == 3), "the metrics roster pre-fill never landed");
        return vm;
    }

    private static async Task<FleetRosterViewModel> RosterAsync(TestClientInstance instance, long fleetId)
    {
        var info = await LocalClient(instance).GetFleetAsync(fleetId);
        var vm = new FleetRosterViewModel(instance.Services, LocalClient(instance), info!, isOwner: true, Owner);
        Assert.True(await WaitForAsync(() => vm.Entries.Count == 3), "the roster window never loaded");
        return vm;
    }

    // Presents the metrics screen the way the user has it: its own window, a docked tab through the REAL module host,
    // or a docked tab migrated back out to floating.
    private static Control Present(FleetMetricsViewModel vm, Shell shell)
    {
        var window = new FleetMetricsWindow(vm) { Width = 900, Height = 620 };
        if (shell is Shell.OwnWindow)
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            return window;
        }

        var display = new FakeDisplay { IsFloating = false };
        var host = new ModuleHostService();
        host.SetOwner(new Window());
        host.SetHost(display);
        host.Open(window, $"FLEET METRICS · {vm.FleetName}", "fleet", $"fleet-metrics:{vm.FleetId}");

        if (shell is Shell.MigratedToFloating)
        {
            display.IsFloating = true;
            host.SwitchMode();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            return window;
        }

        var root = new Window { Width = 900, Height = 620, Content = Assert.Single(display.HostTabs).Content };
        root.Show();
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
        return root;
    }

    // What a window actually paints. A member gone from a collection but still on screen is the failure this screen
    // family keeps producing, so every assertion here reads the rendered text as well.
    private static IReadOnlyList<string> Painted(Control root)
    {
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
        return root.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? string.Empty)
            .Where(t => t.Length > 0)
            .ToList();
    }

    // The removal as the shared flow performs it, from whichever screen supplies the transport — the same call fleet
    // metrics, the roster and the browser card each make.
    private static async Task RemoveAsync(TestClientInstance instance, long fleetId, int characterId)
    {
        var member = (await LocalClient(instance).ListMembersAsync(fleetId)).Single(m => m.CharacterId == characterId);
        var (status, _) = await instance.Services.GetRequiredService<FleetMemberRemovalService>().RemoveAsync(
            LocalClient(instance),
            new FleetMemberRemovalRequest(fleetId, member.Id, characterId, "Nomad Pilot", "Home Fleet"));
        Assert.Equal(FleetMemberRemovalStatus.RemovedFromFleet, status);
    }

    // --- The operator's case: metrics → the fleet browser ------------------------------------------------------

    /// <summary>
    /// Exactly what was reported. The FC removes a pilot in fleet metrics while the fleet-overview tab stands open;
    /// metrics loses the row and the browser card has to lose the leaf with it. Run through every shell, because a
    /// docked tab is where this screen family's fixes have twice stopped applying.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(Shell.OwnWindow)]
    [InlineData(Shell.DockedTab)]
    [InlineData(Shell.MigratedToFloating)]
    public async Task RemovingInFleetMetrics_TakesThePilotOffTheOpenFleetBrowserToo(Shell shell)
    {
        using var instance = CreateInstance(AlwaysConfirms());
        long fleetId = await SeedFleetAsync(instance);

        var browser = await BrowserAsync(instance);
        browser.LocalFleets[0].ToggleExpandedCommand.Execute(null);   // a row starts folded (ET-170); open it to paint its members
        var browserWindow = new FleetsWindow(browser) { Width = 760, Height = 700 };
        browserWindow.Show();

        var metrics = await MetricsAsync(instance, fleetId);
        var metricsRoot = Present(metrics, shell);

        Assert.Contains("Nomad Pilot", Painted(browserWindow));   // both screens agree before the removal
        Assert.Contains("Nomad Pilot", Painted(metricsRoot));

        await RemoveAsync(instance, fleetId, External);

        Assert.DoesNotContain(metrics.Members, m => m.CharacterId == External);
        Assert.True(await WaitForAsync(() => browser.LocalFleets[0].Members.All(m => m.CharacterId != External)),
            "the fleet browser's card still lists a pilot who was removed in fleet metrics");

        Assert.DoesNotContain("Nomad Pilot", Painted(browserWindow));
        Assert.DoesNotContain("Nomad Pilot", Painted(metricsRoot));

        // …and neither screen lost anyone who is still in the fleet.
        Assert.Contains("Jithran", Painted(browserWindow));
        Assert.Contains("Abnoba Auscent", Painted(browserWindow));

        browserWindow.Close();
        browser.Dispose();
        metrics.Dispose();
    }

    /// <summary>The other direction of the same pair: removing on the browser card clears the standing metrics screen,
    /// including its roll-up totals, which is the figure an FC steers on.</summary>
    [AvaloniaFact]
    public async Task RemovingOnTheBrowserCard_TakesThePilotOffAStandingMetricsScreen()
    {
        using var instance = CreateInstance(AlwaysConfirms());
        long fleetId = await SeedFleetAsync(instance);

        var browser = await BrowserAsync(instance);
        var metrics = await MetricsAsync(instance, fleetId);
        var metricsRoot = Present(metrics, Shell.DockedTab);

        var leaf = browser.LocalFleets[0].Members.Single(m => m.CharacterId == External);
        Assert.NotNull(leaf.MemberMenu.SingleOrDefault(i => i.Label.StartsWith("Remove ", StringComparison.Ordinal)));
        await RemoveAsync(instance, fleetId, External);

        Assert.True(await WaitForAsync(() => metrics.Members.All(m => m.CharacterId != External)),
            "fleet metrics still shows a pilot removed on the fleet browser's card");
        Assert.DoesNotContain("Nomad Pilot", Painted(metricsRoot));

        browser.Dispose();
        metrics.Dispose();
    }

    /// <summary>And the third screen. A removal in the roster window has to reach both of the others — one
    /// announcement, however many listeners, which is the whole point of doing this once instead of per pair.</summary>
    [AvaloniaFact]
    public async Task RemovingInTheRosterWindow_ReachesTheBrowserAndFleetMetrics()
    {
        using var instance = CreateInstance(AlwaysConfirms());
        long fleetId = await SeedFleetAsync(instance);

        var browser = await BrowserAsync(instance);
        var roster = await RosterAsync(instance, fleetId);
        var metrics = await MetricsAsync(instance, fleetId);

        await RemoveAsync(instance, fleetId, External);

        Assert.True(await WaitForAsync(() =>
                metrics.Members.All(m => m.CharacterId != External)
                && browser.LocalFleets[0].Members.All(m => m.CharacterId != External)
                && roster.Entries.Count == 2),
            "a removal in the roster window did not reach every open screen");

        browser.Dispose();
        roster.Dispose();
        metrics.Dispose();
    }

    /// <summary>An add is the same news the other way round: a pilot put on the card by ADD EXTERNAL turns up on a
    /// metrics screen that is already open, without the FC reopening it. An external publishes no sample of their
    /// own, so nothing else would ever have put them there.</summary>
    [AvaloniaFact]
    public async Task AddingAPilotOnTheCard_PutsThemOnAStandingMetricsScreen()
    {
        const int Joiner = 96100002;
        var dialogs = AlwaysConfirms();
        dialogs.OnAddExternalMember = _ => Task.FromResult<int?>(Joiner);

        using var instance = CreateInstance(dialogs);
        long fleetId = await SeedFleetAsync(instance);

        var browser = await BrowserAsync(instance);
        var metrics = await MetricsAsync(instance, fleetId);

        await browser.AddLocalExternalCommand.ExecuteAsync(browser.LocalFleets[0]);

        Assert.True(await WaitForAsync(() => metrics.Members.Any(m => m.CharacterId == Joiner)),
            "a pilot added on the browser card never appeared on the open metrics screen");

        browser.Dispose();
        metrics.Dispose();
    }

    // --- The pop-out, ET-43's fourth presentation path ----------------------------------------------------------

    /// <summary>The DPS pop-out is a window of its own onto a member's tracker, so it is a screen showing that pilot
    /// like any other. When their row goes, it goes: left standing it can only ever show the last frame from before
    /// the removal, because the samples that fed it are dropped from then on (ET-49).</summary>
    [AvaloniaFact]
    public async Task RemovingAMember_ClosesTheirDpsPopOut()
    {
        var dialogs = AlwaysConfirms();
        using var instance = CreateInstance(dialogs);
        long fleetId = await SeedFleetAsync(instance);

        var metrics = await MetricsAsync(instance, fleetId);
        DpsViewModel tracker = metrics.Members.Single(m => m.CharacterId == External);

        await RemoveAsync(instance, fleetId, External);
        await SettleAsync();

        Assert.Contains(tracker, dialogs.ClosedDpsOverlays);

        // A member who is still in the fleet keeps their pop-out — this closes one window, not every window.
        Assert.DoesNotContain(metrics.Members.Single(m => m.CharacterId == Alt), dialogs.ClosedDpsOverlays);
        metrics.Dispose();
    }

    // --- The lines the mechanism must not cross -----------------------------------------------------------------

    /// <summary>ET-49's straggler, unchanged: a pilot the roster has NEVER named arrived through a sample alone and is
    /// in the fleet in-game. A roster change about someone else is not a reason to take their row away, and the
    /// removed/never-named distinction still comes from the one place that knows it.</summary>
    [AvaloniaFact]
    public async Task AChangeAboutOnePilot_LeavesAStraggler_AndEveryoneElse_Alone()
    {
        const int Straggler = 96100003;
        using var instance = CreateInstance(AlwaysConfirms());
        long fleetId = await SeedFleetAsync(instance);
        var metrics = await MetricsAsync(instance, fleetId);

        await instance.Services.GetRequiredService<EveUtils.Shared.Messaging.IEventBus>().PublishAsync(
            new EveUtils.Shared.Modules.Fleet.Events.FleetMetricEvent(
                new EveUtils.Shared.Modules.Fleet.Dtos.MetricSample(
                    Straggler, fleetId, EveUtils.Shared.Modules.Fleet.Metrics.MetricKind.Location, 0, 0, "Jita")));
        Assert.True(await WaitForAsync(() => metrics.Members.Any(m => m.CharacterId == Straggler)),
            "the straggler never got a row");

        await RemoveAsync(instance, fleetId, External);
        await SettleAsync();

        Assert.Contains(metrics.Members, m => m.CharacterId == Straggler);
        Assert.Contains(metrics.Members, m => m.CharacterId == Alt);
        Assert.DoesNotContain(metrics.Members, m => m.CharacterId == External);
        metrics.Dispose();
    }

    /// <summary>A change to another fleet is not this screen's news. Two fleets, one removal: the screen watching the
    /// other one must not so much as re-read.</summary>
    [AvaloniaFact]
    public async Task AChangeToAnotherFleet_LeavesThisScreenAlone()
    {
        using var instance = CreateInstance(AlwaysConfirms());
        long fleetId = await SeedFleetAsync(instance);
        var metrics = await MetricsAsync(instance, fleetId);

        var other = await instance.Services.GetRequiredService<ClientFleetService>()
            .CreateLocalFleetAsync("Some other fleet", null, Owner);
        Assert.True(other.IsSuccess);

        instance.Services.GetRequiredService<IFleetRosterWatch>()
            .Announce(FleetRosterChange.Removed(other.Value, External));
        await SettleAsync();

        Assert.Contains(metrics.Members, m => m.CharacterId == External);
        metrics.Dispose();
    }

    /// <summary>A screen whose window is closed is disposed, and a disposed screen takes no more news — otherwise the
    /// watch would keep a torn-down view-model and its render registrations alive for the life of the client.</summary>
    [AvaloniaFact]
    public async Task ADisposedScreen_StopsListening()
    {
        using var instance = CreateInstance(AlwaysConfirms());
        long fleetId = await SeedFleetAsync(instance);
        var metrics = await MetricsAsync(instance, fleetId);
        metrics.Dispose();

        await RemoveAsync(instance, fleetId, External);
        await SettleAsync();

        // Still there: nothing touched the collection after the dispose, which is what "stopped listening" looks like.
        Assert.Contains(metrics.Members, m => m.CharacterId == External);
    }
}
