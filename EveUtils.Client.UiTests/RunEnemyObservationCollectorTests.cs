using EveUtils.Client.ViewModels.Activity;
using EveUtils.Shared.Modules.Runs.Dtos;
using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class RunEnemyObservationCollectorTests
{
    private static readonly DateTime Observed = new(2026, 9, 1, 12, 35, 10, DateTimeKind.Utc);

    [Fact]
    public void Input_NewObservation_DefaultsToZero()
    {
        var input = new RunEnemyObservationInput
        {
            EnemyTypeId = 17155,
            EnemyName = "Centii Servant",
            Direction = Shared.Modules.Runs.Enums.EnemyObservationDirection.To,
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
    /// instant and the database would hold times nobody measured. The gap between first and last is also what a
    /// later room-boundary analysis (ET-55) has to read, and it only exists if it is recorded here.
    /// </summary>
    [Fact]
    public void Record_KeepsTheGamelogsOwnFirstAndLastTime_NotTheReadTime()
    {
        var collector = new RunEnemyObservationCollector(90000001,
            target => target == "Centii Servant" ? 17155 : null);

        collector.Record(90000001, "Centii Servant", Observed);
        collector.Record(90000001, "Centii Servant", Observed.AddSeconds(12));
        collector.Record(90000001, "Centii Servant", Observed.AddSeconds(4));   // out of order: must not move "last" back

        RunEnemyObservationInput stored = Assert.Single(collector.ToInputs());
        Assert.Equal(Observed, stored.FirstObservedAtUtc);
        Assert.Equal(Observed.AddSeconds(12), stored.LastObservedAtUtc);
        Assert.Equal(17155, stored.EnemyTypeId);
        Assert.Equal(Shared.Modules.Runs.Enums.EnemyObservationDirection.To, stored.Direction);
    }

    [Fact]
    public void Record_PlayerName_IsNotObserved()
    {
        var collector = new RunEnemyObservationCollector(90000001, _ => null);

        collector.Record(90000001, "Raymond", Observed);

        Assert.Empty(collector.Observations);
    }
}
