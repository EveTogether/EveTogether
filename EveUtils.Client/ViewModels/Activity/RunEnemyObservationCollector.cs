using System.Collections.ObjectModel;
namespace EveUtils.Client.ViewModels.Activity;

public sealed class RunEnemyObservationCollector(int characterId, Func<string, int?> typeIdResolver)
{
    public ObservableCollection<RunEnemyObservationViewModel> Observations { get; } = [];

    public void Record(int observedCharacterId, string target)
    {
        if (observedCharacterId != characterId || typeIdResolver(target) is not int enemyTypeId)
            return;
        if (Observations.Any(observation => observation.EnemyTypeId == enemyTypeId))
            return;

        Observations.Add(new RunEnemyObservationViewModel(enemyTypeId, target));
    }
}
