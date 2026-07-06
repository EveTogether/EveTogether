namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// Watches the stream of ESI call outcomes and, while ESI is believed up, raises <see cref="OutageSuspected"/> once a
/// run of consecutive server-side failures crosses the threshold — the cue to verify <c>/status/</c> straight away
/// instead of waiting for the next scheduled poll. A real ESI response (even a 4xx) resets the run, since it proves
/// ESI is reachable. The status poller owns the authoritative up/down decision; this only triggers an early check.
/// </summary>
public interface IEsiOutageDetector
{
    /// <summary>True once a short run of consecutive server-side failures suggests ESI is struggling — the retry handler
    /// reads this to stop retrying (no point piling attempts on a likely-dead API; the failure surfaces immediately).
    /// Lower bar than the <see cref="OutageSuspected"/> trip, so we back off well before the /status verification.</summary>
    bool IsSuspect { get; }

    /// <summary>ESI answered (2xx/304, or a 4xx/429 that still proves it's reachable) — clear the failure run.</summary>
    void RecordSuccess();

    /// <summary>A server-side failure (5xx, timeout, transport) — count it toward a suspected outage.</summary>
    void RecordServerFailure();

    /// <summary>Clear the failure run unconditionally — called when the status poller observes ESI recover, so a stale
    /// count never instantly re-trips the gate right after it reopens.</summary>
    void Reset();

    /// <summary>Raised when consecutive server-side failures cross the threshold while ESI is believed up.</summary>
    event Action OutageSuspected;
}
