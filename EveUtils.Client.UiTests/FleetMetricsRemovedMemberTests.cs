using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
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
/// ET-49: a pilot the FC removes leaves the fleet-metrics screen and stays gone — the moment the removal is
/// confirmed, and again when the screen is opened afresh. Two separate mechanisms used to put them back, and each
/// needs its own guard:
///
/// <list type="number">
/// <item>the row is raised again by the next incoming <b>sample</b> (lazy discovery in <c>Track</c>), because this
/// client keeps publishing for a pilot it just kicked until the fleets listing happens to reload;</item>
/// <item>the row survives a <b>re-read</b> of the roster, because re-reading is additive (ET-46) and additive can by
/// definition never take a member off.</item>
/// </list>
///
/// The additive contract itself is not the bug and is kept: a row may legitimately come from samples alone — the
/// straggler who is in the fleet in-game but has never been on the roster. What separates the two is that a removal
/// is an <i>event</i>: a pilot the roster HAS named and no longer names has been taken off, where a pilot the roster
/// has never named is simply not on it.
/// </summary>
public class FleetMetricsRemovedMemberTests
{
    private const int Commander = 90250177;
    private const int Member = 90250178;
    private const int Straggler = 90250179;   // never on the roster: only ever arrives through a sample
    private const long FleetId = 100;

    private static FleetInfo Op() =>
        new(FleetId, "Op", null, FleetVisibility.Public, FleetState.Active, Commander,
            null, null, DateTimeOffset.UnixEpoch, FleetActivation.Active);

    private static FakeFleetClient Roster() => new()
    {
        Members =
        [
            new FleetMemberInfo(1, Commander, -1, -1, FleetRole.FleetCommander, false),
            new FleetMemberInfo(2, Member, 1, 1, FleetRole.SquadCommander, false),
        ],
    };

    private static TestClientInstance CreateInstance(RecordingDialogService? dialogs = null) =>
        TestClientInstance.Create(services =>
        {
            services.AddSingleton<IExternalCharacterLookup>(new FakeExternalLookup
            {
                [Commander] = "RaymondKrah",
                [Member] = "Lionear",
                [Straggler] = "Tarek",
            });
            if (dialogs is not null)
                services.AddSingleton<IDialogService>(dialogs);
        });

    private static RecordingDialogService AlwaysConfirms() =>
        new() { OnConfirm = (_, _) => Task.FromResult(true) };

    /// <summary>Every shell this screen ever reaches a user through. Docked is the default; the float↔dock migration
    /// hands the same content back and forth and has twice been where a fix stopped applying (ET-30, ET-43).</summary>
    public enum Shell
    {
        OwnWindow,
        DockedTab,
        MigratedToFloating
    }

    private sealed class FakeDisplay : IModuleHostDisplay
    {
        public bool IsFloating { get; set; }
        public ObservableCollection<HostTab> HostTabs { get; } = new();
        public HostTab? SelectedHostTab { get; set; }
    }

    // Polls the UI thread until the condition holds; returns whether it did.
    private static async Task<bool> WaitForAsync(Func<bool> condition, int tries = 100)
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

    // Lets everything already queued settle, so an assertion that something did NOT happen is not just early.
    private static async Task SettleAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static async Task<FleetMetricsViewModel> BuildViewModelAsync(
        TestClientInstance instance, IFleetClient fleets, int expected = 2)
    {
        var vm = new FleetMetricsViewModel(instance.Services, fleets, Op(), Commander);
        Assert.True(await WaitForAsync(() => vm.Members.Count == expected),
            $"the roster pre-fill did not land ({vm.Members.Count} of {expected} rows)");
        return vm;
    }

    private static async Task PublishAsync(TestClientInstance instance, int characterId, string system) =>
        await instance.Services.GetRequiredService<IEventBus>().PublishAsync(new FleetMetricEvent(
            new MetricSample(characterId, FleetId, MetricKind.Location, 0, 0, system)));

    private static (Control Root, ModuleHostService? Host) Present(FleetMetricsViewModel vm, Shell shell)
    {
        var window = new FleetMetricsWindow(vm) { Width = 900, Height = 620 };
        if (shell is Shell.OwnWindow)
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            return (window, null);
        }

        var display = new FakeDisplay { IsFloating = false };
        var host = new ModuleHostService();
        host.SetOwner(new Window());
        host.SetHost(display);
        host.Open(window, $"FLEET METRICS · {vm.FleetName}", "fleet", $"fleet-metrics:{vm.FleetId}");

        if (shell is Shell.MigratedToFloating)
        {
            // The dock→float migration hands the content back to its own window; the module's wiring has to survive
            // the round trip, which is precisely what this screen family keeps losing.
            display.IsFloating = true;
            host.SwitchMode();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            return (window, host);
        }

        var root = new Window { Width = 900, Height = 620, Content = Assert.Single(display.HostTabs).Content };
        root.Show();
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
        return (root, host);
    }

    private static ItemsControl MemberHost(Control root, FleetMetricsViewModel vm) =>
        root.GetVisualDescendants().OfType<ItemsControl>().First(c => ReferenceEquals(c.ItemsSource, vm.Members));

    // What the screen actually draws — a member who is gone from the collection but still on screen is the failure
    // mode this window family keeps producing, so every assertion here reads the rendered rows too.
    private static IReadOnlyList<string> MemberTexts(Control root, FleetMetricsViewModel vm)
    {
        Dispatcher.UIThread.RunJobs();
        root.UpdateLayout();
        return MemberHost(root, vm).GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text ?? string.Empty)
            .Where(t => t.Length > 0)
            .ToList();
    }

    /// <summary>The user's own path into the removal: right-click the row, then invoke the one action line on it.</summary>
    private static async Task RemoveThroughTheMenuAsync(Control root, FleetMetricsViewModel vm, int characterId)
    {
        ItemsControl host = MemberHost(root, vm);
        int index = vm.Members.Select((m, i) => (m, i)).First(x => x.m.CharacterId == characterId).i;
        Control container = Assert.IsAssignableFrom<Control>(host.ContainerFromIndex(index));
        Control owner = container.GetSelfAndVisualDescendants().OfType<Control>().First(c => c.ContextMenu is not null);

        try
        {
            owner.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
        }
        catch (InvalidOperationException)
        {
            // Headless has no popup surface; Avalonia only gets here after the window rebuilt the menu.
        }

        Dispatcher.UIThread.RunJobs();
        ContextMenu menu = owner.ContextMenu!;
        if (menu.Parent is null)
            ((ISetLogicalParent)menu).SetParent(owner);
        Dispatcher.UIThread.RunJobs();

        FleetMemberMenuItemViewModel remove =
            Assert.IsAssignableFrom<IEnumerable<FleetMemberMenuItemViewModel>>(menu.ItemsSource)
                .Single(item => item.Label.StartsWith("Remove ", StringComparison.Ordinal));
        await Assert.IsAssignableFrom<IAsyncRelayCommand>(remove.Command).ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
    }

    // --- Symptom 1: the card comes straight back ---

    /// <summary>
    /// The first half of what the operator saw: the card is gone for an instant and back a moment later. Nothing
    /// re-reads the roster here — the row is raised again by lazy discovery in <c>Track</c>, off a sample this client
    /// is still publishing for a pilot it has just kicked. Runs through every density and every shell because the
    /// removal is wired once, on the shared member <c>ItemsControl</c>.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(FleetMetricsLayout.List, Shell.OwnWindow)]
    [InlineData(FleetMetricsLayout.List, Shell.DockedTab)]
    [InlineData(FleetMetricsLayout.List, Shell.MigratedToFloating)]
    [InlineData(FleetMetricsLayout.Grid, Shell.OwnWindow)]
    [InlineData(FleetMetricsLayout.Grid, Shell.DockedTab)]
    [InlineData(FleetMetricsLayout.Compact, Shell.OwnWindow)]
    [InlineData(FleetMetricsLayout.Compact, Shell.DockedTab)]
    public async Task RemovedMember_DoesNotComeBack_WhenALateSampleArrives(FleetMetricsLayout layout, Shell shell)
    {
        using var instance = CreateInstance(AlwaysConfirms());
        var fleets = Roster();
        var vm = await BuildViewModelAsync(instance, fleets);
        vm.SetLayoutCommand.Execute(layout);
        var (root, _) = Present(vm, shell);

        await RemoveThroughTheMenuAsync(root, vm, Member);
        Assert.DoesNotContain(vm.Members, m => m.CharacterId == Member);

        // The kick does not stop the world: a sample stamped before it lands a moment after it.
        await PublishAsync(instance, Member, "Jita");
        await SettleAsync();

        Assert.DoesNotContain(vm.Members, m => m.CharacterId == Member);
        Assert.DoesNotContain("Lionear", MemberTexts(root, vm));

        // …and the fleet still reports on everyone who is in it.
        Assert.Contains(vm.Members, m => m.CharacterId == Commander);
        vm.Dispose();
    }

    /// <summary>The kicked pilot's samples stop at the source too: this client publishes for the fleets it is in, and
    /// a pilot it has just removed is not one of them. Without this the publisher keeps pushing that pilot at 1 Hz
    /// until the fleets listing reloads — which, with the fleets window closed, it never does.</summary>
    [AvaloniaFact]
    public async Task RemovedMember_IsDroppedFromTheParticipationSet_SoNothingKeepsPublishingForThem()
    {
        using var instance = CreateInstance(AlwaysConfirms());
        var participation = instance.Services.GetRequiredService<IFleetParticipation>();
        participation.Set([
            new FleetParticipant(Commander, FleetId, ClientOnly: false),
            new FleetParticipant(Member, FleetId, ClientOnly: false),
        ]);

        var vm = await BuildViewModelAsync(instance, Roster());
        var (root, _) = Present(vm, Shell.DockedTab);

        await RemoveThroughTheMenuAsync(root, vm, Member);

        Assert.DoesNotContain(participation.Current, p => p.CharacterId == Member && p.FleetId == FleetId);
        Assert.Contains(participation.Current, p => p.CharacterId == Commander && p.FleetId == FleetId);
        vm.Dispose();
    }

    // --- Symptom 2: still there after the screen is opened again ---

    /// <summary>
    /// The second half: OPEN METRICS on a fleet whose screen is already standing re-selects that module and asks it to
    /// re-read (ET-46) rather than building a new one, so the re-read is the only thing that can take the pilot off —
    /// and additive re-reading never does. Driven through the real <see cref="ModuleHostService"/> with the real
    /// module id, because that de-dupe is the whole reason re-opening stopped being a fresh start.
    /// </summary>
    [AvaloniaFact]
    public async Task ReopeningTheScreen_DoesNotBringBack_AMemberRemovedElsewhere()
    {
        using var instance = CreateInstance(AlwaysConfirms());
        var fleets = Roster();
        var standing = await BuildViewModelAsync(instance, fleets);

        var display = new FakeDisplay { IsFloating = false };
        var host = new ModuleHostService();
        host.SetOwner(new Window());
        host.SetHost(display);
        host.Open(new FleetMetricsWindow(standing), "FLEET METRICS · Op", "fleet", $"fleet-metrics:{FleetId}");

        // The removal happens on another screen (the roster window), so this one only ever learns of it by re-reading.
        fleets.Members = [fleets.Members[0]];

        // OPEN METRICS again: same module id, so the host re-selects the standing module and refreshes it.
        var second = new FleetMetricsViewModel(instance.Services, fleets, Op(), Commander);
        host.Open(new FleetMetricsWindow(second), "FLEET METRICS · Op", "fleet", $"fleet-metrics:{FleetId}");

        Assert.True(await WaitForAsync(() => standing.Members.All(m => m.CharacterId != Member)),
            "the removed pilot is still on the re-opened metrics screen");

        var root = new Window { Width = 900, Height = 620, Content = Assert.Single(display.HostTabs).Content };
        root.Show();
        Assert.DoesNotContain("Lionear", MemberTexts(root, standing));
        Assert.Contains(standing.Members, m => m.CharacterId == Commander);
        standing.Dispose();
    }

    /// <summary>Same removal, seen from the screen that did it: the roster read that follows must not undo it either.
    /// This is the plain <c>fleet.changed</c> path — a re-read after the row was already dropped.</summary>
    [AvaloniaTheory]
    [InlineData(Shell.OwnWindow)]
    [InlineData(Shell.DockedTab)]
    public async Task RemovedMember_StaysGone_AcrossAFleetChangedRefresh(Shell shell)
    {
        using var instance = CreateInstance(AlwaysConfirms());
        var fleets = Roster();
        var vm = await BuildViewModelAsync(instance, fleets);
        var (root, _) = Present(vm, shell);

        await RemoveThroughTheMenuAsync(root, vm, Member);
        Assert.Equal([2L], fleets.RemovedMemberIds);

        vm.RefreshModule();
        await SettleAsync();

        Assert.DoesNotContain(vm.Members, m => m.CharacterId == Member);
        Assert.DoesNotContain("Lionear", MemberTexts(root, vm));
        vm.Dispose();
    }

    // --- The line the fix must not cross ---

    /// <summary>
    /// ET-46's reason for making the re-read additive, kept: a row may come from samples alone. The straggler is in
    /// the fleet in-game and has never been on the roster, so a re-read that does not name them is not a removal —
    /// there is nothing to remove them from. Take this away and the fix has become "prune everything off-roster".
    /// </summary>
    [AvaloniaFact]
    public async Task AStragglerTheRosterHasNeverNamed_KeepsTheirRow_AcrossEveryRefresh()
    {
        using var instance = CreateInstance();
        var fleets = Roster();
        var vm = await BuildViewModelAsync(instance, fleets);

        await PublishAsync(instance, Straggler, "Jita");
        Assert.True(await WaitForAsync(() => vm.Members.Any(m => m.CharacterId == Straggler)),
            "the straggler never got a row");

        vm.RefreshModule();
        await SettleAsync();
        vm.RefreshModule();
        await SettleAsync();

        Assert.Contains(vm.Members, m => m.CharacterId == Straggler);
        Assert.Equal(3, vm.Members.Count);
        vm.Dispose();
    }

    /// <summary>A removal is an event, not a verdict: the pilot who rejoins is named by the roster again and gets
    /// their row back, live figures and all.</summary>
    [AvaloniaFact]
    public async Task APilotWhoRejoins_GetsTheirRowBack()
    {
        using var instance = CreateInstance(AlwaysConfirms());
        var fleets = Roster();
        var vm = await BuildViewModelAsync(instance, fleets);
        var (root, _) = Present(vm, Shell.DockedTab);

        await RemoveThroughTheMenuAsync(root, vm, Member);
        Assert.DoesNotContain(vm.Members, m => m.CharacterId == Member);

        fleets.Members = [.. fleets.Members, new FleetMemberInfo(9, Member, 1, 1, FleetRole.SquadMember, false)];
        vm.RefreshModule();

        Assert.True(await WaitForAsync(() => vm.Members.Any(m => m.CharacterId == Member)),
            "a pilot who rejoined the fleet never came back onto the screen");

        // And their live data flows again — the removal must not have left a permanent block on their samples.
        await PublishAsync(instance, Member, "Amarr");
        Assert.True(await WaitForAsync(() =>
            vm.Members.Single(m => m.CharacterId == Member).Location == "Amarr"), "their samples are still blocked");
        vm.Dispose();
    }

    // --- The other two presentation paths from ET-43's table ---

    /// <summary>The DPS pop-out is a window of its own that shares the tracker instance. A pilot who is out of the
    /// fleet has no live data to show there either, so their samples must stop reaching it.</summary>
    [AvaloniaFact]
    public async Task TheDpsPopOut_OfARemovedMember_StopsBeingFed()
    {
        using var instance = CreateInstance(AlwaysConfirms());
        var vm = await BuildViewModelAsync(instance, Roster());
        var (root, _) = Present(vm, Shell.OwnWindow);

        DpsViewModel tracker = vm.Members.Single(m => m.CharacterId == Member);
        var overlay = new DpsOverlayWindow(tracker) { Width = 320, Height = 200 };
        overlay.Show();
        Dispatcher.UIThread.RunJobs();

        await RemoveThroughTheMenuAsync(root, vm, Member);

        await PublishAsync(instance, Member, "Jita");
        await SettleAsync();

        // Neither the popped-out graph nor the screen behind it may take that sample: taking it here would mean the
        // screen raised a second row for the same pilot, which is the come-back in another guise.
        Assert.Null(tracker.Location);
        Assert.DoesNotContain(vm.Members, m => m.CharacterId == Member);
        overlay.Close();
        vm.Dispose();
    }

    /// <summary>The ticket's third possibility, ruled in or out: closing the docked metrics tab has to really close
    /// the module — dispose its view-model and drop it from the host — or OPEN METRICS would hand back the very
    /// screen the user just closed.</summary>
    [AvaloniaFact]
    public async Task ClosingTheDockedTab_DisposesTheModule_SoTheNextOpenBuildsAFreshOne()
    {
        using var instance = CreateInstance();
        var fleets = Roster();
        var first = await BuildViewModelAsync(instance, fleets);

        var display = new FakeDisplay { IsFloating = false };
        var host = new ModuleHostService();
        host.SetOwner(new Window());
        host.SetHost(display);
        host.Open(new FleetMetricsWindow(first), "FLEET METRICS · Op", "fleet", $"fleet-metrics:{FleetId}");

        Assert.Single(display.HostTabs).CloseCommand!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(display.HostTabs);

        // A disposed module no longer follows the bus: a sample after the close reaches nothing.
        await PublishAsync(instance, Member, "Jita");
        await SettleAsync();
        Assert.Null(first.Members.Single(m => m.CharacterId == Member).Location);

        // …and the next OPEN really is a fresh screen, not the closed one handed back.
        var second = await BuildViewModelAsync(instance, fleets);
        host.Open(new FleetMetricsWindow(second), "FLEET METRICS · Op", "fleet", $"fleet-metrics:{FleetId}");
        Assert.Same(second, Assert.Single(display.HostTabs).Content is Control content ? content.DataContext : null);
        second.Dispose();
    }
}
