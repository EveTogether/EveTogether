using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Gamelog;
using EveUtils.Client.LocalApi;
using EveUtils.Client.LocalApi.Dtos;
using EveUtils.Client.Platform;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Fleet.Metrics;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Gamelog.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>What the Local API says about combat beyond DPS: reps, neut and cap each way, the application verdict, and
/// the recent history a widget gets on connect from the same buffer the DPS graph draws.</summary>
public class LocalApiCombatTests
{
    private const string Pilot = "Combat Pilot";

    [Fact]
    public async Task Metrics_SupportTraffic_SplitsEveryDirection()
    {
        var (queries, gamelog, _) = _Build();
        gamelog.AddRemoteRep(Pilot, outgoing: false, amount: 500);
        gamelog.AddRemoteRep(Pilot, outgoing: true, amount: 300);
        gamelog.AddNeut(Pilot, outgoing: true, amount: 200);
        gamelog.AddCapTransfer(Pilot, outgoing: false, amount: 100);

        var metrics = Assert.Single(await queries.GetMetricsAsync(TestContext.Current.CancellationToken));

        Assert.True(metrics.RepIn > 0);
        Assert.True(metrics.RepOut > 0);
        Assert.True(metrics.NeutOut > 0);
        Assert.Equal(0, metrics.NeutIn);
        Assert.True(metrics.CapIn > 0);
        Assert.Equal(0, metrics.CapOut);
        Assert.Equal(metrics.NeutIn + metrics.NeutOut, metrics.NeutPerSecond);
        Assert.Equal(metrics.CapIn + metrics.CapOut, metrics.CapPerSecond);
    }

    [Fact]
    public async Task Metrics_NoShooting_ApplicationIsIdle()
    {
        var (queries, _, _) = _Build();

        var metrics = Assert.Single(await queries.GetMetricsAsync(TestContext.Current.CancellationToken));

        Assert.Equal(new ApplicationDto("idle", null, null), metrics.Application);
    }

    [Theory]
    [InlineData(ApplicationVerdict.SweetSpot, 91d, "sweetSpot")]
    [InlineData(ApplicationVerdict.NotEnoughShots, null, "notEnoughShots")]
    public void ApplicationDto_Verdict_IsCamelCaseText(ApplicationVerdict verdict, double? percent, string expected)
    {
        var dto = ApplicationDto.FromSummary(new ApplicationSummary(verdict, percent, "Mega Pulse Laser II 91%"));

        Assert.Equal(expected, dto.Verdict);
        Assert.Equal(percent, dto.Percent);
    }

    [Fact]
    public async Task Metrics_Serialized_KeepsEveryV1FieldAndAddsTheNewOnes()
    {
        var (queries, _, _) = _Build();
        var metrics = await queries.GetMetricsAsync(TestContext.Current.CancellationToken);

        var json = JsonSerializer.SerializeToElement(metrics[0], new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        foreach (var field in new[] { "characterId", "characterName", "running", "dpsOut", "dpsIn", "neutPerSecond",
                     "capPerSecond", "bountyTotal", "kills", "location", "peakDps", "locationWatchActive", "locationStatus",
                     "repIn", "repOut", "neutIn", "neutOut", "capIn", "capOut", "application" })
            Assert.True(json.TryGetProperty(field, out _), field);
    }

    [AvaloniaFact]
    public async Task History_FramesDrawnByTheGraph_ComeBackOnePerSecond()
    {
        var (queries, _, history) = _Build();
        var tracker = new DpsViewModel(Pilot, isSelf: true, history);
        for (var frame = 0; frame < 61; frame++)
            tracker.ApplyRates(new CombatRates(Dealt: 100, Received: 40, NeutIn: 0, NeutOut: 7, CapIn: 0, CapOut: 0, RepIn: 25, RepOut: 0));

        var running = await queries.GetMetricsAsync(TestContext.Current.CancellationToken);
        var character = Assert.Single(queries.GetHistory(running));

        Assert.Equal(Pilot, character.CharacterName);
        Assert.Equal(1, character.IntervalSeconds);
        Assert.Equal(3, character.DpsOut.Length);
        Assert.All(character.DpsOut, value => Assert.Equal(100, value));
        Assert.All(character.DpsIn, value => Assert.Equal(40, value));
        Assert.All(character.RepIn, value => Assert.Equal(25, value));
        Assert.All(character.NeutOut, value => Assert.Equal(7, value));
        Assert.All(character.CapOut, value => Assert.Equal(0, value));
        Assert.Equal(character.DpsOut.Length, character.CapIn.Length);
    }

    [AvaloniaFact]
    public void History_LongerThanTheBuffer_KeepsTheNewestFiveMinutes()
    {
        var history = new CombatHistory();
        var tracker = new DpsViewModel(Pilot, isSelf: true, history);
        for (var frame = 0; frame < CombatHistory.Capacity + 90; frame++)
            tracker.ApplyRates(new CombatRates(frame < CombatHistory.Capacity ? 10 : 500, 0, 0, 0, 0, 0, 0, 0));

        var dps = history.PerSecond(Pilot.ToLowerInvariant(), MetricKind.Dps, LocalApiQueries.HistorySeconds);

        Assert.Equal(LocalApiQueries.HistorySeconds, dps.Length);
        Assert.Equal(dps.Max(), dps[^1]); // the newest second is the loudest: the wrap dropped the oldest, not the newest
    }

    [Fact]
    public void History_CharacterNeverDrawn_IsEmpty()
    {
        Assert.Empty(new CombatHistory().PerSecond("Nobody", MetricKind.Dps, 300));
    }

    [AvaloniaFact]
    public void Graph_WithSharedHistory_DrawsExactlyWhatItDrewWithout()
    {
        var plain = new DpsViewModel(Pilot, isSelf: true);
        var shared = new DpsViewModel(Pilot, isSelf: true, new CombatHistory());
        for (var frame = 0; frame < 200; frame++)
        {
            var rates = new CombatRates(frame % 50 * 10, frame % 30, 0, frame % 7, 0, frame % 9, frame % 11, 0);
            plain.ApplyRates(rates);
            shared.ApplyRates(rates);
        }

        for (var line = 0; line < plain.Series.Count; line++)
            Assert.Equal(plain.Series[line].Values, shared.Series[line].Values);
        Assert.Equal(plain.Dealt, shared.Dealt);
        Assert.Equal(plain.GraphRevision, shared.GraphRevision);
    }

    private static (LocalApiQueries Queries, GamelogClientService Gamelog, CombatHistory History) _Build()
    {
        var presence = new EveClientPresenceService(NullLogger<EveClientPresenceService>.Instance, new RunningProbe());
        presence.PollOnce();
        var history = new CombatHistory();
        var services = new ServiceCollection();
        services.AddSingleton(presence);
        services.AddSingleton(history);
        services.AddSingleton(provider => new GamelogClientService(provider, new InProcessEventBus()));
        var provider = services.BuildServiceProvider();
        return (new LocalApiQueries(provider, new LocalApiPrivacy(provider, false)),
            provider.GetRequiredService<GamelogClientService>(), history);
    }

    private sealed class RunningProbe : IEveClientProbe
    {
        public EveClientEvidence Probe() => new(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Pilot }, new HashSet<int>());
        public int RunningClientCount() => 1;
        public bool Activate(string name) => false;
    }
}
