using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using EveUtils.Client.Controls.Map;
using EveUtils.Client.ViewModels.Map;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Map.Dtos;
using EveUtils.Shared.Modules.Map.Enums;
using Xunit;
using static EveUtils.Client.UiTests.MapFollowFleetTests;
using UiDispatcher = Avalonia.Threading.Dispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>ET-399: the hover popover on the map — what it says (from the map's own snapshot), when it opens (after a rest,
/// at every level of detail), and when it goes (drag, zoom, leaving) — driven by pointer input the way a pilot drives it.</summary>
public sealed class MapHoverPopoverTests
{
    private static readonly Color AccentBright = Color.Parse("#FFD24A");

    [Fact]
    public void Rows_ForASystemWithGates_ShowNameSecurityPlaceRouteJumpsHereAndNeighbours()
    {
        MapSystemInfo info = _Info(neighbours: ["Alpha", "Bravo", "Charlie"], routeStep: 4, routeLength: 12,
            distances: [new MapSystemDistance("Kaelen Voss", 7, false), new MapSystemDistance("FC Mira", 1, false)],
            here: [new MapSystemOccupant("Mira", MapOccupantKind.Commander, T0.AddSeconds(-40)), new MapSystemOccupant("Kaelen Voss", MapOccupantKind.OwnCharacter, null)]);

        string[] rows = [.. MapPopoverRows.From(info, T0, AccentBright).Select(MapPopoverRows.TextOf)];

        Assert.Equal(
        [
            "Amarr 0.9", "Kador · Domain", "Amarr Empire", "On the route · step 4 of 12", "7 jumps from Kaelen Voss", "1 jump from FC Mira",
            "FC · Mira · 40s ago", "Kaelen Voss", "3 stargates · Alpha, Bravo, Charlie"
        ], rows);
    }

    [Fact]
    public void Rows_WithMoreThanSixNeighbours_ListSixAndCountTheRest()
    {
        string[] names = [.. Enumerable.Range(1, 9).Select(number => $"System{number}")];

        string gates = MapPopoverRows.From(_Info(neighbours: names), T0, AccentBright).Select(MapPopoverRows.TextOf).Last();

        Assert.Equal("9 stargates · System1, System2, System3, System4, System5, System6, +3 more", gates);
    }

    [Fact]
    public void Rows_ForAJoveSystem_SayNoGates()
    {
        Assert.Equal("No gates", MapPopoverRows.From(_Info(neighbours: []), T0, AccentBright).Select(MapPopoverRows.TextOf).Last());
    }

    [Fact]
    public void Rows_WithMoreThanEightOccupants_ListEightWithTheCommanderFirstAndCountTheRest()
    {
        MapSystemOccupant[] here =
        [
            new("Boss", MapOccupantKind.Commander, T0),
            .. Enumerable.Range(1, 12).Select(number => new MapSystemOccupant($"Pilot{number}", MapOccupantKind.FleetMember, T0))
        ];

        string[] rows = [.. MapPopoverRows.From(_Info(neighbours: [], here: here), T0, AccentBright).Select(MapPopoverRows.TextOf)];

        Assert.Equal("FC · Boss · 0s ago", rows[3]);
        Assert.Equal("Pilot7 · 0s ago", rows[^3]);
        Assert.Equal("+5 more here", rows[^2]);
    }

    [Fact]
    public void Rows_WhileCountingOrWithoutARoute_SaySo()
    {
        MapSystemInfo info = _Info(neighbours: [], distances: [new MapSystemDistance("A", null, true), new MapSystemDistance("B", null, false)]);

        string[] rows = [.. MapPopoverRows.From(info, T0, AccentBright).Select(MapPopoverRows.TextOf)];

        Assert.Contains("Counting jumps from A…", rows);
        Assert.Contains("No route from B", rows);
    }

    [Fact]
    public void Rows_ColourTheSecurityWithTheGamesScale()
    {
        IReadOnlyList<MapPopoverRun> title = MapPopoverRows.From(_Info(neighbours: [], security: 0.3), T0, AccentBright)[0];

        Assert.Equal(MapPalette.SecurityColour(0.3), title.Single(run => run.Text == "0.3").Colour);
    }

    [Theory]
    [InlineData(100, 100, 300, 200, 1000, 800, 114, 114)]
    [InlineData(950, 100, 300, 200, 1000, 800, 636, 114)]
    [InlineData(100, 700, 300, 200, 1000, 800, 114, 486)]
    [InlineData(990, 790, 300, 200, 1000, 800, 676, 576)]
    [InlineData(0, 0, 1200, 900, 1000, 800, 0, 0)]
    public void Place_FlipsAndClampsSoThePopoverStaysInsideTheMap(double systemX, double systemY, double width, double height,
        double mapWidth, double mapHeight, double expectedX, double expectedY)
    {
        Rect placed = MapPopoverLayout.Place(new Point(systemX, systemY), new Size(width, height), new Size(mapWidth, mapHeight));

        Assert.Equal(expectedX, placed.X, 0.01);
        Assert.Equal(expectedY, placed.Y, 0.01);
    }

    /// <summary>Acceptance: the popover opens after the pointer rests ~250 ms, not before.</summary>
    [AvaloniaFact]
    public async Task ThePopover_OpensOnlyAfterTheDelay()
    {
        using var world = await World.OpenAsync();
        (Window window, StarMapControl map) = _Open(world);
        Assert.Equal(TimeSpan.FromMilliseconds(250), new StarMapControl().PopoverDelay);
        map.PopoverDelay = TimeSpan.FromMilliseconds(250);
        int jita = world.IndexOf(Jita);
        _Focus(map, jita, 11);

        var clock = Stopwatch.StartNew();
        _Hover(window, map, jita);
        Assert.Null(map.PopoverText);
        while (map.PopoverText is null && clock.ElapsedMilliseconds < 3000)
        {
            UiDispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        Assert.NotNull(map.PopoverText);
        Assert.InRange(clock.ElapsedMilliseconds, 200, 1500);
    }

    /// <summary>Acceptance: works at every level of detail — regions, constellations and systems — through the spatial grid.</summary>
    [AvaloniaTheory]
    [InlineData(1.5, MapDetailLevel.Regions)]
    [InlineData(4, MapDetailLevel.Constellations)]
    [InlineData(11, MapDetailLevel.Systems)]
    public async Task HoveringASystem_OpensItsPopover_AtEveryLevelOfDetail(double zoom, MapDetailLevel level)
    {
        using var world = await World.OpenAsync();
        (Window window, StarMapControl map) = _Open(world);
        int jita = world.IndexOf(Jita);
        _Focus(map, jita, zoom);

        _Hover(window, map, jita);

        Assert.Equal(level, map.DetailLevel);
        Assert.Equal(jita, map.PopoverIndex);
        Assert.StartsWith("Jita 0.9\n", map.PopoverText);
    }

    /// <summary>The pick radius grows with the dot it aims at and never past 12 px.</summary>
    [AvaloniaFact]
    public async Task ThePickRadius_FollowsTheLevelOfDetail()
    {
        using var world = await World.OpenAsync();
        (_, StarMapControl map) = _Open(world);
        int jita = world.IndexOf(Jita);
        var radii = new List<double>();

        foreach (double zoom in new[] { 1.5, 4, 11 })
        {
            _Focus(map, jita, zoom);
            radii.Add(map.PickRadius);
        }

        Assert.Equal(radii.Order(), radii);
        Assert.All(radii, radius => Assert.InRange(radius, 6, 12));
        Assert.True(radii[0] < radii[2], $"{radii[0]} at regions, {radii[2]} at systems");
    }

    /// <summary>The place, the route step, the gates and their neighbours, from the real graph.</summary>
    [AvaloniaFact]
    public async Task ThePopover_ShowsPlaceGatesAndPositionOnTheRoute()
    {
        using var world = await World.OpenAsync();
        (Window window, StarMapControl map) = _Open(world);
        MapGraphDto graph = world.Model.Graph!;
        world.Model.FromText = "Jita";
        world.Model.ToText = "Amarr";
        await world.Model.PlanRouteCommand.ExecuteAsync(null);
        IReadOnlyList<int> route = world.Model.RouteIndexes!;
        int onRoute = route[3];
        MapSystemDto system = graph.Systems[onRoute];
        _Focus(map, onRoute, 11);

        _Hover(window, map, onRoute);

        string[] rows = map.PopoverText!.Split('\n');
        Assert.Equal($"{system.Name} {system.DisplaySecurity.ToString("0.0", CultureInfo.InvariantCulture)}", rows[0]);
        Assert.Contains($"{graph.Constellations[system.ConstellationIndex].Name} · {graph.Regions[system.RegionIndex].Name}", rows);
        Assert.Contains($"On the route · step 4 of {route.Count}", rows);
        Assert.StartsWith($"{graph.NeighboursOf(onRoute).Length} stargates · ", rows[^1]);
        Assert.Contains(graph.Systems[graph.NeighboursOf(onRoute)[0]].Name, rows[^1]);
    }

    [AvaloniaFact]
    public async Task ASystemOffTheRoute_SaysNothingAboutIt()
    {
        using var world = await World.OpenAsync();
        (Window window, StarMapControl map) = _Open(world);
        world.Model.FromText = "Jita";
        world.Model.ToText = "Amarr";
        await world.Model.PlanRouteCommand.ExecuteAsync(null);
        int dodixie = world.IndexOf(Dodixie);
        _Focus(map, dodixie, 11);

        _Hover(window, map, dodixie);

        Assert.DoesNotContain("On the route", map.PopoverText);
    }

    [AvaloniaFact]
    public async Task AJoveSystem_SaysNoGates()
    {
        using var world = await World.OpenAsync();
        (Window window, StarMapControl map) = _Open(world);
        MapGraphDto graph = world.Model.Graph!;
        MapSystemDto jove = graph.Systems.First(system => graph.NeighboursOf(system.Index).Length == 0);
        _Focus(map, jove.Index, 11);

        _Hover(window, map, jove.Index);

        Assert.EndsWith("\nNo gates", map.PopoverText);
    }

    /// <summary>Jumps from the followed fleet's commander and from your own character, from the route service; the first hover
    /// says it is counting — the query runs off the UI thread — and the answer replaces that line.</summary>
    [AvaloniaFact]
    public async Task ThePopover_CountsJumpsFromTheCommanderAndFromYourCharacter()
    {
        using var world = await World.OpenAsync(rosterCommander: Mate);
        (Window window, StarMapControl map) = _Open(world);
        world.Sight(Own, Amarr);
        world.Sight(Mate, Perimeter);
        world.FollowFleet();
        int jita = world.IndexOf(Jita);
        _Focus(map, jita, 11);

        MapSystemInfo first = world.Model.SystemInfo(jita)!;
        Assert.Equal(["FC Mira Solenne", OwnName], first.Distances.Select(distance => distance.From).Order());
        Assert.All(first.Distances, distance => Assert.True(distance.IsPending));
        await _WaitAsync(() => world.Model.SystemInfo(jita)!.Distances.All(distance => !distance.IsPending));

        _Hover(window, map, jita);

        Assert.Contains("1 jump from FC Mira Solenne", map.PopoverText);
        Assert.Contains($"11 jumps from {OwnName}", map.PopoverText);
    }

    /// <summary>A followed character is the one the jumps are counted from.</summary>
    [AvaloniaFact]
    public async Task FollowingACharacter_CountsJumpsFromThatCharacter()
    {
        using var world = await World.OpenAsync();
        (Window window, StarMapControl map) = _Open(world);
        world.Sight(Own, Amarr, PositionSource.Gamelog);
        world.Model.SetFollowModeCommand.Execute(MapFollowMode.Character);
        world.Model.FollowCharacterCommand.Execute(world.Model.Characters.Single());
        int jita = world.IndexOf(Jita);
        _Focus(map, jita, 11);

        _Hover(window, map, jita);
        await _WaitAsync(() => map.PopoverText?.Contains("Counting") == false);

        Assert.Contains($"11 jumps from {OwnName}", map.PopoverText);
        Assert.DoesNotContain("FC ", map.PopoverText);
    }

    /// <summary>Acceptance: a drag, the wheel and leaving the map all close it.</summary>
    [AvaloniaFact]
    public async Task ThePopover_GoesOnDragZoomAndLeaving()
    {
        using var world = await World.OpenAsync();
        (Window window, StarMapControl map) = _Open(world);
        int jita = world.IndexOf(Jita);
        _Focus(map, jita, 11);

        _Hover(window, map, jita);
        Assert.NotNull(map.PopoverText);
        Point at = map.TranslatePoint(map.ScreenPointOf(jita), window) ?? default;
        window.MouseDown(at, MouseButton.Left);
        window.MouseMove(at + new Vector(30, 30));
        Assert.Null(map.PopoverText);
        window.MouseUp(at + new Vector(30, 30), MouseButton.Left);

        _Focus(map, jita, 11);
        _Hover(window, map, jita);
        Assert.NotNull(map.PopoverText);
        window.MouseWheel(at, new Vector(0, 1));
        UiDispatcher.UIThread.RunJobs();
        Assert.Null(map.PopoverText);

        _Focus(map, jita, 11);
        _Hover(window, map, jita);
        Assert.NotNull(map.PopoverText);
        window.MouseMove(new Point(-50, -50));
        Assert.Null(map.PopoverText);
    }

    [AvaloniaFact]
    public async Task MovingToAnotherSystem_ReplacesThePopover()
    {
        using var world = await World.OpenAsync();
        (Window window, StarMapControl map) = _Open(world);
        int jita = world.IndexOf(Jita), perimeter = world.IndexOf(Perimeter);
        _Focus(map, jita, 11);

        _Hover(window, map, jita);
        _Hover(window, map, perimeter);

        Assert.Equal(perimeter, map.PopoverIndex);
        Assert.StartsWith("Perimeter ", map.PopoverText);
    }

    /// <summary>Acceptance: stays inside the map bounds, for the system furthest to each edge of the full view.</summary>
    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ThePopover_StaysInsideTheMap_AtEachEdge(int edge)
    {
        using var world = await World.OpenAsync();
        (Window window, StarMapControl map) = _Open(world);
        MapGraphDto graph = world.Model.Graph!;
        MapSystemDto system = edge switch
        {
            0 => graph.Systems.MinBy(candidate => candidate.X)!,
            1 => graph.Systems.MaxBy(candidate => candidate.X)!,
            2 => graph.Systems.MinBy(candidate => candidate.Y)!,
            _ => graph.Systems.MaxBy(candidate => candidate.Y)!
        };

        _Hover(window, map, system.Index);

        Assert.Equal(system.Index, map.PopoverIndex);
        Rect popover = map.PopoverBounds;
        Assert.True(popover.Width > 0 && popover.Height > 0);
        Assert.True(popover.X >= 0 && popover.Y >= 0 && popover.Right <= map.Bounds.Width + 0.01 && popover.Bottom <= map.Bounds.Height + 0.01,
            $"{popover} is not inside {map.Bounds.Size}");
    }

    /// <summary>When the owner says what the popover shows changed (a jump count arrived) it rereads, without a pointer move.</summary>
    [AvaloniaFact]
    public async Task AnOpenPopover_RereadsWhenTheOwnerBumpsTheRevision()
    {
        using var world = await World.OpenAsync();
        (Window window, StarMapControl map) = _Open(world);
        int jita = world.IndexOf(Jita);
        _Focus(map, jita, 11);
        string heading = "First";
        map.SystemInfoSource = index => world.Model.SystemInfo(index) is { } info ? info with { Name = heading } : null;

        _Hover(window, map, jita);
        Assert.StartsWith("First ", map.PopoverText);
        heading = "Second";
        map.InfoRevision++;

        Assert.StartsWith("Second ", map.PopoverText);
    }

    private static (Window, StarMapControl) _Open(World world)
    {
        (Window window, StarMapControl map) = world.Show();
        map.PopoverDelay = TimeSpan.Zero;
        return (window, map);
    }

    private static void _Focus(StarMapControl map, int systemIndex, double zoom)
    {
        map.FocusRequest = new MapFocusRequest([systemIndex], zoom);
        MapFollowTests.Settle();
    }

    // Two moves: the first lands one pixel off so a pointer that already rests here still produces a hover change.
    private static void _Hover(Window window, StarMapControl map, int systemIndex)
    {
        Point at = map.TranslatePoint(map.ScreenPointOf(systemIndex), window) ?? default;
        window.MouseMove(new Point(at.X + 1, at.Y));
        window.MouseMove(at);
        UiDispatcher.UIThread.RunJobs();
    }

    private static async Task _WaitAsync(Func<bool> done)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            UiDispatcher.UIThread.RunJobs();
        }
        Assert.True(done(), "the jump counts did not arrive");
    }

    private static MapSystemInfo _Info(IReadOnlyList<string> neighbours, double security = 0.9, int? routeStep = null, int routeLength = 0,
        IReadOnlyList<MapSystemDistance>? distances = null, IReadOnlyList<MapSystemOccupant>? here = null) =>
        new("Amarr", security, "Kador", "Domain", 2, "Amarr Empire", distances ?? [], routeStep, routeLength, here ?? [], neighbours);
}
