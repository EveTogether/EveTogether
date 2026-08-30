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
/// <param name="UnknownLocations">Tracked members whose solar system is not known, counted apart from the ratio.</param>
/// <param name="CommanderSystem">The commander's solar system, or null when it is unknown.</param>
public readonly record struct FleetCommanderPresence(int InSystem, int Known, int UnknownLocations, string? CommanderSystem)
{
    /// <summary>No commander on the roster, no location shared, or nothing tracked yet.</summary>
    public static readonly FleetCommanderPresence Unknown = new(0, 0, 0, null);

    /// <summary>Every tracked member, whether their location is known or not.</summary>
    public int Total => Known + UnknownLocations;

    /// <summary>
    /// Count <paramref name="memberSystems"/> — one entry per tracked member, the commander's own included — against
    /// the commander's system. Without a commander system there is no honest ratio to show, so the result is
    /// <see cref="Unknown"/> rather than a figure that reads as "nobody is with the FC".
    /// </summary>
    public static FleetCommanderPresence From(string? commanderSystem, IEnumerable<string?> memberSystems)
    {
        if (string.IsNullOrWhiteSpace(commanderSystem))
            return Unknown;

        List<string?> systems = memberSystems.ToList();
        if (systems.Count == 0)
            return Unknown;

        int known = systems.Count(IsKnown);
        return new FleetCommanderPresence(
            systems.Count(system => IsCommanderSystem(system, commanderSystem)),
            known,
            systems.Count - known,
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

    public string BadgeText => IsUnknown ? "◉ — WITH FC" : $"◉ {InSystem}/{Known} WITH FC{UnknownSuffix}";

    // Only when there is something to say. A trailing "(0 unknown)" would be noise on the common case.
    private string UnknownSuffix => UnknownLocations > 0 ? $" ({UnknownLocations} unknown)" : "";

    public string Tooltip => IsUnknown
        ? "Unknown: the fleet has no commander, or nobody's location is known — the commander's own included. "
          + "Location sharing is opt-in."
        : $"{InSystem} of {Known} fleet members with a known location are in {CommanderSystem} with the fleet "
          + $"commander.{UnknownTooltip}";

    private string UnknownTooltip => UnknownLocations switch
    {
        0 => "",
        1 => " 1 more shares no location and is left out of the count.",
        _ => $" {UnknownLocations} more share no location and are left out of the count.",
    };
}
