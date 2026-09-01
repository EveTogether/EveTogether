using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Theming;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The fleet pop-out (ET-72). Three things are under test and they are different kinds of thing: that the window
/// names the right pilot, that it goes on naming the same one while the figures wobble, and that it looks calm when
/// nothing is happening — the last of which is only answerable by rendering it.
/// </summary>
public class FleetOverlayTests
{
    // ---- A fleet, without a service provider, a transport or a bus behind it. -------------------------------------

    private sealed class FakeFleet : IFleetOverlaySource
    {
        public string FleetName { get; init; } = "Home Defence";
        public long FleetId { get; init; } = 77;
        public List<DpsViewModel> Rows { get; } = [];
        public IReadOnlyList<DpsViewModel> Members => Rows;
        public FleetCommanderPresence CommanderPresence { get; set; } = FleetCommanderPresence.Unknown;
    }

    private static readonly DateTime Now = new(2026, 8, 30, 20, 0, 0, DateTimeKind.Utc);

    private static readonly FleetInfo Op = new(77, "Home Defence", null, FleetVisibility.Public, FleetState.Active, 1,
        null, null, DateTimeOffset.UnixEpoch, FleetActivation.Active);

    /// <summary>A member reporting right now. Rates are set the way the real screen sets them — through the metric
    /// kinds and the shared smoothing — so the test cannot pass on a figure production never produces.</summary>
    private static DpsViewModel Member(string name, long dpsIn = 0, long neutIn = 0, DateTime? lastSampleAt = null)
    {
        var row = new DpsViewModel(name, isSelf: false) { CharacterId = Math.Abs(name.GetHashCode()) % 1_000_000 };
        Settle(row, dpsIn, neutIn, lastSampleAt ?? Now);
        return row;
    }

    // One tick of the real thing: new rates in, and the sample stamped — the screen stamps LastSampleAt on every
    // sample it routes, and a test that let it go stale would be testing something else. The EMA needs a handful of
    // frames to reach its target; the render driver gives it 30 a second.
    private static void Settle(DpsViewModel row, long dpsIn, long neutIn, DateTime? at = null)
    {
        row.LastSampleAt = at ?? Now;
        row.SetRate(MetricKind.DpsIn, dpsIn);
        row.SetRate(MetricKind.NeutIn, neutIn);
        for (var i = 0; i < 120; i++)
            row.RenderFrame();
    }

    // ---- What the window says ------------------------------------------------------------------------------------

    [Fact]
    public void Names_TheMemberTakingTheMostDamage_AndTheMostNeut()
    {
        var fleet = new FakeFleet();
        fleet.Rows.Add(Member("RaymondKrah", dpsIn: 90, neutIn: 60));
        fleet.Rows.Add(Member("Lionear", dpsIn: 640, neutIn: 8));
        fleet.Rows.Add(Member("Tarek", dpsIn: 120, neutIn: 12));

        var overlay = new FleetOverlayViewModel(fleet);
        overlay.Refresh(Now);

        Assert.Equal("Lionear", overlay.IncomingName);
        Assert.Equal("RaymondKrah", overlay.NeutedName);
        Assert.False(overlay.IsQuiet);
        // A name without its figure is half an answer; a figure without a name is none.
        Assert.Contains("dps", overlay.IncomingValue);
        Assert.Contains("GJ/s", overlay.NeutedValue);
    }

    [Fact]
    public void WithNothingHappening_ItIsQuiet_AndNamesNobody()
    {
        var fleet = new FakeFleet();
        fleet.Rows.Add(Member("RaymondKrah"));
        fleet.Rows.Add(Member("Lionear", dpsIn: 3, neutIn: 1));   // below both floors: a stray hit, not a decision

        var overlay = new FleetOverlayViewModel(fleet);
        overlay.Refresh(Now);

        Assert.True(overlay.IsQuiet);
        Assert.False(overlay.HasIncoming);
        Assert.False(overlay.HasNeuted);
        Assert.Equal("—", overlay.IncomingName);
        Assert.Equal("", overlay.IncomingValue);
    }

    [Fact]
    public void TheWithFcBadge_IsTheScreensOwn_NotACountOfItsOwn()
    {
        var fleet = new FakeFleet
        {
            // 5 of 8 known with the FC, 2 unknown — exactly what the fleet-metrics header would be showing.
            CommanderPresence = FleetCommanderPresence.From("Jita",
                FleetStandings.At("Jita", "Jita", "Jita", "Jita", "Jita", "Perimeter", "Perimeter", "Perimeter", null, null)),
        };

        var overlay = new FleetOverlayViewModel(fleet);
        overlay.Refresh(Now);

        Assert.Equal("◉ 5/8 WITH FC (2 unknown)", overlay.CommanderPresence.BadgeText);
        Assert.False(overlay.CommanderPresence.IsComplete);
    }

    // ---- Offline and unknown members: the same verdict, not a new one --------------------------------------------

    [Fact]
    public void AMemberWeKnowIsOffline_IsNeverNamed()
    {
        // ESI answers /location/ for a logged-off character, and nothing publishes a zero rate on their behalf — so
        // a pilot who logs off mid-fight keeps whatever they were taking. Without the offline verdict this window
        // would name them for the rest of the evening (the ET-71 lie, in a window whose only job is naming).
        var offline = Member("Tarek", dpsIn: 900, neutIn: 400);
        offline.IsLocalCharacter = true;
        offline.InEve = false;
        Assert.True(offline.IsOffline);

        var fleet = new FakeFleet();
        fleet.Rows.Add(offline);
        fleet.Rows.Add(Member("Lionear", dpsIn: 200, neutIn: 30));

        var overlay = new FleetOverlayViewModel(fleet);
        overlay.Refresh(Now);

        Assert.Equal("Lionear", overlay.IncomingName);
        Assert.Equal("Lionear", overlay.NeutedName);
    }

    [Fact]
    public void AFleetMateOnAnotherMachine_IsNotCalledOffline()
    {
        // We cannot see their EVE client, and that is not evidence their client is shut (ET-70's boundary). They are
        // named on their figures like anyone else.
        var remote = Member("Vex Ardent", dpsIn: 800);
        Assert.False(remote.IsLocalCharacter);
        Assert.False(remote.IsOffline);

        var fleet = new FakeFleet();
        fleet.Rows.Add(remote);
        fleet.Rows.Add(Member("Lionear", dpsIn: 200));

        var overlay = new FleetOverlayViewModel(fleet);
        overlay.Refresh(Now);

        Assert.Equal("Vex Ardent", overlay.IncomingName);
    }

    [Fact]
    public void AMemberWhoseSamplesStopped_IsDroppedRatherThanFrozenOnScreen()
    {
        var vanished = Member("Sil Orn", dpsIn: 900, lastSampleAt: Now - FleetOverlayViewModel.StaleAfter - TimeSpan.FromSeconds(1));
        var flying = Member("Lionear", dpsIn: 200);

        var fleet = new FakeFleet();
        fleet.Rows.Add(vanished);
        fleet.Rows.Add(flying);

        var overlay = new FleetOverlayViewModel(fleet);
        overlay.Refresh(Now);

        Assert.Equal("Lionear", overlay.IncomingName);
    }

    [Fact]
    public void AMemberWhoHasNeverPublished_IsNotNamed()
    {
        // An external pilot has no client of their own, so no sample of theirs is ever coming. Their row exists (the
        // roster pre-fill made it) and every figure on it is zero.
        var external = new DpsViewModel("External Pilot", isSelf: false);
        Assert.Null(external.LastSampleAt);

        var fleet = new FakeFleet();
        fleet.Rows.Add(external);

        var overlay = new FleetOverlayViewModel(fleet);
        overlay.Refresh(Now);

        Assert.True(overlay.IsQuiet);
    }

    // ---- Staying still: the rules that make it readable in flight -------------------------------------------------

    [Fact]
    public void TwoMembersTakingAlmostTheSame_DoNotTradePlacesEverySample()
    {
        var a = Member("Alpha", dpsIn: 400);
        var b = Member("Bravo", dpsIn: 390);
        var fleet = new FakeFleet();
        fleet.Rows.Add(a);
        fleet.Rows.Add(b);

        var overlay = new FleetOverlayViewModel(fleet);
        overlay.Refresh(Now);
        Assert.Equal("Alpha", overlay.IncomingName);

        // Bravo edges ahead and the two keep crossing, as two repped ships under the same guns do. The naive answer
        // would rename the row on every refresh; this one may not move at all.
        var names = new List<string>();
        for (var step = 1; step <= 24; step++)
        {
            var at = Now + TimeSpan.FromMilliseconds(250 * step);
            Settle(a, 400 + (step % 2 == 0 ? 15 : -15), 0, at);
            Settle(b, 400 + (step % 2 == 0 ? -15 : 15), 0, at);
            overlay.Refresh(at);
            names.Add(overlay.IncomingName);
        }

        Assert.Single(names.Distinct());
        Assert.Equal("Alpha", names[^1]);
    }

    [Fact]
    public void ARealOutlier_TakesTheRow_ButOnlyAfterHoldingIt()
    {
        var a = Member("Alpha", dpsIn: 400);
        var b = Member("Bravo", dpsIn: 100);
        var fleet = new FakeFleet();
        fleet.Rows.Add(a);
        fleet.Rows.Add(b);

        var overlay = new FleetOverlayViewModel(fleet);
        overlay.Refresh(Now);
        Assert.Equal("Alpha", overlay.IncomingName);

        // Bravo gets primaried: far ahead, and it lasts.
        Settle(b, 0, 0);
        Settle(b, 1400, 0);

        overlay.Refresh(Now + TimeSpan.FromMilliseconds(250));
        Assert.Equal("Alpha", overlay.IncomingName);      // clearly ahead, but not yet for long enough

        overlay.Refresh(Now + TimeSpan.FromSeconds(2));
        Assert.Equal("Bravo", overlay.IncomingName);      // held it, so the row is theirs
    }

    [Fact]
    public void ASingleVolleyOnSomeoneElse_DoesNotStealTheRow()
    {
        var a = Member("Alpha", dpsIn: 400);
        var b = Member("Bravo", dpsIn: 100);
        var fleet = new FakeFleet();
        fleet.Rows.Add(a);
        fleet.Rows.Add(b);

        var overlay = new FleetOverlayViewModel(fleet);
        overlay.Refresh(Now);

        // One spike well past Alpha, gone again before the dwell is up. The row must not move at ANY point — a name
        // that flashes onto the window and off it again is worse than one that never appeared.
        foreach (var (value, afterMs) in new[] { (1400L, 250), (1400L, 500), (60L, 750) })
        {
            Settle(b, value, 0);
            overlay.Refresh(Now + TimeSpan.FromMilliseconds(afterMs));
            Assert.Equal("Alpha", overlay.IncomingName);
        }
    }

    [Fact]
    public void WhenTheShootingStops_TheRowHoldsBriefly_ThenGoesQuiet()
    {
        var a = Member("Alpha", dpsIn: 400);
        var fleet = new FakeFleet();
        fleet.Rows.Add(a);

        var overlay = new FleetOverlayViewModel(fleet);
        overlay.Refresh(Now);
        Assert.False(overlay.IsQuiet);

        // Alpha keeps reporting — the guns simply stopped. A lull between volleys must not blank the window and
        // refill it a second later.
        Settle(a, 0, 0, Now + TimeSpan.FromSeconds(1));
        overlay.Refresh(Now + TimeSpan.FromSeconds(1));
        Assert.False(overlay.IsQuiet);
        Assert.Equal("Alpha", overlay.IncomingName);

        Settle(a, 0, 0, Now + TimeSpan.FromSeconds(4));
        overlay.Refresh(Now + TimeSpan.FromSeconds(4));
        Assert.True(overlay.IsQuiet);
    }

    [Fact]
    public void TheFirstPilotToTakeFire_IsNamedAtOnce()
    {
        // Steadiness protects a name that is already up. Before there is one, waiting would be waiting to report the
        // very thing this window exists to report.
        var a = Member("Alpha");
        var fleet = new FakeFleet();
        fleet.Rows.Add(a);

        var overlay = new FleetOverlayViewModel(fleet);
        overlay.Refresh(Now);
        Assert.True(overlay.IsQuiet);

        Settle(a, 500, 0);
        overlay.Refresh(Now + TimeSpan.FromMilliseconds(250));

        Assert.Equal("Alpha", overlay.IncomingName);
        Assert.False(overlay.IsQuiet);
    }

    [Fact]
    public void MembersOnAnIdenticalFigure_DoNotSwapWhenTheListReorders()
    {
        var fleet = new FakeFleet();
        var a = Member("Alpha", dpsIn: 300);
        var b = Member("Bravo", dpsIn: 300);
        fleet.Rows.Add(a);
        fleet.Rows.Add(b);

        var overlay = new FleetOverlayViewModel(fleet);
        overlay.Refresh(Now);
        var first = overlay.IncomingName;

        // A drag on the fleet-metrics screen reorders the very collection this reads.
        fleet.Rows.Reverse();
        overlay.Refresh(Now + TimeSpan.FromMilliseconds(250));

        Assert.Equal(first, overlay.IncomingName);
    }

    // ---- The neut direction: the reason MetricKind.NeutIn exists ---------------------------------------------------

    [Fact]
    public void TheNeutRow_NamesWhoIsBeingNeuted_NotWhoIsDoingTheNeuting()
    {
        // MetricKind.Neut combines both directions, by design, because it draws one cap-warfare line on a graph.
        // Reading "who is being neuted" off it names the fleet's own neut boat — the pilot who needs nothing.
        var neutBoat = Member("Bhaalgorn Pilot");
        neutBoat.SetRate(MetricKind.Neut, 900);          // applying a great deal, receiving none
        neutBoat.SetRate(MetricKind.NeutIn, 0);
        for (var i = 0; i < 120; i++) neutBoat.RenderFrame();

        var victim = Member("Logi Anchor");
        victim.SetRate(MetricKind.Neut, 120);            // the same event, seen from the receiving end
        victim.SetRate(MetricKind.NeutIn, 120);
        for (var i = 0; i < 120; i++) victim.RenderFrame();

        Assert.True(neutBoat.Neut > victim.Neut);        // the combined line would pick the wrong pilot…

        var fleet = new FakeFleet();
        fleet.Rows.Add(neutBoat);
        fleet.Rows.Add(victim);

        var overlay = new FleetOverlayViewModel(fleet);
        overlay.Refresh(Now);

        Assert.Equal("Logi Anchor", overlay.NeutedName); // …and the directional one picks the right one.
    }

    [Fact]
    public void NeutIn_RidesTheOneCombatShareToggle()
    {
        // It is the received half of Neut. A key of its own would default to shared and push exactly the fact a user
        // turned combat sharing off to stop.
        Assert.True(MetricShareSnapshot.IsCombat(MetricKind.NeutIn));
        Assert.Equal(MetricShareSnapshot.KeyFor(MetricKind.Neut), MetricShareSnapshot.KeyFor(MetricKind.NeutIn));
        Assert.Equal(MetricShareSnapshot.OverrideKeyFor(7, 42, MetricKind.Neut),
                     MetricShareSnapshot.OverrideKeyFor(7, 42, MetricKind.NeutIn));
    }

    // ---- Rendering: the half of this ticket tests cannot answer on their own ---------------------------------------

    [AvaloniaFact]
    public void InAFight_TheOverlayRenders_WithBothNamesOnScreen()
    {
        using var instance = TestClientInstance.Create();
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);
        // Anchor samples at opening time because the window refreshes against the wall clock.
        var now = DateTime.UtcNow;

        var fleet = new FakeFleet
        {
            CommanderPresence = FleetCommanderPresence.From("Jita", FleetStandings.At("Jita", "Jita", "Perimeter", null)),
        };
        fleet.Rows.Add(Member("RaymondKrah", dpsIn: 120, neutIn: 4, lastSampleAt: now));
        fleet.Rows.Add(Member("Lionear", dpsIn: 812, neutIn: 6, lastSampleAt: now));
        fleet.Rows.Add(Member("Tarek Vex", dpsIn: 60, neutIn: 74, lastSampleAt: now));

        var (window, overlay) = Open(fleet, now);

        Assert.False(overlay.IsQuiet);
        Assert.Equal("Lionear", Text(window, "IncomingName"));
        Assert.Equal("Tarek Vex", Text(window, "NeutedName"));
        Assert.Equal("◉ 2/3 WITH FC (1 unknown)", Text(window, "WithFcChip"));
        // The two names are the point of the window; a value beside each is what makes them actionable.
        Assert.EndsWith("dps", Text(window, "IncomingValue"));
        Assert.EndsWith("GJ/s", Text(window, "NeutedValue"));

        Capture(window, "fleet-overlay-fight");
        window.Close();
    }

    [AvaloniaFact]
    public void AtRest_TheOverlayRenders_Quiet()
    {
        using var instance = TestClientInstance.Create();
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);
        var now = DateTime.UtcNow;

        var fleet = new FakeFleet
        {
            CommanderPresence = FleetCommanderPresence.From("Jita", FleetStandings.At("Jita", "Jita", "Jita")),
        };
        fleet.Rows.Add(Member("RaymondKrah", lastSampleAt: now));
        fleet.Rows.Add(Member("Lionear", lastSampleAt: now));
        fleet.Rows.Add(Member("Tarek Vex", lastSampleAt: now));

        var (window, overlay) = Open(fleet, now);

        Assert.True(overlay.IsQuiet);
        Assert.Equal("—", Text(window, "IncomingName"));
        Assert.Equal("—", Text(window, "NeutedName"));
        Assert.Equal("", Text(window, "IncomingValue"));
        // Everyone is together, so the one thing that is worth saying at rest says it in green.
        Assert.Equal("◉ 3/3 WITH FC", Text(window, "WithFcChip"));
        Assert.True(overlay.CommanderPresence.IsComplete);

        Capture(window, "fleet-overlay-quiet");
        window.Close();
    }

    [AvaloniaFact]
    public void WithOfflineMembers_TheOverlayRenders_WithoutNamingThem()
    {
        using var instance = TestClientInstance.Create();
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);
        var now = DateTime.UtcNow;

        var offline = Member("Tarek Vex", dpsIn: 900, neutIn: 300, lastSampleAt: now);
        offline.IsLocalCharacter = true;
        offline.InEve = false;

        var fleet = new FakeFleet
        {
            CommanderPresence = FleetCommanderPresence.From("Jita", FleetStandings.At("Jita", "Jita", null, null, null)),
        };
        fleet.Rows.Add(Member("RaymondKrah", dpsIn: 210, neutIn: 22, lastSampleAt: now));
        fleet.Rows.Add(Member("Lionear", dpsIn: 90, lastSampleAt: now));
        fleet.Rows.Add(offline);

        var (window, overlay) = Open(fleet, now);

        Assert.Equal("RaymondKrah", Text(window, "IncomingName"));
        Assert.Equal("RaymondKrah", Text(window, "NeutedName"));
        Assert.Equal("◉ 2/2 WITH FC (3 unknown)", Text(window, "WithFcChip"));
        Assert.True(overlay.CommanderPresence.IsComplete);   // everyone we can see is together

        Capture(window, "fleet-overlay-offline");
        window.Close();
    }

    [AvaloniaFact]
    public void SmallEnoughToSitBesideTheGame_ItStillReads()
    {
        using var instance = TestClientInstance.Create();
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);
        var now = DateTime.UtcNow;

        var fleet = new FakeFleet { FleetName = "Sunday Roam — Amarr staging" };
        fleet.Rows.Add(Member("Lionear", dpsIn: 1240, neutIn: 12, lastSampleAt: now));
        fleet.Rows.Add(Member("Constantine Frostwalker", dpsIn: 300, neutIn: 96, lastSampleAt: now));

        var (window, overlay) = Open(fleet, now, width: 250, height: 140);   // the window's minimum

        Assert.Equal("Lionear", Text(window, "IncomingName"));
        Assert.Equal("Constantine Frostwalker", Text(window, "NeutedName"));
        Assert.False(overlay.IsQuiet);

        Capture(window, "fleet-overlay-small");
        window.Close();
    }

    // ---- The way in, and the way out, through the real fleet-metrics screen ----------------------------------------

    /// <summary>The two ways the fleet-metrics screen reaches a user. Docked is the default, and the one that bites:
    /// the module host lifts the content out of the window, so anything on the window is left behind (ET-30/ET-43).</summary>
    public enum Shell { OwnWindow, DockedTab }

    private sealed class FakeDisplay : IModuleHostDisplay
    {
        public bool IsFloating { get; set; }
        public ObservableCollection<HostTab> HostTabs { get; } = [];
        public HostTab? SelectedHostTab { get; set; }
    }

    [AvaloniaTheory]
    [InlineData(FleetMetricsLayout.List, Shell.OwnWindow)]
    [InlineData(FleetMetricsLayout.Grid, Shell.OwnWindow)]
    [InlineData(FleetMetricsLayout.Compact, Shell.OwnWindow)]
    [InlineData(FleetMetricsLayout.List, Shell.DockedTab)]
    [InlineData(FleetMetricsLayout.Grid, Shell.DockedTab)]
    [InlineData(FleetMetricsLayout.Compact, Shell.DockedTab)]
    public void TheFleetPopOutButton_SitsInTheHeader_InEveryDensityAndBothShells(FleetMetricsLayout layout, Shell shell)
    {
        using var instance = TestClientInstance.Create();
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);

        using var vm = new FleetMetricsViewModel(instance.Services, new FakeFleetClient(), Op);
        vm.SetLayoutCommand.Execute(layout);

        var window = new FleetMetricsWindow(vm) { Width = 900, Height = 620 };
        Window root = window;
        if (shell is Shell.DockedTab)
        {
            var display = new FakeDisplay { IsFloating = false };
            var host = new ModuleHostService();
            host.SetOwner(new Window());
            host.SetHost(display);
                host.Open(window, "FLEET METRICS", "fleet", "fleet-metrics");
            root = new Window { Width = 900, Height = 620, Content = Assert.Single(display.HostTabs).Content };
        }

        root.Show();
        Dispatcher.UIThread.RunJobs();

        // In the header beside the WITH FC badge, not in a member row: this is about the fleet, so it has to be
        // reachable whichever density the member list below is in — the same reason the badge lives there (ET-30).
        var button = root.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PopOutFleetButton");
        Assert.True(button.IsVisible);
        Assert.NotNull(button.Command);

        if (layout is FleetMetricsLayout.List)
            Capture(root, $"fleet-metrics-header-{shell}".ToLowerInvariant());

        root.Close();
    }

    [AvaloniaFact]
    public void ThePopOutCommand_HandsTheScreensOwnViewModelToTheOverlay()
    {
        var dialogs = new RecordingDialogService();
        using var instance = TestClientInstance.Create(services => services.AddSingleton<IDialogService>(dialogs));

        using var vm = new FleetMetricsViewModel(instance.Services, new FakeFleetClient(), Op);
        vm.PopOutFleetCommand.Execute(null);

        var overlay = Assert.Single(dialogs.ShownFleetOverlays);
        Assert.Equal(Op.Id, overlay.FleetId);
        Assert.Equal(Op.Name, overlay.FleetName);   // which fleet, when more than one is open
    }

    [AvaloniaFact]
    public void ClosingTheMetricsScreen_TakesItsOverlayWithIt()
    {
        // The overlay's figures come from this view-model's rows. Left open when the screen goes it would sit on top
        // of the game showing the last frame before the screen closed — stale, and convincingly so (the ET-52 rule).
        var dialogs = new RecordingDialogService();
        using var instance = TestClientInstance.Create(services => services.AddSingleton<IDialogService>(dialogs));

        var vm = new FleetMetricsViewModel(instance.Services, new FakeFleetClient(), Op);
        vm.PopOutFleetCommand.Execute(null);
        vm.Dispose();

        Assert.Contains(Op.Id, dialogs.ClosedFleetOverlays);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private static (FleetOverlayWindow Window, FleetOverlayViewModel Overlay) Open(
        FakeFleet fleet, DateTime now, int width = 340, int height = 164)
    {
        var overlay = new FleetOverlayViewModel(fleet);
        overlay.Refresh(now);

        var window = new FleetOverlayWindow(overlay) { Width = width, Height = height };
        window.Show();
        for (var i = 0; i < 10; i++)
            Dispatcher.UIThread.RunJobs();

        return (window, overlay);
    }

    private static string Text(Window window, string name) =>
        window.GetVisualDescendants()
            .OfType<Control>()
            .Where(c => c.Name == name)
            .Select(c => c switch
            {
                TextBlock text => text.Text ?? "",
                Border { Child: TextBlock inner } => inner.Text ?? "",
                _ => "",
            })
            .First();

    // Saved so the result can actually be looked at rather than only asserted on — the lesson this project has
    // learned nine times over. Kept, because an overlay is a thing whose whole purpose is how fast it reads.
    private static void Capture(Window window, string name) => OverlayShots.Capture(window, name);
}
