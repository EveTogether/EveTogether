namespace EveUtils.Shared.Modules.Gamelog.Aggregation;

/// <summary>
/// Abyssal deadspace, as far as the gamelog can see it.
///
/// Nothing is written to the log when a filament pulls you in — no jump, no undock, no notice (measured on ET-55's
/// run of 2026-08-29), so the system name never changes and there is no location to test. A run is recognised
/// instead by what shoots back.
///
/// The readout this feeds is a countdown, not a label, because the only number that matters in the abyss is how
/// long you have left: at <see cref="RunLimit"/> the ship and the pod are destroyed outright.
/// </summary>
public static class AbyssalSpace
{
    /// <summary>Ship and pod are destroyed at 20:00 — for the whole run, not per room.</summary>
    public static readonly TimeSpan RunLimit = TimeSpan.FromMinutes(20);

    // Deliberately short: enough to see the clock work, not a complete vocabulary. The full two-layer list (and why
    // hulls beat adjective prefixes) is in the ET-56 comments.
    private static readonly string[] AbyssalNames =
    [
        "Tessella", "Tyrannos", "Deepwatcher", "Overmind", "Spearfisher", "Watchman", "Firewatcher",
        "Obfuscator", "Illuminator", "Confuser", "Dissipator", "Upholder", "Preserver", "Sentinel",
        "Aegis", "Entangler", "Swarmer", "Escort", "Lancer", "Warden",
        "Biocombinative Cache", "Bioadaptive Cache",
    ];

    /// <summary>Whether a combat target's name says the character is inside the abyss.</summary>
    public static bool IsAbyssalContact(string? target) =>
        !string.IsNullOrWhiteSpace(target) &&
        AbyssalNames.Any(name => target.Contains(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The text a location readout shows. Without a run this is the system name, untouched. With one it is the
    /// deadline counting down from <see cref="RunLimit"/>.
    ///
    /// The countdown never claims more time than there is. <paramref name="anchorUtc"/> is the last moment the log
    /// placed the pilot somewhere — the undock or jump before the first abyssal shot — which is at or before the
    /// real entry, so the number shown is at or below the real remaining time. Past zero we are wrong about
    /// something (the pilot left and we cannot see it, or the anchor was not theirs), and the readout says so
    /// rather than counting on into negative time.
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

        return $"Abyssal ({remaining:mm\\:ss})";
    }
}
