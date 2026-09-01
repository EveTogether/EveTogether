using System.Collections.ObjectModel;
using EveUtils.Shared.Modules.Runs.Dtos;

namespace EveUtils.Client.ViewModels.Activity;

public sealed class RunEnemyObservationCollector(int characterId, Func<string, int?> typeIdResolver)
{
    public ObservableCollection<RunEnemyObservationViewModel> Observations { get; } = [];

    /// <summary>Raised when a row appears or its count changes — a header summary is only true if it is recomputed
    /// then, and nothing else tells the window a count was typed.</summary>
    public event Action? Changed;

    /// <summary>
    /// Record one observed enemy.
    ///
    /// <paramref name="observedAtUtc"/> is the gamelog line's own time, and there is deliberately no overload that
    /// defaults it to "now": EVE flushes its log in chunks, so a single poll can carry several seconds of combat,
    /// and stamping the batch with the read time would file it all at one instant. A time nobody measured is worse
    /// than no time at all.
    ///
    /// An enemy is keyed on <em>type alone</em>. The question this list answers is which kind of enemy and how many;
    /// which way the damage went is not part of it, so a rat you shot and a rat that shot you are one row whose
    /// window spans both sightings (ET-115). Nothing is added up here either: the count stays the player's.
    /// </summary>
    public void Record(int observedCharacterId, string target, DateTime observedAtUtc)
    {
        if (observedCharacterId != characterId || typeIdResolver(target) is not int enemyTypeId)
            return;

        if (Observations.FirstOrDefault(observation => observation.EnemyTypeId == enemyTypeId) is { } seen)
        {
            seen.Observe(observedAtUtc);
            return;
        }

        var added = new RunEnemyObservationViewModel(enemyTypeId, target, observedAtUtc);
        added.PropertyChanged += (_, _) => Changed?.Invoke();
        Observations.Add(added);
        Changed?.Invoke();
    }

    /// <summary>The seam ET-106 left to ET-105: what the window watched, in the shape <c>SaveRunCommand</c> stores.</summary>
    public IReadOnlyList<RunEnemyObservationInput> ToInputs() =>
    [
        .. Observations.Select(observation => new RunEnemyObservationInput
        {
            Count = observation.Count,
            EnemyTypeId = observation.EnemyTypeId,
            EnemyName = observation.EnemyName,
            FirstObservedAtUtc = observation.FirstObservedAtUtc,
            LastObservedAtUtc = observation.LastObservedAtUtc
        })
    ];
}
