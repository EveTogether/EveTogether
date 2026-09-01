using Avalonia.Headless.XUnit;
using EveUtils.Client.ViewModels.Activity;
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

    // ── What the window does with it ────────────────────────────────────────────────────────────────

    /// <summary>A member who is not the FC sees no start, stop or discard button, and is told why.</summary>
    [AvaloniaFact]
    public void AMemberWhoIsNotTheFc_GetsNoRunControls()
    {
        using var instance = TestClientInstance.Create();
        var window = new ActivityWindowViewModel(ActivityKind.Site, instance.Services);
        window.StartManualRun(DateTime.UtcNow);

        window.ApplyFleetCommand(FleetId, Commander, Member);

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
        using var instance = TestClientInstance.Create();
        var window = new ActivityWindowViewModel(ActivityKind.Site, instance.Services);
        window.StartManualRun(DateTime.UtcNow);

        window.ApplyFleetCommand(FleetId, Commander, Commander);

        Assert.True(window.IsStopButtonVisible);
        Assert.True(window.IsDiscardButtonVisible);
        Assert.False(window.IsCommandStatusShown);
    }

    /// <summary>The buttons appearing at one member and vanishing at another during a run is an ordinary state
    /// change. This walks a handover on one live window rather than on two fresh ones.</summary>
    [AvaloniaFact]
    public void OnALiveWindow_AHandoverMovesTheControls()
    {
        using var instance = TestClientInstance.Create();
        var window = new ActivityWindowViewModel(ActivityKind.Site, instance.Services);
        window.StartManualRun(DateTime.UtcNow);

        window.ApplyFleetCommand(FleetId, Commander, Member);
        Assert.False(window.IsDiscardButtonVisible);

        window.ApplyFleetCommand(FleetId, Member, Member);
        Assert.True(window.IsDiscardButtonVisible);

        // ESI then loses the fleet: the controls go away again rather than staying on the last known answer.
        window.ApplyFleetCommand(FleetId, null, Member);
        Assert.False(window.IsDiscardButtonVisible);
        Assert.True(window.IsCommandStatusShown);
    }

    /// <summary>Saving is every member's own. A member who may not steer the shared run may still commit their own
    /// part of it — that is the whole asymmetry the ticket is built on.</summary>
    [AvaloniaFact]
    public void AMemberWhoIsNotTheFc_MayStillSaveTheirOwnRun()
    {
        using var instance = TestClientInstance.Create();
        var window = new ActivityWindowViewModel(ActivityKind.Site, instance.Services);
        window.StartManualRun(DateTime.UtcNow);
        window.RunId = Guid.CreateVersion7();
        window.ApplyFleetCommand(FleetId, Commander, Member);
        window.StopRun(DateTime.UtcNow);

        Assert.False(window.IsDiscardButtonVisible);
        Assert.True(window.IsSaveButtonVisible);
    }
}
