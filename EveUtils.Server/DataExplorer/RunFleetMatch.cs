namespace EveUtils.Server.DataExplorer;

/// <summary>
/// Which fleet a run group was flown in, as far as the data can tell without a Run.FleetId: a fleet that was started
/// before the run began and had not gone quiet yet, with at least one of the group's pilots still on its roster. The
/// fleet sharing the most pilots wins; between equals, the one started last. No such fleet means no match — never a
/// guess. Fleets are deleted a day after they are archived, so older runs usually match nothing.
/// </summary>
public static class RunFleetMatch
{
    public static FleetListItem? Find(
        DateTime startedAtUtc, IReadOnlyCollection<long> pilotCharacterIds, IReadOnlyList<FleetListItem> fleets, DateTimeOffset now)
    {
        var started = new DateTimeOffset(DateTime.SpecifyKind(startedAtUtc, DateTimeKind.Utc));
        return fleets
            .Where(f => f.Fleet.ActivatedAt is { } activated && activated <= started && started <= _WindowEnd(f, now))
            .Select(f => (Fleet: f, Shared: f.MemberCharacterIds.Count(id => pilotCharacterIds.Contains(id))))
            .Where(c => c.Shared > 0)
            .OrderByDescending(c => c.Shared)
            .ThenByDescending(c => c.Fleet.Fleet.ActivatedAt)
            .Select(c => c.Fleet)
            .FirstOrDefault();
    }

    // A fleet in op is still open; any other started fleet ended at its last activity (the conclude or archive stamps it).
    private static DateTimeOffset _WindowEnd(FleetListItem fleet, DateTimeOffset now) =>
        fleet.Status == FleetPanelStatus.InOp ? now : fleet.Fleet.LastActivityAt;
}
