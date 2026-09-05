using System;
using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.Modules.Gamelog.Aggregation;
using EveUtils.Shared.Modules.Runs.Dtos;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Room detection for one abyssal run, on the two runs it was measured against. Both fixtures are read off
/// Raymond's own gamelogs — <c>20260829_161157_883434905.txt</c> and <c>20260829_221231_883434905.txt</c> — with
/// the same combat lines <c>LogLineParser</c> reads, aggregated per enemy name exactly the way ET-106 stores them.
/// Nothing here is invented: a made-up sighting would test the fixture and not the rule.
/// </summary>
public class AbyssalRoomTests
{
    private const int AbyssalSystem = 32_000_042;
    private const int OutsideTheAbyss = 30_000_142;

    /// <summary>Run of 2026-08-29 17:35–17:55. Three rooms; the Biocombinative Cache was shot in all three.</summary>
    private static readonly RunEnemyObservationDto[] Ijkrun =
    [
        Seen("Striking Damavik", 17, 35, 58, 17, 40, 32),
        Seen("Triglavian Biocombinative Cache", 17, 39, 07, 17, 51, 34),
        Seen("Sparkneedle Tessella", 17, 40, 51, 17, 41, 41),
        Seen("Triglavian Extraction SubNode", 17, 41, 02, 17, 41, 02),
        Seen("Photic Abyssal Overmind", 17, 41, 09, 17, 50, 58),
        Seen("Lucid Escort", 17, 51, 24, 17, 55, 08)
    ];

    /// <summary>Run of 2026-08-29 22:17–22:27. Three rooms again, and room two holds nothing any table knows.</summary>
    private static readonly RunEnemyObservationDto[] SecondRun =
    [
        Seen("Devoted Smith", 22, 17, 46, 22, 18, 45),
        Seen("Devoted Hunter", 22, 17, 46, 22, 18, 44),
        Seen("Devoted Priest", 22, 17, 46, 22, 19, 36),
        Seen("Devoted Knight", 22, 17, 49, 22, 20, 24),
        Seen("Triglavian Biocombinative Cache", 22, 20, 12, 22, 25, 35),
        Seen("Ephialtes Illuminator", 22, 21, 22, 22, 23, 09),
        Seen("Karybdis Tyrannos", 22, 21, 22, 22, 24, 12),
        Seen("Ephialtes Entangler", 22, 21, 22, 22, 22, 38),
        Seen("Lucid Sentinel", 22, 25, 35, 22, 25, 40),
        Seen("Scylla Tyrannos", 22, 25, 47, 22, 26, 59),
        Seen("Lucid Upholder", 22, 26, 14, 22, 27, 27)
    ];

    /// <summary>
    /// AC-1. The boundary is the name change; the silence may only bound one, never find one.
    ///
    /// The counter-proof is the whole reason this rule exists: the Cache spans all three rooms, so a plain overlap
    /// sweep — the obvious reading of "a new set of names" — welds the run into one room whatever gap it is given.
    /// </summary>
    [Fact]
    public void Rooms_ComeFromTheNameChange_NotFromTheSilence()
    {
        Assert.Equal(1, NaiveOverlapGroups(Ijkrun));

        var reading = AbyssalRooms.Detect(AbyssalSystem, Ijkrun);

        Assert.False(reading.RoomsUnknown);
        Assert.Equal(
            [(At(17, 35, 58), At(17, 40, 32)), (At(17, 40, 51), At(17, 50, 58)), (At(17, 51, 24), At(17, 55, 08))],
            reading.Rooms.Select(room => (room.StartedAtUtc, room.EndedAtUtc)));

        // The bridging name is kept in every room it overlaps rather than assigned to one of them: which room it
        // belonged to is the thing that could not be established.
        Assert.Equal([2, 4, 2], reading.Rooms.Select(room => room.EnemyNames.Count));
        Assert.All(reading.Rooms, room => Assert.Contains("Triglavian Biocombinative Cache", room.EnemyNames));
    }

    /// <summary>AC-2. Three different factions in one measured run, so this cannot pass on a constant.</summary>
    [Fact]
    public void Factions_MatchTheRunTheyWereMeasuredOn()
    {
        var reading = AbyssalRooms.Detect(AbyssalSystem, Ijkrun);

        Assert.Equal(
            [AbyssalFaction.Triglavian, AbyssalFaction.RogueDrones, AbyssalFaction.Sleepers],
            reading.Rooms.Select(room => room.Faction));
    }

    /// <summary>
    /// AC-3. Ephialtes and the Tyrannos hulls are deliberately absent from the table — the source calls them
    /// Sleeper in one line and Drifter in the next — so the second run carries a room nothing can name. It has to
    /// stay a room: not dropped, not folded into its neighbour, not a run of two.
    /// </summary>
    [Fact]
    public void ARoomNoTableKnows_StaysAVisibleRoom()
    {
        var reading = AbyssalRooms.Detect(AbyssalSystem, SecondRun);

        Assert.Equal(
            [AbyssalFaction.Sansha, AbyssalFaction.Unknown, AbyssalFaction.Sleepers],
            reading.Rooms.Select(room => room.Faction));
        Assert.Equal([At(22, 21, 22), At(22, 24, 12)], [reading.Rooms[1].StartedAtUtc, reading.Rooms[1].EndedAtUtc]);
        Assert.Contains("Karybdis Tyrannos", reading.Rooms[1].EnemyNames);
    }

    /// <summary>
    /// AC-4. Outside the abyss there are no rooms to report, and that is a finding rather than an absence of one:
    /// the same observations inside the range still give three, so an implementation that reported nothing at all
    /// fails here too.
    /// </summary>
    [Fact]
    public void OutsideTheAbyss_NothingIsReported()
    {
        var outside = AbyssalRooms.Detect(OutsideTheAbyss, Ijkrun);

        Assert.Empty(outside.Rooms);
        Assert.False(outside.RoomsUnknown);
        Assert.Equal(3, AbyssalRooms.Detect(AbyssalSystem, Ijkrun).Rooms.Count);
    }

    /// <summary>The rule this one replaces: fold every window that touches the one before it. Kept here because a
    /// counter-proof that is only described is a counter-proof nobody can run.</summary>
    private static int NaiveOverlapGroups(IEnumerable<RunEnemyObservationDto> observations)
    {
        var groups = 0;
        var end = DateTime.MinValue;

        foreach (var observation in observations.OrderBy(observation => observation.FirstObservedAtUtc))
        {
            if (groups == 0 || observation.FirstObservedAtUtc > end)
                groups++;
            if (observation.LastObservedAtUtc > end)
                end = observation.LastObservedAtUtc;
        }

        return groups;
    }

    // The type id plays no part in the grouping; the name and the two stamps are all ET-106 offers it.
    private static RunEnemyObservationDto Seen(
        string name, int fromHour, int fromMinute, int fromSecond, int toHour, int toMinute, int toSecond) =>
        new(Guid.Empty, EnemyTypeId: 0, name, Count: 1,
            At(fromHour, fromMinute, fromSecond), At(toHour, toMinute, toSecond));

    private static DateTime At(int hour, int minute, int second) =>
        new(2026, 8, 29, hour, minute, second, DateTimeKind.Utc);
}
