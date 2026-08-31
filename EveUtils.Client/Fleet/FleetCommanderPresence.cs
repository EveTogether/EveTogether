using System;
using System.Collections.Generic;
using System.Linq;

namespace EveUtils.Client.Fleet;

/// <summary>
/// How much of the fleet stands in the fleet commander's solar system — the figure an FC steers on. The commander
/// counts in both halves of the ratio: they are a fleet member, and they are trivially in their own system.
///
/// The ratio only counts members whose location is actually known (ET-63). A member who shares no location, or
/// whose system nothing has reported yet, is not evidence of anything: counting them in the denominator meant the
/// badge could never read complete while one silent member sat in the fleet, however plainly everyone else stood
/// with the FC. They are named separately instead, so the figure stays honest without being useless.
/// </summary>
/// <param name="InSystem">Members known to stand in <paramref name="CommanderSystem"/>.</param>
/// <param name="Known">Members whose solar system is known — the denominator of the ratio.</param>
/// <param name="UnknownLocations">Tracked members who are here but whose solar system is not known, counted apart
/// from the ratio.</param>
/// <param name="Offline">Tracked members known not to be here at all, counted apart from both (ET-70).</param>
/// <param name="CommanderSystem">The commander's solar system, or null when it is unknown.</param>
public readonly record struct FleetCommanderPresence(
    int InSystem, int Known, int UnknownLocations, int Offline, string? CommanderSystem)
{
    /// <summary>No commander on the roster, no location shared, or nothing tracked yet.</summary>
    public static readonly FleetCommanderPresence Unknown = new(0, 0, 0, 0, null);

    /// <summary>Every tracked member, in whichever of the three buckets they fell.</summary>
    public int Total => Known + UnknownLocations + Offline;

    /// <summary>
    /// Count <paramref name="members"/> — one entry per tracked member, the commander's own included — against the
    /// commander's system. Without a commander system there is no honest ratio to show, so the result is
    /// <see cref="Unknown"/> rather than a figure that reads as "nobody is with the FC".
    ///
    /// Offline members are counted apart from the members with no location fix, rather than folded in with them, and
    /// that separation is the whole of ET-70's badge half: "three pilots have gone" and "three pilots share no
    /// position" call for different things from an FC, and a single "unknown" suffix could say neither.
    /// </summary>
    public static FleetCommanderPresence From(string? commanderSystem, IEnumerable<FleetMemberStanding> members)
    {
        if (string.IsNullOrWhiteSpace(commanderSystem))
            return Unknown;

        List<FleetMemberStanding> standings = members.ToList();
        if (standings.Count == 0)
            return Unknown;

        int known = standings.Count(m => IsKnown(m.System));
        int offline = standings.Count(m => m.IsOffline);
        return new FleetCommanderPresence(
            standings.Count(m => IsCommanderSystem(m.System, commanderSystem)),
            known,
            standings.Count - known - offline,
            offline,
            commanderSystem);
    }

    /// <summary>
    /// Whether one member stands with the commander — the same test the ratio is counted with, so a member's own
    /// readout and the header badge can never tell different stories. False whenever there is no commander system
    /// to compare against: no commander, or one who shares no location, is not a reason to mark anybody present.
    /// A member with no location of their own is false for the same reason, which is exactly why they are left out
    /// of the ratio rather than counted against it.
    /// </summary>
    public bool IsWith(string? memberSystem) => IsCommanderSystem(memberSystem, CommanderSystem);

    /// <summary>A member we have a system for. The one definition of "known", so the denominator and the rows agree.</summary>
    private static bool IsKnown(string? memberSystem) => !string.IsNullOrWhiteSpace(memberSystem);

    private static bool IsCommanderSystem(string? memberSystem, string? commanderSystem) =>
        IsKnown(memberSystem) && commanderSystem is not null &&
        string.Equals(memberSystem, commanderSystem, StringComparison.OrdinalIgnoreCase);

    /// <summary>The one place the badge's reading is decided; the view maps it onto a style, nothing more.</summary>
    public FleetCommanderPresenceLevel Level
    {
        get
        {
            // Nobody's location known is not "everybody is here" — it is the same neutral state as a commander who
            // shares nothing. Without this, an all-unknown fleet reads 0/0 and the badge goes green over nothing.
            if (CommanderSystem is null || Known == 0)
                return FleetCommanderPresenceLevel.Unknown;

            return InSystem == Known ? FleetCommanderPresenceLevel.Complete : FleetCommanderPresenceLevel.Partial;
        }
    }

    public bool IsComplete => Level is FleetCommanderPresenceLevel.Complete;

    public bool IsUnknown => Level is FleetCommanderPresenceLevel.Unknown;

    // In the unknown state the dash already says "no location is known", so repeating it as "(n unknown)" would only
    // restate it — but "(n offline)" is news the dash does not carry, and often the reason for it.
    public string BadgeText => IsUnknown ? $"◉ — WITH FC{OfflineSuffix}" : $"◉ {InSystem}/{Known} WITH FC{Suffix}";

    private string OfflineSuffix => Offline > 0 ? $" ({Offline} offline)" : "";

    // Only what there is something to say about, and offline before unknown: "who is even here" is read first.
    // A trailing "(0 unknown)" would be noise on the common case.
    private string Suffix => (Offline, UnknownLocations) switch
    {
        (0, 0) => "",
        (0, var unknown) => $" ({unknown} unknown)",
        (var offline, 0) => $" ({offline} offline)",
        var (offline, unknown) => $" ({offline} offline, {unknown} unknown)",
    };

    public string Tooltip => IsUnknown
        ? "Unknown: the fleet has no commander, or nobody's location is known — the commander's own included. "
          + $"Location sharing is opt-in.{OfflineTooltip}{UnknownTooltip}"
        : $"{InSystem} of {Known} fleet members with a known location are in {CommanderSystem} with the fleet "
          + $"commander.{OfflineTooltip}{UnknownTooltip}";

    private string UnknownTooltip => UnknownLocations switch
    {
        0 => "",
        1 => " 1 more shares no location and is left out of the count.",
        _ => $" {UnknownLocations} more share no location and are left out of the count.",
    };

    // Named apart from the sentence above, because "not sharing a position" and "not here" are different news.
    private string OfflineTooltip => Offline switch
    {
        0 => "",
        1 => " 1 more is offline and is left out of the count.",
        _ => $" {Offline} more are offline and are left out of the count.",
    };
}

/// <summary>
/// One member as the badge counts them: where they stand, and whether they are here to stand anywhere.
/// <paramref name="System"/> is the location anything may act on — <c>DpsViewModel.KnownLocation</c>, already null for
/// an offline pilot — so the two can never contradict each other.
/// </summary>
public readonly record struct FleetMemberStanding(string? System, bool IsOffline);
