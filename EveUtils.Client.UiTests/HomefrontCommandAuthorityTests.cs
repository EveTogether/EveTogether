using Avalonia.Headless.XUnit;
using EveUtils.Client.Esi;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Runs.Control;
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

    // ── The rule ────────────────────────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void TheFleetBoss_MayCommandTheRun() =>
        Assert.True(RunControlAuthority.From(FleetId, Commander, Commander).CanControl);

    [AvaloniaFact]
    public void AMemberWhoIsNotTheFleetBoss_MayNot()
    {
        RunControlAuthority authority = RunControlAuthority.From(FleetId, Commander, Member);

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
        RunControlAuthority authority = RunControlAuthority.From(FleetId, fleetBossCharacterId: null, Member);

        Assert.False(authority.CanControl);
        Assert.True(authority.IsUnknown);
        Assert.Contains("not known", authority.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(string.Empty, authority.StatusText.Trim());
    }

    /// <summary>A pilot soloing owns their own run: there is no commander to be and no other machine to reach.</summary>
    [AvaloniaFact]
    public void ASoloRun_IsAlwaysTheOwnPilotsToCommand() =>
        Assert.True(RunControlAuthority.From(fleetId: null, fleetBossCharacterId: null, Member).CanControl);

    /// <summary>
    /// The counter-proof for the handover: the same pilot who could not command before may after the boss moves to
    /// them, and the old commander may not any more. Nothing is captured at start.
    /// </summary>
    [AvaloniaFact]
    public void WhenTheFleetBossChanges_TheRightMovesWithIt()
    {
        Assert.False(RunControlAuthority.From(FleetId, Commander, Member).CanControl);
        Assert.True(RunControlAuthority.From(FleetId, Commander, Commander).CanControl);

        // The boss hands over to Member mid-run.
        Assert.True(RunControlAuthority.From(FleetId, Member, Member).CanControl);
        Assert.False(RunControlAuthority.From(FleetId, Member, Commander).CanControl);
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

    /// <summary>The read is not made faster than the endpoint's own 60s ESI cache: a 1 Hz tick must not turn into
    /// a 1 Hz poll, which is how the error-limit budget is spent and the client banned.</summary>
    [AvaloniaFact]
    public void TheTick_DoesNotReadEsiFasterThanItsOwnCache()
    {
        using ClientInFleet client = ClientInFleet.As(Commander, bossCharacterId: Commander);
        var window = new ActivityWindowViewModel(ActivityKind.Site, client.Services);
        DateTime now = DateTime.UtcNow;

        window.StartManualRun(now);
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

        public static ClientInFleet As(int actingCharacterId, int? bossCharacterId)
        {
            var esi = new FakeEsiFleetClient();
            var client = new ClientInFleet(
                TestClientInstance.Create(services => services.AddSingleton<IEsiFleetClient>(esi)), esi);
            client.BossBecomes(bossCharacterId);
            client.Services.GetRequiredService<IActiveFleetState>()
                .Enter(FleetId, actingCharacterId, clientOnly: true);
            return client;
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
