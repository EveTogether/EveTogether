using System;
using System.Collections.Generic;
using System.Linq;

namespace EveUtils.Client.Fleet;

/// <summary>
/// How much of the fleet stands in the fleet commander's solar system — the figure an FC steers on. The commander
/// counts in both halves of the ratio: they are a fleet member, and they are trivially in their own system.
/// A member who shares no location counts in the denominator only; location sharing is opt-in, so a silent member
/// is "not known to be there", never "there".
/// </summary>
/// <param name="InSystem">Members known to stand in <paramref name="CommanderSystem"/>.</param>
/// <param name="Total">Members the metrics screen tracks, commander included.</param>
/// <param name="CommanderSystem">The commander's solar system, or null when it is unknown.</param>
public readonly record struct FleetCommanderPresence(int InSystem, int Total, string? CommanderSystem)
{
    /// <summary>No commander on the roster, no location shared, or nothing tracked yet.</summary>
    public static readonly FleetCommanderPresence Unknown = new(0, 0, null);

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

        int inSystem = systems.Count(system => IsCommanderSystem(system, commanderSystem));
        return new FleetCommanderPresence(inSystem, systems.Count, commanderSystem);
    }

    /// <summary>
    /// Whether one member stands with the commander — the same test the ratio is counted with, so a member's own
    /// readout and the header badge can never tell different stories. False whenever there is no commander system
    /// to compare against: no commander, or one who shares no location, is not a reason to mark anybody present.
    /// </summary>
    public bool IsWith(string? memberSystem) => IsCommanderSystem(memberSystem, CommanderSystem);

    private static bool IsCommanderSystem(string? memberSystem, string? commanderSystem) =>
        commanderSystem is not null && string.Equals(memberSystem, commanderSystem, StringComparison.OrdinalIgnoreCase);

    /// <summary>The one place the badge's reading is decided; the view maps it onto a style, nothing more.</summary>
    public FleetCommanderPresenceLevel Level
    {
        get
        {
            if (CommanderSystem is null)
                return FleetCommanderPresenceLevel.Unknown;

            return InSystem == Total ? FleetCommanderPresenceLevel.Complete : FleetCommanderPresenceLevel.Partial;
        }
    }

    public bool IsComplete => Level is FleetCommanderPresenceLevel.Complete;

    public bool IsUnknown => Level is FleetCommanderPresenceLevel.Unknown;

    public string BadgeText => IsUnknown ? "◉ — WITH FC" : $"◉ {InSystem}/{Total} WITH FC";

    public string Tooltip => IsUnknown
        ? "Unknown: the fleet has no commander, or the commander shares no location (location sharing is opt-in)."
        : $"{InSystem} of {Total} fleet members are in {CommanderSystem} with the fleet commander";
}
