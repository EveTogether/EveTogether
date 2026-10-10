using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveUtils.Client.Dialogs;
using EveUtils.Client.ViewModels;
using EveUtils.Client.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class LogsGroupTests
{
    private static (MainWindowViewModel Vm, MainWindow Window) _BuildShell(IServiceProvider services)
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

    private static MenuItem[] _OpenGroup(MainWindow window)
    {
        var groupItem = window.FindControl<Button>("LogsRailItem") ?? throw new InvalidOperationException("LogsRailItem missing");
        groupItem.Flyout!.ShowAt(groupItem);
        Dispatcher.UIThread.RunJobs();
        return ((MenuFlyout)groupItem.Flyout).Items.OfType<MenuItem>().ToArray();
    }

    [AvaloniaFact]
    public void Rail_LogsGroup_ReplacesEsiInboxAndLogsWithOneItem()
    {
        using var instance = TestClientInstance.Create();
        var (_, window) = _BuildShell(instance.Services);

        var railItems = window.FindControl<ScrollViewer>("RailLauncherScroller")!
            .GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("railitem")).ToArray();

        Assert.Equal(12, railItems.Length);
        Assert.Equal(1, railItems.Count(b => b.Name == "LogsRailItem"));
        Assert.Equal(["ESI metrics", "Inbox", "Game logs", "Client logs"],
            _OpenGroup(window).Select(i => i.Header as string ?? "Inbox").ToArray());
        window.Close();
    }

    [AvaloniaTheory]
    [InlineData("esi")]
    [InlineData("inbox")]
    [InlineData("logs")]
    [InlineData("gamelogs")]
    public void Flyout_Item_OpensItsModule_AndLightsTheGroup(string railId)
    {
        using var instance = TestClientInstance.Create();
        var (vm, window) = _BuildShell(instance.Services);
        Assert.False(vm.IsLogsGroupActive);

        var item = _OpenGroup(window).Single(i => (string?)i.CommandParameter == railId);
        item.Command!.Execute(item.CommandParameter);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(railId, vm.ActiveModule);
        Assert.Equal(railId, vm.SelectedHostTab!.ModuleKey);
        Assert.True(vm.IsLogsGroupActive);

        vm.SelectedHostTab!.CloseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsLogsGroupActive);
        window.Close();
    }

    [AvaloniaFact]
    public void Badge_InboxUnread_IsShownOnGroupItemAndInMenu_AndHiddenWhenRead()
    {
        using var instance = TestClientInstance.Create();
        var (vm, window) = _BuildShell(instance.Services);
        var groupBadge = window.FindControl<Border>("LogsRailBadge")!;
        Assert.False(groupBadge.IsVisible);

        vm.Inbox.UnreadCount = 3;
        Dispatcher.UIThread.RunJobs();
        Assert.True(groupBadge.IsVisible);
        Assert.Equal("3", groupBadge.GetVisualDescendants().OfType<TextBlock>().Single().Text);

        var inboxEntry = _OpenGroup(window).Single(i => (string?)i.CommandParameter == "inbox");
        var menuBadge = inboxEntry.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "InboxMenuBadge");
        Assert.True(menuBadge.IsVisible);

        vm.Inbox.UnreadCount = 0;
        Dispatcher.UIThread.RunJobs();
        Assert.False(groupBadge.IsVisible);
        Assert.False(menuBadge.IsVisible);
        window.Close();
    }
}
