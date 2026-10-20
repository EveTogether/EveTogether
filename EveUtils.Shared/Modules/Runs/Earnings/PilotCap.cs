namespace EveUtils.Shared.Modules.Runs.Earnings;

/// <summary>
/// The per-pilot cap (ET-512 decision 7) exists so one pilot cannot dominate figures that combine several. A weight a
/// server already applied is used as it is, and the runs of a single pilot are not capped at all: there is nobody to
/// dominate, and capping them would read 97 runs as 10 (ET-523).
/// </summary>
internal static class PilotCap
{
    /// <returns>min(1, C / runs of that pilot) per sample, in order, when several pilots are combined.</returns>
    public static double[] Weights(IReadOnlyList<EarningsSample> samples, int runsPerPilotCap)
    {
        Dictionary<string, int> runsByPilot = samples
            .Where(sample => sample.CapWeight is null)
            .GroupBy(sample => sample.PilotBucket, StringComparer.Ordinal)
            .ToDictionary(pilot => pilot.Key, pilot => pilot.Count(), StringComparer.Ordinal);
        bool combinesPilots = runsByPilot.Count > 1;
        return [.. samples.Select(sample => sample.CapWeight
                                            ?? (combinesPilots ? Math.Min(1d, (double)runsPerPilotCap / runsByPilot[sample.PilotBucket]) : 1d))];
    }
}
