using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Runtime.InteropServices;
using EveUtils.Client.Controls.Map;
using EveUtils.Client.Fleet;
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

/// <summary>ET-394: fleet members on the map — a badge per system with who and how recent — and following a fleet,
/// which frames every member on the 2D extent they cover. Driven the way it runs: the fleet is one this client takes part
/// in, its roster is the stored one plus the in-game fleet the boss's ESI poll read, and positions are sightings on the
/// merged position source.</summary>
public sealed class MapFollowFleetTests
{
    internal const int Jita = 30000142;
    internal const int Perimeter = 30000144;
    internal const int Amarr = 30002187;
    internal const int Dodixie = 30002659;

    internal const int Own = 91000001;
    internal const string OwnName = "Kaelen Voss";
    internal const int Mate = 91000002;
    internal const string MateName = "Mira Solenne";
    internal const int Outsider = 91000003;
    internal const string OutsiderName = "Oskar Vale";

    internal static readonly DateTimeOffset T0 = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Acceptance: following the fleet leaves every member inside the map, however far apart they are.</summary>
    [AvaloniaFact]
    public async Task FollowFleet_KeepsEveryMemberInsideTheMap()
    {
        using var world = await World.OpenAsync();
        (_, StarMapControl map) = world.Show();
        world.Sight(Own, Jita);
        world.Sight(Mate, Amarr);
        world.Sight(Outsider, Dodixie, PositionSource.EsiFleet);

        world.FollowFleet();
        MapFollowTests.Settle();

        Assert.All([Jita, Amarr, Dodixie], system => Assert.True(new Rect(map.Bounds.Size).Contains(map.ScreenPointOf(world.IndexOf(system))),
            $"{system} is at {map.ScreenPointOf(world.IndexOf(system))}, outside {map.Bounds.Size}"));
    }

    /// <summary>Acceptance: a fleet gathered in one system lands on 14×, system view with the neighbours around it.</summary>
    [AvaloniaFact]
    public async Task FollowFleet_AllInOneSystem_ZoomsTo14x()
    {
        using var world = await World.OpenAsync();
        (_, StarMapControl map) = world.Show();
        world.Sight(Own, Amarr);
        world.Sight(Mate, Amarr);
        world.Sight(Outsider, Amarr, PositionSource.EsiFleet);

        world.FollowFleet();
        MapFollowTests.Settle();

        Assert.Equal(14, map.ZoomLevel, precision: 2);
        Assert.Equal(MapDetailLevel.Systems, map.DetailLevel);
    }

    /// <summary>Acceptance: monotonic — a spread that is wider in either direction never zooms further in, from one
    /// system (14×) out to all of New Eden (1×). The zoom reads the 2D extent only, so jumps cannot bend it.</summary>
    [AvaloniaTheory]
    [InlineData(0, 0, 800, 0)]
    [InlineData(800, 0, 2400, 0)]
    [InlineData(0, 600, 0, 1800)]
    [InlineData(2400, 0, 2400, 2000)]
    [InlineData(2400, 2000, 10000, 9000)]
    public void FleetFrameScale_AWiderSpread_NeverZoomsFurtherIn(double narrowX, double narrowY, double wideX, double wideY)
    {
        var viewport = new Size(894, 640);
        const double fit = 0.08;

        double narrow = StarMapControl.FleetFrameScale(narrowX, narrowY, viewport, fit);
        double wide = StarMapControl.FleetFrameScale(wideX, wideY, viewport, fit);

        Assert.True(wide <= narrow, $"{wideX}×{wideY} zooms to {wide / fit:0.00}×, closer than {narrowX}×{narrowY} at {narrow / fit:0.00}×");
        Assert.InRange(wide, fit, fit * 14);
    }

    /// <summary>Acceptance: someone in the in-game fleet who does not use EVE Together — not on the roster, no name in
    /// the sighting — gets a badge under their ESI name and is part of the frame.</summary>
    [AvaloniaFact]
    public async Task AnInGameMemberWhoIsNotOnTheRoster_ShowsOnTheMapAndIsFramed()
    {
        using var world = await World.OpenAsync();
        world.Sight(Own, Jita);
        world.Sight(Outsider, Dodixie, PositionSource.EsiFleet);

        world.FollowFleet();
        UiDispatcher.UIThread.RunJobs();

        MapFleetBadge badge = Assert.Single(world.Model.FleetBadges, badge => badge.SystemIndex == world.IndexOf(Dodixie));
        Assert.Equal(OutsiderName, Assert.Single(badge.Members).Name);
        Assert.Contains(world.IndexOf(Dodixie), world.Model.FocusRequest!.SystemIndexes);
        Assert.Equal(MapFraming.Fleet, world.Model.FocusRequest.Framing);
    }

    /// <summary>Acceptance, the expiry as documented on <see cref="MapViewModel.FleetPositionExpiry"/>: a position no source
    /// repeated for ten minutes is left off the badges and out of the frame, and counted as without a position — except
    /// one from your own game log, which only writes on a jump. The outsider has no position at all and is always counted.</summary>
    [AvaloniaTheory]
    [InlineData(PositionSource.EsiFleet, 9, true)]
    [InlineData(PositionSource.EsiFleet, 11, false)]
    [InlineData(PositionSource.FleetMetric, 11, false)]
    [InlineData(PositionSource.Gamelog, 60, true)]
    public async Task APositionOlderThanTheExpiry_IsLeftOffTheMap_UnlessItIsFromTheGameLog(PositionSource source, int minutesOld, bool shown)
    {
        using var world = await World.OpenAsync();
        world.Sight(Mate, Jita);
        world.Sight(Own, Amarr, source, T0 - TimeSpan.FromMinutes(minutesOld));

        world.FollowFleet();

        Assert.Equal(shown, world.Model.FleetBadges.Any(badge => badge.SystemIndex == world.IndexOf(Amarr)));
        Assert.Equal(shown, world.Model.FocusRequest!.SystemIndexes.Contains(world.IndexOf(Amarr)));
        Assert.Equal(shown ? "1 member without a position from the last 10 minutes" : "2 members without a position from the last 10 minutes",
            world.Model.FleetUnplacedText);
    }

    /// <summary>Hovering the badge opens the popover of its system, listing each member with how old their position is, alphabetical.</summary>
    [AvaloniaFact]
    public async Task HoveringABadge_ListsItsMembersWithTheAgeOfTheirPosition()
    {
        using var world = await World.OpenAsync();
        (Window window, StarMapControl map) = world.Show();
        map.PopoverDelay = TimeSpan.Zero;
        world.Sight(Mate, Amarr, PositionSource.FleetMetric, T0 - TimeSpan.FromSeconds(12));
        world.Sight(Outsider, Amarr, PositionSource.EsiFleet, T0 - TimeSpan.FromMinutes(3));
        world.FollowFleet();
        MapFollowTests.Settle();

        Point system = map.ScreenPointOf(world.IndexOf(Amarr));
        window.MouseMove(map.TranslatePoint(new Point(system.X + 10, system.Y - 10), window) ?? default);

        Assert.StartsWith("Amarr 0.9\n", map.PopoverText);
        Assert.Contains($"\n{MateName} · 12s ago\n{OutsiderName} · 3 min ago\n", map.PopoverText);
    }

    /// <summary>As with a character (ET-393): nothing is followed until a fleet is picked, moving the map pauses, a member's
    /// jump then leaves the map alone, and RESUME frames the fleet again.</summary>
    [AvaloniaFact]
    public async Task FollowFleet_IsAnExplicitPick_PausesWhenTheMapIsMoved_AndResumes()
    {
        using var world = await World.OpenAsync();
        world.Sight(Own, Jita);
        world.Sight(Mate, Jita);

        world.Model.SetFollowModeCommand.Execute(MapFollowMode.Fleet);
        UiDispatcher.UIThread.RunJobs();
        Assert.True(world.Model.NeedsFleetPick);
        Assert.Null(world.Model.FocusRequest);

        world.FollowFleet();
        world.Model.PauseFollow();
        MapFocusRequest? paused = world.Model.FocusRequest;
        world.Sight(Mate, Perimeter);

        Assert.Same(paused, world.Model.FocusRequest);
        Assert.Equal("Map: following fleet Wolfpack (paused)", world.Model.FollowStatusText);

        world.Model.ResumeFollowCommand.Execute(null);

        Assert.Equal([world.IndexOf(Jita), world.IndexOf(Perimeter)], world.Model.FocusRequest!.SystemIndexes.Order());
        Assert.Equal("FOLLOWING FLEET WOLFPACK", world.Model.FollowChipText);
    }

    /// <summary>ET-398 acceptance: the commander's marker stands on their own system beside the count badge, also when others
    /// are there, and the count still counts them.</summary>
    [AvaloniaFact]
    public async Task TheCommander_IsMarkedOnTheirSystem_BesideTheBadge_EvenWithOthersThere()
    {
        using var world = await World.OpenAsync(rosterCommander: Mate);
        (_, StarMapControl map) = world.Show();
        world.Sight(Own, Jita);
        world.Sight(Mate, Amarr);
        world.Sight(Outsider, Amarr, PositionSource.EsiFleet);
        world.FollowFleet();
        MapFollowTests.Settle();

        MapFleetBadge amarr = Assert.Single(world.Model.FleetBadges, badge => badge.SystemIndex == world.IndexOf(Amarr));
        Assert.Equal(MateName, amarr.Commander?.Name);
        Assert.Equal(2, amarr.Members.Count);
        Assert.Null(Assert.Single(world.Model.FleetBadges, badge => badge.SystemIndex == world.IndexOf(Jita)).Commander);

        Point system = map.ScreenPointOf(world.IndexOf(Amarr));
        Point star = map.CommanderPointOf(world.IndexOf(Amarr));
        Assert.Equal(system.X - 10, star.X, 0.5);
        Assert.Equal(system.Y - 10, star.Y, 0.5);
        Assert.True(system.X + 10 - star.X >= 18, "the star and the count badge overlap");
        Assert.Equal("", world.Model.FleetCommanderText);
    }

    [AvaloniaFact]
    public async Task ThePopover_ListsTheCommanderFirst_LabelledFc()
    {
        using var world = await World.OpenAsync(rosterCommander: Mate);
        (Window window, StarMapControl map) = world.Show();
        map.PopoverDelay = TimeSpan.Zero;
        world.Sight(Own, Amarr, PositionSource.FleetMetric, T0 - TimeSpan.FromSeconds(2));
        world.Sight(Mate, Amarr, PositionSource.FleetMetric, T0 - TimeSpan.FromSeconds(40));
        world.FollowFleet();
        MapFollowTests.Settle();

        Point system = map.ScreenPointOf(world.IndexOf(Amarr));
        window.MouseMove(map.TranslatePoint(new Point(system.X + 10, system.Y - 10), window) ?? default);

        Assert.StartsWith("Amarr 0.9\n", map.PopoverText);
        Assert.Contains($"\nFC · {MateName} · 40s ago\n{OwnName} · 2s ago\n", map.PopoverText);
    }

    /// <summary>Precedence, as documented on <see cref="MapFleetSource.CommanderOf"/>: the ET roster's FC, and only when it
    /// names nobody the in-game boss.</summary>
    [AvaloniaTheory]
    [InlineData(Own, Mate, Own)]
    [InlineData(null, Mate, Mate)]
    [InlineData(Mate, null, Mate)]
    [InlineData(null, null, null)]
    public async Task TheRostersFc_WinsOverTheInGameBoss_WhichOnlyStandsIn(int? roster, int? boss, int? expected)
    {
        using var world = await World.OpenAsync(roster, boss);
        var fleet = world.Model.Fleets.Single().Fleet;

        Assert.Equal(expected, world.Instance.Services.GetRequiredService<IMapFleetSource>().CommanderOf(fleet));
    }

    [AvaloniaFact]
    public async Task TheInGameBoss_IsMarked_WhenTheRosterNamesNoFc()
    {
        using var world = await World.OpenAsync(inGameBoss: Outsider);
        world.Sight(Own, Jita);
        world.Sight(Outsider, Dodixie, PositionSource.EsiFleet);
        world.FollowFleet();

        Assert.Equal(OutsiderName, Assert.Single(world.Model.FleetBadges, badge => badge.SystemIndex == world.IndexOf(Dodixie)).Commander?.Name);
    }

    /// <summary>ET-398 acceptance: no position for the commander — never seen, or a position that has expired — means no
    /// marker anywhere and the notice in the FOLLOW block and on the card.</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACommanderWithoutAPosition_HasNoMarker_AndTheNoticeSaysSo(bool expired)
    {
        using var world = await World.OpenAsync(rosterCommander: Mate);
        world.Sight(Own, Jita);
        if (expired)
            world.Sight(Mate, Jita, PositionSource.EsiFleet, T0 - TimeSpan.FromMinutes(11));

        world.FollowFleet();

        Assert.All(world.Model.FleetBadges, badge => Assert.Null(badge.Commander));
        Assert.Equal("FC position unknown", world.Model.FleetCommanderText);
        Assert.True(world.Model.HasFleetCommanderNotice);

        world.Sight(Mate, Amarr);

        Assert.Equal("", world.Model.FleetCommanderText);
        Assert.NotNull(Assert.Single(world.Model.FleetBadges, badge => badge.SystemIndex == world.IndexOf(Amarr)).Commander);
    }

    /// <summary>ET-398 acceptance: the commander is inside the follow-fleet frame, on the roster or not, and the frame asks
    /// for nothing beyond the members' own systems.</summary>
    [AvaloniaFact]
    public async Task FollowFleet_FramesTheCommander_WhoIsNotOnTheRoster()
    {
        using var world = await World.OpenAsync(inGameBoss: Outsider);
        (_, StarMapControl map) = world.Show();
        world.Sight(Own, Jita);
        world.Sight(Outsider, Dodixie, PositionSource.EsiFleet);
        world.FollowFleet();
        MapFollowTests.Settle();

        Assert.Equal([world.IndexOf(Jita), world.IndexOf(Dodixie)], world.Model.FocusRequest!.SystemIndexes.Order());
        Assert.True(new Rect(map.Bounds.Size).Contains(map.CommanderPointOf(world.IndexOf(Dodixie))));
    }

    /// <summary>ET-398 acceptance: at every level of detail — the whole of New Eden included — the star is really painted on
    /// the commander's system, measured off a rendered frame.</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheCommandersStar_IsPainted_AtEveryLevelOfDetail(bool zoomedIn)
    {
        using var world = await World.OpenAsync(rosterCommander: Mate);
        (_, StarMapControl map) = world.Show();
        world.Sight(Mate, Amarr);
        world.Sight(Own, Jita);
        world.FollowFleet();
        world.Model.PauseFollow();
        if (zoomedIn)
            world.Model.FocusRequest = new MapFocusRequest([world.IndexOf(Amarr)], 12);
        else
            map.ZoomToFit();
        MapFollowTests.Settle();

        Assert.Equal(zoomedIn ? MapDetailLevel.Systems : MapDetailLevel.Regions, map.DetailLevel);
        Point at = map.CommanderPointOf(world.IndexOf(Amarr));
        Assert.Equal(_AccentOf(map), _PixelAt(map, at));
    }

    private static Color _AccentOf(StarMapControl map) =>
        map.TryFindResource("AccentBrightBrush", map.ActualThemeVariant, out object? found) && found is ISolidColorBrush brush ? brush.Color : MapPalette.Text;

    private static Color _PixelAt(StarMapControl map, Point at)
    {
        using var surface = new RenderTargetBitmap(new PixelSize((int)map.Bounds.Width, (int)map.Bounds.Height));
        surface.Render(map);
        var pixel = new byte[4];
        surface.CopyPixels(new PixelRect((int)Math.Round(at.X), (int)Math.Round(at.Y), 1, 1), Marshal.UnsafeAddrOfPinnedArrayElement(pixel, 0), 4, 4);
        return surface.Format == PixelFormat.Rgba8888 ? Color.FromRgb(pixel[0], pixel[1], pixel[2]) : Color.FromRgb(pixel[2], pixel[1], pixel[0]);
    }

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    internal sealed class World : IDisposable
    {
        private readonly TestClientInstance _instance;
        private readonly FleetPositionSource _positions;
        private readonly MapTrailRecorder _recorder;
        private readonly MovableClock _clock = new(T0);
        private MapViewModel? _model;
        private Window? _window;
        private int _seconds;

        private World(TestClientInstance instance)
        {
            _instance = instance;
            _positions = instance.Services.GetRequiredService<FleetPositionSource>();
            _recorder = new MapTrailRecorder(_positions, _clock);
        }

        public TestClientInstance Instance => _instance;

        public MapViewModel Model => _model ?? throw new InvalidOperationException("the map is not open");

        // A client-only fleet this client takes part in: you and an EVE Together mate on the roster, and an outsider the
        // in-game fleet holds as well.
        public static async Task<World> OpenAsync(int? rosterCommander = null, int? inGameBoss = null)
        {
            var names = new FakeExternalLookup { [Outsider] = OutsiderName, [Mate] = MateName };
            TestClientInstance instance = TestClientInstance.Create(services => services
                .AddSingleton<ISdeAccessor>(MapFixture.Sde())
                .AddSingleton<IExternalCharacterLookup>(names));
            await instance.Services.GetRequiredService<ICharacterRegistry>().AddOrUpdateAsync(new Character(OwnName, Own));

            var repository = instance.Services.GetRequiredService<IFleetRepository>();
            long fleetId = await repository.AddAsync(new FleetEntity { Name = "Wolfpack", CreatorCharacterId = Own, State = FleetState.Active });
            await repository.AddMemberAsync(new FleetMember { FleetId = fleetId, CharacterId = Own, WingId = -1, SquadId = -1 });
            await repository.AddMemberAsync(new FleetMember { FleetId = fleetId, CharacterId = Mate, WingId = -1, SquadId = -1 });
            instance.Services.GetRequiredService<IFleetParticipation>().Set(
                [new FleetParticipant(Own, fleetId, ClientOnly: true, FleetCommanderCharacterId: rosterCommander, FleetName: "Wolfpack")]);
            instance.Services.GetRequiredService<InGameFleetRosters>().Record(InGameFleetRosters.KeyOf(null, fleetId), [Own, Mate, Outsider], inGameBoss);

            var world = new World(instance);
            world._model = new MapViewModel(instance.Services.GetRequiredService<IDispatcher>(), instance.Services.GetRequiredService<ICharacterRegistry>(),
                world._positions, world._recorder, instance.Services.GetRequiredService<IMapFleetSource>(), world._clock);
            await world._model.LoadAsync();
            return world;
        }

        public (Window, StarMapControl) Show()
        {
            _window = new MapWindow(Model);
            _window.Show();
            UiDispatcher.UIThread.RunJobs();
            return (_window, _window.FindControl<StarMapControl>("Map") ?? throw new InvalidOperationException("no map in the window"));
        }

        public void FollowFleet()
        {
            Model.SetFollowModeCommand.Execute(MapFollowMode.Fleet);
            UiDispatcher.UIThread.RunJobs();
            Model.FollowFleetCommand.Execute(Model.Fleets.Single());
            UiDispatcher.UIThread.RunJobs();
        }

        public void Sight(int characterId, int solarSystemId, PositionSource source = PositionSource.FleetMetric, DateTimeOffset? at = null)
        {
            string? name = source == PositionSource.Gamelog ? OwnName : null;
            _positions.Observe(new FleetPositionDto(characterId, name, solarSystemId, source, at ?? T0.AddSeconds(-60 + ++_seconds)));
            UiDispatcher.UIThread.RunJobs();
        }

        public int IndexOf(int solarSystemId)
        {
            MapGraphDto graph = Model.Graph ?? throw new InvalidOperationException("the map is not open");
            Assert.True(graph.TryGetIndex(solarSystemId, out int index));
            return index;
        }

        public void Dispose()
        {
            _window?.Close();
            _model?.Dispose();
            _recorder.Dispose();
            _instance.Dispose();
        }
    }
}
