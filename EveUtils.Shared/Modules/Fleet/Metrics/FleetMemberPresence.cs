namespace EveUtils.Shared.Modules.Fleet.Metrics;

/// <summary>
/// The one definition of whether a fleet member is present (ET-70), and the two timings the two halves of that
/// question run on. A pilot who is offline is still a member — this decides what a screen may say about them, never
/// whether they stay on the roster.
///
/// The verdict has to answer two situations that fail differently. The easy one is a running EVE Together with the
/// game closed: that client knows, and says so as <see cref="MetricKind.Presence"/> on the stream it already
/// publishes. The hard one is a client that is gone altogether, which by definition never sends the message saying
/// so — that is read out of <see cref="SilentAfter"/> seconds of nothing at all.
/// </summary>
public static class FleetMemberPresence
{
    /// <summary>
    /// How long a pilot's client may say nothing before we call them gone. Samples arrive at 1 Hz, so this is 90
    /// missed ones — deliberately far past the grooming's 30-60 s, because the transport itself is allowed to be
    /// quieter than that while everything is fine. One full reconnect cycle in <c>ServerConnection</c> is the sum of
    /// three of its constants, not two: <c>ReceiveDeadline</c> gives a half-open stream 45 s before it is even
    /// noticed, <c>BackoffSeconds</c> then waits out its current step, and <c>ConnectTimeout</c> allows another 5 s
    /// to reconnect. At the 30 s top of that backoff table the worst cycle is 45 + 30 + 5 = 80 s, and this threshold
    /// is what leaves it room. A threshold under a minute would drop a pilot who is flying perfectly well off the
    /// screen on one network hiccup, which is precisely what this must not do.
    ///
    /// <para>That makes the two numbers a pair: raising the backoff cap past 40 s pushes the worst cycle past this
    /// threshold, and a pilot whose client is merely between reconnect attempts would read as offline. The cap was
    /// 60 s when this was first written, but the backoff could not actually grow past its second step (ET-95), so the
    /// arithmetic held by accident. Fixing the backoff meant capping it at 30 s; change one of the two and re-derive
    /// the other.</para>
    ///
    /// This is not the clock that asks whether a figure is current — that is
    /// <c>FleetOverlayViewModel.StaleAfter</c>, five seconds, and it is a different question with a different answer.
    /// Slow and certain for "is this pilot here"; fast and cheap for "is this number still arriving".
    /// </summary>
    public static readonly TimeSpan SilentAfter = TimeSpan.FromSeconds(90);

    /// <summary>
    /// How often a member's <c>LastSeenAt</c> is actually written to the database. Deliberately half
    /// <see cref="SilentAfter"/> rather than the minute <c>FleetActivityTracker</c> throttles a fleet's clock to: the
    /// stored row is what a freshly-opened screen reads before it has heard a single sample of its own, so a throttle
    /// at or near the threshold would let an actively-publishing member read as nearly silent. Half the window means
    /// a stored timestamp can never on its own produce a false "offline".
    /// </summary>
    public static readonly TimeSpan SeenWriteThrottle = TimeSpan.FromSeconds(45);

    /// <summary>
    /// The verdict, from every piece of evidence there is. Pure, so the screens, the badge and the tests all read the
    /// same one.
    /// </summary>
    /// <param name="inGameLocally">This client's own answer for one of its own characters, or null for anyone else —
    /// we cannot see another machine's EVE client and may infer nothing from not seeing it (ET-71).</param>
    /// <param name="reported">What the pilot's own client last said about their game.</param>
    /// <param name="isSilent">Their client was heard from and has since gone quiet past <see cref="SilentAfter"/>.
    /// False when nothing was ever heard: silence with no contact before it is not evidence of leaving.</param>
    public static FleetMemberPresenceState Read(bool? inGameLocally, PresenceState reported, bool isSilent)
    {
        // Our own pilot: the local sweep sees their EVE client directly, which beats anything that travelled a wire.
        if (inGameLocally is { } local)
            return local ? FleetMemberPresenceState.Online : FleetMemberPresenceState.Offline;

        // Heard before, nothing since — their EVE Together is closed, and it was never going to tell us that itself.
        if (isSilent)
            return FleetMemberPresenceState.Offline;

        return reported switch
        {
            PresenceState.InGame => FleetMemberPresenceState.Online,
            PresenceState.NotInGame => FleetMemberPresenceState.Offline,
            // Reporting, but claiming nothing about the game — an older client, or one that has not settled yet.
            // Their location and their place in the count go on being read exactly as before this ticket.
            _ => FleetMemberPresenceState.Unknown,
        };
    }

    /// <summary>Whether a pilot last heard from at <paramref name="lastHeardAt"/> has gone quiet. Null — never heard
    /// from at all — is not silence; see <see cref="Read"/>.</summary>
    public static bool IsSilent(DateTimeOffset? lastHeardAt, DateTimeOffset now) =>
        lastHeardAt is { } at && now - at > SilentAfter;
}
