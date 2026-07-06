using EveUtils.Shared.DependencyInjection;

namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// Trips after <see cref="ConsecutiveFailureThreshold"/> back-to-back server-side failures while ESI is believed up.
/// Only armed when <see cref="IEsiAvailabilityState.IsUsable"/> — once the gate is closed the status poller owns
/// recovery, so withheld calls (which never reach ESI) must not feed the count. On a trip the run is cleared and
/// <see cref="OutageSuspected"/> fires once, so a single burst can't fire it repeatedly.
/// </summary>
public sealed class EsiOutageDetector(IEsiAvailabilityState availability) : IEsiOutageDetector, ISingletonService
{
    private const int ConsecutiveFailureThreshold = 10; // trip the /status verification poke
    private const int SuspectAfterFailures = 3;          // stop retrying — back off well before the trip

    private readonly object _gate = new();
    private int _consecutiveFailures;

    public event Action? OutageSuspected;

    public bool IsSuspect
    {
        get { lock (_gate) return _consecutiveFailures >= SuspectAfterFailures; }
    }

    public void RecordSuccess() => Reset();

    public void RecordServerFailure()
    {
        if (!availability.IsUsable)
            return; // ESI already known down — the status poller drives recovery, don't count withheld/gated calls

        bool tripped;
        lock (_gate)
        {
            tripped = ++_consecutiveFailures >= ConsecutiveFailureThreshold;
            if (tripped)
                _consecutiveFailures = 0; // re-arm so the next run has to build up again
        }

        if (tripped)
            OutageSuspected?.Invoke(); // outside the lock — the handler kicks off a /status poll
    }

    public void Reset()
    {
        lock (_gate)
            _consecutiveFailures = 0;
    }
}
