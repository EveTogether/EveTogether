using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Client.Views;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.Settings.Repositories;
using Material.Icons.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Docked module host: rail modules + sub-screens open as closeable tabs inside the main window when docked
/// (multiple coexist), and as separate windows when floating; the open set migrates on a dock/float switch, and
/// closing a tab disposes the module's view-model. Covers the ModuleHostService routing, hosted bindings, migration
/// and teardown.
/// </summary>
public class ModuleNavigationTests
{
    private sealed class FakeDisplay : IModuleHostDisplay
    {
        public bool IsFloating { get; set; }
        public ObservableCollection<HostTab> HostTabs { get; } = new();
        public HostTab? SelectedHostTab { get; set; }
    }

    /// <summary>A hostable module window with no chrome/XAML to load — just enough to drive
    /// <see cref="IHostableModuleWindow.DockRequested"/> from a test (ET-111).</summary>
    private sealed class HostableWindow : Window, IHostableModuleWindow
    {
        public Action? CloseRequested { get; set; }
        public Action? DockRequested { get; set; }
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int tries = 150)
    {
        for (var i = 0; i < tries; i++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    [AvaloniaFact]
    public void DialogService_Docked_HostsAsTab()
    {
        var fake = new FakeDisplay { IsFloating = false };
        var dialogs = new DialogService();
        dialogs.SetOwner(new Window());            // docked never shows it
        dialogs.SetHost(fake);

        dialogs.ShowLogs(new ClientLogViewModel());

        Assert.Single(fake.HostTabs);
        Assert.Equal("APP LOGS", fake.HostTabs[0].Title);
        Assert.NotNull(fake.SelectedHostTab);
        Assert.NotNull(fake.HostTabs[0].Content);
    }

    [AvaloniaFact]
    public void ModuleHost_DedupesByModuleId_SoOneRosterPerFleet()
    {
        var fake = new FakeDisplay { IsFloating = false };
        var host = new ModuleHostService();
        host.SetOwner(new Window());
        host.SetHost(fake);

        static Window Roster() => new() { Content = new Border() };

        // Two fleets share the roster window TITLE but carry distinct module ids → two distinct tabs. Before the fix
        // the title-only de-dupe collapsed them into one, so MANAGE on fleet B re-selected fleet A's stale roster.
        host.Open(Roster(), "FLEET ROSTER", "fleet", "fleet-roster:1");
        host.Open(Roster(), "FLEET ROSTER", "fleet", "fleet-roster:2");
        Assert.Equal(2, fake.HostTabs.Count);

        // Re-opening fleet 1 re-selects its existing tab — no third tab.
        host.Open(Roster(), "FLEET ROSTER", "fleet", "fleet-roster:1");
        Assert.Equal(2, fake.HostTabs.Count);
    }

    [AvaloniaFact]
    public void ModuleHost_SameTitleWithDistinctModuleIds_HostsBothViewModels()
    {
        var fake = new FakeDisplay { IsFloating = false };
        var host = new ModuleHostService();
        host.SetOwner(new Window());
        host.SetHost(fake);

        var first = new ProbeModule();
        var second = new ProbeModule();
        host.Open(new Window { Content = new Border(), DataContext = first }, "FIT DETAIL", "fits", "fit-detail:1");
        host.Open(new Window { Content = new Border(), DataContext = second }, "FIT DETAIL", "fits", "fit-detail:2");

        Assert.Equal(2, fake.HostTabs.Count);
        Assert.Same(second, fake.HostTabs[1].Content.DataContext);
    }

    /// <summary>A module view-model that records the two things re-opening has to do to it (ET-46).</summary>
    private sealed class ProbeModule : IRefreshableModule, IDisposable
    {
        public int Refreshed { get; private set; }
        public bool Disposed { get; private set; }
        public void RefreshModule() => Refreshed++;
        public void Dispose() => Disposed = true;
    }

    /// <summary>
    /// Re-opening an already-open module must not silently hand back the state it was built with: the standing
    /// module is told to refresh, and the freshly built duplicate the caller handed us — which nothing will ever
    /// show, close or dispose — is disposed here instead of leaking its subscriptions (ET-46).
    /// </summary>
    [AvaloniaFact]
    public void ModuleHost_ReopeningAModule_RefreshesTheStandingOne_AndDisposesTheDuplicate()
    {
        var fake = new FakeDisplay { IsFloating = false };
        var host = new ModuleHostService();
        host.SetOwner(new Window());
        host.SetHost(fake);

        var standing = new ProbeModule();
        host.Open(new Window { Content = new Border(), DataContext = standing }, "PROBE", null, "probe:1");
        Assert.Equal(0, standing.Refreshed);

        var duplicate = new ProbeModule();
        host.Open(new Window { Content = new Border(), DataContext = duplicate }, "PROBE", null, "probe:1");

        Assert.Single(fake.HostTabs);
        Assert.Equal(1, standing.Refreshed);
        Assert.False(standing.Disposed);   // the module the user is looking at survives
        Assert.True(duplicate.Disposed);   // the one nobody will ever see does not linger
        Assert.Equal(0, duplicate.Refreshed);
    }

    /// <summary>The guard on that dispose: some modules are re-opened with the SAME long-lived view-model (the
    /// inbox and the log window are properties of the main view-model), and disposing those would kill a live
    /// screen.</summary>
    [AvaloniaFact]
    public void ModuleHost_ReopeningWithTheSameViewModel_DoesNotDisposeIt()
    {
        var fake = new FakeDisplay { IsFloating = false };
        var host = new ModuleHostService();
        host.SetOwner(new Window());
        host.SetHost(fake);

        var shared = new ProbeModule();
        host.Open(new Window { Content = new Border(), DataContext = shared }, "PROBE", null, "probe:1");
        host.Open(new Window { Content = new Border(), DataContext = shared }, "PROBE", null, "probe:1");

        Assert.False(shared.Disposed);
        Assert.Equal(1, shared.Refreshed);
    }

    [AvaloniaFact]
    public void DialogService_Floating_ShowsWindow_NotTab()
    {
        var fake = new FakeDisplay { IsFloating = true };
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        var dialogs = new DialogService();
        dialogs.SetOwner(owner);
        dialogs.SetHost(fake);

        dialogs.ShowLogs(new ClientLogViewModel());

        Assert.Empty(fake.HostTabs);               // floating → a window, not a tab
        owner.Close();
    }

    [AvaloniaFact]
    public void Docked_TwoNewCompositionEditors_HostBoth_AndResolveBothTasks()
    {
        using var instance = TestClientInstance.Create();
        var fake = new FakeDisplay { IsFloating = false };
        var dialogs = new DialogService();
        dialogs.SetOwner(new Window());            // docked never shows it
        dialogs.SetHost(fake);

        var client = new LocalFleetCompositionClient(
            instance.Services.GetRequiredService<ClientFleetService>(),
            instance.Services.GetRequiredService<IFleetCompositionRepository>(), 95400001);
        var first = CompositionEditorViewModel.ForNew(instance.Services, client);
        var second = CompositionEditorViewModel.ForNew(instance.Services, client);
        var firstTask = dialogs.ShowCompositionEditorAsync(first);
        var secondTask = dialogs.ShowCompositionEditorAsync(second);
        Assert.Equal(2, fake.HostTabs.Count);
        Assert.Same(second, fake.HostTabs[1].Content.DataContext);

        first.CancelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(firstTask.IsCompleted);
        Assert.False(secondTask.IsCompleted);

        second.CancelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(secondTask.IsCompleted);
        Assert.False(secondTask.Result);
        Assert.Empty(fake.HostTabs);
    }

    [AvaloniaFact]
    public async Task Docked_SavedNewComposition_ClosesBeforePersistedCompositionReopens()
    {
        using var instance = TestClientInstance.Create();
        var fake = new FakeDisplay { IsFloating = false };
        var dialogs = new DialogService();
        dialogs.SetOwner(new Window());
        dialogs.SetHost(fake);
        var client = new LocalFleetCompositionClient(
            instance.Services.GetRequiredService<ClientFleetService>(),
            instance.Services.GetRequiredService<IFleetCompositionRepository>(), 95400001);
        var created = CompositionEditorViewModel.ForNew(instance.Services, client);
        created.Name = "Transition doctrine";

        var saveTask = dialogs.ShowCompositionEditorAsync(created);
        await created.SaveCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(saveTask.IsCompleted);
        Assert.True(saveTask.Result);
        Assert.Empty(fake.HostTabs);

        var saved = Assert.Single(await client.ListAsync());
        var detail = await client.GetAsync(saved.Id);
        Assert.NotNull(detail);
        var reopened = CompositionEditorViewModel.ForExisting(instance.Services, client, detail);
        var reopenTask = dialogs.ShowCompositionEditorAsync(reopened);

        Assert.Single(fake.HostTabs);
        reopened.CancelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(reopenTask.IsCompleted);
    }

    [AvaloniaFact]
    public void Docked_Fleets_PreservesRootNameBindings()
    {
        using var instance = TestClientInstance.Create();
        var fake = new FakeDisplay { IsFloating = false };
        var dialogs = new DialogService();
        dialogs.SetOwner(new Window());
        dialogs.SetHost(fake);

        dialogs.ShowFleets(new FleetsViewModel(instance.Services));

        // The Fleets list-item commands bind via {Binding #FleetsRoot...DataContext.Cmd}. After re-hosting, the
        // transferred NameScope must still resolve #FleetsRoot to the (alive) window whose DataContext is the VM.
        Assert.Single(fake.HostTabs);
        var scope = NameScope.GetNameScope(fake.HostTabs[0].Content);
        Assert.NotNull(scope);
        var root = scope!.Find("FleetsRoot") as Control;
        Assert.NotNull(root);
        Assert.IsType<FleetsViewModel>(root!.DataContext);
    }

    private static (MainWindowViewModel vm, MainWindow window) BuildHostedApp(IServiceProvider services)
    {
        var vm = new MainWindowViewModel(services);
        var window = new MainWindow { DataContext = vm, Width = 1100, Height = 720 };
        var dialogs = (DialogService)services.GetRequiredService<IDialogService>();
        dialogs.SetOwner(window);
        dialogs.SetHost(vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (vm, window);
    }

    [AvaloniaFact]
    public void Docked_RailModule_HostsAsTab_BindsVm_Closeable()
    {
        using var instance = TestClientInstance.Create();
        var (vm, window) = BuildHostedApp(instance.Services);

        vm.LaunchModuleCommand.Execute("logs");    // docked → APP LOGS hosts as a tab
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsHomeShown);
        Assert.Single(vm.HostTabs);
        Assert.Equal("APP LOGS", vm.HostTabs[0].Title);
        Assert.Same(vm.Logs, vm.SelectedHostTab!.Content.DataContext);   // tab content binds the module VM

        window.CaptureRenderedFrame()!.Save(Path.Combine(Path.GetTempPath(), "eveutils-shell-docked-logs.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());

        vm.SelectedHostTab!.CloseCommand.Execute(null);   // close the tab → back to the home
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsHomeShown);
        window.Close();
    }

    [AvaloniaFact]
    public void Rail_Highlight_FollowsSelectedTab_NotHome()
    {
        using var instance = TestClientInstance.Create();
        var (vm, window) = BuildHostedApp(instance.Services);

        Assert.Null(vm.ActiveModule);          // home at startup → no rail item highlighted (the reported bug)
        Assert.False(vm.IsFitsActive);

        vm.LaunchModuleCommand.Execute("logs");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("logs", vm.ActiveModule); // the open module's rail item lights up
        Assert.True(vm.IsLogsActive);
        Assert.False(vm.IsFitsActive);

        vm.SelectedHostTab!.CloseCommand.Execute(null);   // close → back to home → nothing highlighted again
        Dispatcher.UIThread.RunJobs();
        Assert.Null(vm.ActiveModule);
        window.Close();
    }

    [AvaloniaFact]
    public void Docked_MultipleModules_CoexistAsTabs()
    {
        using var instance = TestClientInstance.Create();
        var (vm, window) = BuildHostedApp(instance.Services);

        vm.LaunchModuleCommand.Execute("logs");
        Dispatcher.UIThread.RunJobs();
        vm.LaunchModuleCommand.Execute("esi");
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, vm.HostTabs.Count);                 // both modules open as tabs at once
        vm.LaunchModuleCommand.Execute("logs");             // re-opening selects the existing tab, no duplicate
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, vm.HostTabs.Count);
        window.Close();
    }

    /// <summary>ET-324: with a module open there was no way back to the home — it only showed with no tab at all. HOME
    /// on the rail brings the home to the front, leaves the tab open, and the tab brings the module back. Red before:
    /// the home stayed hidden behind any open tab.</summary>
    [AvaloniaFact]
    public void Docked_Home_ShowsTheHome_KeepsTheTab_AndTheTabGoesBack()
    {
        using var instance = TestClientInstance.Create();
        var (vm, window) = BuildHostedApp(instance.Services);
        vm.LaunchModuleCommand.Execute("logs");
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsHomeShown);

        vm.GoHomeCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsHomeShown);
        Assert.Single(vm.HostTabs);
        Assert.True(vm.HasHostTabs);
        Assert.True(window.GetVisualDescendants().OfType<EveUtils.Client.Views.Home.HomeDashboardView>().Single().IsEffectivelyVisible);

        vm.SelectedHostTab = vm.HostTabs[0];
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsHomeShown);
        Assert.Equal("APP LOGS", vm.SelectedHostTab?.Title);
        window.Close();
    }

    [AvaloniaFact]
    public void Docked_EsiMetrics_DisposesOnTabClose()
    {
        using var instance = TestClientInstance.Create();
        var (vm, window) = BuildHostedApp(instance.Services);

        vm.LaunchModuleCommand.Execute("esi");
        Dispatcher.UIThread.RunJobs();
        var esi = Assert.IsType<EsiMetricsViewModel>(vm.SelectedHostTab!.Content.DataContext);
        Assert.False(esi.IsDisposed);

        vm.SelectedHostTab!.CloseCommand.Execute(null);     // closing the tab disposes its live-timer VM (no leak)
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsHomeShown);
        Assert.True(esi.IsDisposed);
        window.Close();
    }

    [AvaloniaFact]
    public void DockFloat_Toggle_MigratesOpenModule()
    {
        using var instance = TestClientInstance.Create();
        var (vm, window) = BuildHostedApp(instance.Services);

        vm.LaunchModuleCommand.Execute("logs");
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsHomeShown);              // hosted as a tab (docked)

        vm.ToggleDockModeCommand.Execute(null);    // → floating: tab becomes a window, host shows the home
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsFloating);
        Assert.True(vm.IsHomeShown);

        vm.ToggleDockModeCommand.Execute(null);    // → docked: the same module migrates back into a tab (not lost)
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsFloating);
        Assert.False(vm.IsHomeShown);
        Assert.Equal("APP LOGS", vm.HostTabs[0].Title);
        window.Close();
    }

    /// <summary>ET-111 AC1: the top-right button pops only the current tab out — the other open module and the
    /// app's own dock mode are left exactly as they were. Distinct from <see cref="DockFloat_Toggle_MigratesOpenModule"/>,
    /// which is the rail's all-modules switch.</summary>
    [AvaloniaFact]
    public void PopOutTab_DetachesOnlyTheSelectedTab_LeavesDockModeAndOtherModulesUntouched()
    {
        using var instance = TestClientInstance.Create();
        var (vm, window) = BuildHostedApp(instance.Services);

        vm.LaunchModuleCommand.Execute("logs");
        Dispatcher.UIThread.RunJobs();
        vm.LaunchModuleCommand.Execute("esi");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, vm.HostTabs.Count);

        var logsTab = vm.HostTabs.Single(t => t.Title == "APP LOGS");
        vm.SelectedHostTab = logsTab;
        Dispatcher.UIThread.RunJobs();

        vm.PopOutTabCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Single(vm.HostTabs);
        Assert.DoesNotContain(logsTab, vm.HostTabs);
        Assert.Equal("ESI METRICS", vm.HostTabs[0].Title);
        Assert.False(vm.IsFloating);
        window.Close();
    }

    /// <summary>ET-111 AC2: the rail's dock/float switch and the top-right pop-out button must stay tellable
    /// apart by a user, not only by which command they run in code.</summary>
    [AvaloniaFact]
    public void RailAndPopOutButtons_AreDistinctCommandsWithDistinctWordsAndIcons()
    {
        using var instance = TestClientInstance.Create();
        var (vm, window) = BuildHostedApp(instance.Services);

        var railButton = window.FindControl<Button>("RailDockToggleButton");
        var popOutButton = window.FindControl<Button>("PopOutTabButton");
        Assert.NotNull(railButton);
        Assert.NotNull(popOutButton);

        Assert.NotSame(railButton!.Command, popOutButton!.Command);
        Assert.NotEqual(ToolTip.GetTip(railButton), ToolTip.GetTip(popOutButton));

        var railIcon = railButton.GetVisualDescendants().OfType<MaterialIcon>().Single();
        var popOutIcon = popOutButton.GetVisualDescendants().OfType<MaterialIcon>().Single();
        Assert.NotEqual(railIcon.Kind, popOutIcon.Kind);
        window.Close();
    }

    /// <summary>ET-111 AC4 (the way back): the window's own DOCK button, wired through <see
    /// cref="IHostableModuleWindow.DockRequested"/>, returns a detached frame to the tab strip, selected.</summary>
    [AvaloniaFact]
    public void DockRequested_ReturnsTheDetachedFrame_AsTheSelectedTab()
    {
        var fake = new FakeDisplay { IsFloating = false };
        var host = new ModuleHostService();
        host.SetOwner(new Window());
        host.SetHost(fake);

        var window = new HostableWindow { Content = new Border() };
        host.Open(window, "APP LOGS", "logs", "logs");
        var tab = fake.HostTabs[0];

        host.PopOut(tab);
        Assert.Empty(fake.HostTabs);

        window.DockRequested!();

        Assert.Single(fake.HostTabs);
        Assert.Same(tab, fake.SelectedHostTab);
    }

    /// <summary>ET-111 AC5: no sequence of pop-out/dock/float actions leaves an open module neither a tab nor a
    /// visible window. Toets on <see cref="ModuleHostService"/>'s own counts, not on window activation — headless
    /// Avalonia keeps Windows empty (see ET-111's ticket description).</summary>
    [AvaloniaFact]
    public void PopOutDockFloatSequence_NeverLosesAnOpenModule()
    {
        var fake = new FakeDisplay { IsFloating = false };
        var host = new ModuleHostService();
        host.SetOwner(new Window());
        host.SetHost(fake);

        var logs = new HostableWindow { Content = new Border() };
        var esi = new HostableWindow { Content = new Border() };
        var inbox = new HostableWindow { Content = new Border() };
        host.Open(logs, "APP LOGS", "logs", "logs");
        host.Open(esi, "ESI METRICS", "esi", "esi-metrics");
        host.Open(inbox, "INBOX", "inbox", "inbox");
        const int openModules = 3;
        Assert.Equal(openModules, fake.HostTabs.Count + host.FloatingWindowCount);

        host.PopOut(fake.HostTabs.Single(t => t.Title == "ESI METRICS"));   // pop out the middle tab
        Assert.Equal(openModules, fake.HostTabs.Count + host.FloatingWindowCount);

        // Closing an unrelated docked tab must not strand the remaining one with nothing selected: Dismiss's own
        // "neighbour" pick can land on the just-detached module, which must not swallow the tab selection.
        fake.HostTabs.Single(t => t.Title == "APP LOGS").CloseCommand.Execute(null);
        Assert.Equal(openModules - 1, fake.HostTabs.Count + host.FloatingWindowCount);
        Assert.Same(fake.HostTabs.Single(t => t.Title == "INBOX"), fake.SelectedHostTab);

        fake.IsFloating = true;
        host.SwitchMode();                                               // rail: everything floats
        Assert.Equal(openModules - 1, fake.HostTabs.Count + host.FloatingWindowCount);

        fake.IsFloating = false;
        host.SwitchMode();                                               // rail: everything docks again
        Assert.Equal(openModules - 1, fake.HostTabs.Count);              // Detached cleared: both are tabs

        host.PopOut(fake.HostTabs.Single(t => t.Title == "ESI METRICS"));   // pop out again
        Assert.Equal(openModules - 1, fake.HostTabs.Count + host.FloatingWindowCount);

        esi.DockRequested!();                                            // ...and dock it back from its own window
        Assert.Equal(openModules - 1, fake.HostTabs.Count);

        host.PopOut(fake.HostTabs.Single(t => t.Title == "ESI METRICS"));
        host.PopOut(fake.HostTabs.Single(t => t.Title == "INBOX"));      // pop the last remaining tab out
        Assert.Empty(fake.HostTabs);                                     // docked host goes empty — the home shows
        Assert.Equal(openModules - 1, host.FloatingWindowCount);

        host.CloseFloatingWindows();
        Assert.Equal(0, fake.HostTabs.Count + host.FloatingWindowCount);
    }

    [AvaloniaFact]
    public async Task Docked_RailFits_HostsBrowser_WithSeededFits()
    {
        using var instance = TestClientInstance.Create();
        await SeedFitsAsync(instance.Services, ("Rifter — Kite", 587), ("Thanatos — Ratting", 23911));
        var (vm, window) = BuildHostedApp(instance.Services);

        Assert.True(await WaitForAsync(() => vm.Fittings.Count >= 2), $"home Fittings = {vm.Fittings.Count}");

        vm.LaunchModuleCommand.Execute("fits");    // rail FITS hosts the full browser (parity with floating)
        Assert.True(await WaitForAsync(() => vm.SelectedHostTab?.Content.DataContext is FitBrowserViewModel),
            "rail FITS did not host the browser");

        var browser = (FitBrowserViewModel)vm.SelectedHostTab!.Content.DataContext!;
        await browser.Tabs[0].EnsureLoadedAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.True(browser.Tabs[0].TotalCount >= 2, $"hosted browser Local rows = {browser.Tabs[0].TotalCount}");
        window.CaptureRenderedFrame()!.Save(Path.Combine(Path.GetTempPath(), "eveutils-shell-docked-browser.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Docked_RailCompositions_HostsLibrary()
    {
        using var instance = TestClientInstance.Create();
        var (vm, window) = BuildHostedApp(instance.Services);

        vm.LaunchModuleCommand.Execute("compositions");   // rail COMP hosts the compositions library
        Assert.True(await WaitForAsync(() => vm.SelectedHostTab?.Content.DataContext is CompositionsViewModel),
            "rail COMP did not host the compositions library");
        Assert.Equal("compositions", vm.ActiveModule);    // the COMP rail item lights up
        Assert.True(vm.IsCompositionsActive);

        window.CaptureRenderedFrame()!.Save(Path.Combine(Path.GetTempPath(), "eveutils-shell-docked-compositions.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        window.Close();
    }

    /// <summary>
    /// The fit browser is one module for the whole app (ET-48, same pattern as the per-fleet roster/metrics fix in
    /// ET-46): re-opening FITS while it is already open must re-select the standing instance — not stack a second
    /// tab — and must refresh it, so a fit imported through another path while the browser stood open is not stuck
    /// missing until restart.
    /// </summary>
    [AvaloniaFact]
    public async Task Docked_ReopeningFits_RefreshesTheStandingBrowser_WithoutDuplicateTab()
    {
        using var instance = TestClientInstance.Create();
        await SeedFitsAsync(instance.Services, ("Rifter — Kite", 587));
        var (vm, window) = BuildHostedApp(instance.Services);

        vm.LaunchModuleCommand.Execute("fits");
        Assert.True(await WaitForAsync(() => vm.SelectedHostTab?.Content.DataContext is FitBrowserViewModel),
            "rail FITS did not host the browser");
        var browser = (FitBrowserViewModel)vm.SelectedHostTab!.Content.DataContext!;
        await browser.Tabs[0].EnsureLoadedAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, browser.Tabs[0].TotalCount);

        // A fit lands in the local library through another path (e.g. an import elsewhere) while the browser
        // stays open — the standing instance's own reload hooks never fire for this, only a re-open can.
        var repo = instance.Services.GetRequiredService<IFittingRepository>();
        await repo.UpsertAsync(new LocalFitting
        {
            OwnerId = "0", EsiFittingId = 2, Name = "Thanatos — Ratting", ShipTypeId = 23911,
            RawJson = JsonSerializer.Serialize(new EsiFitting(0, "Thanatos — Ratting", "", 23911, new List<EsiFittingItem>())),
            ContentHash = "hash-2", ImportedAt = DateTimeOffset.UtcNow
        });

        vm.LaunchModuleCommand.Execute("fits");   // re-open → re-select, not a duplicate
        Assert.True(await WaitForAsync(() => browser.Tabs[0].TotalCount >= 2),
            $"local rows after reopen = {browser.Tabs[0].TotalCount}");
        Assert.Single(vm.HostTabs);
        Assert.Same(browser, vm.SelectedHostTab!.Content.DataContext);   // same instance, not rebuilt
        window.Close();
    }

    /// <summary>Same fix, same reasoning, for the compositions library (ET-48).</summary>
    [AvaloniaFact]
    public async Task Docked_ReopeningCompositions_RefreshesTheStandingLibrary_WithoutDuplicateTab()
    {
        using var instance = TestClientInstance.Create();
        const int owner = 95400001;
        await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character("Pilot One", owner));
        var (vm, window) = BuildHostedApp(instance.Services);

        vm.LaunchModuleCommand.Execute("compositions");
        Assert.True(await WaitForAsync(() => vm.SelectedHostTab?.Content.DataContext is CompositionsViewModel),
            "rail COMP did not host the compositions library");
        var library = (CompositionsViewModel)vm.SelectedHostTab!.Content.DataContext!;
        Assert.True(await WaitForAsync(() => library.SelectedTab is not null));
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(library.SelectedTab!.Compositions);

        // A composition created through another path while the library stays open — same gap as the fit above.
        var compositionRepo = instance.Services.GetRequiredService<IFleetCompositionRepository>();
        var now = DateTimeOffset.UtcNow;
        await compositionRepo.AddAsync(new FleetComposition
        {
            Name = "Armor doctrine", OwnerCharacterId = owner, IsClientOnly = true, CreatedAt = now, UpdatedAt = now
        });

        vm.LaunchModuleCommand.Execute("compositions");   // re-open → re-select, not a duplicate
        Assert.True(await WaitForAsync(() => library.SelectedTab!.Compositions.Any(c => c.Name == "Armor doctrine")),
            "reopened library did not show the composition created while it stood open");
        Assert.Single(vm.HostTabs);
        Assert.Same(library, vm.SelectedHostTab!.Content.DataContext);   // same instance, not rebuilt
        window.Close();
    }

    [AvaloniaFact]
    public async Task ShellState_CollapsePersists_AcrossRestart()
    {
        using var instance = TestClientInstance.Create();
        await instance.Services.GetRequiredService<ISettingRepository>().UpsertAsync("ui.chars-collapsed", "true");

        var vm = new MainWindowViewModel(instance.Services);   // restores shell prefs in its load chain
        Assert.True(await WaitForAsync(() => vm.IsCharsCollapsed), "collapsed character column was not restored");
    }

    private static async Task SeedFitsAsync(IServiceProvider services, params (string Name, int Ship)[] fits)
    {
        var repo = services.GetRequiredService<IFittingRepository>();
        var id = 1;
        foreach (var (name, ship) in fits)
            await repo.UpsertAsync(new LocalFitting
            {
                OwnerId = "0", EsiFittingId = id, Name = name, ShipTypeId = ship,
                RawJson = JsonSerializer.Serialize(new EsiFitting(0, name, "", ship, new List<EsiFittingItem>())),
                ContentHash = "hash-" + id++, ImportedAt = DateTimeOffset.UtcNow
            });
    }
}
