using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using EveUtils.Client.Controls.Map;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.Theming;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.Map;
using EveUtils.Client.Views;
using EveUtils.Client.WorldMap;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Map.Enums;
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using UiDispatcher = Avalonia.Threading.Dispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>ET-396: the map in a window of its own. The same view and view model move — nothing is rebuilt — so follow,
/// trail, route and zoom stay; the MAP tab holds a placeholder meanwhile; putting it back or closing the window returns
/// the map to the tab; the rail and Ctrl+M (both LaunchModule "map") raise the window while it is out. Driven through the
/// real shell, module host and dialog service.</summary>
public sealed class MapPopOutTests
{
    private const int Jita = 30000142;
    private const int Amarr = 30002187;
    private const int Own = 91000001;
    private const string OwnName = "Kaelen Voss";

    [AvaloniaFact]
    public async Task PopOut_MovesTheSameViewAndViewModelIntoItsOwnWindow_WithFollowTrailRouteAndZoomKept()
    {
        using var world = await World.OpenAsync();
        (MapViewModel map, StarMapControl view) = world.OnTheTab();
        await world.SetUpStateAsync(map);
        world.Settle();
        double zoom = view.ZoomLevel;
        string route = map.RouteJumpsText;

        map.PopOutCommand.Execute(null);
        world.Settle();

        Window popout = world.Popout ?? throw new InvalidOperationException("the map has no window of its own");
        var content = Assert.IsAssignableFrom<Control>(popout.Content);
        Assert.Same(map, content.DataContext);
        Assert.Same(view, content.GetVisualDescendants().OfType<StarMapControl>().Single());
        Assert.True(zoom > 1 && view.ZoomLevel > 1, $"the view was zoomed in ({zoom}) and is not back at the whole map ({view.ZoomLevel})");
        Assert.Equal("Map", popout.Title);
        Assert.IsAssignableFrom<ChromedWindow>(popout);
        Assert.True(map.IsPoppedOut);
        Assert.False(map.CanPopOut);

        Assert.Equal(MapFollowMode.Character, map.FollowMode);
        Assert.True(map.IsFollowingCharacter);
        Assert.Equal(20, map.TrailJumps);
        Assert.NotNull(map.Trail);
        Assert.Equal(route, map.RouteJumpsText);
        Assert.True(map.HasRoute);
        Assert.False(world.Shell.IsFloating);
    }

    [AvaloniaFact]
    public async Task WhilePoppedOut_TheTabShowsThePlaceholder_AndTheWindowOffersPutBack()
    {
        using var world = await World.OpenAsync();
        (MapViewModel map, _) = world.OnTheTab();
        map.PopOutCommand.Execute(null);
        world.Settle();

        Assert.Single(world.Shell.HostTabs);
        Assert.Equal("MAP", world.Shell.SelectedHostTab?.Title);
        var placeholder = Assert.IsType<MapPoppedOutPlaceholder>(world.Shell.SelectedHostTab?.Content);
        Assert.Same(map, placeholder.DataContext);
        Assert.Equal("The map is open in its own window", placeholder.FindControl<TextBlock>("Title")?.Text);
        Assert.NotNull(placeholder.FindControl<Button>("PutBackHereButton"));
        Assert.NotNull(placeholder.FindControl<Button>("ShowWindowButton"));

        Control content = Assert.IsAssignableFrom<Control>(world.Popout?.Content);
        Assert.True(content.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PutBackButton").IsVisible);
        Assert.False(content.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PopOutButton").IsVisible);
    }

    [AvaloniaFact]
    public async Task ThePopOutButton_IsOnTheTabsHeader_UntilTheMapIsOut()
    {
        using var world = await World.OpenAsync();
        (MapViewModel map, _) = world.OnTheTab();
        Control tab = Assert.IsAssignableFrom<Control>(world.Shell.SelectedHostTab?.Content);

        Button popOut = tab.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PopOutButton");
        Assert.True(popOut.IsVisible);
        Assert.Same(map.PopOutCommand, popOut.Command);
        Assert.False(tab.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PutBackButton").IsVisible);
    }

    [AvaloniaFact]
    public async Task PutItBackHere_ReturnsTheSameMapToTheTab_WithItsStateKept_AndClosesTheWindow()
    {
        using var world = await World.OpenAsync();
        (MapViewModel map, StarMapControl view) = world.OnTheTab();
        await world.SetUpStateAsync(map);
        map.PopOutCommand.Execute(null);
        world.Settle();
        Window popout = world.Popout ?? throw new InvalidOperationException("no window");
        var closed = false;
        popout.Closed += (_, _) => closed = true;

        world.PlaceholderButton("PutBackHereButton").Command?.Execute(null);
        world.Settle();

        Assert.True(closed);
        Assert.Null(world.Popout);
        Assert.False(map.IsPoppedOut);
        Assert.True(map.CanPopOut);
        Control tab = Assert.IsAssignableFrom<Control>(world.Shell.SelectedHostTab?.Content);
        Assert.IsNotType<MapPoppedOutPlaceholder>(tab);
        Assert.Same(map, tab.DataContext);
        Assert.Same(view, tab.GetVisualDescendants().OfType<StarMapControl>().Single());
        Assert.True(map.IsFollowingCharacter);
        Assert.True(map.HasRoute);
        Assert.Equal(20, map.TrailJumps);
    }

    [AvaloniaFact]
    public async Task ClosingTheWindow_ReturnsTheMapToTheTab_LikePutBack()
    {
        using var world = await World.OpenAsync();
        (MapViewModel map, _) = world.OnTheTab();
        await world.SetUpStateAsync(map);
        map.PopOutCommand.Execute(null);
        world.Settle();

        world.Popout?.Close();
        world.Settle();

        Assert.Null(world.Popout);
        Assert.False(map.IsPoppedOut);
        Assert.Same(map, world.Shell.SelectedHostTab?.Content.DataContext);
        Assert.IsNotType<MapPoppedOutPlaceholder>(world.Shell.SelectedHostTab?.Content);
        Assert.True(map.IsFollowingCharacter);
        Assert.Single(world.Shell.HostTabs);
    }

    [AvaloniaFact]
    public async Task PutBackInMainWindow_InTheWindowsHeader_DoesTheSame()
    {
        using var world = await World.OpenAsync();
        (MapViewModel map, _) = world.OnTheTab();
        map.PopOutCommand.Execute(null);
        world.Settle();
        Window popout = world.Popout ?? throw new InvalidOperationException("no window");
        Button putBack = ((Control)popout.Content!).GetVisualDescendants().OfType<Button>().Single(button => button.Name == "PutBackButton");

        putBack.Command?.Execute(null);
        world.Settle();

        Assert.Null(world.Popout);
        Assert.IsNotType<MapPoppedOutPlaceholder>(world.Shell.SelectedHostTab?.Content);
        Assert.False(map.IsPoppedOut);
    }

    [AvaloniaFact]
    public async Task TheRailAndTheShortcut_RaiseTheWindowWhileTheMapIsOut_InsteadOfOpeningASecondMap()
    {
        using var world = await World.OpenAsync();
        (MapViewModel map, _) = world.OnTheTab();
        map.PopOutCommand.Execute(null);
        world.Settle();
        Window popout = world.Popout ?? throw new InvalidOperationException("no window");
        var opened = 0;
        world.Instance.Services.GetRequiredService<IMapLauncher>().MapOpened += _ => opened++;

        popout.WindowState = WindowState.Minimized;
        await world.Shell.LaunchModuleCommand.ExecuteAsync("map");
        world.Settle();

        Assert.Equal(WindowState.Normal, popout.WindowState);
        Assert.Equal(0, opened);
        Assert.Single(world.Shell.HostTabs);
        Assert.Same(map, world.Shell.SelectedHostTab?.Content.DataContext);
        Assert.Same(popout, world.Popout);
    }

    [AvaloniaFact]
    public async Task ShowTheWindow_RaisesTheWindow()
    {
        using var world = await World.OpenAsync();
        (MapViewModel map, _) = world.OnTheTab();
        map.PopOutCommand.Execute(null);
        world.Settle();
        Window popout = world.Popout ?? throw new InvalidOperationException("no window");

        popout.WindowState = WindowState.Minimized;
        world.PlaceholderButton("ShowWindowButton").Command?.Execute(null);

        Assert.Equal(WindowState.Normal, popout.WindowState);
    }

    [AvaloniaFact]
    public async Task PoppingOutTwice_KeepsTheOneWindow()
    {
        using var world = await World.OpenAsync();
        (MapViewModel map, _) = world.OnTheTab();
        map.PopOutCommand.Execute(null);
        Window? first = world.Popout;

        world.Dialogs.PopOutMap();

        Assert.NotNull(first);
        Assert.Same(first, world.Popout);
    }

    [AvaloniaFact]
    public async Task SwitchingToFloating_PutsAPoppedOutMapBackIntoItsOwnModuleWindow()
    {
        using var world = await World.OpenAsync();
        (MapViewModel map, _) = world.OnTheTab();
        map.PopOutCommand.Execute(null);
        world.Settle();

        world.Shell.ToggleDockModeCommand.Execute(null);
        world.Dialogs.SwitchMode();
        world.Settle();

        Assert.True(world.Shell.IsFloating);
        Assert.Null(world.Popout);
        Assert.False(map.IsPoppedOut);
        Assert.False(map.CanPopOut);
    }

    [AvaloniaFact]
    public async Task ClosingTheTabWhilePoppedOut_ClosesTheWindow_AndDisposesTheMap()
    {
        using var world = await World.OpenAsync();
        (MapViewModel map, _) = world.OnTheTab();
        map.PopOutCommand.Execute(null);
        world.Settle();
        Window popout = world.Popout ?? throw new InvalidOperationException("no window");
        var closed = false;
        popout.Closed += (_, _) => closed = true;

        world.Shell.SelectedHostTab?.CloseCommand.Execute(null);
        world.Settle();

        Assert.True(closed);
        Assert.Empty(world.Shell.HostTabs);
        Assert.Null(world.Popout);
    }

    private sealed class World : IDisposable
    {
        private readonly MainWindow _window;
        private int _seconds;

        private World(TestClientInstance instance, MainWindowViewModel shell, DialogService dialogs, MainWindow window)
        {
            Instance = instance;
            Shell = shell;
            Dialogs = dialogs;
            _window = window;
        }

        public TestClientInstance Instance { get; }

        public MainWindowViewModel Shell { get; }

        public DialogService Dialogs { get; }

        public Window? Popout => Dialogs.MapPopoutWindow;

        public static async Task<World> OpenAsync()
        {
            TestClientInstance instance = TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(MapFixture.Sde()));
            await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character(OwnName, Own));
            instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Gallente);
            var shell = new MainWindowViewModel(instance.Services);
            var window = new MainWindow { DataContext = shell, Width = 1280, Height = 800 };
            var dialogs = (DialogService)instance.Services.GetRequiredService<IDialogService>();
            dialogs.SetOwner(window);
            dialogs.SetHost(shell);
            window.Show();
            UiDispatcher.UIThread.RunJobs();
            return new World(instance, shell, dialogs, window);
        }

        public (MapViewModel Map, StarMapControl View) OnTheTab()
        {
            Shell.LaunchModuleCommand.Execute("map");
            UiDispatcher.UIThread.RunJobs();
            Control tab = Shell.SelectedHostTab?.Content ?? throw new InvalidOperationException("the MAP tab did not open");
            var map = Assert.IsType<MapViewModel>(tab.DataContext);
            return (map, tab.GetVisualDescendants().OfType<StarMapControl>().Single());
        }

        public async Task SetUpStateAsync(MapViewModel map)
        {
            await WaitAsync(() => map.Graph is not null && map.Characters.Count == 1);
            _Positions().Observe(new FleetPositionDto(Own, OwnName, Jita, PositionSource.Gamelog, DateTimeOffset.UtcNow.AddSeconds(-30 + ++_seconds)));
            UiDispatcher.UIThread.RunJobs();
            map.SetFollowModeCommand.Execute(MapFollowMode.Character);
            map.FollowCharacterCommand.Execute(map.Characters.Single());
            _Positions().Observe(new FleetPositionDto(Own, OwnName, Amarr, PositionSource.Gamelog, DateTimeOffset.UtcNow.AddSeconds(-30 + ++_seconds)));
            UiDispatcher.UIThread.RunJobs();
            map.TrailJumps = 20;
            map.FromText = "Jita";
            map.ToText = "Amarr";
            await map.PlanRouteCommand.ExecuteAsync(null);
        }

        public Button PlaceholderButton(string name) =>
            Assert.IsType<MapPoppedOutPlaceholder>(Shell.SelectedHostTab?.Content).FindControl<Button>(name)
            ?? throw new InvalidOperationException($"{name} is not on the placeholder");

        public void Settle() => MapFollowTests.Settle();

        public static async Task WaitAsync(Func<bool> condition)
        {
            for (var attempt = 0; attempt < 250 && !condition(); attempt++)
            {
                UiDispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }

            Assert.True(condition(), "the condition did not hold within the wait");
        }

        public void Dispose()
        {
            Popout?.Close();
            _window.Close();
            Instance.Dispose();
        }

        private FleetPositionSource _Positions() => Instance.Services.GetRequiredService<FleetPositionSource>();
    }
}
