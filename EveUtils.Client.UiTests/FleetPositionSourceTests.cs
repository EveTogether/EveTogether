using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EveUtils.Client.Esi;
using EveUtils.Client.Fleet;
using EveUtils.Client.Gamelog;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Dtos;
using EveUtils.Shared.Modules.Fleet.Enums;
using EveUtils.Shared.Modules.Fleet.Events;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Sde;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-394: the positions the world map will draw — the newest per character over the own gamelog, fleet mates'
/// location samples and the in-game fleet roster, in memory only.
/// </summary>
public sealed class FleetPositionSourceTests
{
    private const int Jita = 30000142;
    private const int Amarr = 30002187;
    private const int Perimeter = 30000144;

    private const int Own = 91000001;
    private const string OwnName = "Own Pilot";
    private const int Mate = 91000002;
    private const int Outsider = 91000003;

    private static readonly DateTimeOffset T0 = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Merge_ThreeSources_KeepsTheNewestPositionPerCharacter()
    {
        using var instance = _CreateInstance();
        var positions = instance.Services.GetRequiredService<FleetPositionSource>();
        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        var bus = instance.Services.GetRequiredService<IEventBus>();

        gamelog.MapCharacter(Own, OwnName);
        gamelog.SetLocation(OwnName, "Jita", T0.UtcDateTime);
        await _PublishLocationAsync(bus, Mate, Amarr, null, T0);
        await _PublishLocationAsync(bus, Mate, Perimeter, null, T0.AddSeconds(30));
        positions.ObserveEsiFleet(
        [
            new EsiFleetMember { CharacterId = Own, SolarSystemId = Amarr },
            new EsiFleetMember { CharacterId = Mate, SolarSystemId = Amarr },
            new EsiFleetMember { CharacterId = Outsider, SolarSystemId = Jita }
        ], T0.AddSeconds(20));

        var byCharacter = positions.GetPositions().ToDictionary(position => position.CharacterId);
        Assert.Equal(3, byCharacter.Count);
        Assert.Equal((Amarr, PositionSource.EsiFleet), (byCharacter[Own].SolarSystemId, byCharacter[Own].Source));
        Assert.Equal(OwnName, byCharacter[Own].Name);
        Assert.Equal((Perimeter, PositionSource.FleetMetric), (byCharacter[Mate].SolarSystemId, byCharacter[Mate].Source));
        Assert.Equal(T0.AddSeconds(30), byCharacter[Mate].ObservedAt);
        Assert.Equal((Jita, PositionSource.EsiFleet), (byCharacter[Outsider].SolarSystemId, byCharacter[Outsider].Source));
        Assert.Null(byCharacter[Outsider].Name);
    }

    [Fact]
    public void Observe_OlderSighting_IsIgnored()
    {
        using var positions = _CreateSource();

        positions.Observe(_Position(Mate, Amarr, PositionSource.FleetMetric, T0.AddSeconds(10)));
        var accepted = positions.Observe(_Position(Mate, Jita, PositionSource.FleetMetric, T0));

        Assert.False(accepted);
        Assert.Equal(Amarr, Assert.Single(positions.GetPositions()).SolarSystemId);
    }

    [Fact]
    public void Observe_CachedEsiAnswerRightAfterAJump_DoesNotMoveThePilotBack()
    {
        using var positions = _CreateSource();
        positions.Observe(_Position(Own, Perimeter, PositionSource.Gamelog, T0));

        var accepted = positions.Observe(_Position(Own, Jita, PositionSource.EsiLocation, T0.AddSeconds(4)));

        Assert.False(accepted);
        Assert.Equal((Perimeter, PositionSource.Gamelog), _Only(positions));
    }

    [Fact]
    public void Observe_EsiAnswerLongAfterTheLastLiveSighting_MovesThePilot()
    {
        using var positions = _CreateSource();
        positions.Observe(_Position(Own, Perimeter, PositionSource.Gamelog, T0));

        var accepted = positions.Observe(
            _Position(Own, Jita, PositionSource.EsiLocation, T0 + FleetPositionSource.CachedSourceLag));

        Assert.True(accepted);
        Assert.Equal((Jita, PositionSource.EsiLocation), _Only(positions));
    }

    [Fact]
    public void Observe_NewPosition_RaisesPositionChanged_AndASameSystemRepeatOnlyRefreshesTheTime()
    {
        using var positions = _CreateSource();
        var raised = new List<FleetPositionDto>();
        positions.PositionChanged += raised.Add;

        positions.Observe(_Position(Mate, Amarr, PositionSource.FleetMetric, T0));
        positions.Observe(_Position(Mate, Amarr, PositionSource.EsiFleet, T0.AddSeconds(5)));
        positions.Observe(_Position(Mate, Jita, PositionSource.FleetMetric, T0.AddSeconds(9)));

        Assert.Equal([Amarr, Jita], raised.Select(position => position.SolarSystemId));
        var current = Assert.Single(positions.GetPositions());
        Assert.Equal(PositionSource.FleetMetric, current.Source);
        Assert.Equal(T0.AddSeconds(9), current.ObservedAt);
    }

    [Fact]
    public void Observe_SameSystemFromACachedSource_KeepsTheLiveSourceButRefreshesTheTime()
    {
        using var positions = _CreateSource();
        positions.Observe(_Position(Own, Amarr, PositionSource.Gamelog, T0));

        positions.Observe(_Position(Own, Amarr, PositionSource.EsiLocation, T0.AddSeconds(6)));

        var current = Assert.Single(positions.GetPositions());
        Assert.Equal(PositionSource.Gamelog, current.Source);
        Assert.Equal(T0.AddSeconds(6), current.ObservedAt);
    }

    [Fact]
    public async Task LocationSample_WithASystemId_UsesTheIdOverTheName()
    {
        using var instance = _CreateInstance();
        var positions = instance.Services.GetRequiredService<FleetPositionSource>();

        await _PublishLocationAsync(instance.Services.GetRequiredService<IEventBus>(), Mate, Amarr, "Jita", T0);

        Assert.Equal((Amarr, PositionSource.FleetMetric), _Only(positions));
    }

    [Fact]
    public async Task LocationSample_FromAnOlderClientWithOnlyAName_IsResolvedThroughTheSde()
    {
        using var instance = _CreateInstance();
        var positions = instance.Services.GetRequiredService<FleetPositionSource>();

        await _PublishLocationAsync(instance.Services.GetRequiredService<IEventBus>(), Mate, 0, "Perimeter", T0);

        Assert.Equal((Perimeter, PositionSource.FleetMetric), _Only(positions));
    }

    [Fact]
    public async Task LocationSample_WithANameTheSdeDoesNotKnow_AddsNothing()
    {
        using var instance = _CreateInstance();
        var positions = instance.Services.GetRequiredService<FleetPositionSource>();

        await _PublishLocationAsync(instance.Services.GetRequiredService<IEventBus>(), Mate, 0, "Nowhere", T0);

        Assert.Empty(positions.GetPositions());
    }

    [Fact]
    public async Task LocationSample_FromInsideAnAbyssalRun_LeavesThePositionAsItWas()
    {
        using var instance = _CreateInstance();
        var positions = instance.Services.GetRequiredService<FleetPositionSource>();
        var bus = instance.Services.GetRequiredService<IEventBus>();
        await _PublishLocationAsync(bus, Mate, Amarr, "Amarr", T0);

        await bus.PublishAsync(new FleetMetricEvent(new MetricSample(Mate, 7, MetricKind.Location, Jita,
            T0.AddSeconds(30).ToUnixTimeMilliseconds(), "Jita", T0.AddSeconds(20).ToUnixTimeMilliseconds())),
            cancellationToken: TestContext.Current.CancellationToken);

        var current = Assert.Single(positions.GetPositions());
        Assert.Equal((Amarr, T0), (current.SolarSystemId, current.ObservedAt));
    }

    [Fact]
    public void LocationMetricSource_SendsTheSystemIdAndKeepsTheNameForOlderClients()
    {
        using var instance = _CreateInstance();
        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        var source = instance.Services.GetRequiredService<LocationMetricSource>();
        gamelog.MapCharacter(Own, OwnName);

        Assert.Empty(source.Sample(7, Own, 1_000));

        gamelog.SetLocation(OwnName, "Amarr", T0.UtcDateTime);

        var sample = Assert.Single(source.Sample(7, Own, 1_000));
        Assert.Equal(MetricKind.Location, sample.Kind);
        Assert.Equal(Amarr, sample.Value);
        Assert.Equal("Amarr", sample.Text);
    }

    [Fact]
    public void LocationMetricSource_SystemTheSdeDoesNotKnow_StillSendsTheName()
    {
        using var instance = _CreateInstance();
        var gamelog = instance.Services.GetRequiredService<GamelogClientService>();
        gamelog.MapCharacter(Own, OwnName);
        gamelog.SetLocation(OwnName, "Nowhere", T0.UtcDateTime);

        var sample = Assert.Single(instance.Services.GetRequiredService<LocationMetricSource>().Sample(7, Own, 1_000));

        Assert.Equal(0, sample.Value);
        Assert.Equal("Nowhere", sample.Text);
    }

    private static TestClientInstance _CreateInstance() =>
        TestClientInstance.Create(services => services.AddSingleton<ISdeAccessor>(_Sde()));

    private static FleetPositionSource _CreateSource() =>
        new(new InProcessEventBus(), new SolarSystemIdResolver(_Sde()));

    private static FakeSdeAccessor _Sde() => new FakeSdeAccessor()
        .AddSolarSystem(new SdeSolarSystem(Jita, "Jita", 0.9))
        .AddSolarSystem(new SdeSolarSystem(Amarr, "Amarr", 1.0))
        .AddSolarSystem(new SdeSolarSystem(Perimeter, "Perimeter", 1.0));

    private static FleetPositionDto _Position(int characterId, int solarSystemId, PositionSource source, DateTimeOffset at) =>
        new(characterId, null, solarSystemId, source, at);

    private static (int SolarSystemId, PositionSource Source) _Only(FleetPositionSource positions)
    {
        var position = Assert.Single(positions.GetPositions());
        return (position.SolarSystemId, position.Source);
    }

    private static Task _PublishLocationAsync(IEventBus bus, int characterId, int solarSystemId, string? name, DateTimeOffset at) =>
        bus.PublishAsync(new FleetMetricEvent(
            new MetricSample(characterId, 7, MetricKind.Location, solarSystemId, at.ToUnixTimeMilliseconds(), name)),
            cancellationToken: TestContext.Current.CancellationToken);
}
