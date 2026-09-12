using System.Collections.Generic;
using System.Linq;
using EveUtils.Shared.DependencyInjection;

namespace EveUtils.Client.Fleet;

/// <summary>
/// Which of this client's own characters are on a shared fleet run right now, and under which group code (ET-242). Put
/// here by the run window flying it, taken away again when that run is saved or thrown away or its window closes — so
/// a character stops counting as "on a shared run" the moment nothing on screen says it is.
///
/// <see cref="MetricShareSnapshot"/> is what reads it: on a shared run, loot and bounty follow that run's own choice
/// first, and nothing about the global opt-in changes for a character that is not on one.
/// </summary>
public sealed class SharedFleetRuns : ISingletonService
{
    private readonly object _gate = new();
    private readonly Dictionary<(long FleetId, int CharacterId), string> _runs = [];

    /// <summary>A copy, so a publish tick reads one coherent set while a window changes it.</summary>
    public IReadOnlyDictionary<(long FleetId, int CharacterId), string> Current
    {
        get
        {
            lock (_gate)
                return new Dictionary<(long FleetId, int CharacterId), string>(_runs);
        }
    }

    public void Set(long fleetId, int characterId, string groupCode)
    {
        lock (_gate)
            _runs[(fleetId, characterId)] = groupCode;
    }

    /// <summary>Takes a character off <paramref name="groupCode"/> only: a second window that has since put the same
    /// character on another run keeps it there.</summary>
    public void Remove(long fleetId, int characterId, string groupCode)
    {
        lock (_gate)
            if (_runs.TryGetValue((fleetId, characterId), out string? held) && held == groupCode)
                _runs.Remove((fleetId, characterId));
    }

    /// <summary>Takes every character off <paramref name="groupCode"/>.</summary>
    public void RemoveRun(string groupCode)
    {
        lock (_gate)
            foreach ((long FleetId, int CharacterId) key in _runs.Where(run => run.Value == groupCode).Select(run => run.Key).ToList())
                _runs.Remove(key);
    }
}
