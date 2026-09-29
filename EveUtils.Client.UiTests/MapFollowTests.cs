using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using EveUtils.Client.Controls.Map;
using EveUtils.Client.Fleet;
using EveUtils.Client.ViewModels;
using EveUtils.Client.ViewModels.Map;
using EveUtils.Client.Views;
using EveUtils.Client.WorldMap;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Identity;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Map.Dtos;
using EveUtils.Shared.Modules.Map.Enums;
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using UiDispatcher = Avalonia.Threading.Dispatcher;

namespace EveUtils.Client.UiTests;

/// <summary>ET-393: the map following one of your own characters and drawing its trail, driven through the view model
/// the way positions reach it — a jump is a sighting on the merged position source.</summary>
public sealed class MapFollowTests
{
    private const int Jita = 30000142;
    private const int Perimeter = 30000144;
    private const int Amarr = 30002187;
    private const int CharacterId = 91000001;
    private const string CharacterName = "Kaelen Voss";

    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Acceptance: a simulated jump centres the viewport on the new system, at system zoom.</summary>
    [AvaloniaFact]
    public async Task Jump_OfTheFollowedCharacter_CentresTheMapOnTheNewSystem()
    {
        using var world = await World.OpenAsync();
        world.Follow();

        world.Jump(Jita);
        MapFocusRequest? first = world.Model.FocusRequest;
        world.Jump(Perimeter);

        Assert.Equal(world.IndexOf(Jita), Assert.Single(first!.SystemIndexes));
        Assert.Equal(world.IndexOf(Perimeter), Assert.Single(world.Model.FocusRequest!.SystemIndexes));
        Assert.True(world.Model.FocusRequest.MinZoom >= 9);
        Assert.Equal(world.IndexOf(Perimeter), world.Model.FollowIndex);
        Assert.Equal($"Map: following {CharacterName}", world.Model.FollowStatusText);
    }

    /// <summary>The same, through the control: after the jump the new system is what sits in the middle of the map.</summary>
    [AvaloniaFact]
    public async Task Jump_WithTheMapOpen_LeavesTheNewSystemInTheMiddle()
    {
        using var world = await World.OpenAsync();
        (Window window, StarMapControl map) = world.Show();
        world.Follow();

        world.Jump(Perimeter);
        _Settle();
        Point middle = map.TranslatePoint(new Point(map.Bounds.Width / 2, map.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("the map is not in the window");
        window.MouseDown(middle, MouseButton.Left);
        window.MouseUp(middle, MouseButton.Left);

        Assert.Equal(world.IndexOf(Perimeter), world.Model.SelectedIndex);
    }

    /// <summary>Acceptance: after a drag the next jump leaves the map where it is; RESUME follows again.</summary>
    [AvaloniaFact]
    public async Task AfterADrag_ANextJumpDoesNotMoveTheMap_AndResumeFollowsAgain()
    {
        using var world = await World.OpenAsync();
        (Window window, StarMapControl map) = world.Show();
        world.Follow();
        world.Jump(Jita);
        _Settle();

        Point from = map.TranslatePoint(new Point(map.Bounds.Width / 2, map.Bounds.Height / 2), window) ?? default;
        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + new Vector(60, 40));
        window.MouseUp(from + new Vector(60, 40), MouseButton.Left);
        MapFocusRequest? beforeJump = world.Model.FocusRequest;
        world.Jump(Perimeter);

        Assert.True(world.Model.IsFollowPaused);
        Assert.Equal("PAUSED · you moved the map", world.Model.FollowChipText);
        Assert.Same(beforeJump, world.Model.FocusRequest);

        world.Model.ResumeFollowCommand.Execute(null);

        Assert.False(world.Model.IsFollowPaused);
        Assert.Equal(world.IndexOf(Perimeter), Assert.Single(world.Model.FocusRequest!.SystemIndexes));
    }

    /// <summary>The map moving itself for a jump is not the pilot moving it — following must not pause on its own flight.</summary>
    [AvaloniaFact]
    public async Task AJumpsOwnFlight_DoesNotPauseFollowing()
    {
        using var world = await World.OpenAsync();
        (_, StarMapControl map) = world.Show();
        int moves = 0;
        map.ViewMovedByUser += (_, _) => moves++;
        world.Follow();

        world.Jump(Jita);
        world.Jump(Perimeter);
        _Settle();

        Assert.Equal(0, moves);
        Assert.False(world.Model.IsFollowPaused);
    }

    [AvaloniaTheory]
    [InlineData("search")]
    [InlineData("route")]
    public async Task SearchingOrPlanningARoute_PausesFollowing(string action)
    {
        using var world = await World.OpenAsync();
        world.Follow();
        world.Jump(Jita);

        if (action == "search")
        {
            world.Model.FindText = "Amarr";
            world.Model.FindCommand.Execute(null);
        }
        else
        {
            world.Model.FromText = "Jita";
            world.Model.ToText = "Perimeter";
            await world.Model.PlanRouteCommand.ExecuteAsync(null);
        }

        Assert.True(world.Model.IsFollowPaused);
    }

    /// <summary>AGENTS.md: no global active character. Choosing CHARACTER follows nobody until one is picked.</summary>
    [AvaloniaFact]
    public async Task ChoosingCharacter_FollowsNobodyUntilOneIsPicked()
    {
        using var world = await World.OpenAsync();

        world.Model.SetFollowModeCommand.Execute(MapFollowMode.Character);
        world.Jump(Jita);

        Assert.True(world.Model.NeedsCharacterPick);
        Assert.False(world.Model.IsFollowing);
        Assert.Null(world.Model.FocusRequest);
        Assert.Equal(string.Empty, world.Model.FollowStatusText);
    }

    [AvaloniaFact]
    public async Task TurningFollowOff_ClearsTheChoiceAndTheStatusLine()
    {
        using var world = await World.OpenAsync();
        world.Follow();
        world.Jump(Jita);

        world.Model.SetFollowModeCommand.Execute(MapFollowMode.Off);

        Assert.Null(world.Model.FollowedCharacter);
        Assert.Equal(string.Empty, world.Model.FollowStatusText);
        Assert.Null(world.Model.Trail);
        Assert.Equal(-1, world.Model.FollowIndex);
    }

    /// <summary>Acceptance: LastJumps 5 holds exactly five jumps, however long the walk was.</summary>
    [AvaloniaTheory]
    [InlineData(5)]
    [InlineData(10)]
    public async Task LastJumps_HoldsExactlyThatManyJumps(int jumps)
    {
        using var world = await World.OpenAsync();
        world.Follow();
        world.Model.TrailJumps = jumps;

        foreach (int system in world.WalkOnGates(jumps + 4))
            world.Jump(system);

        Assert.Equal(jumps + 1, world.Model.Trail?.Count);
        Assert.DoesNotContain(world.Model.Trail!, step => step.IsGapBefore);
    }

    /// <summary>Acceptance: "since" cuts at the right moment — the jumps made after it, from where the first of them left.</summary>
    [AvaloniaFact]
    public async Task Since_KeepsTheJumpsAfterTheCutoff()
    {
        using var world = await World.OpenAsync();
        world.Follow();
        int[] walk = world.WalkOnGates(3);
        world.Model.SetTrailWindowCommand.Execute(TrailWindow.Since);
        world.Model.TrailSince = TrailSince.Last15Minutes;
        TimeSpan[] agoAtJump = [TimeSpan.FromMinutes(40), TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1)];

        for (int at = 0; at < walk.Length; at++)
            world.Jump(walk[at], T0 - agoAtJump[at]);

        Assert.Equal(walk[1..].Select(world.IndexOf), world.Model.Trail?.Select(step => step.SystemIndex));
    }

    [AvaloniaFact]
    public async Task Since_WithNoJumpInTheWindow_ShowsOnlyTheCurrentSystem()
    {
        using var world = await World.OpenAsync();
        world.Follow();
        world.Model.SetTrailWindowCommand.Execute(TrailWindow.Since);
        world.Model.TrailSince = TrailSince.Last15Minutes;

        world.Jump(Jita, T0 - TimeSpan.FromHours(3));
        world.Jump(Perimeter, T0 - TimeSpan.FromHours(2));

        Assert.Equal([world.IndexOf(Perimeter)], world.Model.Trail?.Select(step => step.SystemIndex));
    }

    [Theory]
    [InlineData(12, 30, 30)]
    [InlineData(11, 0, 30)]
    [InlineData(10, 59, 29)]
    public void SinceDowntime_IsTheLatestElevenHundredUtc(int hour, int minute, int expectedDay)
    {
        DateTimeOffset now = new(2026, 9, 30, hour, minute, 0, TimeSpan.Zero);

        DateTimeOffset cutoff = MapTrail.CutoffFor(TrailSince.SinceDowntime, now, appStart: now);

        Assert.Equal(new DateTimeOffset(2026, 9, expectedDay, 11, 0, 0, TimeSpan.Zero), cutoff);
    }

    [Fact]
    public void SinceAppStart_IsTheMomentTheRecorderStarted()
    {
        DateTimeOffset started = T0 - TimeSpan.FromHours(5);

        Assert.Equal(started, MapTrail.CutoffFor(TrailSince.SinceAppStart, T0, started));
    }

    /// <summary>Acceptance: RESET TRAIL leaves only the current system and says when it was pressed.</summary>
    [AvaloniaFact]
    public async Task ResetTrail_LeavesOnlyTheCurrentSystem_AndSaysWhen()
    {
        using var world = await World.OpenAsync();
        world.Follow();
        foreach (int system in world.WalkOnGates(4))
            world.Jump(system);
        Assert.True(world.Model.Trail?.Count > 1);

        world.Model.ResetTrailCommand.Execute(null);

        Assert.Equal([world.IndexOf(world.WalkOnGates(4)[^1])], world.Model.Trail?.Select(step => step.SystemIndex));
        Assert.Equal("reset at " + world.Clock.GetUtcNow().ToLocalTime().ToString("HH:mm"), world.Model.TrailResetText);
    }

    /// <summary>A step the trail did not see is never filled in with the shortest path: two systems that are no gate
    /// neighbours stay two points, and the jump between them is marked as a leap.</summary>
    [AvaloniaFact]
    public async Task AJumpTheTrailDidNotSee_IsAGap_NotAFilledInRoute()
    {
        using var world = await World.OpenAsync();
        world.Follow();

        world.Jump(Jita);
        world.Jump(Amarr);

        Assert.Equal([(world.IndexOf(Jita), false), (world.IndexOf(Amarr), true)],
            world.Model.Trail?.Select(step => (step.SystemIndex, step.IsGapBefore)));
    }

    [AvaloniaFact]
    public async Task TrailOff_DrawsNothing()
    {
        using var world = await World.OpenAsync();
        world.Follow();
        world.Jump(Jita);
        world.Jump(Perimeter);
        Assert.NotNull(world.Model.Trail);

        world.Model.IsTrailOn = false;

        Assert.Null(world.Model.Trail);
    }

    /// <summary>The trail records from app start, so a map opened later still has it.</summary>
    [AvaloniaFact]
    public async Task ATrailRecordedBeforeTheMapOpened_IsThereWhenItOpens()
    {
        using var world = await World.OpenAsync(openMap: false);
        world.Jump(Jita);
        world.Jump(Perimeter);

        await world.OpenMapAsync();
        world.Follow();

        Assert.Equal([world.IndexOf(Jita), world.IndexOf(Perimeter)], world.Model.Trail?.Select(step => step.SystemIndex));
    }

    /// <summary>The shell starts the recorder at app start, so trails exist whether or not the map was ever opened.</summary>
    [AvaloniaFact]
    public void TheShell_StartsTheRecorder_BeforeTheMapIsOpened()
    {
        using TestClientInstance instance = TestClientInstance.Create();
        _ = new MainWindowViewModel(instance.Services);

        instance.Services.GetRequiredService<FleetPositionSource>()
            .Observe(new FleetPositionDto(CharacterId, CharacterName, Jita, PositionSource.Gamelog, T0));

        Assert.Equal(Jita, Assert.Single(instance.Services.GetRequiredService<MapTrailRecorder>().TrailOf(CharacterId).LastJumps(5)).SolarSystemId);
        Assert.Same(instance.Services.GetRequiredService<FleetPositionSource>(), instance.Services.GetRequiredService<IFleetPositionSource>());
    }

    /// <summary>Acceptance: nothing is written — following, walking and resetting leave every file of the client's
    /// data directory as it was.</summary>
    [AvaloniaFact]
    public async Task FollowingWalkingAndResetting_WriteNothingToTheDataDirectory()
    {
        using var world = await World.OpenAsync();
        string[] before = world.DataFiles();

        world.Follow();
        foreach (int system in world.WalkOnGates(6))
            world.Jump(system);
        world.Model.ResetTrailCommand.Execute(null);

        Assert.Equal(before, world.DataFiles());
    }

    [Fact]
    public void Trail_KeepsAtMostItsCapacity()
    {
        var trail = new MapTrail();

        foreach (int step in Enumerable.Range(1, MapTrail.Capacity + 25))
            trail.Add(step, T0.AddSeconds(step));

        Assert.Equal(MapTrail.Capacity, trail.LastJumps(int.MaxValue - 1).Count);
    }

    [Fact]
    public void Trail_IgnoresARepeatOfTheSystemItIsIn()
    {
        var trail = new MapTrail();

        trail.Add(Jita, T0);
        trail.Add(Jita, T0.AddSeconds(5));

        Assert.Single(trail.LastJumps(5));
    }

    /// <summary>Only the pilot's own input raises the pause signal: a drag does, a programmatic focus does not.</summary>
    [AvaloniaFact]
    public async Task ADrag_RaisesViewMovedByUser()
    {
        using var world = await World.OpenAsync();
        (Window window, StarMapControl map) = world.Show();
        int moves = 0;
        map.ViewMovedByUser += (_, _) => moves++;
        Point from = map.TranslatePoint(new Point(200, 200), window) ?? default;

        window.MouseDown(from, MouseButton.Left);
        window.MouseMove(from + new Vector(30, 30));
        window.MouseUp(from + new Vector(30, 30), MouseButton.Left);

        Assert.Equal(1, moves);
    }

    private static void _Settle()
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 700)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            UiDispatcher.UIThread.RunJobs();
            Thread.Sleep(16);
        }
    }

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class World : IDisposable
    {
        private readonly TestClientInstance _instance;
        private readonly FleetPositionSource _positions;
        private readonly MapTrailRecorder _recorder;
        private MapViewModel? _model;
        private MapGraphDto? _graph;
        private int _seconds;
        private Window? _window;

        private World(TestClientInstance instance)
        {
            _instance = instance;
            _positions = instance.Services.GetRequiredService<FleetPositionSource>();
            Clock = new MovableClock(T0);
            _recorder = new MapTrailRecorder(_positions, Clock);
        }

        public TimeProvider Clock { get; }

        public MapViewModel Model => _model ?? throw new InvalidOperationException("the map is not open");

        public static async Task<World> OpenAsync(bool openMap = true)
        {
            TestClientInstance instance = TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(MapFixture.Sde()));
            await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character(CharacterName, CharacterId));
            var world = new World(instance);
            if (openMap)
                await world.OpenMapAsync();
            return world;
        }

        public async Task OpenMapAsync()
        {
            _model = new MapViewModel(_instance.Services.GetRequiredService<IDispatcher>(), _instance.Services.GetRequiredService<ICharacterRegistry>(),
                _positions, _recorder, Clock);
            await _model.LoadAsync();
            _graph = _model.Graph;
        }

        public (Window, StarMapControl) Show()
        {
            _window = new MapWindow(Model);
            _window.Show();
            UiDispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            return (_window, _window.FindControl<StarMapControl>("Map") ?? throw new InvalidOperationException("no map in the window"));
        }

        public void Follow()
        {
            Model.SetFollowModeCommand.Execute(MapFollowMode.Character);
            Model.FollowCharacterCommand.Execute(Model.Characters.Single());
        }

        public void Jump(int solarSystemId, DateTimeOffset? at = null)
        {
            _positions.Observe(new FleetPositionDto(CharacterId, CharacterName, solarSystemId, PositionSource.Gamelog,
                at ?? T0.AddSeconds(++_seconds)));
            UiDispatcher.UIThread.RunJobs();
        }

        public int IndexOf(int solarSystemId)
        {
            MapGraphDto graph = _graph ?? throw new InvalidOperationException("the map is not open");
            Assert.True(graph.TryGetIndex(solarSystemId, out int index));
            return index;
        }

        /// <summary>Jita and then <paramref name="count"/> more systems, each the first not-yet-visited gate neighbour.</summary>
        public int[] WalkOnGates(int count)
        {
            MapGraphDto graph = _graph ?? throw new InvalidOperationException("the map is not open");
            var visited = new List<int> { IndexOf(Jita) };
            while (visited.Count <= count)
                visited.Add(graph.NeighboursOf(visited[^1]).ToArray().First(neighbour => !visited.Contains(neighbour)));
            return [.. visited.Select(index => graph.Systems[index].SolarSystemId)];
        }

        public string[] DataFiles() => [.. Directory.EnumerateFiles(_instance.DataDirectory, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.FullName)
            .Select(file => $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}")];

        public void Dispose()
        {
            _window?.Close();
            _model?.Dispose();
            _recorder.Dispose();
            _instance.Dispose();
        }
    }
}
