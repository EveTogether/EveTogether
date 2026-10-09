using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Gamelog.Parsing;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Entities;
using EveUtils.Shared.Modules.Runs.Queries;
using EveUtils.Shared.Modules.Runs.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-467: what SAVE keeps of a run's combat, built from real gamelog lines — run 5 of 18 Sep 2026, a T3 abyssal in
/// the window ET stored for it. Repair, capacitor and own-neut lines are the client's templates (ET-467 AC0).
/// </summary>
public sealed class RunCombatTelemetryTests
{
    internal static readonly DateTime RunStart = new(2026, 9, 18, 18, 29, 3);
    internal static readonly DateTime RunStop = new(2026, 9, 18, 18, 39, 39);

    /// <summary>
    /// Every combat line of that run, parsed; shared with the detail screen's rendered test (ET-468).
    /// </summary>
    internal static GameLogEvent[] RealRunEvents() => [.. File.ReadLines(_Fixture("abyssal-t3-run.txt")).Select(_Parse)];

    private const string HitOut = "<color=0xff00ffff><b>998</b> <color=0x77ffffff><font size=10>to</font> <b><color=0xffffffff>Ephialtes Dissipator</b><font size=10><color=0x77ffffff> - Nova Fury Light Missile - Hits";

    public static TheoryData<string, string> OneLineEach => new()
    {
        { $"[ 2026.09.18 18:34:32 ] (combat) {HitOut}", "DmgOut:998" },
        { "[ 2026.09.18 18:35:21 ] (combat) <color=0xffcc0000><b>74</b> <color=0x77ffffff><font size=10>from</font> <b><color=0xffffffff>Ephialtes Dissipator</b><font size=10><color=0x77ffffff> - Wrecks", "DmgIn:74" },
        { "[ 2026.09.18 18:35:15 ] (combat) <color=0xffe57f7f><b>16 GJ</b><color=0x77ffffff><font size=10> energy neutralized </font><b><color=0xffffffff>Ephialtes Dissipator</b><color=0x77ffffff><font size=10> - Ephialtes Dissipator</font>", "NeutIn:16" },
        { "[ 2026.09.18 18:30:12 ] (combat) <color=0xff7fffff><b>50 GJ</b><color=0x77ffffff><font size=10> energy neutralized </font><b><color=0xffffffff>Sharouran Hemah</b><color=0x77ffffff><font size=10> - Small Infectious Scoped Energy Neutralizer</font>", "NeutOut:50" },
        { "[ 2026.09.18 18:30:08 ] (combat) <color=0xffccff66><b>466</b><color=0x77ffffff><font size=10> remote armor repaired to </font><b><color=0xffffffff>Fedo [TEST] Osprey | HoS - Shield</b><color=0x77ffffff><font size=10> - Medium Remote Armor Repairer II</font>", "RepOut:466" },
        { "[ 2026.09.18 18:30:09 ] (combat) <color=0xffccff66><b>488</b><color=0x77ffffff><font size=10> remote shield boosted by </font><b><color=0xffffffff>HotSprockets [PTOMO] Osprey</b><color=0x77ffffff><font size=10> - Medium Murky Compact Remote Shield Booster</font>", "RepIn:488" },
        { "[ 2026.09.18 18:30:10 ] (combat) <color=0xffccff66><b>366</b><color=0x77ffffff><font size=10> remote capacitor transmitted by </font><b><color=0xffffffff>HotSprockets [PTOMO] Osprey</b><color=0x77ffffff><font size=10> - Corpum C-Type Medium Remote Capacitor Transmitter</font>", "CapIn:366" },
        { "[ 2026.09.18 18:30:11 ] (combat) <color=0xffccff66><b>366</b><color=0x77ffffff><font size=10> remote capacitor transmitted to </font><b><color=0xffffffff>HotSprockets [PTOMO] Osprey</b><color=0x77ffffff><font size=10> - Corpum C-Type Medium Remote Capacitor Transmitter</font>", "CapOut:366" },
        // A miss carries no damage: a series of nothing else is not stored.
        { "[ 2026.09.18 18:35:19 ] (combat) Ephialtes Dissipator misses you completely", "" },
        { $"[ 2026.09.18 18:29:02 ] (combat) {HitOut}", "" },
        { $"[ 2026.09.18 18:39:40 ] (combat) {HitOut}", "" }
    };

    [Theory]
    [MemberData(nameof(OneLineEach))]
    public void Build_OneLine_LandsInItsOwnSeriesOnly(string line, string expected)
    {
        RunCombatTimeline timeline = RunCombatTelemetry.Build(Guid.NewGuid(), [_Parse(line)], RunStart, RunStop);

        Assert.Equal(expected, _Totals(timeline));
    }

    [Fact]
    public void Build_OverARealAbyssalRun_MatchesTheGameLog()
    {
        RunCombatTimeline timeline = RunCombatTelemetry.Build(Guid.NewGuid(), RealRunEvents(), RunStart, RunStop);

        Assert.Equal("DmgOut:65732,DmgIn:1999,NeutIn:115", _Totals(timeline));
        Assert.Equal((998, "Ephialtes Dissipator", 74, "Ephialtes Dissipator", 109),
            (timeline.MaxHitOut, timeline.MaxHitOutTarget, timeline.MaxHitIn, timeline.MaxHitInSource, timeline.MissesIn));
        Assert.Equal(65_732, timeline.HitTallies.Where(tally => tally.Direction is DamageDirection.Outgoing).Sum(tally => tally.Sum));
        Assert.Equal(109, timeline.HitTallies.Where(tally => tally.Quality is HitQuality.Misses).Sum(tally => tally.Count));

        RunHitTally missiles = Assert.Single(timeline.HitTallies, tally => tally.Direction is DamageDirection.Outgoing
            && tally.Counterparty == "Ephialtes Dissipator" && tally.Weapon == "Nova Fury Light Missile");
        Assert.Equal((HitQuality.Hits, 27, 15_178L, 154, 998), (missiles.Quality, missiles.Count, missiles.Sum, missiles.Min, missiles.Max));

        // Its own guns and its missiles are two rows, even with the same word on the same pilot.
        RunHitTally guns = Assert.Single(timeline.HitTallies, tally => tally.Direction is DamageDirection.Incoming
            && tally.Counterparty == "Ephialtes Dissipator" && tally.Weapon is null && tally.Quality is HitQuality.Hits);
        Assert.Equal((5, 106L), (guns.Count, guns.Sum));
    }

    /// <summary>
    /// The live path end to end: lines tailed while the run is watched, SAVE, and the stored timeline read back — the
    /// series decoded to the same sums, the scalars and the tallies intact, a series of misses only left out.
    /// </summary>
    [AvaloniaFact]
    public async Task SavingAWatchedRun_KeepsItsCombat_AndReadsItBackUnchanged()
    {
        using ActivityWindowHarness harness = await ActivityWindowHarness.CreateAsync();
        ActivityWindowViewModel window = await harness.OpenAsync();
        await harness.StartWatchingAsync();
        await window.StartRunCommand.ExecuteAsync(null);
        DateTime startedAtUtc = Assert.IsType<DateTime>(window.AnchorUtc);
        string at = $"[ {DateTime.UtcNow.AddSeconds(1):yyyy.MM.dd HH:mm:ss} ] (combat) ";

        await harness.WriteLineAsync(at + "<color=0xffe57f7f><b>16 GJ</b><color=0x77ffffff><font size=10> energy neutralized </font><b><color=0xffffffff>Ephialtes Dissipator</b><color=0x77ffffff><font size=10> - Ephialtes Dissipator</font>");
        await harness.WriteLineAsync(at + "Ephialtes Dissipator misses you completely");
        // Last, and the only name the test SDE knows: once ENEMIES has it, every line before it has been taken too.
        await harness.WriteLineAsync(at + "<color=0xff00ffff><b>998</b> <color=0x77ffffff><font size=10>to</font> <b><color=0xffffffff>Centii Servant</b><font size=10><color=0x77ffffff> - Nova Fury Light Missile - Hits");
        await ActivityWindowHarness.WaitUntil(() => window.Enemies().EnemyObservations.Count == 1);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.StopRun(startedAtUtc.AddMinutes(1));
        await window.SaveRunCommand.ExecuteAsync(null);

        Result<RunCombatTimelineDto?> read = await harness.Services.GetRequiredService<IDispatcher>()
            .Query(new GetRunCombatTimelineQuery(window.RunId ?? Guid.Empty), TestContext.Current.CancellationToken);
        RunCombatTimelineDto timeline = Assert.IsType<RunCombatTimelineDto>(read.Value);
        Assert.Equal(61, timeline.Seconds);
        Assert.Equal("DmgOut:998,NeutIn:16",
            string.Join(",", timeline.Series.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value.Sum()}")));
        Assert.Equal((998, "Centii Servant", 1, 1),
            (timeline.MaxHitOut, timeline.MaxHitOutTarget, timeline.HitsOut, timeline.MissesIn));
        Assert.Equal(
            [
                new RunHitTallyDto(DamageDirection.Outgoing, "Centii Servant", "Nova Fury Light Missile", HitQuality.Hits, 1, 998, 998, 998),
                new RunHitTallyDto(DamageDirection.Incoming, "Ephialtes Dissipator", null, HitQuality.Misses, 1, 0, 0, 0)
            ],
            timeline.HitTallies.OrderBy(tally => tally.Direction));
    }

    private static GameLogEvent _Parse(string line) =>
        LogLineParser.Parse(line) ?? throw new InvalidOperationException($"not a gamelog event: {line}");

    private static string _Totals(RunCombatTimeline timeline) =>
        string.Join(",", timeline.Series.OrderBy(series => series.Kind).Select(series => $"{series.Kind}:{series.Total}"));

    private static string _Fixture(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EVE-Together.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("the solution root is not above the test binary"),
            "EveUtils.Client.UiTests", "Fixtures", "Gamelogs", name);
    }
}
