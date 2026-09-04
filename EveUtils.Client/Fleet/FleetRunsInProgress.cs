using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace EveUtils.Client.Fleet;

/// <summary>
/// The lines the stop dialog prints for the runs still going in a fleet (ET-166): one per run of this client's,
/// naming the pilot, where they are and how long it has been. Shared by every screen that offers a STOP, so the
/// roster window and the overview describe the same run in the same words.
/// </summary>
public static class FleetRunsInProgress
{
    public static IReadOnlyList<string> Describe(
        FleetRunGroupCodeCoordinator? coordinator, long fleetId, Func<int, string> nameOf, DateTime nowUtc)
    {
        if (coordinator is null)
            return [];

        return [.. coordinator.ListRunsInProgress(fleetId).Select(run =>
        {
            var elapsed = nowUtc - run.StartedAtUtc;
            if (elapsed < TimeSpan.Zero)
                elapsed = TimeSpan.Zero;

            var where = string.IsNullOrWhiteSpace(run.SiteName) ? run.SolarSystemName : run.SiteName;
            var name = nameOf((int)run.CharacterId);
            // Invariant: this is a clock, and the tests run on a machine whose culture is not English (ET-34).
            return string.IsNullOrWhiteSpace(where)
                ? string.Create(CultureInfo.InvariantCulture, $"{name} — {elapsed:hh\\:mm\\:ss}")
                : string.Create(CultureInfo.InvariantCulture, $"{name} — {where}, {elapsed:hh\\:mm\\:ss}");
        })];
    }
}
