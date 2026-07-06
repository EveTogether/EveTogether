namespace EveUtils.Shared.Modules.Esi.Http;

/// <summary>Whether ESI is usable for normal data calls — drives the downtime gate.</summary>
public enum EsiAvailability
{
    /// <summary>ESI answers normally; calls go through.</summary>
    Available,

    /// <summary>Tranquility/ESI is down (daily downtime or a failed /status/ poll); non-essential calls are withheld.</summary>
    Maintenance
}
