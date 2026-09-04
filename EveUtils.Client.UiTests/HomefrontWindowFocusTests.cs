using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Runs;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Settings.Repositories;
using EveUtils.Shared.Modules.Runs.Enums;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

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

    /// <summary>
    /// A second call carries a newly copied signature, and the window already up is the one that has to hear it.
    /// The service used to raise that window and drop the incoming view model, so "start run" on a fresh signature
    /// left the previous site on screen — Raymond hit that three times (2026-09-02).
    /// </summary>
    [AvaloniaFact]
    public void ASecondStart_HandsItsSignatureToTheWindowAlreadyUp()
    {
        using var instance = TestClientInstance.Create();
        Window owner = _Owner(out DialogService dialogs);

        var open = new ActivityWindowViewModel(ActivityKind.Site, instance.Services) { SignatureName = "Sansha Hideaway" };
        dialogs.ShowActivityWindow(open, RunWindowOpenTrigger.LocalUser);
        Window? first = dialogs.ActivityWindow;

        dialogs.ShowActivityWindow(
            new ActivityWindowViewModel(ActivityKind.Site, instance.Services) { SignatureName = "Drone Cluster" },
            RunWindowOpenTrigger.LocalUser);

        Assert.Same(first, dialogs.ActivityWindow);   // still one window, as ET-100 requires
        Assert.Equal("Drone Cluster", open.SignatureName);

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
    /// FLIPPED (was: the commander's start opens the window here outright). A window a pilot did not ask for is now
    /// offered as a toast first — see <see cref="FleetRunOfferToastTests"/> — so the commander's start opens the
    /// window on this member's machine only when the member set the window preference. What that path must still
    /// never do is take the keyboard, and that is what is asserted here: this is the only caller allowed to pass the
    /// remote trigger, and the reason the rule above is ever exercised in the running app.
    /// </summary>
    [AvaloniaFact]
    public void TheFleetCommandersStart_WhenTheMemberChoseTheWindow_AsksForItWithoutFocus()
    {
        var dialogs = new RecordingDialogService();
        using var instance = TestClientInstance.Create(services => services.AddSingleton<IDialogService>(dialogs));
        instance.Services.GetRequiredService<ISettingRepository>()
            .UpsertAsync(FleetRunWindowPresenter.AutoOpenSettingKey, "true").GetAwaiter().GetResult();
        var bus = instance.Services.GetRequiredService<IEventBus>();
        using var presenter = new FleetRunWindowPresenter(bus, dialogs, instance.Services);

        bus.PublishAsync(new FleetRunGroupCodeEvent(new RunGroupCodeStart(4242, ActivityKind.Site, "HF-F0CU",
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

        bus.PublishAsync(new FleetRunGroupCodeEvent(new RunGroupCodeStart(4242, ActivityKind.Site, "HF-F0CU",
            DateTime.UtcNow, IsFleetCommander: false))).GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(dialogs.ShownActivityWindowTriggers);
    }

    // ── Dialogs opened while the run overlay is up ──────────────────────────────────────────────────

    /// <summary>
    /// The run window is <c>Topmost</c> and every dialog is owned by the main window, so the fit picker opened
    /// behind it and looked like nothing had happened. Each dialog is raised on its way out instead — the owner, and
    /// with it which window the dialog blocks, is untouched.
    ///
    /// <b>Z-order itself cannot be asserted here.</b> The headless platform keeps an empty <c>Windows</c> collection
    /// and models neither activation nor stacking, so a test that "checked" which window is in front would be green
    /// whatever this code did. What is checked instead is that no dialog leaves <see cref="DialogService"/> without
    /// going through that one seam — the thing that would go missing in a revert, or be forgotten by the next dialog
    /// added. That the picker now lands in front is a desktop observation, and is recorded as one.
    /// </summary>
    [Fact]
    public void EveryDialogIsRaisedOverTheRunOverlay()
    {
        string source = File.ReadAllText(_SourcePath("EveUtils.Client/Dialogs/DialogService.cs"));

        foreach (string line in source.Split('\n').Where(line => line.Contains(".ShowDialog", StringComparison.Ordinal)))
            Assert.Contains("_Over(", line, StringComparison.Ordinal);

        // And the seam is the one that raises: a rename that lost the Topmost would leave the calls looking right.
        Assert.Contains("dialog.Topmost = true", source, StringComparison.Ordinal);
    }

    private static string _SourcePath(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EVE-Together.slnx")))
            directory = directory.Parent;

        return Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("the solution root is not above the test binary"),
            relative);
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
