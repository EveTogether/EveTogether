using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace EveUtils.Client.Fleet;

/// <summary>
/// The fleet lifecycle actions a screen is in the middle of. A local fleet's command raises its change on the bus before
/// the call returns, and the acting screen redraws itself once it has: the change is news to every other screen, not to
/// that one. The change is delivered before the awaited call resumes, so a screen that runs its action through here
/// can drop the echo with <see cref="Covers"/> instead of reloading twice.
/// </summary>
public sealed class OwnFleetActions
{
    /// <summary>For an action whose fleet does not exist yet, so its id cannot be named — a create.</summary>
    public const long AnyFleet = 0;

    private readonly Dictionary<long, int> _inFlight = [];

    public bool Covers(long fleetId) => _inFlight.ContainsKey(fleetId) || _inFlight.ContainsKey(AnyFleet);

    public async Task<T> RunAsync<T>(long fleetId, Func<Task<T>> action)
    {
        _inFlight[fleetId] = _inFlight.GetValueOrDefault(fleetId) + 1;
        try
        {
            return await action();
        }
        finally
        {
            if (--_inFlight[fleetId] == 0)
                _inFlight.Remove(fleetId);
        }
    }
}
