using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using StoredActivityKind = EveUtils.Shared.Modules.Runs.Enums.ActivityKind;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-105 AC-2: the run window comes up on the other members' machines when the fleet commander starts, and it does
/// not take the keyboard. In EVE a window that grabs focus mid-fight costs a ship.
///
/// <b>No global input injection is used anywhere here</b> — no <c>SetForegroundWindow</c>, <c>SetWindowPos</c>,
/// <c>SetCursorPos</c>, <c>mouse_event</c> or <c>SendKeys</c>. Those APIs do not respect a process boundary and
/// would take the real machine's mouse and keyboard away from whoever is sitting at it. Everything below is
/// Avalonia's own headless window API.
///
/// <b>What is deliberately not asserted, and why.</b> Avalonia's headless platform models no focus at all:
/// <c>Window.IsActive</c> stays false through <c>Show()</c> and <c>Activate()</c>, and <c>Window.Activated</c>
/// never fires (both probed against this Avalonia version). An <c>Assert.False(window.IsActive)</c> would therefore
/// pass just as happily if this code did steal focus, so it would prove nothing and is left out. What is asserted
/// instead is the mechanism that decides it: the rule in <see cref="RunWindowPresentation"/>, and
/// <see cref="Window.ShowActivated"/> on the window <see cref="DialogService"/> actually built — that flag is what
/// Avalonia reads at <c>Show()</c> to decide whether to take focus, and it flips if either half is broken.
/// </summary>
public sealed class HomefrontWindowFocusTests
{
    // ── The rule ────────────────────────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void FleetCommanderStart_OnAnotherMembersMachine_ShowsWithoutActivating() =>
        Assert.Equal(RunWindowActivation.ShowWithoutActivating,
            RunWindowPresentation.Decide(RunWindowOpenTrigger.RemoteFleetCommander, isAlreadyOpen: false));

    /// <summary>A window already up is left entirely alone: raising or re-showing it reaches for focus too.</summary>
    [AvaloniaFact]
    public void FleetCommanderStart_WithTheWindowAlreadyUp_TouchesNothing() =>
        Assert.Equal(RunWindowActivation.LeaveAsIs,
            RunWindowPresentation.Decide(RunWindowOpenTrigger.RemoteFleetCommander, isAlreadyOpen: true));

    /// <summary>The pilot who clicked is looking at the app, so focus is what they asked for. Without this the rule
    /// would be "never focus", which is a different and worse window.</summary>
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void OwnClick_TakesFocus(bool alreadyOpen) =>
        Assert.Equal(RunWindowActivation.Activate,
            RunWindowPresentation.Decide(RunWindowOpenTrigger.LocalUser, alreadyOpen));

    // ── The window the service really builds ────────────────────────────────────────────────────────

    /// <summary>
    /// The counter-proof: the window the fleet commander's start puts on this member's screen is one Avalonia will
    /// not activate when it shows it.
    /// </summary>
    [AvaloniaFact]
    public void FleetCommanderStart_BuildsAWindowThatDoesNotTakeFocus()
    {
        using var instance = TestClientInstance.Create();
        Window owner = _Owner(out DialogService dialogs);

        dialogs.ShowActivityWindow(new ActivityWindowViewModel(ActivityKind.Site, instance.Services),
            RunWindowOpenTrigger.RemoteFleetCommander);

        Window window = Assert.IsAssignableFrom<Window>(dialogs.ActivityWindow);
        Assert.False(window.ShowActivated);

        dialogs.CloseAllPopouts();
        owner.Close();
    }

    /// <summary>The other half of the same seam: the pilot's own click does open an activating window. Without this
    /// the test above would also pass on a window that can never take focus under any circumstances.</summary>
    [AvaloniaFact]
    public void OwnClick_BuildsAWindowThatTakesFocus()
    {
        using var instance = TestClientInstance.Create();
        Window owner = _Owner(out DialogService dialogs);

        dialogs.ShowActivityWindow(new ActivityWindowViewModel(ActivityKind.Site, instance.Services),
            RunWindowOpenTrigger.LocalUser);

        Assert.True(Assert.IsAssignableFrom<Window>(dialogs.ActivityWindow).ShowActivated);

        dialogs.CloseAllPopouts();
        owner.Close();
    }

    /// <summary>A second fleet-commander start with the window already up must not build or re-show anything: the
    /// service returns before it ever touches the window, so there is no path from here to a focus call.</summary>
    [AvaloniaFact]
    public void FleetCommanderStart_WithTheWindowAlreadyUp_KeepsTheSameWindowUntouched()
    {
        using var instance = TestClientInstance.Create();
        Window owner = _Owner(out DialogService dialogs);

        dialogs.ShowActivityWindow(new ActivityWindowViewModel(ActivityKind.Site, instance.Services),
            RunWindowOpenTrigger.RemoteFleetCommander);
        Window? first = dialogs.ActivityWindow;

        dialogs.ShowActivityWindow(new ActivityWindowViewModel(ActivityKind.Site, instance.Services),
            RunWindowOpenTrigger.RemoteFleetCommander);

        Assert.Same(first, dialogs.ActivityWindow);
        Assert.Equal(1, dialogs.OpenPopoutCount);
        Assert.False(dialogs.ActivityWindow!.ShowActivated);

        dialogs.CloseAllPopouts();
        owner.Close();
    }

    // ── The path that actually opens it on a member's machine ───────────────────────────────────────

    /// <summary>
    /// The fleet commander's start reaches this member and asks for the window under the remote trigger — the only
    /// caller that may, and the reason the rule above is ever exercised in the running app.
    /// </summary>
    [AvaloniaFact]
    public void TheFleetCommandersStart_AsksForTheWindowWithoutFocus()
    {
        var dialogs = new RecordingDialogService();
        using var instance = TestClientInstance.Create(services => services.AddSingleton<IDialogService>(dialogs));
        var bus = instance.Services.GetRequiredService<IEventBus>();
        using var presenter = new FleetRunWindowPresenter(bus, dialogs, instance.Services);

        bus.PublishAsync(new FleetRunGroupCodeEvent(new RunGroupCodeStart(4242, StoredActivityKind.Site, "HF-F0CU",
            DateTime.UtcNow, IsFleetCommander: true))).GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(RunWindowOpenTrigger.RemoteFleetCommander,
            Assert.Single(dialogs.ShownActivityWindowTriggers));
    }

    /// <summary>A member's own start is their own business and opens nothing on anybody else's screen. Without this
    /// the presenter could fire on every start and the test above would still pass.</summary>
    [AvaloniaFact]
    public void AMembersOwnStart_OpensNothingOnOtherScreens()
    {
        var dialogs = new RecordingDialogService();
        using var instance = TestClientInstance.Create(services => services.AddSingleton<IDialogService>(dialogs));
        var bus = instance.Services.GetRequiredService<IEventBus>();
        using var presenter = new FleetRunWindowPresenter(bus, dialogs, instance.Services);

        bus.PublishAsync(new FleetRunGroupCodeEvent(new RunGroupCodeStart(4242, StoredActivityKind.Site, "HF-F0CU",
            DateTime.UtcNow, IsFleetCommander: false))).GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(dialogs.ShownActivityWindowTriggers);
    }

    private static Window _Owner(out DialogService dialogs)
    {
        var owner = new Window { Width = 200, Height = 200 };
        owner.Show();
        dialogs = new DialogService();
        dialogs.SetOwner(owner);
        return owner;
    }
}
