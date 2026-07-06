using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.FitBrowser;
using EveUtils.Client.Views;
using EveUtils.Shared.Modules.Fleet.Composition.Repositories;
using EveUtils.Shared.Modules.Fittings.Dtos;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.Settings.Repositories;
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
        host.Open(Roster(), "FLEET ROSTER", "fleet", moduleId: "fleet-roster:1");
        host.Open(Roster(), "FLEET ROSTER", "fleet", moduleId: "fleet-roster:2");
        Assert.Equal(2, fake.HostTabs.Count);

        // Re-opening fleet 1 re-selects its existing tab — no third tab.
        host.Open(Roster(), "FLEET ROSTER", "fleet", moduleId: "fleet-roster:1");
        Assert.Equal(2, fake.HostTabs.Count);
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
    public void Docked_CompositionEditor_HostsAsTab_AndCancelDismisses()
    {
        using var instance = TestClientInstance.Create();
        var fake = new FakeDisplay { IsFloating = false };
        var dialogs = new DialogService();
        dialogs.SetOwner(new Window());            // docked never shows it
        dialogs.SetHost(fake);

        var client = new LocalFleetCompositionClient(
            instance.Services.GetRequiredService<ClientFleetService>(),
            instance.Services.GetRequiredService<IFleetCompositionRepository>(), 95400001);
        var editor = CompositionEditorViewModel.ForNew(instance.Services, client);

        // The editor opens as a docked tab (a hosted module), not a modal dialog window.
        var task = dialogs.ShowCompositionEditorAsync(editor);
        Assert.Single(fake.HostTabs);
        Assert.Equal("compositions", fake.HostTabs[0].ModuleKey);
        Assert.NotNull(fake.HostTabs[0].Content);
        Assert.False(task.IsCompleted);            // stays open until the editor closes

        // Cancel raises CloseRequested(false) → the host dismisses the tab and the task resolves false (no reload).
        editor.CancelCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(task.IsCompleted);
        Assert.False(task.Result);
        Assert.Empty(fake.HostTabs);
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

        window.CaptureRenderedFrame()!.Save(Path.Combine(Path.GetTempPath(), "eveutils-shell-docked-logs.png"));

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
        window.CaptureRenderedFrame()!.Save(Path.Combine(Path.GetTempPath(), "eveutils-shell-docked-browser.png"));
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

        window.CaptureRenderedFrame()!.Save(Path.Combine(Path.GetTempPath(), "eveutils-shell-docked-compositions.png"));
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
