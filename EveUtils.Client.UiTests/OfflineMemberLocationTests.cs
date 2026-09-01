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
using EveUtils.Client.Gamelog;
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
/// ET-71. ESI answers <c>/location/</c> for a logged-out character with the system they logged off in, and on
/// screen that reads exactly like a current position — the operator opened his fleet and saw everyone standing in
/// Amarr while three of his five accounts were closed.
///
/// So a pilot of ours who is not in game shows no system. The same verdict takes them out of the WITH FC
/// denominator (the decision made on ET-63) and keeps them from colouring green, because three places working out
/// "offline" separately is how they drift apart.
/// </summary>
public class OfflineMemberLocationTests
{
    private const int Commander = 90250177;   // one of ours
    private const int Alt = 90250178;         // one of ours too — the parked account
    private const int Stranger = 90250179;    // a fleet mate on another machine
    private const long FleetId = 100;

    private static readonly FleetInfo Op = new(FleetId, "Op", null, FleetVisibility.Public, FleetState.Active, 1,
        null, null, DateTimeOffset.UnixEpoch, FleetActivation.Active);

    /// <summary>
    /// The operator's picture: the FC is flying, an alt of his is parked, and a fleet mate is on another machine.
    /// Only the parked alt loses its system — his own, because we can see his client; not the fleet mate's, whose
    /// client we cannot see and about whom nothing may be inferred (that stays ET-70).
    /// </summary>
    [AvaloniaFact]
    public async Task AnOfflineCharacterOfOurs_ShowsNoSystem_AndAFleetMateIsUntouched()
    {
        using var harness = await StartAsync(inGame: [Commander]);
        await harness.SayAsync(Commander, "Jita");
        await harness.SayAsync(Alt, "Amarr");
        await harness.SayAsync(Stranger, "Amarr");

        var commander = harness.Row(Commander);
        var alt = harness.Row(Alt);
        var stranger = harness.Row(Stranger);

        // Ours and flying: unchanged.
        Assert.False(commander.IsOffline);
        Assert.Equal("Jita", commander.KnownLocation);
        Assert.Equal("Jita", commander.LocationDisplay);

        // Ours and parked: ESI's logoff spot is still on the row's raw Location, and is shown to nobody.
        Assert.True(alt.IsOffline);
        Assert.Equal("Amarr", alt.Location);
        Assert.Null(alt.KnownLocation);
        Assert.Equal("offline", alt.LocationDisplay);

        // Someone else's pilot: we cannot see their client, so we claim nothing.
        Assert.False(stranger.IsOffline);
        Assert.Equal("Amarr", stranger.KnownLocation);
        Assert.Equal("Amarr", stranger.LocationDisplay);
    }

    /// <summary>
    /// One verdict, not three. The member who shows no system is exactly the member the badge leaves out of its
    /// denominator and refuses to colour green — asserted together, because the failure this guards against is
    /// them disagreeing rather than any one of them being wrong.
    /// </summary>
    [AvaloniaFact]
    public async Task TheOfflineMember_DropsOutOfTheDenominator_AndIsNeverGreen()
    {
        using var harness = await StartAsync(inGame: [Commander]);
        await harness.SayAsync(Commander, "Jita");
        await harness.SayAsync(Alt, "Jita");        // parked in the FC's own system, and still not counted
        await harness.SayAsync(Stranger, "Jita");

        var presence = await harness.WaitForPresenceAsync(p => p.Known == 2);

        Assert.Equal(2, presence.InSystem);
        Assert.Equal(2, presence.Known);
        // Offline is its own bucket since ET-70, and not folded in with the pilots who share no position: "that one
        // is gone" and "we have no fix on that one" are different things to tell an FC.
        Assert.Equal(1, presence.Offline);
        Assert.Equal(0, presence.UnknownLocations);
        Assert.Equal(3, presence.Total);
        Assert.Equal("◉ 2/2 WITH FC (1 offline)", presence.BadgeText);

        var alt = harness.Row(Alt);
        Assert.Null(alt.KnownLocation);
        Assert.False(presence.IsWith(alt.KnownLocation));
        Assert.False(alt.IsWithCommander);

        // Counted and coloured are the same members, off the same verdict.
        Assert.Equal(presence.InSystem, harness.Vm.Members.Count(m => m.IsWithCommander));
        Assert.Equal(presence.Known, harness.Vm.Members.Count(m => m.KnownLocation is not null));
    }

    /// <summary>
    /// The fifth time in this project that a screen had to be made to move with what changed under it (ET-46,
    /// ET-49, ET-52, ET-68). A pilot logging in is announced once, and the rows and the badge both follow it —
    /// with no re-open, no timer and no second refresh path.
    /// </summary>
    [AvaloniaFact]
    public async Task LoggingInBringsTheLocationBack_AndLoggingOutTakesItAway_WhileTheScreenStaysOpen()
    {
        using var harness = await StartAsync(inGame: [Commander]);
        await harness.SayAsync(Commander, "Jita");
        await harness.SayAsync(Alt, "Jita");
        await harness.SayAsync(Stranger, "Jita");   // everybody is in fact in the same system

        var alt = harness.Row(Alt);
        Assert.Equal("offline", alt.LocationDisplay);
        Assert.Equal("◉ 2/2 WITH FC (1 offline)", (await harness.WaitForPresenceAsync(p => p.Known == 2)).BadgeText);

        // The pilot logs in. One announcement, and the row has its system back, is counted with the FC, and the
        // badge's note goes away because there is no longer anybody it does not know about.
        await harness.SetInGameAsync(Commander, Alt);
        Assert.False(alt.IsOffline);
        Assert.Equal("Jita", alt.LocationDisplay);
        Assert.True(alt.IsWithCommander);

        var presence = await harness.WaitForPresenceAsync(p => p.Known == 3);
        Assert.Equal("◉ 3/3 WITH FC", presence.BadgeText);
        Assert.True(presence.IsComplete);

        // …and back out again when they close the client.
        await harness.SetInGameAsync(Commander);
        Assert.True(alt.IsOffline);
        Assert.Equal("offline", alt.LocationDisplay);
        Assert.False(alt.IsWithCommander);
        Assert.Equal("◉ 2/2 WITH FC (1 offline)", (await harness.WaitForPresenceAsync(p => p.Known == 2)).BadgeText);
    }

    /// <summary>
    /// Rendered, in every layout and both shells — this is the screen the operator was actually looking at. The
    /// offline row reads "offline" in the dim colour rather than a system, and the one that is flying still reads
    /// its system in the ordinary accent.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(FleetMetricsLayout.List, false)]
    [InlineData(FleetMetricsLayout.List, true)]
    [InlineData(FleetMetricsLayout.Grid, false)]
    [InlineData(FleetMetricsLayout.Grid, true)]
    [InlineData(FleetMetricsLayout.Compact, false)]
    [InlineData(FleetMetricsLayout.Compact, true)]
    public async Task TheOfflineRow_RendersAsOffline_InEveryLayoutAndShell(FleetMetricsLayout layout, bool docked)
    {
        using var harness = await StartAsync(inGame: [Commander]);
        await harness.SayAsync(Commander, "Jita");
        await harness.SayAsync(Alt, "Amarr");
        await harness.WaitForPresenceAsync(p => p.Known == 1);

        harness.Vm.SetLayoutCommand.Execute(layout);
        Control root = harness.Show(docked);

        var blocks = root.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Classes.Contains("location") && t.IsVisible)
            .ToList();

        // Both rows still render a readout — asserting "none say Amarr" over an empty set would pass for the
        // wrong reason, which is the trap this screen's tests have fallen into before.
        Assert.Equal(2, blocks.Count);
        Assert.Contains(blocks, b => b.Text == "◉ Jita");
        Assert.Contains(blocks, b => b.Text == "◉ offline");
        Assert.DoesNotContain(blocks, b => b.Text == "◉ Amarr");

        var offline = blocks.Single(b => b.Text == "◉ offline");
        Assert.Contains("offline", offline.Classes);
        Assert.DoesNotContain("withfc", offline.Classes);
        Assert.Equal(DimColour(), Assert.IsAssignableFrom<ISolidColorBrush>(offline.Foreground).Color);

        var flying = blocks.Single(b => b.Text == "◉ Jita");
        Assert.NotEqual(DimColour(), Assert.IsAssignableFrom<ISolidColorBrush>(flying.Foreground).Color);
    }

    /// <summary>The pop-out is its own window and inherits nothing from the screen that opened it, so it gets the
    /// same check — the third time that gap has bitten this readout.</summary>
    [AvaloniaFact]
    public async Task ThePopOut_ReadsOfflineToo()
    {
        using var harness = await StartAsync(inGame: [Commander]);
        await harness.SayAsync(Commander, "Jita");
        await harness.SayAsync(Alt, "Amarr");
        await harness.WaitForPresenceAsync(p => p.Known == 1);

        var overlay = new DpsOverlayWindow(harness.Row(Alt)) { Width = 320, Height = 200 };
        overlay.Show();
        Dispatcher.UIThread.RunJobs();
        overlay.UpdateLayout();

        var block = overlay.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("location"));
        Assert.Equal("◉ offline", block.Text);
        Assert.Contains("offline", block.Classes);
        Assert.Equal(DimColour(), Assert.IsAssignableFrom<ISolidColorBrush>(block.Foreground).Color);
    }

    /// <summary>
    /// The home card names the online state itself, right beside the readout, so it takes the system alone —
    /// otherwise an offline character would read "Offline · offline".
    /// </summary>
    [Fact]
    public void TheHomeCard_DropsTheSystemEntirely_RatherThanRepeatingItself()
    {
        var row = new DpsViewModel("Parked", isSelf: true) { Location = "Amarr", IsLocalCharacter = true, InEve = false };

        Assert.True(row.IsOffline);
        Assert.Equal("offline", row.LocationDisplay);   // the fleet rows, where a blank would be ambiguous
        Assert.Null(row.SystemDisplay);                 // the home card, which already says "Offline"

        row.InEve = true;
        Assert.Equal("Amarr", row.SystemDisplay);
    }

    // ---- the per-character metrics window reads the same verdict ------------------------------------------------

    /// <summary>
    /// The metrics window shows a location too, from its own view-model. It gets the verdict rather than a second
    /// definition of it: that row already owned a <see cref="DpsViewModel"/> for its graph, so its readout now
    /// comes from there instead of being formatted a second time beside it.
    ///
    /// Rendered in both shells, because this window is routed like the fleet screen — docked, the module host
    /// lifts the content out of the window and anything left on the window is lost.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheMetricsWindow_ReadsOffline_ForAParkedCharacter(bool docked)
    {
        using var harness = await StartAsync(inGame: [Commander]);

        // Both are this client's characters and both have a system from the gamelog; only one has a client running.
        var gamelog = harness.Services.GetRequiredService<GamelogClientService>();
        gamelog.SetLocation(Names[Commander], "Jita", DateTime.UtcNow);
        gamelog.SetLocation(Names[Alt], "Amarr", DateTime.UtcNow);

        using var vm = new MetricsWindowViewModel(
            harness.Services,
            [(Names[Commander], Commander), (Names[Alt], Alt)],
            preselect: null);
        foreach (var option in vm.Available)
            option.IsSelected = true;

        var flying = vm.Rows.Single(r => r.Character == Names[Commander]);
        var parked = vm.Rows.Single(r => r.Character == Names[Alt]);

        Assert.False(flying.Dps.IsOffline);
        Assert.Equal("Jita", flying.LocationDisplay);
        Assert.True(parked.Dps.IsOffline);
        Assert.Equal("offline", parked.LocationDisplay);   // not "Amarr", which is only where they logged off

        var root = harness.ShowMetrics(vm, docked);
        var readouts = root.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsVisible && t.Text is "Jita" or "Amarr" or "offline")
            .ToList();

        Assert.Contains(readouts, t => t.Text == "Jita");
        Assert.Contains(readouts, t => t.Text == "offline");
        Assert.DoesNotContain(readouts, t => t.Text == "Amarr");

        var offline = readouts.Single(t => t.Text == "offline");
        Assert.Contains("offline", offline.Classes);
        Assert.Equal(DimColour(), Assert.IsAssignableFrom<ISolidColorBrush>(offline.Foreground).Color);
    }

    /// <summary>And it moves with the pilot, on the one announcement, without waiting for the 1 Hz refresh.</summary>
    [AvaloniaFact]
    public async Task TheMetricsWindow_FollowsAPilotLoggingBackIn()
    {
        using var harness = await StartAsync(inGame: [Commander]);
        harness.Services.GetRequiredService<GamelogClientService>()
            .SetLocation(Names[Alt], "Amarr", DateTime.UtcNow);

        using var vm = new MetricsWindowViewModel(harness.Services, [(Names[Alt], Alt)], preselect: Names[Alt]);
        var row = Assert.Single(vm.Rows);
        Assert.Equal("offline", row.LocationDisplay);

        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await harness.SetInGameAsync(Commander, Alt);

        Assert.Equal("Amarr", row.LocationDisplay);
        Assert.Contains(nameof(CharacterMetricsRowViewModel.LocationDisplay), changed);
    }

    /// <summary>
    /// The boundary that does not move: a row for a character this client does not own is never called offline,
    /// however little we can see of them. That stays ET-70's question.
    /// </summary>
    [AvaloniaFact]
    public async Task TheMetricsWindow_ClaimsNothing_AboutACharacterThatIsNotOurs()
    {
        using var harness = await StartAsync(inGame: [Commander]);
        harness.Services.GetRequiredService<GamelogClientService>()
            .SetLocation(Names[Stranger], "Amarr", DateTime.UtcNow);

        using var vm = new MetricsWindowViewModel(
            harness.Services, [(Names[Stranger], Stranger)], preselect: Names[Stranger]);

        var row = Assert.Single(vm.Rows);
        Assert.False(row.Dps.IsLocalCharacter);
        Assert.False(row.Dps.IsOffline);
        Assert.Equal("Amarr", row.LocationDisplay);
    }

    // ---- harness ------------------------------------------------------------------------------------------------

    // Read out of the live theme rather than pinned as a hex here, so the assertion follows the palette.
    private static Color DimColour() =>
        Assert.IsAssignableFrom<ISolidColorBrush>(
            Avalonia.Application.Current?.FindResource("TextDimBrush")
            ?? throw new InvalidOperationException("no TextDimBrush")).Color;

    private static EveClientEvidence Evidence(IEnumerable<string> names) =>
        new(new HashSet<string>(names, StringComparer.OrdinalIgnoreCase), new HashSet<int>());

    private static readonly Dictionary<int, string> Names = new()
    {
        [Commander] = "RaymondKrah",
        [Alt] = "Lionear",
        [Stranger] = "Tarek",
    };

    private static async Task<Harness> StartAsync(params int[] inGame)
    {
        var probe = new FakeProbe();
        var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IEveClientProbe>(probe);
            services.AddSingleton<IExternalCharacterLookup>(new FakeExternalLookup
            {
                [Commander] = Names[Commander],
                [Alt] = Names[Alt],
                [Stranger] = Names[Stranger],
            });
        });

        // Commander and Alt are this client's characters; Stranger is somebody else's and is deliberately absent.
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        await registry.AddOrUpdateAsync(new Character(Names[Commander], Commander));
        await registry.AddOrUpdateAsync(new Character(Names[Alt], Alt));

        var fleets = new FakeFleetClient
        {
            Members =
            [
                new FleetMemberInfo(1, Commander, -1, -1, FleetRole.FleetCommander, false),
                new FleetMemberInfo(2, Alt, 1, 1, FleetRole.SquadMember, false),
                new FleetMemberInfo(3, Stranger, 1, 1, FleetRole.SquadMember, false),
            ],
        };

        var harness = new Harness(instance, probe, fleets);
        await harness.SetInGameAsync(inGame);
        await harness.OpenAsync();
        return harness;
    }

    private sealed class Harness(TestClientInstance instance, FakeProbe probe, FakeFleetClient fleets) : IDisposable
    {
        public IServiceProvider Services => instance.Services;

        public FleetMetricsViewModel Vm { get; private set; } = null!;

        public async Task OpenAsync()
        {
            Vm = new FleetMetricsViewModel(instance.Services, fleets, Op);
            for (var i = 0; i < 200 && Vm.Members.Count < fleets.Members.Count; i++)
                await Task.Delay(20);
            Assert.Equal(fleets.Members.Count, Vm.Members.Count);

            // Names land one lookup after the rows themselves, and the presence verdict is matched on them.
            for (var i = 0; i < 200 && Vm.Members.Any(m => m.Character.StartsWith("Char ", StringComparison.Ordinal)); i++)
                await Task.Delay(20);
        }

        /// <summary>Sets which of this client's pilots have a client running, and drives both sweeps the verdict
        /// rests on so a test never races the 5 s timer or the registry read.</summary>
        public async Task SetInGameAsync(params int[] characterIds)
        {
            probe.Evidence = Evidence(characterIds.Select(id => Names[id]));
            instance.Services.GetRequiredService<EveClientPresenceService>().PollOnce();
            await instance.Services.GetRequiredService<LocalCharacterPresence>().ReloadAsync();
            Dispatcher.UIThread.RunJobs();
        }

        public async Task SayAsync(int characterId, string system)
        {
            await instance.Services.GetRequiredService<IEventBus>().PublishAsync(
                new FleetMetricEvent(new MetricSample(characterId, FleetId, MetricKind.Location, 0, 0, system)));
            for (var i = 0; i < 200 && Row(characterId).Location != system; i++)
                await Task.Delay(10);
        }

        public DpsViewModel Row(int characterId) =>
            Vm.Members.Single(m => string.Equals(m.Character, Names[characterId], StringComparison.OrdinalIgnoreCase));

        public async Task<FleetCommanderPresence> WaitForPresenceAsync(Func<FleetCommanderPresence, bool> ready)
        {
            for (var i = 0; i < 200 && !ready(Vm.CommanderPresence); i++)
                await Task.Delay(20);
            return Vm.CommanderPresence;
        }

        /// <summary>The per-character metrics window, through the same two shells the fleet screen goes through.</summary>
        public Control ShowMetrics(MetricsWindowViewModel metrics, bool docked) =>
            Present(new MetricsWindow(metrics) { Width = 900, Height = 700 }, docked);

        public Control Show(bool docked) =>
            Present(new FleetMetricsWindow(Vm) { Width = 900, Height = 620 }, docked);

        private static Control Present(Window window, bool docked)
        {
            Window root = window;

            if (docked)
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
            root.UpdateLayout();
            return root;
        }

        public void Dispose()
        {
            Vm?.Dispose();
            instance.Dispose();
        }
    }

    private sealed class FakeProbe : IEveClientProbe
    {
        public EveClientEvidence Evidence { get; set; } = EveClientEvidence.Empty;

        public EveClientEvidence Probe() => Evidence;
        public int RunningClientCount() => Evidence.CharacterNames.Count;
        public bool Activate(string characterName) => false;
    }

    private sealed class FakeDisplay : IModuleHostDisplay
    {
        public bool IsFloating { get; set; }
        public ObservableCollection<HostTab> HostTabs { get; } = new();
        public HostTab? SelectedHostTab { get; set; }
    }
}
