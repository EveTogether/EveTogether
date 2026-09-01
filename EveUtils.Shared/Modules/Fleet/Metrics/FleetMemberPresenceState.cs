namespace EveUtils.Shared.Modules.Fleet.Metrics;

/// <summary>
/// What a screen may say about one fleet member's presence. <see cref="Offline"/> is a category of its own and not a
/// flavour of "location unknown" (ET-70): an FC steers differently on "that pilot is gone" than on "we have no
/// position fix for that pilot yet", and a badge that folds the two together can say neither.
/// </summary>
public enum FleetMemberPresenceState
{
    /// <summary>Nothing has ever been heard from this pilot's client, so nothing may be claimed. A member who shares
    /// no metric at all lives here permanently — silence that was never preceded by contact is not evidence.</summary>
    Unknown = 0,

    /// <summary>Their client is reporting, and their EVE client is up.</summary>
    Online = 1,

    /// <summary>Either their client reported that EVE is closed, or it was reporting and has stopped.</summary>
    Offline = 2,
}
