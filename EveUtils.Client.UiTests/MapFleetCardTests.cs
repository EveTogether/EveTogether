using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using EveUtils.Client.Controls.Map;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.Map;
using EveUtils.Client.Views;
using EveUtils.Client.WorldMap;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Entities;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Fleet.Repositories;
using EveUtils.Shared.Modules.Map.Dtos;
using EveUtils.Shared.Modules.Map.Enums;
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using FleetEntity = EveUtils.Shared.Modules.Fleet.Entities.Fleet;
using UiDispatcher = Avalonia.Threading.Dispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>ET-395: the fleet's own place on the map. Fleet metrics carries a second view on the map — it follows this
/// fleet from the moment it opens, a member clicked in the COMPACT list is flown to, and the MAP tab can be opened on the
/// same fleet from the card and from the fleet's row. The map also keeps up with fleets starting and ending while it is
/// open. Driven the way it runs: real fleet, real participation, real position source, the real k-space graph.</summary>
public sealed class MapFleetCardTests
{
    private const int Jita = 30000142;
    private const int Perimeter = 30000144;
    private const int Amarr = 30002187;
    private const int Dodixie = 30002659;

    private const int Own = 91000001;
    private const string OwnName = "Kaelen Voss";
    private const int Mate = 91000002;
    private const string MateName = "Mira Solenne";
    private const int Outsider = 91000003;
    private const string OutsiderName = "Oskar Vale";

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    /// <summary>Acceptance: Fleet metrics opens with follow fleet on and every member inside the card's map.</summary>
    [AvaloniaFact]
    public async Task FleetMetrics_OpensFollowingTheFleet_WithEveryMemberInTheCard()
    {
        using var world = await World.OpenAsync();
        world.Sight(Own, Jita);
        world.Sight(Mate, Amarr);
        world.Sight(Outsider, Dodixie, PositionSource.EsiFleet);

        FleetMapCard card = world.ShowMetrics();
        await world.WaitAsync(() => world.Card.IsFollowingFleet);
        world.Settle();
        StarMapControl map = card.FindControl<StarMapControl>("CardMap") ?? throw new InvalidOperationException("no map in the card");

        Assert.True(world.Card.IsFollowingFleet);
        Assert.Equal("FOLLOWING FLEET", world.Card.FleetChipText);
        Assert.Equal("Wolfpack", world.Card.FollowedFleet?.Name);
        Assert.Equal("3 members in 3 systems", world.Card.FleetSpreadText);
        Assert.All([Jita, Amarr, Dodixie], system => Assert.True(new Rect(map.Bounds.Size).Contains(map.ScreenPointOf(world.IndexOf(system))),
            $"{system} is at {map.ScreenPointOf(world.IndexOf(system))}, outside {map.Bounds.Size}"));
        Assert.Equal(440, card.Bounds.Width);
    }

    /// <summary>Acceptance: a click on a member in the COMPACT list centres the card's map on that member and pauses
    /// following; RESUME brings the whole fleet back. Clicked through the window, the way it is used.</summary>
    [AvaloniaFact]
    public async Task ClickingAMemberInTheCompactList_CentresTheMapOnThemAndPauses_ResumeFramesTheFleetAgain()
    {
        using var world = await World.OpenAsync();
        world.Sight(Own, Jita);
        world.Sight(Mate, Dodixie);
        FleetMapCard card = world.ShowMetrics();
        await world.WaitAsync(() => world.Card.IsFollowingFleet && world.Metrics.Members.Count == 2);
        world.Metrics.SetLayoutCommand.Execute(FleetMetricsLayout.Compact);
        world.Settle();
        StarMapControl map = card.FindControl<StarMapControl>("CardMap") ?? throw new InvalidOperationException("no map in the card");
        DpsViewModel mate = world.Metrics.Members.Single(member => member.Character == MateName);

        Point row = world.CenterOfRowFor(mate);
        world.Window.MouseDown(row, MouseButton.Left);
        world.Window.MouseUp(row, MouseButton.Left);
        world.Settle();

        Point middle = new(map.Bounds.Width / 2, map.Bounds.Height / 2);
        Point mateAt = map.ScreenPointOf(world.IndexOf(Dodixie));
        Assert.True(Point.Distance(middle, mateAt) < 3, $"the map is centred at {mateAt}, not on {middle}");
        Assert.True(map.ZoomLevel >= MapViewModel.FollowMinZoom, $"zoom {map.ZoomLevel}");
        Assert.True(world.Card.IsFollowPaused);
        Assert.Equal("PAUSED", world.Card.FleetChipText);

        world.Card.ResumeFollowCommand.Execute(null);
        world.Settle();

        Assert.False(world.Card.IsFollowPaused);
        Assert.All([Jita, Dodixie], system => Assert.True(new Rect(map.Bounds.Size).Contains(map.ScreenPointOf(world.IndexOf(system)))));
    }

    /// <summary>A click on a member nobody has a current position for says so and leaves the map alone.</summary>
    [AvaloniaFact]
    public async Task ClickingAMemberWithoutAPosition_SaysSo_AndDoesNotMoveTheMap()
    {
        using var world = await World.OpenAsync();
        world.Sight(Own, Jita);
        world.ShowMetrics();
        await world.WaitAsync(() => world.Card.IsFollowingFleet && world.Metrics.Members.Count == 2);
        MapFocusRequest? before = world.Card.FocusRequest;

        world.Metrics.ShowMemberOnMap(world.Metrics.Members.Single(member => member.Character == MateName));

        Assert.Same(before, world.Card.FocusRequest);
        Assert.False(world.Card.IsFollowPaused);
        Assert.Equal($"{MateName} has no position from the last 10 minutes.", world.Card.MemberNoticeText);
    }

    /// <summary>Acceptance: OPEN IN MAP opens the MAP tab following this fleet — and the right one, when there are two.</summary>
    [AvaloniaFact]
    public async Task OpenInMap_OpensTheMapTab_FollowingThisFleetAndNotTheOther()
    {
        var dialogs = new RecordingDialogService();
        using var world = await World.OpenAsync(dialogs);
        long other = await world.AddFleetAsync("Sideshow", started: true);
        await world.RefreshFleetsAsync();
        world.Sight(Own, Jita);

        await world.Metrics.OpenFleetOnMapCommand.ExecuteAsync(null);

        MapViewModel tab = dialogs.LastMap ?? throw new InvalidOperationException("the MAP tab was not opened");
        Assert.True(tab.IsFollowingFleet);
        Assert.Equal(world.FleetId, tab.FollowedFleet?.Fleet.FleetId);
        Assert.NotEqual(other, tab.FollowedFleet?.Fleet.FleetId);
        Assert.Equal("Map: following fleet Wolfpack", tab.FollowStatusText);
        tab.Dispose();
    }

    /// <summary>ET-396: the card's POP OUT opens the map following this fleet — the MAP tab's own map, in its own window —
    /// and not the other fleet.</summary>
    [AvaloniaFact]
    public async Task PopOutOnTheCard_PopsTheMapOut_FollowingThisFleet()
    {
        var dialogs = new RecordingDialogService();
        using var world = await World.OpenAsync(dialogs);
        long other = await world.AddFleetAsync("Sideshow", started: true);
        await world.RefreshFleetsAsync();
        world.Sight(Own, Jita);
        FleetMapCard card = world.ShowMetrics();

        Assert.NotNull(card.FindControl<Button>("PopOutButton"));
        await world.Metrics.PopOutFleetMapCommand.ExecuteAsync(null);

        MapViewModel tab = dialogs.LastMap ?? throw new InvalidOperationException("the MAP tab was not opened");
        Assert.Equal(1, dialogs.MapPopOuts);
        Assert.True(dialogs.IsMapPoppedOut);
        Assert.True(tab.IsFollowingFleet);
        Assert.Equal(world.FleetId, tab.FollowedFleet?.Fleet.FleetId);
        Assert.NotEqual(other, tab.FollowedFleet?.Fleet.FleetId);
        tab.Dispose();
    }

    /// <summary>Acceptance: the MAP action on the fleet row opens the MAP tab on that row's fleet.</summary>
    [AvaloniaFact]
    public async Task TheMapActionOnAFleetRow_OpensTheMapTab_FollowingThatFleet()
    {
        var dialogs = new RecordingDialogService();
        using var world = await World.OpenAsync(dialogs);
        long other = await world.AddFleetAsync("Sideshow", started: true);
        await world.RefreshFleetsAsync();
        var fleets = new FleetsViewModel(world.Instance.Services);
        await world.WaitAsync(() => fleets.ActiveFleets.Any(row => row.Id == other));
        FleetViewModel row = fleets.ActiveFleets.Single(candidate => candidate.Id == other);

        Assert.True(row.ShowMapChip);
        await fleets.MapRowCommand.ExecuteAsync(row);

        MapViewModel tab = dialogs.LastMap ?? throw new InvalidOperationException("the MAP tab was not opened");
        Assert.Equal(other, tab.FollowedFleet?.Fleet.FleetId);
        tab.Dispose();
        fleets.Dispose();
    }

    /// <summary>The fleet commander's system is the green chip in the card's footer, first in the row; the others follow
    /// with their head count.</summary>
    [AvaloniaFact]
    public async Task TheCommandersSystem_IsTheGreenChip_First()
    {
        using var world = await World.OpenAsync(commander: Mate);
        world.Sight(Own, Jita);
        world.Sight(Outsider, Jita, PositionSource.EsiFleet);
        world.Sight(Mate, Amarr);
        FleetMapCard card = world.ShowMetrics();
        await world.WaitAsync(() => world.Card.FleetSystemChips.Count == 2);
        world.Settle();

        Assert.Equal(["Amarr", "Jita ×2"], world.Card.FleetSystemChips.Select(chip => chip.Text));
        Assert.Equal([true, false], world.Card.FleetSystemChips.Select(chip => chip.HasCommander));
        List<Border> chips = [.. card.FindControl<ItemsControl>("SystemChips")!.GetVisualDescendants().OfType<Border>()
            .Where(border => border.Classes.Contains("chip"))];
        Assert.Equal([true, false], chips.Select(chip => chip.Classes.Contains("good")));
    }

    /// <summary>Acceptance, the gap left by the MAP tab: a fleet that starts while the map is open shows up in the fleet
    /// choice, and a map tied to it starts following — and both go again when the fleet ends. Nothing here reopens the map;
    /// the fleet module's change signal is the only thing that moves.</summary>
    [AvaloniaFact]
    public async Task AFleetThatStartsAndEndsWhileTheMapIsOpen_AppearsAndGoesWithoutReopening()
    {
        using var world = await World.OpenAsync(started: false);
        MapViewModel tab = world.NewMap();
        MapViewModel card = world.NewMap();
        await tab.LoadAsync();
        await card.PinFleetAsync(world.FleetId, serverAddress: null);
        Assert.Empty(tab.Fleets);
        Assert.False(card.IsFollowingFleet);
        tab.SetFollowModeCommand.Execute(MapFollowMode.Fleet);

        await world.SetActivationAsync(FleetActivation.Active);
        world.Announce();
        await world.WaitAsync(() => tab.Fleets.Count == 1 && card.IsFollowingFleet);

        Assert.Equal("Wolfpack", tab.Fleets.Single().Name);
        Assert.True(card.IsPinnedFleetActive);
        Assert.Equal("Wolfpack", card.FollowedFleet?.Name);

        tab.FollowFleetCommand.Execute(tab.Fleets.Single());
        Assert.True(tab.IsFollowingFleet);

        await world.SetActivationAsync(FleetActivation.Concluded);
        world.Announce();
        await world.WaitAsync(() => tab.Fleets.Count == 0 && !card.IsFollowingFleet);

        Assert.False(tab.IsFollowingFleet);
        Assert.False(card.IsPinnedFleetActive);
        Assert.Equal("NOT FOLLOWING", card.FleetChipText);
        tab.Dispose();
        card.Dispose();
    }

    /// <summary>Joining and leaving is the same story: the change signal alone puts a fleet into the choice, and taking
    /// your characters out of it takes it out again — the roster of a fleet the map has never listed included.</summary>
    [AvaloniaFact]
    public async Task JoiningAndLeavingAFleet_MovesTheFleetChoice_ForAFleetTheMapNeverListed()
    {
        using var world = await World.OpenAsync(started: true, mine: false);
        MapViewModel map = world.NewMap();
        await map.LoadAsync();
        Assert.Empty(map.Fleets);

        await world.SetMineAsync(true);
        world.Announce();
        await world.WaitAsync(() => map.Fleets.Count == 1);

        await world.SetMineAsync(false);
        world.Announce();
        await world.WaitAsync(() => map.Fleets.Count == 0);
        map.Dispose();
    }

    /// <summary>Two views at once — the MAP tab and the fleet card — drawn to a real Skia surface, every frame, at each
    /// level of detail. The figures are reported; the bound only catches per-frame geometry coming back (the single view
    /// is 9–20 ms at 1400×900).</summary>
    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(10)]
    public async Task TwoViewsAtOnce_MapTabAndFleetCard_RenderWithinAFrameBudget(int notches)
    {
        using var world = await World.OpenAsync();
        world.Sight(Own, Jita);
        world.Sight(Mate, Amarr);
        MapViewModel tab = world.NewMap();
        await tab.LoadAsync();
        var tabWindow = new MapWindow(tab) { Width = 1180, Height = 720 };
        tabWindow.Show();
        FleetMapCard card = world.ShowMetrics();
        await world.WaitAsync(() => world.Card.IsFollowingFleet);
        world.Settle();
        StarMapControl tabMap = tabWindow.FindControl<StarMapControl>("Map") ?? throw new InvalidOperationException("no map in the tab");
        StarMapControl cardMap = card.FindControl<StarMapControl>("CardMap") ?? throw new InvalidOperationException("no map in the card");
        Point centre = new(tabMap.Bounds.Width / 2, tabMap.Bounds.Height / 2);
        tabWindow.MouseWheel(tabMap.TranslatePoint(centre, tabWindow) ?? default, new Vector(0, notches));
        cardMap.ZoomBy(Math.Pow(1.4, notches));
        UiDispatcher.UIThread.RunJobs();

        using var tabSurface = new RenderTargetBitmap(new PixelSize((int)tabMap.Bounds.Width, (int)tabMap.Bounds.Height));
        using var cardSurface = new RenderTargetBitmap(new PixelSize((int)cardMap.Bounds.Width, (int)cardMap.Bounds.Height));
        tabSurface.Render(tabMap);
        cardSurface.Render(cardMap);

        const int frames = 20;
        double tabMs = _PerFrame(frames, () => tabSurface.Render(tabMap));
        double cardMs = _PerFrame(frames, () => cardSurface.Render(cardMap));
        double bothMs = _PerFrame(frames, () =>
        {
            tabSurface.Render(tabMap);
            cardSurface.Render(cardMap);
        });

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{tabMap.DetailLevel}/{cardMap.DetailLevel}: tab {tabMap.Bounds.Width:0}×{tabMap.Bounds.Height:0} {tabMs:0.0} ms, " +
            $"card {cardMap.Bounds.Width:0}×{cardMap.Bounds.Height:0} {cardMs:0.0} ms, both per frame {bothMs:0.0} ms");
        tabWindow.Close();
        tab.Dispose();
        Assert.True(bothMs < 40, $"{bothMs:0.0} ms for both views at {tabMap.DetailLevel}/{cardMap.DetailLevel}");
    }

    private static double _PerFrame(int frames, Action draw)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (int _ in Enumerable.Range(0, frames))
            draw();
        return clock.Elapsed.TotalMilliseconds / frames;
    }

    private sealed class World : IDisposable
    {
        private readonly FleetPositionSource _positions;
        private readonly List<MapViewModel> _maps = [];
        private int _seconds;
        private FleetMetricsWindow? _window;

        private World(TestClientInstance instance, int commander)
        {
            Instance = instance;
            Commander = commander;
            _positions = instance.Services.GetRequiredService<FleetPositionSource>();
        }

        public TestClientInstance Instance { get; }

        public int Commander { get; }

        public long FleetId { get; private set; }

        public FleetMetricsViewModel Metrics { get; private set; } = null!;

        public MapViewModel Card => Metrics.FleetMap ?? throw new InvalidOperationException("the fleet card has no map");

        public Window Window => _window ?? throw new InvalidOperationException("Fleet metrics is not open");

        // A client-only fleet this client takes part in: you and an EVE Together mate on the roster, and an outsider the
        // in-game fleet holds as well.
        public static async Task<World> OpenAsync(IDialogService? dialogs = null, bool started = true, bool mine = true, int commander = Own)
        {
            var names = new FakeExternalLookup { [Outsider] = OutsiderName, [Mate] = MateName, [Own] = OwnName };
            TestClientInstance instance = TestClientInstance.Create(services =>
            {
                services.AddSingleton<ISdeAccessor>(MapFixture.Sde());
                services.AddSingleton<IExternalCharacterLookup>(names);
                if (dialogs is not null)
                    services.AddSingleton(dialogs);
            });
            await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character(OwnName, Own));

            var world = new World(instance, commander);
            world.FleetId = await world.AddFleetAsync("Wolfpack", started, mine ? Own : Outsider, commander);
            await world.RefreshFleetsAsync();
            instance.Services.GetRequiredService<InGameFleetRosters>().Record(InGameFleetRosters.KeyOf(null, world.FleetId), [Own, Mate, Outsider]);
            var roster = new FakeFleetClient
            {
                Members =
                [
                    new FleetMemberInfo(1, Own, -1, -1, commander == Own ? FleetRole.FleetCommander : FleetRole.SquadMember, false),
                    new FleetMemberInfo(2, Mate, -1, -1, commander == Mate ? FleetRole.FleetCommander : FleetRole.SquadMember, false)
                ]
            };
            var info = new FleetInfo(world.FleetId, "Wolfpack", null, FleetVisibility.Public, FleetState.Active, commander,
                null, null, DateTimeOffset.UnixEpoch, FleetActivation.Active);
            world.Metrics = new FleetMetricsViewModel(instance.Services, roster, info, Own);
            return world;
        }

        public async Task<long> AddFleetAsync(string name, bool started, int creator = Own, int commander = Own)
        {
            var repository = Instance.Services.GetRequiredService<IFleetRepository>();
            long id = await repository.AddAsync(new FleetEntity
            {
                Name = name,
                CreatorCharacterId = creator,
                State = FleetState.Active,
                Activation = started ? FleetActivation.Active : FleetActivation.Forming,
                IsClientOnly = true
            });
            await repository.AddMemberAsync(new FleetMember { FleetId = id, CharacterId = Own, WingId = -1, SquadId = -1, Role = RoleOf(Own, commander) });
            await repository.AddMemberAsync(new FleetMember { FleetId = id, CharacterId = Mate, WingId = -1, SquadId = -1, Role = RoleOf(Mate, commander) });

            return id;
        }

        private static FleetRole RoleOf(int characterId, int commander) => characterId == commander ? FleetRole.FleetCommander : FleetRole.SquadMember;

        public async Task SetActivationAsync(FleetActivation activation)
        {
            var repository = Instance.Services.GetRequiredService<IFleetRepository>();
            FleetEntity fleet = await repository.GetAsync(FleetId) ?? throw new InvalidOperationException("the fleet is gone");
            fleet.Activation = activation;
            await repository.UpdateAsync(fleet);
        }

        public async Task SetMineAsync(bool mine)
        {
            var repository = Instance.Services.GetRequiredService<IFleetRepository>();
            FleetEntity fleet = await repository.GetAsync(FleetId) ?? throw new InvalidOperationException("the fleet is gone");
            fleet.CreatorCharacterId = mine ? Own : Outsider;
            await repository.UpdateAsync(fleet);
        }

        public Task RefreshFleetsAsync() => Instance.Services.GetRequiredService<FleetParticipationRefresher>().RefreshAsync();

        // The fleet module's own signal, as the relay folds it in — the only thing a screen gets to hear.
        public void Announce() => Instance.Services.GetRequiredService<IFleetRosterWatch>().Announce(FleetRosterChange.Reloaded(FleetId));

        public MapViewModel NewMap()
        {
            MapViewModel map = Instance.Services.GetRequiredService<IMapLauncher>().Create();
            _maps.Add(map);
            return map;
        }

        public FleetMapCard ShowMetrics()
        {
            _window = new FleetMetricsWindow(Metrics) { Width = 1160, Height = 620 };
            _window.Show();
            UiDispatcher.UIThread.RunJobs();
            return _window.FindControl<FleetMapCard>("MapCard") ?? throw new InvalidOperationException("no map card in Fleet metrics");
        }

        public Point CenterOfRowFor(DpsViewModel member)
        {
            ItemsControl list = Window.FindControl<ItemsControl>("MemberList") ?? throw new InvalidOperationException("no member list");
            Control row = list.GetRealizedContainers().Single(container => ReferenceEquals(container.DataContext, member));
            return row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), Window) ?? default;
        }

        public void Sight(int characterId, int solarSystemId, PositionSource source = PositionSource.FleetMetric)
        {
            _positions.Observe(new FleetPositionDto(characterId, null, solarSystemId, source, DateTimeOffset.UtcNow.AddSeconds(-60 + ++_seconds)));
            UiDispatcher.UIThread.RunJobs();
        }

        public int IndexOf(int solarSystemId)
        {
            MapGraphDto graph = Card.Graph ?? throw new InvalidOperationException("the map is not loaded");
            Assert.True(graph.TryGetIndex(solarSystemId, out int index));
            return index;
        }

        public async Task WaitAsync(Func<bool> condition)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < Wait)
            {
                UiDispatcher.UIThread.RunJobs();
                if (condition())
                    return;
                await Task.Delay(20);
            }

            UiDispatcher.UIThread.RunJobs();
            Assert.True(condition(), "the condition did not hold within the wait");
        }

        public void Settle() => MapFollowTests.Settle();

        public void Dispose()
        {
            _window?.Close();
            foreach (MapViewModel map in _maps)
                map.Dispose();
            Metrics.Dispose();
            Instance.Dispose();
        }
    }
}
