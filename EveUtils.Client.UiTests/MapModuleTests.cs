using Avalonia.Headless.XUnit;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Map;
using EveUtils.Shared.Modules.Map.Dtos;
using EveUtils.Shared.Modules.Map.Enums;
using EveUtils.Shared.Modules.Map.Queries;
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>ET-392: the map module over the real k-space map (<see cref="MapFixture"/>) through the real dispatcher —
/// the graph as the map draws it and the route planner as the wayfinder asks it.</summary>
public sealed class MapModuleTests
{
    private const int Jita = 30000142;
    private const int Amarr = 30002187;
    private const int Ichoriya = 30045329;

    public static IEnumerable<object[]> JitaToAmarr() =>
    [
        [RoutePreference.Shortest, Array.Empty<SecurityBand>(), 11, 10],
        [RoutePreference.Safer, Array.Empty<SecurityBand>(), 34, 34],
        [RoutePreference.Shortest, new[] { SecurityBand.Low, SecurityBand.Null }, 34, 34],
        [RoutePreference.LessSecure, Array.Empty<SecurityBand>(), 40, 6]
    ];

    /// <summary>The in-game routes: shortest runs through Ahbazon (0.4), safer stays in highsec the whole way.</summary>
    [AvaloniaTheory]
    [MemberData(nameof(JitaToAmarr))]
    public async Task PlanRoute_JitaToAmarr_MatchesTheKnownRoutes(RoutePreference preference, SecurityBand[] avoid, int jumps, int highsecJumps)
    {
        using TestClientInstance instance = TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(MapFixture.Sde()));

        Result<RouteDto> route = await _Dispatcher(instance).Query(
            new PlanRouteQuery(Jita, Amarr, preference, avoid.ToHashSet()), TestContext.Current.CancellationToken);

        Assert.True(route.IsSuccess);
        Assert.Equal(jumps, route.Value?.Jumps);
        Assert.Equal(highsecJumps, route.Value?.HighsecJumps);
    }

    /// <summary>Pochven only connects to itself, so there is no gate route in — an expected outcome, not an error.</summary>
    [AvaloniaFact]
    public async Task PlanRoute_IntoPochven_FailsWithRouteNotFound()
    {
        using TestClientInstance instance = TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(MapFixture.Sde()));

        Result<RouteDto> route = await _Dispatcher(instance).Query(
            new PlanRouteQuery(Jita, Ichoriya, RoutePreference.Shortest, new HashSet<SecurityBand>()), TestContext.Current.CancellationToken);

        Assert.False(route.IsSuccess);
        Assert.Equal(MessageCodes.RouteNotFound, Assert.Single(route.Messages).Code);
    }

    /// <summary>The seam for jump bridges and Thera: a registered edge source is a connection the route may use.</summary>
    [AvaloniaFact]
    public async Task PlanRoute_WithAnExtraEdge_UsesIt()
    {
        using TestClientInstance instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<ISdeAccessor>(MapFixture.Sde());
            services.AddSingleton<IRouteEdgeSource>(new FixedEdges(new RouteEdgeDto(Jita, Ichoriya)));
        });

        Result<RouteDto> route = await _Dispatcher(instance).Query(
            new PlanRouteQuery(Jita, Ichoriya, RoutePreference.Shortest, new HashSet<SecurityBand>()), TestContext.Current.CancellationToken);

        Assert.Equal(1, route.Value?.Jumps);
    }

    /// <summary>ET-298: the SDE read behind the graph is blocking, so it may never run on the UI thread that asks.</summary>
    [AvaloniaFact]
    public async Task GetMapGraph_FromTheUiThread_ReadsTheSdeOffIt()
    {
        FakeSdeAccessor sde = MapFixture.Sde();
        using TestClientInstance instance = TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(sde));
        int uiThread = Environment.CurrentManagedThreadId;

        Result<MapGraphDto> graph = await _Dispatcher(instance).Query(new GetMapGraphQuery(), TestContext.Current.CancellationToken);

        Assert.True(graph.IsSuccess);
        Assert.NotEqual(uiThread, Assert.Single(sde.MapReadThreadIds));
    }

    /// <summary>One graph per SDE build: asking again shares it, a new build after an import replaces it.</summary>
    [AvaloniaFact]
    public async Task GetMapGraph_IsSharedPerSdeBuild()
    {
        FakeSdeAccessor sde = MapFixture.Sde();
        using TestClientInstance instance = TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(sde));
        IDispatcher dispatcher = _Dispatcher(instance);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        MapGraphDto? first = (await dispatcher.Query(new GetMapGraphQuery(), cancellationToken)).Value;
        MapGraphDto? again = (await dispatcher.Query(new GetMapGraphQuery(), cancellationToken)).Value;
        sde.WithBuild(MapFixture.BuildNumber + 1);
        MapGraphDto? afterImport = (await dispatcher.Query(new GetMapGraphQuery(), cancellationToken)).Value;

        Assert.Same(first, again);
        Assert.NotSame(first, afterImport);
        Assert.Equal(MapFixture.BuildNumber + 1, afterImport?.BuildNumber);
    }

    [AvaloniaFact]
    public async Task GetMapGraph_WithoutMapData_FailsWithSdeOutdated()
    {
        using TestClientInstance instance = TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(new FakeSdeAccessor()));

        Result<MapGraphDto> graph = await _Dispatcher(instance).Query(new GetMapGraphQuery(), TestContext.Current.CancellationToken);

        Assert.Equal(MessageCodes.SdeOutdated, Assert.Single(graph.Messages).Code);
    }

    /// <summary>k-space only, and a region without a single stargate says so. Two Jove regions have none; the third,
    /// UUA-F4, has had 16 gates between its own systems since build 3552227 (none leading out), so it is not one.</summary>
    [AvaloniaFact]
    public async Task GetMapGraph_RealSde_HoldsKSpaceWithTheGatelessJoveRegionsMarked()
    {
        using TestClientInstance instance = TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(MapFixture.Sde()));

        MapGraphDto? graph = (await _Dispatcher(instance).Query(new GetMapGraphQuery(), TestContext.Current.CancellationToken)).Value;

        Assert.Equal((5485, 6989), (graph?.Systems.Count, graph?.Jumps.Count));
        Assert.Equal(["A821-A", "J7HZ-F"], graph?.Regions.Where(region => !region.HasGates).Select(region => region.Name).Order());
    }

    /// <summary>Region colours exist to tell neighbours apart, so no gate may join two regions of the same colour.</summary>
    [AvaloniaFact]
    public async Task GetMapGraph_RealSde_NeverGivesGateNeighboursTheSameColour()
    {
        using TestClientInstance instance = TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(MapFixture.Sde()));

        MapGraphDto graph = (await _Dispatcher(instance).Query(new GetMapGraphQuery(), TestContext.Current.CancellationToken)).Value
            ?? throw new InvalidOperationException("no graph");

        Assert.DoesNotContain(graph.Jumps.Where(jump => jump.Kind == MapJumpKind.CrossRegion), jump =>
            graph.Regions[graph.Systems[jump.FromIndex].RegionIndex].ColourIndex == graph.Regions[graph.Systems[jump.ToIndex].RegionIndex].ColourIndex);
    }

    /// <summary>The game rounds half up and never shows lowsec as 0.0 — the rule that makes a 0.45 system highsec.</summary>
    [AvaloniaTheory]
    [InlineData(0.45, 0.5, SecurityBand.High)]
    [InlineData(0.449, 0.4, SecurityBand.Low)]
    [InlineData(0.04, 0.1, SecurityBand.Low)]
    [InlineData(0.0, 0.0, SecurityBand.Null)]
    [InlineData(-0.26, -0.3, SecurityBand.Null)]
    public async Task GetMapGraph_ShowsSecurityTheWayTheGameDoes(double security, double shown, SecurityBand band)
    {
        FakeSdeAccessor sde = new FakeSdeAccessor().WithMap(new(
            [new(1, "Probe", security, 10, 100, 0, 0), new(2, "Other", 1.0, 10, 100, 10, 10)],
            [new(10, "C", 100, null)], [new(100, "R", null)], []), new Dictionary<int, string>());
        using TestClientInstance instance = TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(sde));

        MapSystemDto? probe = (await _Dispatcher(instance).Query(new GetMapGraphQuery(), TestContext.Current.CancellationToken)).Value?.Systems[0];

        Assert.Equal((shown, band), (probe?.DisplaySecurity, probe?.Band));
    }

    private static IDispatcher _Dispatcher(TestClientInstance instance) => instance.Services.GetRequiredService<IDispatcher>();

    private sealed class FixedEdges(params RouteEdgeDto[] edges) : IRouteEdgeSource
    {
        public Task<IReadOnlyList<RouteEdgeDto>> GetEdgesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RouteEdgeDto>>(edges);
    }
}
