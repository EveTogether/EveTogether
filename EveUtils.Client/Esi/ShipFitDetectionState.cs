namespace EveUtils.Client.Esi;

/// <summary>The mutually exclusive state of a character's current-ship cache entry.</summary>
public enum ShipFitDetectionState
{
    Unobserved,
    Observed,
    ScopeMissing,
}
