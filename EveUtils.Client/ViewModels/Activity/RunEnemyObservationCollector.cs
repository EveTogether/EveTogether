using System.Collections.ObjectModel;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Activity;

public sealed class RunEnemyObservationCollector(int characterId, Func<string, int?> typeIdResolver)
{
    public ObservableCollection<RunEnemyObservationViewModel> Observations { get; } = [];

    public void Record(int observedCharacterId, string target) => Record(observedCharacterId, target, DateTime.UtcNow);

    public void Record(int observedCharacterId, string target, DateTime observedAtUtc)
    {
        if (observedCharacterId != characterId || typeIdResolver(target) is not int enemyTypeId)
            return;

        if (Observations.FirstOrDefault(observation => observation.EnemyTypeId == enemyTypeId) is { } seen)
        {
            seen.Observe(observedAtUtc);
            return;
        }

        Observations.Add(new RunEnemyObservationViewModel(enemyTypeId, target, observedAtUtc));
    }

    /// <summary>
    /// The seam ET-106 left to ET-105: what the window watched, in the shape <c>SaveRunCommand</c> stores. The
    /// direction is <see cref="EnemyObservationDirection.To"/> because the gamelog line these are built from is this
    /// character shooting that target, not the other way round.
    /// </summary>
    public IReadOnlyList<RunEnemyObservationInput> ToInputs() =>
    [
        .. Observations.Select(observation => new RunEnemyObservationInput
        {
            Count = observation.Count,
            EnemyTypeId = observation.EnemyTypeId,
            EnemyName = observation.EnemyName,
            Direction = EnemyObservationDirection.To,
            FirstObservedAtUtc = observation.FirstObservedAtUtc,
            LastObservedAtUtc = observation.LastObservedAtUtc
        })
    ];
}
