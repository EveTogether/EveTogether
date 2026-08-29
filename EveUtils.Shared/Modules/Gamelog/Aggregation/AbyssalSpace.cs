namespace EveUtils.Shared.Modules.Gamelog.Aggregation;

/// <summary>
/// Abyssal deadspace, read from the two sources that can see it.
///
/// The gamelog sees the way IN and nothing else: no jump, no undock, no notice is written when a filament pulls you
/// in (measured on ET-55's run of 2026-08-29), so the system name never changes and a run is recognised only by what
/// shoots back. It cannot see the way OUT at all — you leave where you fired the filament, so there is no line there
/// either, and silence is not a substitute: a four-minute gap can fall inside a run as easily as between two
/// (measured 2026-08-29, one such gap contained an entry), so no threshold separates them.
///
/// ESI sees both, and is what ends a run: <c>/characters/{id}/location/</c> answers with a solar system id, and
/// <see cref="IsAbyssalSystem"/> is the whole test. The log stays the trigger so nothing is polled until a run looks
/// like it started.
///
/// The readout this feeds is a countdown, not a label, because the only number that matters in the abyss is how
/// long you have left: at <see cref="RunLimit"/> the ship and the pod are destroyed outright.
/// </summary>
public static class AbyssalSpace
{
    /// <summary>Ship and pod are destroyed at 20:00 — for the whole run, not per room.</summary>
    public static readonly TimeSpan RunLimit = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Whether a solar system id is abyssal deadspace. Closed range on purpose: the five ADR regions hold exactly
    /// these 200 systems, but an open <c>&gt;= 32000001</c> would also swallow the Proving Grounds (VR-01..05,
    /// 34000001–34000200) and GPMR-01 (36000001), which are a different game entirely.
    /// [gemeten, live ESI 2026-08-29: ADR01–ADR05 enumerated through their 25 constellations — exactly 32000001
    /// through 32000200, no gaps, nothing outside; 32000000 and 32000201 both 404.]
    /// </summary>
    public static bool IsAbyssalSystem(int solarSystemId) => solarSystemId is >= 32000001 and <= 32000200;

    // Deliberately short: enough to see the clock work, not a complete vocabulary. The full two-layer list (and why
    // hulls beat adjective prefixes) is in the ET-56 comments. Names that double as normal-space ships — Sentinel,
    // Warden, Escort, Lancer, Aegis, Upholder, Preserver — are left out on purpose: a false clock in Aphend would
    // discredit the very readout this exists to check.
    private static readonly string[] AbyssalNames =
    [
        "Tessella", "Tyrannos", "Deepwatcher", "Overmind", "Spearfisher", "Watchman", "Firewatcher",
        "Obfuscator", "Illuminator", "Confuser", "Dissipator", "Marshal Disparu", "Enforcer Disparu",
        "Biocombinative Cache", "Bioadaptive Cache", "Extraction SubNode",
    ];

    // Triglavian hulls that fly in normal space too: a bare "Damavik" proves nothing, "Striking Damavik" does.
    private static readonly string[] PrefixedHulls = ["Damavik", "Vedmak", "Leshak"];

    /// <summary>Whether a combat target's name says the character is inside the abyss.</summary>
    public static bool IsAbyssalContact(string? target) =>
        !string.IsNullOrWhiteSpace(target) &&
        (AbyssalNames.Any(name => target.Contains(name, StringComparison.OrdinalIgnoreCase)) ||
         PrefixedHulls.Any(hull => target.IndexOf(hull, StringComparison.OrdinalIgnoreCase) > 0));

    /// <summary>
    /// Re-base a fleet mate's anchor onto our own clock. Their anchor and their sample timestamp come from the
    /// same machine, so the elapsed time between them is theirs to measure; only that span crosses the wire.
    /// Subtracting it from our own receipt time keeps both halves of the countdown on one clock, so a machine whose
    /// clock differs cannot buy the pilot seconds. Network delay lands on the safe side: it ages the anchor, which
    /// shows less time, never more.
    /// </summary>
    public static DateTime? AnchorFromWire(long anchorMs, long sentMs, DateTime receivedUtc) =>
        anchorMs > 0 ? receivedUtc - TimeSpan.FromMilliseconds(sentMs - anchorMs) : null;

    /// <summary>
    /// The text a location readout shows. Without a run this is the system name, untouched. With one it is the
    /// deadline counting down from <see cref="RunLimit"/>.
    ///
    /// The countdown never claims more time than there is. <paramref name="anchorUtc"/> is the last moment we could
    /// prove the pilot was outside — the undock or jump before the first abyssal shot, or the poll that saw them
    /// leave — which is at or before the real entry, so the number shown is at or below the real remaining time.
    /// Past zero we are wrong about something (the pilot left and we cannot see it, or the anchor was not theirs),
    /// and the readout says so rather than counting on into negative time.
    /// </summary>
    public static string? Describe(string? system, DateTime? anchorUtc, DateTime nowUtc)
    {
        if (anchorUtc is not { } anchor)
            return system;

        var remaining = RunLimit - (nowUtc - anchor);
        if (remaining <= TimeSpan.Zero)
            return "Abyssal (--:--)";

        // An anchor stamped slightly ahead of us (log time vs. wall clock) must not buy the pilot extra seconds.
        if (remaining > RunLimit)
            remaining = RunLimit;

        // The "+" says this is a floor, not a reading: entry falls in a window the log never writes in, measured at
        // 72 s, 84 s and 3.5 minutes on three runs. Drop the sign and the readout starts claiming to be exact.
        return $"Abyssal ({remaining:mm\\:ss}+)";
    }
}
