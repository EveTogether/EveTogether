using Avalonia.Headless.XUnit;
using EveUtils.Client.ViewModels.Activity;
using EveUtils.Client.ViewModels.Runs.Sections;
using EveUtils.Shared.Modules.Runs.Enums;
using EveUtils.Shared.Modules.Sde;
using Microsoft.Extensions.DependencyInjection;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Gamelog.Models;
using EveUtils.Shared.Modules.Gamelog.Reading;
using EveUtils.Shared.Modules.Runs;
using EveUtils.Shared.Modules.Runs.Dtos;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-368: live room detection in an abyssal pocket. Sightings are written "A@30" — enemy A seen 30 s after
/// <see cref="T0"/>; "new@t" presses NEW ROOM and "undo@t" takes the last room back. X stands for an enemy outside the
/// abyssal groups, the way the Precursor Cache is.
/// </summary>
public sealed class AbyssalRoomDetectionTests
{
    private static readonly DateTime T0 = new(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>AC1: a room opens on a new name after 20 s of silence, never at 19 s, never for a name already in the
    /// room, and never as a fourth; a 30 s silence turns sure only when a second new name follows within 15 s.
    /// Counter-proof: drop the silence or the cap and the 19 s and four-room rows go red.</summary>
    [Theory]
    [InlineData("A@0 B@19", "")]
    [InlineData("A@0 B@20", "room@20")]
    [InlineData("A@0 A@40", "")]
    [InlineData("A@0 B@25 C@30", "room@25")]
    [InlineData("A@0 B@30 C@40", "room@30 sure@30")]
    [InlineData("A@0 B@30 C@46", "room@30")]
    [InlineData("A@0 B@30 C@60 D@90", "room@30 room@60")]
    public void Observe_OpensARoomOnANewNameAfterTwentySecondsOfSilence(string sightings, string expected)
    {
        var detector = new AbyssalRoomDetector();

        string detections = string.Join(" ", _Steps(sightings)
            .Select(step => detector.Observe(step.Name[0], step.AtUtc))
            .OfType<RoomDetection>()
            .Select(detection => $"{(detection.IsUpgrade ? "sure" : "room")}@{(detection.AtUtc - T0).TotalSeconds}"));

        Assert.Equal(expected, detections);
    }

    /// <summary>AC4–AC6 on the collector: NEW ROOM takes the detector's boundary over as the pilot's and stops it; undo
    /// keeps the undone room's enemies from opening it again after a new silence; a run outside a pocket, or only the
    /// cache, opens nothing. The read rule beside it: a stray detected row never counts next to one the pilot set.
    /// Counter-proof: keep the detector running after NEW ROOM and the first row gains a RoomDetected@300; skip the
    /// detector's undo and the second row opens a room at 70.</summary>
    [Theory]
    [InlineData(true, "A@0 B@30 new@100 C@300", "RoomStarted()@30 RoomStarted()@100", "30 100")]
    [InlineData(true, "A@0 B@30 undo@35 B@40 A@70", "", "999")]
    [InlineData(true, "A@0 B@30", "RoomDetected(probable)@30", "30 999")]
    [InlineData(true, "X@0 X@60", "", "999")]
    [InlineData(false, "A@0 B@30 C@60", "", "999")]
    public void Collector_HandWinsUndoHoldsAndOnlyAPocketDetects(bool isAbyssalPocket, string steps,
        string expectedSaved, string expectedRead)
    {
        var collector = new RunEnemyObservationCollector(1, name => name[0], isAbyssalPocket ? typeId => typeId != 'X' : null);
        _Play(collector, steps);

        IReadOnlyList<RunParameterInput> saved = collector.ToRoomParameters();
        Guid runId = Guid.NewGuid();
        RunParameterDto[] stored =
        [
            .. saved.Select(row => new RunParameterDto(runId, row.ParameterKey, row.TypedValue, null, null, null, row.ObservedAtUtc)),
            new(runId, Shared.Modules.Runs.Enums.RunParameterKey.RoomDetected, string.Empty, null, null, null, T0.AddSeconds(999))
        ];
        Assert.Equal(expectedSaved, string.Join(" ", saved.Select(row => $"{row.ParameterKey}({row.TypedValue})@{(row.ObservedAtUtc - T0).TotalSeconds}")));
        Assert.Equal(expectedRead, string.Join(" ", RunRooms.Boundaries(stored, runId).Select(at => (at - T0).TotalSeconds)));
    }

    /// <summary>AC2, a regression lock on the rule and not ground truth: Raymond's ten abyssal runs of 2026-09-18,
    /// through ET's own gamelog reader, give eight runs of three rooms and two of two, on the boundaries the research
    /// measured (run 5: 18:33:47 and 18:37:04, ET-466). Counter-proof: lower the silence to 15 s and the boundaries move.</summary>
    [Fact]
    public void Replay_TenRunsOfEighteenSeptember_KeepTheirMeasuredBoundaries()
    {
        string directory = Path.Combine(Path.GetTempPath(), "et368-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.Copy(_Fixture("gamelog-abyssal-runs-2026-09-18.txt"), Path.Combine(directory, "20260918_172733_883434905.txt"));

        string[] boundaries = [.. Runs.Select(run => _Replay(directory, run.Start, run.Stop))];

        Assert.Equal(Expected, boundaries);
    }

    private static readonly (string Name, string At)[] Sightings =
        [("Striking Damavik", "12:00:05"), ("Starving Vedmak", "12:00:35"), ("Ephialtes Entangler", "12:00:40")];

    private static readonly Dictionary<string, Func<ActivityWindowHarness, ActivityWindowViewModel, Task>> Feeds = new()
    {
        ["live"] = (harness, _) => harness.WriteLineAsync(string.Join("\n", Sightings.Select(sighting =>
            ActivityWindowHarness.CombatLine(250, sighting.Name, sighting.At)))),
        ["catch-up"] = (_, model) =>
        {
            Array.ForEach(Sightings, sighting => model.Enemies().RecordCatchUpSighting(ActivityWindowHarness.CharacterId,
                sighting.Name, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc) + TimeSpan.Parse(sighting.At)));
            return Task.CompletedTask;
        }
    };

    /// <summary>AC3: the same sightings open the same room whether the gamelog brings them live or the catch-up read
    /// after a restart does — both end in the collector, and the abyssal window gives it the detector. The room says it
    /// was detected, and how sure. Counter-proof: leave the window's collector without the abyssal filter and neither
    /// path saves a RoomDetected.</summary>
    [AvaloniaTheory]
    [InlineData("live")]
    [InlineData("catch-up")]
    public async Task AbyssalWindow_LiveAndCatchUpOpenTheSameDetectedRoom(string path)
    {
        using var harness = await ActivityWindowHarness.CreateAsync(configure: services => services.AddSingleton<ISdeAccessor>(
            new FakeSdeAccessor().Add(48092, "Striking Damavik", 1982, 11).Add(48087, "Starving Vedmak", 1982, 11)
                .Add(48235, "Ephialtes Entangler", 1982, 11)));
        ActivityWindowViewModel model = await harness.OpenAsync(ActivityKind.Abyssal);
        await harness.StartWatchingAsync();
        await model.StartRunCommand.ExecuteAsync(null);

        await Feeds[path](harness, model);
        await ActivityWindowHarness.WaitUntil(() => model.Enemies().EnemyRooms.Count == 2);
        var draft = new RunSaveDraft(model.RunId ?? Guid.Empty, ActivityWindowHarness.CharacterId, isActingRun: true);
        model.Enemies().AddToSave(draft);

        Assert.Equal(["RoomDetected(sure)@12:00:35"], draft.Parameters.Select(row => $"{row.ParameterKey}({row.TypedValue})@{row.ObservedAtUtc:HH:mm:ss}"));
        Assert.EndsWith("· auto · sure", model.Enemies().EnemyRooms[0].WindowText);
    }

    /// <summary>One run's combat lines through ET's own catch-up reader, into a collector that detects the way an
    /// abyssal window's does.</summary>
    private static string _Replay(string directory, DateTime startUtc, DateTime stopUtc)
    {
        var collector = new RunEnemyObservationCollector(1, name => Array.IndexOf(Names, name) is var index and >= 0 ? index : null,
            typeId => typeId < FirstNonAbyssal);
        foreach (CombatEvent combat in GameLogCatchUpReader.Read(directory, "RaymondKrah", startUtc.AddSeconds(-1), stopUtc).OfType<CombatEvent>())
        {
            collector.Record(1, combat.Target, combat.Timestamp);
        }

        return string.Join(" ", collector.RoomBoundaries.Select(at => at.ToString("HH:mm:ss")));
    }

    private static void _Play(RunEnemyObservationCollector collector, string steps)
    {
        Dictionary<string, Action<DateTime>> presses = new() { ["new"] = at => collector.StartRoom(at), ["undo"] = _ => collector.UndoRoom() };
        foreach ((string name, DateTime atUtc) in _Steps(steps))
        {
            presses.GetValueOrDefault(name, at => collector.Record(1, name, at))(atUtc);
        }
    }

    private static string _Fixture(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EVE-Together.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new InvalidOperationException("the solution root is not above the test binary"),
            "EveUtils.Client.UiTests", "Fixtures", name);
    }

    // The ten runs as ET stored them for RaymondKrah (client.db), and every name their combat lines carry. The names
    // before FirstNonAbyssal sit in SDE groups 1982/1997; the cache is group 2009, the Tyrannos pair have no type.
    private static readonly (DateTime Start, DateTime Stop)[] Runs =
    [
        (At(17, 50, 20), At(17, 59, 44)), (At(18, 3, 39), At(18, 12, 8)), (At(18, 18, 10), At(18, 27, 45)),
        (At(18, 29, 3), At(18, 39, 39)), (At(18, 56, 41), At(19, 3, 39)), (At(19, 8, 17), At(19, 18, 5)),
        (At(19, 25, 45), At(19, 35, 44)), (At(19, 39, 45), At(19, 47, 49)), (At(19, 50, 26), At(20, 0, 26)),
        (At(20, 4, 46), At(20, 12, 38))
    ];

    private static readonly string[] Names =
    [
        "Anchoring Damavik", "Bathyic Abyssal Overmind", "Blastlance Tessella", "Devoted Fisher", "Devoted Hunter",
        "Devoted Knight", "Devoted Lookout", "Devoted Smith", "Devoted Torchbearer", "Embergrip Tessera",
        "Emberlance Tessella", "Ephialtes Confuser", "Ephialtes Dissipator", "Ephialtes Entangler", "Ephialtes Illuminator",
        "Ephialtes Lancer", "Ephialtes Obfuscator", "Ephialtes Spearfisher", "Fogcaster Tessella", "Gazedimmer Tessella",
        "Ghosting Damavik", "Ghosting Kikimora", "Harrowing Vedmak", "Lucid Aegis", "Lucid Deepwatcher", "Lucid Escort",
        "Lucid Firewatcher", "Lucid Preserver", "Lucid Upholder", "Lucid Warden", "Lucid Watchman", "Plateforger Tessella",
        "Shining Drekavac", "Snarecaster Tessella", "Sparkgrip Tessera", "Sparkneedle Tessella", "Spotlighter Tessella",
        "Starving Damavik", "Starving Vedmak", "Strikegrip Tessera", "Strikeneedle Tessella", "Striking Damavik",
        "Striking Kikimora", "Tangling Damavik",
        "Triglavian Biocombinative Cache"
    ];

    private static readonly int FirstNonAbyssal = Array.IndexOf(Names, "Triglavian Biocombinative Cache");

    private static readonly string[] Expected =
    [
        "17:53:45 17:56:42", "18:06:31 18:09:35", "18:21:29 18:23:33", "18:33:47 18:37:04", "19:01:31",
        "19:12:31 19:15:26", "19:30:35", "19:42:46 19:45:46", "19:54:03 19:56:48", "20:08:09 20:10:27"
    ];

    private static DateTime At(int hour, int minute, int second) => new(2026, 9, 18, hour, minute, second, DateTimeKind.Utc);

    private static IEnumerable<(string Name, DateTime AtUtc)> _Steps(string steps) =>
        steps.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(step => step.Split('@'))
            .Select(parts => (parts[0], T0.AddSeconds(int.Parse(parts[1]))));
}
