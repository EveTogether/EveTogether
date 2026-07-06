namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>
/// Shared, host-agnostic view of ESI availability. The client's status poller (which already polls
/// <c>/status/</c> every 30 s) updates it; the <see cref="EsiGatingHandler"/> reads it to withhold
/// non-essential calls while ESI is down. Defaults to <see cref="EsiAvailability.Available"/>.
/// </summary>
public interface IEsiAvailabilityState
{
    EsiAvailability Current { get; }

    /// <summary>True when normal data calls should be attempted.</summary>
    bool IsUsable { get; }

    void Set(EsiAvailability availability);
}
