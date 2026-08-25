namespace EveUtils.Client.Updates;

/// <summary>
/// What a finished update check has to say. Derived from the result's message code, never from its text.
/// </summary>
public enum UpdateNoticeKind
{
    /// <summary>
    /// A newer build is on offer.
    /// </summary>
    Available,

    /// <summary>
    /// The feed answered and this build is the latest. Only ever reached when the feed actually answered.
    /// </summary>
    UpToDate,

    /// <summary>
    /// This copy was not placed by the installer, so it replaces itself by hand. An ordinary state, not a fault.
    /// </summary>
    NotInstalled,

    /// <summary>
    /// Nothing could be asked — no network, a rate limit, a timeout. Says nothing about whether a newer build exists.
    /// </summary>
    Failed,
}
