using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Server.DataExplorer;

/// <summary>
/// One row of the Runs list: the Run rows that share a <c>GroupCode</c>, or a single run flown without a group. The
/// facts are the group's, not one pilot's: the earliest start, the latest stop, the highest revision and push.
/// </summary>
public sealed class RunGroupListItem
{
    /// <summary>The group code, or the run's own id for a run without a group.</summary>
    public required string Key { get; init; }

    public string? GroupCode { get; init; }
    public string? SiteName { get; init; }
    public required ActivityKind Kind { get; init; }
    public int? SolarSystemId { get; init; }
    public required IReadOnlyList<RunPilot> Pilots { get; init; }
    public required DateTime StartedAtUtc { get; init; }

    /// <summary>Null while any pilot's run is still on the clock.</summary>
    public DateTime? StoppedAtUtc { get; init; }

    public HomefrontOutcome? Outcome { get; init; }
    public required int Revision { get; init; }
    public DateTime? LastPushedAtUtc { get; init; }
    public required decimal BountyIsk { get; init; }

    /// <summary>The fleet this group was most likely flown in, read from its pilots and the fleet's time window. There
    /// is no Run.FleetId, so this is an inference and is shown as one.</summary>
    public FleetListItem? DerivedFleet { get; init; }

    public TimeSpan? Flown => StoppedAtUtc - StartedAtUtc;

    public DateOnly Day => DateOnly.FromDateTime(StartedAtUtc);

    /// <summary>The site's name as the scanner or the pilot gave it; a run without one is named by its kind.</summary>
    public string Title => SiteName ?? KindLabel(Kind);

    public static string KindLabel(ActivityKind kind) => kind switch
    {
        ActivityKind.Abyssal => "Abyssal",
        ActivityKind.Site => "Site",
        ActivityKind.Mission => "Mission",
        _ => "Mining",
    };
}
