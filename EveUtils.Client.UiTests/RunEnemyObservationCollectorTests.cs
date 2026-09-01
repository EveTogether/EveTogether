using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;
using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class RunEnemyObservationCollectorTests
{
    private static readonly DateTime Observed = new(2026, 9, 1, 12, 35, 10, DateTimeKind.Utc);
    private const EnemyObservationDirection Shot = EnemyObservationDirection.To;
    private const EnemyObservationDirection ShotAt = EnemyObservationDirection.From;

    [Fact]
    public void Input_NewObservation_DefaultsToZero()
    {
        var input = new RunEnemyObservationInput
        {
            EnemyTypeId = 17155,
            EnemyName = "Centii Servant",
            Direction = EnemyObservationDirection.To,
            FirstObservedAtUtc = DateTime.UtcNow,
            LastObservedAtUtc = DateTime.UtcNow
        };

        Assert.Equal(0, input.Count);
    }

    [Fact]
    public void Record_NpcSeenTwice_AddsOneEditableObservation()
    {
        var collector = new RunEnemyObservationCollector(90000001,
            target => target == "Centii Servant" ? 17155 : null);

        collector.Record(90000001, "Centii Servant", Observed, Shot);
        collector.Record(90000001, "Centii Servant", Observed, Shot);

        RunEnemyObservationViewModel observation = Assert.Single(collector.Observations);
        Assert.Equal(17155, observation.EnemyTypeId);
        Assert.Equal("Centii Servant", observation.EnemyName);
        Assert.Equal(0, observation.Count);
    }

    /// <summary>
    /// The stored window is the one that was witnessed. EVE flushes its gamelog in chunks, so a single poll can
    /// carry several seconds of combat; if the collector stamped its own read time these would collapse onto one
    /// instant and the database would hold times nobody measured. The gap between first and last is also what a
    /// later room-boundary analysis (ET-55) has to read, and it only exists if it is recorded here.
    /// </summary>
    [Fact]
    public void Record_KeepsTheGamelogsOwnFirstAndLastTime_NotTheReadTime()
    {
        var collector = new RunEnemyObservationCollector(90000001,
            target => target == "Centii Servant" ? 17155 : null);

        collector.Record(90000001, "Centii Servant", Observed, Shot);
        collector.Record(90000001, "Centii Servant", Observed.AddSeconds(12), Shot);
        collector.Record(90000001, "Centii Servant", Observed.AddSeconds(4), Shot);   // out of order: must not move "last" back

        RunEnemyObservationInput stored = Assert.Single(collector.ToInputs());
        Assert.Equal(Observed, stored.FirstObservedAtUtc);
        Assert.Equal(Observed.AddSeconds(12), stored.LastObservedAtUtc);
        Assert.Equal(17155, stored.EnemyTypeId);
        Assert.Equal(EnemyObservationDirection.To, stored.Direction);
    }

    /// <summary>
    /// The direction is read from the log, never assumed. <c>CombatObserved</c> fires for both ways — the gamelog
    /// carries <c>250 to Centii Scavenger</c> and <c>1 from Centii Servant</c> alike — so a rat that only ever shot
    /// at you must not be stored as one you shot at.
    /// </summary>
    [Fact]
    public void Record_EnemyThatOnlyShotAtYou_IsNotStoredAsOneYouShot()
    {
        var collector = new RunEnemyObservationCollector(90000001,
            target => target == "Centii Servant" ? 17155 : null);

        collector.Record(90000001, "Centii Servant", Observed, ShotAt);

        RunEnemyObservationInput stored = Assert.Single(collector.ToInputs());
        Assert.Equal(EnemyObservationDirection.From, stored.Direction);
    }

    /// <summary>
    /// The chosen design for an enemy met both ways: <b>two rows, keyed on type and direction together</b>, each
    /// keeping its own first/last window. One row would have to drop or invent one of the two directions —
    /// <see cref="RunEnemyObservationInput.Direction"/> holds exactly one — and the later sighting would silently
    /// overwrite the earlier. Two rows also keep the per-direction timing a room-boundary analysis (ET-55) reads.
    /// </summary>
    [Fact]
    public void Record_EnemyMetBothWays_KeepsBothDirectionsWithTheirOwnWindows()
    {
        var collector = new RunEnemyObservationCollector(90000001,
            target => target == "Centii Servant" ? 17155 : null);

        collector.Record(90000001, "Centii Servant", Observed, Shot);
        collector.Record(90000001, "Centii Servant", Observed.AddSeconds(30), ShotAt);
        collector.Record(90000001, "Centii Servant", Observed.AddSeconds(40), ShotAt);

        List<RunEnemyObservationInput> stored = [.. collector.ToInputs()];
        Assert.Equal(2, stored.Count);

        RunEnemyObservationInput outgoing = Assert.Single(stored, row => row.Direction == EnemyObservationDirection.To);
        RunEnemyObservationInput incoming = Assert.Single(stored, row => row.Direction == EnemyObservationDirection.From);

        // Neither overwrote the other, and each carries the window it was actually seen in.
        Assert.Equal(Observed, outgoing.FirstObservedAtUtc);
        Assert.Equal(Observed, outgoing.LastObservedAtUtc);
        Assert.Equal(Observed.AddSeconds(30), incoming.FirstObservedAtUtc);
        Assert.Equal(Observed.AddSeconds(40), incoming.LastObservedAtUtc);
        Assert.All(stored, row => Assert.Equal(17155, row.EnemyTypeId));
    }

    [Fact]
    public void Record_PlayerName_IsNotObserved()
    {
        var collector = new RunEnemyObservationCollector(90000001, _ => null);

        collector.Record(90000001, "Raymond", Observed, Shot);

        Assert.Empty(collector.Observations);
    }
}
