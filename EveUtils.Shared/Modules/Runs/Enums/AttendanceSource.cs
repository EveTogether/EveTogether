namespace EveUtils.Shared.Modules.Runs.Enums;

/// <summary>Who decided a homefront's attendance list (ET-230): the fleet commander for every run of a fleet's group,
/// or the pilot over a run of their own. Stored, never derived — the fleet's commander can change after the site.
/// Appended only.</summary>
public enum AttendanceSource
{
    Pilot,
    FleetCommander
}
