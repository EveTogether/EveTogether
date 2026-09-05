using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Commands;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Events;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using CqrsDispatcher = EveUtils.Shared.Cqrs.IDispatcher;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;
using UiDispatcher = Avalonia.Threading.Dispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Stopping a fleet puts it back on standby instead of ending it (ET-166). The transition itself is proven against
/// the real repository — roster intact, members uncoupled, startable again — and the screen side is proven where an
/// FC meets it: one STOP button in the roster header that opens a dialog offering the three exits, with concluding
/// still one step away and disbanding deliberately not present. Rendered in all three shells this app has, because
/// a docked tab is a different control tree from a floating window (ET-42).
/// </summary>
public class FleetStopTests
{
    private const int Owner = 95000166;
    private const int Member = 95000167;
    private const int Stranger = 95000168;

    private static FleetInfo InfoFor(FleetEntity fleet, FleetActivation activation, DateTimeOffset? activatedAt = null) => new(
        fleet.Id, fleet.Name, fleet.Description, fleet.Visibility, fleet.State,
        fleet.CreatorCharacterId, fleet.FromTime, fleet.ToTime, fleet.CreatedAt, activation,
        ActivatedAt: activatedAt);

    private static FleetInfo FakeFleet(FleetActivation activation, DateTimeOffset? activatedAt = null) =>
        new(8, "Wednesday Homefronts", null, FleetVisibility.Public, FleetState.Active, Owner,
            null, null, DateTimeOffset.UtcNow, activation, ActivatedAt: activatedAt);

    private static FleetMemberInfo MemberRow(long id, int characterId, bool external = false) =>
        new(id, characterId, -1, -1, FleetRole.SquadMember, external);

    private static async Task WaitLoadedAsync(FleetRosterViewModel roster)
    {
        for (var i = 0; i < 100 && roster.Entries.Count == 0; i++)
            await Task.Delay(50);
    }

    /// <summary>
    /// The acceptance criterion itself: an active fleet that is stopped stands by with the same members on it, those
    /// members are coupled to nothing (so they can be coupled elsewhere), and it starts again.
    /// </summary>
    [AvaloniaFact]
    public async Task Stop_StandsTheFleetDown_KeepsItsRoster_AndFreesItsMembers()
    {
        using var instance = TestClientInstance.Create();
        var services = instance.Services;
        var fleetService = services.GetRequiredService<ClientFleetService>();
        var repository = services.GetRequiredService<IFleetRepository>();
        var characters = services.GetRequiredService<ICharacterRegistry>();

        var created = await fleetService.CreateLocalFleetAsync("Wednesday Homefronts", null, Owner);
        Assert.True(created.IsSuccess);
        var fleetId = created.Value;
        await repository.AddMemberAsync(new FleetMember
        {
            FleetId = fleetId,
            CharacterId = Member,
            Role = FleetRole.SquadMember,
            IsExternal = false
        });

        var client = new LocalFleetClient(fleetService, repository, characters, Owner);
        Assert.True((await client.StartFleetAsync(fleetId)).Ok);

        // While it runs, the member is coupled here — that is what blocks them joining another fleet that goes active.
        Assert.Equal(fleetId, Assert.Single(await repository.ListActiveMembershipsAsync(Member)).FleetId);

        var stopped = await client.StopFleetAsync(fleetId);
        Assert.True(stopped.Ok, stopped.Message);

        var after = await repository.GetAsync(fleetId);
        Assert.Equal(FleetActivation.Forming, after!.Activation);
        Assert.Equal(FleetState.Active, after.State);   // standing by is not archived: the fleet is still there

        // The roster survives the stop — that is the whole reason a recurring op does not have to be recreated.
        Assert.Equal(Member, Assert.Single(
            await repository.ListMembersAsync(fleetId), m => m.CharacterId == Member).CharacterId);

        // …and their coupling does not: they count toward no active fleet, so the next fleet may couple them.
        Assert.Empty(await repository.ListActiveMembershipsAsync(Member));

        // Next Wednesday.
        Assert.True((await client.StartFleetAsync(fleetId)).Ok);
        Assert.Equal(FleetActivation.Active, (await repository.GetAsync(fleetId))!.Activation);
    }

    /// <summary>
    /// The guards: only the creator stops a fleet, a fleet that is already standing by succeeds without a second
    /// round of notifications, and a concluded fleet is refused — stopping is the way back from Active, not a way
    /// out of the terminal state. Conclude keeps behaving exactly as it did.
    /// </summary>
    [AvaloniaFact]
    public async Task Stop_IsCreatorOnly_IsIdempotent_AndCannotResurrectAConcludedFleet()
    {
        using var instance = TestClientInstance.Create();
        var services = instance.Services;
        var fleetService = services.GetRequiredService<ClientFleetService>();
        var repository = services.GetRequiredService<IFleetRepository>();
        var dispatcher = services.GetRequiredService<CqrsDispatcher>();

        var fleetId = (await fleetService.CreateLocalFleetAsync("Guard test", null, Owner)).Value;
        Assert.True((await dispatcher.Send(new StartFleetCommand(fleetId, Owner))).IsSuccess);

        // A member — or anyone else — cannot stand the FC's fleet down.
        var foreign = await dispatcher.Send(new StopFleetCommand(fleetId, Stranger));
        Assert.False(foreign.IsSuccess);
        Assert.Contains(foreign.Messages, m => m.Code == MessageCodes.PermissionDenied);
        Assert.Equal(FleetActivation.Active, (await repository.GetAsync(fleetId))!.Activation);

        Assert.True((await dispatcher.Send(new StopFleetCommand(fleetId, Owner))).IsSuccess);
        Assert.True((await dispatcher.Send(new StopFleetCommand(fleetId, Owner))).IsSuccess);   // idempotent
        Assert.Equal(FleetActivation.Forming, (await repository.GetAsync(fleetId))!.Activation);

        // Conclude is untouched: still refused on a standing-by fleet, still terminal once taken.
        var concludeForming = await dispatcher.Send(new ConcludeFleetCommand(fleetId, Owner));
        Assert.False(concludeForming.IsSuccess);

        Assert.True((await dispatcher.Send(new StartFleetCommand(fleetId, Owner))).IsSuccess);
        Assert.True((await dispatcher.Send(new ConcludeFleetCommand(fleetId, Owner))).IsSuccess);

        var stopConcluded = await dispatcher.Send(new StopFleetCommand(fleetId, Owner));
        Assert.False(stopConcluded.IsSuccess);
        Assert.Contains(stopConcluded.Messages, m => m.Code == MessageCodes.ValidationFailed);
        Assert.Equal(FleetActivation.Concluded, (await repository.GetAsync(fleetId))!.Activation);
        Assert.False((await dispatcher.Send(new StartFleetCommand(fleetId, Owner))).IsSuccess);
    }

    /// <summary>STOP is the owner's button on a running fleet, and nobody else's — the same window opened as a
    /// member offers no way to stand the FC's fleet down.</summary>
    [AvaloniaFact]
    public async Task RosterHeader_OffersStop_OnlyToTheOwnerOfARunningFleet()
    {
        using var instance = TestClientInstance.Create();
        var services = instance.Services;
        var fleetService = services.GetRequiredService<ClientFleetService>();
        var repository = services.GetRequiredService<IFleetRepository>();
        var characters = services.GetRequiredService<ICharacterRegistry>();

        var fleetId = (await fleetService.CreateLocalFleetAsync("Header test", null, Owner)).Value;
        var fleet = await repository.GetAsync(fleetId);
        var client = new LocalFleetClient(fleetService, repository, characters, Owner);

        using var forming = new FleetRosterViewModel(services, client, InfoFor(fleet!, FleetActivation.Forming), isOwner: true, Owner);
        Assert.True(forming.CanStart);
        Assert.False(forming.CanStop);      // nothing to stand down yet

        using var active = new FleetRosterViewModel(services, client, InfoFor(fleet!, FleetActivation.Active), isOwner: true, Owner);
        Assert.False(active.CanStart);
        Assert.True(active.CanStop);
        Assert.True(active.CanConclude);    // still available — through the dialog STOP opens

        using var asMember = new FleetRosterViewModel(services, client, InfoFor(fleet!, FleetActivation.Active), isOwner: false, Member);
        Assert.False(asMember.CanStop);

        using var concluded = new FleetRosterViewModel(services, client, InfoFor(fleet!, FleetActivation.Concluded), isOwner: true, Owner);
        Assert.False(concluded.CanStop);    // terminal
    }

    /// <summary>
    /// The dialog's choice is the transition: stopping calls stop, concluding calls conclude, backing out calls
    /// neither. Three different acts behind one button is only safe if the button does what was chosen.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(StopFleetChoice.Stop)]
    [InlineData(StopFleetChoice.Conclude)]
    [InlineData(StopFleetChoice.Cancel)]
    public async Task StopButton_TakesTheExitTheDialogReturned(StopFleetChoice choice)
    {
        var dialogs = new RecordingDialogService { FleetExit = choice };
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(dialogs));
        var fake = new FakeFleetClient
        {
            Fleet = FakeFleet(FleetActivation.Active),
            Members = [MemberRow(5, Owner), MemberRow(6, Member)],
            Connected = [new ConnectedCharacterInfo(Owner, "Ravnholt")]
        };

        using var roster = new FleetRosterViewModel(instance.Services, fake, fake.Fleet!, isOwner: true, Owner);
        await WaitLoadedAsync(roster);
        await roster.StopCommand.ExecuteAsync(null);

        Assert.Equal(choice == StopFleetChoice.Stop ? [8L] : (long[])[], fake.StoppedFleetIds);
        Assert.Equal(choice == StopFleetChoice.Conclude ? [8L] : (long[])[], fake.ConcludedFleetIds);

        if (choice == StopFleetChoice.Stop)
            Assert.Equal("Forming", roster.ActivationLabel);
        else if (choice == StopFleetChoice.Conclude)
            Assert.Equal("Concluded", roster.ActivationLabel);
        else
            Assert.Equal("Active", roster.ActivationLabel);
    }

    /// <summary>
    /// The dialog is told what the fleet actually is, so the FC decides against its state and not its name: how long
    /// it has been running, who is on it split into mine / other people's / external, and whether leaving with one
    /// of my own characters is even an option here.
    /// </summary>
    [AvaloniaFact]
    public async Task StopDialog_IsToldWhatTheFleetIs()
    {
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-86);
        var dialogs = new RecordingDialogService { FleetExit = StopFleetChoice.Cancel };
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(dialogs));
        var fake = new FakeFleetClient
        {
            Fleet = FakeFleet(FleetActivation.Active, startedAt),
            // Me + one of my alts, one other pilot, one external.
            Members = [MemberRow(5, Owner), MemberRow(6, Member), MemberRow(7, Stranger), MemberRow(8, 90001, external: true)],
            Connected = [new ConnectedCharacterInfo(Owner, "Ravnholt"), new ConnectedCharacterInfo(Member, "Kaska Vex")]
        };

        using var roster = new FleetRosterViewModel(instance.Services, fake, fake.Fleet!, isOwner: true, Owner);
        await WaitLoadedAsync(roster);
        await roster.StopCommand.ExecuteAsync(null);

        var prompt = Assert.IsType<StopFleetPrompt>(dialogs.FleetExitPrompt);
        Assert.Equal("Wednesday Homefronts", prompt.FleetName);
        Assert.Equal(startedAt, prompt.ActivatedAt);
        Assert.Equal(2, prompt.OwnMemberCount);        // me + my alt
        Assert.Equal(1, prompt.OtherMemberCount);      // somebody else's pilot
        Assert.Equal(1, prompt.ExternalMemberCount);
        Assert.Equal(1, prompt.LeavableCharacterCount);   // my alt; my own FC character is never a leave candidate
    }

    /// <summary>
    /// A run that is under way reaches the dialog by name. End to end on the real coordinator — the one place that
    /// knows which of this client's runs belong to which fleet — because "your measurements survive this" is the
    /// sentence that makes stopping safe to press, and a promise made from an empty list is worse than no promise.
    /// </summary>
    [AvaloniaFact]
    public async Task StopDialog_NamesTheRunsThatAreStillGoing()
    {
        var dialogs = new RecordingDialogService { FleetExit = StopFleetChoice.Cancel };
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(dialogs));

        // The coordinator only listens once it has been resolved — it subscribes in its constructor.
        instance.Services.GetRequiredService<FleetRunGroupCodeCoordinator>();
        var bus = instance.Services.GetRequiredService<IEventBus>();
        await bus.PublishAsync(new RunStartedEvent(
            Guid.NewGuid(), Member, ActivityKind.Site, DateTime.UtcNow.AddMinutes(-11),
            fleetId: 8, groupCode: null, isFleetCommander: false,
            solarSystemName: "Otanuomi", siteName: "Fortress Sansha"), EventTarget.Local, default);

        var fake = new FakeFleetClient
        {
            Fleet = FakeFleet(FleetActivation.Active),
            Members = [MemberRow(5, Owner), MemberRow(6, Member)],
            Connected = [new ConnectedCharacterInfo(Owner, "Ravnholt"), new ConnectedCharacterInfo(Member, "Kaska Vex")]
        };

        using var roster = new FleetRosterViewModel(instance.Services, fake, fake.Fleet!, isOwner: true, Owner);
        await WaitLoadedAsync(roster);
        await roster.StopCommand.ExecuteAsync(null);

        var line = Assert.Single(dialogs.FleetExitPrompt!.RunsInProgress);
        Assert.StartsWith("Kaska Vex — Fortress Sansha, 00:11:", line, StringComparison.Ordinal);
    }

    /// <summary>With no character of mine besides the FC there is nothing to leave with, so that exit is not
    /// offered — an option that cannot do anything is worse than an absent one.</summary>
    [AvaloniaFact]
    public async Task StopDialog_DropsTheLeaveExit_WhenNoOwnCharacterCouldLeave()
    {
        var dialogs = new RecordingDialogService { FleetExit = StopFleetChoice.Cancel };
        using var instance = TestClientInstance.Create(s => s.AddSingleton<IDialogService>(dialogs));
        var fake = new FakeFleetClient
        {
            Fleet = FakeFleet(FleetActivation.Active),
            Members = [MemberRow(5, Owner), MemberRow(6, Member)],
            Connected = [new ConnectedCharacterInfo(Owner, "Ravnholt")]
        };

        using var roster = new FleetRosterViewModel(instance.Services, fake, fake.Fleet!, isOwner: true, Owner);
        await WaitLoadedAsync(roster);
        await roster.StopCommand.ExecuteAsync(null);

        Assert.Equal(0, dialogs.FleetExitPrompt!.LeavableCharacterCount);
        Assert.DoesNotContain(
            new StopFleetWindow(dialogs.FleetExitPrompt).Options,
            option => option.Choice == StopFleetChoice.LeaveOnly);
    }

    /// <summary>
    /// The screen the ticket is about: the exits stand side by side, stopping leads and is marked as the reversible
    /// one, concluding is right there in one step rather than hidden behind an expander — and disbanding is nowhere
    /// on it, because deleting the fleet is not the same weight of decision and lives on the overview.
    /// </summary>
    [AvaloniaFact]
    public void StopDialog_ShowsThreeExitsSideBySide_AndNoDisband()
    {
        var window = new StopFleetWindow(new StopFleetPrompt(
            "Wednesday Homefronts", DateTimeOffset.UtcNow.AddMinutes(-86), 3, 0, 2,
            ["Kaska Vex — Fortress Sansha, 00:11:42", "Torv Kesh — Fortress Sansha, 00:11:38"], 3));
        window.Show();
        UiDispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var texts = RenderedText.VisibleTexts(window);
        Assert.Contains(texts, t => t.StartsWith("STOP — back to standing by", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.StartsWith("CONCLUDE — final", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.StartsWith("LEAVE —", StringComparison.Ordinal));
        Assert.Contains("recommended", texts);
        Assert.Contains("cannot be undone", texts);

        // The state block reads real values, not placeholders: 86 minutes ago renders as 01:26:xx.
        Assert.Contains(texts, t => t.StartsWith("01:26:", StringComparison.Ordinal) && t.Contains("· since", StringComparison.Ordinal));
        Assert.Contains("3 of your characters + 2 external", texts);
        Assert.Contains("2 still running", texts);
        Assert.Contains("Kaska Vex — Fortress Sansha, 00:11:42", texts);

        // Not here, on purpose.
        Assert.DoesNotContain(texts, t => t.Contains("isband", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.Contains("elete", StringComparison.Ordinal));

        window.CaptureRenderedFrame()?.Save("/tmp/eveutils-et166-stop-dialog.png",
            new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        window.Close();
    }

    /// <summary>
    /// The confirm button carries the weight of what was chosen: accent while the reversible stop is selected, the
    /// destructive red once the terminal conclude is. Read off the applied classes rather than a Foreground, because
    /// a locally-set Foreground would beat the style setter and freeze the button on whichever ink it got first.
    /// </summary>
    [AvaloniaFact]
    public void StopDialog_ConfirmButton_FollowsTheSelectedExit()
    {
        var window = new StopFleetWindow(new StopFleetPrompt(
            "Wednesday Homefronts", DateTimeOffset.UtcNow.AddMinutes(-5), 1, 0, 0, [], 0));
        window.Show();
        UiDispatcher.UIThread.RunJobs();

        var confirm = window.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Classes.Contains("accent") || b.Classes.Contains("danger"));
        Assert.Equal("STOP FLEET →", confirm.Content);
        Assert.Contains("accent", confirm.Classes);
        Assert.DoesNotContain("danger", confirm.Classes);

        var list = window.GetVisualDescendants().OfType<ListBox>().Single();
        list.SelectedIndex = 1;   // Conclude
        UiDispatcher.UIThread.RunJobs();

        Assert.Equal("CONCLUDE FLEET →", confirm.Content);
        Assert.Contains("danger", confirm.Classes);
        Assert.DoesNotContain("accent", confirm.Classes);

        window.Close();
    }

    /// <summary>The runs block only appears when something is running — an empty "nothing is going on" panel is
    /// noise on a decision screen.</summary>
    [AvaloniaFact]
    public void StopDialog_HidesTheRunsBlock_WhenNothingIsRunning()
    {
        var window = new StopFleetWindow(new StopFleetPrompt(
            "Quiet fleet", DateTimeOffset.UtcNow.AddMinutes(-3), 1, 0, 0, [], 0));
        window.Show();
        UiDispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        window.CaptureRenderedFrame()?.Save("/tmp/eveutils-et166-stop-dialog-quiet.png",
            new Avalonia.Media.Imaging.PngBitmapEncoderOptions());

        var texts = RenderedText.VisibleTexts(window);
        Assert.DoesNotContain(texts, t => t.Contains("still running", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.Contains("keep going", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.Contains("keeps going", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.StartsWith("STOP — back to standing by", StringComparison.Ordinal));

        window.Close();
    }

    /// <summary>ET-185: known combines with running exactly as the mockup draws it — "7 completed · 2 still
    /// running" — because both halves of that line are trusted numbers here.</summary>
    [AvaloniaFact]
    public void StopDialog_ShowsCompletedAndRunning_WhenBothAreKnown()
    {
        var window = new StopFleetWindow(new StopFleetPrompt(
            "Wednesday Homefronts", DateTimeOffset.UtcNow.AddMinutes(-86), 3, 0, 2,
            ["Kaska Vex — Fortress Sansha, 00:11:42", "Torv Kesh — Fortress Sansha, 00:11:38"], 3, CompletedRunCount: 7));
        window.Show();
        UiDispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.Contains(RenderedText.VisibleTexts(window), t => t == "7 completed · 2 still running");

        window.Close();
    }

    /// <summary>A genuine zero — a fleet young enough that RunGroupOrigin could not have missed a run of its — is
    /// shown as the real zero it is, not suppressed the way an unknown count is (ET-185).</summary>
    [AvaloniaFact]
    public void StopDialog_ShowsACompletedZero_WhenThatZeroIsKnownRatherThanGuessed()
    {
        var window = new StopFleetWindow(new StopFleetPrompt(
            "Brand new fleet", DateTimeOffset.UtcNow.AddMinutes(-5), 1, 0, 0, [], 0, CompletedRunCount: 0));
        window.Show();
        UiDispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.Contains(RenderedText.VisibleTexts(window), t => t == "0 completed");

        window.Close();
    }

    /// <summary>Where the count is not known (ET-182's coverage gap), this screen reads exactly as it did before
    /// ET-185: no completed figure at all, just the running count — never a guess and never an "unknown" filler
    /// standing in for the number.</summary>
    [AvaloniaFact]
    public void StopDialog_OmitsCompletedCount_WhenNotKnown()
    {
        var window = new StopFleetWindow(new StopFleetPrompt(
            "Fleet older than tracking", DateTimeOffset.UtcNow.AddMinutes(-30), 1, 0, 0,
            ["Kaska Vex — Fortress Sansha, 00:11:42"], 0, CompletedRunCount: null));
        window.Show();
        UiDispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var texts = RenderedText.VisibleTexts(window);
        Assert.Contains(texts, t => t == "1 still running");
        Assert.DoesNotContain(texts, t => t.Contains("completed", StringComparison.Ordinal));

        window.Close();
    }

    private sealed class FakeDisplay : IModuleHostDisplay
    {
        public bool IsFloating { get; set; }
        public ObservableCollection<HostTab> HostTabs { get; } = new();
        public HostTab? SelectedHostTab { get; set; }
    }

    private static Button? StopButton(Visual root) =>
        root.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.IsEffectivelyVisible && (b.Content as string) == "STOP");

    /// <summary>
    /// STOP has to be there in every shell this app presents a module in: its own floating window, a docked tab
    /// (where <see cref="ModuleHostService"/> lifts the content out of the window entirely), and after switching
    /// from the one to the other — the moment ET-42 was found in, and the reason a header is checked three times
    /// rather than once.
    /// </summary>
    [AvaloniaFact]
    public async Task RosterHeader_ShowsStop_Floating_Docked_AndAfterSwitchingBetweenThem()
    {
        using var instance = TestClientInstance.Create();
        var services = instance.Services;
        var fleetService = services.GetRequiredService<ClientFleetService>();
        var repository = services.GetRequiredService<IFleetRepository>();
        var characters = services.GetRequiredService<ICharacterRegistry>();

        var fleetId = (await fleetService.CreateLocalFleetAsync("Shell test", null, Owner)).Value;
        var fleet = await repository.GetAsync(fleetId);
        var client = new LocalFleetClient(fleetService, repository, characters, Owner);
        using var roster = new FleetRosterViewModel(
            services, client, InfoFor(fleet!, FleetActivation.Active, DateTimeOffset.UtcNow.AddMinutes(-40)),
            isOwner: true, Owner);
        await WaitLoadedAsync(roster);

        // 1 — the module's own window.
        var window = new FleetRosterWindow(roster) { Width = 900, Height = 600 };
        var display = new FakeDisplay { IsFloating = false };
        var host = new ModuleHostService();
        host.SetOwner(new Window());
        host.SetHost(display);
        host.Open(window, "FLEET ROSTER", "fleet", $"fleet-roster:{fleetId}");

        // 2 — docked: the very same content, reparented into a tab. Stood in a plain window so it has somewhere to
        // lay out; the module's own window is deliberately not the host.
        var docked = new Window { Width = 758, Height = 606, Content = Assert.Single(display.HostTabs).Content };
        docked.Show();
        UiDispatcher.UIThread.RunJobs();
        docked.UpdateLayout();

        var dockedStop = StopButton(docked);
        Assert.NotNull(dockedStop);
        Assert.True(dockedStop!.Bounds.Width > 0, "STOP rendered with no width in the docked tab");
        Assert.Contains("accent", dockedStop.Classes);
        Assert.DoesNotContain(RenderedText.VisibleTexts(docked), t => t == "CONCLUDE");
        docked.CaptureRenderedFrame()?.Save("/tmp/eveutils-et166-roster-docked.png",
            new Avalonia.Media.Imaging.PngBitmapEncoderOptions());

        // 3 — the switch back to floating reparents that same content into the module's own window.
        docked.Content = null;
        docked.Close();
        UiDispatcher.UIThread.RunJobs();

        display.IsFloating = true;
        host.SwitchMode();
        UiDispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var floatingStop = StopButton(window);
        Assert.NotNull(floatingStop);
        Assert.True(floatingStop!.Bounds.Width > 0, "STOP rendered with no width after switching to a floating window");
        Assert.Contains("accent", floatingStop.Classes);
        Assert.DoesNotContain(RenderedText.VisibleTexts(window), t => t == "CONCLUDE");
        window.CaptureRenderedFrame()?.Save("/tmp/eveutils-et166-roster-floating.png",
            new Avalonia.Media.Imaging.PngBitmapEncoderOptions());

        window.Close();
    }

    /// <summary>
    /// The reversible exit is preselected and marked as such — measured, not eyeballed: the selected card carries the
    /// accent border, the others the resting one. The XAML cannot do this selection, because the list binds to a
    /// collection the constructor fills afterwards and a SelectedIndex set on an empty list falls back to -1 — which
    /// left the dialog opening with nothing chosen and a confirm button that did nothing at all.
    /// </summary>
    [AvaloniaFact]
    public void StopDialog_OpensOnTheReversibleExit_AndMarksIt()
    {
        var window = new StopFleetWindow(new StopFleetPrompt(
            "Wednesday Homefronts", DateTimeOffset.UtcNow.AddMinutes(-86), 3, 0, 2, [], 3));
        window.Show();
        UiDispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var list = window.GetVisualDescendants().OfType<ListBox>().Single();
        Assert.Equal(0, list.SelectedIndex);
        Assert.Equal(StopFleetChoice.Stop, Assert.IsType<StopFleetOption>(list.SelectedItem).Choice);

        Assert.True(window.TryFindResource("AccentBrush", out var accent));
        var accentColor = Assert.IsAssignableFrom<ISolidColorBrush>(accent).Color;

        var cards = list.GetVisualDescendants().OfType<ListBoxItem>()
            .Select(item => (item.IsSelected, Border: item.GetVisualDescendants().OfType<ContentPresenter>().First().BorderBrush))
            .ToList();
        Assert.Equal(3, cards.Count);
        Assert.Equal(accentColor, Assert.IsAssignableFrom<ISolidColorBrush>(Assert.Single(cards, c => c.IsSelected).Border).Color);
        Assert.All(cards.Where(c => !c.IsSelected),
            c => Assert.NotEqual(accentColor, Assert.IsAssignableFrom<ISolidColorBrush>(c.Border).Color));

        window.Close();
    }

    /// <summary>
    /// The dialog fits at the size it opens at, with the busiest content it can carry: three exits, two runs and a
    /// long fleet name. Measured off the ScrollViewer's own extent rather than guessed from a screenshot — an
    /// overflow of a handful of pixels puts a scrollbar over the right-hand column, which is exactly how the state
    /// block's values first came out clipped.
    /// </summary>
    [AvaloniaFact]
    public void StopDialog_FitsAtItsOpeningSize()
    {
        var window = new StopFleetWindow(new StopFleetPrompt(
            "Wednesday Homefronts", DateTimeOffset.UtcNow.AddMinutes(-86), 3, 1, 2,
            ["Kaska Vex — Fortress Sansha, 00:11:42", "Torv Kesh — Fortress Sansha, 00:11:38"], 3));
        window.Show();
        UiDispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().First();
        Assert.True(scroller.Viewport.Height > 0, "the dialog body never laid out");
        Assert.True(scroller.Extent.Height <= scroller.Viewport.Height,
            $"the body needs {scroller.Extent.Height}px in a {scroller.Viewport.Height}px viewport — it opens scrolled");
        Assert.True(scroller.Extent.Width <= scroller.Viewport.Width,
            $"the body is {scroller.Extent.Width}px wide in a {scroller.Viewport.Width}px viewport");

        window.Close();
    }

    /// <summary>The dialog's own styles sit on its content root, not on the window: the ET-42 rule, checked rather
    /// than trusted, so the exit cards keep their border and fill wherever the content ends up.</summary>
    [AvaloniaFact]
    public void StopDialog_Styles_LiveOnTheContentRoot()
    {
        var window = new StopFleetWindow(new StopFleetPrompt("Style test", null, 1, 0, 0, [], 0));
        Assert.Empty(window.Styles);
        Assert.NotEmpty(Assert.IsType<DockPanel>(window.Content).Styles);
    }
}
