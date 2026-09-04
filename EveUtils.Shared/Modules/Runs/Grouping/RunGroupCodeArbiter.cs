using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Runs.Enums;

namespace EveUtils.Shared.Modules.Runs.Grouping;

public static class RunGroupCodeArbiter
{
    public static Result<string> Select(ActivityKind activityKind, IReadOnlyList<RunGroupCodeCandidate> candidates)
    {
        if (candidates.Count == 0)
            return Result<string>.Failure(new ResultMessage(MessageSeverity.Error, MessageCodes.ValidationFailed,
                "A group code requires a candidate.", "Runs"));

        IEnumerable<RunGroupCodeCandidate> eligible = TakesGroupFromCommanderOnly(activityKind)
            ? candidates.Where(candidate => candidate.IsFleetCommander)
            : [];
        RunGroupCodeCandidate winner = eligible.Any()
            ? _First(eligible)
            : _First(candidates);

        return Result<string>.Success(winner.GroupCode);
    }

    /// <summary>
    /// Whether only the fleet commander's start may make the group. A site is one shared pocket everybody flies at
    /// once, so the commander's start is the run; an abyssal or a mission is flown per pilot, and there whoever
    /// started first carries the code.
    ///
    /// The two callers used to carry this test as a bare <c>== Site</c> each, which is a rule stated twice and
    /// explained nowhere. A kind added later lands on the per-pilot side, which is the safe half: it hands nobody
    /// authority over anybody else's run.
    /// </summary>
    public static bool TakesGroupFromCommanderOnly(ActivityKind activityKind) => activityKind is ActivityKind.Site;

    private static RunGroupCodeCandidate _First(IEnumerable<RunGroupCodeCandidate> candidates) => candidates
            .OrderBy(candidate => candidate.StartedAtUtc)
            .ThenBy(candidate => candidate.GroupCode, StringComparer.Ordinal)
            .First();
}
