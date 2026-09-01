using System.Collections.ObjectModel;
using EveUtils.Shared.Modules.Runs.Dtos;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Client.ViewModels.Activity;

public sealed class RunEnemyObservationCollector(int characterId, Func<string, int?> typeIdResolver)
{
    public ObservableCollection<RunEnemyObservationViewModel> Observations { get; } = [];

    /// <summary>
    /// Record one observed enemy.
    ///
    /// <paramref name="observedAtUtc"/> is the gamelog line's own time, and there is deliberately no overload that
    /// defaults it to "now": EVE flushes its log in chunks, so a single poll can carry several seconds of combat,
    /// and stamping the batch with the read time would file it all at one instant. A time nobody measured is worse
    /// than no time at all.
    ///
    /// <paramref name="direction"/> must likewise come from the log line rather than be assumed. An enemy is keyed
    /// on <em>type and direction together</em>, so a rat you shot and a rat that shot you are two rows, each with
    /// its own first/last window. Folding them into one row would have to drop or invent one of the two directions,
    /// and the later one would silently overwrite the earlier.
    /// </summary>
    public void Record(int observedCharacterId, string target, DateTime observedAtUtc,
        EnemyObservationDirection direction)
    {
        if (observedCharacterId != characterId || typeIdResolver(target) is not int enemyTypeId)
            return;

        if (Observations.FirstOrDefault(observation =>
                observation.EnemyTypeId == enemyTypeId && observation.Direction == direction) is { } seen)
        {
            seen.Observe(observedAtUtc);
            return;
        }

        Observations.Add(new RunEnemyObservationViewModel(enemyTypeId, target, observedAtUtc, direction));
    }

    /// <summary>The seam ET-106 left to ET-105: what the window watched, in the shape <c>SaveRunCommand</c> stores.</summary>
    public IReadOnlyList<RunEnemyObservationInput> ToInputs() =>
    [
        .. Observations.Select(observation => new RunEnemyObservationInput
        {
            Count = observation.Count,
            EnemyTypeId = observation.EnemyTypeId,
            EnemyName = observation.EnemyName,
            Direction = observation.Direction,
            FirstObservedAtUtc = observation.FirstObservedAtUtc,
            LastObservedAtUtc = observation.LastObservedAtUtc
        })
    ];
}
