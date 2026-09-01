using EveUtils.Client.ViewModels.Activity;
using Xunit;

namespace EveUtils.Client.UiTests;

public sealed class RunEnemyObservationCollectorTests
{
    [Fact]
    public void Record_NpcSeenTwice_AddsOneEditableObservation()
    {
        var collector = new RunEnemyObservationCollector(90000001,
            target => target == "Centii Servant" ? 17155 : null);

        collector.Record(90000001, "Centii Servant");
        collector.Record(90000001, "Centii Servant");

        RunEnemyObservationViewModel observation = Assert.Single(collector.Observations);
        Assert.Equal(17155, observation.EnemyTypeId);
        Assert.Equal("Centii Servant", observation.EnemyName);
        Assert.Equal(0, observation.Count);
    }

    [Fact]
    public void Record_PlayerName_IsNotObserved()
    {
        var collector = new RunEnemyObservationCollector(90000001, _ => null);

        collector.Record(90000001, "Raymond");

        Assert.Empty(collector.Observations);
    }
}
