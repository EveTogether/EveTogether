namespace EveUtils.Client.Fleet;

/// <summary>
/// One of this client's own runs that is still going in a given fleet (ET-166). Read when the FC is about to stop
/// the fleet, so the dialog can say which measurements are underway and that stopping does not throw them away.
/// Only local runs: the coordinator this comes from sees the runs of the characters on THIS machine, which is what
/// the FC is being asked about — their own screen keeps recording either way.
/// </summary>
/// <param name="CharacterId">The pilot flying it.</param>
/// <param name="SiteName">The site, as it was read off the clipboard; null when the run names none.</param>
/// <param name="SolarSystemName">Where it is being flown; null when unknown.</param>
/// <param name="StartedAtUtc">When it started — the elapsed clock is taken against this.</param>
public sealed record FleetRunInProgress(
    long CharacterId,
    string? SiteName,
    string? SolarSystemName,
    DateTime StartedAtUtc);
