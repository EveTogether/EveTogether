using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Modules.Runs.Dtos;
using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class RunEnemyObservationCollectorTests
{
    private static readonly DateTime Observed = new(2026, 9, 1, 12, 35, 10, DateTimeKind.Utc);

    private static RunEnemyObservationCollector _Collector() => new(90000001, target => target switch
    {
        "Centii Servant" => 17155,
        "Centii Scavenger" => 17156,
        _ => null
    });

    [Fact]
    public void Input_NewObservation_DefaultsToZero()
    {
        var input = new RunEnemyObservationInput
        {
            EnemyTypeId = 17155,
            EnemyName = "Centii Servant",
            FirstObservedAtUtc = DateTime.UtcNow,
            LastObservedAtUtc = DateTime.UtcNow
        };

        Assert.Equal(0, input.Count);
    }

    [Fact]
    public void Record_NpcSeenTwice_AddsOneEditableObservation()
    {
        RunEnemyObservationCollector collector = _Collector();

        collector.Record(90000001, "Centii Servant", Observed);
        collector.Record(90000001, "Centii Servant", Observed);

        RunEnemyObservationViewModel observation = Assert.Single(collector.Observations);
        Assert.Equal(17155, observation.EnemyTypeId);
        Assert.Equal("Centii Servant", observation.EnemyName);
        Assert.Equal(0, observation.Count);
    }

    /// <summary>
    /// The stored window is the one that was witnessed. EVE flushes its gamelog in chunks, so a single poll can
    /// carry several seconds of combat; if the collector stamped its own read time these would collapse onto one
    /// instant and the database would hold times nobody measured. A chunk can also arrive out of order, so the
    /// window widens both ways rather than only forwards.
    /// </summary>
    [Fact]
    public void Record_KeepsTheGamelogsOwnFirstAndLastTime_NotTheReadTime()
    {
        RunEnemyObservationCollector collector = _Collector();

        collector.Record(90000001, "Centii Servant", Observed);
        collector.Record(90000001, "Centii Servant", Observed.AddSeconds(12));
        collector.Record(90000001, "Centii Servant", Observed.AddSeconds(4));    // must not move "last" back
        collector.Record(90000001, "Centii Servant", Observed.AddSeconds(-8));   // must move "first" back

        RunEnemyObservationInput stored = Assert.Single(collector.ToInputs());
        Assert.Equal(Observed.AddSeconds(-8), stored.FirstObservedAtUtc);
        Assert.Equal(Observed.AddSeconds(12), stored.LastObservedAtUtc);
        Assert.Equal(17155, stored.EnemyTypeId);
    }

    /// <summary>
    /// ET-115's counter-proof, at the level the list is built: three sightings of one kind are one row, and the
    /// number on it is the player's. Counting the sightings would be a plausible-looking answer to a question
    /// nobody asked — a rat hit fifty times is not fifty rats — and adding them up silently is exactly what the
    /// ticket forbids. Nothing is dropped either: a second kind is still its own row.
    /// </summary>
    [Fact]
    public void Record_ThreeSightingsOfOneKind_AreOneRowWhoseCountIsThePlayers_NotThree()
    {
        RunEnemyObservationCollector collector = _Collector();

        collector.Record(90000001, "Centii Servant", Observed);
        collector.Record(90000001, "Centii Servant", Observed.AddSeconds(5));
        collector.Record(90000001, "Centii Servant", Observed.AddSeconds(9));
        collector.Record(90000001, "Centii Scavenger", Observed.AddSeconds(11));

        RunEnemyObservationViewModel servant =
            Assert.Single(collector.Observations, row => row.EnemyTypeId == 17155);
        // Both, and do not drop the second for reading like the first. NotEqual rules one wrong answer out;
        // Equal states what the right one is. Measured: a collector that counted its own sightings reached 2, not
        // 3 — NotEqual(3) stayed green through it and only Equal(0) went red.
        Assert.NotEqual(3, servant.Count);
        Assert.Equal(0, servant.Count);

        servant.Count = 7;
        Assert.Equal(7, Assert.Single(collector.ToInputs(), row => row.EnemyTypeId == 17155).Count);
        // The other kind was neither folded into it nor lost.
        Assert.Equal(2, collector.ToInputs().Count);
        Assert.Equal(0, Assert.Single(collector.ToInputs(), row => row.EnemyTypeId == 17156).Count);
    }

    /// <summary>A count of zero is "seen, not counted" and stays visibly apart from a counted row (ET-106); which
    /// of the two a row is decides whether SAVE stores it at all.</summary>
    [Fact]
    public void ARowAtZero_ReadsAsNotCounted_AndAFilledOneDoesNot()
    {
        RunEnemyObservationCollector collector = _Collector();
        collector.Record(90000001, "Centii Servant", Observed);
        RunEnemyObservationViewModel row = Assert.Single(collector.Observations);

        Assert.False(row.IsCounted);
        string atZero = row.CountStateText;

        row.Count = 4;

        Assert.True(row.IsCounted);
        Assert.NotEqual(atZero, row.CountStateText);
    }

    [Fact]
    public void Record_PlayerName_IsNotObserved()
    {
        var collector = new RunEnemyObservationCollector(90000001, _ => null);

        collector.Record(90000001, "Raymond", Observed);

        Assert.Empty(collector.Observations);
    }

    /// <summary>ET-240 AC-1: the same type seen either side of NEW ROOM is two rows, each with a window on its own side
    /// of the boundary — and a line EVE flushes late lands by its own time. Counter-proof: key on type alone and there
    /// is one row whose window runs over the boundary.</summary>
    [Fact]
    public void Record_SameTypeEitherSideOfNewRoom_KeepsOneRowPerRoomWithItsOwnWindow()
    {
        RunEnemyObservationCollector collector = _Collector();

        collector.Record(90000001, "Centii Servant", Observed);
        collector.StartRoom(Observed.AddSeconds(30));
        collector.Record(90000001, "Centii Servant", Observed.AddSeconds(40));
        collector.Record(90000001, "Centii Servant", Observed.AddSeconds(20));

        Assert.Equal(
            [((int?)1, Observed, Observed.AddSeconds(20)), (2, Observed.AddSeconds(40), Observed.AddSeconds(40))],
            collector.ToInputs().Select(input => (input.RoomNumber, input.FirstObservedAtUtc, input.LastObservedAtUtc)));
    }

    /// <summary>ET-240 AC-4: undo folds the last room back per type — counts added, window widened — and undoing the
    /// only boundary leaves a run without rooms. Counter-proof: a second undo with no boundary left changes nothing.</summary>
    [Fact]
    public void UndoRoom_FoldsTheLastRoomBackPerType_AndLeavesNoRoomsBehind()
    {
        RunEnemyObservationCollector collector = _Collector();
        collector.Record(90000001, "Centii Servant", Observed);
        collector.StartRoom(Observed.AddSeconds(30));
        collector.Record(90000001, "Centii Servant", Observed.AddSeconds(40));
        collector.Record(90000001, "Centii Scavenger", Observed.AddSeconds(50));
        collector.Observations[0].Count = 2;
        collector.Observations[1].Count = 3;

        bool undone = collector.UndoRoom();
        bool undoneAgain = collector.UndoRoom();

        Assert.True(undone);
        Assert.False(undoneAgain);
        Assert.Empty(collector.RoomBoundaries);
        Assert.Equal(
            [((int?)null, 17155, 5, Observed, Observed.AddSeconds(40)), (null, 17156, 0, Observed.AddSeconds(50), Observed.AddSeconds(50))],
            collector.ToInputs().Select(input =>
                (input.RoomNumber, input.EnemyTypeId, input.Count, input.FirstObservedAtUtc, input.LastObservedAtUtc)));
    }
}
