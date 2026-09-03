using Avalonia.Headless.XUnit;
using EveUtils.Client.Esi;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Data;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Runs.Control;
using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-105 AC-4, with Raymond's ruling of 2026-09-01: the right to start, stop and discard hangs on the
/// <b>current ESI fleet boss</b>, not on whoever started the run. A handover mid-run moves the controls with it.
/// Where that is decided is one place — <see cref="RunControlAuthority"/> — so the answer can be changed in one
/// edit rather than unpicked from the controls.
/// </summary>
public sealed class HomefrontCommandAuthorityTests
{
    private const long FleetId = 4242;
    private const int Commander = 90000001;
    private const int Member = 90000002;

    /// <summary>
    /// How much this client has been told about the fleet it is in. The three are a ladder, and ET-152 is the gap
    /// between the bottom two: the run window used to know its fleet only at the top rung.
    /// </summary>
    public enum FleetKnowledge
    {
        /// <summary>Nothing was opened at all. The fleet exists in the local repository and the startup sweep is
        /// the only thing that has looked at it — a pilot who launched the client while already in a fleet.</summary>
        SweptOnStartup,

        /// <summary>The membership set is filled, but no fleet row was ever selected: the fleets window loaded and
        /// nothing was clicked in it.</summary>
        MembershipOnly,

        /// <summary>OPEN METRICS was pressed, so <c>IActiveFleetState.Enter</c> ran too. The only state in which the
        /// run window used to know its fleet at all.</summary>
        MetricsOpened
    }

    // ── The rule ────────────────────────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void TheFleetBoss_MayCommandTheRun() =>
        Assert.True(RunControlAuthority.From(FleetId, Commander, Commander, groupCode: null).CanControl);

    [AvaloniaFact]
    public void AMemberWhoIsNotTheFleetBoss_MayNot()
    {
        RunControlAuthority authority = RunControlAuthority.From(FleetId, Commander, Member, groupCode: null);

        Assert.False(authority.CanControl);
        Assert.False(authority.IsUnknown);          // a plain no, not a shrug
        Assert.Equal(RunControlAuthorityLevel.Denied, authority.Level);
    }

    /// <summary>
    /// ESI lags and drops out. Not knowing who commands must not read as "everybody may": discard reaches four other
    /// machines. It must not be silent either — the window says why the controls are gone (ET-65 AC-7's rule).
    /// </summary>
    [AvaloniaFact]
    public void WithNoKnownFleetBoss_NobodyCommandsAndTheWindowSaysSo()
    {
        RunControlAuthority authority =
            RunControlAuthority.From(FleetId, fleetBossCharacterId: null, Member, groupCode: null);

        Assert.False(authority.CanControl);
        Assert.True(authority.IsUnknown);
        Assert.Contains("not known", authority.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(string.Empty, authority.StatusText.Trim());
    }

    /// <summary>A pilot soloing owns their own run: there is no commander to be and no other machine to reach.
    /// Solo is the run carrying no group code, not merely the client having no fleet id to hand (ET-135).</summary>
    [AvaloniaFact]
    public void ASoloRun_IsAlwaysTheOwnPilotsToCommand() =>
        Assert.True(RunControlAuthority
            .From(fleetId: null, fleetBossCharacterId: null, Member, groupCode: null).CanControl);

    /// <summary>
    /// ET-135. The run carries a group code, so it is the commander's and a discard of it reaches every other
    /// member — but this client has no fleet id, so there is no fleet whose boss ESI could be asked about. That is
    /// not knowing, and not knowing is not "yes": the pilot who never opened the fleets window used to be handed
    /// the whole set of buttons here, DISCARD included.
    /// </summary>
    [AvaloniaFact]
    public void ASharedRunWithNoFleetId_IsNobodysToCommandAndSaysSo()
    {
        RunControlAuthority authority =
            RunControlAuthority.From(fleetId: null, fleetBossCharacterId: null, Member, groupCode: "HF-7Q2");

        Assert.False(authority.CanControl);
        Assert.True(authority.IsUnknown);
        Assert.Contains("not known", authority.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The plain no survives the change: a member of a shared run whose fleet boss IS known is denied,
    /// not shrugged at, and is told who to ask.</summary>
    [AvaloniaFact]
    public void AMemberOfASharedRunWithAKnownBoss_IsStillPlainlyDenied()
    {
        RunControlAuthority authority = RunControlAuthority.From(FleetId, Commander, Member, groupCode: "HF-7Q2");

        Assert.Equal(RunControlAuthorityLevel.Denied, authority.Level);
        Assert.False(authority.IsUnknown);
        Assert.Contains("fleet commander", authority.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The counter-proof for the handover: the same pilot who could not command before may after the boss moves to
    /// them, and the old commander may not any more. Nothing is captured at start.
    /// </summary>
    [AvaloniaFact]
    public void WhenTheFleetBossChanges_TheRightMovesWithIt()
    {
        Assert.False(RunControlAuthority.From(FleetId, Commander, Member, groupCode: null).CanControl);
        Assert.True(RunControlAuthority.From(FleetId, Commander, Commander, groupCode: null).CanControl);

        // The boss hands over to Member mid-run.
        Assert.True(RunControlAuthority.From(FleetId, Member, Member, groupCode: null).CanControl);
        Assert.False(RunControlAuthority.From(FleetId, Member, Commander, groupCode: null).CanControl);
    }

    // ── What the window does with it, driven from the source the application uses ───────────────────
    //
    // None of these calls ApplyFleetCommand. They set what ESI answers and which fleet this client is in, then let
    // the window's own tick work it out — take the wiring out and they go red. Calling the method directly is what
    // left the gap of 2026-09-01 standing for a day: green tests over a boss id nothing in the app ever supplied.

    /// <summary>A member who is not the FC sees no start, stop or discard button, and is told why.</summary>
    [AvaloniaFact]
    public void AMemberWhoIsNotTheFc_GetsNoRunControls()
    {
        using ClientInFleet client = ClientInFleet.As(Member, bossCharacterId: Commander);
        var window = new ActivityWindowViewModel(ActivityKind.Site, client.Services);

        window.StartManualRun(DateTime.UtcNow);

        Assert.False(window.IsStartButtonVisible);
        Assert.False(window.IsStopButtonVisible);
        Assert.False(window.IsDiscardButtonVisible);
        Assert.True(window.IsCommandStatusShown);
        Assert.Contains("fleet commander", window.CommandStatusText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The FC flies one toon while the fleets window's row was last selected as another. The controls belong to the
    /// character he is actually in the fleet as, not to whichever one that row happens to act for — asking
    /// IActiveFleetState instead told a real fleet commander that only the FC may start or stop (Jithran,
    /// 2026-09-02).
    /// </summary>
    [AvaloniaFact]
    public void TheFc_GetsTheRunControls_EvenWhenTheFleetRowActsForAnotherOfHisToons()
    {
        using ClientInFleet client = ClientInFleet.As(Member, bossCharacterId: Commander, flyingAs: Commander);
        var window = new ActivityWindowViewModel(ActivityKind.Site, client.Services);

        window.StartManualRun(DateTime.UtcNow);

        Assert.True(window.IsStopButtonVisible);
        Assert.True(window.IsDiscardButtonVisible);
        Assert.False(window.IsCommandStatusShown);
    }

    /// <summary>The FC does get them — so the assertion above is pinning down a choice, not a window with no
    /// buttons at all.</summary>
    [AvaloniaFact]
    public void TheFc_GetsTheRunControls()
    {
        using ClientInFleet client = ClientInFleet.As(Commander, bossCharacterId: Commander);
        var window = new ActivityWindowViewModel(ActivityKind.Site, client.Services);

        window.StartManualRun(DateTime.UtcNow);

        Assert.True(window.IsStopButtonVisible);
        Assert.True(window.IsDiscardButtonVisible);
        Assert.False(window.IsCommandStatusShown);
        Assert.Equal(FleetId, window.FleetId); // and the run is filed under the fleet the discard fans out over
    }

    /// <summary>
    /// The counter-proof for the handover, end to end: ESI hands the fleet over and the window follows. The same
    /// pilot who could not command may after it, and once it moves on again they may not any more. Nothing is
    /// captured at start — pin the authority down at START instead of re-reading it and this goes red.
    /// </summary>
    [AvaloniaFact]
    public void WhenEsiReportsANewFleetBoss_TheControlsMoveWithIt()
    {
        using ClientInFleet client = ClientInFleet.As(Member, bossCharacterId: Commander);
        var window = new ActivityWindowViewModel(ActivityKind.Site, client.Services);
        DateTime now = DateTime.UtcNow;

        window.StartManualRun(now);
        Assert.False(window.IsDiscardButtonVisible);

        client.BossBecomes(Member);
        window.Refresh(now + FleetBossTracker.Ttl + TimeSpan.FromSeconds(1));
        Assert.True(window.IsDiscardButtonVisible, "the new FC did not inherit the controls");

        client.BossBecomes(Commander);
        window.Refresh(now + FleetBossTracker.Ttl * 2 + TimeSpan.FromSeconds(2));
        Assert.False(window.IsDiscardButtonVisible, "the former FC kept the controls after handing over");
        Assert.True(window.IsCommandStatusShown);
    }

    /// <summary>
    /// The source drops out — ESI times out mid-run. Not knowing must stay not knowing: no controls, no falling
    /// back on the last name it gave, and the reason on screen rather than an empty corner.
    /// </summary>
    [AvaloniaFact]
    public void WhenEsiStopsAnswering_TheAuthorityGoesUnknownAndSaysSo()
    {
        using ClientInFleet client = ClientInFleet.As(Commander, bossCharacterId: Commander);
        var window = new ActivityWindowViewModel(ActivityKind.Site, client.Services);
        DateTime now = DateTime.UtcNow;

        window.StartManualRun(now);
        Assert.True(window.IsDiscardButtonVisible); // it had the answer a moment ago

        client.EsiStopsAnswering();
        window.Refresh(now + FleetBossTracker.Ttl + TimeSpan.FromSeconds(1));

        Assert.False(window.IsStartButtonVisible);
        Assert.False(window.IsStopButtonVisible);
        Assert.False(window.IsDiscardButtonVisible);
        Assert.True(window.IsCommandStatusShown);
        Assert.Contains("not known", window.CommandStatusText, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The other half of that: with no fleet to be commanded, the pilot keeps their own run. Without this
    /// the test above would also pass on a window that simply never shows a control.</summary>
    [AvaloniaFact]
    public void WithNoFleetToCommand_TheSoloPilotKeepsTheirOwnRun()
    {
        using var instance = TestClientInstance.Create();
        var window = new ActivityWindowViewModel(ActivityKind.Site, instance.Services);

        window.StartManualRun(DateTime.UtcNow);

        Assert.True(window.IsStopButtonVisible);
        Assert.True(window.IsDiscardButtonVisible);
        Assert.False(window.IsCommandStatusShown);
        Assert.Null(window.FleetId);
    }

    /// <summary>
    /// ET-135, end to end and in the shape it actually happens in: the member accepted the commander's offer, so
    /// <c>FleetRunWindowPresenter</c> built the window with the group code and fleet id off the wire, and this pilot
    /// never opened the fleets window. The run is still the commander's. Take the group code out of the decision and
    /// this window gets START, STOP and DISCARD, and that DISCARD reaches every other member's machine.
    ///
    /// The fleet-id assertion below was <c>Assert.Null</c> until ET-152 and is deliberately turned over: it pinned
    /// down that the tick overwrote the wire's fleet id with what <c>IActiveFleetState</c> knew, which was nothing.
    /// Now the tick reads membership, so the id survives — and this member is denied because the fleet has a boss who
    /// is somebody else, rather than because the window could not name a fleet at all. That is the stronger reason,
    /// and it is the one ET-152's proof of done asks for.
    /// </summary>
    [AvaloniaFact]
    public void AMemberWhoseFleetsWindowWasNeverOpened_GetsNoControlsOverTheCommandersRun()
    {
        using ClientInFleet client = ClientInFleet.As(Member, bossCharacterId: Commander, knowledge: FleetKnowledge.MembershipOnly);
        ActivityWindowViewModel window =
            new(ActivityKind.Site, client.Services) { GroupCode = "HF-7Q2", FleetId = FleetId };

        window.StartManualRun(DateTime.UtcNow);

        Assert.Equal(FleetId, window.FleetId);      // membership carries it now, where IActiveFleetState knew nothing
        Assert.Equal(RunControlAuthorityLevel.Denied, window.Authority.Level); // and denied for the right reason
        Assert.False(window.IsStartButtonVisible);
        Assert.False(window.IsStopButtonVisible);
        Assert.False(window.IsDiscardButtonVisible);
        Assert.True(window.IsCommandStatusShown);   // and it says why, rather than a corner where buttons used to be
        Assert.NotEqual(string.Empty, window.CommandStatusText.Trim());
    }

    /// <summary>
    /// The other half, and the one that keeps the gate from swinging shut over everybody: the same client, in the
    /// same in-game fleet with the same boss, flying a run of its OWN — no group code, so no group to command and
    /// nobody else's machine to reach. This pilot keeps every button. Decide on "is there a fleet boss" rather than
    /// on the run's group code and this goes red.
    /// </summary>
    [AvaloniaFact]
    public void APilotsOwnRunWhileInAnInGameFleet_KeepsItsButtons()
    {
        using ClientInFleet client = ClientInFleet.As(Member, bossCharacterId: Commander, knowledge: FleetKnowledge.MembershipOnly);
        var window = new ActivityWindowViewModel(ActivityKind.Site, client.Services);

        window.StartManualRun(DateTime.UtcNow);

        Assert.Null(window.GroupCode);
        Assert.True(window.IsStopButtonVisible);
        Assert.True(window.IsDiscardButtonVisible);
        Assert.False(window.IsCommandStatusShown);
    }

    /// <summary>The read is not made faster than the endpoint's own 60s ESI cache: a 1 Hz tick must not turn into
    /// a 1 Hz poll, which is how the error-limit budget is spent and the client banned.</summary>
    [AvaloniaFact]
    public void TheTick_DoesNotReadEsiFasterThanItsOwnCache()
    {
        using ClientInFleet client = ClientInFleet.As(Commander, bossCharacterId: Commander);
        var window = new ActivityWindowViewModel(ActivityKind.Site, client.Services);
        DateTime now = DateTime.UtcNow;

        window.StartManualRun(now);
        // The two assertions around the loop are what stop this from checking nothing: on its own, "the count did
        // not go up" is also satisfied by a count that never left zero — it passed on a window wired to no source at
        // all. Do not simplify back to the middle assertion alone.
        int afterFirstTick = client.Esi.CharFleetReads;
        Assert.True(afterFirstTick > 0, "no read was made at all — this would pass on a window wired to nothing");

        for (int second = 1; second <= 30; second++)
            window.Refresh(now + TimeSpan.FromSeconds(second));
        Assert.Equal(afterFirstTick, client.Esi.CharFleetReads);

        // And it does read again once that cache has expired, or the answer would never move.
        window.Refresh(now + FleetBossTracker.Ttl + TimeSpan.FromSeconds(1));
        Assert.True(client.Esi.CharFleetReads > afterFirstTick);
    }

    /// <summary>Saving is every member's own. A member who may not steer the shared run may still commit their own
    /// part of it — that is the whole asymmetry the ticket is built on.</summary>
    [AvaloniaFact]
    public void AMemberWhoIsNotTheFc_MayStillSaveTheirOwnRun()
    {
        using ClientInFleet client = ClientInFleet.As(Member, bossCharacterId: Commander);
        var window = new ActivityWindowViewModel(ActivityKind.Site, client.Services);
        window.StartManualRun(DateTime.UtcNow);
        window.RunId = Guid.CreateVersion7();

        window.StopRun(DateTime.UtcNow);

        Assert.False(window.IsDiscardButtonVisible);
        Assert.True(window.IsSaveButtonVisible);
    }

    // ── What the START button hands the command ────────────────────────────────────────────────────
    //
    // ET-147. The tests above prove the window KNOWS who commands; these prove it SAYS SO when it starts a run.
    // StartRunCommand.IsFleetCommander defaulted to false and the window never passed it, so a site run never got a
    // group code and no FleetRunGroupCodeEvent was ever published, by anybody. Asserting on a StartRunCommand the
    // test built itself is what let that stand: it proves the handler, not the caller. These drive the button.

    /// <summary>
    /// The FC starts a site run in his fleet and the fleet is told, with a group code for the members to file their
    /// own runs under. Red before ET-147 at every rung: nothing was published at all.
    ///
    /// The three rungs are ET-152. Announcing used to need <c>IActiveFleetState</c>, which only OPEN METRICS fills —
    /// so a commander who never pressed that button published nothing and nobody saw his run start. Both lower rungs
    /// are red before ET-152 and the top one is not, which is what makes them worth the rows: the bug was invisible
    /// from the only state the suite used to build.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(FleetKnowledge.MetricsOpened)]
    [InlineData(FleetKnowledge.MembershipOnly)]
    [InlineData(FleetKnowledge.SweptOnStartup)]
    public async Task TheFcsStart_IsAnnouncedToTheFleet(FleetKnowledge knowledge)
    {
        using ClientInFleet client = ClientInFleet.As(Commander, bossCharacterId: Commander, knowledge: knowledge);
        var window = new ActivityWindowViewModel(ActivityKind.Site, client.Services);
        window.UseCharacter(Commander, "Jithran");

        List<RunGroupCodeStart> announced = _AnnouncementsOf(client, out IDisposable subscription);
        using (subscription)
            await window.StartRunCommand.ExecuteAsync(null);

        RunGroupCodeStart start = Assert.Single(announced);
        Assert.True(start.IsFleetCommander);
        Assert.Equal(client.FleetId, start.FleetId);
        Assert.NotNull(await _StoredGroupCodeAsync(client, Commander));
    }

    /// <summary>The other half, and the one that matters most if the flag is ever wired to the wrong thing: a
    /// member's own start is their own business. No group code, and it reaches nobody — pass
    /// <c>IsFleetCommander: true</c> unconditionally and this goes red.</summary>
    [AvaloniaFact]
    public async Task AMembersOwnStart_IsNotAnnouncedToTheFleet()
    {
        using ClientInFleet client = ClientInFleet.As(Member, bossCharacterId: Commander);
        var window = new ActivityWindowViewModel(ActivityKind.Site, client.Services);
        window.UseCharacter(Member, "Raymond");

        List<RunGroupCodeStart> announced = _AnnouncementsOf(client, out IDisposable subscription);
        using (subscription)
            await window.StartRunCommand.ExecuteAsync(null);

        Assert.Empty(announced);
        Assert.Null(await _StoredGroupCodeAsync(client, Member));
    }

    // ── ET-150: what the window does before it has been told anything ──────────────────────────────

    /// <summary>
    /// The window as <c>FleetRunWindowPresenter</c> builds it — group code and fleet id off the wire — with the
    /// boss read still in flight, which is the only state a real client opens in. The initial authority used to be
    /// <c>From(null, null, null, null)</c>, and four nulls read as "solo", so every button was on the screen the
    /// pilot is actually looking at until ESI answered. DISCARD there reaches every other member's machine.
    /// </summary>
    [AvaloniaFact]
    public void AFreshMembersWindow_HasNoRunControlsWhileTheBossIsStillBeingRead()
    {
        using ClientInFleet client = ClientInFleet.As(Member, bossCharacterId: Commander);
        client.Esi.HoldEsiOpen();

        ActivityWindowViewModel window =
            new(ActivityKind.Site, client.Services) { GroupCode = "HF-7Q2", FleetId = FleetId };

        Assert.False(window.IsStartButtonVisible);
        Assert.False(window.IsDiscardButtonVisible);
        Assert.True(window.IsCommandStatusShown);
        client.Esi.LetEsiAnswer();
    }

    /// <summary>
    /// The counter-proof, and the reason "start at Unknown" cannot be the whole fix: a pilot flying alone keeps
    /// every button from the moment the window opens, with the very first boss read still in flight. His own run,
    /// so no boss's answer could change the outcome and there is nothing to wait for.
    ///
    /// Nothing may touch this window after the constructor. A second <c>Refresh</c> makes it pass either way:
    /// <see cref="FleetBossTracker"/> stamps its slot before the read goes out, so the next call finds it inside the
    /// TTL and returns without ever reaching the gate. That is what the first version of this test did, and it was
    /// green with the fix taken back out.
    /// </summary>
    [AvaloniaFact]
    public void AFreshSoloWindow_KeepsItsButtonsWhileTheBossIsStillBeingRead()
    {
        using ClientInFleet client = ClientInFleet.As(Member, bossCharacterId: Commander, knowledge: FleetKnowledge.MembershipOnly);
        client.Esi.HoldEsiOpen();

        var window = new ActivityWindowViewModel(ActivityKind.Site, client.Services);

        Assert.Null(window.GroupCode);              // genuinely alone, not merely unable to say
        Assert.True(window.IsStartButtonVisible);
        Assert.False(window.IsCommandStatusShown);
        Assert.Equal(0, client.Esi.CharFleetReads); // and it never asked about a boss it does not need
        client.Esi.LetEsiAnswer();
    }

    private static List<RunGroupCodeStart> _AnnouncementsOf(ClientInFleet client, out IDisposable subscription)
    {
        List<RunGroupCodeStart> announced = [];
        subscription = client.Services.GetRequiredService<IEventBus>().Subscribe<FleetRunGroupCodeEvent>(
            (integrationEvent, _) =>
            {
                announced.Add(integrationEvent.Data);
                return Task.CompletedTask;
            });
        return announced;
    }

    private static async Task<string?> _StoredGroupCodeAsync(ClientInFleet client, int characterId)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using ClientDbContext db = await client.Services
            .GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken);
        return (await db.Set<Run>()
            .SingleAsync(run => run.CharacterId == characterId, cancellationToken)).GroupCode;
    }

    /// <summary>
    /// A real client, participating in a fleet, with ESI standing in for the one thing a headless run cannot have:
    /// an in-game fleet with a boss. Nothing else is substituted, so what the window shows is what the wiring
    /// produced.
    /// </summary>
    private sealed class ClientInFleet(TestClientInstance instance, FakeEsiFleetClient esi) : IDisposable
    {
        private const long InGameFleetId = 987654321;

        public IServiceProvider Services => instance.Services;

        public FakeEsiFleetClient Esi => esi;

        /// <summary>The fleet this client is in — the shared constant, or the id the local repository handed out
        /// when the fleet was really created (<see cref="FleetKnowledge.SweptOnStartup"/>).</summary>
        public long FleetId { get; private set; } = HomefrontCommandAuthorityTests.FleetId;

        /// <param name="actingCharacterId">The character the fleets-window row was selected as.</param>
        /// <param name="flyingAs">The character this client actually publishes as; defaults to the same one.</param>
        /// <param name="knowledge">How much this client has been told about the fleet it is in.</param>
        public static ClientInFleet As(
            int actingCharacterId, int? bossCharacterId, int? flyingAs = null,
            FleetKnowledge knowledge = FleetKnowledge.MetricsOpened)
        {
            var esi = new FakeEsiFleetClient();
            var client = new ClientInFleet(
                TestClientInstance.Create(services => services.AddSingleton<IEsiFleetClient>(esi)), esi);
            client.BossBecomes(bossCharacterId);

            if (knowledge == FleetKnowledge.SweptOnStartup)
            {
                client._CreateRealFleet(flyingAs ?? actingCharacterId);
                return client;
            }

            if (knowledge == FleetKnowledge.MetricsOpened)
                client.Services.GetRequiredService<IActiveFleetState>()
                    .Enter(client.FleetId, actingCharacterId, clientOnly: true);
            // Who this client flies as, which is what the run controls are decided for — the fleets-window row
            // selection above only says which fleet is on screen.
            client.Services.GetRequiredService<IFleetParticipation>()
                .Set([new FleetParticipant(flyingAs ?? actingCharacterId, client.FleetId, ClientOnly: true)]);
            return client;
        }

        /// <summary>
        /// The state a pilot's client is really in after starting the app while already in a fleet: the fleet exists
        /// in the local repository and nothing else has been told anything. Nothing calls <c>Enter</c> and nothing
        /// hands the participation set a ready-made entry — the startup sweep has to find it, which is the whole
        /// point of the row that uses this.
        /// </summary>
        private void _CreateRealFleet(int characterId)
        {
            Services.GetRequiredService<ICharacterRegistry>()
                .AddOrUpdateAsync(new Character("Jithran", characterId)).GetAwaiter().GetResult();
            Result<long> created = Services.GetRequiredService<ClientFleetService>()
                .CreateLocalFleetAsync("HF", null, characterId).GetAwaiter().GetResult();
            Assert.True(created.IsSuccess);
            FleetId = created.Value;

            // What MainWindowViewModel's startup chain reaches through Home.RefreshAsync() — the one sweep a pilot
            // gets without opening anything.
            Services.GetRequiredService<FleetParticipationRefresher>().RefreshAsync().GetAwaiter().GetResult();
        }

        /// <summary>ESI reports the fleet under new command from its next read on.</summary>
        public void BossBecomes(int? bossCharacterId)
        {
            esi.Error = null;
            esi.CharFleet = bossCharacterId is { } boss
                ? new EsiCharacterFleet { FleetId = InGameFleetId, FleetBossId = boss }
                : null;
        }

        /// <summary>ESI lags or drops out — a transient failure, not "you are in no fleet".</summary>
        public void EsiStopsAnswering() =>
            esi.Error = EsiError.Of(EsiErrorKind.Timeout, "ESI did not answer", 504);

        public void Dispose() => instance.Dispose();
    }
}
