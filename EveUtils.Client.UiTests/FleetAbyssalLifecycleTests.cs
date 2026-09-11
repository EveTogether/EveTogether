using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Notifications;
using EveUtils.Client.Platform;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Runs.Queries;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using IDispatcher = EveUtils.Shared.Cqrs.IDispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>
/// One fleet abyssal from end to end (ET-246, ET-243): the commander prepares it and the members are offered it before
/// anybody is in; each pilot's clock starts on their own way in and stops on their own way out; the activity runs from
/// the first pilot in to the last one out, and the commander's STOP ends only the commander's own leg.
/// </summary>
public sealed class FleetAbyssalLifecycleTests
{
    private const long FleetId = 4242;
    private const string GroupCode = "AB-F1ER";
    private const int Pilot = 100;
    private const int Commander = 9001;
    private const int Fierce = 3;

    // ── ET-243: a member's clock is theirs ──────────────────────────────────────────────────────────

    /// <summary>The report itself (Jithran, 2026-09-11): the commander left the pocket and stopped, and the member
    /// still inside was stopped with him — clock and stored row both, at the commander's moment.</summary>
    [AvaloniaFact]
    public async Task AMemberStillInThePocket_KeepsRunning_WhenTheCommanderStops()
    {
        var (instance, _, _, bus, presenter) = _Harness(commander: Commander);
        using (instance)
        using (presenter)
        {
            using var window = await _JoinedAndInAsync(instance);

            await bus.PublishAsync(new FleetRunStoppedEvent(
                new RunGroupStop(FleetId, ActivityKind.Abyssal, GroupCode, DateTime.UtcNow)), EventTarget.Local);
            await _SettleAsync(() => false, attempts: 10);

            Assert.Equal(ActivityRunState.Running, window.RunState);
            Assert.Null(window.StoppedAtUtc);
            using var scope = instance.Services.CreateScope();
            Result<RunningRunDto> stored = await scope.ServiceProvider.GetRequiredService<IDispatcher>()
                .Query(new GetRunningRunQuery(Pilot));
            Assert.True(stored.IsSuccess, "the member's stored run was stopped at the commander's moment");
            Assert.Equal(window.RunId, stored.Value!.Id);
        }
    }

    /// <summary>The commander's own STOP is his own way out: it goes to the fleet as that and nothing more. The old
    /// <c>fleet.run-stopped</c> is exactly what an older member's client still obeys, so it must not be sent at all.</summary>
    [AvaloniaFact]
    public async Task TheCommandersStop_IsAnnouncedAsHisOwnWayOut_NotAsAStopForEverybody()
    {
        var (instance, _, _, bus, presenter) = _Harness(commander: Pilot);
        using (instance)
        using (presenter)
        {
            List<string> announced = [];
            using var stops = bus.Subscribe<FleetRunStoppedEvent>(_ => announced.Add("run-stopped"));
            using var legs = bus.Subscribe<FleetRunPilotStoppedEvent>(e => announced.Add($"pilot-stopped:{e.CharacterId}"));
            using var window = new ActivityWindowViewModel(ActivityKind.Abyssal, instance.Services);
            await window.LoadAsync();
            await window.StartRunCommand.ExecuteAsync(null);
            Assert.NotNull(window.GroupCode);

            window.StopRunCommand.Execute(null);
            await _SettleAsync(() => announced.Count > 0);

            Assert.Equal([$"pilot-stopped:{Pilot}"], announced);
        }
    }

    // ── ET-250: a stopped leg picked back up is announced too ───────────────────────────────────────

    /// <summary>Acceptance 1 (ET-250): a pilot presses STOP and then START again in the same leg. The restart is
    /// announced too, in its own shape rather than a repeat of fleet.run-group (which an older client, and
    /// FleetRunGroupCodeCoordinator on this one, would misread as a fresh start) or of pilot-stopped (read the
    /// other way around).</summary>
    [AvaloniaFact]
    public async Task StoppingAndStartingTheSameLegAgain_IsAnnouncedAsAResume()
    {
        var (instance, _, _, bus, presenter) = _Harness(commander: Pilot);
        using (instance)
        using (presenter)
        {
            List<string> announced = [];
            using var stops = bus.Subscribe<FleetRunPilotStoppedEvent>(e => announced.Add($"pilot-stopped:{e.CharacterId}"));
            using var resumes = bus.Subscribe<FleetRunPilotResumedEvent>(e => announced.Add($"pilot-resumed:{e.CharacterId}"));
            using var window = new ActivityWindowViewModel(ActivityKind.Abyssal, instance.Services);
            await window.LoadAsync();
            await window.StartRunCommand.ExecuteAsync(null);

            window.StopRunCommand.Execute(null);
            await _SettleAsync(() => announced.Count > 0);
            await window.StartRunCommand.ExecuteAsync(null);
            await _SettleAsync(() => announced.Count > 1);

            Assert.Equal([$"pilot-stopped:{Pilot}", $"pilot-resumed:{Pilot}"], announced);
            Assert.Equal(ActivityRunState.Running, window.RunState);
        }
    }

    /// <summary>Acceptance 1 (ET-250), from the other side: another pilot's window reads the resumed leg as "in"
    /// again within seconds, not only once the 20-minute cut-off would have retired it.</summary>
    [AvaloniaFact]
    public async Task AResumedLeg_ReadsAsInAgain_OnAnotherPilotsFleetClock()
    {
        var (instance, dialogs, _, bus, presenter) = _Harness(commander: Commander);
        using (instance)
        using (presenter)
        {
            DateTime firstIn = DateTime.UtcNow.AddMinutes(-9);
            using var window = await _ArmedAsync(instance);
            dialogs.IsActivityWindowOpen = true;
            // This pilot's own leg stays in throughout, so the commander coming out does not read as the run having
            // ended without this pilot (_EndedWithoutThisPilot) — the point under test is the commander's own leg.
            window.StartManualRun(firstIn.AddMinutes(1));

            await bus.PublishAsync(new FleetRunGroupCodeEvent(_Start(firstIn), Commander));
            await bus.PublishAsync(new FleetRunPilotStoppedEvent(
                new RunGroupStop(FleetId, ActivityKind.Abyssal, GroupCode, firstIn.AddMinutes(5)), Commander));
            window.Refresh(firstIn.AddMinutes(6));

            Assert.EndsWith("1 pilot in", window.FleetClockText);

            await bus.PublishAsync(new FleetRunPilotResumedEvent(
                new RunGroupResume(FleetId, ActivityKind.Abyssal, GroupCode, firstIn.AddMinutes(7)), Commander));
            window.Refresh(firstIn.AddMinutes(8));

            Assert.EndsWith("2 pilots in", window.FleetClockText);
        }
    }

    // ── ET-246: joining arms, the pilot's own way in starts ──────────────────────────────────────────

    /// <summary>The commander is already in. Accepting his offer puts nothing on this pilot's clock and makes no row
    /// yet: it arms the window, on his tier and weather, for this pilot's own way in.</summary>
    [AvaloniaFact]
    public async Task AcceptingTheCommandersAbyssal_ArmsTheWindow_AndStartsNothing()
    {
        var (instance, dialogs, toasts, bus, presenter) = _Harness(commander: Commander);
        using (instance)
        using (presenter)
        {
            await bus.PublishAsync(new FleetRunGroupCodeEvent(_Start(DateTime.UtcNow.AddMinutes(-4)), Commander));
            Dispatcher.UIThread.RunJobs();
            Assert.Single(toasts.ActionToasts).Actions.Single(action => action.Label == "Join run").Run();
            await _SettleAsync(() => dialogs.ShownActivityWindows.Count > 0);

            var window = Assert.Single(dialogs.ShownActivityWindows);
            await window.LoadAsync();
            await _SettleAsync(() => false, attempts: 10);

            Assert.Equal(ActivityRunState.NotStarted, window.RunState);
            Assert.Null(window.AnchorUtc);
            Assert.Null(window.RunId);
            Assert.Equal(GroupCode, window.GroupCode);
            Assert.Equal(Fierce, window.TierIndex);
            Assert.True(window.IsArmedShown);
            Assert.True(window.IsStartButtonVisible);
        }
    }

    // ── ET-246: the offer before anybody is in ───────────────────────────────────────────────────────

    /// <summary>The commander's side: an abyssal window in his fleet with the tier and weather known is offered to the
    /// fleet straight away — no run, no row — and closing it before anybody went in calls it off under the same code.
    /// </summary>
    [AvaloniaFact]
    public async Task APreparedAbyssal_IsOfferedBeforeAnyoneIsIn_AndClosingItCallsItOff()
    {
        var (instance, _, _, bus, presenter) = _Harness(commander: Pilot);
        using (instance)
        using (presenter)
        {
            List<RunGroupCodeStart> prepared = [];
            List<RunGroupDiscard> calledOff = [];
            using var preparedSubscription = bus.Subscribe<FleetRunGroupPreparedEvent>(e => prepared.Add(e.Data));
            using var discardSubscription = bus.Subscribe<FleetRunDiscardedEvent>(e => calledOff.Add(e.Data));
            using var window = new ActivityWindowViewModel(ActivityKind.Abyssal, instance.Services);
            await window.LoadAsync();

            window.TierIndex = Fierce;
            window.WeatherIndex = AbyssalWeather.IndexOf("Dark");
            await window.RefreshFleetCommandAsync(DateTime.UtcNow);
            await _SettleAsync(() => prepared.Count > 0);

            RunGroupCodeStart offer = Assert.Single(prepared);
            Assert.Equal((Fierce, "Dark"), (offer.AbyssalTierIndex, offer.AbyssalWeatherName));
            Assert.Equal(window.GroupCode, offer.GroupCode);
            Assert.Equal(ActivityRunState.NotStarted, window.RunState);
            Assert.Null(window.RunId);

            Assert.True(await window.RequestCloseAsync());
            await _SettleAsync(() => calledOff.Count > 0);

            Assert.Equal(offer.GroupCode, Assert.Single(calledOff).GroupCode);
        }
    }

    /// <summary>The member's side: the prepared offer names the filament and says it starts on the way in; the real
    /// start replaces that same card; and a prepared offer the commander calls off is taken down.</summary>
    [AvaloniaFact]
    public async Task APreparedOffer_NamesTheFilament_AndComesDownWhenCalledOff()
    {
        var (instance, _, toasts, bus, presenter) = _Harness(commander: Commander);
        using (instance)
        using (presenter)
        {
            await bus.PublishAsync(new FleetRunGroupPreparedEvent(_Start(DateTime.UtcNow), Commander));
            Dispatcher.UIThread.RunJobs();

            var card = Assert.Single(toasts.ActionToasts);
            Assert.Equal("Fleet run prepared", card.Title);
            Assert.Equal("Fierce Dark · Osmon — your run starts when you jump in", card.Message);

            await bus.PublishAsync(new FleetRunDiscardedEvent(
                new RunGroupDiscard(FleetId, ActivityKind.Abyssal, GroupCode, DateTime.UtcNow)));

            Assert.Equal(card.ReplacementKey, Assert.Single(toasts.Dismissed));
        }
    }

    [AvaloniaFact]
    public async Task TheRealStart_ReplacesThePreparedCard_RatherThanStandingBesideIt()
    {
        var (instance, _, toasts, bus, presenter) = _Harness(commander: Commander);
        using (instance)
        using (presenter)
        {
            await bus.PublishAsync(new FleetRunGroupPreparedEvent(_Start(DateTime.UtcNow), Commander));
            await bus.PublishAsync(new FleetRunGroupCodeEvent(_Start(DateTime.UtcNow), Commander));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(["Fleet run prepared", "Fleet run started"], toasts.ActionToasts.Select(toast => toast.Title));
            Assert.Single(toasts.ActionToasts.Select(toast => toast.ReplacementKey).Distinct());

            // Started now, so a call-off no longer takes it down: accepting says it ended (ET-105's rule).
            await bus.PublishAsync(new FleetRunDiscardedEvent(
                new RunGroupDiscard(FleetId, ActivityKind.Abyssal, GroupCode, DateTime.UtcNow)));
            Assert.Empty(toasts.Dismissed);
        }
    }

    // ── ET-243: the fleet's clock ────────────────────────────────────────────────────────────────────

    /// <summary>First pilot in to last one out, from every pilot's own announcements — this pilot out first reads as
    /// waiting on the one still in, and the activity ends only when that one is out too.</summary>
    [AvaloniaFact]
    public async Task TheFleetClock_RunsFromTheFirstPilotInToTheLastOneOut()
    {
        var (instance, dialogs, _, bus, presenter) = _Harness(commander: Commander);
        using (instance)
        using (presenter)
        {
            DateTime firstIn = DateTime.UtcNow.AddMinutes(-9);
            using var window = await _ArmedAsync(instance);
            dialogs.IsActivityWindowOpen = true;

            await bus.PublishAsync(new FleetRunGroupCodeEvent(_Start(firstIn), Commander));
            window.StartManualRun(firstIn.AddMinutes(2));
            window.StopRun(firstIn.AddMinutes(5));
            window.Refresh(firstIn.AddMinutes(6));

            Assert.True(window.HasFleetClock);
            Assert.True(window.IsWaitingForFleet);
            Assert.StartsWith($"first in {firstIn.ToLocalTime():HH:mm:ss} · 06:00 so far", window.FleetClockText);
            Assert.EndsWith("waiting for 1 pilot", window.FleetClockText);

            await bus.PublishAsync(new FleetRunPilotStoppedEvent(
                new RunGroupStop(FleetId, ActivityKind.Abyssal, GroupCode, firstIn.AddMinutes(8)), Commander));
            window.Refresh(firstIn.AddMinutes(9));

            Assert.False(window.IsWaitingForFleet);
            Assert.Equal($"first in {firstIn.ToLocalTime():HH:mm:ss} · last out "
                         + $"{firstIn.AddMinutes(8).ToLocalTime():HH:mm:ss} · 08:00", window.FleetClockText);
        }
    }

    /// <summary>Everyone who went in is out and this pilot never went in: the window stops being armed on a run that
    /// is over, and says so, instead of waiting for a way in that would no longer belong to it.</summary>
    [AvaloniaFact]
    public async Task AnArmedPilotWhoNeverWentIn_IsToldTheRunEndedWithoutThem()
    {
        var (instance, dialogs, _, bus, presenter) = _Harness(commander: Commander);
        using (instance)
        using (presenter)
        {
            DateTime firstIn = DateTime.UtcNow.AddMinutes(-12);
            using var window = await _ArmedAsync(instance);
            dialogs.IsActivityWindowOpen = true;

            await bus.PublishAsync(new FleetRunGroupCodeEvent(_Start(firstIn), Commander));
            await bus.PublishAsync(new FleetRunPilotStoppedEvent(
                new RunGroupStop(FleetId, ActivityKind.Abyssal, GroupCode, firstIn.AddMinutes(11)), Commander));
            window.Refresh(DateTime.UtcNow);

            Assert.Null(window.GroupCode);
            Assert.True(window.HasRunNotice);
            Assert.Contains("ended without you", window.RunNoticeText, StringComparison.Ordinal);
            Assert.Null(window.RunId);
        }
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────────

    private static RunGroupCodeStart _Start(DateTime startedAtUtc) => new(
        FleetId, ActivityKind.Abyssal, GroupCode, startedAtUtc, IsFleetCommander: true,
        SolarSystemName: "Osmon", AbyssalTierIndex: Fierce, AbyssalWeatherName: "Dark");

    /// <summary>A member's window on the commander's run, armed and not yet in.</summary>
    private static async Task<ActivityWindowViewModel> _ArmedAsync(TestClientInstance instance)
    {
        var window = new ActivityWindowViewModel(ActivityKind.Abyssal, instance.Services);
        await window.LoadAsync();
        window.JoinFleetRun(_Start(DateTime.UtcNow));
        return window;
    }

    /// <summary>…and then in, on this pilot's own START, with a stored row of their own.</summary>
    private static async Task<ActivityWindowViewModel> _JoinedAndInAsync(TestClientInstance instance)
    {
        ActivityWindowViewModel window = await _ArmedAsync(instance);
        await window.StartRunCommand.ExecuteAsync(null);
        await _SettleAsync(() => window.RunId is not null);
        Assert.Equal(ActivityRunState.Running, window.RunState);
        Assert.Equal(GroupCode, window.GroupCode);
        return window;
    }

    private static async Task _SettleAsync(Func<bool> until, int attempts = 100)
    {
        for (var attempt = 0; attempt < attempts && !until(); attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>One pilot of this client's own, flying, in <see cref="FleetId"/> under <paramref name="commander"/> —
    /// this pilot themselves when the test is the commander's side. The legs book is up first, as the app has it.</summary>
    private static (TestClientInstance Instance, RecordingDialogService Dialogs, RecordingToastService Toasts,
        IEventBus Bus, FleetRunWindowPresenter Presenter) _Harness(int commander)
    {
        var dialogs = new RecordingDialogService();
        var toasts = new RecordingToastService();
        var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IDialogService>(dialogs);
            services.AddSingleton<IToastService>(toasts);
            services.AddSingleton<ILocalCharacterPresence>(new OnePilotFlying());
        });
        instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Lionear", Pilot))
            .GetAwaiter().GetResult();
        instance.Services.GetRequiredService<IFleetParticipation>()
            .Set([new FleetParticipant(Pilot, FleetId, ClientOnly: true, commander)]);
        _ = instance.Services.GetRequiredService<FleetRunLegs>();
        var bus = instance.Services.GetRequiredService<IEventBus>();
        return (instance, dialogs, toasts, bus, new FleetRunWindowPresenter(bus, dialogs, instance.Services));
    }

    private sealed class OnePilotFlying : ILocalCharacterPresence
    {
        public bool? IsInGame(int characterId, string? characterName) => characterId == Pilot;
        public bool? IsInGame(int characterId) => IsInGame(characterId, null);
        public IDisposable Subscribe(Action handler) => new Unsubscribed();

        private sealed class Unsubscribed : IDisposable
        {
            public void Dispose() { }
        }
    }
}
