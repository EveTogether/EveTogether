using System.Collections.Generic;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Ctrl+Shift+T (ET-209): <see cref="ModuleHostService"/> signals a module's id whenever it closes, docked or
/// floating, and <see cref="MainWindowViewModel"/> uses that to reopen the most recently closed one — but only for
/// the no-argument, single-instance rail modules (fits, compositions, ESI metrics, …), since a per-entity module
/// (a specific fleet's roster, a fit detail, an activity) has no single "the one to reopen" without fabricating
/// context it never had.
/// </summary>
public class ReopenLastClosedTabTests
{
    private sealed class FakeDisplay : IModuleHostDisplay
    {
        public bool IsFloating { get; set; }
        public ObservableCollection<HostTab> HostTabs { get; } = new();
        public HostTab? SelectedHostTab { get; set; }
    }

    [AvaloniaFact]
    public void Dismiss_RaisesModuleClosed_WithTheClosedModulesId()
    {
        var fake = new FakeDisplay();
        var host = new ModuleHostService();
        host.SetOwner(new Window());
        host.SetHost(fake);

        string? closedId = null;
        host.ModuleClosed += id => closedId = id;

        host.Open(new Window { Content = new Border() }, "APP LOGS", "logs", "app-logs");
        Assert.Single(fake.HostTabs);

        fake.HostTabs[0].CloseCommand.Execute(null);   // the tab's own X

        Assert.Equal("app-logs", closedId);
        Assert.Empty(fake.HostTabs);
    }

    [AvaloniaFact]
    public void WindowClosedDirectly_AlsoRaisesModuleClosed()
    {
        var fake = new FakeDisplay { IsFloating = true };
        var owner = new Window { Width = 400, Height = 300 };
        owner.Show();
        var host = new ModuleHostService();
        host.SetOwner(owner);
        host.SetHost(fake);

        string? closedId = null;
        host.ModuleClosed += id => closedId = id;

        var window = new Window { Content = new Border() };
        host.Open(window, "APP LOGS", "logs", "app-logs");
        window.Close();   // the floating window's own chrome X — not Dismiss()

        Assert.Equal("app-logs", closedId);
        owner.Close();
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
    public void ReopenLastClosedTab_ReopensTheModuleThatClosedMostRecently()
    {
        using var instance = TestClientInstance.Create();
        var (vm, window) = BuildHostedApp(instance.Services);

        vm.LaunchModuleCommand.Execute("logs");
        Dispatcher.UIThread.RunJobs();
        Assert.Single(vm.HostTabs);

        vm.SelectedHostTab!.CloseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsHomeShown);

        vm.ReopenLastClosedTabCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsHomeShown);
        Assert.Equal("APP LOGS", vm.HostTabs[0].Title);
        window.Close();
    }

    [AvaloniaFact]
    public void ReopenLastClosedTab_WithNothingClosed_DoesNothing()
    {
        using var instance = TestClientInstance.Create();
        var (vm, window) = BuildHostedApp(instance.Services);

        vm.ReopenLastClosedTabCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsHomeShown);
        window.Close();
    }

    [AvaloniaFact]
    public void ReopenLastClosedTab_ClosingTheSameModuleTwice_DoesNotDuplicateTheStackEntry()
    {
        using var instance = TestClientInstance.Create();
        var (vm, window) = BuildHostedApp(instance.Services);

        // Open+close "logs" twice, then "esi" once — the stack should read [logs, esi] (de-duped), so reopening
        // twice brings back esi then logs, not logs twice.
        vm.LaunchModuleCommand.Execute("logs");
        Dispatcher.UIThread.RunJobs();
        vm.SelectedHostTab!.CloseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        vm.LaunchModuleCommand.Execute("logs");
        Dispatcher.UIThread.RunJobs();
        vm.SelectedHostTab!.CloseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        vm.LaunchModuleCommand.Execute("esi");
        Dispatcher.UIThread.RunJobs();
        vm.SelectedHostTab!.CloseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        // The stack now reads [logs, esi] (de-duped — logs appears once despite closing twice). Reopening pops esi
        // first, leaving logs for a second reopen — without closing esi again, which would just push it back on.
        vm.ReopenLastClosedTabCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("ESI METRICS", vm.HostTabs[0].Title);

        vm.ReopenLastClosedTabCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, vm.HostTabs.Count);
        Assert.Equal("APP LOGS", vm.HostTabs[1].Title);
        window.Close();
    }
}
