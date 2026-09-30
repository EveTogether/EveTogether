using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using EveUtils.Client.Controls.Map;
using EveUtils.Client.ViewModels.Map;
using EveUtils.Client.WorldMap;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Modules.Map.Enums;
using EveUtils.Shared.Modules.Settings.Queries;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using World = EveUtils.Client.UiTests.MapPopOutTests.World;

namespace EveUtils.Client.UiTests;

/// <summary>ET-397: the map's side panel folds away to a strip. The map takes the width, the followed system stays in the
/// middle of it, following goes on, and the MAP tab and the popped-out window each remember their own choice.</summary>
public sealed class MapPanelCollapseTests
{
    [AvaloniaFact]
    public async Task Hiding_LeavesAStripWithTheShowButton_AndTheMapTakesTheFreedWidth()
    {
        using var world = await World.OpenAsync();
        (MapViewModel map, StarMapControl view) = world.OnTheTab();
        await world.SetUpStateAsync(map);
        world.Settle();
        Control tab = _Tab(world);
        Border panel = _Panel(tab);
        Button toggle = _Toggle(tab);
        double panelBefore = panel.Bounds.Width;
        double mapBefore = view.Bounds.Width;

        Assert.Equal(MapViewModel.ExpandedPanelWidth, panelBefore);
        Assert.Equal("Hide panel", ToolTip.GetTip(toggle));

        await map.TogglePanelCommand.ExecuteAsync(null);
        world.Settle();

        Assert.Equal(MapViewModel.CollapsedPanelWidth, panel.Bounds.Width);
        Assert.Equal(mapBefore + panelBefore - MapViewModel.CollapsedPanelWidth, view.Bounds.Width, 0.5);
        Assert.Equal("Show panel", ToolTip.GetTip(toggle));
        Assert.True(toggle.IsEffectivelyVisible);
        Assert.True(toggle.Bounds.Right <= panel.Bounds.Width);
        Assert.False(tab.GetVisualDescendants().OfType<ScrollViewer>().First(scroll => scroll.Parent == panel.Child).IsVisible);
    }

    [AvaloniaFact]
    public async Task Toggling_KeepsTheFollowedSystemCentred_AndFollowingOn()
    {
        using var world = await World.OpenAsync();
        (MapViewModel map, StarMapControl view) = world.OnTheTab();
        await world.FollowOwnCharacterAsync(map);
        world.Settle();
        Assert.Equal(view.Bounds.Width / 2, view.ScreenPointOf(map.FollowIndex).X, 1);

        foreach (bool collapsed in new[] { true, false })
        {
            await map.TogglePanelCommand.ExecuteAsync(null);
            world.Settle();

            Point at = view.ScreenPointOf(map.FollowIndex);
            Assert.Equal(collapsed, map.IsPanelCollapsed);
            Assert.Equal(view.Bounds.Width / 2, at.X, 1);
            Assert.Equal(view.Bounds.Height / 2, at.Y, 1);
            Assert.True(map.IsFollowingCharacter);
            Assert.False(map.IsFollowPaused);
            Assert.Equal(MapFollowMode.Character, map.FollowMode);
        }
    }

    [AvaloniaFact]
    public async Task Hiding_IsRememberedForANewMap()
    {
        using var world = await World.OpenAsync();
        (MapViewModel map, _) = world.OnTheTab();
        await map.TogglePanelCommand.ExecuteAsync(null);

        MapViewModel reopened = world.Instance.Services.GetRequiredService<IMapLauncher>().Create();
        await reopened.LoadAsync();

        Assert.True(reopened.IsPanelCollapsed);
        Assert.Equal(MapViewModel.CollapsedPanelWidth, reopened.PanelWidth);
        reopened.Dispose();
    }

    [AvaloniaFact]
    public async Task TheTabAndThePoppedOutWindow_EachKeepTheirOwnChoice()
    {
        using var world = await World.OpenAsync();
        (MapViewModel map, _) = world.OnTheTab();
        await map.TogglePanelCommand.ExecuteAsync(null);
        Assert.True(map.IsPanelCollapsed);

        map.PopOutCommand.Execute(null);
        world.Settle();
        Assert.False(map.IsPanelCollapsed);
        Assert.Equal(MapViewModel.ExpandedPanelWidth, _Panel((Control)world.Popout!.Content!).Bounds.Width);

        await map.TogglePanelCommand.ExecuteAsync(null);
        map.PutBackCommand.Execute(null);
        world.Settle();
        Assert.True(map.IsPanelCollapsed);

        await map.TogglePanelCommand.ExecuteAsync(null);
        Assert.False(map.IsPanelCollapsed);

        IReadOnlyList<Shared.Modules.Settings.Dtos.SettingDto> settings =
            await world.Instance.Services.GetRequiredService<IDispatcher>().Query(new GetSettingsQuery());
        Assert.Equal("false", settings.Single(setting => setting.Key == MapViewModel.PanelCollapsedInTabSettingKey).Value);
        Assert.Equal("true", settings.Single(setting => setting.Key == MapViewModel.PanelCollapsedInWindowSettingKey).Value);
    }

    [AvaloniaFact]
    public async Task TheToggleButton_SitsAtTheTopOfThePanel_AndIsWiredToTheCommand()
    {
        using var world = await World.OpenAsync();
        (MapViewModel map, _) = world.OnTheTab();
        Control tab = _Tab(world);

        Button toggle = _Toggle(tab);

        Assert.Same(map.TogglePanelCommand, toggle.Command);
        Assert.Equal(0, toggle.TranslatePoint(new Point(0, 0), _Panel(tab))?.Y);
    }

    private static Control _Tab(World world) =>
        world.Shell.SelectedHostTab?.Content ?? throw new InvalidOperationException("the MAP tab is not open");

    private static Border _Panel(Control root) =>
        root.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "SidePanel");

    private static Button _Toggle(Control root) =>
        root.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PanelToggleButton");
}
