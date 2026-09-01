using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Grouping;

public static class RunGroupCodeArbiter
{
    public static string Select(ActivityKind activityKind, IReadOnlyList<RunGroupCodeCandidate> candidates)
    {
        if (candidates.Count == 0)
            throw new ArgumentException("A group code requires a candidate.", nameof(candidates));

        if (activityKind != ActivityKind.Abyssal)
            return candidates.Single(candidate => candidate.IsFleetCommander).GroupCode;

        return candidates
            .OrderBy(candidate => candidate.StartedAtUtc)
            .ThenBy(candidate => candidate.GroupCode, StringComparer.Ordinal)
            .First()
            .GroupCode;
    }
}
