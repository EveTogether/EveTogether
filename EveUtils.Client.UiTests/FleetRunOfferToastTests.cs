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
using EveUtils.Shared.Modules.Runs.Commands;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Settings.Commands;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using IDispatcher = EveUtils.Shared.Cqrs.IDispatcher;
using StoredActivityKind = EveUtils.Shared.Modules.Runs.Enums.ActivityKind;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The fleet commander's start reaches a member as an OFFER, not as a window. Accepting the offer is the whole of
/// joining — the window opens under the commander's group code and that is the participation — so declining must
/// leave nothing behind at all.
///
/// The offer carries the site and the system because the group code is keyed on fleet + activity kind and knows no
/// site: the announcement also reaches members flying somewhere else entirely, and the site name is the only thing
/// that lets such a pilot see this is not their run.
/// </summary>
public sealed class FleetRunOfferToastTests
{
    private const long FleetId = 4242;
    private const string GroupCode = "HF-F0CU";
    private const int Lionear = 100;
    private const int Maricadie = 200;

    // ── The default: an offer ───────────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task CommanderStart_OffersAToastNamingTheSiteAndSystem_AndOpensNothing()
    {
        var (instance, dialogs, toasts, bus, presenter) = _Harness();
        using (instance)
        using (presenter)
        {
            await _CommanderStartsAsync(bus);

            var toast = Assert.Single(toasts.Toasts);
            Assert.Equal("Fleet run started", toast.Title);
            Assert.Equal("Blood Watch · Osmon", toast.Message);
            Assert.Empty(dialogs.ShownActivityWindowTriggers);
        }
    }

    /// <summary>An offer that withdraws itself is an offer the pilot can miss, and missing it is missing the group.
    /// The offer is an ACTION toast, and <c>ToastActionContentTests</c> pins that those never auto-expire — so the
    /// card standing there until it is answered or dismissed is what carrying a button buys.</summary>
    [AvaloniaFact]
    public async Task TheOffer_CarriesOneJoinButton_WhichIsWhatKeepsItOnScreen()
    {
        var (instance, _, toasts, bus, presenter) = _Harness();
        using (instance)
        using (presenter)
        {
            await _CommanderStartsAsync(bus);

            Assert.Equal("Join run", Assert.Single(Assert.Single(toasts.ActionToasts).Actions).Label);
        }
    }

    [AvaloniaFact]
    public async Task AcceptingTheOffer_OpensTheWindowWithoutFocus_UnderTheCommandersGroupCode()
    {
        var (instance, dialogs, toasts, bus, presenter) = _Harness();
        using (instance)
        using (presenter)
        {
            await _CommanderStartsAsync(bus);
            _Accept(toasts);

            Assert.Equal(RunWindowOpenTrigger.RemoteFleetCommander, Assert.Single(dialogs.ShownActivityWindowTriggers));
            var window = Assert.Single(dialogs.ShownActivityWindows);
            Assert.Equal(GroupCode, window.GroupCode);
            Assert.Equal(FleetId, window.FleetId);
        }
    }

    /// <summary>Dismissing is declining: no window, no group code, and above all no half-created run row for a
    /// pilot who never said yes.</summary>
    [AvaloniaFact]
    public async Task LeavingTheOfferAlone_CreatesNoWindowAndNoRun()
    {
        var (instance, dialogs, _, bus, presenter) = _Harness();
        using (instance)
        using (presenter)
        {
            await _CommanderStartsAsync(bus);

            Assert.Empty(dialogs.ShownActivityWindowTriggers);
            using var scope = instance.Services.CreateScope();
            Assert.False((await scope.ServiceProvider.GetRequiredService<IDispatcher>()
                .Query(new GetRunningRunQuery())).IsSuccess);
        }
    }

    // ── Joining while already flying ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The member was already in a run when the offer arrived. Accepting adopts that run rather than opening a
    /// second, and adopting publishes no <c>RunStartedEvent</c> — so without an explicit relink the coordinator
    /// never hears of it and the row stays outside the group. Asserted on the STORE, not on the view-model: the
    /// view-model showing the right code is exactly the failure this guards against.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(null)]        // the member's run belongs to no group yet
    [InlineData("HF-0LD1")]   // …or to an earlier one, which has to be released first
    public async Task AcceptingTheOffer_WhileAlreadyFlying_PutsTheStoredRunInTheCommandersGroup(string? existingGroupCode)
    {
        var (instance, dialogs, toasts, bus, presenter) = _Harness();
        using (instance)
        using (presenter)
        {
            await _StartOwnRunAsync(instance, existingGroupCode);

            await _CommanderStartsAsync(bus);
            _Accept(toasts);
            await Assert.Single(dialogs.ShownActivityWindows).LoadAsync();

            Assert.Equal(GroupCode, await _StoredGroupCodeAsync(instance));
        }
    }

    // ── Who registers it, when that question is real ────────────────────────────────────────────────

    /// <summary>
    /// Two of this pilot's characters have an EVE client up, so "which of them is flying this?" is a real question
    /// and gets asked between accepting and opening. Asserted on the run's stored CharacterId: the answer has to
    /// reach the row the run is filed under, not just the window's caption.
    /// </summary>
    [AvaloniaFact]
    public async Task AcceptingTheOffer_WithTwoPilotsFlying_AsksWhoRegisters_AndFilesTheRunUnderTheAnswer()
    {
        var (instance, dialogs, toasts, bus, presenter) = _Harness(Lionear, Maricadie);
        using (instance)
        using (presenter)
        {
            await _SeedCharactersAsync(instance);
            // Answers the fleet-run question only. START has a picker of its own ("Whose run is this?") and it must
            // never be reached: the answer given here has to carry into the window, not be asked for a second time.
            dialogs.OnPickCharacter = (prompt, _) => prompt == "Who is registering this run?"
                ? Task.FromResult<int?>(Maricadie)
                : throw new InvalidOperationException($"the window asked again: '{prompt}'");

            await _CommanderStartsAsync(bus);
            _Accept(toasts);
            await _SettleAsync(() => dialogs.ShownActivityWindows.Count > 0);

            Assert.Equal("Who is registering this run?", dialogs.LastPrompt);
            Assert.Equal([Lionear, Maricadie], dialogs.LastOptions!.Select(option => option.CharacterId));

            var window = Assert.Single(dialogs.ShownActivityWindows);
            await window.LoadAsync();
            await window.StartRunCommand.ExecuteAsync(null);
            Assert.Equal(Maricadie, await _StoredCharacterIdAsync(instance)); // the PICKED pilot, not the first one
        }
    }

    /// <summary>Dismissing the question is dismissing the offer. Nothing opens and nothing
    /// is created — the same "no half-made run" guarantee the toast itself carries.</summary>
    [AvaloniaFact]
    public async Task DismissingTheQuestion_OpensNothingAndCreatesNoRun()
    {
        var (instance, dialogs, toasts, bus, presenter) = _Harness(Lionear, Maricadie);
        using (instance)
        using (presenter)
        {
            await _SeedCharactersAsync(instance);
            dialogs.OnPickCharacter = (_, _) => Task.FromResult<int?>(null);

            await _CommanderStartsAsync(bus);
            _Accept(toasts);
            await _SettleAsync(() => dialogs.LastPrompt is not null);

            Assert.Empty(dialogs.ShownActivityWindows);
            using var scope = instance.Services.CreateScope();
            Assert.False((await scope.ServiceProvider.GetRequiredService<IDispatcher>()
                .Query(new GetRunningRunQuery())).IsSuccess);
        }
    }

    /// <summary>One client up means nothing to choose between, so no dialog that answers itself. This is also what
    /// a probe that sees less than is really running degrades to — straight through, exactly as before.</summary>
    [AvaloniaTheory]
    [InlineData(Lionear)]   // one of the two characters is flying
    [InlineData(0)]         // …or the probe sees nobody at all
    public async Task AcceptingTheOffer_WithoutARealChoice_AsksNothingAndOpensTheWindow(int flying)
    {
        var (instance, dialogs, toasts, bus, presenter) = _Harness(flying == 0 ? [] : [flying]);
        using (instance)
        using (presenter)
        {
            await _SeedCharactersAsync(instance);

            await _CommanderStartsAsync(bus);
            _Accept(toasts);
            await _SettleAsync(() => dialogs.ShownActivityWindows.Count > 0);

            Assert.Null(dialogs.LastPrompt);
            Assert.Equal(RunWindowOpenTrigger.RemoteFleetCommander, Assert.Single(dialogs.ShownActivityWindowTriggers));
        }
    }

    // ── The offer outliving the run it offers ───────────────────────────────────────────────────────

    [AvaloniaFact]
    public async Task AcceptingAfterTheCommanderEndedTheRun_OpensNothing_AndSaysWhy()
    {
        var (instance, dialogs, toasts, bus, presenter) = _Harness();
        using (instance)
        using (presenter)
        {
            await _CommanderStartsAsync(bus);
            await bus.PublishAsync(new FleetRunDiscardedEvent(
                new RunGroupDiscard(FleetId, StoredActivityKind.Site, GroupCode, DateTime.UtcNow)));

            _Accept(toasts);

            Assert.Empty(dialogs.ShownActivityWindowTriggers);
            Assert.Equal("Fleet run already ended", toasts.Toasts[^1].Title);
        }
    }

    // ── The other setting: the window, straight away, as before ─────────────────────────────────────

    [AvaloniaFact]
    public async Task WithTheWindowSettingOn_TheWindowOpensStraightAway_AndNoOfferIsMade()
    {
        var (instance, dialogs, toasts, bus, presenter) = _Harness();
        using (instance)
        using (presenter)
        {
            await _SetAutoOpenAsync(instance, true);

            await _CommanderStartsAsync(bus);

            Assert.Equal(RunWindowOpenTrigger.RemoteFleetCommander, Assert.Single(dialogs.ShownActivityWindowTriggers));
            Assert.Empty(toasts.Toasts);
        }
    }

    // ── Who is not offered anything ─────────────────────────────────────────────────────────────────

    /// <summary>A member's own start is their own business: it offers nothing on anybody else's screen.</summary>
    [AvaloniaFact]
    public async Task AMembersOwnStart_OffersNothing()
    {
        var (instance, dialogs, toasts, bus, presenter) = _Harness();
        using (instance)
        using (presenter)
        {
            await bus.PublishAsync(new FleetRunGroupCodeEvent(_Start(isFleetCommander: false)));
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(toasts.Toasts);
            Assert.Empty(dialogs.ShownActivityWindowTriggers);
        }
    }

    /// <summary>The commander's own client receives its own announcement. Its window is already up, so there is
    /// nothing to offer — the same "leave it alone" answer the window path has always given.</summary>
    [AvaloniaFact]
    public async Task WithTheActivityWindowAlreadyUp_NothingIsOffered()
    {
        var (instance, dialogs, toasts, bus, presenter) = _Harness();
        using (instance)
        using (presenter)
        {
            dialogs.IsActivityWindowOpen = true;

            await _CommanderStartsAsync(bus);

            Assert.Empty(toasts.Toasts);
            Assert.Empty(dialogs.ShownActivityWindowTriggers);
        }
    }

    // ── What joining actually joins ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The window a member joins on shows the commander's run, not an empty one wearing his group code. It used to
    /// carry the group code and the fleet id and nothing else, so a member who joined a run that had been going for
    /// minutes sat at NOT STARTED, ELAPSED --:--, "no signature" (Raymond, 2026-09-03) — and with no run row of his
    /// own there was nowhere for his loot to go either.
    /// </summary>
    [AvaloniaFact]
    public async Task AcceptingTheOffer_TakesOverTheRunTheCommanderIsFlying()
    {
        var (instance, dialogs, toasts, bus, presenter) = _Harness(Lionear);
        using (instance)
        using (presenter)
        {
            await _SeedCharactersAsync(instance);
            DateTime startedAt = DateTime.UtcNow.AddMinutes(-4);

            await bus.PublishAsync(new FleetRunGroupCodeEvent(_Start(startedAt: startedAt)));
            Dispatcher.UIThread.RunJobs();
            _Accept(toasts);
            await _SettleAsync(() => dialogs.ShownActivityWindows.Count > 0);

            var window = Assert.Single(dialogs.ShownActivityWindows);
            Assert.Equal(ActivityRunState.Running, window.RunState);
            Assert.Equal(startedAt, window.AnchorUtc);
            Assert.Equal("Blood Watch", window.SignatureName);

            // The stored row, not only the readout: the loot and the bounties hang off that row.
            await _SettleAsync(() => window.RunId is not null);
            Assert.NotNull(window.RunId);
            Assert.Equal(GroupCode, await _StoredGroupCodeAsync(instance));
        }
    }

    /// <summary>
    /// The two halves of "er lijkt compleet geen communicatie te zijn" (Raymond, 2026-09-03), one row each.
    ///
    /// Receiving: a member whose window is already open hears nothing from the presenter — that only ever opens
    /// windows — so what the commander does next has to reach the window itself, and none of the three did. START
    /// was dropped once a window was up, STOP had no event at all, and DISCARD crossed to a client where nothing
    /// was listening. Driven over the bus, because the wiring is what is under test.
    ///
    /// Sending: the commander's own window did not know the group code of the run it had just started — the command
    /// mints it and hands back only the run id — so STOP had nothing to announce and DISCARD, which announces only
    /// when it has a code, reached nobody at all. Driven from the buttons and read off the bus.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("member-start")]
    [InlineData("member-stop")]
    [InlineData("member-discard")]
    [InlineData("commander-stop")]
    [InlineData("commander-discard")]
    public async Task TheCommandersRunAndAMembersOpenWindow_AreConnectedBothWays(string row)
    {
        var (instance, dialogs, _, bus, presenter) = _Harness(Lionear);
        using (instance)
        using (presenter)
        {
            await _SeedCharactersAsync(instance);
            DateTime startedAt = DateTime.UtcNow.AddMinutes(-4);
            DateTime endedAt = startedAt.AddMinutes(3);

            if (row.StartsWith("commander", StringComparison.Ordinal))
            {
                List<string> announced = [];
                using var stopped = bus.Subscribe<FleetRunStoppedEvent>(e => announced.Add("stop:" + e.Data.GroupCode));
                using var discarded = bus.Subscribe<FleetRunDiscardedEvent>(e => announced.Add("discard:" + e.Data.GroupCode));
                instance.Services.GetRequiredService<IFleetParticipation>()
                    .Set([new FleetParticipant(Lionear, FleetId, ClientOnly: true, Lionear)]);

                using var commander = new ActivityWindowViewModel(ActivityKind.Site, instance.Services);
                await commander.LoadAsync();
                commander.SignatureName = "Blood Watch";
                await commander.StartRunCommand.ExecuteAsync(null);

                // The code the store minted, back on the window that started it: everything below hangs off it.
                Assert.NotNull(commander.GroupCode);

                if (row == "commander-stop")
                {
                    commander.StopRunCommand.Execute(null);
                    await _SettleAsync(() => announced.Count > 0);
                    Assert.Equal([$"stop:{commander.GroupCode}"], announced);
                    return;
                }

                dialogs.OnConfirm = (_, _) => Task.FromResult(true);
                string code = commander.GroupCode!;
                await commander.DiscardRunCommand.ExecuteAsync(null);
                await _SettleAsync(() => announced.Count > 0);
                Assert.Equal([$"discard:{code}"], announced);
                return;
            }

            using var window = new ActivityWindowViewModel(ActivityKind.Site, instance.Services);
            await window.LoadAsync();

            await bus.PublishAsync(new FleetRunGroupCodeEvent(_Start(startedAt: startedAt)));
            await _SettleAsync(() => window.RunState == ActivityRunState.Running);

            if (row == "member-start")
            {
                Assert.Equal(ActivityRunState.Running, window.RunState);
                Assert.Equal(startedAt, window.AnchorUtc);
                Assert.Equal(GroupCode, window.GroupCode);
                return;
            }

            if (row == "member-stop")
            {
                await bus.PublishAsync(new FleetRunStoppedEvent(
                    new RunGroupStop(FleetId, StoredActivityKind.Site, GroupCode, endedAt)));
                await _SettleAsync(() => window.RunState == ActivityRunState.Stopped);

                Assert.Equal(ActivityRunState.Stopped, window.RunState);
                Assert.Equal(endedAt, window.StoppedAtUtc);
                return;
            }

            await bus.PublishAsync(new FleetRunDiscardedEvent(
                new RunGroupDiscard(FleetId, StoredActivityKind.Site, GroupCode, endedAt)));
            await _SettleAsync(() => window.GroupCode is null);

            Assert.Equal(ActivityRunState.NotStarted, window.RunState);
            Assert.Null(window.GroupCode);
        }
    }

    // ── Harness ─────────────────────────────────────────────────────────────────────────────────────

    private static RunGroupCodeStart _Start(bool isFleetCommander = true, DateTime? startedAt = null) => new(
        FleetId, StoredActivityKind.Site, GroupCode, startedAt ?? DateTime.UtcNow, isFleetCommander,
        SiteName: "Blood Watch", SolarSystemName: "Osmon");

    private static async Task _CommanderStartsAsync(IEventBus bus)
    {
        await bus.PublishAsync(new FleetRunGroupCodeEvent(_Start()));
        Dispatcher.UIThread.RunJobs();
    }

    private static void _Accept(RecordingToastService toasts)
    {
        Assert.Single(toasts.ActionToasts).Actions.Single(action => action.Label == "Join run").Run();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A run of this member's own, already on the clock when the commander's offer arrives.</summary>
    private static async Task _StartOwnRunAsync(TestClientInstance instance, string? groupCode)
    {
        using var scope = instance.Services.CreateScope();
        Assert.True((await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(new StartRunCommand(
            CharacterId: 7, StoredActivityKind.Site, DateTime.UtcNow.AddMinutes(-5),
            SiteTypeId: 0, SiteName: "Rogue Drone Asteroid Infestation", SolarSystemId: null,
            GroupCode: groupCode))).IsSuccess);
    }

    private static async Task<string?> _StoredGroupCodeAsync(TestClientInstance instance)
    {
        using var scope = instance.Services.CreateScope();
        var running = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new GetRunningRunQuery());
        Assert.True(running.IsSuccess);
        return running.Value!.GroupCode;
    }

    private static async Task _SetAutoOpenAsync(TestClientInstance instance, bool on)
    {
        using var scope = instance.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IDispatcher>().Send(
            new SetSettingCommand(FleetRunWindowPresenter.AutoOpenSettingKey, on ? "true" : "false"));
    }

    private static async Task _SeedCharactersAsync(TestClientInstance instance)
    {
        var registry = instance.Services.GetRequiredService<ICharacterRegistry>();
        await registry.AddOrUpdateAsync(new Character("Lionear", Lionear));
        await registry.AddOrUpdateAsync(new Character("Maricadie", Maricadie));
    }

    private static async Task<long> _StoredCharacterIdAsync(TestClientInstance instance)
    {
        using var scope = instance.Services.CreateScope();
        var running = await scope.ServiceProvider.GetRequiredService<IDispatcher>().Query(new GetRunningRunQuery());
        Assert.True(running.IsSuccess);
        return running.Value!.CharacterId;
    }

    /// <summary>Accepting runs the pick + open off a toast button, so it cannot be awaited from here.</summary>
    private static async Task _SettleAsync(Func<bool> until)
    {
        for (var attempt = 0; attempt < 100 && !until(); attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static (TestClientInstance Instance, RecordingDialogService Dialogs, RecordingToastService Toasts,
        IEventBus Bus, FleetRunWindowPresenter Presenter) _Harness(params int[] flying)
    {
        var dialogs = new RecordingDialogService();
        var toasts = new RecordingToastService();
        var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IDialogService>(dialogs);
            services.AddSingleton<IToastService>(toasts);
            services.AddSingleton<ILocalCharacterPresence>(new FlyingPilots(flying));
        });
        var bus = instance.Services.GetRequiredService<IEventBus>();
        return (instance, dialogs, toasts, bus, new FleetRunWindowPresenter(bus, dialogs, instance.Services));
    }

    /// <summary>The one fact a headless run cannot observe: which characters have an EVE client up.</summary>
    private sealed class FlyingPilots(IReadOnlyCollection<int> inGame) : ILocalCharacterPresence
    {
        public bool? IsInGame(int characterId, string? characterName) => inGame.Contains(characterId);
        public bool? IsInGame(int characterId) => IsInGame(characterId, null);
        public IDisposable Subscribe(Action handler) => new Unsubscribed();

        private sealed class Unsubscribed : IDisposable
        {
            public void Dispose() { }
        }
    }
}
