using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Esi;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Settings.Commands;
using EveUtils.Shared.Modules.Settings.Dtos;
using EveUtils.Shared.Modules.Settings.Queries;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using ICqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-44: right-clicking a fleet member gives one shared menu — what we know about that pilot beyond what the card
/// shows, plus the owner-only removal. The menu is defined once (<see cref="FleetMemberMenu"/> + the app-level
/// control themes) and mounted on every member row, so these tests check it through every layout AND every shell:
/// a context menu is exactly the sort of thing that falls out of the docked tab unnoticed (ET-30, ET-43).
/// Removing means removing from the EVE Together fleet; the in-game kick is a separate, second question.
/// </summary>
public class FleetMemberMenuTests
{
    private const int Commander = 90250177;
    private const int Member = 90250178;
    private const int ExternalPilot = 96000001;   // on the roster, no client here, so never a sample of their own
    private const long FleetId = 100;
    private const long EsiFleetId = 999;

    // Owned by the commander, so the commander is the FC-and-owner and the member is the one who can be removed.
    private static FleetInfo Op(long? esiFleetId = null, int? esiBossId = null) =>
        new(FleetId, "Op", null, FleetVisibility.Public, FleetState.Active, Commander,
            null, null, DateTimeOffset.UnixEpoch, FleetActivation.Active,
            EsiFleetId: esiFleetId, EsiFleetBossId: esiBossId);

    private static FakeFleetClient Roster() => new()
    {
        Members =
        [
            new FleetMemberInfo(1, Commander, -1, -1, FleetRole.FleetCommander, false),
            new FleetMemberInfo(2, Member, 1, 1, FleetRole.SquadCommander, false),
        ],
    };

    // The same roster plus an external pilot: on it, but with no client of their own here (ET-46).
    private static FakeFleetClient RosterWithExternal() => new()
    {
        Members =
        [
            new FleetMemberInfo(1, Commander, -1, -1, FleetRole.FleetCommander, false),
            new FleetMemberInfo(2, Member, 1, 1, FleetRole.SquadCommander, false),
            new FleetMemberInfo(3, ExternalPilot, 1, 1, FleetRole.SquadMember, IsExternal: true),
        ],
    };

    private static TestClientInstance CreateInstance(RecordingDialogService? dialogs = null) =>
        TestClientInstance.Create(services =>
        {
            services.AddSingleton<IExternalCharacterLookup>(new FakeExternalLookup
            {
                [Commander] = "RaymondKrah",
                [Member] = "Lionear",
                [ExternalPilot] = "Nomad Pilot",
            });
            if (dialogs is not null)
                services.AddSingleton<IDialogService>(dialogs);
        });

    public enum Shell
    {
        OwnWindow,
        DockedTab
    }

    private sealed class FakeDisplay : IModuleHostDisplay
    {
        public bool IsFloating { get; set; }
        public ObservableCollection<HostTab> HostTabs { get; } = new();
        public HostTab? SelectedHostTab { get; set; }
    }

    private static async Task<FleetMetricsViewModel> BuildViewModelAsync(
        TestClientInstance instance, IFleetClient fleets, FleetInfo fleet, int actingCharacterId, int expected = 2)
    {
        var vm = new FleetMetricsViewModel(instance.Services, fleets, fleet, actingCharacterId);
        for (var i = 0; i < 100 && vm.Members.Count < expected; i++)
            await Task.Delay(20);
        Assert.Equal(expected, vm.Members.Count);
        return vm;
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

    private static async Task<(Control Root, FleetMetricsViewModel Vm)> ShowAsync(
        TestClientInstance instance, FakeFleetClient fleets, FleetMetricsLayout layout, Shell shell,
        FleetInfo? fleet = null, int actingCharacterId = Commander, int expected = 2)
    {
        var vm = await BuildViewModelAsync(instance, fleets, fleet ?? Op(), actingCharacterId, expected);
        var bus = instance.Services.GetRequiredService<IEventBus>();

        // A live sample for the two pilots who have a client of their own, so "last update" and the location line
        // have something real to report. An external pilot deliberately gets none — they never publish.
        int[] publishers = [Commander, Member];
        foreach (int characterId in publishers)
            await bus.PublishAsync(new FleetMetricEvent(
                new MetricSample(characterId, FleetId, MetricKind.Location, 0, 0, "Jita")));
        await WaitForAsync(() => vm.Members
            .Where(m => publishers.Contains(m.CharacterId))
            .All(m => m.Location is not null));

        vm.SetLayoutCommand.Execute(layout);

        var window = new FleetMetricsWindow(vm) { Width = 900, Height = 620 };
        Window root = window;
        if (shell is Shell.DockedTab)
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
        return (root, vm);
    }

    private static ItemsControl MemberHost(Control root, FleetMetricsViewModel vm) =>
        root.GetVisualDescendants().OfType<ItemsControl>().First(c => ReferenceEquals(c.ItemsSource, vm.Members));

    /// <summary>
    /// A real right-click on a member row, as far as this platform goes: the row's own
    /// <c>ContextRequested</c> is raised, which is what runs the window's handler and rebuilds the menu's live
    /// lines. Avalonia then tries to show the popup and the headless platform has no surface for one — by which
    /// point everything under test here has already happened. What comes back is the menu as it now stands.
    /// </summary>
    private static IReadOnlyList<FleetMemberMenuItemViewModel> OpenMenuItems(
        Control root, FleetMetricsViewModel vm, int index)
    {
        ItemsControl host = MemberHost(root, vm);
        Control container = Assert.IsAssignableFrom<Control>(host.ContainerFromIndex(index));

        // The menu hangs off the row's own Border inside the container, not off the container itself.
        Control owner = container.GetSelfAndVisualDescendants().OfType<Control>()
            .First(c => c.ContextMenu is not null);

        try
        {
            owner.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent });
        }
        catch (InvalidOperationException)
        {
            // "no overlay layer / no IPopupImpl" — the headless platform cannot show a popup. Avalonia only gets
            // here after the window's handler has run, so the menu is already built.
        }

        Dispatcher.UIThread.RunJobs();

        ContextMenu menu = owner.ContextMenu!;

        // Showing a context menu parents it to its control, which is what lets its bindings see the row. That is
        // the one step the failed popup skipped, so take it by hand.
        if (menu.Parent is null)
            ((ISetLogicalParent)menu).SetParent(owner);
        Dispatcher.UIThread.RunJobs();

        // Both halves of the shared menu have to have reached this row: the item source bound to the row's
        // MemberMenu, and the app-level theme that turns each line into a MenuItem. Losing either in a docked tab is
        // exactly the ET-30/ET-43 failure — and it shows up as an empty menu, never as an error.
        Assert.NotNull(menu.ItemContainerTheme);
        return [.. Assert.IsAssignableFrom<IEnumerable<FleetMemberMenuItemViewModel>>(menu.ItemsSource)];
    }

    private static IReadOnlyList<string> OpenMenu(Control root, FleetMetricsViewModel vm, int index) =>
        OpenMenuItems(root, vm, index).Select(item => item.Label).ToList();

    private static FleetMemberMenuItemViewModel RemoveItem(Control root, FleetMetricsViewModel vm, int index) =>
        OpenMenuItems(root, vm, index)
            .Single(item => item.Label.StartsWith("Remove ", StringComparison.Ordinal));

    private static async Task InvokeRemoveAsync(Control root, FleetMetricsViewModel vm, int index)
    {
        FleetMemberMenuItemViewModel item = RemoveItem(root, vm, index);
        Assert.True(item.IsEnabled, "the removal line rendered as information rather than an action");
        await Assert.IsAssignableFrom<IAsyncRelayCommand>(item.Command).ExecuteAsync(null);
    }

    private static int IndexOf(FleetMetricsViewModel vm, int characterId) =>
        vm.Members.Select((m, i) => (m, i)).First(x => x.m.CharacterId == characterId).i;

    private static async Task<string?> ReadSettingAsync(TestClientInstance instance, string key)
    {
        using var scope = instance.Services.CreateScope();
        IReadOnlyList<SettingDto> settings = await scope.ServiceProvider
            .GetRequiredService<ICqrsDispatcher>().Query(new GetSettingsQuery());
        return settings.FirstOrDefault(s => s.Key == key)?.Value;
    }

    private static RecordingDialogService AlwaysConfirms() =>
        new() { OnConfirm = (_, _) => Task.FromResult(true) };

    // --- The menu reaches the user, in every density and every shell ---

    /// <summary>
    /// The regression this whole screen keeps producing: something that works in a floating window is silently gone
    /// in the docked tab, which is the DEFAULT shell. A menu that renders no items reads, in the tree, exactly like
    /// a menu that is simply closed — so assert on the items themselves.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(FleetMetricsLayout.List, Shell.OwnWindow)]
    [InlineData(FleetMetricsLayout.List, Shell.DockedTab)]
    [InlineData(FleetMetricsLayout.Grid, Shell.OwnWindow)]
    [InlineData(FleetMetricsLayout.Grid, Shell.DockedTab)]
    [InlineData(FleetMetricsLayout.Compact, Shell.OwnWindow)]
    [InlineData(FleetMetricsLayout.Compact, Shell.DockedTab)]
    public async Task MemberMenu_Opens_WithItsLines_InEveryLayoutAndShell(FleetMetricsLayout layout, Shell shell)
    {
        using var instance = CreateInstance();
        var (root, vm) = await ShowAsync(instance, Roster(), layout, shell);

        var headers = OpenMenu(root, vm, IndexOf(vm, Member));

        Assert.Contains("Lionear", headers);
        Assert.Contains("Squad Commander", headers);
        Assert.Contains(headers, h => h.StartsWith("Remove Lionear", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, string.IsNullOrEmpty);
    }

    // The whole reason for the menu: it says more than the card does.
    [AvaloniaTheory]
    [InlineData(Shell.OwnWindow)]
    [InlineData(Shell.DockedTab)]
    public async Task MemberMenu_ShowsWhatTheCardDoesNot(Shell shell)
    {
        using var instance = CreateInstance();
        var (root, vm) = await ShowAsync(instance, Roster(), FleetMetricsLayout.Compact, shell);

        var headers = OpenMenu(root, vm, IndexOf(vm, Member));

        Assert.Contains("Squad Commander", headers);                              // position in the fleet
        Assert.Contains("No fit assigned", headers);                              // the ship, honestly absent
        Assert.Contains("In Jita — with the FC", headers);                        // shared location + FC verdict
        Assert.Contains(headers, h => h.StartsWith("Last update", StringComparison.Ordinal));
    }

    // A pilot who shares nothing must read as "not sharing", never as an empty or invented line.
    [AvaloniaFact]
    public async Task MemberMenu_SaysNotSharing_ForAPilotWithNoLocation()
    {
        using var instance = CreateInstance();
        var vm = await BuildViewModelAsync(instance, Roster(), Op(), Commander);
        var window = new FleetMetricsWindow(vm) { Width = 900, Height = 620 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var headers = OpenMenu(window, vm, IndexOf(vm, Member));

        Assert.Contains("Not sharing location", headers);
        Assert.Contains("No live data yet", headers);
        window.Close();
    }

    // --- Who gets the removal ---

    /// <summary>The action is the owner's. A member looking at the same screen gets the information and no more —
    /// and the server refuses them anyway, which is where the real authority sits.</summary>
    [AvaloniaTheory]
    [InlineData(Shell.OwnWindow)]
    [InlineData(Shell.DockedTab)]
    public async Task MemberMenu_OffersNoRemoval_ToSomeoneWhoDoesNotOwnTheFleet(Shell shell)
    {
        using var instance = CreateInstance();
        var (root, vm) = await ShowAsync(instance, Roster(), FleetMetricsLayout.List, shell, actingCharacterId: Member);

        var headers = OpenMenu(root, vm, IndexOf(vm, Member));

        Assert.Contains("Lionear", headers);                                       // the information still stands
        Assert.DoesNotContain(headers, h => h.StartsWith("Remove ", StringComparison.Ordinal));
    }

    // Removing the creator can never succeed — ownership has to move first — so it is not offered.
    [AvaloniaFact]
    public async Task MemberMenu_OffersNoRemoval_ForTheFleetCreator()
    {
        using var instance = CreateInstance();
        var (root, vm) = await ShowAsync(instance, Roster(), FleetMetricsLayout.List, Shell.OwnWindow);

        var headers = OpenMenu(root, vm, IndexOf(vm, Commander));

        Assert.Contains("RaymondKrah", headers);
        Assert.DoesNotContain(headers, h => h.StartsWith("Remove ", StringComparison.Ordinal));
    }

    // --- Removing ---

    /// <summary>
    /// Without an in-game coupling, removal is one question and one action: out of the EVE Together fleet, done. The
    /// card goes at once — a client-only fleet pushes no roster event, so waiting for one would leave a ghost card.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(FleetMetricsLayout.List, Shell.OwnWindow)]
    [InlineData(FleetMetricsLayout.Grid, Shell.DockedTab)]
    [InlineData(FleetMetricsLayout.Compact, Shell.DockedTab)]
    public async Task Remove_TakesTheMemberOutOfTheFleetAndOffTheScreen_WithNoEsiCoupling(
        FleetMetricsLayout layout, Shell shell)
    {
        var dialogs = AlwaysConfirms();
        using var instance = CreateInstance(dialogs);
        var fleets = Roster();
        var (root, vm) = await ShowAsync(instance, fleets, layout, shell);

        await InvokeRemoveAsync(root, vm, IndexOf(vm, Member));

        Assert.Equal([2L], fleets.RemovedMemberIds);
        Assert.DoesNotContain(vm.Members, m => m.CharacterId == Member);
        Assert.Single(vm.Members);

        // Exactly one question, and it names the pilot and says the in-game fleet is left alone.
        var (title, message) = Assert.Single(dialogs.ConfirmPrompts);
        Assert.Equal("Remove from fleet", title);
        Assert.Contains("Lionear", message, StringComparison.Ordinal);
        Assert.Contains("in-game fleet is not touched", message, StringComparison.Ordinal);
    }

    // Declining the first confirmation is a no-op everywhere: no call, no card removed, no second question.
    [AvaloniaFact]
    public async Task Remove_DoesNothing_WhenTheConfirmationIsDeclined()
    {
        var dialogs = new RecordingDialogService { OnConfirm = (_, _) => Task.FromResult(false) };
        using var instance = CreateInstance(dialogs);
        var fleets = Roster();
        var (root, vm) = await ShowAsync(instance, fleets, FleetMetricsLayout.List, Shell.OwnWindow);

        await InvokeRemoveAsync(root, vm, IndexOf(vm, Member));

        Assert.Empty(fleets.RemovedMemberIds);
        Assert.Equal(2, vm.Members.Count);
        Assert.Single(dialogs.ConfirmPrompts);
    }

    // A refused removal (the server is the authority) changes nothing on screen either.
    [AvaloniaFact]
    public async Task Remove_KeepsTheCard_WhenTheServerRefusesTheRemoval()
    {
        var dialogs = AlwaysConfirms();
        using var instance = CreateInstance(dialogs);
        var fleets = Roster();
        fleets.RemoveFailure = "You can only remove yourself from the fleet.";
        var (root, vm) = await ShowAsync(instance, fleets, FleetMetricsLayout.List, Shell.OwnWindow);

        await InvokeRemoveAsync(root, vm, IndexOf(vm, Member));

        Assert.Equal(2, vm.Members.Count);
        Assert.Single(dialogs.ConfirmPrompts); // never asks about the in-game fleet after failing here
    }

    /// <summary>
    /// ET-28: the dragged order is a list of character ids. A removed member has to fall out of it, so a rejoining
    /// pilot does not inherit an old place and the 512-character value is not spent on pilots who have left.
    /// </summary>
    [AvaloniaFact]
    public async Task Remove_DropsTheMemberFromTheStoredDragOrder()
    {
        var dialogs = AlwaysConfirms();
        using var instance = CreateInstance(dialogs);
        var fleets = Roster();
        var (root, vm) = await ShowAsync(instance, fleets, FleetMetricsLayout.List, Shell.OwnWindow);

        await InvokeRemoveAsync(root, vm, IndexOf(vm, Member));
        for (var i = 0; i < 100 && await ReadSettingAsync(instance, vm.OrderSettingKey) is null; i++)
            await Task.Delay(20);

        // The order that is left names the members that are left — no gap where the removed id was.
        Assert.Equal(Commander.ToString(), await ReadSettingAsync(instance, vm.OrderSettingKey));
    }

    // --- The second, separate question ---

    /// <summary>
    /// The operator's rule: removal is out of EVE Together first, and ONLY then, and only for a coupled fleet, a
    /// second confirmation about the in-game fleet. Never one question, never a checkbox up front.
    /// </summary>
    [AvaloniaFact]
    public async Task Remove_AsksAboutTheInGameFleetSecond_WhenTheFleetIsCoupled()
    {
        var dialogs = AlwaysConfirms();
        var esi = new RecordingKickClient();
        using var instance = CreateCoupledInstance(dialogs, esi);
        await GrantWriteFleetAsync(instance);
        var fleets = Roster();
        var (root, vm) = await ShowAsync(instance, fleets, FleetMetricsLayout.List, Shell.OwnWindow,
            Op(EsiFleetId, Commander));

        await InvokeRemoveAsync(root, vm, IndexOf(vm, Member));

        Assert.Equal(2, dialogs.ConfirmPrompts.Count);
        Assert.Equal("Remove from fleet", dialogs.ConfirmPrompts[0].Title);
        Assert.Equal("Remove from the in-game fleet too?", dialogs.ConfirmPrompts[1].Title);
        Assert.Contains("Lionear", dialogs.ConfirmPrompts[1].Message, StringComparison.Ordinal);

        Assert.Equal([2L], fleets.RemovedMemberIds);              // out of EVE Together
        Assert.Equal([Member], esi.KickedCharacters);             // and out of the live fleet, because it was agreed
    }

    /// <summary>Declining the in-game kick is a complete result: the pilot is off the EVE Together roster and stays
    /// in the live fleet. Nothing about it is a failure.</summary>
    [AvaloniaFact]
    public async Task Remove_LeavesTheInGameFleetAlone_WhenTheSecondQuestionIsDeclined()
    {
        // Yes to the removal, no to the in-game kick.
        var dialogs = new RecordingDialogService
        {
            OnConfirm = (title, _) => Task.FromResult(title == "Remove from fleet"),
        };
        var esi = new RecordingKickClient();
        using var instance = CreateCoupledInstance(dialogs, esi);
        await GrantWriteFleetAsync(instance);
        var fleets = Roster();
        var (root, vm) = await ShowAsync(instance, fleets, FleetMetricsLayout.List, Shell.OwnWindow,
            Op(EsiFleetId, Commander));

        await InvokeRemoveAsync(root, vm, IndexOf(vm, Member));

        Assert.Equal([2L], fleets.RemovedMemberIds);
        Assert.Empty(esi.KickedCharacters);
        Assert.DoesNotContain(vm.Members, m => m.CharacterId == Member);   // still gone from EVE Together
    }

    /// <summary>
    /// The state the FC must never have to guess at: they said yes to the in-game kick and it failed, so the pilot
    /// is off the roster and still in the live fleet. That has to be said out loud.
    /// </summary>
    [AvaloniaFact]
    public async Task Remove_SaysSo_WhenTheInGameKickFailsAfterItWasAgreed()
    {
        var dialogs = AlwaysConfirms();
        var esi = new RecordingKickClient { Failure = "Fleet boss required." };
        var toasts = new RecordingToastService();
        using var instance = CreateCoupledInstance(dialogs, esi, toasts);
        await GrantWriteFleetAsync(instance);
        var fleets = Roster();
        var (root, vm) = await ShowAsync(instance, fleets, FleetMetricsLayout.List, Shell.OwnWindow,
            Op(EsiFleetId, Commander));

        await InvokeRemoveAsync(root, vm, IndexOf(vm, Member));

        var toast = Assert.Single(toasts.Toasts);
        Assert.Equal(Notifications.ToastKind.Warning, toast.Kind);
        Assert.Contains("still in the in-game fleet", toast.Message ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain(vm.Members, m => m.CharacterId == Member);   // it did leave EVE Together
    }

    /// <summary>No coupling, no second question — and a coupled fleet whose boss cannot write to it does not get one
    /// either, since the only possible answer would be an ESI error.</summary>
    [AvaloniaFact]
    public async Task Remove_AsksNothingAboutTheInGameFleet_WhenTheBossCannotWriteToIt()
    {
        var dialogs = AlwaysConfirms();
        var esi = new RecordingKickClient();
        using var instance = CreateCoupledInstance(dialogs, esi);
        // No write_fleet granted to the boss.
        var fleets = Roster();
        var (root, vm) = await ShowAsync(instance, fleets, FleetMetricsLayout.List, Shell.OwnWindow,
            Op(EsiFleetId, Commander));

        await InvokeRemoveAsync(root, vm, IndexOf(vm, Member));

        Assert.Single(dialogs.ConfirmPrompts);
        Assert.Empty(esi.KickedCharacters);
        Assert.Equal([2L], fleets.RemovedMemberIds);
    }

    // --- The external pilot (ET-46) ---

    /// <summary>
    /// ET-46 made the external pilot visible again — they are on the roster, they have no client here, and they can
    /// therefore never publish a sample. Every line of the menu has to have an honest answer for that pilot rather
    /// than a blank or a guess, and the name has to be the looked-up one, not the "Char {id}" placeholder.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(Shell.OwnWindow)]
    [InlineData(Shell.DockedTab)]
    public async Task MemberMenu_ReadsHonestly_ForAnExternalPilotWhoPublishesNothing(Shell shell)
    {
        using var instance = CreateInstance();
        var (root, vm) = await ShowAsync(instance, RosterWithExternal(), FleetMetricsLayout.List, shell, expected: 3);

        // The lookup is async, so wait for the real name before reading the menu that quotes it.
        await WaitForAsync(() => vm.Members.All(m => !m.Character.StartsWith("Char ", StringComparison.Ordinal)));

        var headers = OpenMenu(root, vm, IndexOf(vm, ExternalPilot));

        Assert.Contains("Nomad Pilot", headers);
        Assert.Contains("Squad Member · external pilot", headers);
        Assert.Contains("Not sharing location", headers);
        Assert.Contains("No live data yet", headers);
        Assert.Contains(headers, h => h.StartsWith("Remove Nomad Pilot", StringComparison.Ordinal));
        Assert.DoesNotContain(headers, string.IsNullOrEmpty);
    }

    /// <summary>An external pilot has a roster row like anyone else, so removing them is the ordinary removal —
    /// the member id reaches the transport and the card goes.</summary>
    [AvaloniaFact]
    public async Task Remove_WorksOnAnExternalPilot()
    {
        var dialogs = AlwaysConfirms();
        using var instance = CreateInstance(dialogs);
        var fleets = RosterWithExternal();
        var (root, vm) = await ShowAsync(instance, fleets, FleetMetricsLayout.List, Shell.OwnWindow, expected: 3);
        await WaitForAsync(() => vm.Members.All(m => !m.Character.StartsWith("Char ", StringComparison.Ordinal)));

        FleetMemberMenuItemViewModel remove = RemoveItem(root, vm, IndexOf(vm, ExternalPilot));
        await Assert.IsAssignableFrom<IAsyncRelayCommand>(remove.Command).ExecuteAsync(null);

        Assert.Equal([3L], fleets.RemovedMemberIds);
        Assert.DoesNotContain(vm.Members, m => m.CharacterId == ExternalPilot);
        Assert.Contains("Nomad Pilot", Assert.Single(dialogs.ConfirmPrompts).Message, StringComparison.Ordinal);
    }

    // --- Where ET-44 meets ET-46 ---

    /// <summary>
    /// ET-46's re-read is deliberately additive: a member the roster no longer names KEEPS their row, because a row
    /// can legitimately come from samples alone. Taking someone off this screen is the removal action's job, where a
    /// human said so — so the two rules have to hold at once, and this pins both halves against a future "tidy-up"
    /// that starts pruning on refresh.
    /// </summary>
    [AvaloniaFact]
    public async Task RefreshModule_AddsWithoutRemoving_WhileTheRemovalActionStillDropsTheRow()
    {
        var dialogs = AlwaysConfirms();
        using var instance = CreateInstance(dialogs);
        var fleets = Roster();
        var (root, vm) = await ShowAsync(instance, fleets, FleetMetricsLayout.List, Shell.OwnWindow);

        // A pilot joins while the screen stands open: the additive re-read brings them in (ET-46).
        fleets.Members = [.. fleets.Members, new FleetMemberInfo(3, ExternalPilot, 1, 1, FleetRole.SquadMember, IsExternal: true)];
        vm.RefreshModule();
        Assert.True(await WaitForAsync(() => vm.Members.Count == 3), "the re-read did not pick up the new member");

        // …and a roster that no longer names someone takes nobody off: their row stays, totals and all.
        fleets.Members = [fleets.Members[0]];
        vm.RefreshModule();
        await Task.Delay(60);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, vm.Members.Count);

        // The removal action is the one thing that does drop a row.
        fleets.Members = Roster().Members;
        vm.RefreshModule();
        await WaitForAsync(() => vm.Members.Count == 3);
        await InvokeRemoveAsync(root, vm, IndexOf(vm, Member));

        Assert.DoesNotContain(vm.Members, m => m.CharacterId == Member);
        Assert.Equal(2, vm.Members.Count);
    }

    // --- The other member lists ---

    /// <summary>The same information block, built from the same place, on the roster's member rows — the ticket's
    /// "one shared menu on a fleet member, not one per screen".</summary>
    [AvaloniaFact]
    public void RosterMemberNode_CarriesTheSharedMemberMenu()
    {
        var member = new FleetMemberInfo(7, Member, 1, 1, FleetRole.SquadCommander, false);
        var node = new MemberNodeViewModel(
            member, "Lionear", isOwner: true, [],
            unassignCommand: new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => Task.CompletedTask),
            removeFromFleetCommand: new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => Task.CompletedTask),
            transferOwnershipCommand: new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => Task.CompletedTask),
            assignFitCommand: new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => Task.CompletedTask),
            openFitCommand: new CommunityToolkit.Mvvm.Input.AsyncRelayCommand(() => Task.CompletedTask));

        var labels = node.MemberMenu.Select(i => i.Label).ToList();

        Assert.Contains("Lionear", labels);
        Assert.Contains("Squad Commander", labels);
        Assert.Contains("No fit assigned", labels);

        // The roster keeps its own "Remove from fleet" item, so the shared block adds no second one.
        Assert.DoesNotContain(labels, l => l.StartsWith("Remove ", StringComparison.Ordinal));

        // The left member list shows the same block, so an unplaced member has the same surface.
        Assert.Equal(labels, RosterEntryViewModel.Accepted(member, "Lionear", node).MemberMenu.Select(i => i.Label));
    }

    /// <summary>
    /// The other half of the rendering: the app-level item theme has to turn each line into a real
    /// <see cref="MenuItem"/> with its label, its command and its enablement. Driven through a <see cref="Menu"/>
    /// rather than the row's own <see cref="ContextMenu"/> because a context menu needs a popup surface the headless
    /// platform does not have — the theme under test is the same one the rows resolve.
    /// </summary>
    [AvaloniaFact]
    public void MemberMenuItemTheme_RendersEachLine_AsAMenuItemWithItsLabelAndCommand()
    {
        using var _ = TestClientInstance.Create(); // brings the App (with the global themes) up

        var facts = new FleetMemberFacts("Lionear", FleetRole.SquadCommander, IsExternal: false,
            ShipName: "Vexor Navy Issue", FitName: "Armor VNI");
        var remove = new RelayCommand(() => { });
        IReadOnlyList<FleetMemberMenuItemViewModel> items =
            FleetMemberMenu.Build(facts, DateTimeOffset.UnixEpoch, remove);

        Assert.True(Application.Current!.TryGetResource("FleetMemberMenuItemTheme", null, out var themeObject));
        var root = new MenuItem
        {
            Header = "Pilot",
            ItemsSource = items,
            ItemContainerTheme = (ControlTheme)themeObject!,
        };
        var menu = new Menu();
        menu.Items.Add(root);
        var window = new Window { Content = menu, Width = 360, Height = 320 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        root.IsSubMenuOpen = true;
        Dispatcher.UIThread.RunJobs();

        var rendered = Enumerable.Range(0, items.Count)
            .Select(i => Assert.IsType<MenuItem>(root.ContainerFromIndex(i)))
            .ToList();

        Assert.Equal(items.Select(i => i.Label), rendered.Select(r => r.Header as string));
        Assert.All(rendered.Take(items.Count - 1), r => Assert.False(r.IsEnabled)); // the information lines
        Assert.True(rendered[^1].IsEnabled);
        Assert.Same(remove, rendered[^1].Command);
        window.Close();
    }

    // --- The menu's own text ---

    [AvaloniaTheory]
    [InlineData(2, "Last update just now")]
    [InlineData(42, "Last update 42s ago")]
    [InlineData(90, "Last update 1m ago")]
    [InlineData(7200, "Last update 2h ago")]
    public void MemberMenu_ReportsTheSampleAge_InvariantOfTheOperatingSystemLocale(int secondsAgo, string expected)
    {
        var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        var facts = new FleetMemberFacts("Lionear", FleetRole.SquadMember, IsExternal: false,
            LastSampleAt: now.AddSeconds(-secondsAgo), TracksLiveMetrics: true);

        var labels = FleetMemberMenu.Build(facts, now).Select(i => i.Label).ToList();

        Assert.Contains(expected, labels);
    }

    [AvaloniaFact]
    public void MemberMenu_NamesTheHullAndTheFit_WhenAFitIsAssigned()
    {
        var facts = new FleetMemberFacts("Lionear", FleetRole.SquadMember, IsExternal: true,
            ShipName: "Vexor Navy Issue", FitName: "Armor VNI");

        var labels = FleetMemberMenu.Build(facts, DateTimeOffset.UnixEpoch).Select(i => i.Label).ToList();

        Assert.Contains("Flying Vexor Navy Issue — Armor VNI", labels);
        Assert.Contains("Squad Member · external pilot", labels);
    }

    // Information lines are shown, not clickable; only the action can be invoked.
    [AvaloniaFact]
    public void MemberMenu_LeavesItsInformationLinesDisabled()
    {
        var facts = new FleetMemberFacts("Lionear", FleetRole.SquadMember, IsExternal: false);
        var remove = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { });

        var items = FleetMemberMenu.Build(facts, DateTimeOffset.UnixEpoch, remove);

        Assert.All(items.Take(items.Count - 1), item => Assert.False(item.IsEnabled));
        Assert.True(items[^1].IsEnabled);
        Assert.Same(remove, items[^1].Command);
    }

    // --- helpers for the coupled cases ---

    private static TestClientInstance CreateCoupledInstance(
        RecordingDialogService dialogs, RecordingKickClient esi, RecordingToastService? toasts = null) =>
        TestClientInstance.Create(services =>
        {
            services.AddSingleton<IExternalCharacterLookup>(new FakeExternalLookup
            {
                [Commander] = "RaymondKrah",
                [Member] = "Lionear",
            });
            services.AddSingleton<IDialogService>(dialogs);
            services.AddSingleton<Esi.IEsiFleetClient>(esi);
            if (toasts is not null)
                services.AddSingleton<Notifications.IToastService>(toasts);
        });

    // The real scope gate reads the granted scopes off the character, so grant them rather than fake the gate.
    private static async Task GrantWriteFleetAsync(TestClientInstance instance) =>
        await instance.Services.GetRequiredService<Shared.Identity.ICharacterRegistry>().AddOrUpdateAsync(
            new Shared.Identity.Character("RaymondKrah", Commander, GrantedScopes:
                [Shared.Modules.Fleet.FleetsScopeCatalog.ReadFleet, Shared.Modules.Fleet.FleetsScopeCatalog.WriteFleet]));

    /// <summary>The in-game side, recorded: which pilots were kicked, and optionally a failure to stand in for ESI
    /// refusing the kick after the FC agreed to it. The real in-game effect is verified by hand.</summary>
    private sealed class RecordingKickClient : Esi.IEsiFleetClient
    {
        public List<int> KickedCharacters { get; } = [];
        public string? Failure { get; set; }

        public Task<EsiResult> KickMemberAsync(long fleetId, int memberCharacterId, int actingCharacterId,
            CancellationToken cancellationToken = default)
        {
            if (Failure is { } failure)
                return Task.FromResult(EsiResult.Fail(EsiError.Of(EsiErrorKind.ScopeForbidden, failure)));
            KickedCharacters.Add(memberCharacterId);
            return Task.FromResult(EsiResult.Ok());
        }

        public Task<EsiResult<EsiCharacterFleet>> GetCharacterFleetAsync(int characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult<EsiCharacterFleet>.Fail(EsiError.Of(EsiErrorKind.NotFound, "not used")));

        public Task<EsiResult<EsiFleetMember[]>> GetMembersAsync(long fleetId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult<EsiFleetMember[]>.Ok([]));

        public Task<EsiResult<EsiFleetWing[]>> GetWingsAsync(long fleetId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult<EsiFleetWing[]>.Ok([]));

        public Task<EsiResult> SetFleetSettingsAsync(long fleetId, int actingCharacterId, string? motd, bool? isFreeMove, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult.Ok());

        public Task<EsiResult> MoveMemberAsync(long fleetId, int memberCharacterId, string role, long? wingId, long? squadId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult.Ok());

        public Task<EsiResult<long>> CreateWingAsync(long fleetId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult<long>.Ok(0));

        public Task<EsiResult> RenameWingAsync(long fleetId, long wingId, string name, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult.Ok());

        public Task<EsiResult<long>> CreateSquadAsync(long fleetId, long wingId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult<long>.Ok(0));

        public Task<EsiResult> RenameSquadAsync(long fleetId, long squadId, string name, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult.Ok());

        public Task<EsiResult> DeleteWingAsync(long fleetId, long wingId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult.Ok());

        public Task<EsiResult> DeleteSquadAsync(long fleetId, long squadId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult.Ok());

        public Task<EsiResult> InviteMemberAsync(long fleetId, int characterId, string role, long? wingId, long? squadId, int actingCharacterId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EsiResult.Ok());
    }
}
