namespace EveUtils.Shared.Modules.Esi.Status;

/// <summary>
/// The Tranquility server's coarse state as shown in the client status bar (ESI <c>/status/</c>).
/// An enum, not a set of bools, so adding a future state (e.g. a maintenance banner) stays non-breaking.
/// </summary>
public enum EveServerState
{
    /// <summary>We have not reached ESI yet, or a network/timeout error means we cannot tell.</summary>
    Unknown,

    /// <summary>Tranquility is up and accepting players.</summary>
    Online,

    /// <summary>Tranquility is in VIP mode — up but restricted (typically just after downtime).</summary>
    Vip,

    /// <summary>Tranquility itself is down (ESI returned a 5xx, e.g. 503 during downtime).</summary>
    Offline
}
