using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Platform;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-70 on the two screens an FC actually reads. ET-71 taught this client to stop claiming a location for one of
/// <b>our own</b> parked pilots; everybody else's still looked present whatever they were doing, because we cannot
/// see another machine's EVE client. Now their own client says so on the stream it already publishes — and when that
/// client is gone altogether, the silence says it instead.
/// </summary>
public class FleetPresenceScreenTests
{
    private const int Commander = 90250177;   // one of ours, flying
    private const int Mate = 90250181;        // a fleet mate on another machine
    private const int Quiet = 90250182;       // a fleet mate who shares nothing at all
    private const long FleetId = 220;

    private static readonly Dictionary<int, string> Names = new()
    {
        [Commander] = "RaymondKrah",
        [Mate] = "Tarek",
        [Quiet] = "Vex Ardent",
    };

    private static readonly FleetInfo Op = new(FleetId, "Op", null, FleetVisibility.Public, FleetState.Active, 1,
        null, null, DateTimeOffset.UnixEpoch, FleetActivation.Active);

    // ---- fleet metrics ------------------------------------------------------------------------------------------

    /// <summary>
    /// Scenario one, the one that was simply never transmitted: a fleet mate's EVE Together is running and their game
    /// is closed. Their own client knows, so it says so — and the row, the location and the badge all move off that
    /// one verdict, exactly as they do for our own pilots.
    /// </summary>
    [AvaloniaFact]
    public async Task AMateWhoseGameIsClosed_ReadsOffline_AndLeavesTheRatio()
    {
        using var harness = await StartAsync();
        await harness.SayLocationAsync(Commander, "Jita");
        await harness.SayLocationAsync(Mate, "Jita");
        await harness.SayPresenceAsync(Commander, PresenceState.InGame);

        var mate = harness.Row(Mate);
        Assert.Equal("Jita", mate.KnownLocation);   // before their client says anything, nothing is claimed

        await harness.SayPresenceAsync(Mate, PresenceState.NotInGame);

        Assert.Equal(FleetMemberPresenceState.Offline, mate.Presence);
        Assert.True(mate.IsOffline);
        Assert.Equal("Jita", mate.Location);        // the raw report is untouched…
        Assert.Null(mate.KnownLocation);            // …and nothing may act on it
        Assert.Equal("offline", mate.LocationDisplay);
        Assert.False(mate.IsWithCommander);

        var presence = await harness.WaitForPresenceAsync(p => p.Offline == 1);
        Assert.Equal(1, presence.Offline);
        Assert.Equal(1, presence.Known);            // the commander alone
        Assert.Equal("◉ 1/1 WITH FC (1 offline, 1 unknown)", presence.BadgeText);
    }

    /// <summary>
    /// Scenario two, the one no message can report. The mate's client is gone; nothing announces that, so the screen
    /// reads it off its own clock. Driven through <see cref="FleetMetricsViewModel.RefreshPresence"/> with a time the
    /// test owns rather than by waiting ninety seconds.
    /// </summary>
    [AvaloniaFact]
    public async Task AMateWhoseClientDisappears_ReadsOffline_OnceTheSilenceIsLongEnough()
    {
        using var harness = await StartAsync();
        await harness.SayLocationAsync(Commander, "Jita");   // there is no honest ratio without the FC's own system
        await harness.SayLocationAsync(Mate, "Jita");
        await harness.SayPresenceAsync(Mate, PresenceState.InGame);

        var mate = harness.Row(Mate);
        var heardAt = mate.LastSampleAt ?? throw new InvalidOperationException("no sample recorded");
        Assert.Equal(FleetMemberPresenceState.Online, mate.Presence);

        // A hiccup, well inside the window: they do not blink out of the fleet.
        harness.Vm.RefreshPresence(heardAt + FleetMemberPresence.SilentAfter);
        Assert.False(mate.IsOffline);
        Assert.Equal("Jita", mate.KnownLocation);

        // Past it, and they are gone.
        harness.Vm.RefreshPresence(heardAt + FleetMemberPresence.SilentAfter + TimeSpan.FromSeconds(1));
        Assert.True(mate.IsOffline);
        Assert.Equal("offline", mate.LocationDisplay);
        Assert.Equal(1, harness.Vm.CommanderPresence.Offline);
    }

    /// <summary>
    /// The pilot we have never heard a word from stays <b>unknown</b> through all of it. They may have every metric
    /// switched off, and calling that "gone" would be an accusation drawn from nothing. They are counted apart from
    /// the ratio, as before, and never as offline.
    /// </summary>
    [AvaloniaFact]
    public async Task ThePilotWhoSharesNothing_StaysUnknown_HoweverLongTheScreenIsOpen()
    {
        using var harness = await StartAsync();
        await harness.SayLocationAsync(Commander, "Jita");

        var quiet = harness.Row(Quiet);
        Assert.Null(quiet.LastSampleAt);

        harness.Vm.RefreshPresence(DateTimeOffset.UtcNow + TimeSpan.FromHours(4));

        Assert.Equal(FleetMemberPresenceState.Unknown, quiet.Presence);
        Assert.False(quiet.IsOffline);
        Assert.Equal(0, harness.Vm.CommanderPresence.Offline);
        Assert.Contains("unknown", harness.Vm.CommanderPresence.BadgeText);
    }

    /// <summary>
    /// Coming back is immediate. A returning sample ends the silence on the spot rather than at the next sweep: a
    /// pilot whose figures are already moving must not still be labelled gone.
    /// </summary>
    [AvaloniaFact]
    public async Task AReturningSample_ClearsTheOfflineReading_WithoutWaitingForTheSweep()
    {
        using var harness = await StartAsync();
        await harness.SayPresenceAsync(Mate, PresenceState.InGame);

        var mate = harness.Row(Mate);
        harness.Vm.RefreshPresence((mate.LastSampleAt ?? DateTimeOffset.UtcNow) + TimeSpan.FromHours(1));
        Assert.True(mate.IsOffline);

        await harness.SayLocationAsync(Mate, "Amarr");

        Assert.False(mate.IsOffline);
        Assert.Equal("Amarr", mate.KnownLocation);
    }

    /// <summary>
    /// A screen that has only just opened has heard nobody, so silence tells it nothing — which is exactly when the
    /// FC opens it. The server's own record of when each member last published comes down with the roster read and
    /// closes that gap: a pilot who left an hour ago reads offline immediately, with no live sample at all.
    /// </summary>
    [AvaloniaFact]
    public async Task AMemberWhoLeftBeforeTheScreenOpened_ReadsOffline_FromTheServersRecord()
    {
        using var harness = await StartAsync(seenLongAgo: Mate);

        var mate = harness.Row(Mate);
        Assert.NotNull(mate.ServerLastSeenAt);

        harness.Vm.RefreshPresence(DateTimeOffset.UtcNow);

        Assert.True(mate.IsOffline);
        // …while the pilot the server has never heard from either is still only unknown.
        Assert.False(harness.Row(Quiet).IsOffline);
        Assert.Equal(FleetMemberPresenceState.Unknown, harness.Row(Quiet).Presence);
    }

    /// <summary>
    /// Rendered, in all three densities and both shells — a verdict behind a screen that does not show it is this
    /// project's most repeated failure. The offline mate reads "offline" in the dim colour where their system was.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(FleetMetricsLayout.List, false)]
    [InlineData(FleetMetricsLayout.List, true)]
    [InlineData(FleetMetricsLayout.Grid, false)]
    [InlineData(FleetMetricsLayout.Grid, true)]
    [InlineData(FleetMetricsLayout.Compact, false)]
    [InlineData(FleetMetricsLayout.Compact, true)]
    public async Task TheOfflineMate_RendersAsOffline_InEveryLayoutAndShell(FleetMetricsLayout layout, bool docked)
    {
        using var harness = await StartAsync();
        await harness.SayLocationAsync(Commander, "Jita");
        await harness.SayLocationAsync(Mate, "Jita");
        await harness.SayPresenceAsync(Mate, PresenceState.NotInGame);

        harness.Vm.SetLayoutCommand.Execute(layout);
        Control root = harness.Show(docked);

        var blocks = root.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Classes.Contains("location") && t.IsVisible)
            .ToList();

        // Both readouts are on screen — "none of them says Jita twice" over an empty set would pass for the wrong
        // reason, which is the trap this screen's tests have fallen into before.
        Assert.Equal(2, blocks.Count);
        Assert.Contains(blocks, b => b.Text == "◉ Jita");
        var offline = Assert.Single(blocks, b => b.Text == "◉ offline");
        Assert.Contains("offline", offline.Classes);
        Assert.Equal(DimColour(), Assert.IsAssignableFrom<ISolidColorBrush>(offline.Foreground).Color);
    }

    /// <summary>
    /// Dock → float. The migration hands the content back to the module's own window, and the wiring on this screen
    /// family keeps not surviving that round trip (ET-46, ET-48, ET-49). The verdict has to still be on screen after
    /// it, and — because this one moves with a clock rather than with a sample — still be following its sweep.
    /// </summary>
    [AvaloniaFact]
    public async Task TheOfflineMark_SurvivesTheDockToFloatMigration_AndKeepsMoving()
    {
        using var harness = await StartAsync();
        await harness.SayLocationAsync(Commander, "Jita");
        await harness.SayLocationAsync(Mate, "Jita");
        await harness.SayPresenceAsync(Mate, PresenceState.InGame);

        var display = new FakeDisplay { IsFloating = false };
        var host = new ModuleHostService();
        host.SetOwner(new Window());
        host.SetHost(display);
        var window = new FleetMetricsWindow(harness.Vm) { Width = 900, Height = 620 };
        host.Open(window, "FLEET METRICS", "fleet", $"fleet-metrics:{harness.Vm.FleetId}");

        display.IsFloating = true;
        host.SwitchMode();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.DoesNotContain("◉ offline", Readouts());

        // …and the pilot goes quiet AFTER the migration, so this proves the sweep still reaches the re-parented rows
        // rather than that a mark drawn before the move happened to be carried across.
        harness.Vm.RefreshPresence((harness.Row(Mate).LastSampleAt ?? DateTimeOffset.UtcNow) + TimeSpan.FromHours(1));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.Contains("◉ offline", Readouts());
        Assert.Contains("◉ Jita", Readouts());   // the FC is untouched by it

        List<string?> Readouts() => window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Classes.Contains("location") && t.IsVisible)
            .Select(t => t.Text)
            .ToList();
    }

    /// <summary>The DPS pop-out is its own window and inherits nothing from the screen that opened it.</summary>
    [AvaloniaFact]
    public async Task ThePopOut_ReadsTheMateAsOfflineToo()
    {
        using var harness = await StartAsync();
        await harness.SayLocationAsync(Mate, "Jita");
        await harness.SayPresenceAsync(Mate, PresenceState.NotInGame);

        var overlay = new DpsOverlayWindow(harness.Row(Mate)) { Width = 320, Height = 200 };
        overlay.Show();
        Dispatcher.UIThread.RunJobs();
        overlay.UpdateLayout();

        var block = overlay.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("location"));
        Assert.Equal("◉ offline", block.Text);
        Assert.Contains("offline", block.Classes);
    }

    /// <summary>The shared pilot menu names the state in words, and stops calling an absent pilot's blank location a
    /// privacy choice they never made.</summary>
    [AvaloniaFact]
    public async Task ThePilotMenu_NamesThePresence_AndStopsCallingItAChoice()
    {
        using var harness = await StartAsync();
        await harness.SayLocationAsync(Mate, "Jita");
        await harness.SayPresenceAsync(Mate, PresenceState.NotInGame);

        var mate = harness.Row(Mate);
        harness.Vm.RefreshMemberMenu(mate);
        var lines = mate.MemberMenu.Select(i => i.Label).ToList();

        Assert.Contains("Offline — not in game", lines);
        Assert.Contains("No location — offline", lines);
        Assert.DoesNotContain("Not sharing location", lines);

        harness.Vm.RefreshMemberMenu(harness.Row(Quiet));
        Assert.Contains(
            "Presence unknown — this pilot's client has never reported",
            harness.Row(Quiet).MemberMenu.Select(i => i.Label));
    }

    // ---- the roster ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The roster shows no figures and hears no metrics of its own — but "who is even here" is a roster question, so
    /// it reads the presence half of the very same stream. Both surfaces of the window carry the mark: a placed
    /// member has a node in the structure tree AND a second one behind their row in the left list, and a sweep that
    /// reached only one of them would leave the other telling a different story.
    /// </summary>
    [AvaloniaFact]
    public async Task TheRoster_MarksTheOfflineMember_InTheTreeAndTheMemberList()
    {
        using var harness = await StartAsync();
        using var roster = new FleetRosterViewModel(harness.Services, harness.Fleets, Op, isOwner: true, Commander);
        for (var i = 0; i < 200 && roster.Entries.Count < 3; i++)
            await Task.Delay(20);
        Assert.Equal(3, roster.Entries.Count);

        await harness.SayPresenceAsync(Mate, PresenceState.NotInGame, waitForRow: false);
        roster.RefreshPresence(DateTimeOffset.UtcNow);

        var mate = roster.Entries.Single(e => e.Member?.CharacterId == Mate);
        Assert.True(mate.Node?.IsOffline);
        Assert.Contains("Offline — not in game", mate.MemberMenu.Select(i => i.Label));

        // The pilot who has never said anything is not marked — unknown is not offline, here either.
        Assert.False(roster.Entries.Single(e => e.Member?.CharacterId == Quiet).Node?.IsOffline);

        var window = new FleetRosterWindow(roster) { Width = 900, Height = 640 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        OverlayShots.Capture(window, "et70-fleet-roster");

        // One mark for the tree leaf and one for the left-list row — the same pilot, both halves of the window.
        var marks = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsVisible && t.Text == "◉ offline")
            .ToList();
        Assert.Equal(2, marks.Count);
        Assert.All(marks, m => Assert.Contains("offline", m.Classes));
        Assert.All(marks, m => Assert.Equal(DimColour(), Assert.IsAssignableFrom<ISolidColorBrush>(m.Foreground).Color));
    }

    /// <summary>And the roster reads the silence too, off the server's record, for a member who was gone before it
    /// opened.</summary>
    [AvaloniaFact]
    public async Task TheRoster_MarksAMemberWhoLeftBeforeItOpened()
    {
        using var harness = await StartAsync(seenLongAgo: Mate);
        using var roster = new FleetRosterViewModel(harness.Services, harness.Fleets, Op, isOwner: true, Commander);
        for (var i = 0; i < 200 && roster.Entries.Count < 3; i++)
            await Task.Delay(20);

        roster.RefreshPresence(DateTimeOffset.UtcNow);

        Assert.True(roster.Entries.Single(e => e.Member?.CharacterId == Mate).Node?.IsOffline);
        Assert.False(roster.Entries.Single(e => e.Member?.CharacterId == Quiet).Node?.IsOffline);
    }

    /// <summary>
    /// The operator's scene, rendered so it can be looked at rather than only asserted on: an FC who is flying, a
    /// mate whose game is closed, a mate whose client is gone, and a pilot who has never shared a thing. Frames land
    /// wherever <c>EVEUTILS_SHOT_DIR</c> points; without it they go to the temp directory and this is an ordinary
    /// test. The assertion is that the four states are on screen at once and read differently.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(FleetMetricsLayout.List)]
    [InlineData(FleetMetricsLayout.Compact)]
    public async Task TheWholeScene_Renders_WithEachStateTellingItselfApart(FleetMetricsLayout layout)
    {
        using var harness = await StartAsync();
        await harness.SayLocationAsync(Commander, "Jita");
        await harness.SayPresenceAsync(Commander, PresenceState.InGame);
        await harness.SayLocationAsync(Mate, "Jita");
        await harness.SayPresenceAsync(Mate, PresenceState.NotInGame);   // game closed, client still reporting
        await harness.SayLocationAsync(Quiet, "Perimeter");
        await harness.SayPresenceAsync(Quiet, PresenceState.InGame);

        // …and now the fourth pilot's client goes away entirely. Nothing announces it; the clock reads it.
        harness.Vm.RefreshPresence((harness.Row(Quiet).LastSampleAt ?? DateTimeOffset.UtcNow) + TimeSpan.FromHours(1));

        harness.Vm.SetLayoutCommand.Execute(layout);
        var window = new FleetMetricsWindow(harness.Vm) { Width = 900, Height = 620 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        OverlayShots.Capture(window, $"et70-fleet-metrics-{layout}".ToLowerInvariant());

        var readouts = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Classes.Contains("location") && t.IsVisible)
            .Select(t => t.Text)
            .ToList();

        // The FC is where he is; both departed pilots read offline; nobody's last known system is passed off as
        // current. Perimeter is the one that would betray a stale readout — that pilot is gone.
        Assert.Equal(3, readouts.Count);
        Assert.Contains("◉ Jita", readouts);
        Assert.Equal(2, readouts.Count(t => t == "◉ offline"));
        Assert.DoesNotContain("◉ Perimeter", readouts);
        Assert.Equal("◉ 1/1 WITH FC (2 offline)", harness.Vm.CommanderPresence.BadgeText);
    }

    // ---- harness ------------------------------------------------------------------------------------------------

    private static Color DimColour() =>
        Assert.IsAssignableFrom<ISolidColorBrush>(
            Avalonia.Application.Current?.FindResource("TextDimBrush")
            ?? throw new InvalidOperationException("no TextDimBrush")).Color;

    private static async Task<Harness> StartAsync(int? seenLongAgo = null)
    {
        var probe = new StubProbe();
        var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IEveClientProbe>(probe);
            services.AddSingleton<IExternalCharacterLookup>(new FakeExternalLookup
            {
                [Commander] = Names[Commander],
                [Mate] = Names[Mate],
                [Quiet] = Names[Quiet],
            });
        });

        // Only the commander is ours; the other two are on other machines, where this client can see nothing.
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        await registry.AddOrUpdateAsync(new Character(Names[Commander], Commander));

        // A member the server last heard from long ago — the state a screen cannot work out for itself on opening.
        DateTimeOffset? longAgo = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
        var fleets = new FakeFleetClient
        {
            Members =
            [
                new FleetMemberInfo(1, Commander, -1, -1, FleetRole.FleetCommander, false),
                new FleetMemberInfo(2, Mate, 1, 1, FleetRole.SquadMember, false,
                    LastSeenAt: seenLongAgo == Mate ? longAgo : null),
                new FleetMemberInfo(3, Quiet, 1, 1, FleetRole.SquadMember, false),
            ],
            // A real structure, so the roster's members are PLACED: an unplaced member has no tree node, and a test
            // over a structure-less fleet would silently only ever check the left list.
            Wings = [new FleetWingInfo(1, FleetId, "Wing 1")],
            Squads = [new FleetSquadInfo(1, 1, "Squad 1")],
        };

        probe.Evidence = new EveClientEvidence(
            new HashSet<string>([Names[Commander]], StringComparer.OrdinalIgnoreCase), new HashSet<int>());
        instance.Services.GetRequiredService<EveClientPresenceService>().PollOnce();
        await instance.Services.GetRequiredService<LocalCharacterPresence>().ReloadAsync();
        Dispatcher.UIThread.RunJobs();

        var harness = new Harness(instance, fleets);
        await harness.OpenAsync();
        return harness;
    }

    private sealed class Harness(TestClientInstance instance, FakeFleetClient fleets) : IDisposable
    {
        public IServiceProvider Services => instance.Services;
        public FakeFleetClient Fleets => fleets;
        public FleetMetricsViewModel Vm { get; private set; } = null!;

        public async Task OpenAsync()
        {
            Vm = new FleetMetricsViewModel(instance.Services, fleets, Op);
            for (var i = 0; i < 200 && Vm.Members.Count < fleets.Members.Count; i++)
                await Task.Delay(20);
            Assert.Equal(fleets.Members.Count, Vm.Members.Count);

            for (var i = 0; i < 200 && Vm.Members.Any(m => m.Character.StartsWith("Char ", StringComparison.Ordinal)); i++)
                await Task.Delay(20);
        }

        public Task SayLocationAsync(int characterId, string system) =>
            PublishAsync(new MetricSample(characterId, FleetId, MetricKind.Location, 0, 0, system),
                () => Row(characterId).Location == system);

        public Task SayPresenceAsync(int characterId, PresenceState state, bool waitForRow = true) =>
            PublishAsync(new MetricSample(characterId, FleetId, MetricKind.Presence, (double)state, 0),
                waitForRow ? () => Row(characterId).ReportedPresence == state : () => true);

        private async Task PublishAsync(MetricSample sample, Func<bool> settled)
        {
            await instance.Services.GetRequiredService<IEventBus>().PublishAsync(new FleetMetricEvent(sample));
            for (var i = 0; i < 200 && !settled(); i++)
                await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }

        public DpsViewModel Row(int characterId) =>
            Vm.Members.Single(m => string.Equals(m.Character, Names[characterId], StringComparison.OrdinalIgnoreCase));

        public async Task<FleetCommanderPresence> WaitForPresenceAsync(Func<FleetCommanderPresence, bool> ready)
        {
            for (var i = 0; i < 200 && !ready(Vm.CommanderPresence); i++)
                await Task.Delay(20);
            return Vm.CommanderPresence;
        }

        public Control Show(bool docked)
        {
            Window root = new FleetMetricsWindow(Vm) { Width = 900, Height = 620 };

            if (docked)
            {
                var display = new FakeDisplay { IsFloating = false };
                var host = new ModuleHostService();
                host.SetOwner(new Window());
                host.SetHost(display);
                host.Open(root, "FLEET METRICS", "fleet");
                root = new Window { Width = 900, Height = 620, Content = Assert.Single(display.HostTabs).Content };
            }

            root.Show();
            Dispatcher.UIThread.RunJobs();
            root.UpdateLayout();
            return root;
        }

        public void Dispose()
        {
            Vm?.Dispose();
            instance.Dispose();
        }
    }

    private sealed class FakeDisplay : IModuleHostDisplay
    {
        public bool IsFloating { get; set; }
        public ObservableCollection<HostTab> HostTabs { get; } = new();
        public HostTab? SelectedHostTab { get; set; }
    }

    private sealed class StubProbe : IEveClientProbe
    {
        public EveClientEvidence Evidence { get; set; } = EveClientEvidence.Empty;

        public EveClientEvidence Probe() => Evidence;
        public int RunningClientCount() => Evidence.CharacterNames.Count;
        public bool Activate(string characterName) => false;
    }
}
